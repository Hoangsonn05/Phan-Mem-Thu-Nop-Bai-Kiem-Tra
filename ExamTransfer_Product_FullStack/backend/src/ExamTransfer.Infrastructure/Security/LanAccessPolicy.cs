using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ExamTransfer.Application;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class LanAccessPolicy : ILanAccessPolicy
{
    private readonly IReadOnlyList<NetworkRange> ranges;
    private readonly IReadOnlyList<NetworkRange> trustedDockerGateways;
    private readonly bool trustDockerDesktopNat;

    public LanAccessPolicy(IOptions<ExamTransferOptions> options)
        : this(
            GetAllowedRanges(options.Value),
            ParseConfigured(options.Value.LanAccess.TrustedDockerGatewayCidrs).ToList(),
            options.Value.LanAccess.TrustDockerDesktopNat)
    {
    }

    internal LanAccessPolicy(
        IReadOnlyList<NetworkRange> ranges,
        IReadOnlyList<NetworkRange>? trustedDockerGateways = null,
        bool trustDockerDesktopNat = false)
    {
        this.ranges = ranges;
        this.trustedDockerGateways = trustedDockerGateways ?? [];
        this.trustDockerDesktopNat = trustDockerDesktopNat;
    }

    public bool IsAllowed(string? remoteAddress)
    {
        if (!IPAddress.TryParse(remoteAddress, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(address)) return false;
        if (ranges.Any(range => range.Contains(address))) return true;
        return trustDockerDesktopNat
            && LanNetworkConfiguration.RunningInContainer
            && trustedDockerGateways.Any(range => range.Contains(address));
    }

    internal static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    internal static bool TryParseCidr(string value, out NetworkRange range)
        => TryParseCidr(value, out range, out _);

    public static bool IsValidPrivateCidr(string value) =>
        TryParseCidr(value, out _, out _);

    internal static bool TryParseCidr(string value, out NetworkRange range, out int prefix)
    {
        range = default;
        prefix = 0;
        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out prefix)
            || !IsPrivateCidr(address, prefix))
            return false;

        range = NetworkRange.FromPrefix(address, prefix);
        return true;
    }

    private static bool IsPrivateCidr(IPAddress address, int prefix)
    {
        if (prefix is < 8 or > 32) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 10) return prefix >= 8;
        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return prefix >= 12;
        return bytes[0] == 192 && bytes[1] == 168 && prefix >= 16;
    }

    private static IEnumerable<NetworkRange> GetLocalRanges()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(x => x.OperationalStatus == OperationalStatus.Up))
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses
                         .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && x.IPv4Mask is not null))
            {
                if (IsPrivate(unicast.Address))
                    yield return NetworkRange.FromMask(unicast.Address, unicast.IPv4Mask);
            }
        }
    }

    private static IEnumerable<NetworkRange> ParseConfigured(IEnumerable<string> values)
    {
        foreach (var value in values)
            if (TryParseCidr(value, out var range))
                yield return range;
    }

    private static IReadOnlyList<NetworkRange> GetAllowedRanges(ExamTransferOptions options)
    {
        var configured = options.LanAccess.AllowedCidrs
            .Concat(options.Discovery.AdditionalAllowedCidrs);
        var explicitRanges = ParseConfigured(configured).ToList();
        if (LanNetworkConfiguration.RunningInContainer)
            return explicitRanges;
        return GetLocalRanges().Concat(explicitRanges).Distinct().ToList();
    }

    internal readonly record struct NetworkRange(uint Network, uint Mask)
    {
        public bool Contains(IPAddress address) =>
            (ToUInt32(address) & Mask) == Network;

        public static NetworkRange FromMask(IPAddress address, IPAddress mask)
        {
            var maskValue = ToUInt32(mask);
            return new(ToUInt32(address) & maskValue, maskValue);
        }

        public static NetworkRange FromPrefix(IPAddress address, int prefix)
        {
            var mask = prefix == 0 ? 0U : uint.MaxValue << (32 - prefix);
            return new(ToUInt32(address) & mask, mask);
        }

        private static uint ToUInt32(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }
    }
}
