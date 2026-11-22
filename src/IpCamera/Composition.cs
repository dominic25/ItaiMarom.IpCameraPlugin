using IpCamera.Cameras;
using IpCamera.Streaming;
using MacroDeck.Plugin.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace IpCamera;

/// <summary>Wires the integration and the services it owns into the plugin host.</summary>
internal static class Composition
{
	public static PluginHostBuilder Register(PluginHostBuilder builder)
	{
		builder.Services.AddHttpClient(CameraSourceFactory.HttpClientName, client =>
		{
			// A watched MJPEG response stays open for as long as the widget shows it, so the timeout is
			// per request rather than per client.
			client.Timeout = Timeout.InfiniteTimeSpan;
		});

		builder.Services.AddSingleton<FfmpegLocator>();
		builder.Services.AddSingleton<CameraSourceFactory>();
		builder.Services.AddSingleton(services => new CameraRegistry(
			services.GetRequiredService<CameraSourceFactory>().Create,
			services.GetRequiredService<ILogger>()));
		builder.Services.AddSingleton(services => new MjpegServer(
			cameraId => services.GetRequiredService<CameraRegistry>().Find(cameraId),
			services.GetRequiredService<ILogger>()));

		return builder.RegisterIntegration(services => new PluginIntegration(
			services.GetRequiredService<CameraRegistry>(),
			services.GetRequiredService<MjpegServer>(),
			services.GetRequiredService<FfmpegLocator>(),
			services.GetRequiredService<ILogger>()));
	}
}
