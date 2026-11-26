using IpCamera.Cameras;
using IpCamera.ConfigFlow;
using IpCamera.Onvif;
using IpCamera.Streaming;
using IpCamera.Xiaomi;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Http;
using NUnit.Framework;
using Serilog;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraConfigFlowTests
{
	private static readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	[OneTimeTearDown]
	public void DisposeLogger()
	{
		(_logger as IDisposable)?.Dispose();
	}

	[Test]
	public async Task The_probe_frame_size_is_saved_with_the_camera()
	{
		var flow = CreateFlow(
			probeResult: CameraProbeResult.Success with { Width = 1280, Height = 720 });

		var result = await flow.SubmitAsync(
			"camera",
			Input(
				("name", "Front door"),
				("kind", "snapshot"),
				("url", "http://192.168.1.20/snapshot.jpg"),
				("fps", 6d)),
			new StubFlowContext(),
			CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
		Assert.Multiple(() =>
		{
			Assert.That(result.Values![CameraSettings.StreamWidthKey].Value, Is.EqualTo("1280"));
			Assert.That(result.Values![CameraSettings.StreamHeightKey].Value, Is.EqualTo("720"));
		});
	}

	[Test]
	public async Task A_complete_snapshot_camera_is_saved_as_one_entry()
	{
		var flow = CreateFlow();

		var result = await flow.SubmitAsync("camera", Input(
			("name", "Front door"),
			("kind", "snapshot"),
			("url", "http://192.168.1.20/snapshot.jpg"),
			("fps", 6d)), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
		Assert.That(result.EntryTitle, Is.EqualTo("Front door"));
		Assert.That(result.Values![CameraSettings.KindKey], Is.Not.Null);
	}

	[Test]
	public async Task A_name_is_required()
	{
		var flow = CreateFlow();

		var result = await flow.SubmitAsync("camera", Input(
			("name", "  "),
			("kind", "snapshot"),
			("url", "http://192.168.1.20/snapshot.jpg")), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Does.ContainKey("name"));
	}

	[Test]
	public async Task A_url_with_the_wrong_scheme_is_rejected()
	{
		var flow = CreateFlow();

		var result = await flow.SubmitAsync("camera", Input(
			("name", "Garden"),
			("kind", "rtsp"),
			("url", "http://192.168.1.30/snapshot.jpg")), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Does.ContainKey("url"));
	}

	[Test]
	public async Task An_rtsp_camera_is_refused_without_ffmpeg()
	{
		var flow = CreateFlow();

		var result = await flow.SubmitAsync("camera", Input(
			("name", "Garden"),
			("kind", "rtsp"),
			("url", "rtsp://192.168.1.30/live/ch00_0")), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Is.Null);
	}

	[Test]
	public async Task An_rtsp_camera_is_accepted_when_ffmpeg_exists()
	{
		// The locator only checks that the file exists, so the test needs no real ffmpeg build.
		var executable = Path.GetTempFileName();
		try
		{
			var flow = CreateFlow(new FfmpegLocator(executable));

			var result = await flow.SubmitAsync("camera", Input(
				("name", "Garden"),
				("kind", "rtsp"),
				("url", "rtsp://192.168.1.30/live/ch00_0"),
				("rtspTransport", "udp")), new StubFlowContext(), CancellationToken.None);

			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
			Assert.That(result.Values![CameraSettings.RtspTransportKey], Is.Not.Null);
		}
		finally
		{
			File.Delete(executable);
		}
	}

	[Test]
	public async Task A_probe_failure_is_reported_on_the_url_field()
	{
		var flow = CreateFlow(probeResult: new CameraProbeResult(CameraProbeFailure.NoFrame));

		var result = await flow.SubmitAsync("camera", Input(
			("name", "Garden"),
			("kind", "mjpeg"),
			("url", "http://192.168.1.30/video.mjpg")), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Does.ContainKey("url"));
	}

	[Test]
	public async Task A_xiaomi_control_saves_the_address_and_a_secret_token()
	{
		var flow = CreateFlow();

		var result = await flow.SubmitAsync("camera", Input(
			("name", "mi360"),
			("kind", "mjpeg"),
			("url", "http://192.168.1.60:1984/api/stream.mjpeg?src=mi360_mjpeg"),
			("control", "xiaomi"),
			("controlHost", "192.168.1.30"),
			("controlToken", "00112233445566778899aabbccddeeff")), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
		Assert.Multiple(() =>
		{
			Assert.That(result.Values![CameraSettings.ControlKey].Value, Is.EqualTo("xiaomi"));
			Assert.That(result.Values[CameraSettings.ControlHostKey].Value, Is.EqualTo("192.168.1.30"));
			Assert.That(result.Values[CameraSettings.ControlTokenKey].Value, Is.EqualTo("00112233445566778899aabbccddeeff"));
			Assert.That(result.Values[CameraSettings.ControlTokenKey].IsSecret, Is.True);
		});
	}

	[Test]
	public async Task A_xiaomi_control_needs_the_camera_address()
	{
		var flow = CreateFlow();

		var result = await flow.SubmitAsync("camera", Input(
			("name", "mi360"),
			("kind", "mjpeg"),
			("url", "http://192.168.1.60:1984/api/stream.mjpeg?src=mi360_mjpeg"),
			("control", "xiaomi"),
			("controlHost", " "),
			("controlToken", "00112233445566778899aabbccddeeff")), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Does.ContainKey(CameraSettings.ControlHostKey));
	}

	[Test]
	public async Task A_xiaomi_token_that_is_not_hexadecimal_is_rejected()
	{
		var flow = CreateFlow();

		var result = await flow.SubmitAsync("camera", Input(
			("name", "mi360"),
			("kind", "mjpeg"),
			("url", "http://192.168.1.60:1984/api/stream.mjpeg?src=mi360_mjpeg"),
			("control", "xiaomi"),
			("controlHost", "192.168.1.30"),
			("controlToken", "xyz")), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Does.ContainKey(CameraSettings.ControlTokenKey));
	}

	[Test]
	public async Task A_xiaomi_token_that_the_camera_rejects_is_reported()
	{
		var flow = CreateFlow(miio: new StubMiioClient(success: false));

		var result = await flow.SubmitAsync("camera", Input(
			("name", "mi360"),
			("kind", "mjpeg"),
			("url", "http://192.168.1.60:1984/api/stream.mjpeg?src=mi360_mjpeg"),
			("control", "xiaomi"),
			("controlHost", "192.168.1.30"),
			("controlToken", "00112233445566778899aabbccddeeff")), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Does.ContainKey(CameraSettings.ControlTokenKey));
	}

	private static CameraConfigFlow CreateFlow(
		FfmpegLocator? ffmpeg = null,
		CameraProbeResult? probeResult = null,
		IOnvifDiscovery? discovery = null,
		IOnvifClient? onvifClient = null,
		IMiioClient? miio = null)
	{
		return new CameraConfigFlow(
			ffmpeg ?? new FfmpegLocator(null),
			new StubProbe(probeResult ?? CameraProbeResult.Success),
			discovery ?? new StubDiscovery(),
			onvifClient ?? new StubOnvifClient(null),
			miio ?? new StubMiioClient(),
			_logger);
	}

	private static Dictionary<string, object?> Input(params (string Key, object? Value)[] pairs)
	{
		return pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
	}

	[Test]
	public async Task A_discovered_onvif_camera_resolves_its_rtsp_url()
	{
		var device = new OnvifDevice("http://192.168.1.50/onvif/device_service", "Front", "Hikvision");
		var ffmpegPath = Path.GetTempFileName();

		try
		{
			var flow = CreateFlow(
				ffmpeg: new FfmpegLocator(ffmpegPath),
				discovery: new StubDiscovery([device]),
				onvifClient: new StubOnvifClient(new OnvifStream(
					"profile1",
					"rtsp://192.168.1.50/Streaming/Channels/101",
					null)));

			await flow.StartAsync(new StubFlowContext(), CancellationToken.None);

			var result = await flow.SubmitAsync("camera", Input(
				("name", "Front"),
				("onvif", device.XAddr),
				("kind", "snapshot"),
				("url", "")), new StubFlowContext(), CancellationToken.None);

			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
			Assert.That(result.Values![CameraSettings.KindKey].Value, Is.EqualTo("rtsp"));
			Assert.That(result.Values[CameraSettings.UrlKey].Value, Does.StartWith("rtsp://"));
		}
		finally
		{
			File.Delete(ffmpegPath);
		}
	}

	[Test]
	public async Task An_onvif_camera_without_a_stream_reports_the_field()
	{
		var device = new OnvifDevice("http://192.168.1.50/onvif/device_service", "Front", "Hikvision");
		var flow = CreateFlow(discovery: new StubDiscovery([device]));

		await flow.StartAsync(new StubFlowContext(), CancellationToken.None);

		var result = await flow.SubmitAsync("camera", Input(
			("name", "Front"),
			("onvif", device.XAddr),
			("kind", "snapshot"),
			("url", "")), new StubFlowContext(), CancellationToken.None);

		Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
		Assert.That(result.FieldErrors, Does.ContainKey("onvif"));
	}

	private sealed class StubDiscovery(IReadOnlyList<OnvifDevice>? devices = null) : IOnvifDiscovery
	{
		public Task<IReadOnlyList<OnvifDevice>> DiscoverAsync(
			TimeSpan timeout,
			CancellationToken cancellationToken)
		{
			return Task.FromResult(devices ?? []);
		}
	}

	private sealed class StubOnvifClient(OnvifStream? result) : IOnvifClient
	{
		public Task<OnvifStream?> ResolveAsync(
			OnvifDevice device,
			string? username,
			string? password,
			CancellationToken cancellationToken)
		{
			return Task.FromResult(result);
		}

		public Task<bool> MoveAsync(
			OnvifDevice device,
			string? username,
			string? password,
			string direction,
			double speed,
			CancellationToken cancellationToken)
		{
			return Task.FromResult(false);
		}

		public Task<bool> StopAsync(
			OnvifDevice device,
			string? username,
			string? password,
			CancellationToken cancellationToken)
		{
			return Task.FromResult(false);
		}

		public Task<bool> SetIrAsync(
			OnvifDevice device,
			string? username,
			string? password,
			string mode,
			CancellationToken cancellationToken)
		{
			return Task.FromResult(false);
		}
	}

	private sealed class StubMiioClient(bool success = true) : IMiioClient
	{
		public Task<MiioResult> SetMotorAsync(
			MiioEndpoint endpoint,
			int operation,
			CancellationToken cancellationToken)
		{
			return Result();
		}

		public Task<MiioResult> SetNightModeAsync(
			MiioEndpoint endpoint,
			int mode,
			CancellationToken cancellationToken)
		{
			return Result();
		}

		public Task<MiioResult> ProbeAsync(MiioEndpoint endpoint, CancellationToken cancellationToken)
		{
			return Result();
		}

		private Task<MiioResult> Result()
		{
			return Task.FromResult(success ? MiioResult.Success : MiioResult.Failure("test"));
		}
	}

	private sealed class StubProbe(CameraProbeResult result) : ICameraProbe
	{
		public Task<CameraProbeResult> ProbeAsync(CameraDefinition camera, CancellationToken cancellationToken)
		{
			return Task.FromResult(result);
		}
	}

	private sealed class StubFlowContext : IConfigFlowContext
	{
		public IOAuthSession OAuth => throw new NotSupportedException();

		public IHostHttp Http => DefaultHostHttp.Instance;
	}
}
