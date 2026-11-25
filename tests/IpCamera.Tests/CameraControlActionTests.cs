using IpCamera.Actions;
using IpCamera.Cameras;
using IpCamera.Onvif;
using IpCamera.Streaming;
using IpCamera.Xiaomi;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.VideoStreams;
using NUnit.Framework;
using Serilog;

namespace IpCamera.Tests;

[TestFixture]
public sealed class CameraControlActionTests
{
	private const string Token = "00112233445566778899aabbccddeeff";

	private static readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	[Test]
	public async Task A_move_uses_the_camera_chosen_in_the_action()
	{
		var camera = Camera(CameraControlKind.Xiaomi);
		await using var registry = new CameraRegistry(_ => new StubStream(camera));
		await registry.SetCamerasAsync([camera], CancellationToken.None);

		var miio = new FakeMiio();
		var result = await ExecuteAsync(registry, miio, new FakeOnvif(), "move-left", camera.Id);

		Assert.Multiple(() =>
		{
			Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
			Assert.That(miio.Motors, Has.Count.EqualTo(1));
			Assert.That(miio.Motors[0], Is.EqualTo(1));
		});
	}

	[Test]
	public async Task Night_vision_uses_the_xiaomi_night_mode_numbers()
	{
		var camera = Camera(CameraControlKind.Xiaomi);
		await using var registry = new CameraRegistry(_ => new StubStream(camera));
		await registry.SetCamerasAsync([camera], CancellationToken.None);

		var miio = new FakeMiio();
		var on = await ExecuteAsync(registry, miio, new FakeOnvif(), "ir-on", camera.Id);
		var off = await ExecuteAsync(registry, miio, new FakeOnvif(), "ir-off", camera.Id);
		var auto = await ExecuteAsync(registry, miio, new FakeOnvif(), "ir-auto", camera.Id);

		Assert.Multiple(() =>
		{
			Assert.That(on.Status, Is.EqualTo(ActionResultStatus.Succeeded));
			Assert.That(off.Status, Is.EqualTo(ActionResultStatus.Succeeded));
			Assert.That(auto.Status, Is.EqualTo(ActionResultStatus.Succeeded));
			Assert.That(miio.NightModes, Has.Count.EqualTo(3));
			Assert.That(miio.NightModes[0], Is.EqualTo(2));
			Assert.That(miio.NightModes[1], Is.EqualTo(1));
			Assert.That(miio.NightModes[2], Is.EqualTo(0));
		});
	}

	[Test]
	public async Task Stop_on_a_xiaomi_camera_succeeds_without_a_command()
	{
		var camera = Camera(CameraControlKind.Xiaomi);
		await using var registry = new CameraRegistry(_ => new StubStream(camera));
		await registry.SetCamerasAsync([camera], CancellationToken.None);

		var miio = new FakeMiio();
		var result = await ExecuteAsync(registry, miio, new FakeOnvif(), "ptz-stop", camera.Id);

		Assert.Multiple(() =>
		{
			Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
			Assert.That(miio.Motors, Is.Empty);
			Assert.That(miio.NightModes, Is.Empty);
		});
	}

