using System.Net;
using System.Text;
using System.Threading.Channels;
using IpCamera.Streaming;
using MacroDeck.Sdk.VideoStreams;
using NUnit.Framework;
using Serilog;

namespace IpCamera.Tests;

[TestFixture]
public sealed class MjpegServerTests
{
	private static readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	[OneTimeTearDown]
	public void DisposeLogger()
	{
		(_logger as IDisposable)?.Dispose();
	}

	[Test]
	public async Task A_client_receives_the_current_frame_as_a_multipart_part()
	{
		var jpeg = JpegTests.Frame();
		var stream = new TestFrameStream { CurrentFrame = jpeg };
		await using var server = new MjpegServer(id => id == "camera-1" ? stream : null, _logger);
		await server.StartAsync(CancellationToken.None);
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

		using var response = await http.GetAsync(
			server.UrlFor("camera-1"),
			HttpCompletionOption.ResponseHeadersRead);

		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
		Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("multipart/x-mixed-replace"));

		var body = await ReadUntilAsync(response, jpeg);
		Assert.That(Contains(body, jpeg), Is.True);
	}

	[Test]
	public async Task A_frame_published_after_the_client_connected_reaches_it()
	{
		var stream = new TestFrameStream();
		await using var server = new MjpegServer(id => id == "camera-1" ? stream : null, _logger);
		await server.StartAsync(CancellationToken.None);
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

		using var response = await http.GetAsync(
			server.UrlFor("camera-1"),
			HttpCompletionOption.ResponseHeadersRead);
		var jpeg = JpegTests.Frame(320, 240);
		var pending = ReadUntilAsync(response, jpeg);

		stream.Publish(jpeg);

		var body = await pending;
		Assert.That(Contains(body, jpeg), Is.True);
	}

	[Test]
	public async Task A_head_request_answers_with_the_stream_headers()
	{
		var stream = new TestFrameStream { CurrentFrame = JpegTests.Frame() };
		await using var server = new MjpegServer(id => id == "camera-1" ? stream : null, _logger);
		await server.StartAsync(CancellationToken.None);
		using var http = new HttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Head, server.UrlFor("camera-1"));

		using var response = await http.SendAsync(request);

		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
		Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("multipart/x-mixed-replace"));
	}

	[Test]
	public async Task An_unknown_camera_is_a_404()
	{
		await using var server = new MjpegServer(_ => null, _logger);
		await server.StartAsync(CancellationToken.None);
		using var http = new HttpClient();

		using var response = await http.GetAsync(server.UrlFor("missing"));

		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
	}

	[Test]
	public async Task The_health_endpoint_answers()
	{
		await using var server = new MjpegServer(_ => null, _logger);
		await server.StartAsync(CancellationToken.None);
		using var http = new HttpClient();

		var body = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/health");

		Assert.That(body, Is.EqualTo("ok"));
	}

	private static async Task<byte[]> ReadUntilAsync(HttpResponseMessage response, byte[] needle)
	{
		await using var stream = await response.Content.ReadAsStreamAsync();
		var collected = new MemoryStream();
		var buffer = new byte[8192];

		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		try
		{
			while (collected.Length < 64 * 1024)
			{
				var read = await stream.ReadAsync(buffer, cancellation.Token);
				if (read == 0)
				{
					break;
				}

				collected.Write(buffer, 0, read);
				if (collected.GetBuffer().AsSpan(0, (int)collected.Length).IndexOf(needle) >= 0)
				{
					break;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}

		return collected.ToArray();
	}

	private static bool Contains(byte[] haystack, byte[] needle)
	{
		return haystack.AsSpan().IndexOf(needle) >= 0;
	}

	private sealed class TestFrameStream : IFrameStream
	{
		private readonly object _gate = new();
		private readonly List<Channel<byte[]>> _subscribers = [];

		public VideoStreamState State => VideoStreamState.Connected;

		public int? Width => 640;

		public int? Height => 360;

		public byte[]? CurrentFrame { get; set; }

		public event EventHandler? StateChanged
		{
			add { }
			remove { }
		}

		public FrameSubscription Subscribe()
		{
			var channel = Channel.CreateBounded<byte[]>(4);
			lock (_gate)
			{
				if (CurrentFrame is { } frame)
				{
					channel.Writer.TryWrite(frame);
				}

				_subscribers.Add(channel);
			}

			return new FrameSubscription(channel.Reader, () =>
			{
				lock (_gate)
				{
					_subscribers.Remove(channel);
				}
			});
		}

		public void Publish(byte[] frame)
		{
			CurrentFrame = frame;
			lock (_gate)
			{
				foreach (var subscriber in _subscribers)
				{
					subscriber.Writer.TryWrite(frame);
				}
			}
		}
	}
}
