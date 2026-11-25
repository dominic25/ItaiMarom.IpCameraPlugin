using System.Text.Json;
using IpCamera.Cameras;
using IpCamera.Onvif;
using IpCamera.Widgets;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Widgets;
using Serilog;

namespace IpCamera.Actions;

/// <summary>ONVIF pan, tilt, stop and IR actions that act on the pressed camera widget.</summary>
internal sealed class CameraControlAction : IActionDefinition
{
	private readonly string _id;
	private readonly LocalizedText _name;
	private readonly LocalizedText _description;
	private readonly Func<IWidgetApi?> _widgets;
	private readonly CameraRegistry _registry;
	private readonly IOnvifClient _onvif;
	private readonly ILogger _logger;
	private readonly Func<OnvifTarget, CancellationToken, Task<bool>> _operation;

	private CameraControlAction(
		string id,
		LocalizedText name,
		LocalizedText description,
		Func<IWidgetApi?> widgets,
		CameraRegistry registry,
		IOnvifClient onvif,
		ILogger logger,
		Func<OnvifTarget, CancellationToken, Task<bool>> operation)
	{
		_id = id;
		_name = name;
		_description = description;
		_widgets = widgets;
		_registry = registry;
		_onvif = onvif;
		_logger = logger;
		_operation = operation;
	}

	public string Id => _id;

	public LocalizedText Name => _name;

	public LocalizedText Description => _description;

	public IReadOnlyList<ActionParameter> Parameters => [];

	public IActionExecutor CreateExecutor()
	{
		return new Executor(this);
	}

	public static IReadOnlyList<IActionDefinition> Create(
		Func<IWidgetApi?> widgets,
		CameraRegistry registry,
		IOnvifClient onvif,
		ILogger logger)
	{
		static CameraControlAction Action(
			string id,
			LocalizedText name,
			LocalizedText description,
			Func<IWidgetApi?> widgets,
			CameraRegistry registry,
			IOnvifClient onvif,
			ILogger logger,
			Func<OnvifTarget, CancellationToken, Task<bool>> operation)
		{
			return new CameraControlAction(id, name, description, widgets, registry, onvif, logger, operation);
		}

		return
		[
			Action("move-left", Strings.Actions.Control.MoveLeft.Name(),
				Strings.Actions.Control.MoveLeft.Description(), widgets, registry, onvif, logger,
				(target, cancellationToken) => onvif.MoveAsync(target.Device, target.Camera.Username,
					target.Camera.Password, "left", 0.5, cancellationToken)),
			Action("move-right", Strings.Actions.Control.MoveRight.Name(),
				Strings.Actions.Control.MoveRight.Description(), widgets, registry, onvif, logger,
				(target, cancellationToken) => onvif.MoveAsync(target.Device, target.Camera.Username,
					target.Camera.Password, "right", 0.5, cancellationToken)),
			Action("move-up", Strings.Actions.Control.MoveUp.Name(),
				Strings.Actions.Control.MoveUp.Description(), widgets, registry, onvif, logger,
				(target, cancellationToken) => onvif.MoveAsync(target.Device, target.Camera.Username,
					target.Camera.Password, "up", 0.5, cancellationToken)),
			Action("move-down", Strings.Actions.Control.MoveDown.Name(),
				Strings.Actions.Control.MoveDown.Description(), widgets, registry, onvif, logger,
				(target, cancellationToken) => onvif.MoveAsync(target.Device, target.Camera.Username,
					target.Camera.Password, "down", 0.5, cancellationToken)),
			Action("ptz-stop", Strings.Actions.Control.Stop.Name(),
				Strings.Actions.Control.Stop.Description(), widgets, registry, onvif, logger,
				(target, cancellationToken) => onvif.StopAsync(target.Device, target.Camera.Username,
					target.Camera.Password, cancellationToken)),
			Action("ir-auto", Strings.Actions.Control.IrAuto.Name(),
				Strings.Actions.Control.IrAuto.Description(), widgets, registry, onvif, logger,
				(target, cancellationToken) => onvif.SetIrAsync(target.Device, target.Camera.Username,
					target.Camera.Password, "AUTO", cancellationToken)),
			Action("ir-on", Strings.Actions.Control.IrOn.Name(),
				Strings.Actions.Control.IrOn.Description(), widgets, registry, onvif, logger,
				(target, cancellationToken) => onvif.SetIrAsync(target.Device, target.Camera.Username,
					target.Camera.Password, "ON", cancellationToken)),
			Action("ir-off", Strings.Actions.Control.IrOff.Name(),
				Strings.Actions.Control.IrOff.Description(), widgets, registry, onvif, logger,
				(target, cancellationToken) => onvif.SetIrAsync(target.Device, target.Camera.Username,
					target.Camera.Password, "OFF", cancellationToken))
		];
	}

	private sealed class Executor(CameraControlAction action) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (string.IsNullOrEmpty(context.OwnerWidgetId) || action._widgets() is not { } widgetApi)
			{
				return ActionResult.Failed(
					ActionErrorCodes.NotFound,
					Strings.Actions.Control.NoWidget());
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
				action._logger.Debug("Could not read the camera widget: {Reason}", exception.Message);
				return ActionResult.Failed(
					ActionErrorCodes.ProviderError,
					Strings.Actions.Control.ReadFailed());
			}

			var cameraId = ReadCameraId(snapshot?.Data);
			if (cameraId is null || action._registry.Find(cameraId) is not { } stream)
			{
				return ActionResult.Failed(
					ActionErrorCodes.NotConfigured,
					Strings.Actions.Control.NotConfigured());
			}

			var camera = stream.Definition;
			var device = new OnvifDevice(
				$"http://{camera.Url.Host}/onvif/device_service",
				camera.Name,
				null);

			var succeeded = await action._operation(new OnvifTarget(camera, device), context.CancellationToken)
				.ConfigureAwait(false);

			return succeeded
				? ActionResult.Success()
				: ActionResult.Failed(
					ActionErrorCodes.Unavailable,
					Strings.Actions.Control.Failed());
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

	internal sealed record OnvifTarget(CameraDefinition Camera, OnvifDevice Device);
}
