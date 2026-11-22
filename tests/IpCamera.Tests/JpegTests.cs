using IpCamera.Streaming;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class JpegTests
{
	internal static byte[] Frame(int width = 640, int height = 360)
	{
		// A JPEG header with one component is enough to parse a size and to pass the marker checks.
		return
		[
			0xFF, 0xD8,
			0xFF, 0xC0, 0x00, 0x0B, 0x08,
			(byte)(height >> 8), (byte)height,
			(byte)(width >> 8), (byte)width,
			0x01,
			0x01, 0x11, 0x00,
			0xFF, 0xD9
		];
	}

	[Test]
	public void The_size_is_read_from_the_start_of_frame_marker()
	{
		var parsed = JpegInfo.TryReadSize(Frame(1920, 1080), out var width, out var height);

		Assert.That(parsed, Is.True);
		Assert.That(width, Is.EqualTo(1920));
		Assert.That(height, Is.EqualTo(1080));
	}

	[Test]
	public void Data_that_does_not_begin_or_end_like_a_jpeg_is_not_a_frame()
	{
		Assert.That(JpegInfo.IsJpeg(Frame() ), Is.True);
		Assert.That(JpegInfo.IsJpeg([1, 2, 3]), Is.False);
		Assert.That(JpegInfo.IsJpeg(Frame().AsSpan(0, Frame().Length - 2).ToArray()), Is.False);
	}

	[Test]
	public async Task Frames_are_split_out_of_a_multipart_stream()
	{
		var first = Frame(640, 360);
		var second = Frame(320, 240);
		var bytes = new List<byte>();
		bytes.AddRange("--frame\r\nContent-Type: image/jpeg\r\n\r\n"u8.ToArray());
		bytes.AddRange(first);
		bytes.AddRange("\r\n--frame\r\n\r\n"u8.ToArray());
		bytes.AddRange(second);

		var frames = await ReadAsync(bytes.ToArray(), 2);

		Assert.That(frames, Has.Count.EqualTo(2));
		Assert.That(frames[0], Is.EqualTo(first));
		Assert.That(frames[1], Is.EqualTo(second));
	}

	[Test]
	public async Task A_partial_frame_at_the_end_is_not_reported()
	{
		var bytes = Frame();
		var frames = await ReadAsync(bytes[..^2], 1, TimeSpan.FromMilliseconds(200));

		Assert.That(frames, Is.Empty);
	}

	private static async Task<List<byte[]>> ReadAsync(byte[] data, int wanted, TimeSpan? timeout = null)
	{
		var frames = new List<byte[]>();
		using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));

		try
		{
			await foreach (var frame in JpegFrameReader
				.ReadAsync(new MemoryStream(data), cancellation.Token)
				.ConfigureAwait(false))
			{
				frames.Add(frame);
				if (frames.Count == wanted)
				{
					break;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}

		return frames;
	}
}
