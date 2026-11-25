using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IpCamera.Xiaomi;
using NUnit.Framework;
using Serilog;

namespace IpCamera.Tests;

[TestFixture]
public sealed class MiioClientTests
{
	private const string Token = "00112233445566778899aabbccddeeff";
	private const string OtherToken = "ffeeddccbbaa99887766554433221100";

	private static readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
	private static readonly string[] _ok = ["ok"];

	[Test]
	public async Task A_move_sends_the_motor_operation_and_succeeds()
	{
		using var device = new FakeDevice(Token);
		var client = new MiioClient(_logger);

		var result = await client.SetMotorAsync(device.Endpoint(Token), 3, CancellationToken.None);

		Assert.That(result.Succeeded, Is.True, result.Reason);
		Assert.Multiple(() =>
		{
			Assert.That(device.Methods, Has.Count.EqualTo(1));
			Assert.That(device.Methods[0], Is.EqualTo("set_motor"));
			Assert.That(device.Parameters[0], Does.Contain("\"operation\":3"));
		});
	}

	[Test]
	public async Task The_night_mode_uses_the_xiaomi_numbers()
	{
		using var device = new FakeDevice(Token);
		var client = new MiioClient(_logger);

		var result = await client.SetNightModeAsync(device.Endpoint(Token), 0, CancellationToken.None);

		Assert.That(result.Succeeded, Is.True, result.Reason);
		Assert.Multiple(() =>
		{
			Assert.That(device.Methods, Has.Count.EqualTo(1));
			Assert.That(device.Methods[0], Is.EqualTo("set_night_mode"));
			Assert.That(device.Parameters[0], Is.EqualTo("[0]"));
		});
	}

	[Test]
	public async Task A_wrong_token_is_rejected()
	{
		using var device = new FakeDevice(Token);
		var client = new MiioClient(_logger, TimeSpan.FromMilliseconds(500));

		var result = await client.SetMotorAsync(device.Endpoint(OtherToken), 1, CancellationToken.None);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(result.Reason, Does.Contain("token"));
	}

	[Test]
	public async Task A_token_that_is_not_hexadecimal_fails_before_the_network()
	{
		var client = new MiioClient(_logger);
		var endpoint = new MiioEndpoint("127.0.0.1", 54321, "not-a-token");

		var result = await client.SetMotorAsync(endpoint, 1, CancellationToken.None);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(result.Reason, Does.Contain("token"));
	}

	[Test]
	public async Task An_unanswered_command_fails()
	{
		using var device = new FakeDevice(Token, answerCommands: false);
		var client = new MiioClient(_logger, TimeSpan.FromMilliseconds(500));

		var result = await client.SetMotorAsync(device.Endpoint(Token), 1, CancellationToken.None);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(result.Reason, Is.Not.Empty);
	}

	[Test]
	public void A_token_may_be_written_with_colons()
	{
		Assert.That(MiioClient.TryReadToken("00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff", out var token), Is.True);
		Assert.That(token, Has.Length.EqualTo(16));
	}

	[Test]
	public void The_payload_survives_a_round_trip()
	{
		var payload = JsonSerializer.SerializeToUtf8Bytes(new { hello = "camera" });
		var token = Convert.FromHexString(Token);

		var encrypted = MiioClient.Encrypt(payload, token);
		var decrypted = MiioClient.Decrypt(encrypted, token);

		Assert.That(decrypted, Is.EqualTo(payload));
	}

	[OneTimeTearDown]
	public void DisposeLogger()
	{
		(_logger as IDisposable)?.Dispose();
	}

	private sealed class FakeDevice : IDisposable
	{
		private const uint DeviceId = 0x11223344;
		private const uint Stamp = 1000;

		private readonly UdpClient _server = new(new IPEndPoint(IPAddress.Loopback, 0));
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _loop;
		private readonly byte[] _token;
		private readonly bool _answerCommands;

		public FakeDevice(string token, bool answerCommands = true)
		{
			_token = Convert.FromHexString(token);
			_answerCommands = answerCommands;
			_loop = Task.Run(RunAsync);
		}

		public List<string> Methods { get; } = [];

		public List<string> Parameters { get; } = [];

		public MiioEndpoint Endpoint(string token)
		{
			return new MiioEndpoint(
				"127.0.0.1",
				((IPEndPoint)_server.Client.LocalEndPoint!).Port,
				token);
		}

		public void Dispose()
		{
			_stop.Cancel();
			_server.Dispose();
			_stop.Dispose();
		}

		private async Task RunAsync()
		{
			while (!_stop.IsCancellationRequested)
			{
				UdpReceiveResult received;
				try
				{
					received = await _server.ReceiveAsync(_stop.Token).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
				{
					return;
				}

				if (received.Buffer.Length == 32)
				{
					await AnswerHelloAsync(received).ConfigureAwait(false);
					continue;
				}

				if (_answerCommands)
				{
					await AnswerCommandAsync(received).ConfigureAwait(false);
				}
			}
		}

		private async Task AnswerHelloAsync(UdpReceiveResult received)
		{
			var hello = new byte[32];
			hello[0] = 0x21;
			hello[1] = 0x31;
			hello[3] = 0x20;
			WriteUInt32(hello, 8, DeviceId);
			WriteUInt32(hello, 12, Stamp);

			await _server.SendAsync(hello, received.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
		}

		private async Task AnswerCommandAsync(UdpReceiveResult received)
		{
			var packet = received.Buffer;
			var payload = packet.AsSpan(32).ToArray();
			var id = 0;
			var method = "unknown";
			var parameters = "null";

			try
			{
				var request = MiioClient.Decrypt(payload, _token);
				using var document = JsonDocument.Parse(request);
				var root = document.RootElement;
				id = root.GetProperty("id").GetInt32();
				method = root.GetProperty("method").GetString() ?? string.Empty;
				parameters = root.GetProperty("params").GetRawText();
			}
			catch (Exception)
			{
				// A client with another token cannot be understood; answer with ours so it can see the
				// checksum fail, which is what the camera does.
			}

			Methods.Add(method);
			Parameters.Add(parameters);

			var answer = JsonSerializer.SerializeToUtf8Bytes(new
			{
				id,
				result = _ok
			});

			var encrypted = MiioClient.Encrypt(answer, _token);
			var header = MiioClient.BuildHeader(encrypted.Length, DeviceId, Stamp + 1);
			var checksum = MiioClient.Checksum(header, _token, encrypted);
			var response = new byte[header.Length + checksum.Length + encrypted.Length];
			header.CopyTo(response, 0);
			checksum.CopyTo(response, header.Length);
			encrypted.CopyTo(response, header.Length + checksum.Length);

			await _server.SendAsync(response, received.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
		}

		private static void WriteUInt32(byte[] data, int offset, uint value)
		{
			data[offset] = (byte)(value >> 24);
			data[offset + 1] = (byte)(value >> 16);
			data[offset + 2] = (byte)(value >> 8);
			data[offset + 3] = (byte)value;
		}
	}
}
