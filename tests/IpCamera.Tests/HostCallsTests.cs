using MacroDeck.Plugin.Hosting.Transport;
using MacroDeck.Plugin.Protocol.Errors;
using NUnit.Framework;

namespace IpCamera.Tests;

[TestFixture]
public sealed class HostCallsTests
{
	[Test]
	public async Task A_rate_limited_call_is_retried_until_it_succeeds()
	{
		var attempts = 0;
		var value = await HostCalls.RunAsync<string?>(() =>
		{
			attempts++;
			return attempts < 3
				? Task.FromException<string?>(HostInvocationException.CreateRetryable(
					ProtocolErrorCodes.RateLimited,
					"This plugin is calling back into the host too quickly."))
				: Task.FromResult<string?>("http://192.168.1.20/snapshot.jpg");
		});

		Assert.That(value, Is.EqualTo("http://192.168.1.20/snapshot.jpg"));
		Assert.That(attempts, Is.EqualTo(3));
	}

	[Test]
	public void A_failure_that_is_not_a_rate_limit_is_not_retried()
	{
		var attempts = 0;

		Assert.ThrowsAsync<HostInvocationException>(async () => await HostCalls.RunAsync<string?>(() =>
		{
			attempts++;
			return Task.FromException<string?>(HostInvocationException.CreateRetryable(
				ProtocolErrorCodes.Timeout,
				"The host did not answer in time."));
		}));
		Assert.That(attempts, Is.EqualTo(1));
	}

	[Test]
	public async Task Calls_are_spaced_so_a_burst_does_not_drain_the_host_budget()
	{
		var elapsed = System.Diagnostics.Stopwatch.StartNew();
		for (var i = 0; i < 5; i++)
		{
			await HostCalls.RunAsync(() => Task.CompletedTask);
		}

		Assert.That(elapsed.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(400)));
	}
}
