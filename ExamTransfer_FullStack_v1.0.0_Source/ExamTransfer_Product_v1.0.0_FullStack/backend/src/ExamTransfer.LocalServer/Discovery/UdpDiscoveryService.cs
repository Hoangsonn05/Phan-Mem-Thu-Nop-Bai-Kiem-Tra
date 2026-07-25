using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Discovery;

public sealed class DiscoveryRuntimeState
{
    public bool Enabled { get; internal set; }
    public bool Listening { get; internal set; }
    public string? LastErrorCode { get; internal set; }
}

public sealed class UdpDiscoveryService(
    IServiceScopeFactory scopeFactory,
    IOptions<ExamTransferOptions> options,
    DiscoveryRuntimeState state,
    ILogger<UdpDiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.Enabled = options.Value.Discovery.Enabled;
        if (!options.Value.Discovery.Enabled) return;
        var discoveryPort = options.Value.Discovery.Port;

        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
            state.Listening = true;
            logger.LogInformation("UDP discovery listening on 0.0.0.0:{Port}", discoveryPort);
        }
        catch (SocketException ex)
        {
            state.LastErrorCode = "UDP_DISCOVERY_BIND_FAILED";
            logger.LogWarning(ex, "UDP discovery could not bind 0.0.0.0:{Port}; LAN auto-discovery is disabled for this run.", discoveryPort);
            return;
        }

        using (udp)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var received = await udp.ReceiveAsync(stoppingToken);
                var text = Encoding.UTF8.GetString(received.Buffer).Trim();
                if (!text.Equals(options.Value.Discovery.RequestMagic, StringComparison.Ordinal)) continue;
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rooms = await db.ExamSessionsSet.CountAsync(
                    x => x.AccessMode == SessionAccessMode.LanOnly
                        && x.Status == SessionStatus.Waiting
                        && x.AcceptingParticipants,
                    stoppingToken);
                var endpoint = LanNetworkConfiguration.ResolveAdvertisedEndpoint(options.Value);
                if (!endpoint.Ready)
                {
                    state.LastErrorCode = endpoint.Code;
                    logger.LogWarning(
                        "UDP discovery response suppressed: {Code}. {Detail}",
                        endpoint.Code,
                        endpoint.Detail);
                    continue;
                }
                var response = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryServerDto(
                    DiscoveryProtocol.ProtocolVersion,
                    Environment.MachineName,
                    endpoint.Address!,
                    options.Value.Server.Port,
                    MachineFingerprint(),
                    rooms,
                    typeof(UdpDiscoveryService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    DateTimeOffset.UtcNow,
                    MachineFingerprint()));
                await udp.SendAsync(response, received.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "UDP discovery request failed"); }
        }
        state.Listening = false;
        if (stoppingToken.IsCancellationRequested)
        {
            state.LastErrorCode = null;
        }
    }

    private static string MachineFingerprint()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|ExamTransfer|discovery"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
