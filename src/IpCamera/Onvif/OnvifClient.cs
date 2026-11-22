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

	private async Task<XDocument?> PostAsync(
		OnvifDevice device,
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

			using var request = new HttpRequestMessage(HttpMethod.Post, device.XAddr)
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
					device.Host,
					(int)response.StatusCode);
				return null;
			}

			var xml = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
			return XDocument.Parse(xml);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			logger.Information("ONVIF device {Host} did not answer in time.", device.Host);
			return null;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			logger.Information("ONVIF device {Host} failed: {Reason}", device.Host, exception.Message);
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
