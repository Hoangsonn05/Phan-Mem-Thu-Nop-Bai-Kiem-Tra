using ExamTransfer.Application;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.LocalServer.Discovery;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer;

public sealed record RuntimeHealthComponent(string Status, string Code, string Detail);

public sealed record RuntimeHealthReport(
    string Status,
    DateTimeOffset ServerNowUtc,
    string LocalSchemaVersion,
    bool RunningInContainer,
    RuntimeHealthComponent BackendRuntime,
    RuntimeHealthComponent Sqlite,
    RuntimeHealthComponent VolumeWritable,
    RuntimeHealthComponent DataProtectionKeys,
    RuntimeHealthComponent UdpDiscovery,
    RuntimeHealthComponent AdvertisedLanIp,
    RuntimeHealthComponent AllowedLanCidrs,
    RuntimeHealthComponent SupabaseConfigured,
    RuntimeHealthComponent SupabaseSchemaCompatible,
    RuntimeHealthComponent CloudWorker,
    RuntimeHealthComponent PublicCloudPullWorker,
    string? AdvertisedAddress,
    int AllowedCidrCount,
    string LanNatEnforcement);

public sealed class RuntimeHealthReporter(
    IServiceScopeFactory scopeFactory,
    IStoragePaths paths,
    IOptions<ExamTransferOptions> options,
    DiscoveryRuntimeState discovery)
{
    public async Task<RuntimeHealthReport> GetAsync(CancellationToken cancellationToken)
    {
        var sqlite = await CheckSqliteAsync(cancellationToken);
        var volume = CheckWritableDirectory(paths.RootPath, "VOLUME");
        var keys = CheckWritableDirectory(DependencyInjection.ResolveDataProtectionKeyDirectory(), "DATA_PROTECTION_KEYS");
        var lan = LanNetworkConfiguration.ResolveAdvertisedEndpoint(options.Value);
        var cidrs = options.Value.LanAccess.AllowedCidrs
            .Concat(options.Value.Discovery.AdditionalAllowedCidrs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var scope = scopeFactory.CreateScope();
        var cloud = scope.ServiceProvider.GetRequiredService<ICloudAdapter>();
        var cloudConfigured = !options.Value.Cloud.Enabled
            ? Component("Degraded", "SUPABASE_DISABLED", "Supabase is disabled; LanOnly remains available.")
            : cloud.Configured
                ? Component("Healthy", "SUPABASE_CONFIGURED", "Supabase configuration is present; remote compatibility was not queried by this health endpoint.")
                : Component("Degraded", "SUPABASE_CONFIGURATION_INCOMPLETE", "Supabase is enabled but its local configuration is incomplete.");
        var cloudSchema = !options.Value.Cloud.Enabled
            ? Component("Degraded", "SUPABASE_SCHEMA_NOT_APPLICABLE", "Cloud is disabled.")
            : Component("Degraded", "SUPABASE_SCHEMA_NOT_CHECKED", $"Remote schema compatibility version {CloudSchemaCompatibility.RequiredVersion} requires an explicit preflight.");

        var udp = !discovery.Enabled
            ? Component("Degraded", "UDP_DISCOVERY_DISABLED", "UDP discovery is disabled.")
            : discovery.Listening
                ? Component("Healthy", "UDP_DISCOVERY_LISTENING", $"UDP discovery is listening on 0.0.0.0:{discovery.ListeningPort ?? options.Value.Discovery.Port}.")
                : Component("Degraded", discovery.LastErrorCode ?? "UDP_DISCOVERY_STARTING", "UDP discovery is not listening.");
        var advertised = lan.Ready
            ? Component("Healthy", lan.Code, lan.Detail)
            : Component("Degraded", lan.Code, lan.Detail);
        var allowed = !LanNetworkConfiguration.RunningInContainer || cidrs.Count > 0
            ? Component("Healthy", "LAN_CIDRS_READY", cidrs.Count == 0 ? "Native mode uses active physical adapter CIDRs." : $"{cidrs.Count} explicit private CIDR entries configured.")
            : Component("Degraded", "LAN_CIDRS_REQUIRED_IN_DOCKER", "Docker mode requires explicit LanAccess:AllowedCidrs.");
        var worker = options.Value.Cloud.Enabled && cloud.CanSynchronize
            ? Component("Degraded", "CLOUD_WORKER_WAITING_FOR_PREFLIGHT", "Cloud worker is configured but remote schema compatibility is not checked by health.")
            : Component("Degraded", "CLOUD_WORKER_INACTIVE", "Cloud worker will not process until cloud configuration and authentication are ready.");
        var pullWorker = options.Value.Cloud.Enabled && cloud.CanSynchronize
            ? Component("Degraded", "PUBLIC_CLOUD_PULL_WAITING_FOR_PREFLIGHT", "PublicCloud pull is configured and still requires schema preflight.")
            : Component("Degraded", "PUBLIC_CLOUD_PULL_INACTIVE", "PublicCloud pull is inactive.");

        var criticalUnhealthy = sqlite.Status == "Unhealthy" || volume.Status == "Unhealthy" || keys.Status == "Unhealthy";
        var anyDegraded = new[] { udp, advertised, allowed, cloudConfigured, cloudSchema, worker, pullWorker }
            .Any(x => x.Status == "Degraded");
        var status = criticalUnhealthy ? "Unhealthy" : anyDegraded ? "Degraded" : "Healthy";

        return new(
            status,
            DateTimeOffset.UtcNow,
            ContractInfo.SchemaVersion,
            LanNetworkConfiguration.RunningInContainer,
            Component("Healthy", "BACKEND_RUNTIME_READY", "Backend runtime is responding."),
            sqlite,
            volume,
            keys,
            udp,
            advertised,
            allowed,
            cloudConfigured,
            cloudSchema,
            worker,
            pullWorker,
            lan.Address,
            cidrs.Count,
            options.Value.LanAccess.TrustDockerDesktopNat
                ? "ExplicitDockerGatewayAndWindowsPrivateFirewall"
                : "ApplicationRemoteIpOnly");
    }

    private async Task<RuntimeHealthComponent> CheckSqliteAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? Component("Healthy", "SQLITE_READY", "SQLite is reachable.")
                : Component("Unhealthy", "SQLITE_UNREACHABLE", "SQLite cannot be reached.");
        }
        catch
        {
            return Component("Unhealthy", "SQLITE_UNREACHABLE", "SQLite health check failed.");
        }
    }

    private static RuntimeHealthComponent CheckWritableDirectory(string directory, string prefix)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".health-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return Component("Healthy", $"{prefix}_WRITABLE", "Persistent directory is writable.");
        }
        catch
        {
            return Component("Unhealthy", $"{prefix}_NOT_WRITABLE", "Persistent directory is not writable.");
        }
    }

    private static RuntimeHealthComponent Component(string status, string code, string detail) =>
        new(status, code, detail);
}
