using System.Text.Json;
using IpCamera.Cameras;
using IpCamera.Onvif;
using IpCamera.Widgets;
using IpCamera.Xiaomi;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Widgets;
using Serilog;

namespace IpCamera.Actions;

/// <summary>
/// Pan, tilt, stop and night vision for one camera. The camera comes from the action's own parameter
/// when it has one, so the action works on a plain button; without it the pressed camera widget decides,
/// which is how every earlier version behaved.
/// </summary>
internal sealed class CameraControlAction : IActionDefinition, IDynamicOptionsActionDefinition
{
	private const string CameraParameter = "camera";

	private static readonly IReadOnlyList<ActionParameter> _parameters =
	[
		ActionParameter.DynamicChoice(
			CameraParameter,
			label: Strings.Actions.Control.Camera(),
			description: Strings.Actions.Control.CameraDescription(),
			placeholder: Strings.Actions.Control.PressedWidget(),
			required: false)
	];

	private readonly string _id;
	private readonly LocalizedText _name;
	private readonly LocalizedText _description;
	private readonly Func<IWidgetApi?> _widgets;
	private readonly CameraRegistry _registry;
	private readonly IOnvifClient _onvif;
	private readonly IMiioClient _miio;
	private readonly ILogger _logger;
	private readonly CameraControlOperation _operation;

	private CameraControlAction(
		string id,
		LocalizedText name,
		LocalizedText description,
		Func<IWidgetApi?> widgets,
		CameraRegistry registry,
		IOnvifClient onvif,
		IMiioClient miio,
		ILogger logger,
		CameraControlOperation operation)
	{
		_id = id;
		_name = name;
		_description = description;
		_widgets = widgets;
		_registry = registry;
		_onvif = onvif;
		_miio = miio;
		_logger = logger;
		_operation = operation;
	}

	public string Id => _id;

	public LocalizedText Name => _name;

	public LocalizedText Description => _description;

	public IReadOnlyList<ActionParameter> Parameters => _parameters;

	public IActionExecutor CreateExecutor()
	{
		return new Executor(this);
	}

	public Task<DynamicOptionsResult> GetDynamicOptionsAsync(
		DynamicOptionsContext context,
		CancellationToken cancellationToken)
	{
		if (context.ParameterName != CameraParameter)
		{
			return Task.FromResult(new DynamicOptionsResult { Options = [] });
		}

		var options = new List<ActionParameterOption>
		{
			new()
			{
				Value = string.Empty,
				Label = Strings.Actions.Control.PressedWidget()
			}
		};

		options.AddRange(_registry.Cameras
			.OrderBy(camera => camera.Name, StringComparer.OrdinalIgnoreCase)
			.Select(camera => new ActionParameterOption
			{
				Value = camera.Id,
				Label = LocalizedText.FromLiteral(camera.Name)
			}));

		return Task.FromResult(new DynamicOptionsResult
		{
			Options = options,
			CacheSeconds = 10
		});
	}

	public static IReadOnlyList<IActionDefinition> Create(
		Func<IWidgetApi?> widgets,
		CameraRegistry registry,
		IOnvifClient onvif,
		IMiioClient miio,
		ILogger logger)
	{
		static CameraControlAction Action(
			string id,
			LocalizedText name,
			LocalizedText description,
			CameraControlOperation operation,
			Func<IWidgetApi?> widgets,
			CameraRegistry registry,
			IOnvifClient onvif,
			IMiioClient miio,
			ILogger logger)
		{
			return new CameraControlAction(
				id,
				name,
				description,
				widgets,
				registry,
				onvif,
				miio,
				logger,
				operation);
		}

		return
		[
			Action("move-left", Strings.Actions.Control.MoveLeft.Name(),
				Strings.Actions.Control.MoveLeft.Description(), CameraControlOperation.MoveLeft,
				widgets, registry, onvif, miio, logger),
			Action("move-right", Strings.Actions.Control.MoveRight.Name(),
				Strings.Actions.Control.MoveRight.Description(), CameraControlOperation.MoveRight,
				widgets, registry, onvif, miio, logger),
			Action("move-up", Strings.Actions.Control.MoveUp.Name(),
				Strings.Actions.Control.MoveUp.Description(), CameraControlOperation.MoveUp,
				widgets, registry, onvif, miio, logger),
			Action("move-down", Strings.Actions.Control.MoveDown.Name(),
				Strings.Actions.Control.MoveDown.Description(), CameraControlOperation.MoveDown,
				widgets, registry, onvif, miio, logger),
			Action("ptz-stop", Strings.Actions.Control.Stop.Name(),
				Strings.Actions.Control.Stop.Description(), CameraControlOperation.Stop,
				widgets, registry, onvif, miio, logger),
			Action("ir-auto", Strings.Actions.Control.IrAuto.Name(),
				Strings.Actions.Control.IrAuto.Description(), CameraControlOperation.NightAuto,
				widgets, registry, onvif, miio, logger),
			Action("ir-on", Strings.Actions.Control.IrOn.Name(),
				Strings.Actions.Control.IrOn.Description(), CameraControlOperation.NightOn,
				widgets, registry, onvif, miio, logger),
			Action("ir-off", Strings.Actions.Control.IrOff.Name(),
				Strings.Actions.Control.IrOff.Description(), CameraControlOperation.NightOff,
				widgets, registry, onvif, miio, logger)
		];
	}

