using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace IpCamera.Xiaomi;

/// <summary>One Xiaomi device on the local miIO command channel.</summary>
internal sealed record MiioEndpoint(string Host, int Port, string Token)
{
	public const int DefaultPort = 54321;
}

internal sealed record MiioResult(bool Succeeded, string? Reason)
{
	public static MiioResult Success { get; } = new(true, null);

	public static MiioResult Failure(string reason) => new(false, reason);
}

internal interface IMiioClient
{
	Task<MiioResult> SetMotorAsync(MiioEndpoint endpoint, int operation, CancellationToken cancellationToken);

	Task<MiioResult> SetNightModeAsync(MiioEndpoint endpoint, int mode, CancellationToken cancellationToken);
}

/// <summary>
/// The local miIO channel: a UDP handshake followed by an AES encrypted JSON command. The camera answers
/// the motor and night mode commands Xiaomi's own app uses, without a cloud session and without touching
/// the stream. The token is never written to a log line.
/// </summary>
internal sealed class MiioClient : IMiioClient
{
	private const ushort Magic = 0x2131;
	private const int HeaderLength = 16;
	private const int ChecksumLength = 16;
	private const int PacketOverhead = HeaderLength + ChecksumLength;

	private static readonly byte[] _hello = Convert.FromHexString(
		"21310020ffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

	private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(3);

	private readonly ILogger _logger;
	private readonly TimeSpan _timeout;

	public MiioClient(ILogger logger)
		: this(logger, _defaultTimeout)
	{
	}

	internal MiioClient(ILogger logger, TimeSpan timeout)
	{
		_logger = logger;
		_timeout = timeout;
	}

	public Task<MiioResult> SetMotorAsync(
		MiioEndpoint endpoint,
		int operation,
		CancellationToken cancellationToken)
	{
		if (operation is < 1 or > 4)
		{
			return Task.FromResult(MiioResult.Failure("the motor operation is out of range"));
		}

		return SendAsync(endpoint, "set_motor", new JsonObject { ["operation"] = operation }, cancellationToken);
	}

	public Task<MiioResult> SetNightModeAsync(
		MiioEndpoint endpoint,
		int mode,
		CancellationToken cancellationToken)
	{
		if (mode is < 0 or > 2)
		{
			return Task.FromResult(MiioResult.Failure("the night mode is out of range"));
		}

		return SendAsync(
			endpoint,
			"set_night_mode",
			JsonSerializer.SerializeToNode(new[] { mode })!,
			cancellationToken);
	}

	internal static bool TryReadToken(string? text, out byte[] token)
	{
		token = [];

		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var compact = new string(text.Where(character => character != ':' && !char.IsWhiteSpace(character)).ToArray());
		if (compact.Length != 32 || compact.Any(character => !Uri.IsHexDigit(character)))
		{
			return false;
		}

		token = Convert.FromHexString(compact);
		return true;
	}

	private async Task<MiioResult> SendAsync(
		MiioEndpoint endpoint,
		string method,
		JsonNode parameters,
		CancellationToken cancellationToken)
	{
		if (!TryReadToken(endpoint.Token, out var token))
		{
			return MiioResult.Failure("the token is not 32 hexadecimal characters");
		}

		try
		{
			using var client = new UdpClient();
			client.Connect(endpoint.Host, endpoint.Port);

			var handshake = await HandshakeAsync(client, _timeout, cancellationToken).ConfigureAwait(false);
			if (!handshake.Succeeded)
			{
				return MiioResult.Failure(handshake.Reason ?? "the handshake failed");
			}

			var request = new JsonObject
			{
				["id"] = Random.Shared.Next(1, int.MaxValue),
				["method"] = method,
				["params"] = parameters
			};

			var payload = JsonSerializer.SerializeToUtf8Bytes(request);
			var timestamp = handshake.Stamp + 1;

			var encrypted = Encrypt(payload, token);
			var header = BuildHeader(encrypted.Length, handshake.DeviceId, timestamp);
			var checksum = Checksum(header, token, encrypted);

			var packet = new byte[PacketOverhead + encrypted.Length];
			header.CopyTo(packet, 0);
			checksum.CopyTo(packet, HeaderLength);
			encrypted.CopyTo(packet, PacketOverhead);

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(_timeout);

			await client.SendAsync(packet, timeout.Token).ConfigureAwait(false);
			var response = (await client.ReceiveAsync(timeout.Token).ConfigureAwait(false)).Buffer;

			return ReadResponse(response, token, method);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			_logger.Information("Xiaomi device {Host} did not answer in time.", endpoint.Host);
			return MiioResult.Failure("the device did not answer");
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Information("Xiaomi device {Host} failed: {Reason}", endpoint.Host, exception.Message);
			return MiioResult.Failure("the command could not be sent");
		}
	}

	private static async Task<Handshake> HandshakeAsync(
		UdpClient client,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		source.CancelAfter(timeout);

		try
		{
			await client.SendAsync(_hello, source.Token).ConfigureAwait(false);
			var response = (await client.ReceiveAsync(source.Token).ConfigureAwait(false)).Buffer;

			if (response.Length < PacketOverhead || ReadUInt16(response, 0) != Magic)
			{
				return Handshake.Failed("the handshake answer was not a miIO packet");
			}

			return new Handshake(true, ReadUInt32(response, 8), ReadUInt32(response, 12), null);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return Handshake.Failed("the device did not answer the handshake");
		}
	}

	private static MiioResult ReadResponse(byte[] response, byte[] token, string method)
	{
		if (response.Length < PacketOverhead || ReadUInt16(response, 0) != Magic)
		{
			return MiioResult.Failure("the answer was not a miIO packet");
		}

		var declared = ReadUInt16(response, 2);
		if (declared < PacketOverhead || declared > response.Length)
		{
			return MiioResult.Failure("the answer was shorter than its header said");
		}

		var payload = response.AsSpan(PacketOverhead, declared - PacketOverhead).ToArray();
		var expected = Checksum(response.AsSpan(0, HeaderLength).ToArray(), token, payload);
		if (!CryptographicOperations.FixedTimeEquals(expected, response.AsSpan(HeaderLength, ChecksumLength)))
		{
			// A wrong token decrypts to noise and fails here before the JSON is read.
			return MiioResult.Failure("the token was rejected");
		}

		var message = Decrypt(payload, token);
		var document = JsonDocument.Parse(message);
		if (document.RootElement.TryGetProperty("error", out var error))
		{
			return MiioResult.Failure($"the device reported an error: {error}");
		}

		return MiioResult.Success;
	}

	internal static byte[] Encrypt(byte[] payload, byte[] token)
	{
		// The camera expects the JSON plus a terminating zero, padded to the AES block size.
		var plain = new byte[payload.Length + 1];
		payload.CopyTo(plain, 0);

		using var aes = Aes.Create();
		aes.Key = Key(token);
		aes.IV = InitializationVector(token, aes.Key);
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;

		using var encryptor = aes.CreateEncryptor();
		return encryptor.TransformFinalBlock(plain, 0, plain.Length);
	}

	internal static byte[] Decrypt(byte[] payload, byte[] token)
	{
		using var aes = Aes.Create();
		aes.Key = Key(token);
		aes.IV = InitializationVector(token, aes.Key);
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;

		using var decryptor = aes.CreateDecryptor();
		var plain = decryptor.TransformFinalBlock(payload, 0, payload.Length);

		var length = plain.Length;
		while (length > 0 && plain[length - 1] == 0)
		{
			length--;
		}

		return plain.AsSpan(0, length).ToArray();
	}

	internal static byte[] BuildHeader(int payloadLength, uint deviceId, uint timestamp)
	{
		var header = new byte[HeaderLength];
		WriteUInt16(header, 0, Magic);
		WriteUInt16(header, 2, (ushort)(payloadLength + PacketOverhead));
		WriteUInt32(header, 4, 0);
		WriteUInt32(header, 8, deviceId);
		WriteUInt32(header, 12, timestamp);
		return header;
	}

	internal static byte[] Checksum(byte[] header, byte[] token, byte[] payload)
	{
		var input = new byte[header.Length + token.Length + payload.Length];
		header.CopyTo(input, 0);
		token.CopyTo(input, header.Length);
		payload.CopyTo(input, header.Length + token.Length);

		// The miIO protocol defines MD5 here; it is an interop constraint, not a security choice.
#pragma warning disable CA5351
		return MD5.HashData(input);
#pragma warning restore CA5351
	}

	private static byte[] Key(byte[] token)
	{
		// The camera derives its AES key with MD5. The protocol leaves no alternative.
#pragma warning disable CA5351
		return MD5.HashData(token);
#pragma warning restore CA5351
	}

	private static byte[] InitializationVector(byte[] token, byte[] key)
	{
		var input = new byte[key.Length + token.Length];
		key.CopyTo(input, 0);
		token.CopyTo(input, key.Length);
#pragma warning disable CA5351
		return MD5.HashData(input);
#pragma warning restore CA5351
	}

	private static ushort ReadUInt16(byte[] data, int offset)
	{
		return (ushort)((data[offset] << 8) | data[offset + 1]);
	}

	private static uint ReadUInt32(byte[] data, int offset)
	{
		return ((uint)data[offset] << 24) |
			((uint)data[offset + 1] << 16) |
			((uint)data[offset + 2] << 8) |
			data[offset + 3];
	}

	private static void WriteUInt16(byte[] data, int offset, ushort value)
	{
		data[offset] = (byte)(value >> 8);
		data[offset + 1] = (byte)value;
	}

	private static void WriteUInt32(byte[] data, int offset, uint value)
	{
		data[offset] = (byte)(value >> 24);
		data[offset + 1] = (byte)(value >> 16);
		data[offset + 2] = (byte)(value >> 8);
		data[offset + 3] = (byte)value;
	}

	private sealed record Handshake(bool Succeeded, uint DeviceId, uint Stamp, string? Reason)
	{
		public static Handshake Failed(string reason) => new(false, 0, 0, reason);
	}
}
