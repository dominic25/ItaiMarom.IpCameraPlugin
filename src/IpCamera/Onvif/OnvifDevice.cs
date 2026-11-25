namespace IpCamera.Onvif;

/// <summary>One camera that answered an ONVIF WS-Discovery probe.</summary>
internal sealed record OnvifDevice(string XAddr, string Name, string? Hardware)
{
	public string DisplayName => Name;

	public string Host
	{
		get
		{
			return Uri.TryCreate(XAddr, UriKind.Absolute, out var uri) ? uri.Host : XAddr;
		}
	}
}

/// <summary>The stream addresses ONVIF returned for one profile.</summary>
internal sealed record OnvifStream(
	string ProfileToken,
	string? StreamUri,
	string? SnapshotUri);

internal interface IOnvifDiscovery
{
	Task<IReadOnlyList<OnvifDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface IOnvifClient
{
	Task<OnvifStream?> ResolveAsync(
		OnvifDevice device,
		string? username,
		string? password,
		CancellationToken cancellationToken);

	Task<bool> MoveAsync(
		OnvifDevice device,
		string? username,
		string? password,
		string direction,
		double speed,
		CancellationToken cancellationToken);

	Task<bool> StopAsync(
		OnvifDevice device,
		string? username,
		string? password,
		CancellationToken cancellationToken);

	Task<bool> SetIrAsync(
		OnvifDevice device,
		string? username,
		string? password,
		string mode,
		CancellationToken cancellationToken);
}
