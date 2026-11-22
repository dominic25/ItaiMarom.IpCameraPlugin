using System.Collections.Concurrent;
using System.Threading.Channels;
using IpCamera.Cameras;
using IpCamera.Streaming;
using MacroDeck.Localization;
using MacroDeck.Sdk.VideoStreams;
using Serilog;

namespace IpCamera.VideoStreams;

/// <summary>
/// Offers every configured camera as one stream. Macro Deck fetches the frames from the loopback relay, so
/// the camera itself never has to be reachable from the device showing the widget.
/// </summary>
internal sealed class CameraStreamProvider : IVideoStreamProvider, IAsyncDisposable
{
	private const string MjpegTransport = "mjpeg";
	private static readonly TimeSpan _primeTimeout = TimeSpan.FromSeconds(10);

	private readonly CameraRegistry _registry;
	private readonly IMjpegEndpoint _endpoint;
	private readonly IVideoStreamProviderContext _context;
	private readonly ILogger _logger;
	private readonly CancellationTokenSource _dispose = new();
	private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
	private readonly Channel<bool> _pendingNotifications = Channel.CreateBounded<bool>(
		new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

	private Task? _notificationLoop;

	public CameraStreamProvider(
		CameraRegistry registry,
		IMjpegEndpoint endpoint,
		IVideoStreamProviderContext context,
		ILogger logger)
	{
		_registry = registry;
		_endpoint = endpoint;
		_context = context;
		_logger = logger.ForContext<CameraStreamProvider>();
	}

	public string Id => "cameras";

	public LocalizedText Name => Strings.VideoStreams.Provider.Name();

	public void Start()
	{
		_registry.StreamChanged += OnStreamChanged;
		_notificationLoop = Task.Run(() => NotifyLoopAsync(_dispose.Token));
	}

	public Task<IReadOnlyList<VideoStreamDescriptor>> GetStreamsAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		IReadOnlyList<VideoStreamDescriptor> streams = _registry.Cameras
			.Select(camera =>
			{
				var stream = _registry.Find(camera.Id);
				return new VideoStreamDescriptor(
					camera.Id,
					LocalizedText.FromLiteral(camera.Name),
					Description(camera.Kind),
					stream?.Width,
					stream?.Height,
					HasAudio: false,
					State: ReportState(stream?.State ?? VideoStreamState.Disconnected));
			})
			.ToList();

		return Task.FromResult(streams);
	}

	public async Task<VideoStreamSessionDescription> OpenAsync(
		VideoStreamOpenRequest request,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (!request.AcceptedTransports.Contains(MjpegTransport, StringComparer.Ordinal))
		{
			throw new VideoStreamException(
				VideoStreamErrorCode.TransportNotAccepted,
				"This provider serves MJPEG only.");
		}

		var camera = _registry.Cameras.FirstOrDefault(candidate => candidate.Id == request.StreamId)
			?? throw new VideoStreamException(
				VideoStreamErrorCode.UnknownStream,
				$"No camera with id '{request.StreamId}' is configured.");

		if (_registry.Find(camera.Id) is { } stream)
		{
			var primed = await stream.PrimeAsync(_primeTimeout, cancellationToken).ConfigureAwait(false);
			if (!primed)
			{
				throw new VideoStreamException(
					VideoStreamErrorCode.StreamUnavailable,
					"The camera did not deliver a frame in time.");
			}
		}

		_sessions[request.SessionId] = camera.Id;

		return VideoStreamSessionDescription.Mjpeg(_endpoint.UrlFor(camera.Id));
	}

	public Task SuspendAsync(string sessionId, CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}

	public Task CloseAsync(
		string sessionId,
		VideoStreamSessionReason reason,
		CancellationToken cancellationToken)
	{
		// The relay aborts the HTTP response, which ends the subscription; only the session bookkeeping
		// is left here.
		_sessions.TryRemove(sessionId, out _);
		return Task.CompletedTask;
	}

	private static LocalizedText Description(CameraSourceKind kind)
	{
		return kind switch
		{
			CameraSourceKind.Rtsp => Strings.VideoStreams.Kinds.Rtsp(),
			CameraSourceKind.Mjpeg => Strings.VideoStreams.Kinds.Mjpeg(),
			_ => Strings.VideoStreams.Kinds.Snapshot()
		};
	}

	/// <summary>
	/// A configured camera can always be opened, even when its source is not running yet: Macro Deck's
	/// readers refuse to open a stream reported as <c>Disconnected</c> or <c>Unavailable</c>, and the
	/// producer only starts once somebody opens it. Reporting the idle state as <c>Connected</c> breaks
	/// that deadlock; <c>Connecting</c> would be just as openable but reads worse in a picker.
	/// </summary>
	private static VideoStreamState ReportState(VideoStreamState state)
	{
		return state == VideoStreamState.Connected ? VideoStreamState.Connected : VideoStreamState.Connecting;
	}

	private void OnStreamChanged(IFrameStream stream)
	{
		_pendingNotifications.Writer.TryWrite(true);

		foreach (var (sessionId, cameraId) in _sessions)
		{
			if (cameraId == stream.Definition.Id)
			{
				_ = ReportSessionStateAsync(sessionId, stream);
			}
		}
	}

	internal async Task ReportSessionStateAsync(string sessionId, IFrameStream stream)
	{
		try
		{
			if (stream.State == VideoStreamState.Connected)
			{
				await _context.UpdateSessionAsync(
					sessionId,
					VideoStreamSessionState.Active,
					reason: VideoStreamSessionReason.SourceRecovered,
					cancellationToken: _dispose.Token).ConfigureAwait(false);
			}
			else if (stream.HasFailed)
			{
				await _context.UpdateSessionAsync(
					sessionId,
					VideoStreamSessionState.Reconnecting,
					reason: VideoStreamSessionReason.SourceLost,
					message: Strings.VideoStreams.Session.Offline(),
					cancellationToken: _dispose.Token).ConfigureAwait(false);
			}
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			_logger.Debug("Could not report a session state: {Reason}", exception.Message);
		}
	}

	private async Task NotifyLoopAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (await _pendingNotifications.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
			{
				while (_pendingNotifications.Reader.TryRead(out _))
				{
				}

				// A camera that reconnects in a loop should not turn into a notification storm.
				await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

				try
				{
					await _context.NotifyStreamsChangedAsync(Id, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is not OperationCanceledException)
				{
					_logger.Debug("Could not report a stream change: {Reason}", exception.Message);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	public async ValueTask DisposeAsync()
	{
		_registry.StreamChanged -= OnStreamChanged;
		_pendingNotifications.Writer.TryComplete();
		await _dispose.CancelAsync().ConfigureAwait(false);

		if (_notificationLoop is not null)
		{
			try
			{
				await _notificationLoop.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		_dispose.Dispose();
	}
}
