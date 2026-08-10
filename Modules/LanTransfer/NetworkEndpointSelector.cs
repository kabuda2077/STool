using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace STool.Modules.LanTransfer;

internal static class NetworkEndpointSelector
{
    private static readonly string[] ExcludedAdapterTerms =
    [
        "virtual", "vpn", "mihomo", "wsl", "hyper-v", "tailscale",
        "zerotier", "docker", "loopback", "tunnel"
    ];

    public static IReadOnlyList<NetworkEndpoint> GetCandidates()
    {
        var candidates = new List<(NetworkEndpoint Endpoint, bool HasGateway)>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var identity = $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();
            if (ExcludedAdapterTerms.Any(identity.Contains))
                continue;

            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch
            {
                continue;
            }

            var hasGateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));

            foreach (var addressInfo in properties.UnicastAddresses)
            {
                var address = addressInfo.Address;
                var mask = addressInfo.IPv4Mask;
                if (address.AddressFamily != AddressFamily.InterNetwork || mask == null || !IsPrivateLanAddress(address))
                    continue;

                candidates.Add((new NetworkEndpoint(address, mask, networkInterface.Name), hasGateway));
            }
        }

        return candidates
            .OrderByDescending(item => item.HasGateway)
            .ThenBy(item => item.Endpoint.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Endpoint)
            .ToArray();
    }

    public static NetworkEndpoint? GetPreferred() => GetCandidates().FirstOrDefault();

    public static bool IsRemoteAllowed(NetworkEndpoint endpoint, IPAddress remoteAddress)
    {
        if (IPAddress.IsLoopback(remoteAddress))
            return true;

        if (remoteAddress.IsIPv4MappedToIPv6)
            remoteAddress = remoteAddress.MapToIPv4();
        if (remoteAddress.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var local = endpoint.Address.GetAddressBytes();
        var remote = remoteAddress.GetAddressBytes();
        var mask = endpoint.Mask.GetAddressBytes();
        for (var i = 0; i < local.Length; i++)
        {
            if ((local[i] & mask[i]) != (remote[i] & mask[i]))
                return false;
        }

        return true;
    }

    internal static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;

        if (bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168))
        {
            return true;
        }

        return false;
    }
}
