using System.Globalization;

namespace IpCamera.Cameras;

internal enum CameraSourceKind
{
	Snapshot,
	Mjpeg,
	Rtsp
}

/// <summary>How a camera is controlled, separate from how its video is fetched.</summary>
internal enum CameraControlKind
{
	/// <summary>Talk ONVIF to the device the stream points at.</summary>
	Onvif,

	/// <summary>Talk miIO to a Xiaomi camera, using the address and token from the entry.</summary>
	Xiaomi,

	/// <summary>The camera has no controls.</summary>
	None
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

	/// <summary>How control actions talk to this camera. Video settings never depend on it.</summary>
	public CameraControlKind Control { get; init; } = CameraControlKind.Onvif;

	/// <summary>The device address for control, when it differs from the stream address.</summary>
	public string? ControlHost { get; init; }

	/// <summary>The Xiaomi device token. Never log it, never write it to a widget.</summary>
	public string? ControlToken { get; init; }

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
		out string? error,
		string? controlToken = null)
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

		var control = Read(values, CameraSettings.ControlKey) switch
		{
			"xiaomi" => CameraControlKind.Xiaomi,
			"none" => CameraControlKind.None,
			_ => CameraControlKind.Onvif
		};

		return new CameraDefinition(
			entryId.ToString("D", CultureInfo.InvariantCulture),
			title,
			kind,
			url,
			NullIfEmpty(Read(values, CameraSettings.UsernameKey)),
			NullIfEmpty(password),
			ApplyQuality(Read(values, CameraSettings.QualityKey), ReadInt(values, CameraSettings.FpsKey, 5, 1, 15)),
			ApplyQualityWidth(Read(values, CameraSettings.QualityKey), ReadInt(values, CameraSettings.MaxWidthKey, 640, 0, 3840)),
			Read(values, CameraSettings.RtspTransportKey) == "udp" ? "udp" : "tcp")
		{
			Control = control,
			ControlHost = NullIfEmpty(Read(values, CameraSettings.ControlHostKey)),
			ControlToken = NullIfEmpty(controlToken)
		};
	}

	private static int ApplyQuality(string? quality, int custom)
	{
		return quality switch
		{
			"low" => 2,
			"balanced" => 5,
			"high" => 10,
			_ => custom
		};
	}

	private static int ApplyQualityWidth(string? quality, int custom)
	{
		return quality switch
		{
			"low" => 640,
			"balanced" => 1280,
			"high" => 1920,
			_ => custom
		};
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
	public const string QualityKey = "quality";
	public const string ControlKey = "control";
	public const string ControlHostKey = "controlHost";
	public const string ControlTokenKey = "controlToken";
}
