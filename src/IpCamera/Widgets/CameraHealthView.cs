using IpCamera.Cameras;
using IpCamera.Streaming;
using MacroDeck.Localization;
using MacroDeck.Sdk.VideoStreams;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;

namespace IpCamera.Widgets;

/// <summary>The integration page that lists every camera and lets the user restart one.</summary>
internal sealed class CameraHealthView
{
	private const string LiveColor = "#22c55e";
	private const string PendingColor = "#f59e0b";
	private const string IdleColor = "#94a3b8";
	private const string OfflineColor = "#ef4444";

	private readonly List<Row> _rows;
	private readonly UiState<string?> _selected = new(null);

	public CameraHealthView(CameraRegistry registry)
	{
		_rows = registry.Cameras
			.Select((camera, index) => new Row(camera, registry, index))
			.ToList();

		if (_rows.Count > 0)
		{
			_selected.Set(_rows[0].Camera.Id);
		}
	}

	private Row? Selected =>
		_rows.FirstOrDefault(row => row.Camera.Id == _selected.Value) ?? _rows.FirstOrDefault();

	public UiSplit Build()
	{
		var rail = _rows.Select(row => row.BuildListItem(_selected)).ToList();

		UiElement detail;
		if (_rows.Count == 0)
		{
			detail = new UiEmptyState
			{
				Key = "health-empty",
				Icon = UiValue.Of("camera"),
				Title = Strings.Health.Title(),
				Text = Strings.Health.Empty()
			};
		}
		else
		{
			detail = new UiConfigStack
			{
				Key = "health-detail",
				Gap = UiValue.Of(UiStackGaps.Medium),
				Children =
				[
					new UiHeading
					{
						Key = "health-detail-title",
						Text = UiText.From(() => Selected?.Camera.Name ?? string.Empty)
					},
					new UiTextRun
					{
						Key = "health-detail-details",
						Text = UiText.From(() => Selected?.Summary ?? string.Empty),
						Size = 0.032,
						MinSize = 0.022,
						Role = UiValue.Of(UiComponentTextRoles.Secondary),
						MaxLines = 2,
						Wrap = UiValue.Of(true)
					},
					new UiTextRun
					{
						Key = "health-detail-state",
						Text = UiText.FromLocalized(() => Selected?.State ?? Strings.Health.Connecting()),
						Size = 0.04,
						MinSize = 0.028,
						Color = UiValue.From(() => Selected?.Color ?? PendingColor),
						MaxLines = 1
					},
					new UiConfigButton
					{
						Key = "health-retry",
						Label = Strings.Health.Retry(),
						Variant = UiValue.Of(UiButtonVariants.Primary),
						Events =
						[
							UiEventHandler.OnAsync(
								UiConfigEvents.Activate,
								async cancellationToken =>
								{
									if (Selected?.Stream is { } stream)
									{
										cancellationToken.ThrowIfCancellationRequested();
										await stream.RestartAsync().ConfigureAwait(false);
									}
								})
						]
					}
				]
			};
		}

		return new UiSplit
		{
			Key = "health-page",
			RailTitle = Strings.Health.Title(),
			Rail =
			[
				new UiConfigList
				{
					Key = "health-list",
					Label = Strings.Health.Title(),
					Children = rail
				}
			],
			Detail = [detail]
		};
	}

	public void Update()
	{
		foreach (var row in _rows)
		{
			row.Update();
		}
	}

	private sealed class Row(CameraDefinition camera, CameraRegistry registry, int index)
	{
		private readonly UiState<LocalizedString> _state = new(Strings.Health.Connecting());
		private readonly UiState<string> _stateColor = new(PendingColor);
		private readonly UiState<string> _details = new(Details(camera, null));

		private IFrameStream? _stream;

		public CameraDefinition Camera => camera;

		public LocalizedString State => _state.Value;

		public string Color => _stateColor.Value;

		public string Summary => _details.Value;

		public IFrameStream? Stream => _stream;

		public void Update()
		{
			_stream = registry.Find(camera.Id);

			if (_stream is null)
			{
				_state.Set(Strings.Health.Connecting());
				_stateColor.Set(PendingColor);
				_details.Set(Details(camera, null));
				return;
			}

			if (_stream.HasFailed)
			{
				_state.Set(Strings.Health.Offline());
				_stateColor.Set(OfflineColor);
			}
			else if (_stream.State == VideoStreamState.Connected)
			{
				_state.Set(Strings.Health.Live());
				_stateColor.Set(LiveColor);
			}
			else if (_stream.State == VideoStreamState.Disconnected)
			{
				_state.Set(Strings.Health.Idle());
				_stateColor.Set(IdleColor);
			}
			else
			{
				_state.Set(Strings.Health.Connecting());
				_stateColor.Set(PendingColor);
			}

			_details.Set(Details(camera, _stream));
		}

		public UiConfigListItem BuildListItem(UiState<string?> selected)
		{
			return new UiConfigListItem
			{
				Key = $"health-item-{index}",
				Label = camera.Name,
				Description = UiText.FromLocalized(() => _state.Value),
				Severity = UiValue.From(() => _stateColor.Value switch
				{
					LiveColor => "success",
					OfflineColor => "error",
					_ => "warning"
				}),
				Selected = UiValue.From(() => selected.Value == camera.Id),
				Events =
				[
					UiEventHandler.On(UiConfigEvents.Activate, () => selected.Value = camera.Id)
				]
			};
		}

		private static string Details(CameraDefinition camera, IFrameStream? stream)
		{
			var kind = camera.Kind switch
			{
				CameraSourceKind.Rtsp => "RTSP",
				CameraSourceKind.Mjpeg => "MJPEG",
				_ => "Snapshot"
			};

			var size = stream is { Width: { } width, Height: { } height }
				? $"{width}x{height}"
				: "no frame";

			if (stream?.LastFrameAt is not { } lastFrameAt)
			{
				return $"{kind} · {size}";
			}

			var age = DateTimeOffset.UtcNow - lastFrameAt;
			var ageText = age < TimeSpan.FromMinutes(1)
				? $"{(int)Math.Max(0, age.TotalSeconds)}s"
				: age < TimeSpan.FromHours(1)
					? $"{(int)age.TotalMinutes}m"
					: $"{(int)age.TotalHours}h";

			return $"{kind} · {size} · {ageText} ago";
		}
	}
}
