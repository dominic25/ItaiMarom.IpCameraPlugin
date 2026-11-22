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
