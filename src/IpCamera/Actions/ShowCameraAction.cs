using System.Text.Json;
using IpCamera.Cameras;
using IpCamera.Widgets;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using Serilog;

namespace IpCamera.Actions;

/// <summary>
/// Opens a camera in a full-screen modal. The camera comes from the action's own parameter when it has
/// one, so a plain button can open a chosen camera; without it the pressed camera widget decides.
/// </summary>
internal sealed class ShowCameraAction : IActionDefinition, IDynamicOptionsActionDefinition
{
	public const string ActionId = "show-camera";

	public const string ModalViewId = "camera";

	private const string CameraParameter = "camera";

	private static readonly IReadOnlyList<ActionParameter> _parameters =
	[
		ActionParameter.DynamicChoice(
			CameraParameter,
			label: Strings.Actions.Control.Camera(),
			description: Strings.Actions.ShowCamera.CameraDescription(),
			placeholder: Strings.Actions.Control.PressedWidget(),
			required: false)
	];

	private readonly Func<IWidgetApi?> _widgets;
	private readonly CameraRegistry _registry;
	private readonly ILogger _logger;

	public ShowCameraAction(Func<IWidgetApi?> widgets, CameraRegistry registry, ILogger logger)
	{
		_widgets = widgets;
		_registry = registry;
		_logger = logger.ForContext<ShowCameraAction>();
	}

	public string Id => ActionId;

	public LocalizedText Name => Strings.Actions.ShowCamera.Name();

	public LocalizedText Description => Strings.Actions.ShowCamera.Description();

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

	private sealed class Executor(ShowCameraAction action) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Ui is null || string.IsNullOrEmpty(context.OriginClientId))
			{
				return ActionResult.Failed(
					ActionErrorCodes.Unavailable,
					Strings.Actions.ShowCamera.NoClient());
			}

			var selected = ReadParameter(context, CameraParameter);
			string? cameraId;

			if (!string.IsNullOrEmpty(selected))
			{
				if (action._registry.Find(selected)?.Definition is not { } chosen)
				{
					return ActionResult.Failed(
						ActionErrorCodes.NotConfigured,
						Strings.Actions.Control.CameraMissing());
				}

				cameraId = chosen.Id;
			}
			else
			{
				if (string.IsNullOrEmpty(context.OwnerWidgetId) || action._widgets() is not { } widgetApi)
				{
					return ActionResult.Failed(
						ActionErrorCodes.NotFound,
						Strings.Actions.ShowCamera.NoWidget());
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
						Strings.Actions.ShowCamera.ReadFailed());
				}

				cameraId = ReadCameraId(snapshot?.Data);
			}

			if (cameraId is null)
			{
				return ActionResult.Failed(
					ActionErrorCodes.NotConfigured,
					Strings.Actions.ShowCamera.NotConfigured());
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

		private static string? ReadParameter(ActionExecutionContext context, string name)
		{
			return context.Parameters.TryGetValue(name, out var value) && value is string text &&
				text.Length > 0
					? text
					: null;
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
