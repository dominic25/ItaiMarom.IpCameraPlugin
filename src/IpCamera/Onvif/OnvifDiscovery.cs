using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Serilog;

namespace IpCamera.Onvif;

/// <summary>
/// Finds ONVIF cameras with a WS-Discovery multicast probe. The probe goes out of every IPv4 interface
/// that is up, because a host with a VPN or WSL adapter often routes multicast through the wrong one.
/// </summary>
internal sealed class OnvifDiscovery(ILogger logger) : IOnvifDiscovery
{
	private static readonly IPEndPoint _multicast = new(IPAddress.Parse("239.255.255.250"), 3702);

	private const string Probe = """
		<?xml version="1.0" encoding="UTF-8"?>
		<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
		            xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
		            xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
		            xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
		  <s:Header>
		    <a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>
		    <a:MessageID>urn:uuid:6f7c9a2e-2f1a-4e4b-9c3d-0a1b2c3d4e5f</a:MessageID>
		    <a:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>
		  </s:Header>
		  <s:Body>
		    <d:Probe>
		      <d:Types>dn:NetworkVideoTransmitter</d:Types>
		    </d:Probe>
		  </s:Body>
		</s:Envelope>
		""";

	public async Task<IReadOnlyList<OnvifDevice>> DiscoverAsync(
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		var found = new ConcurrentDictionary<string, OnvifDevice>(StringComparer.OrdinalIgnoreCase);
		var probe = Encoding.UTF8.GetBytes(Probe);

		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(timeout);

		var tasks = NetworkAddresses()
			.Select(address => DiscoverOnAsync(address, probe, found, deadline.Token))
			.ToList();

		try
		{
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}

		return [.. found.Values.OrderBy(device => device.XAddr, StringComparer.OrdinalIgnoreCase)];
	}

	private async Task DiscoverOnAsync(
		IPAddress address,
		byte[] probe,
		ConcurrentDictionary<string, OnvifDevice> found,
		CancellationToken cancellationToken)
	{
		try
		{
			using var client = new UdpClient(new IPEndPoint(address, 0));
			await client.SendAsync(probe, _multicast, cancellationToken).ConfigureAwait(false);

			while (!cancellationToken.IsCancellationRequested)
			{
				var response = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
				var xml = Encoding.UTF8.GetString(response.Buffer);

				foreach (var device in ParseProbeMatches(xml))
				{
					found.TryAdd(device.XAddr, device);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (SocketException exception)
		{
			logger.Debug("ONVIF discovery on {Address} stopped: {Reason}", address, exception.Message);
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			logger.Debug("ONVIF discovery on {Address} failed: {Reason}", address, exception.Message);
		}
	}

	internal static IReadOnlyList<OnvifDevice> ParseProbeMatches(string xml)
	{
		XDocument document;
		try
		{
			document = XDocument.Parse(xml);
		}
		catch (System.Xml.XmlException)
		{
			return [];
		}

		var devices = new List<OnvifDevice>();

		foreach (var match in document.Descendants().Where(element => element.Name.LocalName == "ProbeMatch"))
		{
			var xaddrs = match.Descendants().FirstOrDefault(element => element.Name.LocalName == "XAddrs")?.Value;
			if (string.IsNullOrWhiteSpace(xaddrs))
			{
				continue;
			}

			var scopes = match.Descendants().FirstOrDefault(element => element.Name.LocalName == "Scopes")?.Value;
			var name = ScopeValue(scopes, "name");
			var hardware = ScopeValue(scopes, "hardware");

			foreach (var address in xaddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				var host = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;
				devices.Add(new OnvifDevice(address, name ?? host, hardware));
			}
		}

		return devices;
	}

	private static string? ScopeValue(string? scopes, string key)
	{
		if (string.IsNullOrWhiteSpace(scopes))
		{
			return null;
		}

		var prefix = $"onvif://www.onvif.org/{key}/";
		foreach (var scope in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					return Uri.UnescapeDataString(scope[prefix.Length..]).Replace('_', ' ');
				}
				catch (UriFormatException)
				{
					return scope[prefix.Length..];
				}
			}
		}

		return null;
	}

	private static IEnumerable<IPAddress> NetworkAddresses()
	{
		var addresses = new List<IPAddress>();

		try
		{
			foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (network.OperationalStatus != OperationalStatus.Up ||
					network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
				{
					continue;
				}

				foreach (var unicast in network.GetIPProperties().UnicastAddresses)
				{
					if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
						!IPAddress.IsLoopback(unicast.Address))
					{
						addresses.Add(unicast.Address);
					}
				}
			}
		}
		catch (NetworkInformationException)
		{
		}

		return addresses.Distinct().Take(8);
	}
}
