using System.Net.Http.Headers;
using IpCamera.Cameras;
using System.Text;

namespace IpCamera.Streaming;

/// <summary>Polls a camera's JPEG snapshot URL and turns the responses into a frame stream.</summary>
internal sealed class SnapshotFrameSource(HttpClient httpClient, CameraDefinition camera) : IFrameSource
{
	private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);

	public async IAsyncEnumerable<byte[]> ReadFramesAsync(
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var interval = TimeSpan.FromSeconds(1.0 / camera.FramesPerSecond);
		using var timer = new PeriodicTimer(interval);

		do
		{
			yield return await FetchAsync(cancellationToken).ConfigureAwait(false);
		}
		while (await WaitAsync(timer, cancellationToken).ConfigureAwait(false));
	}

	private async Task<byte[]> FetchAsync(CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_requestTimeout);

		try
		{
			using var request = CameraHttp.CreateRequest(camera);
			using var response = await httpClient
				.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
				.ConfigureAwait(false);

			response.EnsureSuccessStatusCode();
			var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);

			return JpegInfo.IsJpeg(bytes)
				? bytes
				: throw new FrameSourceException("The snapshot URL did not return a JPEG image.");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new FrameSourceException("The snapshot request timed out.");
		}
	}

	private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
	{
		try
		{
			return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}

/// <summary>Reads one long-lived multipart MJPEG response and forwards its JPEG parts.</summary>
internal sealed class MjpegFrameSource(HttpClient httpClient, CameraDefinition camera) : IFrameSource
{
	private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(15);

	public async IAsyncEnumerable<byte[]> ReadFramesAsync(
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		using var request = CameraHttp.CreateRequest(camera);
		using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		connect.CancelAfter(_connectTimeout);

		HttpResponseMessage response;
		try
		{
			response = await httpClient
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new FrameSourceException("The MJPEG stream did not answer in time.");
		}

		using (response)
		{
			response.EnsureSuccessStatusCode();
			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			await foreach (var frame in JpegFrameReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
			{
				yield return frame;
			}
		}
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}

internal static class CameraHttp
{
	public static HttpRequestMessage CreateRequest(CameraDefinition camera)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, camera.Url);
		request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

		if (!string.IsNullOrEmpty(camera.Username))
		{
			var raw = $"{camera.Username}:{camera.Password}";
			request.Headers.Authorization = new AuthenticationHeaderValue(
				"Basic",
				Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
		}

		return request;
	}
}
