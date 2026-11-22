using MacroDeck.Plugin.Testing;
using MacroDeck.Sdk;
using MacroDeck.Sdk.VideoStreams;
using MacroDeck.Sdk.Widgets;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace IpCamera.Tests;

/// <summary>
/// Behaviour tests through <see cref="PluginTestHarness"/>: the plugin's own capability handlers run,
/// but nothing crosses a socket.
/// </summary>
[TestFixture]
public sealed class PluginIntegrationTests
{
	private static PluginTestHarness CreateHarness()
	{
		return PluginTestHarness.Create(builder => Composition.Register(
			builder.UseLocalization(Strings.LocalizationCatalog)));
	}

	private static async Task<(PluginTestHarness Harness, PluginIntegration Integration)> CreateInitializedAsync()
	{
		var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		// The harness runs only the integration lifecycle, so the provider hooks are driven the way the
		// host's own lifecycle service drives them.
		var integration = harness.Services.GetServices<IPluginIntegration>().OfType<PluginIntegration>().Single();
		await ((IVideoStreamIntegration)integration).InitializeAsync(harness.Context.VideoStreams);
		await ((IWidgetTypeProvider)integration).InitializeAsync(harness.Context.WidgetTypes);

		return (harness, integration);
	}

	[Test]
	public async Task The_plugin_builds_and_initializes()
	{
		await using var harness = CreateHarness();

		Assert.DoesNotThrowAsync(harness.InitializeIntegrationsAsync);
	}

	[Test]
	public async Task The_camera_provider_is_registered_with_no_cameras_configured()
	{
		var (harness, _) = await CreateInitializedAsync();
		await using var _harness = harness;

		Assert.That(harness.Context.VideoStreams.Providers.Keys, Does.Contain("cameras"));
	}

	[Test]
	public async Task The_camera_widget_type_is_registered()
	{
		var (harness, _) = await CreateInitializedAsync();
		await using var _harness = harness;

		Assert.That(harness.Context.WidgetTypes.WidgetTypes.Keys, Does.Contain("camera"));
	}
}

/// <summary>
/// The localization set is generated from <c>Localization/*.resx</c>, so these guard the wiring rather
/// than any wording: a missing catalog registration leaves every label showing its raw key.
/// </summary>
[TestFixture]
public sealed class LocalizationTests
{
	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id()
	{
		Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.itaim.ip-camera"));
	}

	[Test]
	public void English_is_the_default_culture()
	{
		Assert.That(Strings.LocalizationCatalog.DefaultCulture, Is.EqualTo("en"));
		Assert.That(Strings.LocalizationCatalog.Cultures, Does.Contain("en"));
	}

	[Test]
	public void The_camera_strings_come_from_the_catalog()
	{
		Assert.That(Strings.LocalizationCatalog.KeysOf("en"), Does.Contain("Widgets.Camera.Name"));
	}

	[Test]
	public void Every_key_the_default_culture_declares_resolves_to_text()
	{
		foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
		{
			Assert.That(Strings.LocalizationCatalog.TryGetTemplate("en", key, out var text), Is.True);
			Assert.That(text, Is.Not.Empty);
		}
	}
}
