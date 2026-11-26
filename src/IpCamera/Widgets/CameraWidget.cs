using System.Globalization;
using System.Text.Json;
using IpCamera.Actions;
using IpCamera.Cameras;
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

/// <summary>Runs one control for the arrows drawn on a widget cell.</summary>
internal delegate Task CameraControlHandler(
	string cameraId,
	CameraControlOperation operation,
	CancellationToken cancellationToken);

/// <summary>The widget type the plugin offers, its picker sample, its live view and its editor.</summary>
internal static class CameraWidget
{
	public const string TypeId = "camera";

	public const string CameraKey = "camera";
	public const string FitKey = "fit";
	public const string OverlayKey = "overlay";
	public const string OverlayBackgroundKey = "overlayBackground";
	public const string ControlsKey = "controls";
	public const string ControlsOpacityKey = "controlsOpacity";
	public const string OpenWindowKey = "openWindow";
	public const string LayoutKey = "layout";
	public const string Camera2Key = "camera2";
	public const string Camera3Key = "camera3";
	public const string Camera4Key = "camera4";

	public const string LayoutSingle = "single";
	public const string LayoutSplit = "split";
	public const string LayoutGrid = "grid";

	public const string FitContain = "contain";
	public const string FitCover = "cover";

	private const string DataSchema = """
		{
		  "type": "object",
		  "properties": {
		    "camera": { "type": "string" },
		    "camera2": { "type": "string" },
		    "camera3": { "type": "string" },
		    "camera4": { "type": "string" },
		    "layout": { "type": "string" },
		    "fit": { "type": "string" },
		    "overlay": { "type": "boolean" },
		    "overlayBackground": { "type": "number" },
		    "controls": { "type": "boolean" },
		    "controlsOpacity": { "type": "number" },
		    "openWindow": { "type": "boolean" },
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
			DefaultData: $$"""{"{{CameraKey}}":"","{{FitKey}}":"{{FitCover}}","{{OverlayKey}}":true,"{{OverlayBackgroundKey}}":0,"{{ControlsKey}}":false,"{{ControlsOpacityKey}}":60,"{{OpenWindowKey}}":true,"{{LayoutKey}}":"{{LayoutSingle}}"}""",
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
		return BuildView(surface, providerId, null, null).Root;
	}

	public static CameraWidgetView BuildView(
		UiSurface surface,
		string? providerId,
		Func<string, IFrameStream?>? findStream,
		CameraControlHandler? control = null)
	{
		var data = UiWidgetSurface.Data(surface);
		var cameraId = ReadString(data, CameraKey);

		if (UiWidgetSurface.IsSampleRequested(surface) ||
			string.IsNullOrEmpty(providerId) ||
			string.IsNullOrEmpty(cameraId))
		{
			return new CameraWidgetView(
				Placeholder(Strings.Widgets.Camera.NotConfigured()),
				[],
				new Dictionary<string, string>(),
				false,
				0.6);
		}

		var appearance = UiWidgetAppearance.Read(data);
		var fit = ReadString(data, FitKey) == FitContain ? FitContain : FitCover;
		var layout = ReadLayout(data);
		var overlays = new List<CameraOverlay>();
		var openTargets = new Dictionary<string, string>(StringComparer.Ordinal);
		var controlsEnabled = ReadBool(data, ControlsKey) && control is not null;
		var controlsOpacity = Math.Clamp(ReadNumber(data, ControlsOpacityKey, 60), 0, 100) / 100.0;
		var openWindow = data.ValueKind != JsonValueKind.Object ||
			!data.TryGetProperty(OpenWindowKey, out var storedOpenWindow) ||
			storedOpenWindow.ValueKind != JsonValueKind.False;

		UiElement content;
		CameraOverlay? overlay = null;
		UiElement? arrows = null;

		if (layout is LayoutSplit or LayoutGrid)
		{
			content = BuildMultiContent(
				layout,
				data,
				providerId,
				fit,
				findStream,
				overlays,
				openWindow ? openTargets : null,
				controlsEnabled,
				controlsOpacity,
				control);
		}
		else
		{
			content = new UiVideoStream
			{
				Key = "stream",
				Stream = UiValue.Of(new UiVideoStreamReference { Provider = providerId, Id = cameraId }),
				Fit = UiValue.Of(fit),
				Fill = true,
				Fallback = Placeholder(Strings.Widgets.Camera.UpdateRequired())
			};

			if (ReadBool(data, OverlayKey) && findStream is not null)
			{
				overlay = new CameraOverlay(
					() => findStream(cameraId),
					Math.Clamp(ReadNumber(data, OverlayBackgroundKey, 0), 0, 100));
				overlay.Update();
				overlays.Add(overlay);
			}

			if (openWindow)
			{
				openTargets["open-camera"] = cameraId;
			}

			if (controlsEnabled && findStream?.Invoke(cameraId) is not null)
			{
				arrows = ArrowPad(cameraId, controlsOpacity, control!, "arrows");
			}
		}

		var children = new List<UiElement> { content };

		if (BuildLabel(appearance) is { } label)
		{
			children.Add(label);
		}

		if (overlay is not null)
		{
			children.Add(overlay.Build());
		}

		if (layout is not (LayoutSplit or LayoutGrid) && openTargets.ContainsKey("open-camera"))
		{
			children.Add(OpenButton("open-camera"));
		}

		if (arrows is not null)
		{
			children.Add(arrows);
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

		return new CameraWidgetView(root, overlays, openTargets, controlsEnabled, controlsOpacity);
	}

	internal static UiButton OpenButton(string key)
	{
		return new UiButton
		{
			Key = key,
			Fill = true,
			Background = UiValue.Of("transparent"),
			Events = [UiEventHandler.On(UiComponentEvents.Press, () => { })]
		};
	}

	private static UiStack BuildMultiContent(
		string layout,
		JsonElement data,
		string providerId,
		string fit,
		Func<string, IFrameStream?>? findStream,
		List<CameraOverlay> overlays,
		Dictionary<string, string>? openTargets,
		bool controlsEnabled,
		double controlsOpacity,
		CameraControlHandler? control)
	{
		string?[] cameras = layout == LayoutSplit
			?
			[
				ReadString(data, CameraKey),
				ReadString(data, Camera2Key)
			]
			:
			[
				ReadString(data, CameraKey),
				ReadString(data, Camera2Key),
				ReadString(data, Camera3Key),
				ReadString(data, Camera4Key)
			];

		var overlayEnabled = ReadBool(data, OverlayKey) && findStream is not null;
		var opacity = Math.Clamp(ReadNumber(data, OverlayBackgroundKey, 0), 0, 100);
		var cells = cameras
			.Select((camera, index) => BuildCell(
				camera,
				providerId,
				fit,
				index,
				overlayEnabled ? findStream : null,
				opacity,
				overlays,
				openTargets,
				controlsEnabled ? findStream : null,
				controlsOpacity,
				control))
			.ToList();

		if (layout == LayoutSplit)
		{
			return new UiStack
			{
				Key = "split",
				Direction = UiValue.Of(UiComponentDirections.Horizontal),
				Fill = true,
				Gap = 0.01,
				Children = cells
			};
		}

		return new UiStack
		{
			Key = "grid",
			Direction = UiValue.Of(UiComponentDirections.Vertical),
			Fill = true,
			Gap = 0.01,
			Children =
			[
				Row("grid-row-1", cells[0], cells[1]),
				Row("grid-row-2", cells[2], cells[3])
			]
		};
	}

	private static UiStack Row(string key, UiElement left, UiElement right)
	{
		return new UiStack
		{
			Key = key,
			Direction = UiValue.Of(UiComponentDirections.Horizontal),
			Fill = true,
			Gap = 0.01,
			Children = [left, right]
		};
	}

	private static UiElement BuildCell(
		string? cameraId,
		string providerId,
		string fit,
		int index,
		Func<string, IFrameStream?>? findStream,
		double backgroundOpacity,
		List<CameraOverlay> overlays,
		Dictionary<string, string>? openTargets,
		Func<string, IFrameStream?>? controlsFinder,
		double controlsOpacity,
		CameraControlHandler? control)
	{
		if (string.IsNullOrEmpty(cameraId))
		{
			return Placeholder(
				Strings.Widgets.Camera.NotConfigured(),
				$"cell-{index}-empty");
		}

		var video = new UiVideoStream
		{
			Key = $"cell-{index}",
			Stream = UiValue.Of(new UiVideoStreamReference { Provider = providerId, Id = cameraId }),
			Fit = UiValue.Of(fit),
			Fill = true,
			Fallback = Placeholder(
				Strings.Widgets.Camera.UpdateRequired(),
				$"cell-{index}-fallback")
		};

		var children = new List<UiElement> { video };

		if (findStream is not null)
		{
			var overlay = new CameraOverlay(
				() => findStream(cameraId),
				backgroundOpacity,
				$"cell-{index}-status");
			overlay.Update();
			overlays.Add(overlay);
			children.Add(overlay.Build());
		}

		if (openTargets is not null)
		{
			openTargets[$"open-camera-{index}"] = cameraId;
			children.Add(OpenButton($"open-camera-{index}"));
		}

		if (control is not null &&
			controlsFinder?.Invoke(cameraId) is not null)
		{
			children.Add(ArrowPad(cameraId, controlsOpacity, control, $"c{index}"));
		}

		if (children.Count == 1)
		{
			return video;
		}

		return new UiLayer
		{
			Key = $"cell-{index}-layer",
			Fill = true,
			Children = children
		};
	}

	internal static UiStack ArrowPad(
		string cameraId,
		double opacity,
		CameraControlHandler control,
		string keyPrefix)
	{
		return new UiStack
		{
			Key = keyPrefix,
			Fill = true,
			Direction = UiValue.Of(UiComponentDirections.Vertical),
			Children =
			[
				ArrowRow(
					keyPrefix + "-top",
					UiComponentJustify.Center,
					Arrow(keyPrefix + "-up", "\u25B2", cameraId, CameraControlOperation.MoveUp, opacity, control)),
				new UiStack
				{
					Key = keyPrefix + "-middle",
					Fill = true,
					Direction = UiValue.Of(UiComponentDirections.Horizontal),
					Justify = UiValue.Of(UiComponentJustify.SpaceBetween),
					Align = UiValue.Of(UiComponentAlignments.Center),
					Padding = 0.02,
					Children =
					[
						Arrow(keyPrefix + "-left", "\u25C0", cameraId, CameraControlOperation.MoveLeft, opacity, control),
						Arrow(keyPrefix + "-right", "\u25B6", cameraId, CameraControlOperation.MoveRight, opacity, control)
					]
				},
				ArrowRow(
					keyPrefix + "-bottom",
					UiComponentJustify.Center,
					Arrow(keyPrefix + "-down", "\u25BC", cameraId, CameraControlOperation.MoveDown, opacity, control))
			]
		};
	}

	private static UiStack ArrowRow(string key, string justify, UiElement arrow)
	{
		return new UiStack
		{
			Key = key,
			Direction = UiValue.Of(UiComponentDirections.Horizontal),
			Justify = UiValue.Of(justify),
			Align = UiValue.Of(UiComponentAlignments.Center),
			Padding = 0.02,
			Children = [arrow]
		};
	}

	private static UiModifier Arrow(
		string key,
		string glyph,
		string cameraId,
		CameraControlOperation operation,
		double opacity,
		CameraControlHandler control)
	{
		return new UiModifier
		{
			Key = key + "-box",
			MainSize = UiSize.FromBasis(0.11),
			Frame = UiValue.Of(new UiFrame { AspectRatio = 1 }),
			Opacity = UiValue.Of(opacity),
			Child = new UiButton
			{
				Key = key,
				Justify = UiValue.Of(UiComponentJustify.Center),
				Align = UiValue.Of(UiComponentAlignments.Center),
				Background = UiValue.Of("#000000"),
				Events =
				[
					UiEventHandler.OnAsync(
						UiComponentEvents.Press,
						cancellationToken => control(cameraId, operation, cancellationToken))
				],
				Children =
				[
					new UiTextRun
					{
						Key = key + "-g",
						Text = glyph,
						Size = 0.04,
						MinSize = 0.02,
						Align = UiValue.Of(UiComponentAlignments.Center),
						Color = UiValue.Of("#ffffff"),
						MaxLines = 1
					}
				]
			}
		};
	}

	public static UiElement BuildModal(
		UiSurface surface,
		string? providerId,
		Func<string, CameraDefinition?>? findCamera = null,
		CameraControlHandler? control = null)
	{
		var data = UiWidgetSurface.Data(surface);
		var cameraId = ReadString(data, CameraKey);

		if (string.IsNullOrEmpty(cameraId) || string.IsNullOrEmpty(providerId))
		{
			return Placeholder(Strings.Widgets.Camera.NotConfigured());
		}

		var children = new List<UiElement>
		{
			new UiVideoStream
			{
				Key = "stream",
				Stream = UiValue.Of(new UiVideoStreamReference { Provider = providerId, Id = cameraId }),
				Fit = UiValue.Of(FitCover),
				Fill = true,
				Fallback = Placeholder(Strings.Widgets.Camera.UpdateRequired())
			}
		};

		var wantsControls = data.ValueKind != JsonValueKind.Object ||
			!data.TryGetProperty(ControlsKey, out var storedControls) ||
			storedControls.ValueKind != JsonValueKind.False;
		var controlsOpacity =
			Math.Clamp(ReadNumber(data, ControlsOpacityKey, 60), 0, 100) / 100.0;

		if (wantsControls && control is not null && findCamera?.Invoke(cameraId) is not null)
		{
			children.Add(ArrowPad(cameraId, controlsOpacity, control, "modal"));
		}

		return new UiStack
		{
			Key = "modal",
			Fill = true,
			Background = UiValue.Of("#000000"),
			Children = children
		};
	}

	public static UiElement BuildConfiguration(UiSurface surface)
	{
		var data = UiWidgetSurface.Data(surface);
		var layout = new UiState<string>(ReadLayout(data));
		var camera = new UiState<string>(ReadString(data, CameraKey) ?? string.Empty);
		var camera2 = new UiState<string>(ReadString(data, Camera2Key) ?? string.Empty);
		var camera3 = new UiState<string>(ReadString(data, Camera3Key) ?? string.Empty);
		var camera4 = new UiState<string>(ReadString(data, Camera4Key) ?? string.Empty);
		var fit = new UiState<string>(ReadString(data, FitKey) == FitContain ? FitContain : FitCover);
		var overlay = new UiState<bool>(ReadBool(data, OverlayKey));
		var overlayBackground = new UiState<double>(
			Math.Clamp(ReadNumber(data, OverlayBackgroundKey, 0), 0, 100));
		var controls = new UiState<bool>(ReadBool(data, ControlsKey));
		var controlsOpacity = new UiState<double>(
			Math.Clamp(ReadNumber(data, ControlsOpacityKey, 60), 0, 100));
		var openWindow = new UiState<bool>(
			data.ValueKind != JsonValueKind.Object ||
			!data.TryGetProperty(OpenWindowKey, out var storedOpenWindow) ||
			storedOpenWindow.ValueKind != JsonValueKind.False);
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
					new UiChoiceInput
					{
						Key = LayoutKey,
						Label = Strings.Widgets.Camera.Config.Layout(),
						Segmented = UiValue.Of(true),
						Options = UiValue.Of<IReadOnlyList<UiOption>>(
						[
							new UiOption { Value = LayoutSingle, Label = Strings.Widgets.Camera.Config.LayoutSingle() },
							new UiOption { Value = LayoutSplit, Label = Strings.Widgets.Camera.Config.LayoutSplit() },
							new UiOption { Value = LayoutGrid, Label = Strings.Widgets.Camera.Config.LayoutGrid() }
						]),
						Binding = Bind.To(layout)
					},
					new UiIntegrationPickerInput
					{
						Key = CameraKey,
						Label = Strings.Widgets.Camera.Config.Camera(),
						Description = Strings.Widgets.Camera.Config.CameraDescription(),
						Capability = UiValue.Of("video-stream-provider"),
						ConfigurationEntries = UiValue.Of(true),
						Binding = Bind.To(camera)
					},
					new UiIntegrationPickerInput
					{
						Key = Camera2Key,
						Label = Strings.Widgets.Camera.Config.Camera2(),
						Description = Strings.Widgets.Camera.Config.Camera2Description(),
						Capability = UiValue.Of("video-stream-provider"),
						ConfigurationEntries = UiValue.Of(true),
						Binding = Bind.To(camera2)
					},
					new UiIntegrationPickerInput
					{
						Key = Camera3Key,
						Label = Strings.Widgets.Camera.Config.Camera3(),
						Description = Strings.Widgets.Camera.Config.Camera34Description(),
						Capability = UiValue.Of("video-stream-provider"),
						ConfigurationEntries = UiValue.Of(true),
						Binding = Bind.To(camera3)
					},
					new UiIntegrationPickerInput
					{
						Key = Camera4Key,
						Label = Strings.Widgets.Camera.Config.Camera4(),
						Description = Strings.Widgets.Camera.Config.Camera34Description(),
						Capability = UiValue.Of("video-stream-provider"),
						ConfigurationEntries = UiValue.Of(true),
						Binding = Bind.To(camera4)
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
					new UiNumberInput
					{
						Key = OverlayBackgroundKey,
						Label = Strings.Widgets.Camera.Config.OverlayBackground(),
						Description = Strings.Widgets.Camera.Config.OverlayBackgroundDescription(),
						Min = 0,
						Max = 100,
						Step = 5,
						ShowSlider = true,
						Binding = Bind.To(overlayBackground)
					},
					new UiBooleanInput
					{
						Key = ControlsKey,
						Label = Strings.Widgets.Camera.Config.Controls(),
						Description = Strings.Widgets.Camera.Config.ControlsDescription(),
						Binding = Bind.To(controls)
					},
					new UiNumberInput
					{
						Key = ControlsOpacityKey,
						Label = Strings.Widgets.Camera.Config.ControlsOpacity(),
						Description = Strings.Widgets.Camera.Config.ControlsOpacityDescription(),
						Min = 0,
						Max = 100,
						Step = 5,
						ShowSlider = true,
						Binding = Bind.To(controlsOpacity)
					},
					new UiBooleanInput
					{
						Key = OpenWindowKey,
						Label = Strings.Widgets.Camera.Config.OpenWindow(),
						Description = Strings.Widgets.Camera.Config.OpenWindowDescription(),
						Binding = Bind.To(openWindow)
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

	private static UiStack Placeholder(MacroDeck.Localization.LocalizedText hint, string key = "placeholder")
	{
		return new UiStack
		{
			Key = key,
			Fill = true,
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

	private static double ReadNumber(JsonElement data, string key, double fallback)
	{
		return data.ValueKind == JsonValueKind.Object &&
			data.TryGetProperty(key, out var value) &&
			value.ValueKind == JsonValueKind.Number &&
			value.TryGetDouble(out var number)
				? number
				: fallback;
	}

	private static string ReadLayout(JsonElement data)
	{
		return ReadString(data, LayoutKey) switch
		{
			LayoutSplit => LayoutSplit,
			LayoutGrid => LayoutGrid,
			_ => LayoutSingle
		};
	}
}

/// <summary>The widget tree plus the live overlay a session ticks, when the overlay is enabled.</summary>
internal sealed record CameraWidgetView(
	UiElement Root,
	IReadOnlyList<CameraOverlay> Overlays,
	IReadOnlyDictionary<string, string> OpenTargets,
	bool Controls,
	double ControlsOpacity);

/// <summary>The small status pill drawn over the picture: a live dot and the age of the last frame.</summary>
internal sealed class CameraOverlay(
	Func<IFrameStream?> findStream,
	double backgroundOpacity = 0,
	string keyPrefix = "status")
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
		var pill = new UiStack
		{
			Key = $"{keyPrefix}-pill",
			Direction = UiValue.Of(UiComponentDirections.Horizontal),
			Align = UiValue.Of(UiComponentAlignments.Center),
			Gap = 0.012,
			Children =
			[
				new UiTextRun
				{
					Key = $"{keyPrefix}-dot",
					Text = "●",
					Size = 0.04,
					MinSize = 0.03,
					Color = UiValue.From(() => _color.Value),
					Align = UiValue.Of(UiComponentAlignments.Center),
					MaxLines = 1
				},
				new UiTextRun
				{
					Key = $"{keyPrefix}-text",
					Text = UiText.FromLocalized(() => _text.Value),
					Size = 0.038,
					MinSize = 0.028,
					Color = UiValue.Of("#e8eef6"),
					MaxLines = 1
				}
			]
		};

		if (backgroundOpacity > 0)
		{
			var alpha = (int)Math.Round(Math.Clamp(backgroundOpacity, 0, 100) / 100 * 255);
			pill = pill with
			{
				Background = UiValue.Of($"#000000{alpha:X2}"),
				Padding = UiSize.FromBasis(0.022)
			};
		}

		return new UiStack
		{
			Key = $"{keyPrefix}-overlay",
			Fill = true,
			Justify = UiValue.Of(UiComponentJustify.Start),
			Align = UiValue.Of(UiComponentAlignments.Start),
			Padding = 0.035,
			Children = [pill]
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
