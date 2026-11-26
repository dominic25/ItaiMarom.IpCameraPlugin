using System.Text.Json;
using IpCamera.Actions;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Scripts;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Runtime;

namespace IpCamera.Widgets;

/// <summary>
/// Wraps the view session so a press on the picture acts from the UI event origin. With a press script it
/// runs that script on the pressed widget; otherwise it opens that camera's modal. The widget's own
/// Short Press flow cannot run here because the arrow controls claim the tile press.
/// </summary>
internal sealed class CameraModalSession : IUiSession, IAsyncDisposable
{
	private readonly ViewSession _inner;
	private readonly IReadOnlyDictionary<string, ModalDefinition> _modals;
	private readonly string? _pressScript;
	private readonly IScriptApi? _scripts;
	private readonly string? _widgetId;

	public CameraModalSession(
		UiView view,
		IReadOnlyDictionary<string, string> openTargets,
		bool controls = true,
		double controlsOpacity = 0.6,
		string? pressScript = null,
		IScriptApi? scripts = null,
		string? widgetId = null,
		Action? update = null)
	{
		_inner = new ViewSession(view, update);
		_pressScript = pressScript;
		_scripts = scripts;
		_widgetId = widgetId;
		_modals = openTargets.ToDictionary(
			pair => pair.Key,
			pair => CreateModal(pair.Value, controls, controlsOpacity),
			StringComparer.Ordinal);
	}

	public event EventHandler? Changed
	{
		add => _inner.Changed += value;
		remove => _inner.Changed -= value;
	}

	public event EventHandler<UiSessionFaultedEventArgs>? Faulted
	{
		add => _inner.Faulted += value;
		remove => _inner.Faulted -= value;
	}

	public UiTree BuildTree()
	{
		return _inner.BuildTree();
	}

	public IReadOnlyList<UiPatch> DrainPatches()
	{
		return _inner.DrainPatches();
	}

	public void Dispatch(UiEvent uiEvent)
	{
		_inner.Dispatch(uiEvent);
	}

	public void Dispatch(UiEvent uiEvent, IUiEventOrigin origin)
	{
		if (uiEvent.Name == UiComponentEvents.Press)
		{
			foreach (var pair in _modals)
			{
				if (uiEvent.NodeId.EndsWith(pair.Key, StringComparison.Ordinal))
				{
					if (!string.IsNullOrEmpty(_pressScript))
					{
						_ = RunScriptAsync(origin, _pressScript);
					}
					else
					{
						_ = OpenAsync(origin, pair.Value);
					}

					return;
				}
			}
		}

		_inner.Dispatch(uiEvent, origin);
	}

	public ValueTask DisposeAsync()
	{
		return _inner.DisposeAsync();
	}

	private async Task RunScriptAsync(IUiEventOrigin origin, string scriptId)
	{
		try
		{
			if (_scripts is null)
			{
				await origin
					.ReportFailureAsync(Strings.Widgets.Camera.PressScript.Unavailable(), CancellationToken.None)
					.ConfigureAwait(false);
				return;
			}

			var result = await _scripts
				.RunAsync(scriptId, null, origin.ClientId, _widgetId, CancellationToken.None)
				.ConfigureAwait(false);

			if (result.Status != ActionResultStatus.Succeeded)
			{
				await origin
					.ReportFailureAsync(Strings.Widgets.Camera.PressScript.Failed(), CancellationToken.None)
					.ConfigureAwait(false);
			}
		}
		catch (Exception)
		{
			// The screen or the script may have gone away between the press and the run.
		}
	}

	private static ModalDefinition CreateModal(
		string cameraId,
		bool controls,
		double controlsOpacity)
	{
		return new ModalDefinition
		{
			ViewId = ShowCameraAction.ModalViewId,
			Title = Strings.Actions.ShowCamera.ModalTitle(),
			Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				[CameraWidget.CameraKey] = JsonSerializer.SerializeToElement(cameraId),
				[CameraWidget.ControlsKey] = JsonSerializer.SerializeToElement(controls),
				[CameraWidget.ControlsOpacityKey] =
					JsonSerializer.SerializeToElement(Math.Clamp(controlsOpacity, 0, 1) * 100)
			}
		};
	}

	private static async Task OpenAsync(IUiEventOrigin origin, ModalDefinition modal)
	{
		try
		{
			if (!await origin.ShowModalAsync(modal, CancellationToken.None).ConfigureAwait(false))
			{
				await origin
					.ReportFailureAsync(Strings.Actions.ShowCamera.OpenFailed(), CancellationToken.None)
					.ConfigureAwait(false);
			}
		}
		catch (Exception)
		{
			// The screen may have gone away between the press and the modal; there is nothing to recover.
		}
	}
}
