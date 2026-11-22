using System.Runtime.CompilerServices;
using IpCamera.Cameras;
using IpCamera.Streaming;
using MacroDeck.Sdk.VideoStreams;
using NUnit.Framework;
using Serilog;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraStreamTests
{
	private static readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	private static CameraDefinition Camera()
	{
		return new CameraDefinition(
			"camera-1",
			"Test camera",
			CameraSourceKind.Snapshot,
			new Uri("http://127.0.0.1/snapshot.jpg"),
			null,
			null,
			5,
			640,
			"tcp");
	}

	[Test]
	public async Task Priming_waits_for_the_first_frame()
	{
		await using var stream = new CameraStream(
			Camera(),
			_ => new DelayedSource(JpegTests.Frame(), TimeSpan.FromMilliseconds(150)),
			_logger);

		var primed = await stream.PrimeAsync(TimeSpan.FromSeconds(3), CancellationToken.None);

		Assert.That(primed, Is.True);
		Assert.That(stream.CurrentFrame, Is.Not.Null);
		Assert.That(stream.State, Is.EqualTo(VideoStreamState.Connected));
	}

	[Test]
	public async Task Priming_reports_a_source_that_fails()
	{
		await using var stream = new CameraStream(
			Camera(),
			_ => new FailingSource(),
			_logger);

		var primed = await stream.PrimeAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

		Assert.That(primed, Is.False);
		Assert.That(stream.HasFailed, Is.True);
	}

	[OneTimeTearDown]
	public void DisposeLogger()
	{
		(_logger as IDisposable)?.Dispose();
	}

	private sealed class DelayedSource(byte[] frame, TimeSpan delay) : IFrameSource
	{
		public async IAsyncEnumerable<byte[]> ReadFramesAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await Task.Delay(delay, cancellationToken);
			yield return frame;
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FailingSource : IFrameSource
	{
		public async IAsyncEnumerable<byte[]> ReadFramesAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await Task.Yield();
			cancellationToken.ThrowIfCancellationRequested();
			throw new FrameSourceException("No route to host.");

#pragma warning disable CS0162 // The compiler cannot see that the loop above never completes normally.
			yield break;
#pragma warning restore CS0162
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}
}
