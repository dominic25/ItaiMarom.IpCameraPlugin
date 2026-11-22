using System.Threading.Channels;
using MacroDeck.Sdk.VideoStreams;

namespace IpCamera.Streaming;

/// <summary>One camera's live frame pipeline, as the MJPEG server and the video stream provider see it.</summary>
internal interface IFrameStream
{
	VideoStreamState State { get; }

	int? Width { get; }

	int? Height { get; }

	/// <summary>The most recent frame, handed to a new subscriber so its first picture arrives at once.</summary>
	byte[]? CurrentFrame { get; }

	event EventHandler? StateChanged;

	FrameSubscription Subscribe();
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
