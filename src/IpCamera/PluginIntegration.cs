using IpCamera.Cameras;
using IpCamera.ConfigFlow;
using IpCamera.Streaming;
using IpCamera.VideoStreams;
using IpCamera.Widgets;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
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
	IIntegrationIssueProvider
{
	private const string FfmpegIssueId = "ffmpeg-missing";

	private readonly ILogger _logger;
	private readonly CameraRegistry _registry;
	private readonly MjpegServer _server;
	private readonly FfmpegLocator _ffmpeg;

	private CameraStreamProvider? _provider;
	private string? _providerQualifiedId;
	private string? _widgetTypeId;
	private IReadOnlyList<WidgetTypeDescriptor> _widgetTypes = [];
	private IReadOnlyList<IntegrationIssue> _issues = [];

	internal PluginIntegration(
		CameraRegistry registry,
		MjpegServer server,
		FfmpegLocator ffmpeg,
		ILogger logger)
	{
		_registry = registry;
		_server = server;
		_ffmpeg = ffmpeg;
		_logger = logger.ForContext<PluginIntegration>();
	}

	public IReadOnlyList<IActionDefinition> Actions => [];

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
	[
		new() { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive }
	];

	IConfigFlow IConfigFlowProvider.CreateConfigFlow()
	{
		return new CameraConfigFlow(_ffmpeg, _logger);
	}

	public async Task InitializeAsync(IIntegrationContext context)
	{
		var cameras = await ReadCamerasAsync(context).ConfigureAwait(false);
		await _registry.SetCamerasAsync(cameras, CancellationToken.None).ConfigureAwait(false);
		await _server.StartAsync(CancellationToken.None).ConfigureAwait(false);

		_issues = cameras.Any(camera => camera.NeedsFfmpeg) && !_ffmpeg.IsAvailable
			? [FfmpegIssue()]
			: [];

		_logger.Information("Loaded {CameraCount} camera(s).", cameras.Count);
	}

	public async Task InitializeAsync(IVideoStreamProviderContext context, CancellationToken cancellationToken = default)
	{
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
			var registration = await context.RegisterProviderAsync(provider, cancellationToken).ConfigureAwait(false);
			_provider = provider;
			_providerQualifiedId = string.IsNullOrEmpty(registration.QualifiedId) ? null : registration.QualifiedId;
		}
		catch
		{
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

	public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (_widgetTypeId is null)
		{
			return Task.FromResult<IUiSession?>(null);
		}

		var surface = request.Surface;

		if (UiWidgetSurface.IsConfigurationFor(surface, _widgetTypeId))
		{
			return Create(surface, CameraWidget.BuildConfiguration(surface));
		}

		if (UiWidgetSurface.IsFor(surface, _widgetTypeId))
		{
			return Create(surface, CameraWidget.BuildView(surface, _providerQualifiedId));
		}

		return Task.FromResult<IUiSession?>(null);
	}

	public Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(_issues);
	}

	public Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		return Task.FromResult(issueId == FfmpegIssueId
			? IssueResolution.Failed(Strings.Issues.FfmpegMissing.StillMissing())
			: IssueResolution.Failed());
	}

	public async Task ShutdownAsync()
	{
		if (_provider is not null)
		{
			await _provider.DisposeAsync().ConfigureAwait(false);
			_provider = null;
			_providerQualifiedId = null;
		}
	}

	private static Task<IUiSession?> Create(UiSurface surface, MacroDeck.Ui.Dsl.UiElement root)
	{
		IUiSession session = new ViewSession(new UiView(surface, root));
		return Task.FromResult<IUiSession?>(session);
	}

	private async Task RegisterAsync(
		IWidgetTypeProviderContext context,
		WidgetTypeDescriptor descriptor,
		CancellationToken cancellationToken)
	{
		var registration = await context.RegisterWidgetTypeAsync(descriptor, cancellationToken).ConfigureAwait(false);
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

	private async Task<IReadOnlyList<CameraDefinition>> ReadCamerasAsync(IIntegrationContext context)
	{
		var entries = await context.Config.GetEntriesAsync().ConfigureAwait(false);
		var cameras = new List<CameraDefinition>(entries.Count);

		foreach (var entry in entries)
		{
			var values = new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				[CameraSettings.KindKey] = await context.Config.GetStringAsync(entry.Id, CameraSettings.KindKey).ConfigureAwait(false),
				[CameraSettings.UrlKey] = await context.Config.GetStringAsync(entry.Id, CameraSettings.UrlKey).ConfigureAwait(false),
				[CameraSettings.UsernameKey] = await context.Config.GetStringAsync(entry.Id, CameraSettings.UsernameKey).ConfigureAwait(false),
				[CameraSettings.FpsKey] = await context.Config.GetStringAsync(entry.Id, CameraSettings.FpsKey).ConfigureAwait(false),
				[CameraSettings.MaxWidthKey] = await context.Config.GetStringAsync(entry.Id, CameraSettings.MaxWidthKey).ConfigureAwait(false),
				[CameraSettings.RtspTransportKey] = await context.Config.GetStringAsync(entry.Id, CameraSettings.RtspTransportKey).ConfigureAwait(false)
			};

			var password = await context.Config
				.GetSecretAsync(entry.Id, CameraSettings.PasswordKey)
				.ConfigureAwait(false);

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
}
