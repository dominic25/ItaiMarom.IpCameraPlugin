using System.Runtime.CompilerServices;

namespace IpCamera.Streaming;

/// <summary>
/// Splits a byte stream into JPEG frames by their start and end markers. Multipart MJPEG and ffmpeg's
/// <c>mjpeg</c> muxer both emit bare concatenated JPEG images, and tolerating the surrounding multipart
/// headers this way means one reader serves every HTTP camera style.
/// </summary>
internal static class JpegFrameReader
{
	private const int MaxFrameBytes = 16 * 1024 * 1024;

	public static async IAsyncEnumerable<byte[]> ReadAsync(
		Stream stream,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var buffer = new byte[64 * 1024];
		var frame = new MemoryStream(128 * 1024);
		var insideFrame = false;
		var previous = -1;

		while (true)
		{
			var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				yield break;
			}

			for (var index = 0; index < read; index++)
			{
				var current = buffer[index];

				if (!insideFrame)
				{
					if (previous == 0xFF && current == 0xD8)
					{
						insideFrame = true;
						frame.WriteByte(0xFF);
						frame.WriteByte(0xD8);
					}
				}
				else
				{
					frame.WriteByte(current);

					if (previous == 0xFF && current == 0xD9)
					{
						var bytes = frame.ToArray();
						frame.SetLength(0);
						insideFrame = false;
						yield return bytes;
					}
					else if (frame.Length > MaxFrameBytes)
					{
						frame.SetLength(0);
						insideFrame = false;
					}
				}

				previous = current;
			}
		}
	}
}
