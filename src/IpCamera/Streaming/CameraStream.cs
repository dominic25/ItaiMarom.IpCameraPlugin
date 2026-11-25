using System.Threading.Channels;
using IpCamera.Cameras;
using MacroDeck.Sdk.VideoStreams;
using Serilog;

namespace IpCamera.Streaming;

/// <summary>
/// Runs one camera's frame source while at least one client watches it, and fans the frames out to every
/// client. The source starts on the first subscriber and stops shortly after the last one leaves, so an
/// unused camera costs neither bandwidth nor an ffmpeg process.
/// </summary>
internal sealed class CameraStream : IFrameStream, IAsyncDisposable
{
	private static readonly TimeSpan _idleStopDelay = TimeSpan.FromSeconds(20);

	// The player is expected to be idle at a low frame rate. A frame repeated twice keeps Macro Deck's relay
	// from cutting a session after its 30 second silence limit.
	private static readonly TimeSpan _keepAliveInterval = TimeSpan.FromSeconds(10);

	private readonly CameraDefinition _camera;
	private readonly Func<CameraDefinition, IFrameSource> _sourceFactory;
	private readonly ILogger _logger;
	private readonly object _gate = new();
	private readonly List<Channel<byte[]>> _subscribers = [];
	private readonly CancellationTokenSource _dispose = new();

	private Task? _pump;
	private CancellationTokenSource? _run;
	private CancellationTokenSource? _idleStop;
	private byte[]? _currentFrame;
	private long _lastFrameTicks;
	private DateTimeOffset? _lastFrameAt;
	private VideoStreamState _state = VideoStreamState.Disconnected;
	private bool _hasFailed;
	private int? _width;
	private int? _height;
	private bool _disposed;

	public CameraStream(
		CameraDefinition camera,
		Func<CameraDefinition, IFrameSource> sourceFactory,
		ILogger logger)
	{
		_camera = camera;
		_sourceFactory = sourceFactory;
		_logger = logger.ForContext<CameraStream>().ForContext("Camera", camera.Name);
	}

	public CameraDefinition Definition => _camera;

	public VideoStreamState State
	{
		get
		{
			lock (_gate)
			{
				return _state;
			}
		}
	}

	public int? Width
	{
		get
		{
			lock (_gate)
			{
				return _width;
			}
		}
	}

	public bool HasFailed
	{
		get
		{
			lock (_gate)
			{
				return _hasFailed;
			}
		}
	}

	public DateTimeOffset? LastFrameAt
	{
		get
		{
			lock (_gate)
			{
				return _lastFrameAt;
			}
		}
	}

	public int? Height
	{
		get
		{
			lock (_gate)
			{
				return _height;
			}
		}
	}

	public byte[]? CurrentFrame
	{
		get
		{
			lock (_gate)
			{
				return _currentFrame;
			}
		}
	}

	public event EventHandler? StateChanged;

