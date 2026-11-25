using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using IpCamera.Cameras;
using Serilog;

namespace IpCamera.Onvif;

/// <summary>
/// Asks one discovered device for its profiles and resolves the RTSP and snapshot addresses. ONVIF
/// devices normally protect these calls with a WS-Security UsernameToken, so the security header is
/// built here instead of relying on HTTP authentication.
/// </summary>
internal sealed class OnvifClient(IHttpClientFactory httpClients, ILogger logger) : IOnvifClient
{
	private const string EnvelopeStart = """
		<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
		            xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
		            xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
		            xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
		            xmlns:timg="http://www.onvif.org/ver20/imaging/wsdl"
		            xmlns:tt="http://www.onvif.org/ver10/schema">
		""";

	private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(6);

	public async Task<OnvifStream?> ResolveAsync(
		OnvifDevice device,
		string? username,
		string? password,
		CancellationToken cancellationToken)
	{
		var profiles = await PostAsync(
			device,
			"<trt:GetProfiles/>",
			username,
			password,
			cancellationToken).ConfigureAwait(false);

		var token = profiles?
			.Descendants()
			.FirstOrDefault(element => element.Name.LocalName == "Profiles")?
			.Attribute("token")?
			.Value;

		if (string.IsNullOrEmpty(token))
		{
			logger.Information("ONVIF device {Host} returned no media profile.", device.Host);
			return null;
		}

		var stream = await PostAsync(
			device,
			$"""
			<trt:GetStreamUri>
			  <trt:StreamSetup>
			    <tt:Stream>RTP-Unicast</tt:Stream>
			    <tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport>
			  </trt:StreamSetup>
			  <trt:ProfileToken>{SecurityElement.Escape(token)}</trt:ProfileToken>
			</trt:GetStreamUri>
			""",
			username,
			password,
			cancellationToken).ConfigureAwait(false);

		var snapshot = await PostAsync(
			device,
			$"""
			<trt:GetSnapshotUri>
			  <trt:ProfileToken>{SecurityElement.Escape(token)}</trt:ProfileToken>
			</trt:GetSnapshotUri>
			""",
			username,
			password,
			cancellationToken).ConfigureAwait(false);

		var streamUri = UriValue(stream);
		var snapshotUri = UriValue(snapshot);

		if (streamUri is null && snapshotUri is null)
		{
			logger.Information("ONVIF device {Host} returned no stream address.", device.Host);
			return null;
		}

		return new OnvifStream(token, StripUserInfo(streamUri), StripUserInfo(snapshotUri));
	}

	public async Task<bool> MoveAsync(
		OnvifDevice device,
		string? username,
		string? password,
		string direction,
		double speed,
		CancellationToken cancellationToken)
	{
		var capabilities = await CapabilitiesAsync(device, username, password, cancellationToken)
			.ConfigureAwait(false);
		var media = Service(capabilities, "Media") ?? DefaultService(device, "media_service");
		var ptz = Service(capabilities, "PTZ") ?? DefaultService(device, "ptz_service");
		var token = await ProfileTokenAsync(media, device, username, password, cancellationToken)
			.ConfigureAwait(false);

		if (token is null)
		{
			return false;
		}

		var speedValue = Math.Clamp(speed, 0.1, 1.0);
		var x = direction switch
		{
			"left" => -speedValue,
			"right" => speedValue,
			_ => 0
		};
		var y = direction switch
		{
			"up" => speedValue,
			"down" => -speedValue,
			_ => 0
		};

		var body = $"""
			<tptz:ContinuousMove>
			  <tptz:ProfileToken>{SecurityElement.Escape(token)}</tptz:ProfileToken>
			  <tptz:Velocity>
			    <tt:PanTilt x="{x.ToString("0.###", CultureInfo.InvariantCulture)}" y="{y.ToString("0.###", CultureInfo.InvariantCulture)}"/>
			    <tt:Zoom x="0"/>
			  </tptz:Velocity>
			</tptz:ContinuousMove>
			""";

		return await PostToAsync(ptz, body, username, password, cancellationToken).ConfigureAwait(false) is not null;
	}

	public async Task<bool> StopAsync(
		OnvifDevice device,
		string? username,
		string? password,
		CancellationToken cancellationToken)
	{
		var capabilities = await CapabilitiesAsync(device, username, password, cancellationToken)
			.ConfigureAwait(false);
		var media = Service(capabilities, "Media") ?? DefaultService(device, "media_service");
		var ptz = Service(capabilities, "PTZ") ?? DefaultService(device, "ptz_service");
		var token = await ProfileTokenAsync(media, device, username, password, cancellationToken)
			.ConfigureAwait(false);

		if (token is null)
		{
			return false;
		}

		var body = $"""
			<tptz:Stop>
			  <tptz:ProfileToken>{SecurityElement.Escape(token)}</tptz:ProfileToken>
			  <tptz:PanTilt>true</tptz:PanTilt>
			  <tptz:Zoom>true</tptz:Zoom>
			</tptz:Stop>
			""";

		return await PostToAsync(ptz, body, username, password, cancellationToken).ConfigureAwait(false) is not null;
	}

