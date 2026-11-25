using IpCamera.Cameras;
using IpCamera.Streaming;
using MacroDeck.Localization;
using MacroDeck.Sdk.VideoStreams;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;

namespace IpCamera.Widgets;

/// <summary>The integration page that lists every camera and lets the user restart one.</summary>
internal sealed class CameraHealthView
{
	private const string LiveColor = "#22c55e";
	private const string PendingColor = "#f59e0b";
	private const string OfflineColor = "#ef4444";

	private readonly CameraRegistry _registry;
	private readonly List<Row> _rows;

	public CameraHealthView(CameraRegistry registry)
	{
		_registry = registry;
		_rows = registry.Cameras
			.Select((camera, index) => new Row(camera, registry, index))
			.ToList();
	}

	public UiStack Build()
	{
		var children = new List<UiElement>
		{
			new UiTextRun
			{
				Key = "health-title",
				Text = Strings.Health.Title(),
				Size = 0.06,
				MinSize = 0.04,
				MaxLines = 1
			},
			new UiTextRun
			{
				Key = "health-description",
				Text = Strings.Health.Description(),
				Size = 0.03,
				MinSize = 0.022,
				Role = UiValue.Of(UiComponentTextRoles.Secondary),
				MaxLines = 2,
				Wrap = UiValue.Of(true)
			}
		};

		if (_rows.Count == 0)
		{
			children.Add(new UiTextRun
			{
				Key = "health-empty",
				Text = Strings.Health.Empty(),
				Size = 0.035,
				MinSize = 0.025,
				Role = UiValue.Of(UiComponentTextRoles.Secondary),
				MaxLines = 2,
				Wrap = UiValue.Of(true)
			});
		}
		else
		{
			children.AddRange(_rows.Select(row => row.Build()));
		}

		return new UiStack
		{
			Key = "health-page",
			Fill = true,
			Direction = UiValue.Of(UiComponentDirections.Vertical),
			Padding = 0.04,
			Gap = 0.02,
			Background = UiValue.Of("#0b0f14"),
			Children = children
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
			else
			{
				_state.Set(Strings.Health.Connecting());
				_stateColor.Set(PendingColor);
			}

			_details.Set(Details(camera, _stream));
		}

		public UiStack Build()
		{
			return new UiStack
			{
				Key = $"health-row-{index}",
				Direction = UiValue.Of(UiComponentDirections.Horizontal),
				Fill = true,
				Align = UiValue.Of(UiComponentAlignments.Center),
				Gap = 0.02,
				Padding = 0.025,
				Background = UiValue.Of("#11161d"),
				Children =
				[
					new UiStack
					{
						Key = $"health-name-{index}",
						Fill = true,
						Direction = UiValue.Of(UiComponentDirections.Vertical),
						Gap = 0.006,
						Children =
						[
							new UiTextRun
							{
								Key = $"health-name-text-{index}",
								Text = camera.Name,
								Size = 0.04,
								MinSize = 0.028,
								MaxLines = 1
							},
							new UiTextRun
							{
								Key = $"health-details-{index}",
								Text = UiText.From(() => _details.Value),
								Size = 0.028,
								MinSize = 0.02,
								Role = UiValue.Of(UiComponentTextRoles.Secondary),
								MaxLines = 1
							}
						]
					},
					new UiTextRun
					{
						Key = $"health-state-{index}",
						Text = UiText.FromLocalized(() => _state.Value),
						Size = 0.035,
						MinSize = 0.024,
						Color = UiValue.From(() => _stateColor.Value),
						MaxLines = 1
					},
					new UiButton
					{
						Key = $"health-retry-{index}",
						MainSize = UiSize.FromBasis(0.12),
						Events =
						[
							UiEventHandler.OnAsync(
								UiComponentEvents.Press,
								async cancellationToken =>
								{
									if (_stream is not null)
									{
										await _stream.RestartAsync().ConfigureAwait(false);
									}
								})
						],
						Children =
						[
							new UiTextRun
							{
								Key = $"health-retry-text-{index}",
								Text = Strings.Health.Retry(),
								Size = 0.03,
								MinSize = 0.022,
								Align = UiValue.Of(UiComponentAlignments.Center),
								Color = UiValue.Of("#ffffff"),
								MaxLines = 1
							}
						]
					}
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
				return $"{kind} · {size} · no frame";
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