	[Test]
	public async Task An_onvif_camera_still_uses_the_onvif_service()
	{
		var camera = Camera(CameraControlKind.Onvif);
		await using var registry = new CameraRegistry(_ => new StubStream(camera));
		await registry.SetCamerasAsync([camera], CancellationToken.None);

		var onvif = new FakeOnvif();
		var result = await ExecuteAsync(registry, new FakeMiio(), onvif, "move-right", camera.Id);

		Assert.Multiple(() =>
		{
			Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Succeeded));
			Assert.That(onvif.Directions, Has.Count.EqualTo(1));
			Assert.That(onvif.Directions[0], Is.EqualTo("right"));
			Assert.That(onvif.LastAddress, Is.EqualTo("http://192.168.1.50/onvif/device_service"));
		});
	}

	[Test]
	public async Task A_xiaomi_camera_without_its_token_asks_for_the_settings()
	{
		var camera = Camera(CameraControlKind.Xiaomi, withToken: false);
		await using var registry = new CameraRegistry(_ => new StubStream(camera));
		await registry.SetCamerasAsync([camera], CancellationToken.None);

		var result = await ExecuteAsync(registry, new FakeMiio(), new FakeOnvif(), "move-left", camera.Id);

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Failed));
		Assert.That(result.ErrorCode, Is.EqualTo(ActionErrorCodes.NotConfigured));
	}

	[Test]
	public async Task A_camera_that_was_removed_fails_instead_of_falling_back()
	{
		var camera = Camera(CameraControlKind.Onvif);
		await using var registry = new CameraRegistry(_ => new StubStream(camera));
		await registry.SetCamerasAsync([camera], CancellationToken.None);

		var result = await ExecuteAsync(registry, new FakeMiio(), new FakeOnvif(), "move-left", "missing");

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Failed));
		Assert.That(result.ErrorCode, Is.EqualTo(ActionErrorCodes.NotConfigured));
	}

	[Test]
	public async Task A_disabled_camera_reports_that_controls_are_off()
	{
		var camera = Camera(CameraControlKind.None);
		await using var registry = new CameraRegistry(_ => new StubStream(camera));
		await registry.SetCamerasAsync([camera], CancellationToken.None);

		var result = await ExecuteAsync(registry, new FakeMiio(), new FakeOnvif(), "move-left", camera.Id);

		Assert.That(result.Status, Is.EqualTo(ActionResultStatus.Failed));
		Assert.That(result.ErrorCode, Is.EqualTo(ActionErrorCodes.Unavailable));
	}

	[Test]
	public async Task The_camera_options_list_the_configured_cameras()
	{
		var camera = Camera(CameraControlKind.Onvif);
		await using var registry = new CameraRegistry(_ => new StubStream(camera));
		await registry.SetCamerasAsync([camera], CancellationToken.None);

		var action = CameraControlAction
			.Create(() => null, registry, new FakeOnvif(), new FakeMiio(), _logger)
			.OfType<IDynamicOptionsActionDefinition>()
			.First();

		var options = await action.GetDynamicOptionsAsync(
			new DynamicOptionsContext
			{
				ParameterName = "camera",
				CurrentParameters = new Dictionary<string, object?>()
			},
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(options.Options, Has.Count.EqualTo(2));
			Assert.That(options.Options[0].Value, Is.Empty);
			Assert.That(options.Options[1].Value, Is.EqualTo(camera.Id));
		});
	}

	[OneTimeTearDown]
	public void DisposeLogger()
	{
		(_logger as IDisposable)?.Dispose();
	}

	private static CameraDefinition Camera(CameraControlKind control, bool withToken = true)
	{
		return new CameraDefinition(
			"camera-1",
			"mi360",
			CameraSourceKind.Mjpeg,
			new Uri("http://192.168.1.60:1984/api/stream.mjpeg?src=mi360_mjpeg"),
			null,
			null,
			5,
			640,
			"tcp")
		{
			Control = control,
			ControlHost = control == CameraControlKind.None ? null : "192.168.1.50",
			ControlToken = control == CameraControlKind.Xiaomi && withToken ? Token : null
		};
	}

	private static Task<ActionResult> ExecuteAsync(
		CameraRegistry registry,
		FakeMiio miio,
		FakeOnvif onvif,
		string actionId,
		string cameraId)
	{
		var action = CameraControlAction
			.Create(() => null, registry, onvif, miio, _logger)
			.Single(candidate => candidate.Id == actionId);

		return action.CreateExecutor().ExecuteAsync(new ActionExecutionContext
		{
			Parameters = new Dictionary<string, object> { ["camera"] = cameraId },
			CancellationToken = CancellationToken.None
		});
	}

	private sealed class FakeMiio : IMiioClient
	{
		public List<int> Motors { get; } = [];

		public List<int> NightModes { get; } = [];

		public Task<MiioResult> SetMotorAsync(
			MiioEndpoint endpoint,
			int operation,
			CancellationToken cancellationToken)
		{
			Motors.Add(operation);
			return Task.FromResult(MiioResult.Success);
		}

		public Task<MiioResult> SetNightModeAsync(
			MiioEndpoint endpoint,
			int mode,
			CancellationToken cancellationToken)
		{
			NightModes.Add(mode);
			return Task.FromResult(MiioResult.Success);
		}

		public Task<MiioResult> ProbeAsync(MiioEndpoint endpoint, CancellationToken cancellationToken)
		{
			return Task.FromResult(MiioResult.Success);
		}
	}

	private sealed class FakeOnvif : IOnvifClient
	{
		public List<string> Directions { get; } = [];

		public string? LastAddress { get; private set; }

		public Task<OnvifStream?> ResolveAsync(
			OnvifDevice device,
			string? username,
			string? password,
			CancellationToken cancellationToken)
		{
			return Task.FromResult<OnvifStream?>(null);
		}

		public Task<bool> MoveAsync(
			OnvifDevice device,
			string? username,
			string? password,
			string direction,
			double speed,
			CancellationToken cancellationToken)
		{
			Directions.Add(direction);
			LastAddress = device.XAddr;
			return Task.FromResult(true);
		}

		public Task<bool> StopAsync(
			OnvifDevice device,
			string? username,
			string? password,
			CancellationToken cancellationToken)
		{
			LastAddress = device.XAddr;
			return Task.FromResult(true);
		}

		public Task<bool> SetIrAsync(
			OnvifDevice device,
			string? username,
			string? password,
			string mode,
			CancellationToken cancellationToken)
		{
			LastAddress = device.XAddr;
			return Task.FromResult(true);
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
