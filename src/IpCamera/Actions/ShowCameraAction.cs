using System.Text.Json;
using IpCamera.Widgets;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using Serilog;

namespace IpCamera.Actions;

/// <summary>
/// Opens the camera of the pressed widget in a full-screen modal. The widget type names this action as its
/// default short press, so a tile without a flow of its own still has a useful primary interaction.
/// </summary>
internal sealed class ShowCameraAction(
	Func<IWidgetApi?> widgets,
	ILogger logger) : IActionDefinition
{
	public const string ActionId = "show-camera";

	public const string ModalViewId = "camera";

	public string Id => ActionId;

	public MacroDeck.Localization.LocalizedText Name => Strings.Actions.ShowCamera.Name();

	public MacroDeck.Localization.LocalizedText Description => Strings.Actions.ShowCamera.Description();

	public IReadOnlyList<ActionParameter> Parameters => [];

	public IActionExecutor CreateExecutor()
	{
		return new Executor(widgets, logger.ForContext<ShowCameraAction>());
	}

	private sealed class Executor(Func<IWidgetApi?> widgets, ILogger logger) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Ui is null || string.IsNullOrEmpty(context.OriginClientId))
			{
				return ActionResult.Failed(ActionErrorCodes.Unavailable, Strings.Actions.ShowCamera.NoClient());
			}

			if (string.IsNullOrEmpty(context.OwnerWidgetId) || widgets() is not { } widgetApi)
			{
				return ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Actions.ShowCamera.NoWidget());
			}

			WidgetDataSnapshot? snapshot;
			try
			{
				snapshot = await widgetApi
					.GetWidgetDataAsync(context.OwnerWidgetId, context.CancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				logger.Debug("Could not read the camera widget: {Reason}", exception.Message);
				return ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Actions.ShowCamera.ReadFailed());
			}

			var cameraId = ReadCameraId(snapshot?.Data);
			if (cameraId is null)
			{
				return ActionResult.Failed(ActionErrorCodes.NotConfigured, Strings.Actions.ShowCamera.NotConfigured());
			}

			var modal = new ModalDefinition
			{
				ViewId = ModalViewId,
				Title = Strings.Actions.ShowCamera.ModalTitle(),
				Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
				{
					[CameraWidget.CameraKey] = JsonSerializer.SerializeToElement(cameraId)
				}
			};

			var opened = await context.Ui
				.ShowModalAsync(context.OriginClientId, modal, context.CancellationToken)
				.ConfigureAwait(false);

			return opened
				? ActionResult.Success()
				: ActionResult.Failed(ActionErrorCodes.Unavailable, Strings.Actions.ShowCamera.OpenFailed());
		}

		private static string? ReadCameraId(JsonElement? data)
		{
			if (data is not { ValueKind: JsonValueKind.Object } value ||
				!value.TryGetProperty(CameraWidget.CameraKey, out var camera) ||
				camera.ValueKind != JsonValueKind.String)
			{
				return null;
			}

			return camera.GetString() is { Length: > 0 } id ? id : null;
		}
	}
}