	public FrameSubscription Subscribe()
	{
		var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true
		});

		byte[]? first;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			_subscribers.Add(channel);
			first = _currentFrame;
			CancelIdleStop();
			EnsureRunning();
		}

		if (first is not null)
		{
			channel.Writer.TryWrite(first);
		}

		return new FrameSubscription(channel.Reader, () => Unsubscribe(channel));
	}

	private void Unsubscribe(Channel<byte[]> channel)
	{
		lock (_gate)
		{
			if (!_subscribers.Remove(channel))
			{
				return;
			}

			channel.Writer.TryComplete();

			if (_subscribers.Count == 0)
			{
				ScheduleIdleStop();
			}
		}
	}

	// Callers hold _gate.
	private void EnsureRunning()
	{
		if (_pump is not null)
		{
			return;
		}

		_run = CancellationTokenSource.CreateLinkedTokenSource(_dispose.Token);
		var token = _run.Token;
		_pump = Task.Run(async () => await Task.WhenAll(PumpAsync(token), KeepAliveAsync(token)).ConfigureAwait(false));
	}

	// Callers hold _gate.
	private void CancelIdleStop()
	{
		_idleStop?.Cancel();
		_idleStop?.Dispose();
		_idleStop = null;
	}

	// Callers hold _gate.
	private void ScheduleIdleStop()
	{
		CancelIdleStop();
		var stop = CancellationTokenSource.CreateLinkedTokenSource(_dispose.Token);
		_idleStop = stop;
		_ = StopAfterIdleAsync(stop);
	}

	private async Task StopAfterIdleAsync(CancellationTokenSource stop)
	{
		try
		{
			await Task.Delay(_idleStopDelay, stop.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		lock (_gate)
		{
			if (ReferenceEquals(_idleStop, stop))
			{
				_idleStop = null;
			}
		}

		await StopAsync().ConfigureAwait(false);
	}

	private async Task StopAsync()
	{
		Task? pump;
		CancellationTokenSource? run;

		lock (_gate)
		{
			if (_subscribers.Count > 0 || _pump is null)
			{
				return;
			}

			pump = _pump;
			run = _run;
			_pump = null;
			_run = null;
			_currentFrame = null;
			_lastFrameAt = null;
		}

		if (run is not null)
		{
			await run.CancelAsync().ConfigureAwait(false);
		}

		if (pump is not null)
		{
			try
			{
				await pump.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		run?.Dispose();
		SetIdle();
	}

	private async Task PumpAsync(CancellationToken cancellationToken)
	{
		var backoff = TimeSpan.FromSeconds(1);

		while (!cancellationToken.IsCancellationRequested)
		{
			SetState(VideoStreamState.Connecting);

			try
			{
				await using var source = _sourceFactory(_camera);
				await foreach (var frame in source.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
				{
					backoff = TimeSpan.FromSeconds(1);
					Publish(frame);
					SetState(VideoStreamState.Connected);
				}

				throw new FrameSourceException("The frame source ended.");
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				SetFailed();
				_logger.Warning("Camera frame source failed: {Reason}", exception.Message);
			}

			try
			{
				await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(15).Ticks));
		}
	}

	private async Task KeepAliveAsync(CancellationToken cancellationToken)
	{
		using var timer = new PeriodicTimer(_keepAliveInterval);

		try
		{
			while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
			{
				byte[]? frame;
				lock (_gate)
				{
					if (_subscribers.Count == 0 ||
						_currentFrame is null ||
						Environment.TickCount64 - _lastFrameTicks < _keepAliveInterval.TotalMilliseconds / 2)
					{
						continue;
					}

					frame = _currentFrame;
					foreach (var subscriber in _subscribers)
					{
						subscriber.Writer.TryWrite(frame);
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void Publish(byte[] frame)
	{
		lock (_gate)
		{
			_currentFrame = frame;
			_lastFrameTicks = Environment.TickCount64;
			_lastFrameAt = DateTimeOffset.UtcNow;

			if (JpegInfo.TryReadSize(frame, out var width, out var height))
			{
				_width = width;
				_height = height;
			}

			foreach (var subscriber in _subscribers)
			{
				subscriber.Writer.TryWrite(frame);
			}
		}
	}

	private void SetState(VideoStreamState state)
	{
		bool changed;

		lock (_gate)
		{
			changed = _state != state;
			_state = state;

			if (state == VideoStreamState.Connected && _hasFailed)
			{
				_hasFailed = false;
				changed = true;
			}
		}

		if (changed)
		{
			StateChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	private void SetFailed()
	{
		bool changed;

		lock (_gate)
		{
			changed = !_hasFailed || _state != VideoStreamState.Disconnected;
			_hasFailed = true;
			_state = VideoStreamState.Disconnected;
		}

		if (changed)
		{
			StateChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	private void SetIdle()
	{
		bool changed;

		lock (_gate)
		{
			changed = _hasFailed || _state != VideoStreamState.Disconnected;
			_hasFailed = false;
			_state = VideoStreamState.Disconnected;
		}

		if (changed)
		{
			StateChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>Drops the current source and starts a new one when somebody is still watching.</summary>
	public async Task<bool> PrimeAsync(TimeSpan timeout, CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			if (_currentFrame is not null)
			{
				return true;
			}

			ObjectDisposedException.ThrowIf(_disposed, this);
			EnsureRunning();

			// Nobody is subscribed yet; stop again if the viewer never opens the URL.
			if (_subscribers.Count == 0)
			{
				ScheduleIdleStop();
			}
		}

		var deadline = DateTime.UtcNow + timeout;

		while (DateTime.UtcNow < deadline)
		{
			if (CurrentFrame is not null)
			{
				return true;
			}

			if (HasFailed)
			{
				return false;
			}

			try
			{
				await Task.Delay(100, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return false;
			}
		}

		return CurrentFrame is not null;
	}

	public async Task RestartAsync()
	{
		Task? pump;
		CancellationTokenSource? run;
		bool resume;

		lock (_gate)
		{
			pump = _pump;
			run = _run;
			_pump = null;
			_run = null;
			_currentFrame = null;
			_lastFrameAt = null;
			_hasFailed = false;
			resume = _subscribers.Count > 0 && !_disposed;
		}

		if (run is not null)
		{
			await run.CancelAsync().ConfigureAwait(false);
		}

		if (pump is not null)
		{
			try
			{
				await pump.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		run?.Dispose();
		SetIdle();

		if (resume)
		{
			lock (_gate)
			{
				if (_subscribers.Count > 0 && !_disposed)
				{
					EnsureRunning();
				}
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		Task? pump;
		CancellationTokenSource? run;
		Channel<byte[]>[] subscribers;

		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			pump = _pump;
			run = _run;
			subscribers = [.. _subscribers];
			_pump = null;
			_run = null;
			_subscribers.Clear();
			CancelIdleStop();
		}

		await _dispose.CancelAsync().ConfigureAwait(false);

		if (run is not null)
		{
			await run.CancelAsync().ConfigureAwait(false);
		}

		if (pump is not null)
		{
			try
			{
				await pump.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		foreach (var subscriber in subscribers)
		{
			subscriber.Writer.TryComplete();
		}

		run?.Dispose();
		_dispose.Dispose();
	}
}
