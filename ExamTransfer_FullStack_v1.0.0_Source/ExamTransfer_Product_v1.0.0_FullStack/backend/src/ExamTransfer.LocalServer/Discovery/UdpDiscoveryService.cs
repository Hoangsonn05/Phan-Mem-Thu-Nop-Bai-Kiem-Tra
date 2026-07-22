using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Discovery;

public sealed class UdpDiscoveryService(IServiceScopeFactory scopeFactory, IOptions<ExamTransferOptions> options, ILogger<UdpDiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Discovery.Enabled) return;
        var discoveryPort = options.Value.Discovery.Port;

        if (IPGlobalProperties
            .GetIPGlobalProperties()
            .GetActiveUdpListeners()
            .Any(endpoint => endpoint.Port == discoveryPort))
        {
            logger.LogWarning(
                "UDP discovery port {Port} is already in use. LAN auto-discovery is disabled for this run.",
                discoveryPort);

            return;
        }

        using var udp = new UdpClient(
            new IPEndPoint(IPAddress.Any, discoveryPort));
        logger.LogInformation("UDP discovery listening on {Port}", options.Value.Discovery.Port);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var received = await udp.ReceiveAsync(stoppingToken);
                var text = Encoding.UTF8.GetString(received.Buffer).Trim();
                if (!text.Equals(options.Value.Discovery.RequestMagic, StringComparison.Ordinal)) continue;
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rooms = await db.ExamSessionsSet.CountAsync(x => x.Status == SessionStatus.Waiting || x.Status == SessionStatus.InProgress || x.Status == SessionStatus.Paused || x.Status == SessionStatus.Collecting, stoppingToken);
                var address = options.Value.Server.PreferredIp ?? GetLanIp();
                var response = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryServerDto(
                    DiscoveryProtocol.ProtocolVersion,
                    Environment.MachineName,
                    address,
                    options.Value.Server.Port,
                    MachineFingerprint(),
                    rooms,
                    typeof(UdpDiscoveryService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    DateTimeOffset.UtcNow));
                await udp.SendAsync(response, received.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "UDP discovery request failed"); }
        }
    }

    private static string GetLanIp()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var address = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address));
            if (address is not null) return address.Address.ToString();
        }
        return "127.0.0.1";
    }

    private static string MachineFingerprint()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|ExamTransfer|discovery"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}

