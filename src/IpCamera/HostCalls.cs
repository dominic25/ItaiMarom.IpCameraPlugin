using System.Diagnostics;
using MacroDeck.Plugin.Hosting.Transport;
using MacroDeck.Plugin.Protocol.Errors;

namespace IpCamera;

/// <summary>
/// Keeps the plugin's host callbacks under Macro Deck's per-plugin limit: a twenty call burst refilled
/// at ten per second. A refused call waits and tries again instead of failing the integration.
/// </summary>
internal static class HostCalls
{
	private static readonly SemaphoreSlim _gate = new(1, 1);
	private static readonly long _minimumInterval = (long)(Stopwatch.Frequency * 0.11);
	private static long _lastCall;

	public static async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken = default)
	{
		for (var attempt = 1; ; attempt++)
		{
			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var wait = _lastCall + _minimumInterval - Stopwatch.GetTimestamp();
				if (wait > 0)
				{
					await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), cancellationToken)
						.ConfigureAwait(false);
				}

				_lastCall = Stopwatch.GetTimestamp();
				return await call().ConfigureAwait(false);
			}
			catch (HostInvocationException exception)
				when (exception.Code == ProtocolErrorCodes.RateLimited && attempt < 10)
			{
			}
			finally
			{
				_gate.Release();
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
		}
	}

	public static Task RunAsync(Func<Task> call, CancellationToken cancellationToken = default)
	{
		return RunAsync(async () =>
		{
			await call().ConfigureAwait(false);
			return true;
		}, cancellationToken);
	}
}
