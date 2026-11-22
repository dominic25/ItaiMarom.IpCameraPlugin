using System.Net;
using IpCamera.Cameras;
using IpCamera.Streaming;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class FrameSourceTests
{
	private static CameraDefinition Camera(string url = "http://127.0.0.1/snapshot.jpg")
	{
		return new CameraDefinition(
			"camera-1",
			"Test camera",
			CameraSourceKind.Snapshot,
			new Uri(url),
			null,
			null,
			5,
			640,
			"tcp");
	}

	[Test]
	public async Task A_snapshot_source_yields_the_fetched_jpeg()
	{
		var jpeg = JpegTests.Frame();
		using var http = new HttpClient(new StubHandler(jpeg)) { Timeout = Timeout.InfiniteTimeSpan };
		await using var source = new SnapshotFrameSource(http, Camera());
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var frames = new List<byte[]>();

		await foreach (var frame in source.ReadFramesAsync(cancellation.Token))
		{
			frames.Add(frame);
			break;
		}

		Assert.That(frames, Has.Count.EqualTo(1));
		Assert.That(frames[0], Is.EqualTo(jpeg));
	}

	[Test]
	public async Task A_snapshot_that_is_not_a_jpeg_fails()
	{
		using var http = new HttpClient(new StubHandler("not a jpeg"u8.ToArray())) { Timeout = Timeout.InfiniteTimeSpan };
		await using var source = new SnapshotFrameSource(http, Camera());
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

		Assert.ThrowsAsync<FrameSourceException>(async () =>
		{
			await foreach (var _ in source.ReadFramesAsync(cancellation.Token))
			{
				break;
			}
		});
	}

	[Test]
	public void The_ffmpeg_arguments_ask_for_mjpeg_on_stdout()
	{
		var arguments = RtspFrameSource.BuildArguments(Camera("rtsp://192.168.1.30/live/ch00_0") with
		{
			Kind = CameraSourceKind.Rtsp,
			RtspTransport = "tcp"
		});

		Assert.That(arguments, Does.Contain("-rtsp_transport"));
		Assert.That(arguments, Does.Contain("tcp"));
		Assert.That(arguments, Does.Contain("rtsp://192.168.1.30/live/ch00_0"));
		Assert.That(arguments, Does.Contain("scale=640:-2"));
		Assert.That(arguments, Does.Contain("pipe:1"));
	}

	[Test]
	public void Credentials_are_carried_in_the_rtsp_url_for_ffmpeg()
	{
		var arguments = RtspFrameSource.BuildArguments(Camera("rtsp://192.168.1.30/live") with
		{
			Kind = CameraSourceKind.Rtsp,
			Username = "admin",
			Password = "secret"
		});

		var inputIndex = arguments.ToList().IndexOf("-i");
		Assert.That(arguments[inputIndex + 1], Does.Contain("admin"));
		Assert.That(arguments[inputIndex + 1], Does.Contain("192.168.1.30"));
	}

	private sealed class StubHandler(byte[] body) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(body)
			});
		}
	}
}
