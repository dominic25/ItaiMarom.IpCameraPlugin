namespace IpCamera.Streaming;

/// <summary>Reads the pixel size out of a JPEG frame so the deck can reserve the right aspect ratio.</summary>
internal static class JpegInfo
{
	public static bool IsJpeg(ReadOnlySpan<byte> data)
	{
		return data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[^2] == 0xFF && data[^1] == 0xD9;
	}

	public static bool TryReadSize(ReadOnlySpan<byte> jpeg, out int width, out int height)
	{
		width = 0;
		height = 0;

		if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
		{
			return false;
		}

		var offset = 2;
		while (offset + 3 < jpeg.Length)
		{
			if (jpeg[offset] != 0xFF)
			{
				offset++;
				continue;
			}

			var marker = jpeg[offset + 1];
			offset += 2;

			if (marker is 0xD8 or 0xD9 or 0xDA || marker is >= 0xD0 and <= 0xD7)
			{
				if (marker == 0xDA)
				{
					return false;
				}

				continue;
			}

			if (offset + 1 >= jpeg.Length)
			{
				return false;
			}

			var length = (jpeg[offset] << 8) | jpeg[offset + 1];
			if (length < 2)
			{
				return false;
			}

			if (IsStartOfFrame(marker) && offset + 7 < jpeg.Length)
			{
				height = (jpeg[offset + 3] << 8) | jpeg[offset + 4];
				width = (jpeg[offset + 5] << 8) | jpeg[offset + 6];
				return width > 0 && height > 0;
			}

			offset += length;
		}

		return false;
	}

	private static bool IsStartOfFrame(byte marker)
	{
		return marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
	}
}
