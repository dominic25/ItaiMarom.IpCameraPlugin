using System.Net;
using System.Text;
using IpCamera.Cameras;
using IpCamera.Streaming;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class MjpegFrameSourceTests
{
	[Test]
	public async Task A_multipart_mjpeg_response_yields_its_frames()
	{
		var first = JpegTests.Frame(320, 240);
		var second = JpegTests.Frame(320, 240);
		var body = new List<byte>();
		body.AddRange("--boundary\r\nContent-Type: image/jpeg\r\n\r\n"u8.ToArray());
		body.AddRange(first);
		body.AddRange("\r\n--boundary\r\nContent-Type: image/jpeg\r\n\r\n"u8.ToArray());
		body.AddRange(second);

		using var http = new HttpClient(new MultipartHandler(body.ToArray()))
		{
			Timeout = Timeout.InfiniteTimeSpan
		};

		await using var source = new MjpegFrameSource(http, Camera());
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var frames = new List<byte[]>();

		await foreach (var frame in source.ReadFramesAsync(cancellation.Token))
		{
			frames.Add(frame);
			if (frames.Count == 2)
			{
				break;
			}
		}

		Assert.That(frames, Has.Count.EqualTo(2));
		Assert.That(frames[0], Is.EqualTo(first));
	}

	private static CameraDefinition Camera()
	{
		return new CameraDefinition(
			"camera-1",
			"Test camera",
			CameraSourceKind.Mjpeg,
			new Uri("http://127.0.0.1/video.mjpg"),
			null,
			null,
			5,
			640,
			"tcp");
	}

	private sealed class MultipartHandler(byte[] body) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			await Task.Yield();

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StreamContent(new MemoryStream(body))
			};
		}
	}
}
