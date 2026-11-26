using System.Text.Json;
using IpCamera.Cameras;
using IpCamera.Widgets;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Widgets;
using Serilog;

namespace IpCamera.Actions;

/// <summary>Saves the current picture of the pressed camera as a JPEG on the Macro Deck computer.</summary>
internal sealed class SaveSnapshotAction(
	Func<IWidgetApi?> widgets,
	CameraRegistry registry,
	ILogger logger) : IActionDefinition
{
	public const string ActionId = "save-snapshot";
	public const string FolderKey = "folder";

	public string Id => ActionId;

	public MacroDeck.Localization.LocalizedText Name => Strings.Actions.SaveSnapshot.Name();

	public MacroDeck.Localization.LocalizedText Description => Strings.Actions.SaveSnapshot.Description();

	public IReadOnlyList<ActionParameter> Parameters =>
	[
		ActionParameter.Folder(
			FolderKey,
			label: Strings.Actions.SaveSnapshot.Folder(),
			description: Strings.Actions.SaveSnapshot.FolderDescription())
	];

	public IActionExecutor CreateExecutor()
	{
		return new Executor(widgets, registry, logger.ForContext<SaveSnapshotAction>());
	}

	private sealed class Executor(
		Func<IWidgetApi?> widgets,
		CameraRegistry registry,
		ILogger logger) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (string.IsNullOrEmpty(context.OwnerWidgetId) || widgets() is not { } widgetApi)
			{
				return ActionResult.Failed(
					ActionErrorCodes.NotFound,
					Strings.Actions.SaveSnapshot.NoWidget());
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
				return ActionResult.Failed(
					ActionErrorCodes.ProviderError,
					Strings.Actions.SaveSnapshot.ReadFailed());
			}

			var cameraId = ReadCameraId(snapshot?.Data);
			if (cameraId is null)
			{
				return ActionResult.Failed(
					ActionErrorCodes.NotConfigured,
					Strings.Actions.SaveSnapshot.NotConfigured());
			}

			if (registry.Find(cameraId) is not { } stream)
			{
				return ActionResult.Failed(
					ActionErrorCodes.NotFound,
					Strings.Actions.SaveSnapshot.NotConfigured());
			}

			var frame = stream.CurrentFrame;
			if (frame is null)
			{
				await stream.PrimeAsync(TimeSpan.FromSeconds(5), context.CancellationToken).ConfigureAwait(false);
				frame = stream.CurrentFrame;
			}

			if (frame is null)
			{
				return ActionResult.Failed(
					ActionErrorCodes.Unavailable,
					Strings.Actions.SaveSnapshot.NoFrame());
			}

			var folder = context.Parameters.TryGetValue(FolderKey, out var value)
				? value as string
				: null;

			try
			{
				await SnapshotWriter.SaveAsync(
					folder,
					stream.Definition.Name,
					frame,
					context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success();
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				logger.Warning("Could not save a camera snapshot: {Reason}", exception.Message);
				return ActionResult.Failed(
					ActionErrorCodes.ProviderError,
					Strings.Actions.SaveSnapshot.SaveFailed());
			}
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

/// <summary>Names and writes the JPEG so the action and its test share one rule.</summary>
internal static class SnapshotWriter
{
	public static async Task<string> SaveAsync(
		string? folder,
		string cameraName,
		byte[] frame,
		CancellationToken cancellationToken)
	{
		var directory = string.IsNullOrWhiteSpace(folder)
			? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
				"IP Camera")
			: folder;

		Directory.CreateDirectory(directory);

		var name = Sanitize(cameraName);
		var file = Path.Combine(
			directory,
			$"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");

		await File.WriteAllBytesAsync(file, frame, cancellationToken).ConfigureAwait(false);
		return file;
	}

	internal static string Sanitize(string cameraName)
	{
		var invalid = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
		var cleaned = new string(cameraName
			.Select(character => character < ' ' || invalid.Contains(character) ? '-' : character)
			.ToArray())
			.Trim()
			.TrimEnd('.');

		return string.IsNullOrEmpty(cleaned) ? "camera" : cleaned;
	}
}
