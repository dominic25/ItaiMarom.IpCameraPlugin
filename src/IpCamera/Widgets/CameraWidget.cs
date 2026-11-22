using System.Text.Json;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Model.References;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Widgets;

namespace IpCamera.Widgets;

/// <summary>The widget type the plugin offers, its picker sample, its live view and its editor.</summary>
internal static class CameraWidget
{
	public const string TypeId = "camera";

	public const string CameraKey = "camera";
	public const string FitKey = "fit";

	public const string FitContain = "contain";
	public const string FitCover = "cover";

	private const string DataSchema = """
		{
		  "type": "object",
		  "properties": {
		    "camera": { "type": "string" },
		    "fit": { "type": "string" },
		    "border": { "type": "object" }
		  }
		}
		""";

	public static WidgetTypeDescriptor Descriptor()
	{
		return new WidgetTypeDescriptor(
			TypeId,
			Strings.Widgets.Camera.Name(),
			Strings.Widgets.Camera.Description(),
			DefaultData: $$"""{"{{CameraKey}}":"","{{FitKey}}":"{{FitCover}}"}""",
			DataSchema: DataSchema,
			HasConfiguration: true);
	}

	public static UiElement BuildView(UiSurface surface, string? providerId)
	{
		var data = UiWidgetSurface.Data(surface);
		var cameraId = ReadString(data, CameraKey);

		if (UiWidgetSurface.IsSampleRequested(surface) ||
			string.IsNullOrEmpty(providerId) ||
			string.IsNullOrEmpty(cameraId))
		{
			return Placeholder(Strings.Widgets.Camera.NotConfigured());
		}

		var fit = ReadString(data, FitKey) == FitContain ? FitContain : FitCover;

		return new UiVideoStream
		{
			Key = "stream",
			Stream = UiValue.Of(new UiVideoStreamReference { Provider = providerId, Id = cameraId }),
			Fit = UiValue.Of(fit),
			Fill = true,
			Fallback = Placeholder(Strings.Widgets.Camera.UpdateRequired())
		};
	}

	public static UiElement BuildConfiguration(UiSurface surface)
	{
		var data = UiWidgetSurface.Data(surface);
		var camera = new UiState<string>(ReadString(data, CameraKey) ?? string.Empty);
		var fit = new UiState<string>(ReadString(data, FitKey) == FitContain ? FitContain : FitCover);

		return new UiWidgetConfiguration
		{
			Key = "root",
			Properties = new UiWidgetProperties
			{
				Key = "properties",
				Children =
				[
					new UiIntegrationPickerInput
					{
						Key = CameraKey,
						Label = Strings.Widgets.Camera.Config.Camera(),
						Description = Strings.Widgets.Camera.Config.CameraDescription(),
						Capability = UiValue.Of("video-stream-provider"),
						ConfigurationEntries = UiValue.Of(true),
						Binding = Bind.To(camera)
					},
					new UiChoiceInput
					{
						Key = FitKey,
						Label = Strings.Widgets.Camera.Config.Fit(),
						Segmented = UiValue.Of(true),
						Options = UiValue.Of<IReadOnlyList<UiOption>>(
						[
							new UiOption { Value = FitCover, Label = Strings.Widgets.Camera.Config.FitCover() },
							new UiOption { Value = FitContain, Label = Strings.Widgets.Camera.Config.FitContain() }
						]),
						Binding = Bind.To(fit)
					}
				]
			}
		};
	}

	private static UiStack Placeholder(MacroDeck.Localization.LocalizedText hint)
	{
		return new UiStack
		{
			Key = "placeholder",
			Justify = UiValue.Of(UiComponentJustify.Center),
			Align = UiValue.Of(UiComponentAlignments.Center),
			Padding = 0.06,
			Children =
			[
				new UiTextRun
				{
					Key = "hint",
					Text = hint,
					Size = 0.10,
					MinSize = 0.07,
					Role = UiValue.Of(UiComponentTextRoles.Secondary),
					Align = UiValue.Of(UiComponentAlignments.Center),
					Wrap = UiValue.Of(true),
					MaxLines = UiValue.Of(2)
				}
			]
		};
	}

	private static string? ReadString(JsonElement data, string key)
	{
		return data.ValueKind == JsonValueKind.Object &&
			data.TryGetProperty(key, out var value) &&
			value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;
	}
}

/// <summary>
/// Adapts a <see cref="UiView" /> to the session contract the host drives. The view owns the tree and the
/// patch stream; this class only forwards them and releases the view when the host ends the session.
/// </summary>
internal sealed class ViewSession : IUiSession
{
	private readonly UiView _view;

	public ViewSession(UiView view)
	{
		_view = view;
		_view.Changed += OnChanged;
		_view.HandlerFaulted += OnHandlerFaulted;
	}

	public event EventHandler? Changed;

	public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

	public UiTree BuildTree()
	{
		return _view.Tree;
	}

	public IReadOnlyList<UiPatch> DrainPatches()
	{
		return _view.DrainPatches();
	}

	public void Dispatch(UiEvent uiEvent)
	{
		_view.Dispatch(uiEvent);
	}

	public void Dispatch(UiEvent uiEvent, IUiEventOrigin origin)
	{
		_ = origin;
		_view.Dispatch(uiEvent);
	}

	public ValueTask DisposeAsync()
	{
		_view.Changed -= OnChanged;
		_view.HandlerFaulted -= OnHandlerFaulted;
		_view.Dispose();
		return ValueTask.CompletedTask;
	}

	private void OnChanged(object? sender, EventArgs e)
	{
		Changed?.Invoke(this, EventArgs.Empty);
	}

	private void OnHandlerFaulted(object? sender, UiHandlerFaultEventArgs e)
	{
		Faulted?.Invoke(this, new UiSessionFaultedEventArgs(e.EventName, e.Exception));
	}
}
