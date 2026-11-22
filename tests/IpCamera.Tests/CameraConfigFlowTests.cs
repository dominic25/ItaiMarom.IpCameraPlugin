using IpCamera.Cameras;
using IpCamera.ConfigFlow;
using IpCamera.Streaming;
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
	public async Task A_complete_snapshot_camera_is_saved_as_one_entry()
	{
		var flow = new CameraConfigFlow(new FfmpegLocator(null), _logger);

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
		var flow = new CameraConfigFlow(new FfmpegLocator(null), _logger);

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
		var flow = new CameraConfigFlow(new FfmpegLocator(null), _logger);

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
		var flow = new CameraConfigFlow(new FfmpegLocator(null), _logger);

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
			var flow = new CameraConfigFlow(new FfmpegLocator(executable), _logger);

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

	private static Dictionary<string, object?> Input(params (string Key, object? Value)[] pairs)
	{
		return pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
	}

	private sealed class StubFlowContext : IConfigFlowContext
	{
		public IOAuthSession OAuth => throw new NotSupportedException();

		public IHostHttp Http => DefaultHostHttp.Instance;
	}
}
