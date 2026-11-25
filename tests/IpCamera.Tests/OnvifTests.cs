using System.Net;
using System.Text;
using IpCamera.Onvif;
using NUnit.Framework;
using Serilog;

namespace IpCamera.Tests;

[TestFixture]
public sealed class OnvifTests
{
	private static readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	private static readonly string _probeMatches = """
		<?xml version="1.0" encoding="UTF-8"?>
		<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
		            xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
		            xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
		  <s:Body>
		    <d:ProbeMatches>
		      <d:ProbeMatch>
		        <a:EndpointReference>
		          <a:Address>urn:uuid:11111111-2222-3333-4444-555555555555</a:Address>
		        </a:EndpointReference>
		        <d:Scopes>onvif://www.onvif.org/name/Front%20door onvif://www.onvif.org/hardware/Hikvision</d:Scopes>
		        <d:XAddrs>http://192.168.1.50/onvif/device_service</d:XAddrs>
		      </d:ProbeMatch>
		    </d:ProbeMatches>
		  </s:Body>
		</s:Envelope>
		""";

	[Test]
	public void Probe_matches_are_read_into_devices()
	{
		var devices = OnvifDiscovery.ParseProbeMatches(_probeMatches);

		Assert.That(devices, Has.Count.EqualTo(1));
		Assert.That(devices[0].XAddr, Is.EqualTo("http://192.168.1.50/onvif/device_service"));
		Assert.That(devices[0].Name, Is.EqualTo("Front door"));
		Assert.That(devices[0].Hardware, Is.EqualTo("Hikvision"));
	}

	[Test]
	public void The_security_header_carries_a_password_digest()
	{
		var header = OnvifClient.SecurityHeader("admin", "secret");

		Assert.That(header, Does.Contain("<wsse:Username>admin</wsse:Username>"));
		Assert.That(header, Does.Contain("#PasswordDigest"));
		Assert.That(header, Does.Contain("<wsse:Nonce"));
	}

	[Test]
	public async Task A_profile_and_both_addresses_are_resolved()
	{
		using var handler = new OnvifHandler();
		var client = new OnvifClient(new StubFactory(handler), _logger);

		var result = await client.ResolveAsync(
			new OnvifDevice("http://192.168.1.50/onvif/device_service", "Front", null),
			"admin",
			"secret",
			CancellationToken.None);

		Assert.That(result, Is.Not.Null);
		Assert.That(result!.ProfileToken, Is.EqualTo("profile1"));
		Assert.That(result.StreamUri, Is.EqualTo("rtsp://192.168.1.50/Streaming/Channels/101"));
		Assert.That(result.SnapshotUri, Is.EqualTo("http://192.168.1.50/snapshot.jpg"));
	}

	[Test]
	public async Task A_rejected_device_returns_no_stream()
	{
		using var handler = new RejectedHandler();
		var client = new OnvifClient(new StubFactory(handler), _logger);

		var result = await client.ResolveAsync(
			new OnvifDevice("http://192.168.1.50/onvif/device_service", "Front", null),
			"admin",
			"wrong",
			CancellationToken.None);

		Assert.That(result, Is.Null);
	}

	[Test]
	public async Task A_ptz_move_stop_and_ir_update_are_sent()
	{
		using var handler = new ControlHandler();
		var client = new OnvifClient(new StubFactory(handler), _logger);
		var device = new OnvifDevice("http://192.168.1.50/onvif/device_service", "Front", null);

		Assert.That(await client.MoveAsync(device, "admin", "secret", "left", 0.5, CancellationToken.None), Is.True);
		Assert.That(await client.StopAsync(device, "admin", "secret", CancellationToken.None), Is.True);
		Assert.That(await client.SetIrAsync(device, "admin", "secret", "ON", CancellationToken.None), Is.True);

		Assert.Multiple(() =>
		{
			Assert.That(handler.Bodies.Any(body => body.Contains("ContinuousMove", StringComparison.Ordinal)), Is.True);
			Assert.That(handler.Bodies.Any(body => body.Contains("PanTilt x=\"-0.5\"", StringComparison.Ordinal)), Is.True);
			Assert.That(handler.Bodies.Any(body => body.Contains("<tptz:Stop>", StringComparison.Ordinal)), Is.True);
			Assert.That(handler.Bodies.Any(body => body.Contains("<tt:IrCutFilter>ON</tt:IrCutFilter>", StringComparison.Ordinal)), Is.True);
		});
	}

