using System.Text.Json;
using IpCamera.Actions;
using IpCamera.Cameras;
using IpCamera.Streaming;
using IpCamera.Widgets;
using MacroDeck.Sdk.VideoStreams;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraWidgetTests
{
	private const string ProviderId = "test-plugin::cameras";

	[Test]
	public void A_configured_widget_draws_the_selected_stream()
	{
		var surface = Surface(new { camera = "camera-1", fit = "contain" });

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(Contains(view.Tree.Root, "macrodeck.video-stream"), Is.True);
	}

	[Test]
	public void A_label_is_drawn_over_the_picture()
	{
		var surface = Surface(new { camera = "camera-1", label = "Front door" });

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(Contains(view.Tree.Root, "ui.text"), Is.True);
	}

	[Test]
	public void An_unconfigured_widget_draws_the_hint()
	{
		var surface = Surface(new { });

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(view.Tree.Root.Type, Is.EqualTo("ui.stack"));
	}

	[Test]
	public void The_picker_sample_draws_without_a_stream()
	{
		var surface = new UiSurface
		{
			Kind = UiSurfaceKinds.Preview,
			SessionMode = UiSessionModes.Shared,
			Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["sample"] = JsonSerializer.SerializeToElement(true),
				["data"] = JsonSerializer.SerializeToElement(new { camera = "camera-1" })
			}
		};

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(view.Tree.Root.Type, Is.EqualTo("ui.stack"));
	}

	[Test]
	public void The_configuration_offers_the_camera_picker_and_the_fit_choice()
	{
		var surface = new UiSurface
		{
			Kind = UiSurfaceKinds.Config,
			SessionMode = UiSessionModes.Exclusive,
			Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["entryPoint"] = JsonSerializer.SerializeToElement("widget-config"),
				["widgetData"] = JsonSerializer.SerializeToElement(new { camera = "camera-1", fit = "cover" })
			}
		};

		using var view = new UiView(surface, CameraWidget.BuildConfiguration(surface));

		Assert.That(view.Tree.Root.Type, Is.EqualTo("widget-configuration"));
		Assert.That(ContainsId(view.Tree.Root, "overlay"), Is.True);
		Assert.That(ContainsId(view.Tree.Root, "overlayBackground"), Is.True);
	}

	[Test]
	public void The_status_background_is_optional()
	{
		var surface = Surface(new { camera = "camera-1", overlay = true, overlayBackground = 100 });

		var view = CameraWidget.BuildView(surface, ProviderId, _ => null);
		using var ui = new UiView(surface, view.Root);

		var pill = FindById(ui.Tree.Root, "status-pill");
		Assert.That(pill, Is.Not.Null);
		Assert.That(pill!.Properties.ContainsKey("background"), Is.True);

		var plain = Surface(new { camera = "camera-1", overlay = true, overlayBackground = 0 });
		var plainView = CameraWidget.BuildView(plain, ProviderId, _ => null);
		using var plainUi = new UiView(plain, plainView.Root);

		var plainPill = FindById(plainUi.Tree.Root, "status-pill");
		Assert.That(plainPill, Is.Not.Null);
		Assert.That(plainPill!.Properties.ContainsKey("background"), Is.False);
	}

	[Test]
	public void The_split_layout_draws_two_streams()
	{
		var surface = Surface(new
		{
			camera = "camera-1",
			camera2 = "camera-2",
			layout = CameraWidget.LayoutSplit
		});

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.EqualTo(2));
	}

	[Test]
	public void The_grid_layout_draws_four_streams()
	{
		var surface = Surface(new
		{
			camera = "camera-1",
			camera2 = "camera-2",
			camera3 = "camera-3",
			camera4 = "camera-4",
			layout = CameraWidget.LayoutGrid
		});

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.EqualTo(4));
	}

	[Test]
	public void An_empty_grid_cell_draws_the_hint_instead_of_a_stream()
	{
		var surface = Surface(new
		{
			camera = "camera-1",
			camera2 = "camera-2",
			camera3 = "camera-3",
			layout = CameraWidget.LayoutGrid
		});

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(CountType(view.Tree.Root, "macrodeck.video-stream"), Is.EqualTo(3));
		Assert.That(ContainsId(view.Tree.Root, "cell-3-empty"), Is.True);
	}

	[Test]
	public void The_configuration_offers_the_layout_and_extra_cameras()
	{
		var surface = new UiSurface
		{
			Kind = UiSurfaceKinds.Config,
			SessionMode = UiSessionModes.Exclusive,
			Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["entryPoint"] = JsonSerializer.SerializeToElement("widget-config"),
				["widgetData"] = JsonSerializer.SerializeToElement(new { camera = "camera-1", layout = "single" })
			}
		};

		using var view = new UiView(surface, CameraWidget.BuildConfiguration(surface));

		Assert.Multiple(() =>
		{
			Assert.That(ContainsId(view.Tree.Root, "layout"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "camera2"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "camera3"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "camera4"), Is.True);
		});
	}

	[Test]
	public void The_status_overlay_is_drawn_when_enabled()
	{
		var surface = Surface(new { camera = "camera-1", overlay = true });

		var view = CameraWidget.BuildView(surface, ProviderId, _ => null);
		using var ui = new UiView(surface, view.Root);

		Assert.That(view.Overlays, Is.Not.Empty);
		Assert.That(ContainsId(ui.Tree.Root, "status-text"), Is.True);
	}

	[Test]
	public void Every_configured_grid_cell_gets_its_own_overlay()
	{
		var surface = Surface(new
		{
			camera = "camera-1",
			camera2 = "camera-2",
			camera3 = "camera-3",
			camera4 = "camera-4",
			layout = CameraWidget.LayoutGrid,
			overlay = true
		});

		var view = CameraWidget.BuildView(surface, ProviderId, _ => null);
		using var ui = new UiView(surface, view.Root);

		Assert.That(view.Overlays, Has.Count.EqualTo(4));
		Assert.That(ContainsId(ui.Tree.Root, "cell-0-status-text"), Is.True);
		Assert.That(ContainsId(ui.Tree.Root, "cell-3-status-text"), Is.True);
	}

	[Test]
	public void The_status_overlay_is_off_by_default()
	{
		var surface = Surface(new { camera = "camera-1" });

		var view = CameraWidget.BuildView(surface, ProviderId, _ => null);
		using var ui = new UiView(surface, view.Root);

		Assert.That(view.Overlays, Is.Empty);
		Assert.That(ContainsId(ui.Tree.Root, "status-text"), Is.False);
	}

	[Test]
	public void The_overlay_says_reconnecting_after_a_failure()
	{
		var (text, color) = CameraOverlay.Describe(
			VideoStreamState.Disconnected,
			true,
			DateTimeOffset.UtcNow.AddSeconds(-30),
			DateTimeOffset.UtcNow);

		Assert.That(text.Key.Name, Is.EqualTo("Widgets.Camera.Overlay.Reconnecting"));
		Assert.That(color, Is.EqualTo(CameraOverlay.PendingColor));
	}

	[Test]
	public void A_dropped_stream_that_has_shown_a_frame_is_reconnecting()
	{
		var (text, color) = CameraOverlay.Describe(
			VideoStreamState.Disconnected,
			false,
			DateTimeOffset.UtcNow.AddSeconds(-30),
			DateTimeOffset.UtcNow);

		Assert.That(text.Key.Name, Is.EqualTo("Widgets.Camera.Overlay.Reconnecting"));
		Assert.That(color, Is.EqualTo(CameraOverlay.PendingColor));
	}

	[Test]
	public void The_overlay_says_connecting_before_a_frame()
	{
		var (text, color) = CameraOverlay.Describe(
			VideoStreamState.Connected,
			false,
			null,
			DateTimeOffset.UtcNow);

		Assert.That(text.Key.Name, Is.EqualTo("Widgets.Camera.Overlay.Connecting"));
		Assert.That(color, Is.EqualTo(CameraOverlay.PendingColor));
	}

	[Test]
	public void The_overlay_reports_the_age_of_the_last_frame()
	{
		var now = DateTimeOffset.UtcNow;
		var (text, color) = CameraOverlay.Describe(
			VideoStreamState.Connected,
			false,
			now.AddSeconds(-12),
			now);

		Assert.That(text.Key.Name, Is.EqualTo("Widgets.Camera.Overlay.LiveSeconds"));
		Assert.That(text.Arguments.ContainsKey("seconds"), Is.True);
		Assert.That(color, Is.EqualTo(CameraOverlay.LiveColor));
	}

	[Test]
	public void The_type_offers_flows_and_a_default_press()
	{
		var descriptor = CameraWidget.Descriptor();

		Assert.That(descriptor.SupportsFlows, Is.True);
		Assert.That(descriptor.DefaultShortPressAction?.ActionId, Is.EqualTo(ShowCameraAction.ActionId));
	}

	[Test]
	public void The_modal_draws_the_stream_full_size()
	{
		var surface = new UiSurface
		{
			Kind = UiSurfaceKinds.Dialog,
			SessionMode = UiSessionModes.Exclusive,
			Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["viewId"] = JsonSerializer.SerializeToElement(ShowCameraAction.ModalViewId),
				["data"] = JsonSerializer.SerializeToElement(new { camera = "camera-1" })
			}
		};

		using var view = new UiView(surface, CameraWidget.BuildModal(surface, ProviderId));

		Assert.That(view.Tree.Root.Type, Is.EqualTo("ui.stack"));
	}

	[Test]
	public void The_movement_arrows_appear_when_they_are_enabled()
	{
		var surface = Surface(new { camera = "camera-1", controls = true, controlsOpacity = 40 });

		var view = CameraWidget.BuildView(
			surface,
			ProviderId,
			_ => Stream(),
			(_, _, _) => Task.CompletedTask);
		using var ui = new UiView(surface, view.Root);

		Assert.Multiple(() =>
		{
			Assert.That(ContainsId(ui.Tree.Root, "arrows-left"), Is.True);
			Assert.That(ContainsId(ui.Tree.Root, "arrows-up"), Is.True);
			Assert.That(ContainsId(ui.Tree.Root, "arrows-down"), Is.True);
			Assert.That(ContainsId(ui.Tree.Root, "arrows-right"), Is.True);
		});
	}

	[Test]
	public void The_movement_arrows_stay_off_by_default()
	{
		var surface = Surface(new { camera = "camera-1" });

		var view = CameraWidget.BuildView(
			surface,
			ProviderId,
			_ => Stream(),
			(_, _, _) => Task.CompletedTask);
		using var ui = new UiView(surface, view.Root);

		Assert.That(ContainsId(ui.Tree.Root, "arrows-left"), Is.False);
	}

	[Test]
	public void Every_grid_cell_gets_its_own_arrows()
	{
		var surface = Surface(new
		{
			camera = "camera-1",
			camera2 = "camera-2",
			camera3 = "camera-3",
			camera4 = "camera-4",
			layout = CameraWidget.LayoutGrid,
			controls = true
		});

		var view = CameraWidget.BuildView(
			surface,
			ProviderId,
			_ => Stream(),
			(_, _, _) => Task.CompletedTask);
		using var ui = new UiView(surface, view.Root);

		Assert.Multiple(() =>
		{
			Assert.That(ContainsId(ui.Tree.Root, "cell-0-arrows-left"), Is.True);
			Assert.That(ContainsId(ui.Tree.Root, "cell-3-arrows-right"), Is.True);
		});
	}

	[Test]
	public void The_widget_settings_offer_arrows_and_their_transparency()
	{
		var surface = new UiSurface
		{
			Kind = UiSurfaceKinds.Config,
			SessionMode = UiSessionModes.Exclusive,
			Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["entryPoint"] = JsonSerializer.SerializeToElement("widget-config"),
				["widgetData"] = JsonSerializer.SerializeToElement(new { camera = "camera-1" })
			}
		};

		using var view = new UiView(surface, CameraWidget.BuildConfiguration(surface));

		Assert.Multiple(() =>
		{
			Assert.That(ContainsId(view.Tree.Root, "controls"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "controlsOpacity"), Is.True);
		});
	}

	private static bool Contains(UiNode node, string type)
	{
		return node.Type == type || node.Children.Any(child => Contains(child, type));
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

	private static UiNode? FindById(UiNode node, string id)
	{
		if (node.Id.EndsWith(id, StringComparison.Ordinal))
		{
			return node;
		}

		foreach (var child in node.Children)
		{
			if (FindById(child, id) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private static StubStream Stream()
	{
		return new StubStream(new CameraDefinition(
			"camera-1",
			"Test camera",
			CameraSourceKind.Mjpeg,
			new Uri("http://127.0.0.1/camera.mjpeg"),
			null,
			null,
			5,
			640,
			"tcp"));
	}

	private sealed class StubStream(CameraDefinition definition) : IFrameStream
	{
		public CameraDefinition Definition => definition;

		public VideoStreamState State => VideoStreamState.Connected;

		public bool HasFailed => false;

		public DateTimeOffset? LastFrameAt => DateTimeOffset.UtcNow;

		public int? Width => 640;

		public int? Height => 360;

		public byte[]? CurrentFrame => null;

		public event EventHandler? StateChanged
		{
			add { }
			remove { }
		}

		public FrameSubscription Subscribe()
		{
			return new FrameSubscription(
				System.Threading.Channels.Channel.CreateBounded<byte[]>(1).Reader,
				() => { });
		}

		public Task RestartAsync()
		{
			return Task.CompletedTask;
		}

		public Task<bool> PrimeAsync(TimeSpan timeout, CancellationToken cancellationToken)
		{
			return Task.FromResult(true);
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}

	private static UiSurface Surface(object data)	{
		return new UiSurface
		{
			Kind = UiSurfaceKinds.Widget,
			SessionMode = UiSessionModes.Shared,
			Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["data"] = JsonSerializer.SerializeToElement(data)
			}
		};
	}
}