	private sealed class Executor(CameraControlAction action) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var camera = await ResolveCameraAsync(context).ConfigureAwait(false);
			if (camera.Error is { } error)
			{
				return error;
			}

			return await CameraControl.ExecuteAsync(
				camera.Definition!,
				action._operation,
				action._onvif,
				action._miio,
				action._logger,
				context.CancellationToken).ConfigureAwait(false);
		}

		private async Task<CameraSelection> ResolveCameraAsync(ActionExecutionContext context)
		{
			var selected = ReadParameter(context, CameraParameter);
			if (!string.IsNullOrEmpty(selected))
			{
				return action._registry.Find(selected)?.Definition is { } chosen
					? new CameraSelection(chosen, null)
					: new CameraSelection(
						null,
						ActionResult.Failed(
							ActionErrorCodes.NotConfigured,
							Strings.Actions.Control.CameraMissing()));
			}

			if (string.IsNullOrEmpty(context.OwnerWidgetId) || action._widgets() is not { } widgetApi)
			{
				return new CameraSelection(
					null,
					ActionResult.Failed(
						ActionErrorCodes.NotFound,
						Strings.Actions.Control.NoWidget()));
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
				return new CameraSelection(
					null,
					ActionResult.Failed(
						ActionErrorCodes.ProviderError,
						Strings.Actions.Control.ReadFailed()));
			}

			var cameraId = ReadCameraId(snapshot?.Data);
			return cameraId is not null && action._registry.Find(cameraId) is { } stream
				? new CameraSelection(stream.Definition, null)
				: new CameraSelection(
					null,
					ActionResult.Failed(
						ActionErrorCodes.NotConfigured,
						Strings.Actions.Control.NotConfigured()));
		}

		private static string? ReadParameter(ActionExecutionContext context, string name)
		{
			return context.Parameters.TryGetValue(name, out var value) && value is string text &&
				text.Length > 0
					? text
					: null;
		}

		private static string HostOf(string value)
		{
			var text = value.Trim();
			var scheme = text.IndexOf("://", StringComparison.Ordinal);
			if (scheme >= 0)
			{
				text = text[(scheme + 3)..];
			}

			var slash = text.IndexOf('/');
			return slash >= 0 ? text[..slash] : text;
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

	private sealed record CameraSelection(CameraDefinition? Definition, ActionResult? Error);
}

/// <summary>
/// Runs one control against one camera over ONVIF or miIO. Shared by the actions and by the arrows on
/// the widget, so both take the same path to the device.
/// </summary>
internal static class CameraControl
{
	public static async Task<ActionResult> ExecuteAsync(
		CameraDefinition camera,
		CameraControlOperation operation,
		IOnvifClient onvif,
		IMiioClient miio,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		return camera.Control switch
		{
			CameraControlKind.None => ActionResult.Failed(
				ActionErrorCodes.Unavailable,
				Strings.Actions.Control.Disabled()),
			CameraControlKind.Xiaomi => await ControlXiaomiAsync(
				camera,
				operation,
				miio,
				logger,
				cancellationToken).ConfigureAwait(false),
			_ => await ControlOnvifAsync(
				camera,
				operation,
				onvif,
				cancellationToken).ConfigureAwait(false)
		};
	}

	private static async Task<ActionResult> ControlXiaomiAsync(
		CameraDefinition camera,
		CameraControlOperation operation,
		IMiioClient miio,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrEmpty(camera.ControlHost) || string.IsNullOrEmpty(camera.ControlToken))
		{
			return ActionResult.Failed(
				ActionErrorCodes.NotConfigured,
				Strings.Actions.Control.XiaomiNotConfigured());
		}

