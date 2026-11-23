using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class WidgetTypeMatchTests
{
	[TestCase("com.itaimarom.ip-camera::camera", true)]
	[TestCase("com.example.ip-camera::camera", true)]
	[TestCase("com.itaimarom.ip-camera::camera2", false)]
	[TestCase("com.itaimarom.ip-camera::snapshot", false)]
	[TestCase("camera", false)]
	[TestCase("", false)]
	[TestCase(null, false)]
	public void Only_a_qualified_camera_widget_is_accepted_before_registration(string? widgetType, bool expected)
	{
		Assert.That(PluginIntegration.IsCameraWidgetType(widgetType), Is.EqualTo(expected));
	}
}
