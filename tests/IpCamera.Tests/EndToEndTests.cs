using System.Net;
using System.Net.Sockets;
using System.Text;
using IpCamera.Cameras;
using MacroDeck.Plugin.Testing;
using MacroDeck.Sdk;
using MacroDeck.Sdk.VideoStreams;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace IpCamera.Tests;

/// <summary>
/// Runs the real frame pipeline against a local HTTP snapshot server: config entry, camera registry,
/// frame source, loopback relay and the video stream provider all take part.
/// </summary>
[TestFixture]
public sealed class EndToEndTests
{
	[Test]
	public async Task A_configured_snapshot_camera_reaches_an_mjpeg_client()
	{
		var jpeg = JpegTests.Frame(640, 360);
		await using var cameraServer = await SnapshotServer.StartAsync(jpeg);

		await using var harness = PluginTestHarness.Create(builder => Composition.Register(
			builder.UseLocalization(Strings.LocalizationCatalog)));

		var entry = harness.Context.Config.AddEntry("Front door");
		harness.Context.Config.SeedString(entry, CameraSettings.KindKey, "snapshot");
		harness.Context.Config.SeedString(entry, CameraSettings.UrlKey, cameraServer.Url);
		harness.Context.Config.SeedString(entry, CameraSettings.FpsKey, "5");

		await harness.InitializeIntegrationsAsync();
		var integration = harness.Services.GetServices<IPluginIntegration>().OfType<PluginIntegration>().Single();
		await ((IVideoStreamIntegration)integration).InitializeAsync(harness.Context.VideoStreams);

		var provider = harness.Context.VideoStreams.Providers["cameras"];
		var streams = await provider.GetStreamsAsync(CancellationToken.None);
		var cameraId = entry.ToString("D");

		Assert.That(streams.Single().Id, Is.EqualTo(cameraId));

		var description = await provider.OpenAsync(
			new VideoStreamOpenRequest("session-1", cameraId, ["mjpeg"]),
			CancellationToken.None);

		Assert.That(description.Url, Is.Not.Null);

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
		using var response = await http.GetAsync(description.Url, HttpCompletionOption.ResponseHeadersRead);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var firstFrame = await ReadUntilAsync(response, jpeg, cancellation.Token);

		Assert.That(firstFrame, Is.True);
	}

	private static async Task<bool> ReadUntilAsync(
		HttpResponseMessage response,
		byte[] needle,
		CancellationToken cancellationToken)
	{
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		var collected = new MemoryStream();
		var buffer = new byte[8192];

		try
		{
			while (collected.Length < 128 * 1024)
			{
				var read = await stream.ReadAsync(buffer, cancellationToken);
				if (read == 0)
				{
					return false;
				}

				collected.Write(buffer, 0, read);
				if (collected.GetBuffer().AsSpan(0, (int)collected.Length).IndexOf(needle) >= 0)
				{
					return true;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}

		return false;
	}

	private sealed class SnapshotServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private readonly byte[] _jpeg;
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _loop;

		private SnapshotServer(TcpListener listener, byte[] jpeg)
		{
			_listener = listener;
			_jpeg = jpeg;
			_loop = Task.Run(() => AcceptAsync(_stop.Token));
		}

		public string Url { get; private init; } = string.Empty;

		public static Task<SnapshotServer> StartAsync(byte[] jpeg)
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;
			var server = new SnapshotServer(listener, jpeg)
			{
				Url = $"http://127.0.0.1:{port}/snapshot.jpg"
			};

			return Task.FromResult(server);
		}

		private async Task AcceptAsync(CancellationToken cancellationToken)
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await _listener.AcceptTcpClientAsync(cancellationToken);
				}
				catch (Exception)
				{
					return;
				}

				_ = RespondAsync(client, cancellationToken);
			}
		}

		private async Task RespondAsync(TcpClient client, CancellationToken cancellationToken)
		{
			using (client)
			{
				var stream = client.GetStream();
				var buffer = new byte[4096];
				await stream.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false, cancellationToken);

				var headers = Encoding.ASCII.GetBytes(
					"HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: " +
					$"{_jpeg.Length}\r\nConnection: close\r\n\r\n");

				await stream.WriteAsync(headers, cancellationToken);
				await stream.WriteAsync(_jpeg, cancellationToken);
			}
		}

		public async ValueTask DisposeAsync()
		{
			await _stop.CancelAsync();
			_listener.Stop();
			try
			{
				await _loop;
			}
			catch (OperationCanceledException)
			{
			}

			_stop.Dispose();
		}
	}
}
