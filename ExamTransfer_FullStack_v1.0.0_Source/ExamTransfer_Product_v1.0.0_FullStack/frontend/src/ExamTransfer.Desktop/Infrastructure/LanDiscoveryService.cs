using System.Net;
using System.Net.Sockets;
using System.Text;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class LanDiscoveryService(int discoveryPort = DiscoveryProtocol.DefaultPort) : ILanDiscoveryService
{
    public async Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var request = Encoding.UTF8.GetBytes(DiscoveryProtocol.RequestMagic);
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Broadcast, discoveryPort), ct);

        var found = new Dictionary<string, DiscoveryServerDto>(StringComparer.OrdinalIgnoreCase);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                var received = await udp.ReceiveAsync(linked.Token);
                if (!DiscoveryProtocol.TryParseResponse(received.Buffer, out var server) || server is null)
                    continue;

                var address = IPAddress.Any.ToString() == server.Address
                    ? received.RemoteEndPoint.Address.ToString()
                    : server.Address;
                var normalized = server with { Address = address };
                found[$"{normalized.Fingerprint}|{normalized.Address}:{normalized.Port}"] = normalized;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }

        ct.ThrowIfCancellationRequested();
        return found.Values.OrderBy(x => x.ServerName).ThenBy(x => x.Address).ToList();
    }
}