		var endpoint = new MiioEndpoint(
			HostOf(camera.ControlHost),
			MiioEndpoint.DefaultPort,
			camera.ControlToken);

		// A motor command is a single step, so there is nothing left to stop.
		if (operation == CameraControlOperation.Stop)
		{
			return ActionResult.Success();
		}

		var result = operation switch
		{
			CameraControlOperation.MoveLeft => await miio.SetMotorAsync(endpoint, 1, cancellationToken)
				.ConfigureAwait(false),
			CameraControlOperation.MoveRight => await miio.SetMotorAsync(endpoint, 2, cancellationToken)
				.ConfigureAwait(false),
			CameraControlOperation.MoveUp => await miio.SetMotorAsync(endpoint, 3, cancellationToken)
				.ConfigureAwait(false),
			CameraControlOperation.MoveDown => await miio.SetMotorAsync(endpoint, 4, cancellationToken)
				.ConfigureAwait(false),
			CameraControlOperation.NightAuto => await miio.SetNightModeAsync(endpoint, 0, cancellationToken)
				.ConfigureAwait(false),
			CameraControlOperation.NightOff => await miio.SetNightModeAsync(endpoint, 1, cancellationToken)
				.ConfigureAwait(false),
			CameraControlOperation.NightOn => await miio.SetNightModeAsync(endpoint, 2, cancellationToken)
				.ConfigureAwait(false),
			_ => MiioResult.Failure("unsupported operation")
		};

		if (result.Succeeded)
		{
			return ActionResult.Success();
		}

		logger.Information(
			"Xiaomi control {Operation} on {Camera} failed: {Reason}",
			operation,
			camera.Name,
			result.Reason);

		return ActionResult.Failed(
			ActionErrorCodes.Unavailable,
			Strings.Actions.Control.XiaomiFailed());
	}

	private static async Task<ActionResult> ControlOnvifAsync(
		CameraDefinition camera,
		CameraControlOperation operation,
		IOnvifClient onvif,
		CancellationToken cancellationToken)
	{
		var host = string.IsNullOrEmpty(camera.ControlHost)
			? camera.Url.Host
			: HostOf(camera.ControlHost);

		var device = new OnvifDevice(
			$"http://{host}/onvif/device_service",
			camera.Name,
			null);

		var succeeded = operation switch
		{
			CameraControlOperation.MoveLeft => await onvif.MoveAsync(
				device, camera.Username, camera.Password, "left", 0.5, cancellationToken).ConfigureAwait(false),
			CameraControlOperation.MoveRight => await onvif.MoveAsync(
				device, camera.Username, camera.Password, "right", 0.5, cancellationToken).ConfigureAwait(false),
			CameraControlOperation.MoveUp => await onvif.MoveAsync(
				device, camera.Username, camera.Password, "up", 0.5, cancellationToken).ConfigureAwait(false),
			CameraControlOperation.MoveDown => await onvif.MoveAsync(
				device, camera.Username, camera.Password, "down", 0.5, cancellationToken).ConfigureAwait(false),
			CameraControlOperation.Stop => await onvif.StopAsync(
				device, camera.Username, camera.Password, cancellationToken).ConfigureAwait(false),
			CameraControlOperation.NightAuto => await onvif.SetIrAsync(
				device, camera.Username, camera.Password, "AUTO", cancellationToken).ConfigureAwait(false),
			CameraControlOperation.NightOn => await onvif.SetIrAsync(
				device, camera.Username, camera.Password, "ON", cancellationToken).ConfigureAwait(false),
			CameraControlOperation.NightOff => await onvif.SetIrAsync(
				device, camera.Username, camera.Password, "OFF", cancellationToken).ConfigureAwait(false),
			_ => false
		};

		return succeeded
			? ActionResult.Success()
			: ActionResult.Failed(
				ActionErrorCodes.Unavailable,
				Strings.Actions.Control.Failed());
	}

	private static string HostOf(string value)
	{
		var text = value.Trim();
		var scheme = text.IndexOf("://", StringComparison.Ordinal);
		if (scheme >= 0)
		{
			text = text[(scheme + 3)..];
		}

		var slash = text.IndexOf('/');
		return slash >= 0 ? text[..slash] : text;
	}
}

/// <summary>The control a user asked for, independent of the protocol that carries it.</summary>
internal enum CameraControlOperation
{
	MoveLeft,
	MoveRight,
	MoveUp,
	MoveDown,
	Stop,
	NightAuto,
	NightOn,
	NightOff
}
