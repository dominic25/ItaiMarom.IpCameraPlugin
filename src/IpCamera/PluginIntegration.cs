using System.Text.Json;
using IpCamera.Actions;
using IpCamera.Cameras;
using IpCamera.ConfigFlow;
using IpCamera.Onvif;
using IpCamera.Streaming;
using IpCamera.VideoStreams;
using IpCamera.Widgets;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.FolderViews;
using MacroDeck.Sdk.Issues;
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

	private readonly ILogger _logger;
	private readonly CameraRegistry _registry;
	private readonly MjpegServer _server;
	private readonly FfmpegLocator _ffmpeg;
	private readonly ICameraProbe _probe;
	private readonly IOnvifDiscovery _onvifDiscovery;
	private readonly IOnvifClient _onvifClient;

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
		ILogger logger)
	{
		_registry = registry;
		_server = server;
		_ffmpeg = ffmpeg;
		_probe = probe;
		_onvifDiscovery = onvifDiscovery;
		_onvifClient = onvifClient;
		_logger = logger.ForContext<PluginIntegration>();
		Actions =
		[
			new ShowCameraAction(() => _context?.Widgets, _logger),
			new SaveSnapshotAction(() => _context?.Widgets, _registry, _logger),
			.. CameraControlAction.Create(() => _context?.Widgets, _registry, _onvifClient, _logger)
		];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
	[
		new() { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive },
		new() { Kind = UiSurfaceKinds.Dialog, SessionMode = UiSessionModes.Exclusive },
		new() { Kind = UiSurfaceKinds.Page, SessionMode = UiSessionModes.Exclusive },
		new() { Kind = UiSurfaceKinds.Folder, SessionMode = UiSessionModes.Shared }
	];

	public IReadOnlyList<UiPageDeclaration> Pages { get; } =
	[
		new() { Id = "cameras", Title = Strings.Health.Title() }
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
		return new CameraConfigFlow(_ffmpeg, _probe, _onvifDiscovery, _onvifClient, _logger);
	}

	public async Task InitializeAsync(IIntegrationContext context)
	{
		_context = context;
		var cameras = await ReadCamerasAsync(context).ConfigureAwait(false);
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

		if (surface.Kind == UiSurfaceKinds.Page &&
			surface.Attributes.TryGetValue(UiPageSurfaceAttributes.PageId, out var pageId) &&
			pageId.GetString() == "cameras")
		{
			var health = new CameraHealthView(_registry);
			health.Update();

			return new ViewSession(
				new UiView(surface, health.Build()),
				health.Update);
		}

		if (surface.Kind == UiSurfaceKinds.Folder &&
			surface.Attributes.TryGetValue(UiFolderSurfaceAttributes.ViewId, out var folderViewId) &&
			IsCameraWall(folderViewId.GetString()))
		{
			var configuration = surface.Attributes.TryGetValue(
				UiFolderSurfaceAttributes.Configuration,
				out var stored) ? stored : default;

			return Create(surface, CameraWallView.Build(configuration, _providerQualifiedId));
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
			viewId.GetString() == ShowCameraAction.ModalViewId)
		{
			return Create(surface, CameraWidget.BuildModal(surface, _providerQualifiedId));
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
			return Create(surface, CameraWidget.BuildConfiguration(surface));
		}

		if (UiWidgetSurface.IsFor(surface, widgetTypeId))
		{
			var providerId = await ResolveProviderIdAsync(cancellationToken).ConfigureAwait(false);
			var view = CameraWidget.BuildView(surface, providerId, _registry.Find);
			return new ViewSession(
				new UiView(surface, view.Root),
				view.Overlay is null ? null : view.Overlay.Update);
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
				[CameraSettings.RtspTransportKey] = await ReadStringAsync(context.Config, entry.Id, CameraSettings.RtspTransportKey).ConfigureAwait(false)
			};

			var password = await ReadSecretAsync(context.Config, entry.Id, CameraSettings.PasswordKey).ConfigureAwait(false);

			var camera = CameraDefinition.TryCreate(entry.Id, entry.Title, values, password, out var error);
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
