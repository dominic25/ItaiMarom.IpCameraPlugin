using IpCamera.Cameras;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraDefinitionTests
{
	private static readonly Guid _entryId = Guid.Parse("11111111-1111-1111-1111-111111111111");

	private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs)
	{
		return pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
	}

	[Test]
	public void A_snapshot_camera_reads_its_url_and_defaults()
	{
		var camera = CameraDefinition.TryCreate(
			_entryId,
			"Front door",
			Values(
				(CameraSettings.KindKey, "snapshot"),
				(CameraSettings.UrlKey, "http://192.168.1.20/snapshot.jpg")),
			null,
			out var error);

		Assert.That(camera, Is.Not.Null, error);
		Assert.That(camera!.Kind, Is.EqualTo(CameraSourceKind.Snapshot));
		Assert.That(camera.FramesPerSecond, Is.EqualTo(5));
		Assert.That(camera.MaxWidth, Is.EqualTo(640));
		Assert.That(camera.Id, Is.EqualTo(_entryId.ToString("D")));
	}

	[Test]
	public void An_rtsp_camera_keeps_its_transport_and_clamps_the_frame_rate()
	{
		var camera = CameraDefinition.TryCreate(
			_entryId,
			"Garden",
			Values(
				(CameraSettings.KindKey, "rtsp"),
				(CameraSettings.UrlKey, "rtsp://192.168.1.30/live/ch00_0"),
				(CameraSettings.FpsKey, "40"),
				(CameraSettings.RtspTransportKey, "udp")),
			null,
			out _);

		Assert.That(camera, Is.Not.Null);
		Assert.That(camera!.FramesPerSecond, Is.EqualTo(15));
		Assert.That(camera.RtspTransport, Is.EqualTo("udp"));
		Assert.That(camera.NeedsFfmpeg, Is.True);
	}

	[TestCase("low", 2, 640)]
	[TestCase("balanced", 5, 1280)]
	[TestCase("high", 10, 1920)]
	public void A_quality_preset_overrides_the_custom_values(string quality, int fps, int maxWidth)
	{
		var camera = CameraDefinition.TryCreate(
			_entryId,
			"Front door",
			Values(
				(CameraSettings.KindKey, "mjpeg"),
				(CameraSettings.UrlKey, "http://192.168.1.20/camera.mjpeg"),
				(CameraSettings.QualityKey, quality),
				(CameraSettings.FpsKey, "12"),
				(CameraSettings.MaxWidthKey, "3840")),
			null,
			out var error);

		Assert.That(camera, Is.Not.Null, error);
		Assert.That(camera!.FramesPerSecond, Is.EqualTo(fps));
		Assert.That(camera.MaxWidth, Is.EqualTo(maxWidth));
	}

	[Test]
	public void The_control_settings_are_read()
	{
		var camera = CameraDefinition.TryCreate(
			_entryId,
			"mi360",
			Values(
				(CameraSettings.KindKey, "mjpeg"),
				(CameraSettings.UrlKey, "http://192.168.1.60:1984/api/stream.mjpeg?src=mi360_mjpeg"),
				(CameraSettings.ControlKey, "xiaomi"),
				(CameraSettings.ControlHostKey, "192.168.1.30")),
			null,
			out var error,
			"00112233445566778899aabbccddeeff");

		Assert.That(camera, Is.Not.Null, error);
		Assert.Multiple(() =>
		{
			Assert.That(camera!.Control, Is.EqualTo(CameraControlKind.Xiaomi));
			Assert.That(camera.ControlHost, Is.EqualTo("192.168.1.30"));
			Assert.That(camera.ControlToken, Is.EqualTo("00112233445566778899aabbccddeeff"));
		});
	}

	[Test]
	public void A_camera_without_a_control_setting_uses_onvif()
	{
		var camera = CameraDefinition.TryCreate(
			_entryId,
			"Front door",
			Values(
				(CameraSettings.KindKey, "mjpeg"),
				(CameraSettings.UrlKey, "http://192.168.1.20/camera.mjpeg")),
			null,
			out var error);

		Assert.That(camera, Is.Not.Null, error);
		Assert.That(camera!.Control, Is.EqualTo(CameraControlKind.Onvif));
		Assert.That(camera.ControlHost, Is.Null);
	}

	[Test]
	public void The_custom_quality_keeps_the_typed_values()
	{
		var camera = CameraDefinition.TryCreate(
			_entryId,
			"Front door",
			Values(
				(CameraSettings.KindKey, "mjpeg"),
				(CameraSettings.UrlKey, "http://192.168.1.20/camera.mjpeg"),
				(CameraSettings.QualityKey, "custom"),
				(CameraSettings.FpsKey, "12"),
				(CameraSettings.MaxWidthKey, "800")),
			null,
			out var error);

		Assert.That(camera, Is.Not.Null, error);
		Assert.That(camera!.FramesPerSecond, Is.EqualTo(12));
		Assert.That(camera.MaxWidth, Is.EqualTo(800));
	}

	[Test]
	public void An_entry_without_a_url_is_rejected()
	{
		var camera = CameraDefinition.TryCreate(
			_entryId,
			"Broken",
			Values((CameraSettings.KindKey, "snapshot")),
			null,
			out var error);

		Assert.That(camera, Is.Null);
		Assert.That(error, Does.Contain("url"));
	}

	[Test]
	public void A_scheme_that_does_not_fit_the_kind_is_rejected()
	{
		var camera = CameraDefinition.TryCreate(
			_entryId,
			"Wrong",
			Values(
				(CameraSettings.KindKey, "rtsp"),
				(CameraSettings.UrlKey, "http://192.168.1.30/snapshot.jpg")),
			null,
			out var error);

		Assert.That(camera, Is.Null);
		Assert.That(error, Does.Contain("scheme"));
	}

	[Test]
	public void An_unknown_kind_is_rejected()
	{
		var camera = CameraDefinition.TryCreate(
			_entryId,
			"Odd",
			Values(
				(CameraSettings.KindKey, "onvif"),
				(CameraSettings.UrlKey, "http://192.168.1.30/snapshot.jpg")),
			null,
			out _);

		Assert.That(camera, Is.Null);
	}
}
