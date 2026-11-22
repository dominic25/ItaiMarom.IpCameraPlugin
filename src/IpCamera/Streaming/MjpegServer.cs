using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace IpCamera.Streaming;

/// <summary>Where Macro Deck can fetch a camera's MJPEG relay.</summary>
internal interface IMjpegEndpoint
{
	string UrlFor(string cameraId);
}

/// <summary>
/// A minimal HTTP server on the loopback interface, so the frames are reachable by Macro Deck on this
/// computer only. It speaks just enough HTTP/1.1 for the relay: one GET per connection, a
/// <c>multipart/x-mixed-replace</c> response that lasts until the client leaves, and a health reply.
/// </summary>
internal sealed class MjpegServer(Func<string, IFrameStream?> resolve, ILogger logger)
	: IMjpegEndpoint, IAsyncDisposable
{
	private const string CameraPath = "/camera/";
	private const string Boundary = "frame";
	private const int MaxHeaderBytes = 16 * 1024;

	private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
	private readonly CancellationTokenSource _shutdown = new();
	private readonly object _gate = new();
	private readonly HashSet<Task> _clients = [];
	private Task? _acceptLoop;
	private bool _started;

	public int Port { get; private set; }

	public Task StartAsync(CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			if (_started)
			{
				return Task.CompletedTask;
			}

			_listener.Start();
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
			_started = true;
			_acceptLoop = AcceptAsync(_shutdown.Token);
		}

		logger.Debug("Camera relay listening on 127.0.0.1:{Port}", Port);
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}

	public string UrlFor(string cameraId)
	{
		return $"http://127.0.0.1:{Port}{CameraPath}{Uri.EscapeDataString(cameraId)}";
	}

	private async Task AcceptAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (SocketException)
			{
				return;
			}
			catch (ObjectDisposedException)
			{
				return;
			}

			_ = HandleTrackedAsync(client, cancellationToken);
		}
	}

	private async Task HandleTrackedAsync(TcpClient client, CancellationToken cancellationToken)
	{
		var task = HandleAsync(client, cancellationToken);
		lock (_gate)
		{
			_clients.Add(task);
		}

		try
		{
			await task.ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			logger.Debug("Camera relay client ended: {Reason}", exception.Message);
		}
		finally
		{
			lock (_gate)
			{
				_clients.Remove(task);
			}
		}
	}

	private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
	{
		using (client)
		{
			client.NoDelay = true;
			var stream = client.GetStream();

			using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			headerTimeout.CancelAfter(TimeSpan.FromSeconds(15));

			string? method;
			string? path;
			try
			{
				var request = await ReadRequestAsync(stream, headerTimeout.Token).ConfigureAwait(false);
				method = request?.Method;
				path = request?.Path;
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (IOException)
			{
				return;
			}

			if (method is null || path is null)
			{
				await WriteStatusAsync(stream, "400 Bad Request", cancellationToken).ConfigureAwait(false);
				return;
			}

			var headOnly = method == "HEAD";

			if (path == "/health")
			{
				if (headOnly)
				{
					await WriteStatusAsync(stream, "200 OK", cancellationToken).ConfigureAwait(false);
				}
				else
				{
					await WriteHealthAsync(stream, cancellationToken).ConfigureAwait(false);
				}

				return;
			}

			var cameraId = ParseCameraId(path);
			var frames = cameraId is null ? null : resolve(cameraId);
			if (frames is null)
			{
				await WriteStatusAsync(stream, "404 Not Found", cancellationToken).ConfigureAwait(false);
				return;
			}

			if (headOnly)
			{
				await WriteStreamHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
				return;
			}

			try
			{
				using var subscription = frames.Subscribe();
				await WriteStreamHeadersAsync(stream, cancellationToken).ConfigureAwait(false);

				if (frames.CurrentFrame is { } first)
				{
					await WritePartAsync(stream, first, cancellationToken).ConfigureAwait(false);
				}

				while (await subscription.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
				{
					while (subscription.Reader.TryRead(out var frame))
					{
						await WritePartAsync(stream, frame, cancellationToken).ConfigureAwait(false);
					}
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (IOException)
			{
			}
		}
	}

	private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		var buffer = new byte[1024];
		using var header = new MemoryStream();

		while (header.Length < MaxHeaderBytes)
		{
			var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				return null;
			}

			header.Write(buffer, 0, read);
			var text = Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length);
			var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
			if (headerEnd < 0)
			{
				continue;
			}

			var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
			var requestLine = lineEnd < 0 ? text : text[..lineEnd];
			var parts = requestLine.Split(' ');
			if (parts.Length < 2 || parts[0] is not ("GET" or "HEAD") || !parts[1].StartsWith('/'))
			{
				return null;
			}

			var target = parts[1];
			var query = target.IndexOf('?', StringComparison.Ordinal);
			return new HttpRequest(parts[0], query < 0 ? target : target[..query]);
		}

		return null;
	}

	private readonly record struct HttpRequest(string Method, string Path);

	private static string? ParseCameraId(string path)
	{
		if (!path.StartsWith(CameraPath, StringComparison.Ordinal))
		{
			return null;
		}

		var raw = path[CameraPath.Length..];
		if (raw.Length == 0 || raw.Contains('/', StringComparison.Ordinal))
		{
			return null;
		}

		try
		{
			return Uri.UnescapeDataString(raw);
		}
		catch (UriFormatException)
		{
			return null;
		}
	}

	private static async Task WriteStreamHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		var headers =
			"HTTP/1.1 200 OK\r\n" +
			$"Content-Type: multipart/x-mixed-replace; boundary={Boundary}\r\n" +
			"Cache-Control: no-store, no-cache, must-revalidate\r\n" +
			"Pragma: no-cache\r\n" +
			"Connection: close\r\n" +
			"\r\n";

		await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task WritePartAsync(NetworkStream stream, byte[] frame, CancellationToken cancellationToken)
	{
		var partHeader = Encoding.ASCII.GetBytes(
			$"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");

		await stream.WriteAsync(partHeader, cancellationToken).ConfigureAwait(false);
		await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
		await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken cancellationToken)
	{
		var response = Encoding.ASCII.GetBytes(
			$"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

		await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
	}

	private static async Task WriteHealthAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		var response = Encoding.ASCII.GetBytes(
			"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");

		await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		Task? acceptLoop;
		Task[] clients;

		lock (_gate)
		{
			acceptLoop = _acceptLoop;
			clients = [.. _clients];
			_clients.Clear();
		}

		await _shutdown.CancelAsync().ConfigureAwait(false);
		_listener.Stop();

		if (acceptLoop is not null)
		{
			try
			{
				await acceptLoop.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		try
		{
			await Task.WhenAll(clients).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}

		_shutdown.Dispose();
	}
}
