using IpCamera.Cameras;
using IpCamera.Streaming;
using IpCamera.Widgets;
using MacroDeck.Sdk.VideoStreams;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraHealthViewTests
{
	private static CameraDefinition Camera()
	{
		return new CameraDefinition(
			"camera-1",
			"Front door",
			CameraSourceKind.Mjpeg,
			new Uri("http://127.0.0.1/camera.mjpeg"),
			null,
			null,
			5,
			640,
			"tcp");
	}

	[Test]
	public async Task The_page_lists_a_camera_state_and_retry()
	{
		var camera = Camera();
		await using var registry = new CameraRegistry(_ => new StubStream(camera));
		await registry.SetCamerasAsync([camera], CancellationToken.None);

		var page = new CameraHealthView(registry);
		page.Update();

		using var view = new UiView(Surface(), page.Build());

		Assert.Multiple(() =>
		{
			Assert.That(ContainsId(view.Tree.Root, "health-retry-camera-1"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "health-state-camera-1"), Is.True);
			Assert.That(ContainsId(view.Tree.Root, "health-details-camera-1"), Is.True);
		});
	}

	[Test]
	public async Task The_page_says_when_no_cameras_are_configured()
	{
		await using var registry = new CameraRegistry(_ => new StubStream(Camera()));
		await registry.SetCamerasAsync([], CancellationToken.None);

		var page = new CameraHealthView(registry);
		page.Update();

		using var view = new UiView(Surface(), page.Build());

		Assert.That(ContainsId(view.Tree.Root, "health-empty"), Is.True);
	}

	private static UiSurface Surface()
	{
		return new UiSurface
		{
			Kind = UiSurfaceKinds.Page,
			SessionMode = UiSessionModes.Exclusive
		};
	}

	private static bool ContainsId(UiNode node, string id)
	{
		return node.Id.EndsWith(id, StringComparison.Ordinal) ||
			node.Children.Any(child => ContainsId(child, id));
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