	[Test]
	public void User_info_is_removed_from_an_onvif_url()
	{
		Assert.That(
			OnvifClient.StripUserInfo("rtsp://admin:secret@192.168.1.50/live"),
			Is.EqualTo("rtsp://192.168.1.50/live"));
	}

	[OneTimeTearDown]
	public void DisposeLogger()
	{
		(_logger as IDisposable)?.Dispose();
	}

	private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name)
		{
			return new HttpClient(handler, disposeHandler: false);
		}
	}

	private sealed class OnvifHandler : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

			var xml = body switch
			{
				var text when text.Contains("GetProfiles", StringComparison.Ordinal) => ProfileResponse,
				var text when text.Contains("GetStreamUri", StringComparison.Ordinal) => StreamResponse,
				_ => SnapshotResponse
			};

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(xml, Encoding.UTF8, "application/soap+xml")
			};
		}
	}

	private sealed class RejectedHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
		}
	}

	private const string ProfileResponse = """
		<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
		            xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
		  <s:Body>
		    <trt:GetProfilesResponse>
		      <trt:Profiles token="profile1"><trt:Name>mainStream</trt:Name></trt:Profiles>
		    </trt:GetProfilesResponse>
		  </s:Body>
		</s:Envelope>
		""";

	private const string StreamResponse = """
		<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
		            xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
		            xmlns:tt="http://www.onvif.org/ver10/schema">
		  <s:Body>
		    <trt:GetStreamUriResponse>
		      <trt:MediaUri>
		        <tt:Uri>rtsp://admin:secret@192.168.1.50/Streaming/Channels/101</tt:Uri>
		      </trt:MediaUri>
		    </trt:GetStreamUriResponse>
		  </s:Body>
		</s:Envelope>
		""";

	private const string SnapshotResponse = """
		<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
		            xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
		            xmlns:tt="http://www.onvif.org/ver10/schema">
		  <s:Body>
		    <trt:GetSnapshotUriResponse>
		      <trt:MediaUri>
		        <tt:Uri>http://192.168.1.50/snapshot.jpg</tt:Uri>
		      </trt:MediaUri>
		    </trt:GetSnapshotUriResponse>
		  </s:Body>
		</s:Envelope>
		""";

	private sealed class ControlHandler : HttpMessageHandler
	{
		public List<string> Bodies { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			Bodies.Add(body);

			var xml = body switch
			{
				var text when text.Contains("GetCapabilities", StringComparison.Ordinal) => CapabilitiesResponse,
				var text when text.Contains("GetProfiles", StringComparison.Ordinal) => ProfileResponse,
				var text when text.Contains("GetVideoSources", StringComparison.Ordinal) => VideoSourcesResponse,
				_ => EmptyResponse
			};

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(xml, Encoding.UTF8, "application/soap+xml")
			};
		}
	}

	private const string CapabilitiesResponse = """
		<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
		            xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
		            xmlns:tt="http://www.onvif.org/ver10/schema">
		  <s:Body>
		    <tds:GetCapabilitiesResponse>
		      <tds:Capabilities>
		        <tt:Media><tt:XAddr>http://192.168.1.50/onvif/media_service</tt:XAddr></tt:Media>
		        <tt:PTZ><tt:XAddr>http://192.168.1.50/onvif/ptz_service</tt:XAddr></tt:PTZ>
		        <tt:Imaging><tt:XAddr>http://192.168.1.50/onvif/imaging_service</tt:XAddr></tt:Imaging>
		      </tds:Capabilities>
		    </tds:GetCapabilitiesResponse>
		  </s:Body>
		</s:Envelope>
		""";

	private const string VideoSourcesResponse = """
		<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
		            xmlns:trt="http://www.onvif.org/ver10/media/wsdl">
		  <s:Body>
		    <trt:GetVideoSourcesResponse>
		      <trt:VideoSources token="source1"/>
		    </trt:GetVideoSourcesResponse>
		  </s:Body>
		</s:Envelope>
		""";

	private const string EmptyResponse = """
		<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
		  <s:Body/>
		</s:Envelope>
		""";
}
