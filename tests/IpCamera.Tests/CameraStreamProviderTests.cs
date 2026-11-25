using IpCamera.Cameras;
using IpCamera.Streaming;
using IpCamera.VideoStreams;
using MacroDeck.Plugin.Testing.Fakes;
using MacroDeck.Sdk.VideoStreams;
using NUnit.Framework;
using Serilog;
using System.Threading.Channels;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraStreamProviderTests
{
	private static readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	[OneTimeTearDown]
	public void DisposeLogger()
	{
		(_logger as IDisposable)?.Dispose();
	}

	private static CameraDefinition Camera(string id = "camera-1")
	{
		return new CameraDefinition(
			id,
			"Front door",
			CameraSourceKind.Snapshot,
			new Uri("http://127.0.0.1/snapshot.jpg"),
			null,
			null,
			5,
			640,
			"tcp");
	}

	private static async Task<CameraRegistry> RegistryAsync(params CameraDefinition[] cameras)
	{
		var registry = new CameraRegistry(camera => new StatefulStream(camera));
		await registry.SetCamerasAsync(cameras, CancellationToken.None);
		return registry;
	}

	[Test]
	public async Task Every_configured_camera_is_listed_with_its_state()
	{
		await using var registry = await RegistryAsync(Camera("one"), Camera("two"));
		await using var provider = new CameraStreamProvider(
			registry,
			new StubEndpoint(),
			new FakeVideoStreamProviderContext(),
			_logger);

		var streams = await provider.GetStreamsAsync(CancellationToken.None);

		Assert.That(streams.Select(stream => stream.Id), Is.EquivalentTo(["one", "two"]));
	}

	[Test]
	public async Task An_idle_camera_is_still_reported_as_openable()
	{
		// Macro Deck's readers do not open a stream reported as Disconnected or Unavailable, and the
		// producer only starts when a reader opens one. Reporting it as Connecting keeps it openable.
		await using var registry = await RegistryAsync(Camera());
		await using var provider = new CameraStreamProvider(
			registry,
			new StubEndpoint(),
			new FakeVideoStreamProviderContext(),
			_logger);

		var streams = await provider.GetStreamsAsync(CancellationToken.None);

		Assert.That(streams.Single().State, Is.EqualTo(VideoStreamState.Connecting));
	}

	[Test]
	public async Task Opening_a_stream_returns_the_loopback_mjpeg_address()
	{
		await using var registry = await RegistryAsync(Camera());
		await using var provider = new CameraStreamProvider(
			registry,
			new StubEndpoint(),
			new FakeVideoStreamProviderContext(),
			_logger);

		var description = await provider.OpenAsync(
			new VideoStreamOpenRequest("session-1", "camera-1", ["mjpeg"]),
			CancellationToken.None);

		Assert.That(description.Transport, Is.EqualTo("mjpeg"));
		Assert.That(description.Url, Is.EqualTo("http://127.0.0.1:41234/camera/camera-1"));
	}

	[Test]
	public async Task An_unknown_stream_is_refused_with_the_matching_code()
	{
		await using var registry = await RegistryAsync(Camera());
		await using var provider = new CameraStreamProvider(
			registry,
			new StubEndpoint(),
			new FakeVideoStreamProviderContext(),
			_logger);

		var exception = Assert.ThrowsAsync<VideoStreamException>(() => provider.OpenAsync(
			new VideoStreamOpenRequest("session-1", "no-such-camera", ["mjpeg"]),
			CancellationToken.None));

		Assert.That(exception!.ErrorCode, Is.EqualTo(VideoStreamErrorCode.UnknownStream));
	}

	[Test]
	public async Task A_client_without_mjpeg_is_refused()
	{
		await using var registry = await RegistryAsync(Camera());
		await using var provider = new CameraStreamProvider(
			registry,
			new StubEndpoint(),
			new FakeVideoStreamProviderContext(),
			_logger);

		var exception = Assert.ThrowsAsync<VideoStreamException>(() => provider.OpenAsync(
			new VideoStreamOpenRequest("session-1", "camera-1", ["hls"]),
			CancellationToken.None));

		Assert.That(exception!.ErrorCode, Is.EqualTo(VideoStreamErrorCode.TransportNotAccepted));
	}

	[Test]
	public async Task A_camera_that_sends_no_frame_refuses_the_session()
	{
		var camera = Camera();
		var stream = new StatefulStream(camera) { PrimeResult = false };
		await using var registry = new CameraRegistry(_ => stream);
		await registry.SetCamerasAsync([camera], CancellationToken.None);
		await using var provider = new CameraStreamProvider(
			registry,
			new StubEndpoint(),
			new FakeVideoStreamProviderContext(),
			_logger);

		var exception = Assert.ThrowsAsync<VideoStreamException>(() => provider.OpenAsync(
			new VideoStreamOpenRequest("session-1", camera.Id, ["mjpeg"]),
			CancellationToken.None));

		Assert.That(exception!.ErrorCode, Is.EqualTo(VideoStreamErrorCode.StreamUnavailable));
	}

	[Test]
	public async Task A_failed_camera_reports_its_session_as_reconnecting()
	{
		var camera = Camera();
		var stream = new StatefulStream(camera);
		await using var registry = new CameraRegistry(_ => stream);
		await registry.SetCamerasAsync([camera], CancellationToken.None);
		var context = new FakeVideoStreamProviderContext();
		await using var provider = new CameraStreamProvider(
			registry,
			new StubEndpoint(),
			context,
			_logger);

		await provider.OpenAsync(
			new VideoStreamOpenRequest("session-1", camera.Id, ["mjpeg"]),
			CancellationToken.None);

		stream.State = VideoStreamState.Disconnected;
		stream.HasFailed = true;
		await provider.ReportSessionStateAsync("session-1", stream);

		var update = context.Calls.Last(call => call.Kind == VideoStreamProviderCallKind.SessionUpdate);
		Assert.That(update.State, Is.EqualTo(VideoStreamSessionState.Reconnecting));
		Assert.That(update.Reason, Is.EqualTo(VideoStreamSessionReason.SourceLost));
		Assert.That(update.Message, Is.Not.Null);
	}

	[Test]
	public async Task A_recovered_camera_reports_its_session_as_active()
	{
		var camera = Camera();
		var stream = new StatefulStream(camera) { State = VideoStreamState.Connected };
		await using var registry = new CameraRegistry(_ => stream);
		await registry.SetCamerasAsync([camera], CancellationToken.None);
		var context = new FakeVideoStreamProviderContext();
		await using var provider = new CameraStreamProvider(
			registry,
			new StubEndpoint(),
			context,
			_logger);

		await provider.OpenAsync(
			new VideoStreamOpenRequest("session-1", camera.Id, ["mjpeg"]),
			CancellationToken.None);
		await provider.ReportSessionStateAsync("session-1", stream);

		var update = context.Calls.Last(call => call.Kind == VideoStreamProviderCallKind.SessionUpdate);
		Assert.That(update.State, Is.EqualTo(VideoStreamSessionState.Active));
		Assert.That(update.Reason, Is.EqualTo(VideoStreamSessionReason.SourceRecovered));
	}

	private sealed class StatefulStream(CameraDefinition definition) : IFrameStream
	{
		public CameraDefinition Definition => definition;

		public VideoStreamState State { get; set; } = VideoStreamState.Disconnected;

		public bool HasFailed { get; set; }

		public DateTimeOffset? LastFrameAt => null;

		public int? Width => null;

		public int? Height => null;

		public byte[]? CurrentFrame => null;

		public event EventHandler? StateChanged
		{
			add { }
			remove { }
		}

		public FrameSubscription Subscribe()
		{
			return new FrameSubscription(Channel.CreateBounded<byte[]>(1).Reader, () => { });
		}

		public Task RestartAsync()
		{
			return Task.CompletedTask;
		}

		public bool PrimeResult { get; set; } = true;

		public Task<bool> PrimeAsync(TimeSpan timeout, CancellationToken cancellationToken)
		{
			return Task.FromResult(PrimeResult);
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}

	private sealed class StubEndpoint : IMjpegEndpoint
	{
		public string UrlFor(string cameraId)
		{
			return $"http://127.0.0.1:41234/camera/{cameraId}";
		}
	}
}
