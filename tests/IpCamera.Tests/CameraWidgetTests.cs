using System.Text.Json;
using IpCamera.Widgets;
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

		Assert.That(view.Tree.Root.Type, Is.EqualTo("macrodeck.video-stream"));
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

	private static UiSurface Surface(object data)
	{
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
