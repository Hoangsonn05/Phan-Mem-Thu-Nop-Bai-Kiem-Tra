using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class LanDiscoveryService(int discoveryPort = DiscoveryProtocol.DefaultPort) : ILanDiscoveryService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

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

    public async Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var servers = await DiscoverAsync(timeout, ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var requests = servers.Select(async server =>
        {
            try
            {
                var endpoint = new Uri(new Uri(server.BaseAddress + "/"), "api/v1/discovery/open-sessions");
                var response = await http.GetFromJsonAsync<ApiResponse<IReadOnlyList<OpenSessionDiscoveryDto>>>(endpoint, Json, ct);
                return response?.Success == true ? response.Data ?? [] : [];
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return [];
            }
        });
        var batches = await Task.WhenAll(requests);
        return batches
            .SelectMany(x => x)
            .Where(x => x.AccessMode == SessionAccessMode.LanOnly && x.SessionState == SessionStatus.Waiting)
            .GroupBy(x => $"{x.ServerId}|{x.SessionId}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(room => room.RespondedAtUtc).First())
            .OrderBy(x => x.ScheduledStartUtc)
            .ThenBy(x => x.ClassCode)
            .ThenBy(x => x.RoomCode)
            .ToList();
    }
}
