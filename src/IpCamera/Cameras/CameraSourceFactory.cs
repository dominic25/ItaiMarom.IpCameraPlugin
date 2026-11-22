using IpCamera.Streaming;

namespace IpCamera.Cameras;

/// <summary>Builds the frame source that fits a camera's configured kind.</summary>
internal sealed class CameraSourceFactory(IHttpClientFactory httpClients, FfmpegLocator ffmpeg)
{
	public const string HttpClientName = "ip-camera";

	public IFrameSource Create(CameraDefinition camera)
	{
		return camera.Kind switch
		{
			CameraSourceKind.Rtsp => new RtspFrameSource(ffmpeg, camera),
			CameraSourceKind.Mjpeg => new MjpegFrameSource(httpClients.CreateClient(HttpClientName), camera),
			_ => new SnapshotFrameSource(httpClients.CreateClient(HttpClientName), camera)
		};
	}
}
