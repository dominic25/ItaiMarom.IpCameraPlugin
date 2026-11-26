using System.Text.Json;
using IpCamera.Actions;
using MacroDeck.Localization;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Runtime;

namespace IpCamera.Widgets;

/// <summary>
/// Wraps the view session so a press on the picture opens that camera's modal directly from the
/// UI event origin. The widget's default short-press action needs an action client and is blocked by the
/// arrow overlay; the event origin is the connected screen, so this works from the same press.
/// </summary>
internal sealed class CameraModalSession : IUiSession, IAsyncDisposable
{
	private readonly ViewSession _inner;
	private readonly IReadOnlyDictionary<string, ModalDefinition> _modals;

	public CameraModalSession(
		UiView view,
		IReadOnlyDictionary<string, string> openTargets,
		bool controls = true,
		double controlsOpacity = 0.6,
		Action? update = null)
	{
		_inner = new ViewSession(view, update);
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
					_ = OpenAsync(origin, pair.Value);
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
