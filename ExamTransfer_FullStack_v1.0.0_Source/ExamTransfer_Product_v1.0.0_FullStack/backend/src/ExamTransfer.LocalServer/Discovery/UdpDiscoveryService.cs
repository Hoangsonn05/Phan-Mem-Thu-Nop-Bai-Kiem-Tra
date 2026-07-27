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
    public int? ListeningPort { get; internal set; }
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
        var bindFailureLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpClient? udp = null;
            var listeningPort = discoveryPort;
            try
            {
                SocketException? lastBindError = null;
                foreach (var candidatePort in DiscoveryProtocol.CandidatePorts(discoveryPort))
                {
                    try
                    {
                        udp = new UdpClient(new IPEndPoint(IPAddress.Any, candidatePort));
                        listeningPort = candidatePort;
                        break;
                    }
                    catch (SocketException ex)
                    {
                        lastBindError = ex;
                        udp?.Dispose();
                        udp = null;
                    }
                }

                if (udp is null)
                    throw lastBindError ?? new SocketException((int)SocketError.AddressAlreadyInUse);

                state.Listening = true;
                state.ListeningPort = listeningPort;
                state.LastErrorCode = null;
                bindFailureLogged = false;
                if (listeningPort == discoveryPort)
                {
                    logger.LogInformation("UDP discovery listening on 0.0.0.0:{Port}", listeningPort);
                }
                else
                {
                    logger.LogWarning(
                        "UDP discovery port {PreferredPort} is unavailable; listening on fallback port {Port}.",
                        discoveryPort,
                        listeningPort);
                }

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
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "UDP discovery request failed");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (SocketException ex)
            {
                state.Listening = false;
                state.ListeningPort = null;
                state.LastErrorCode = "UDP_DISCOVERY_BIND_FAILED";
                if (!bindFailureLogged)
                {
                    logger.LogWarning(
                        ex,
                        "UDP discovery could not bind any port in the fallback range beginning at {Port}; retrying while REST remains available.",
                        discoveryPort);
                    bindFailureLogged = true;
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
            finally
            {
                udp?.Dispose();
                state.Listening = false;
                state.ListeningPort = null;
            }
        }

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
