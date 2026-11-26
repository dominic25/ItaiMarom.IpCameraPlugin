using System.Net;
using IpCamera.Streaming;
using Serilog;

namespace IpCamera.Cameras;

internal enum CameraProbeFailure
{
	None,
	Unreachable,
	Unauthorized,
	HttpError,
	NotJpeg,
	NoFrame
}

internal sealed record CameraProbeResult(CameraProbeFailure Failure)
{
	public static readonly CameraProbeResult Success = new(CameraProbeFailure.None);

	/// <summary>The size of the frame the probe read, when it read one.</summary>
	public int? Width { get; init; }

	public int? Height { get; init; }

	public bool Succeeded => Failure == CameraProbeFailure.None;
}

/// <summary>Checks that a camera answers with something the frame sources can actually use.</summary>
internal interface ICameraProbe
{
	Task<CameraProbeResult> ProbeAsync(CameraDefinition camera, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches one picture from a snapshot or MJPEG URL. This catches the frequent mistakes during setup:
/// a wrong host, rejected credentials, an HTML page instead of a camera, or a go2rtc stream that still
/// offers H.265 only and therefore never sends a JPEG frame.
/// </summary>
internal sealed class CameraProbe(
	IHttpClientFactory httpClients,
	ILogger logger,
	TimeSpan? timeout = null) : ICameraProbe
{
	private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(10);

	private readonly TimeSpan _timeout = timeout ?? _defaultTimeout;

	public async Task<CameraProbeResult> ProbeAsync(CameraDefinition camera, CancellationToken cancellationToken)
	{
		if (camera.Kind == CameraSourceKind.Rtsp)
		{
			// RTSP needs a full ffmpeg probe to be meaningful, which the setup flow leaves to runtime.
			return CameraProbeResult.Success;
		}

		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(_timeout);

		var headersReceived = false;

		try
		{
			using var request = CameraHttp.CreateRequest(camera);
			using var response = await httpClients
				.CreateClient(CameraSourceFactory.HttpClientName)
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
				.ConfigureAwait(false);

			headersReceived = true;

			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				return new CameraProbeResult(CameraProbeFailure.Unauthorized);
			}

			if (!response.IsSuccessStatusCode)
			{
				logger.Information("Camera probe got HTTP {Status} from {Host}.", (int)response.StatusCode, camera.Url.Host);
				return new CameraProbeResult(CameraProbeFailure.HttpError);
			}

			var mediaType = response.Content.Headers.ContentType?.MediaType;
			if (IsText(mediaType) || (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
				&& !mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)))
			{
				return new CameraProbeResult(CameraProbeFailure.NotJpeg);
			}

			await using var stream = await response.Content
				.ReadAsStreamAsync(deadline.Token)
				.ConfigureAwait(false);

			await foreach (var frame in JpegFrameReader.ReadAsync(stream, deadline.Token).ConfigureAwait(false))
			{
				if (!JpegInfo.IsJpeg(frame))
				{
					return new CameraProbeResult(CameraProbeFailure.NotJpeg);
				}

				return JpegInfo.TryReadSize(frame, out var width, out var height)
					? new CameraProbeResult(CameraProbeFailure.None) { Width = width, Height = height }
					: CameraProbeResult.Success;
			}

			return new CameraProbeResult(CameraProbeFailure.NoFrame);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			logger.Information("Camera probe timed out for {Host}.", camera.Url.Host);
			return new CameraProbeResult(headersReceived ? CameraProbeFailure.NoFrame : CameraProbeFailure.Unreachable);
		}
		catch (HttpRequestException exception)
		{
			logger.Information("Camera probe failed for {Host}: {Reason}", camera.Url.Host, exception.Message);
			return new CameraProbeResult(CameraProbeFailure.Unreachable);
		}
		catch (IOException exception)
		{
			logger.Information("Camera probe connection failed for {Host}: {Reason}", camera.Url.Host, exception.Message);
			return new CameraProbeResult(headersReceived ? CameraProbeFailure.NoFrame : CameraProbeFailure.Unreachable);
		}
	}

	private static bool IsText(string? mediaType)
	{
		return mediaType is not null &&
			(mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
			 mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
			 mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase));
	}
}
