using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace ExamTransfer.Shared.Contracts;

public sealed record LanIpv4Interface(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    IPAddress Address,
    int PrefixLength,
    bool HasGateway,
    int Metric)
{
    public IPAddress DirectedBroadcast => LanIpv4Network.GetDirectedBroadcast(Address, PrefixLength);
}

public sealed record LanInterfaceDecision(
    LanIpv4Interface Interface,
    bool Included,
    string Reason,
    int Score);

public static class LanIpv4Network
{
    private static readonly string[] VirtualHints =
    [
        "docker", "hyper-v", "vethernet", "virtual", "vmware", "vmnet",
        "vpn", "tap", "tun", "loopback", "wsl", "wireguard"
    ];

    public static IReadOnlyList<LanIpv4Interface> GetSystemInterfaces()
    {
        var result = new List<LanIpv4Interface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var hasGateway = properties.GatewayAddresses.Any(x =>
                x.Address.AddressFamily == AddressFamily.InterNetwork
                && !x.Address.Equals(IPAddress.Any));
            var metric = 0;
            try
            {
                metric = ResolveInterfaceMetric(nic, properties.GetIPv4Properties()?.Index);
            }
            catch (NetworkInformationException)
            {
                metric = int.MaxValue;
            }

            foreach (var unicast in properties.UnicastAddresses.Where(x =>
                         x.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                result.Add(new(
                    nic.Id,
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType,
                    nic.OperationalStatus,
                    unicast.Address,
                    unicast.PrefixLength,
                    hasGateway,
                    metric));
            }
        }
        return result;
    }

    public static IReadOnlyList<LanInterfaceDecision> Evaluate(
        IEnumerable<LanIpv4Interface> interfaces,
        IPAddress? remoteAddress = null)
    {
        return interfaces
            .Select(candidate =>
            {
                var reason = ExclusionReason(candidate, remoteAddress);
                return new LanInterfaceDecision(
                    candidate,
                    reason is null,
                    reason ?? "usable physical LAN interface",
                    reason is null ? Score(candidate) : int.MinValue);
            })
            .OrderByDescending(x => x.Included)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Interface.Metric)
            .ThenBy(x => x.Interface.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<LanIpv4Interface> SelectUsable(
        IEnumerable<LanIpv4Interface> interfaces,
        IPAddress? remoteAddress = null) =>
        Evaluate(interfaces, remoteAddress)
            .Where(x => x.Included)
            .Select(x => x.Interface)
            .ToList();

    public static IPAddress GetDirectedBroadcast(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("An IPv4 address is required.", nameof(address));
        if (prefixLength is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(prefixLength));

        var value = ToUInt32(address);
        var mask = uint.MaxValue << (32 - prefixLength);
        return FromUInt32((value & mask) | ~mask);
    }

    public static bool IsSameSubnet(IPAddress left, IPAddress right, int prefixLength)
    {
        if (left.AddressFamily != AddressFamily.InterNetwork
            || right.AddressFamily != AddressFamily.InterNetwork
            || prefixLength is < 1 or > 32)
            return false;
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (ToUInt32(left) & mask) == (ToUInt32(right) & mask);
    }

    private static string? ExclusionReason(LanIpv4Interface candidate, IPAddress? remoteAddress)
    {
        if (candidate.Status != OperationalStatus.Up)
            return "adapter is disconnected";
        if (candidate.Type is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel
            or NetworkInterfaceType.Unknown)
            return $"unsupported interface type {candidate.Type}";
        if (candidate.Type is not (NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT))
            return $"non-LAN interface type {candidate.Type}";
        if (!DiscoveryProtocol.IsUsableEndpointAddress(candidate.Address))
            return "IPv4 address is loopback, unspecified, broadcast, or link-local";
        if (!IsPrivate(candidate.Address))
            return "IPv4 address is not RFC1918 private";
        if (candidate.PrefixLength is < 1 or > 30)
            return "IPv4 prefix cannot produce a usable directed broadcast";
        if (!candidate.HasGateway)
            return "adapter has no IPv4 gateway";

        var text = $"{candidate.Name} {candidate.Description}".ToLowerInvariant();
        if (VirtualHints.Any(text.Contains))
            return "adapter metadata identifies a virtual, tunnel, or VPN interface";
        if (remoteAddress is not null && !IsSameSubnet(candidate.Address, remoteAddress, candidate.PrefixLength))
            return "adapter subnet does not match remote client";
        return null;
    }

    private static int Score(LanIpv4Interface candidate) =>
        (candidate.Type == NetworkInterfaceType.Wireless80211 ? 300 : 200)
        + (candidate.HasGateway ? 50 : 0)
        - Math.Min(Math.Max(candidate.Metric, 0), 100);

    private static int ResolveInterfaceMetric(NetworkInterface nic, int? interfaceIndex)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.Id}");
                if (key?.GetValue("InterfaceMetric") is int configured && configured >= 0)
                    return configured;
                if (key?.GetValue("InterfaceMetric") is string text
                    && int.TryParse(text, out configured)
                    && configured >= 0)
                    return configured;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or IOException
                                       or System.Security.SecurityException)
            {
                // Fall through to the same link-speed tiers used by automatic metric selection.
            }
        }

        var speed = nic.Speed;
        var automaticMetric = speed switch
        {
            >= 10_000_000_000 => 5,
            >= 2_000_000_000 => 10,
            >= 200_000_000 => 25,
            >= 80_000_000 => 35,
            >= 20_000_000 => 45,
            >= 4_000_000 => 55,
            >= 500_000 => 65,
            _ => 75
        };
        return automaticMetric + Math.Min(interfaceIndex ?? 0, 10);
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24)
            | ((uint)bytes[1] << 16)
            | ((uint)bytes[2] << 8)
            | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
