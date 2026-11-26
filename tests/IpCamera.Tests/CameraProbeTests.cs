using System.Net;
using System.Net.Http.Headers;
using IpCamera.Cameras;
using NUnit.Framework;
using Serilog;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraProbeTests
{
	private static readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	private static CameraProbe Probe(HttpMessageHandler handler, TimeSpan? timeout = null)
	{
		return new CameraProbe(new StubFactory(handler), _logger, timeout ?? TimeSpan.FromSeconds(5));
	}

	private static CameraDefinition Camera(CameraSourceKind kind = CameraSourceKind.Snapshot)
	{
		return new CameraDefinition(
			"camera-1",
			"Test camera",
			kind,
			new Uri(kind == CameraSourceKind.Rtsp
				? "rtsp://127.0.0.1:554/live"
				: "http://127.0.0.1/snapshot.jpg"),
			null,
			null,
			5,
			640,
			"tcp");
	}

	[Test]
	public async Task A_jpeg_snapshot_succeeds()
	{
		using var handler = new ResponseHandler(JpegTests.Frame(), "image/jpeg");

		var result = await Probe(handler).ProbeAsync(Camera(), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(result.Succeeded, Is.True);
			Assert.That(result.Width, Is.EqualTo(640));
			Assert.That(result.Height, Is.EqualTo(360));
		});
	}

	[Test]
	public async Task A_multipart_stream_with_a_frame_succeeds()
	{
		var body = new List<byte>();
		body.AddRange("--frame\r\nContent-Type: image/jpeg\r\n\r\n"u8.ToArray());
		body.AddRange(JpegTests.Frame());
		body.AddRange("\r\n"u8.ToArray());
		using var handler = new ResponseHandler(body.ToArray(), "multipart/x-mixed-replace; boundary=frame");

		var result = await Probe(handler).ProbeAsync(Camera(CameraSourceKind.Mjpeg), CancellationToken.None);

		Assert.That(result.Succeeded, Is.True);
	}

	[Test]
	public async Task An_html_answer_is_not_a_jpeg()
	{
		using var handler = new ResponseHandler("<html>login</html>"u8.ToArray(), "text/html");

		var result = await Probe(handler).ProbeAsync(Camera(), CancellationToken.None);

		Assert.That(result.Failure, Is.EqualTo(CameraProbeFailure.NotJpeg));
	}

	[Test]
	public async Task A_rejected_password_is_reported()
	{
		using var handler = new ResponseHandler([], "text/plain", HttpStatusCode.Unauthorized);

		var result = await Probe(handler).ProbeAsync(Camera(), CancellationToken.None);

		Assert.That(result.Failure, Is.EqualTo(CameraProbeFailure.Unauthorized));
	}

	[Test]
	public async Task A_stream_without_a_frame_is_reported()
	{
		using var handler = new ResponseHandler([], "multipart/x-mixed-replace; boundary=frame");

		var result = await Probe(handler).ProbeAsync(Camera(CameraSourceKind.Mjpeg), CancellationToken.None);

		Assert.That(result.Failure, Is.EqualTo(CameraProbeFailure.NoFrame));
	}

	[Test]
	public async Task A_connect_failure_is_reported()
	{
		using var handler = new ThrowingHandler();

		var result = await Probe(handler).ProbeAsync(Camera(), CancellationToken.None);

		Assert.That(result.Failure, Is.EqualTo(CameraProbeFailure.Unreachable));
	}

	[Test]
	public async Task An_rtsp_camera_is_not_probed_over_http()
	{
		using var handler = new ThrowingHandler();

		var result = await Probe(handler).ProbeAsync(Camera(CameraSourceKind.Rtsp), CancellationToken.None);

		Assert.That(result.Succeeded, Is.True);
	}

	[OneTimeTearDown]
	public void DisposeLogger()
	{
		(_logger as IDisposable)?.Dispose();
	}

	private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name)
		{
			return new HttpClient(handler, disposeHandler: false);
		}
	}

	private sealed class ResponseHandler(
		byte[] body,
		string mediaType,
		HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			var content = new ByteArrayContent(body);
			content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);

			return Task.FromResult(new HttpResponseMessage(status) { Content = content });
		}
	}

	private sealed class ThrowingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			throw new HttpRequestException("No route to host.");
		}
	}
}
