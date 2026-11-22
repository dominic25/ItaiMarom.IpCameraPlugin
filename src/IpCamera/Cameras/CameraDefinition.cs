using System.Globalization;

namespace IpCamera.Cameras;

internal enum CameraSourceKind
{
	Snapshot,
	Mjpeg,
	Rtsp
}

/// <summary>
/// One camera as the integration sees it: everything the frame sources and the video stream provider need.
/// The id is the config entry id, so a widget keeps pointing at the same camera when its settings change.
/// </summary>
internal sealed record CameraDefinition(
	string Id,
	string Name,
	CameraSourceKind Kind,
	Uri Url,
	string? Username,
	string? Password,
	int FramesPerSecond,
	int MaxWidth,
	string RtspTransport)
{
	public bool NeedsFfmpeg => Kind == CameraSourceKind.Rtsp;

	// Record ToString would otherwise put the RTSP password into a log or an exception.
	public override string ToString() => $"{Name} ({Kind})";

	/// <summary>
	/// Builds a camera from the values of one config entry. Returns null with a reason for a value the flow
	/// would have refused, so a hand-edited entry cannot take the integration down.
	/// </summary>
	public static CameraDefinition? TryCreate(
		Guid entryId,
		string title,
		IReadOnlyDictionary<string, string?> values,
		string? password,
		out string? error)
	{
		error = null;

		if (!TryReadKind(Read(values, CameraSettings.KindKey), out var kind))
		{
			error = "unknown source kind";
			return null;
		}

		if (!Uri.TryCreate(Read(values, CameraSettings.UrlKey), UriKind.Absolute, out var url))
		{
			error = "missing or invalid stream url";
			return null;
		}

		if (!IsSchemeValid(kind, url.Scheme))
		{
			error = $"scheme '{url.Scheme}' does not fit source kind '{kind}'";
			return null;
		}

		return new CameraDefinition(
			entryId.ToString("D", CultureInfo.InvariantCulture),
			title,
			kind,
			url,
			NullIfEmpty(Read(values, CameraSettings.UsernameKey)),
			NullIfEmpty(password),
			ReadInt(values, CameraSettings.FpsKey, 5, 1, 15),
			ReadInt(values, CameraSettings.MaxWidthKey, 640, 0, 3840),
			Read(values, CameraSettings.RtspTransportKey) == "udp" ? "udp" : "tcp");
	}

	public static bool TryReadKind(string? value, out CameraSourceKind kind)
	{
		switch (value)
		{
			case "mjpeg":
				kind = CameraSourceKind.Mjpeg;
				return true;
			case "rtsp":
				kind = CameraSourceKind.Rtsp;
				return true;
			case "snapshot":
				kind = CameraSourceKind.Snapshot;
				return true;
			default:
				kind = CameraSourceKind.Snapshot;
				return false;
		}
	}

	public static string ToWireValue(CameraSourceKind kind)
	{
		return kind switch
		{
			CameraSourceKind.Mjpeg => "mjpeg",
			CameraSourceKind.Rtsp => "rtsp",
			_ => "snapshot"
		};
	}

	private static bool IsSchemeValid(CameraSourceKind kind, string scheme)
	{
		return kind == CameraSourceKind.Rtsp
			? scheme is "rtsp" or "rtsps"
			: scheme is "http" or "https";
	}

	private static string? Read(IReadOnlyDictionary<string, string?> values, string key)
	{
		return values.TryGetValue(key, out var value) ? value : null;
	}

	private static string? NullIfEmpty(string? value)
	{
		return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	private static int ReadInt(IReadOnlyDictionary<string, string?> values, string key, int fallback, int min, int max)
	{
		return int.TryParse(Read(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
			? Math.Clamp(value, min, max)
			: fallback;
	}
}

/// <summary>The keys the config flow writes and the integration reads back.</summary>
internal static class CameraSettings
{
	public const string KindKey = "kind";
	public const string UrlKey = "url";
	public const string UsernameKey = "username";
	public const string PasswordKey = "password";
	public const string FpsKey = "fps";
	public const string MaxWidthKey = "maxWidth";
	public const string RtspTransportKey = "rtspTransport";
}
