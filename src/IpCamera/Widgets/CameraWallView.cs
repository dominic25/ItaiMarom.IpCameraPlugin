using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;

namespace IpCamera.Widgets;

/// <summary>The folder view that paints one or more cameras across the whole folder.</summary>
internal static class CameraWallView
{
	public const string ViewId = "camera-wall";
	public const string ColumnsKey = "columns";
	public const string FitKey = "fit";

	private static readonly string[] _cameraKeys = ["camera1", "camera2", "camera3", "camera4"];

	public static UiStack Build(JsonElement configuration, string? providerId)
	{
		var cameras = _cameraKeys
			.Select(key => ReadString(configuration, key))
			.ToArray();

		if (string.IsNullOrEmpty(providerId) || cameras.All(string.IsNullOrEmpty))
		{
			return Placeholder(Strings.FolderViews.Config.Empty());
		}

		var columns = ReadString(configuration, ColumnsKey) == "2" ? 2 : 1;
		var fit = ReadString(configuration, FitKey) == "contain" ? "contain" : "cover";

		var cells = cameras
			.Select((camera, index) => Cell(camera, providerId, fit, index))
			.ToList();

		if (columns == 1)
		{
			return new UiStack
			{
				Key = "wall",
				Fill = true,
				Direction = UiValue.Of(UiComponentDirections.Vertical),
				Gap = 0.008,
				Background = UiValue.Of("#000000"),
				Children = cells
			};
		}

		var nonEmpty = cameras.Count(camera => !string.IsNullOrEmpty(camera));
		var rows = new List<UiElement>();
		for (var index = 0; index < nonEmpty; index += 2)
		{
			var row = new List<UiElement> { cells[index] };
			if (index + 1 < nonEmpty)
			{
				row.Add(cells[index + 1]);
			}

			rows.Add(new UiStack
			{
				Key = $"wall-row-{index / 2}",
				Fill = true,
				Direction = UiValue.Of(UiComponentDirections.Horizontal),
				Gap = 0.008,
				Children = row
			});
		}

		return new UiStack
		{
			Key = "wall",
			Fill = true,
			Direction = UiValue.Of(UiComponentDirections.Vertical),
			Gap = 0.008,
			Background = UiValue.Of("#000000"),
			Children = rows
		};
	}

	public static UiStack BuildConfiguration(JsonElement configuration)
	{
		var camera1 = new UiState<string>(ReadString(configuration, "camera1") ?? string.Empty);
		var camera2 = new UiState<string>(ReadString(configuration, "camera2") ?? string.Empty);
		var camera3 = new UiState<string>(ReadString(configuration, "camera3") ?? string.Empty);
		var camera4 = new UiState<string>(ReadString(configuration, "camera4") ?? string.Empty);
		var columns = new UiState<string>(ReadString(configuration, ColumnsKey) == "2" ? "2" : "1");
		var fit = new UiState<string>(ReadString(configuration, FitKey) == "contain" ? "contain" : "cover");

		return new UiStack
		{
			Key = "camera-wall-config",
			Direction = UiValue.Of(UiComponentDirections.Vertical),
			Padding = 0.04,
			Gap = 0.02,
			Children =
			[
				new UiTextRun
				{
					Key = "wall-config-title",
					Text = Strings.FolderViews.Config.Title(),
					Size = 0.055,
					MinSize = 0.04,
					MaxLines = 1
				},
				new UiTextRun
				{
					Key = "wall-config-description",
					Text = Strings.FolderViews.Config.Description(),
					Size = 0.03,
					MinSize = 0.022,
					Role = UiValue.Of(UiComponentTextRoles.Secondary),
					Wrap = UiValue.Of(true),
					MaxLines = 3
				},
				Picker("camera1", Strings.FolderViews.Config.Camera1(), camera1),
				Picker("camera2", Strings.FolderViews.Config.Camera2(), camera2),
				Picker("camera3", Strings.FolderViews.Config.Camera3(), camera3),
				Picker("camera4", Strings.FolderViews.Config.Camera4(), camera4),
				new UiChoiceInput
				{
					Key = ColumnsKey,
					Label = Strings.FolderViews.Config.Columns(),
					Segmented = UiValue.Of(true),
					Options = UiValue.Of<IReadOnlyList<MacroDeck.Ui.Config.Options.UiOption>>(
					[
						new() { Value = "1", Label = Strings.FolderViews.Config.ColumnOne() },
						new() { Value = "2", Label = Strings.FolderViews.Config.ColumnTwo() }
					]),
					Binding = Bind.To(columns)
				},
				new UiChoiceInput
				{
					Key = FitKey,
					Label = Strings.Widgets.Camera.Config.Fit(),
					Segmented = UiValue.Of(true),
					Options = UiValue.Of<IReadOnlyList<MacroDeck.Ui.Config.Options.UiOption>>(
					[
						new() { Value = "cover", Label = Strings.Widgets.Camera.Config.FitCover() },
						new() { Value = "contain", Label = Strings.Widgets.Camera.Config.FitContain() }
					]),
					Binding = Bind.To(fit)
				}
			]
		};
	}

	private static UiIntegrationPickerInput Picker(string key, LocalizedText label, UiState<string> state)
	{
		return new UiIntegrationPickerInput
		{
			Key = key,
			Label = label,
			Capability = UiValue.Of("video-stream-provider"),
			ConfigurationEntries = UiValue.Of(true),
			Binding = Bind.To(state)
		};
	}

	private static UiElement Cell(string? camera, string providerId, string fit, int index)
	{
		if (string.IsNullOrEmpty(camera))
		{
			return Placeholder(
				Strings.Widgets.Camera.NotConfigured(),
				$"wall-cell-{index}-empty");
		}

		return new UiVideoStream
		{
			Key = $"wall-cell-{index}",
			Stream = UiValue.Of(new MacroDeck.Ui.Model.References.UiVideoStreamReference
			{
				Provider = providerId,
				Id = camera
			}),
			Fit = UiValue.Of(fit),
			Fill = true,
			Fallback = Placeholder(
				Strings.Widgets.Camera.UpdateRequired(),
				$"wall-cell-{index}-fallback")
		};
	}

	private static UiStack Placeholder(LocalizedText hint, string key = "wall-placeholder")
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
					Key = key + "-text",
					Text = hint,
					Size = 0.08,
					MinSize = 0.05,
					Role = UiValue.Of(UiComponentTextRoles.Secondary),
					Align = UiValue.Of(UiComponentAlignments.Center),
					Wrap = UiValue.Of(true),
					MaxLines = 2
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
