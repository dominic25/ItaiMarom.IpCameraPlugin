using System.Text.Json;
using IpCamera.Cameras;
using IpCamera.Widgets;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraWallViewTests
{
	private const string ProviderId = "test-plugin::cameras";

	[Test]
	public void A_single_camera_fills_the_folder()
	{
		var config = JsonSerializer.SerializeToElement(new { camera1 = "camera-1", columns = "1" });

		using var view = new UiView(Surface(), CameraWallView.Build(config, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.EqualTo(1));
	}

	[Test]
	public void Two_columns_draw_two_streams()
	{
		var config = JsonSerializer.SerializeToElement(new
		{
			camera1 = "camera-1",
			camera2 = "camera-2",
			columns = "2"
		});

		using var view = new UiView(Surface(), CameraWallView.Build(config, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.EqualTo(2));
	}

	[Test]
	public void Four_cameras_draw_four_streams()
	{
		var config = JsonSerializer.SerializeToElement(new
		{
			camera1 = "camera-1",
			camera2 = "camera-2",
			camera3 = "camera-3",
			camera4 = "camera-4",
			columns = "2"
		});

		using var view = new UiView(Surface(), CameraWallView.Build(config, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.EqualTo(4));
	}

	[Test]
	public void An_empty_position_is_skipped()
	{
		var config = JsonSerializer.SerializeToElement(new
		{
			camera1 = "",
			camera2 = "camera-2",
			columns = "2"
		});

		using var view = new UiView(Surface(), CameraWallView.Build(config, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.EqualTo(1));
	}

	[Test]
	public void One_column_shows_only_the_configured_cameras()
	{
		var config = JsonSerializer.SerializeToElement(new
		{
			camera1 = "camera-1",
			camera2 = "camera-2",
			camera3 = "",
			camera4 = "",
			columns = "1"
		});

		using var view = new UiView(Surface(), CameraWallView.Build(config, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.EqualTo(2));
	}

	[Test]
	public void A_configuration_stored_as_a_string_is_read()
	{
		var json = """{"camera1":"camera-1","camera2":"camera-2","columns":"2"}""";
		var config = JsonSerializer.SerializeToElement(json);

		using var view = new UiView(Surface(), CameraWallView.Build(config, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.EqualTo(2));
	}

	[Test]
	public void An_empty_wall_draws_the_hint()
	{
		var config = JsonSerializer.SerializeToElement(new { columns = "1" });

		using var view = new UiView(Surface(), CameraWallView.Build(config, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.Zero);
		Assert.That(ContainsId(view.Tree.Root, "wall-placeholder"), Is.True);
	}

	[Test]
	public void The_folder_configuration_offers_cameras_columns_and_fit()
	{
		var config = JsonSerializer.SerializeToElement(new { camera1 = "camera-1", columns = "2" });

		using var view = new UiView(ConfigSurface(), CameraWallView.BuildConfiguration(config));

		Assert.Multiple(() =>
		{
			Assert.That(ContainsId(view.Tree.Root, "camera1"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "camera4"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "columns"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "fit"), Is.True);
		});
	}

	[Test]
	public void The_wall_draws_movement_arrows_on_each_cell_when_enabled()
	{
		var config = JsonSerializer.SerializeToElement(new
		{
			camera1 = "camera-1",
			camera2 = "camera-2",
			columns = "2",
			controls = true,
			controlsOpacity = 40
		});
		var camera = Camera();

		var view = CameraWallView.Build(
			config,
			ProviderId,
			_ => camera,
			(_, _, _) => Task.CompletedTask);
		using var ui = new UiView(Surface(), view);

		Assert.Multiple(() =>
		{
			Assert.That(ContainsId(ui.Tree.Root, "w0-left"), Is.True);
			Assert.That(ContainsId(ui.Tree.Root, "w1-right"), Is.True);
		});
	}

	[Test]
	public void The_wall_has_no_arrows_by_default()
	{
		var config = JsonSerializer.SerializeToElement(new { camera1 = "camera-1" });

		var view = CameraWallView.Build(
			config,
			ProviderId,
			_ => Camera(),
			(_, _, _) => Task.CompletedTask);
		using var ui = new UiView(Surface(), view);

		Assert.That(ContainsId(ui.Tree.Root, "w0-left"), Is.False);
	}

	[Test]
	public void Every_configured_cell_opens_its_own_camera()
	{
		var config = JsonSerializer.SerializeToElement(new { camera1 = "camera-1", camera2 = "camera-2", columns = "2" });
		var targets = new Dictionary<string, string>(StringComparer.Ordinal);

		using var view = new UiView(Surface(), CameraWallView.Build(config, ProviderId, openTargets: targets));

		Assert.Multiple(() =>
		{
			Assert.That(targets, Is.EqualTo(new Dictionary<string, string>
			{
				["open-camera-0"] = "camera-1",
				["open-camera-1"] = "camera-2"
			}));
			Assert.That(ContainsId(view.Tree.Root, "open-camera-0"), Is.True);
		});
	}

	[Test]
	public void The_folder_configuration_offers_arrows_and_their_opacity()
	{
		var config = JsonSerializer.SerializeToElement(new { camera1 = "camera-1" });

		using var view = new UiView(ConfigSurface(), CameraWallView.BuildConfiguration(config));

		Assert.Multiple(() =>
		{
			Assert.That(ContainsId(view.Tree.Root, "controls"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "controlsOpacity"), Is.True);
		});
	}

	private static CameraDefinition Camera()
	{
		return new CameraDefinition(
			"camera-1",
			"Test camera",
			CameraSourceKind.Mjpeg,
			new Uri("http://127.0.0.1/camera.mjpeg"),
			null,
			null,
			5,
			640,
			"tcp");
	}

	private static UiSurface Surface()
	{
		return new UiSurface
		{
			Kind = UiSurfaceKinds.Folder,
			SessionMode = UiSessionModes.Shared
		};
	}

	private static UiSurface ConfigSurface()
	{
		return new UiSurface
		{
			Kind = UiSurfaceKinds.Config,
			SessionMode = UiSessionModes.Exclusive
		};
	}

	private static int CountType(UiNode node, string type)
	{
		return (node.Type == type ? 1 : 0) +
			node.Children.Sum(child => CountType(child, type));
	}

	private static bool ContainsId(UiNode node, string id)
	{
		return node.Id.EndsWith(id, StringComparison.Ordinal) ||
			node.Children.Any(child => ContainsId(child, id));
	}
}
