using System.Text.Json;
using IpCamera.Actions;
using IpCamera.Widgets;
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
	public void A_filled_widget_with_an_offset_draws_a_transform()
	{
		var surface = Surface(new { camera = "camera-1", fit = "cover", zoom = 1.4, offsetX = 0.2, offsetY = -0.1 });

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(Contains(view.Tree.Root, "ui.transform"), Is.True);
	}

	[Test]
	public void A_label_is_drawn_over_the_picture()
	{
		var surface = Surface(new { camera = "camera-1", label = "Front door" });

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(Contains(view.Tree.Root, "ui.text"), Is.True);
	}

	[Test]
	public void A_fit_widget_ignores_the_crop_settings()
	{
		var surface = Surface(new { camera = "camera-1", fit = "contain", zoom = 2, offsetX = 0.5 });

		using var view = new UiView(surface, CameraWidget.BuildView(surface, ProviderId));

		Assert.That(Contains(view.Tree.Root, "ui.transform"), Is.False);
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

	private static bool Contains(UiNode node, string type)
	{
		return node.Type == type || node.Children.Any(child => Contains(child, type));
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