	public async Task<bool> SetIrAsync(
		OnvifDevice device,
		string? username,
		string? password,
		string mode,
		CancellationToken cancellationToken)
	{
		var capabilities = await CapabilitiesAsync(device, username, password, cancellationToken)
			.ConfigureAwait(false);
		var media = Service(capabilities, "Media") ?? DefaultService(device, "media_service");
		var imaging = Service(capabilities, "Imaging") ?? DefaultService(device, "imaging_service");
		var source = await VideoSourceTokenAsync(media, device, username, password, cancellationToken)
			.ConfigureAwait(false);

		if (source is null)
		{
			return false;
		}

		var body = $"""
			<timg:SetImagingSettings>
			  <timg:VideoSourceToken>{SecurityElement.Escape(source)}</timg:VideoSourceToken>
			  <timg:ImagingSettings>
			    <tt:IrCutFilter>{SecurityElement.Escape(mode)}</tt:IrCutFilter>
			  </timg:ImagingSettings>
			</timg:SetImagingSettings>
			""";

		return await PostToAsync(imaging, body, username, password, cancellationToken).ConfigureAwait(false) is not null;
	}

	private async Task<XDocument?> CapabilitiesAsync(
		OnvifDevice device,
		string? username,
		string? password,
		CancellationToken cancellationToken)
	{
		return await PostAsync(
			device,
			"<tds:GetCapabilities><tds:Category>All</tds:Category></tds:GetCapabilities>",
			username,
			password,
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<string?> ProfileTokenAsync(
		Uri media,
		OnvifDevice device,
		string? username,
		string? password,
		CancellationToken cancellationToken)
	{
		var profiles = await PostToAsync(
			media,
			"<trt:GetProfiles/>",
			username,
			password,
			cancellationToken).ConfigureAwait(false);

		var token = profiles?
			.Descendants()
			.FirstOrDefault(element => element.Name.LocalName == "Profiles")?
			.Attribute("token")?
			.Value;

		if (string.IsNullOrEmpty(token))
		{
			logger.Information("ONVIF device {Host} returned no profile for a control.", device.Host);
			return null;
		}

		return token;
	}

	private async Task<string?> VideoSourceTokenAsync(
		Uri media,
		OnvifDevice device,
		string? username,
		string? password,
		CancellationToken cancellationToken)
	{
		var sources = await PostToAsync(
			media,
			"<trt:GetVideoSources/>",
			username,
			password,
			cancellationToken).ConfigureAwait(false);

		var token = sources?
			.Descendants()
			.FirstOrDefault(element => element.Name.LocalName == "VideoSources")?
			.Attribute("token")?
			.Value;

		if (string.IsNullOrEmpty(token))
		{
			logger.Information("ONVIF device {Host} returned no video source for imaging.", device.Host);
			return null;
		}

		return token;
	}

	private static Uri? Service(XDocument? capabilities, string name)
	{
		var address = capabilities?
			.Descendants()
			.FirstOrDefault(element => element.Name.LocalName == name)?
			.Descendants()
			.FirstOrDefault(element => element.Name.LocalName == "XAddr")?
			.Value;

		return Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null;
	}

	private static Uri DefaultService(OnvifDevice device, string path)
	{
		return new Uri($"http://{device.Host}/onvif/{path}");
	}

	private async Task<XDocument?> PostAsync(
		OnvifDevice device,
		string body,
		string? username,
		string? password,
		CancellationToken cancellationToken)
	{
		return await PostToAsync(
			new Uri(device.XAddr),
			body,
			username,
			password,
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<XDocument?> PostToAsync(
		Uri address,
		string body,
		string? username,
		string? password,
		CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_timeout);

		try
		{
			var security = string.IsNullOrEmpty(username)
				? string.Empty
				: SecurityHeader(username, password ?? string.Empty);

			var envelope = $"{EnvelopeStart}<s:Header>{security}</s:Header><s:Body>{body}</s:Body></s:Envelope>";

			using var request = new HttpRequestMessage(HttpMethod.Post, address)
			{
				Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml")
			};

			using var response = await httpClients
				.CreateClient(CameraSourceFactory.HttpClientName)
				.SendAsync(request, timeout.Token)
				.ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				logger.Information(
					"ONVIF device {Host} answered {Status}.",
					address.Host,
					(int)response.StatusCode);
				return null;
			}

			var xml = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
			return XDocument.Parse(xml);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			logger.Information("ONVIF device {Host} did not answer in time.", address.Host);
			return null;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			logger.Information("ONVIF device {Host} failed: {Reason}", address.Host, exception.Message);
			return null;
		}
	}

	internal static string? UriValue(XDocument? document)
	{
		return document?
			.Descendants()
			.FirstOrDefault(element => element.Name.LocalName == "Uri")?
			.Value;
	}

	internal static string? StripUserInfo(string? url)
	{
		if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
			uri.UserInfo.Length == 0)
		{
			return url;
		}

		var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
		return builder.Uri.ToString();
	}

	private static byte[] Sha1(byte[] material)
	{
#pragma warning disable CA5350 // WS-Security PasswordDigest is defined over SHA1; no other hash is accepted.
		return SHA1.HashData(material);
#pragma warning restore CA5350
	}

	internal static string SecurityHeader(string username, string password)	{
		var nonce = RandomNumberGenerator.GetBytes(16);
		var created = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

		var material = new byte[nonce.Length + created.Length + password.Length];
		var offset = 0;
		nonce.CopyTo(material, offset);
		offset += nonce.Length;
		var createdBytes = Encoding.UTF8.GetBytes(created);
		createdBytes.CopyTo(material, offset);
		offset += createdBytes.Length;
		Encoding.UTF8.GetBytes(password).CopyTo(material, offset);

		var digest = Sha1(material);

		return $"""
			<wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
			               xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
			  <wsse:UsernameToken>
			    <wsse:Username>{SecurityElement.Escape(username)}</wsse:Username>
			    <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{Convert.ToBase64String(digest)}</wsse:Password>
			    <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{Convert.ToBase64String(nonce)}</wsse:Nonce>
			    <wsu:Created>{created}</wsu:Created>
			  </wsse:UsernameToken>
			</wsse:Security>
			""";
	}
}
