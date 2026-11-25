using System.Threading.Channels;
using IpCamera.Cameras;
using MacroDeck.Sdk.VideoStreams;

namespace IpCamera.Streaming;

/// <summary>One camera's live frame pipeline, as the MJPEG server and the video stream provider see it.</summary>
internal interface IFrameStream : IAsyncDisposable
{
	CameraDefinition Definition { get; }

	VideoStreamState State { get; }

	/// <summary>Whether the source failed since it last delivered a frame.</summary>
	bool HasFailed { get; }

	/// <summary>When the most recent frame arrived, or <c>null</c> before the first frame and after an idle stop.</summary>
	DateTimeOffset? LastFrameAt { get; }

	int? Width { get; }

	int? Height { get; }

	/// <summary>The most recent frame, handed to a new subscriber so its first picture arrives at once.</summary>
	byte[]? CurrentFrame { get; }

	event EventHandler? StateChanged;

	FrameSubscription Subscribe();

	/// <summary>Drops the current source and starts a new one when somebody is still watching.</summary>
	Task RestartAsync();

	/// <summary>Starts the source and waits for a first frame, so the first viewer gets a picture at once.</summary>
	Task<bool> PrimeAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>A consumer's view of a stream: a bounded channel that drops its oldest frame rather than
/// growing when the client falls behind.</summary>
internal sealed class FrameSubscription : IDisposable
{
	private readonly Action _release;

	public FrameSubscription(ChannelReader<byte[]> reader, Action release)
	{
		Reader = reader;
		_release = release;
	}

	public ChannelReader<byte[]> Reader { get; }

	public void Dispose()
	{
		_release();
	}
}

/// <summary>Produces JPEG frames for one camera and ends when the source ends or fails.</summary>
internal interface IFrameSource : IAsyncDisposable
{
	IAsyncEnumerable<byte[]> ReadFramesAsync(CancellationToken cancellationToken);
}

/// <summary>A source that could not deliver frames, with a message meant for the log only.</summary>
internal sealed class FrameSourceException(string message, Exception? innerException = null)
	: Exception(message, innerException);
