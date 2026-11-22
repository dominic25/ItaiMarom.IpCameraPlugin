using System.Text.Json;
using IpCamera.Actions;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using NUnit.Framework;
using Serilog;

namespace IpCamera.Tests;

[TestFixture]
public sealed class ShowCameraActionTests
{
	private static readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	[Test]
	public async Task The_pressed_widget_camera_opens_in_a_modal()
	{
		using var data = JsonDocument.Parse("""{"camera":"camera-1"}""");
		var widgets = new StubWidgetApi(new WidgetDataSnapshot("widget-1", "com.itaim.ip-camera::camera", data.RootElement.Clone()));
		var ui = new StubInteractions();
		var executor = new ShowCameraAction(() => widgets, _logger).CreateExecutor();

		var result = await executor.ExecuteAsync(Context(ui));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
		Assert.That(ui.Modal, Is.Not.Null);
		Assert.That(ui.Modal!.ViewId, Is.EqualTo(ShowCameraAction.ModalViewId));
		Assert.That(ui.Modal.Data!["camera"].GetString(), Is.EqualTo("camera-1"));
	}

	[Test]
	public async Task A_widget_without_a_camera_is_reported_as_not_configured()
	{
		using var data = JsonDocument.Parse("""{"camera":""}""");
		var widgets = new StubWidgetApi(new WidgetDataSnapshot("widget-1", "com.itaim.ip-camera::camera", data.RootElement.Clone()));
		var executor = new ShowCameraAction(() => widgets, _logger).CreateExecutor();

		var result = await executor.ExecuteAsync(Context(new StubInteractions()));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Failed));
		Assert.That(result.ErrorCode, Is.EqualTo(ActionErrorCodes.NotConfigured));
	}

	[Test]
	public async Task A_run_without_a_widget_fails()
	{
		var executor = new ShowCameraAction(() => null, _logger).CreateExecutor();

		var result = await executor.ExecuteAsync(Context(new StubInteractions(), withOwnerWidget: false));

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Failed));
		Assert.That(result.ErrorCode, Is.EqualTo(ActionErrorCodes.NotFound));
	}

	[OneTimeTearDown]
	public void DisposeLogger()
	{
		(_logger as IDisposable)?.Dispose();
	}

	private static ActionExecutionContext Context(StubInteractions ui, bool withOwnerWidget = true)
	{
		return new ActionExecutionContext
		{
			Parameters = new Dictionary<string, object>(),
			OriginClientId = "client-1",
			OwnerWidgetId = withOwnerWidget ? "widget-1" : null,
			Ui = ui
		};
	}

	private sealed class StubWidgetApi(WidgetDataSnapshot? snapshot) : IWidgetApi
	{
		public IReadOnlyList<WidgetTargetInfo> GetWidgets()
		{
			return [];
		}

		public bool Exists(string widgetId)
		{
			return true;
		}

		public Task<bool> ApplyAsync(WidgetAppearanceRequest request, CancellationToken cancellationToken = default)
		{
			return Task.FromResult(false);
		}

		public Task<WidgetDataSnapshot?> GetWidgetDataAsync(
			string widgetId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(snapshot);
		}
	}

	private sealed class StubInteractions : IUiInteractions
	{
		public ModalDefinition? Modal { get; private set; }

		public Task<bool> ShowModalAsync(
			string? originClientId,
			ModalDefinition modal,
			CancellationToken cancellationToken = default)
		{
			Modal = modal;
			return Task.FromResult(true);
		}

		public Task<ModalResult<T>> ShowModalAsync<T>(
			string? originClientId,
			ModalDefinition modal,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(ModalResult.FromCancellation<T>());
		}
	}
}
