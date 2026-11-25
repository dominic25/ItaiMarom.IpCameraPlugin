using System.Globalization;
using System.Text.Json;
using IpCamera.Actions;
using IpCamera.Streaming;
using MacroDeck.Localization;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.VideoStreams;
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
	public const string OverlayKey = "overlay";

	public const string FitContain = "contain";
	public const string FitCover = "cover";

	private const string DataSchema = """
		{
		  "type": "object",
		  "properties": {
		    "camera": { "type": "string" },
		    "fit": { "type": "string" },
		    "overlay": { "type": "boolean" },
		    "flows": { "type": "array" },
		    "backgroundColor": { "type": "string" },
		    "label": { "type": "string" },
		    "labelColor": { "type": "string" },
		    "fontFaceId": { "type": "string" },
		    "fontSize": { "type": "number" },
		    "textAlign": { "type": "string" },
		    "labelPosition": { "type": "string" },
		    "accentColor": { "type": "string" },
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
			DefaultData: $$"""{"{{CameraKey}}":"","{{FitKey}}":"{{FitCover}}","{{OverlayKey}}":true}""",
			DataSchema: DataSchema,
			HasConfiguration: true)
		{
			SupportsFlows = true,
			DefaultShortPressAction = new WidgetDefaultAction(ShowCameraAction.ActionId),
			AppearanceProperties =
			[
				WidgetAppearanceProperty.BackgroundColor,
				WidgetAppearanceProperty.Label,
				WidgetAppearanceProperty.LabelColor,
				WidgetAppearanceProperty.Font,
				WidgetAppearanceProperty.AccentColor
			]
		};
	}

	public static UiElement BuildView(UiSurface surface, string? providerId)
	{
		return BuildView(surface, providerId, null).Root;
	}

	public static CameraWidgetView BuildView(
		UiSurface surface,
		string? providerId,
		Func<string, IFrameStream?>? findStream)
	{
		var data = UiWidgetSurface.Data(surface);
		var cameraId = ReadString(data, CameraKey);

		if (UiWidgetSurface.IsSampleRequested(surface) ||
			string.IsNullOrEmpty(providerId) ||
			string.IsNullOrEmpty(cameraId))
		{
			return new CameraWidgetView(Placeholder(Strings.Widgets.Camera.NotConfigured()), null);
		}

		var appearance = UiWidgetAppearance.Read(data);
		var fit = ReadString(data, FitKey) == FitContain ? FitContain : FitCover;

		UiElement content = new UiVideoStream
		{
			Key = "stream",
			Stream = UiValue.Of(new UiVideoStreamReference { Provider = providerId, Id = cameraId }),
			Fit = UiValue.Of(fit),
			Fill = true,
			Fallback = Placeholder(Strings.Widgets.Camera.UpdateRequired())
		};

		var children = new List<UiElement> { content };

		if (BuildLabel(appearance) is { } label)
		{
			children.Add(label);
		}

		CameraOverlay? overlay = null;
		if (ReadBool(data, OverlayKey) && findStream is not null)
		{
			overlay = new CameraOverlay(() => findStream(cameraId));
			overlay.Update();
			children.Add(overlay.Build());
		}

		var root = new UiStack
		{
			Key = "root",
			Fill = true,
			Background = appearance.BackgroundColor is { Length: > 0 } background
				? UiValue.Of(background)
				: default,
			Children =
			[
				new UiLayer
				{
					Key = "layer",
					Fill = true,
					Children = children
				}
			]
		};

		return new CameraWidgetView(root, overlay);
	}

	public static UiElement BuildModal(UiSurface surface, string? providerId)
	{
		var data = UiWidgetSurface.Data(surface);
		var cameraId = ReadString(data, CameraKey);

		if (string.IsNullOrEmpty(cameraId) || string.IsNullOrEmpty(providerId))
		{
			return Placeholder(Strings.Widgets.Camera.NotConfigured());
		}

		return new UiStack
		{
			Key = "modal",
			Fill = true,
			Background = UiValue.Of("#000000"),
			Children =
			[
				new UiVideoStream
				{
					Key = "stream",
					Stream = UiValue.Of(new UiVideoStreamReference { Provider = providerId, Id = cameraId }),
					Fit = UiValue.Of(FitCover),
					Fill = true,
					Fallback = Placeholder(Strings.Widgets.Camera.UpdateRequired())
				}
			]
		};
	}

	public static UiElement BuildConfiguration(UiSurface surface)
	{
		var data = UiWidgetSurface.Data(surface);
		var camera = new UiState<string>(ReadString(data, CameraKey) ?? string.Empty);
		var fit = new UiState<string>(ReadString(data, FitKey) == FitContain ? FitContain : FitCover);
		var overlay = new UiState<bool>(ReadBool(data, OverlayKey));
		var flows = new UiState<JsonElement>(
			data.ValueKind == JsonValueKind.Object &&
			data.TryGetProperty("flows", out var stored) &&
			stored.ValueKind == JsonValueKind.Array
				? stored.Clone()
				: JsonSerializer.SerializeToElement(Array.Empty<object>()));

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
					},
					new UiBooleanInput
					{
						Key = OverlayKey,
						Label = Strings.Widgets.Camera.Config.Overlay(),
						Description = Strings.Widgets.Camera.Config.OverlayDescription(),
						Binding = Bind.To(overlay)
					},
					UiWidgetAppearance.Section(data, UiWidgetAppearanceFields.All)
				]
			},
			Editor = new UiWidgetEditor
			{
				Key = "editor",
				Children =
				[
					new UiActionsListEditor
					{
						Key = "flows",
						Binding = Bind.To(flows),
						CanRun = true
					}
				]
			}
		};
	}

	private static UiStack? BuildLabel(UiWidgetAppearanceValues appearance)
	{
		if (string.IsNullOrWhiteSpace(appearance.Label))
		{
			return null;
		}

		var size = Math.Clamp((appearance.FontSize ?? 100) / 100d * 0.10, 0.05, 0.25);
		var justify = appearance.LabelPosition switch
		{
			"top" => UiComponentJustify.Start,
			"center" => UiComponentJustify.Center,
			_ => UiComponentJustify.End
		};
		var align = appearance.TextAlign switch
		{
			"left" => UiComponentAlignments.Start,
			"right" => UiComponentAlignments.End,
			_ => UiComponentAlignments.Center
		};

		return new UiStack
		{
			Key = "label-overlay",
			Fill = true,
			Justify = UiValue.Of(justify),
			Align = UiValue.Of(UiComponentAlignments.Stretch),
			Padding = 0.05,
			Children =
			[
				new UiTextRun
				{
					Key = "label",
					Text = appearance.Label,
					Size = size,
					MinSize = size * 0.7,
					Align = UiValue.Of(align),
					Color = UiValue.Of(string.IsNullOrEmpty(appearance.LabelColor) ? "#ffffff" : appearance.LabelColor),
					FontFace = appearance.FontFaceId is { Length: > 0 } face ? UiValue.Of(face) : default,
					MaxLines = UiValue.Of(2),
					Wrap = UiValue.Of(true)
				}
			]
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

	private static bool ReadBool(JsonElement data, string key)
	{
		return data.ValueKind == JsonValueKind.Object &&
			data.TryGetProperty(key, out var value) &&
			value.ValueKind == JsonValueKind.True;
	}
}

/// <summary>The widget tree plus the live overlay a session ticks, when the overlay is enabled.</summary>
internal sealed record CameraWidgetView(UiElement Root, CameraOverlay? Overlay);

/// <summary>The small status pill drawn over the picture: a live dot and the age of the last frame.</summary>
internal sealed class CameraOverlay(Func<IFrameStream?> findStream)
{
	public const string LiveColor = "#22c55e";
	public const string PendingColor = "#f59e0b";

	private readonly UiState<LocalizedString> _text = new(Strings.Widgets.Camera.Overlay.Connecting());
	private readonly UiState<string> _color = new(PendingColor);

	public void Update()
	{
		var stream = findStream();
		var (text, color) = Describe(
			stream?.State ?? VideoStreamState.Disconnected,
			stream?.HasFailed ?? false,
			stream?.LastFrameAt,
			DateTimeOffset.UtcNow);

		_text.Set(text);
		_color.Set(color);
	}

	public UiElement Build()
	{
		return new UiStack
		{
			Key = "status-overlay",
			Fill = true,
			Justify = UiValue.Of(UiComponentJustify.Start),
			Align = UiValue.Of(UiComponentAlignments.Start),
			Padding = 0.035,
			Children =
			[
				new UiStack
				{
					Key = "status-pill",
					Direction = UiValue.Of(UiComponentDirections.Horizontal),
					Align = UiValue.Of(UiComponentAlignments.Center),
					Gap = 0.012,
					Padding = 0.022,
					Background = UiValue.Of("#000000a8"),
					Children =
					[
						new UiTextRun
						{
							Key = "status-dot",
							Text = "●",
							Size = 0.04,
							MinSize = 0.03,
							Color = UiValue.From(() => _color.Value),
							Align = UiValue.Of(UiComponentAlignments.Center),
							MaxLines = 1
						},
						new UiTextRun
						{
							Key = "status-text",
							Text = UiText.FromLocalized(() => _text.Value),
							Size = 0.038,
							MinSize = 0.028,
							Color = UiValue.Of("#e8eef6"),
							MaxLines = 1
						}
					]
				}
			]
		};
	}

	internal static (LocalizedString Text, string Color) Describe(
		VideoStreamState state,
		bool hasFailed,
		DateTimeOffset? lastFrameAt,
		DateTimeOffset now)
	{
		if (hasFailed || (state != VideoStreamState.Connected && lastFrameAt is not null))
		{
			return (Strings.Widgets.Camera.Overlay.Reconnecting(), PendingColor);
		}

		if (state != VideoStreamState.Connected || lastFrameAt is null)
		{
			return (Strings.Widgets.Camera.Overlay.Connecting(), PendingColor);
		}

		var age = now - lastFrameAt.Value;
		if (age < TimeSpan.Zero)
		{
			age = TimeSpan.Zero;
		}

		if (age < TimeSpan.FromSeconds(1))
		{
			return (Strings.Widgets.Camera.Overlay.Live(), LiveColor);
		}

		if (age < TimeSpan.FromMinutes(1))
		{
			return (
				Strings.Widgets.Camera.Overlay.LiveSeconds(
					LocalizedText.FromLiteral(((int)age.TotalSeconds).ToString(CultureInfo.InvariantCulture))),
				LiveColor);
		}

		if (age < TimeSpan.FromHours(1))
		{
			return (
				Strings.Widgets.Camera.Overlay.LiveMinutes(
					LocalizedText.FromLiteral(((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture))),
				LiveColor);
		}

		return (
			Strings.Widgets.Camera.Overlay.LiveHours(
				LocalizedText.FromLiteral(((int)age.TotalHours).ToString(CultureInfo.InvariantCulture))),
			LiveColor);
	}
}

/// <summary>
/// Adapts a <see cref="UiView" /> to the session contract the host drives. The view owns the tree and the
/// patch stream; this class only forwards them and releases the view when the host ends the session.
/// </summary>
internal sealed class ViewSession : IUiSession
{
	private readonly UiView _view;
	private readonly CancellationTokenSource? _tickCancellation;
	private readonly Task? _tickTask;

	public ViewSession(UiView view, Action? tick = null)
	{
		_view = view;
		_view.Changed += OnChanged;
		_view.HandlerFaulted += OnHandlerFaulted;

		if (tick is not null)
		{
			_tickCancellation = new CancellationTokenSource();
			_tickTask = RunTickAsync(tick, _tickCancellation.Token);
		}
	}

	private static async Task RunTickAsync(Action tick, CancellationToken cancellationToken)
	{
		using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

		try
		{
			while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
			{
				tick();
			}
		}
		catch (OperationCanceledException)
		{
		}
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

	public async ValueTask DisposeAsync()
	{
		_view.Changed -= OnChanged;
		_view.HandlerFaulted -= OnHandlerFaulted;

		if (_tickCancellation is not null)
		{
			await _tickCancellation.CancelAsync().ConfigureAwait(false);

			if (_tickTask is not null)
			{
				try
				{
					await _tickTask.ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
				}
			}

			_tickCancellation.Dispose();
		}

		_view.Dispose();
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
