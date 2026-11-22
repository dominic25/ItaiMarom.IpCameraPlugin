using System.Diagnostics;
using IpCamera.Cameras;

namespace IpCamera.Streaming;

/// <summary>
/// Finds the ffmpeg executable used for RTSP sources. The PATH is the normal case, and
/// <c>IPCAM_FFMPEG_PATH</c> overrides it for a portable install the PATH does not see.
/// </summary>
internal sealed class FfmpegLocator
{
	public FfmpegLocator()
		: this(ResolveFromEnvironment())
	{
	}

	internal FfmpegLocator(string? path)
	{
		Path = path;
	}

	public string? Path { get; }

	public bool IsAvailable => Path is not null;

	private static string? ResolveFromEnvironment()
	{
		var configured = Environment.GetEnvironmentVariable("IPCAM_FFMPEG_PATH");
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
		{
			return configured;
		}

		var fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
		var searchPath = Environment.GetEnvironmentVariable("PATH");
		if (searchPath is null)
		{
			return null;
		}

		foreach (var directory in searchPath.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			try
			{
				var candidate = System.IO.Path.Combine(directory.Trim('"'), fileName);
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}
			catch (ArgumentException)
			{
			}
		}

		return null;
	}
}

/// <summary>
/// Converts an RTSP camera into JPEG frames by running ffmpeg and reading its <c>mjpeg</c> output. One
/// process runs per camera while a client watches it.
/// </summary>
internal sealed class RtspFrameSource(FfmpegLocator ffmpeg, CameraDefinition camera) : IFrameSource
{
	public async IAsyncEnumerable<byte[]> ReadFramesAsync(
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (ffmpeg.Path is not { } executable)
		{
			throw new FrameSourceException("ffmpeg was not found on this computer.");
		}

		using var process = Start(executable, cancellationToken);
		var stderrTail = new StderrTail(process.StandardError);

		try
		{
			await foreach (var frame in JpegFrameReader
				.ReadAsync(process.StandardOutput.BaseStream, cancellationToken)
				.ConfigureAwait(false))
			{
				yield return frame;
			}
		}
		finally
		{
			Kill(process);
		}

		var reason = Redact(await stderrTail.ReadAsync().ConfigureAwait(false));
		throw new FrameSourceException($"ffmpeg stopped: {reason}");
	}

	private string Redact(string message)
	{
		if (string.IsNullOrEmpty(camera.Password))
		{
			return message;
		}

		return message.Replace(camera.Password, "***", StringComparison.Ordinal);
	}

	internal static IReadOnlyList<string> BuildArguments(CameraDefinition camera)
	{
		var arguments = new List<string>
		{
			"-hide_banner",
			"-nostdin",
			"-loglevel", "error",
			"-rtsp_transport", camera.RtspTransport,
			"-i", BuildInputUrl(camera),
			"-an",
			"-r", camera.FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture)
		};

		if (camera.MaxWidth > 0)
		{
			arguments.Add("-vf");
			arguments.Add($"scale={camera.MaxWidth}:-2");
		}

		arguments.Add("-f");
		arguments.Add("mjpeg");
		arguments.Add("-q:v");
		arguments.Add("5");
		arguments.Add("pipe:1");

		return arguments;
	}

	private static string BuildInputUrl(CameraDefinition camera)
	{
		if (string.IsNullOrEmpty(camera.Username))
		{
			return camera.Url.ToString();
		}

		var builder = new UriBuilder(camera.Url)
		{
			UserName = camera.Username,
			Password = camera.Password ?? string.Empty
		};

		return builder.Uri.ToString();
	}

	private Process Start(string executable, CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo(executable)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		foreach (var argument in BuildArguments(camera))
		{
			startInfo.ArgumentList.Add(argument);
		}

		var process = new Process { StartInfo = startInfo };

		try
		{
			if (!process.Start())
			{
				process.Dispose();
				throw new FrameSourceException("ffmpeg could not be started.");
			}
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			process.Dispose();
			cancellationToken.ThrowIfCancellationRequested();
			throw new FrameSourceException($"ffmpeg could not be started: {exception.Message}", exception);
		}

		return process;
	}

	private static void Kill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
		}
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}

	/// <summary>Keeps the end of ffmpeg's error output so a failure has a reason to log.</summary>
	private sealed class StderrTail
	{
		private const int MaxLines = 20;

		private readonly Queue<string> _lines = new();
		private readonly Task _reader;
		private readonly object _gate = new();

		public StderrTail(StreamReader reader)
		{
			_reader = ReadLoopAsync(reader);
		}

		public async Task<string> ReadAsync()
		{
			try
			{
				await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
			}

			lock (_gate)
			{
				return _lines.Count == 0 ? "no error output" : string.Join(" | ", _lines);
			}
		}

		private async Task ReadLoopAsync(StreamReader reader)
		{
			while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
			{
				lock (_gate)
				{
					_lines.Enqueue(line);
					while (_lines.Count > MaxLines)
					{
						_lines.Dequeue();
					}
				}
			}
		}
	}
}
