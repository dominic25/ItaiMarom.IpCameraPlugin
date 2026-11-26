using System.Text.Json;
using IpCamera.Cameras;
using IpCamera.Streaming;
using IpCamera.Widgets;
using MacroDeck.Localization;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.VideoStreams;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraModalSessionTests
{
	private const string ProviderId = "test-plugin::cameras";

	[Test]
	public async Task A_press_on_the_picture_opens_that_camera()
	{
		var surface = Surface(new { camera = "camera-1" });
		var camera = Camera();
		var view = CameraWidget.BuildView(surface, ProviderId, _ => new StubStream(camera));
		var origin = new StubOrigin();

		await using var session = new CameraModalSession(
			new UiView(surface, view.Root),
			view.OpenTargets);

		session.Dispatch(
			new UiEvent { NodeId = "root.layer.open-camera", Name = UiComponentEvents.Press },
			origin);

		await Task.Delay(20);

		Assert.That(origin.Modal, Is.Not.Null);
		Assert.That(origin.Modal!.ViewId, Is.EqualTo("camera"));
		Assert.That(origin.Modal.Data!["camera"].GetString(), Is.EqualTo("camera-1"));
	}

	private static CameraDefinition Camera()
	{
		return new CameraDefinition(
			"camera-1",
			"Test camera",
			CameraSourceKind.Mjpeg,
			new Uri("http://127.0.0.1/camera.mjpeg"),
			null,
			null,
			5,
			640,
			"tcp");
	}

	private static UiSurface Surface(object data)
	{
		return new UiSurface
		{
			Kind = UiSurfaceKinds.Widget,
			SessionMode = UiSessionModes.Shared,
			Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["data"] = JsonSerializer.SerializeToElement(data)
			}
		};
	}

	private sealed class StubOrigin : IUiEventOrigin
	{
		public string ClientId => "client-1";

		public ModalDefinition? Modal { get; private set; }

		public Task<bool> ShowModalAsync(
			ModalDefinition modal,
			CancellationToken cancellationToken = default)
		{
			Modal = modal;
			return Task.FromResult(true);
		}

		public Task<ModalResult<T>> ShowModalAsync<T>(
			ModalDefinition modal,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(ModalResult.FromCancellation<T>());
		}

		public Task<bool> ReportFailureAsync(
			LocalizedText message,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(true);
		}

		public Task<bool> OpenUrlAsync(Uri url, CancellationToken cancellationToken = default)
		{
			return Task.FromResult(true);
		}

		public Task<ReadOnlyMemory<byte>> ReadUploadAsync(
			string uploadId,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult(ReadOnlyMemory<byte>.Empty);
		}
	}

	private sealed class StubStream(CameraDefinition definition) : IFrameStream
	{
		public CameraDefinition Definition => definition;

		public VideoStreamState State => VideoStreamState.Connected;

		public bool HasFailed => false;

		public DateTimeOffset? LastFrameAt => DateTimeOffset.UtcNow;

		public int? Width => 640;

		public int? Height => 360;

		public byte[]? CurrentFrame => null;

		public event EventHandler? StateChanged
		{
			add { }
			remove { }
		}

		public FrameSubscription Subscribe()
		{
			return new FrameSubscription(
				System.Threading.Channels.Channel.CreateBounded<byte[]>(1).Reader,
				() => { });
		}

		public Task RestartAsync()
		{
			return Task.CompletedTask;
		}

		public Task<bool> PrimeAsync(TimeSpan timeout, CancellationToken cancellationToken)
		{
			return Task.FromResult(true);
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}
}
