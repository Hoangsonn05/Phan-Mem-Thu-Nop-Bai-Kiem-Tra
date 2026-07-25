using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class ExamTransferOptionsValidator : IValidateOptions<ExamTransferOptions>
{
    public ValidateOptionsResult Validate(string? name, ExamTransferOptions options)
    {
        var errors = new List<string>();
        ValidateCidrs(options.LanAccess.AllowedCidrs, false, "LanAccess:AllowedCidrs", errors);
        ValidateCidrs(options.Discovery.AdditionalAllowedCidrs, false, "Discovery:AdditionalAllowedCidrs", errors);
        ValidateCidrs(options.LanAccess.TrustedDockerGatewayCidrs, true, "LanAccess:TrustedDockerGatewayCidrs", errors);

        if (options.LanAccess.TrustDockerDesktopNat
            && options.LanAccess.TrustedDockerGatewayCidrs.Count == 0)
            errors.Add("LanAccess:TrustDockerDesktopNat requires at least one narrowly scoped TrustedDockerGatewayCidrs entry.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateCidrs(
        IEnumerable<string> cidrs,
        bool trustedGateway,
        string optionName,
        ICollection<string> errors)
    {
        var index = 0;
        foreach (var cidr in cidrs)
        {
            if (!LanAccessPolicy.TryParseCidr(cidr, out _, out var prefix))
            {
                errors.Add($"{optionName}[{index}] is not a valid private IPv4 CIDR: '{cidr}'.");
            }
            else if (trustedGateway && prefix < 24)
            {
                errors.Add($"{optionName}[{index}] must be /24 or narrower; broad trusted NAT ranges are forbidden.");
            }
            index++;
        }
    }
}

public sealed record LanAdvertisedEndpointStatus(
    bool RunningInContainer,
    bool Ready,
    string? Address,
    string Code,
    string Detail);

public static class LanNetworkConfiguration
{
    public static bool RunningInContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    public static LanAdvertisedEndpointStatus ResolveAdvertisedEndpoint(ExamTransferOptions options)
    {
        var inContainer = RunningInContainer;
        var preferred = options.Server.PreferredIp?.Trim();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            if (!IPAddress.TryParse(preferred, out var parsed)
                || parsed.AddressFamily != AddressFamily.InterNetwork)
                return Invalid(inContainer, "LAN_PREFERRED_IP_INVALID", "Server:PreferredIp must be an IPv4 address.");
            if (IPAddress.IsLoopback(parsed))
                return Invalid(inContainer, "LAN_PREFERRED_IP_LOOPBACK", "Server:PreferredIp cannot be loopback.");
            if (!LanAccessPolicy.IsPrivate(parsed))
                return Invalid(inContainer, "LAN_PREFERRED_IP_NOT_PRIVATE", "Server:PreferredIp must be an RFC1918 private address.");
            if (inContainer && IsLikelyDockerBridge(parsed))
                return Invalid(inContainer, "LAN_PREFERRED_IP_DOCKER_BRIDGE", "Server:PreferredIp looks like a Docker bridge address.");
            if (!inContainer && !IsAssignedToActivePhysicalAdapter(parsed))
                return Invalid(inContainer, "LAN_PREFERRED_IP_NOT_ON_HOST", "Server:PreferredIp is not assigned to an active host adapter.");
            return new(inContainer, true, parsed.ToString(), "LAN_ADVERTISED_IP_READY", "Advertised LAN IPv4 is valid.");
        }

        if (inContainer)
            return Invalid(true, "LAN_PREFERRED_IP_REQUIRED_IN_DOCKER", "Docker cannot infer the Windows host LAN IP; configure Server:PreferredIp.");

        var detected = GetActivePhysicalAddresses().FirstOrDefault();
        return detected is null
            ? Invalid(false, "LAN_HOST_IP_NOT_FOUND", "No active physical RFC1918 IPv4 adapter was found.")
            : new(false, true, detected.ToString(), "LAN_ADVERTISED_IP_AUTO", "Advertised LAN IPv4 was selected from an active host adapter.");
    }

    public static bool IsLikelyDockerBridge(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 172 && bytes[1] is >= 16 and <= 18;
    }

    public static IReadOnlyList<IPAddress> GetActivePhysicalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsPhysicalLanAdapter)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses)
            .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(x.Address)
                && LanAccessPolicy.IsPrivate(x.Address))
            .Select(x => x.Address)
            .Distinct()
            .ToList();

    private static bool IsAssignedToActivePhysicalAdapter(IPAddress address) =>
        GetActivePhysicalAddresses().Contains(address);

    private static bool IsPhysicalLanAdapter(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up
            || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel
                or NetworkInterfaceType.Unknown)
            return false;

        var text = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        return !new[] { "docker", "hyper-v", "vethernet", "virtual", "vpn", "tap", "tun", "loopback", "wsl" }
            .Any(text.Contains);
    }

    private static LanAdvertisedEndpointStatus Invalid(bool inContainer, string code, string detail) =>
        new(inContainer, false, null, code, detail);
}
