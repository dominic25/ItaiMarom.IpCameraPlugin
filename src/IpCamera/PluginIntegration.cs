using System.Collections.Concurrent;
using System.Text.Json;
using IpCamera.Actions;
using IpCamera.Cameras;
using IpCamera.ConfigFlow;
using IpCamera.Onvif;
using IpCamera.Streaming;
using IpCamera.VideoStreams;
using IpCamera.Widgets;
using IpCamera.Xiaomi;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.FolderViews;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Scripts;
using MacroDeck.Sdk.Storage;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.VideoStreams;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Widgets;
using Serilog;

namespace IpCamera;

/// <summary>
/// Reads the camera entries, keeps their frame pipelines, and offers them to Macro Deck three ways: as
/// video streams, as the camera widget's streams, and as the widget type itself.
/// </summary>
internal sealed class PluginIntegration : IPluginIntegration,
	IConfigFlowProvider,
	IVideoStreamIntegration,
	IWidgetTypeProvider,
	IUiProvider,
	IFolderViewProvider,
	IIntegrationIssueProvider
{
	private const string FfmpegIssueId = "ffmpeg-missing";
	private const string CameraIssuePrefix = "camera-offline-";
	private const string StreamSizePrefix = "stream-size:";

	private readonly ConcurrentDictionary<string, (int Width, int Height)> _streamSizes = new(StringComparer.Ordinal);

	private readonly ILogger _logger;
	private readonly CameraRegistry _registry;
	private readonly MjpegServer _server;
	private readonly FfmpegLocator _ffmpeg;
	private readonly ICameraProbe _probe;
	private readonly IOnvifDiscovery _onvifDiscovery;
	private readonly IOnvifClient _onvifClient;
	private readonly IMiioClient _miioClient;

	private CameraStreamProvider? _provider;
	private string? _providerQualifiedId;
	private TaskCompletionSource<string?> _providerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private IFolderViewProviderContext? _folderViewContext;
	private string? _folderViewQualifiedId;
	private IIntegrationContext? _context;
	private string? _widgetTypeId;
	private IReadOnlyList<WidgetTypeDescriptor> _widgetTypes = [];
	private bool _ffmpegIssue;
	internal PluginIntegration(
		CameraRegistry registry,
		MjpegServer server,
		FfmpegLocator ffmpeg,
		ICameraProbe probe,
		IOnvifDiscovery onvifDiscovery,
		IOnvifClient onvifClient,
		IMiioClient miioClient,
		ILogger logger)
	{
		_registry = registry;
		_server = server;
		_ffmpeg = ffmpeg;
		_probe = probe;
		_onvifDiscovery = onvifDiscovery;
		_onvifClient = onvifClient;
		_miioClient = miioClient;
		_logger = logger.ForContext<PluginIntegration>();
		Actions =
		[
			new ShowCameraAction(() => _context?.Widgets, _registry, _logger),
			new SaveSnapshotAction(() => _context?.Widgets, _registry, _logger),
			.. CameraControlAction.Create(() => _context?.Widgets, _registry, _onvifClient, _miioClient, _logger)
		];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
	[
		new() { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive },
		new() { Kind = UiSurfaceKinds.Dialog, SessionMode = UiSessionModes.Exclusive },
		new() { Kind = UiSurfaceKinds.Folder, SessionMode = UiSessionModes.Shared }
	];

	private static readonly FolderViewDescriptor _cameraWall = new(
		CameraWallView.ViewId,
		Strings.FolderViews.CameraWall.Name(),
		Strings.FolderViews.CameraWall.Description(),
		HasConfiguration: true);

	public IReadOnlyList<FolderViewDescriptor> GetFolderViews()
	{
		return [_cameraWall];
	}

	public async Task InitializeAsync(
		IFolderViewProviderContext context,
		CancellationToken cancellationToken = default)
	{
		_folderViewContext = context;

		var registration = await HostCalls.RunAsync(
			() => context.RegisterFolderViewAsync(_cameraWall, cancellationToken),
			cancellationToken).ConfigureAwait(false);

		_folderViewQualifiedId = registration.FolderViewId;
	}

	IConfigFlow IConfigFlowProvider.CreateConfigFlow()
	{
		return new CameraConfigFlow(_ffmpeg, _probe, _onvifDiscovery, _onvifClient, _miioClient, _logger);
	}

	public async Task InitializeAsync(IIntegrationContext context)
	{
		_context = context;
		_registry.StreamChanged -= OnStreamSizeChanged;
		_registry.StreamChanged += OnStreamSizeChanged;
		var cameras = await ReadCamerasAsync(context).ConfigureAwait(false);
		cameras = await WithStoredSizesAsync(context, cameras).ConfigureAwait(false);
		await _registry.SetCamerasAsync(cameras, CancellationToken.None).ConfigureAwait(false);
		await _server.StartAsync(CancellationToken.None).ConfigureAwait(false);

		_ffmpegIssue = cameras.Any(camera => camera.NeedsFfmpeg) && !_ffmpeg.IsAvailable;

		_logger.Information("Loaded {CameraCount} camera(s).", cameras.Count);
	}

	public async Task InitializeAsync(IVideoStreamProviderContext context, CancellationToken cancellationToken = default)
	{
		var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Interlocked.Exchange(ref _providerReady, ready)?.TrySetResult(null);

		if (_provider is not null)
		{
			await _provider.DisposeAsync().ConfigureAwait(false);
			_provider = null;
			_providerQualifiedId = null;
		}

		var provider = new CameraStreamProvider(_registry, _server, context, _logger);
		provider.Start();

		try
		{
			var registration = await HostCalls.RunAsync(
				() => context.RegisterProviderAsync(provider, cancellationToken),
				cancellationToken).ConfigureAwait(false);
			_provider = provider;
			_providerQualifiedId = string.IsNullOrEmpty(registration.QualifiedId) ? null : registration.QualifiedId;
			ready.TrySetResult(_providerQualifiedId);
		}
		catch
		{
			ready.TrySetResult(null);
			await provider.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default)
	{
		var descriptor = CameraWidget.Descriptor();
		_widgetTypes = [descriptor];
		return RegisterAsync(context, descriptor, cancellationToken);
	}

	public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes()
	{
		return _widgetTypes;
	}

	public async Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var surface = request.Surface;

		if (surface.Kind == UiSurfaceKinds.Folder &&
			surface.Attributes.TryGetValue(UiFolderSurfaceAttributes.ViewId, out var folderViewId) &&
			IsCameraWall(folderViewId.GetString()))
		{
			var configuration = surface.Attributes.TryGetValue(
				UiFolderSurfaceAttributes.Configuration,
				out var stored) ? stored : default;

			var openTargets = new Dictionary<string, string>(StringComparer.Ordinal);
			var wall = CameraWallView.Build(
				configuration,
				_providerQualifiedId,
				id => _registry.Find(id)?.Definition,
				ControlCameraAsync,
				openTargets);
			var controls = CameraWallView.ReadControls(configuration);

			return new CameraModalSession(
				new UiView(surface, wall),
				openTargets,
				controls.Controls,
				controls.Opacity);
		}

		if (surface.Kind == UiSurfaceKinds.Config &&
			surface.Attributes.TryGetValue(UiConfigSurfaceAttributes.EntryPoint, out var entryPoint) &&
			entryPoint.GetString() == UiConfigEntryPoints.FolderViewConfig &&
			surface.Attributes.TryGetValue(UiConfigSurfaceAttributes.FolderViewId, out var configuredView) &&
			IsCameraWall(configuredView.GetString()))
		{
			var configuration = surface.Attributes.TryGetValue(
				UiConfigSurfaceAttributes.FolderViewConfiguration,
				out var stored) ? stored : default;

			return Create(surface, CameraWallView.BuildConfiguration(configuration));
		}

		if (surface.Kind == UiSurfaceKinds.Dialog &&
			surface.Attributes.TryGetValue(UiDialogSurfaceAttributes.ViewId, out var viewId) &&
			IsCameraModal(viewId.GetString()))
		{
			return Create(
				surface,
				CameraWidget.BuildModal(
					surface,
					_providerQualifiedId,
					id => _registry.Find(id)?.Definition,
					ControlCameraAsync));
		}

		var widgetTypeId = _widgetTypeId;
		if (widgetTypeId is null &&
			WidgetTypeOf(surface) is { } reported &&
			IsCameraWidgetType(reported))
		{
			widgetTypeId = reported;
		}

		if (widgetTypeId is null)
		{
			return null;
		}

		if (UiWidgetSurface.IsConfigurationFor(surface, widgetTypeId))
		{
			return Create(surface, CameraWidget.BuildConfiguration(surface, ReadScripts()));
		}

		if (UiWidgetSurface.IsFor(surface, widgetTypeId))
		{
			var providerId = await ResolveProviderIdAsync(cancellationToken).ConfigureAwait(false);
			var view = CameraWidget.BuildView(
				surface,
				providerId,
				_registry.Find,
				ControlCameraAsync);
			return new CameraModalSession(
				new UiView(surface, view.Root),
				view.OpenTargets,
				view.Controls,
				view.ControlsOpacity,
				view.PressScript,
				_context?.Scripts,
				surface.Attributes.TryGetValue(UiWidgetSurfaceAttributes.WidgetId, out var widgetId)
					? widgetId.GetString()
					: null,
				view.Overlays.Count == 0
					? null
					: () =>
					{
						foreach (var overlay in view.Overlays)
						{
							overlay.Update();
						}
					});
		}

		return null;
	}

	private async Task<string?> ResolveProviderIdAsync(CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

		while (_providerQualifiedId is null)
		{
			var remaining = deadline - DateTime.UtcNow;
			if (remaining <= TimeSpan.Zero)
			{
				break;
			}

			var ready = Volatile.Read(ref _providerReady);
			var finished = await Task.WhenAny(ready.Task, Task.Delay(remaining, cancellationToken))
				.ConfigureAwait(false);
			if (finished != ready.Task)
			{
				break;
			}

			await ready.Task.ConfigureAwait(false);
		}

		return _providerQualifiedId;
	}

	private static string? WidgetTypeOf(UiSurface surface)
	{
		var key = surface.Kind == UiSurfaceKinds.Config
			? UiConfigSurfaceAttributes.WidgetType
			: UiWidgetSurfaceAttributes.WidgetType;

		return surface.Attributes.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
	}

	internal static bool IsCameraWidgetType(string? widgetType)
	{
		return widgetType is not null &&
			widgetType.EndsWith("::" + CameraWidget.TypeId, StringComparison.Ordinal);
	}

	internal static bool IsCameraModal(string? viewId)
	{
		return viewId is not null &&
			(viewId == ShowCameraAction.ModalViewId ||
				viewId.EndsWith("::" + ShowCameraAction.ModalViewId, StringComparison.Ordinal));
	}

	private bool IsCameraWall(string? viewId)
	{
		return viewId == CameraWallView.ViewId ||
			(viewId is not null && viewId == _folderViewQualifiedId);
	}

	public Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var issues = new List<IntegrationIssue>();

		if (_ffmpegIssue)
		{
			issues.Add(FfmpegIssue());
		}

		foreach (var camera in _registry.Cameras)
		{
			if (_registry.Find(camera.Id) is { HasFailed: true, State: not VideoStreamState.Connected })
			{
				issues.Add(CameraIssue(camera));
			}
		}

		return Task.FromResult<IReadOnlyList<IntegrationIssue>>(issues);
	}

	public async Task<IssueResolution> ResolveIssueAsync(
		string issueId,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (issueId.StartsWith(CameraIssuePrefix, StringComparison.Ordinal) &&
			_registry.Find(issueId[CameraIssuePrefix.Length..]) is { } stream)
		{
			await stream.RestartAsync().ConfigureAwait(false);
			return IssueResolution.Ok(Strings.Issues.CameraOffline.Retrying());
		}

		return issueId == FfmpegIssueId
			? IssueResolution.Failed(Strings.Issues.FfmpegMissing.StillMissing())
			: IssueResolution.Failed();
	}

	public async Task ShutdownAsync()
	{
		_registry.StreamChanged -= OnStreamSizeChanged;
		_providerReady.TrySetResult(null);

		if (_folderViewContext is not null)
		{
			try
			{
				await _folderViewContext
					.UnregisterFolderViewAsync(CameraWallView.ViewId, CancellationToken.None)
					.ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				_logger.Debug("Could not withdraw the camera wall: {Reason}", exception.Message);
			}

			_folderViewContext = null;
			_folderViewQualifiedId = null;
		}

		if (_provider is not null)
		{
			await _provider.DisposeAsync().ConfigureAwait(false);
			_provider = null;
			_providerQualifiedId = null;
		}
	}

	private async Task<IReadOnlyList<CameraDefinition>> WithStoredSizesAsync(
		IIntegrationContext context,
		IReadOnlyList<CameraDefinition> cameras)
	{
		var result = new List<CameraDefinition>(cameras.Count);
		foreach (var camera in cameras)
		{
			if (camera.StreamWidth is > 0 && camera.StreamHeight is > 0)
			{
				_streamSizes[camera.Id] = (camera.StreamWidth.Value, camera.StreamHeight.Value);
				result.Add(camera);
				continue;
			}

			if (await TryReadStoredSizeAsync(context, camera.Id).ConfigureAwait(false) is { } size)
			{
				_streamSizes[camera.Id] = size;
				result.Add(camera with { StreamWidth = size.Width, StreamHeight = size.Height });
			}
			else
			{
				result.Add(camera);
			}
		}

		return result;
	}

	private static async Task<(int Width, int Height)?> TryReadStoredSizeAsync(
		IIntegrationContext context,
		string cameraId)
	{
		try
		{
			var read = await context.Storage
				.ReadAsync(StreamSizePrefix + cameraId, _ => true, CancellationToken.None)
				.ConfigureAwait(false);
			if (read.Status != IntegrationStorageReadStatus.Found || read.Content is not { } content)
			{
				return null;
			}

			var parts = content.Split('x');
			return parts.Length == 2 &&
				int.TryParse(parts[0], out var width) && width > 0 &&
				int.TryParse(parts[1], out var height) && height > 0
					? (width, height)
					: null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void OnStreamSizeChanged(IFrameStream stream)
	{
		if (stream.Width is not > 0 || stream.Height is not > 0)
		{
			return;
		}

		var size = (stream.Width.Value, stream.Height.Value);
		if (_streamSizes.TryGetValue(stream.Definition.Id, out var known) && known == size)
		{
			return;
		}

		_streamSizes[stream.Definition.Id] = size;
		_ = SaveStreamSizeAsync(stream.Definition.Id, size);
	}

	private async Task SaveStreamSizeAsync(string cameraId, (int Width, int Height) size)
	{
		var storage = _context?.Storage;
		if (storage is null)
		{
			return;
		}

		try
		{
			await storage
				.WriteAsync(
					StreamSizePrefix + cameraId,
					$"{size.Width}x{size.Height}",
					CancellationToken.None)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			_logger.Debug("Could not remember a stream size: {Reason}", exception.Message);
		}
	}

	private IReadOnlyList<Script> ReadScripts()
	{
		try
		{
			return _context?.Scripts.GetScripts() ?? [];
		}
		catch (Exception exception)
		{
			_logger.Debug("Could not read the scripts: {Reason}", exception.Message);
			return [];
		}
	}

	private Task<ActionResult> ControlCameraAsync(
		string cameraId,
		CameraControlOperation operation,
		CancellationToken cancellationToken)
	{
		var camera = _registry.Find(cameraId)?.Definition;
		if (camera is null)
		{
			return Task.FromResult(ActionResult.Failed(
				ActionErrorCodes.NotConfigured,
				Strings.Actions.Control.CameraMissing()));
		}

		return CameraControl.ExecuteAsync(
			camera,
			operation,
			_onvifClient,
			_miioClient,
			_logger,
			cancellationToken);
	}

	private static IUiSession Create(UiSurface surface, MacroDeck.Ui.Dsl.UiElement root)
	{
		IUiSession session = new ViewSession(new UiView(surface, root));
		return session;
	}

	private async Task RegisterAsync(
		IWidgetTypeProviderContext context,
		WidgetTypeDescriptor descriptor,
		CancellationToken cancellationToken)
	{
		var registration = await HostCalls.RunAsync(
			() => context.RegisterWidgetTypeAsync(descriptor, cancellationToken),
			cancellationToken).ConfigureAwait(false);
		_widgetTypeId = registration.WidgetTypeId;
	}

	private static IntegrationIssue FfmpegIssue()
	{
		return new IntegrationIssue
		{
			Id = FfmpegIssueId,
			Title = Strings.Issues.FfmpegMissing.Title(),
			Description = Strings.Issues.FfmpegMissing.Description(),
			Severity = IntegrationIssueSeverity.Warning,
			HelpUrl = new Uri("https://ffmpeg.org/download.html")
		};
	}

	private static IntegrationIssue CameraIssue(CameraDefinition camera)
	{
		return new IntegrationIssue
		{
			Id = CameraIssuePrefix + camera.Id,
			Title = Strings.Issues.CameraOffline.Title(),
			Description = Strings.Issues.CameraOffline.Description(camera.Name),
			Severity = IntegrationIssueSeverity.Warning,
			ActionLabel = Strings.Issues.CameraOffline.Retry()
		};
	}

	private async Task<IReadOnlyList<CameraDefinition>> ReadCamerasAsync(IIntegrationContext context)
	{
		var entries = await context.Config.GetEntriesAsync().ConfigureAwait(false);
		var cameras = new List<CameraDefinition>(entries.Count);

		foreach (var entry in entries)
		{
			var values = new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				[CameraSettings.KindKey] = await ReadStringAsync(context.Config, entry.Id, CameraSettings.KindKey).ConfigureAwait(false),
				[CameraSettings.UrlKey] = await ReadStringAsync(context.Config, entry.Id, CameraSettings.UrlKey).ConfigureAwait(false),
				[CameraSettings.UsernameKey] = await ReadStringAsync(context.Config, entry.Id, CameraSettings.UsernameKey).ConfigureAwait(false),
				[CameraSettings.FpsKey] = await ReadStringAsync(context.Config, entry.Id, CameraSettings.FpsKey).ConfigureAwait(false),
				[CameraSettings.MaxWidthKey] = await ReadStringAsync(context.Config, entry.Id, CameraSettings.MaxWidthKey).ConfigureAwait(false),
				[CameraSettings.RtspTransportKey] = await ReadStringAsync(context.Config, entry.Id, CameraSettings.RtspTransportKey).ConfigureAwait(false),
				[CameraSettings.ControlKey] = await ReadStringAsync(context.Config, entry.Id, CameraSettings.ControlKey).ConfigureAwait(false),
				[CameraSettings.ControlHostKey] = await ReadStringAsync(context.Config, entry.Id, CameraSettings.ControlHostKey).ConfigureAwait(false)
			};

			var password = await ReadSecretAsync(context.Config, entry.Id, CameraSettings.PasswordKey).ConfigureAwait(false);
			var controlToken = await ReadSecretAsync(context.Config, entry.Id, CameraSettings.ControlTokenKey).ConfigureAwait(false);

			var camera = CameraDefinition.TryCreate(
				entry.Id,
				entry.Title,
				values,
				password,
				out var error,
				controlToken);
			if (camera is null)
			{
				_logger.Warning("Skipped camera entry '{Title}': {Reason}.", entry.Title, error);
				continue;
			}

			cameras.Add(camera);
		}

		return cameras;
	}

	// The host allows a plugin about twenty callbacks in a burst and refills ten per second. Reading
	// seven values per camera outruns that at startup with a few cameras, so every call is paced.
	private static Task<string?> ReadStringAsync(IIntegrationConfig config, Guid entryId, string key)
	{
		return HostCalls.RunAsync(() => config.GetStringAsync(entryId, key));
	}

	private static Task<string?> ReadSecretAsync(IIntegrationConfig config, Guid entryId, string key)
	{
		return HostCalls.RunAsync(() => config.GetSecretAsync(entryId, key));
	}
}
