using IpCamera.Streaming;
using Serilog;

namespace IpCamera.Cameras;

/// <summary>
/// The cameras of the current configuration and the live pipeline of each. Replacing the camera list keeps
/// the pipelines whose settings did not change, so an edit of one camera does not interrupt the others.
/// </summary>
internal sealed class CameraRegistry(Func<CameraDefinition, IFrameStream> streamFactory) : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly Dictionary<string, IFrameStream> _streams = new(StringComparer.Ordinal);
	private IReadOnlyList<CameraDefinition> _cameras = [];
	private bool _disposed;

	public event Action<IFrameStream>? StreamChanged;

	public IReadOnlyList<CameraDefinition> Cameras
	{
		get
		{
			lock (_gate)
			{
				return _cameras;
			}
		}
	}

	public IFrameStream? Find(string cameraId)
	{
		lock (_gate)
		{
			return _streams.GetValueOrDefault(cameraId);
		}
	}

	public async Task SetCamerasAsync(IReadOnlyList<CameraDefinition> cameras, CancellationToken cancellationToken)
	{
		List<IFrameStream> removed = [];
		var added = new List<IFrameStream>();

		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			foreach (var (id, stream) in _streams)
			{
				if (!cameras.Any(camera => camera.Id == id && camera == stream.Definition))
				{
					removed.Add(stream);
				}
			}

			var next = new Dictionary<string, IFrameStream>(cameras.Count, StringComparer.Ordinal);
			foreach (var camera in cameras)
			{
				if (_streams.TryGetValue(camera.Id, out var existing) && camera == existing.Definition)
				{
					next[camera.Id] = existing;
					continue;
				}

				var stream = streamFactory(camera);
				stream.StateChanged += (_, _) => StreamChanged?.Invoke(stream);
				next[camera.Id] = stream;
				added.Add(stream);
			}

			_streams.Clear();
			foreach (var (id, stream) in next)
			{
				_streams[id] = stream;
			}

			_cameras = cameras;
		}

		foreach (var stream in removed)
		{
			await stream.DisposeAsync().ConfigureAwait(false);
		}

		foreach (var stream in added)
		{
			StreamChanged?.Invoke(stream);
		}

		cancellationToken.ThrowIfCancellationRequested();
	}

	public async ValueTask DisposeAsync()
	{
		IFrameStream[] streams;

		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			streams = [.. _streams.Values];
			_streams.Clear();
			_cameras = [];
		}

		foreach (var stream in streams)
		{
			await stream.DisposeAsync().ConfigureAwait(false);
		}
	}
}
