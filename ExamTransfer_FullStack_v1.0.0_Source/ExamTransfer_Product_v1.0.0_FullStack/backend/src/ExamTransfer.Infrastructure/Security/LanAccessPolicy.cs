using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ExamTransfer.Application;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class LanAccessPolicy : ILanAccessPolicy
{
    private readonly IReadOnlyList<NetworkRange> ranges;

    public LanAccessPolicy(IOptions<ExamTransferOptions> options)
        : this(GetLocalRanges().Concat(ParseConfigured(options.Value.Discovery.AdditionalAllowedCidrs)).ToList())
    {
    }

    internal LanAccessPolicy(IReadOnlyList<NetworkRange> ranges) => this.ranges = ranges;

    public bool IsAllowed(string? remoteAddress)
    {
        if (!IPAddress.TryParse(remoteAddress, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(address)) return false;
        return ranges.Any(range => range.Contains(address));
    }

    internal static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 127
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    internal static bool TryParseCidr(string value, out NetworkRange range)
    {
        range = default;
        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out var prefix)
            || prefix is < 0 or > 32
            || !IsPrivate(address))
            return false;

        range = NetworkRange.FromPrefix(address, prefix);
        return true;
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
