using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class LanDiscoveryService(
    int discoveryPort = DiscoveryProtocol.DefaultPort,
    Func<IReadOnlyList<LanIpv4Interface>>? interfaceProvider = null) : ILanDiscoveryService
{
    // Diagnostic-only probe: a legacy server response is classified as
    // DISCOVERY_PROTOCOL_MISMATCH and is never accepted as a room endpoint.
    private static readonly byte[] LegacyProtocolDiagnosticProbe =
        Encoding.ASCII.GetBytes("EXAMTRANSFER_DISCOVER_V1");

    private readonly Func<IReadOnlyList<LanIpv4Interface>> interfaces =
        interfaceProvider ?? LanIpv4Network.GetSystemInterfaces;

    public async Task<LanDiscoverySnapshot> DiscoverSnapshotAsync(
        TimeSpan timeout,
        string? roomCode = null,
        CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var normalizedRoomCode = string.IsNullOrWhiteSpace(roomCode)
            ? null
            : RoomCodeRules.Normalize(roomCode);
        if (normalizedRoomCode is not null && !RoomCodeRules.IsValid(normalizedRoomCode))
            throw new LanDiscoveryException("ROOM_CODE_INVALID", RoomCodeRules.ValidationMessage);

        var requestId = Guid.NewGuid().ToString("N");
        var decisions = LanIpv4Network.Evaluate(interfaces());
        foreach (var decision in decisions)
        {
            FrontendLogger.LogMessage(
                $"request_id={requestId}; interface={decision.Interface.Name}; address={decision.Interface.Address}/{decision.Interface.PrefixLength}; included={decision.Included}; reason={decision.Reason}",
                "LanDiscovery.Interface");
        }

        var selected = decisions.Where(x => x.Included).Select(x => x.Interface).ToList();
        if (selected.Count == 0)
        {
            FrontendLogger.LogMessage(
                $"request_id={requestId}; result=no_usable_interface; room_filter={MaskRoomCode(normalizedRoomCode)}",
                "LanDiscovery");
            return new([], [], requestId, 0);
        }

        var responses = new ConcurrentBag<DiscoveryServerDto>();
        var rejectionCodes = new ConcurrentBag<string>();
        await Task.WhenAll(selected.Select(candidate =>
            ScanInterfaceAsync(
                candidate,
                requestId,
                normalizedRoomCode,
                timeout,
                responses,
                rejectionCodes,
                ct)));
        ct.ThrowIfCancellationRequested();

        var servers = responses
            .GroupBy(
                x => $"{x.ServerId}|{x.Address}:{x.Port}",
                StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(server => server.ServerNowUtc).First())
            .OrderBy(x => x.ServerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rooms = responses
            .SelectMany(x => x.Sessions ?? [])
            .Where(x => x.AccessMode == SessionAccessMode.LanOnly
                && x.SessionState == SessionStatus.Waiting)
            .GroupBy(
                x => $"{x.ServerId}|{x.SessionId}|{x.BaseAddress}",
                StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(room => room.RespondedAtUtc).First())
            .OrderBy(x => x.ScheduledStartUtc)
            .ThenBy(x => x.ExamTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RoomCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FrontendLogger.LogMessage(
            $"request_id={requestId}; responses={responses.Count}; servers={servers.Count}; sessions={rooms.Count}; room_filter={MaskRoomCode(normalizedRoomCode)}",
            "LanDiscovery.Result");
        return new(
            servers,
            rooms,
            requestId,
            responses.Count,
            rejectionCodes.Distinct(StringComparer.Ordinal).ToList());
    }

    public async Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken ct = default) =>
        (await DiscoverSnapshotAsync(timeout, null, ct)).Servers;

    public async Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(
        TimeSpan timeout,
        CancellationToken ct = default) =>
        (await DiscoverSnapshotAsync(timeout, null, ct)).Rooms;

    public async Task<OpenSessionDiscoveryDto?> DiscoverByRoomCodeAsync(
        string roomCode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var normalized = RoomCodeRules.Normalize(roomCode);
        if (!RoomCodeRules.IsValid(normalized))
            throw new LanDiscoveryException("ROOM_CODE_INVALID", RoomCodeRules.ValidationMessage);

        var snapshot = await DiscoverSnapshotAsync(timeout, normalized, cancellationToken);
        var matches = snapshot.Rooms
            .Where(x => x.RoomCode.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count > 1)
            throw new LanDiscoveryException(
                "ROOM_CODE_AMBIGUOUS",
                "Mã phòng xuất hiện trên nhiều máy giáo viên trong mạng LAN.");
        if (matches.Count == 1)
            return matches[0];
        var rejection = snapshot.RejectionCodes?.FirstOrDefault(code =>
            code is DiscoveryProtocol.ProtocolMismatch
                or DiscoveryProtocol.BuildMismatch
                or DiscoveryProtocol.PortMismatch);
        if (rejection is not null)
            throw new LanDiscoveryException(
                rejection,
                rejection switch
                {
                    DiscoveryProtocol.ProtocolMismatch =>
                        "Đã tìm thấy ExamTransfer Local Server dùng discovery protocol không tương thích. Hãy cập nhật đồng bộ máy giáo viên và học sinh.",
                    DiscoveryProtocol.BuildMismatch =>
                        "Client và Local Server không cùng BuildId. Hãy cài lại cùng bộ cài ExamTransfer.",
                    _ =>
                        $"Local Server đang quảng bá sai cổng discovery; phiên bản này yêu cầu UDP {DiscoveryProtocol.DefaultPort}."
                });
        if (snapshot.ResponseCount > 0)
            throw new LanDiscoveryException(
                "SERVER_FOUND_ROOM_NOT_FOUND",
                "Đã tìm thấy máy giáo viên nhưng phòng không còn mở hoặc không nhận thêm học sinh.");
        return null;
    }

    private async Task ScanInterfaceAsync(
        LanIpv4Interface candidate,
        string requestId,
        string? roomCode,
        TimeSpan timeout,
        ConcurrentBag<DiscoveryServerDto> responses,
        ConcurrentBag<string> rejectionCodes,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(candidate.Address, 0));

            var request = DiscoveryProtocol.CreateRequest(requestId, roomCode);
            var targets = new[] { candidate.DirectedBroadcast, IPAddress.Broadcast }.Distinct().ToList();
            foreach (var target in targets)
            {
                try
                {
                    await udp.SendAsync(
                        request,
                        new IPEndPoint(target, discoveryPort),
                        cancellationToken);
                    await udp.SendAsync(
                        LegacyProtocolDiagnosticProbe,
                        new IPEndPoint(target, discoveryPort),
                        cancellationToken);
                    FrontendLogger.LogMessage(
                        $"request_id={requestId}; interface={candidate.Name}; local={candidate.Address}; target={target}:{discoveryPort}; protocol_probe=v2_plus_legacy_mismatch_detection",
                        "LanDiscovery.Broadcast");
                }
                catch (SocketException ex)
                {
                    FrontendLogger.LogMessage(
                        $"request_id={requestId}; interface={candidate.Name}; target={target}:{discoveryPort}; send_error={ex.SocketErrorCode}",
                        "LanDiscovery.Broadcast");
                }
            }

            while (!linked.IsCancellationRequested)
            {
                try
                {
                    var received = await udp.ReceiveAsync(linked.Token);
                    var validation = DiscoveryProtocol.ValidateResponse(
                        received.Buffer,
                        requestId,
                        ReleaseIdentity.BuildId,
                        DiscoveryProtocol.DefaultPort);
                    if (validation.Code != DiscoveryProtocol.Accepted
                        || validation.Server is null)
                    {
                        rejectionCodes.Add(validation.Code);
                        FrontendLogger.LogMessage(
                            $"request_id={requestId}; remote={received.RemoteEndPoint}; discarded={validation.Code}",
                            "LanDiscovery.Response");
                        continue;
                    }
                    var server = validation.Server;
                    responses.Add(server);
                    FrontendLogger.LogMessage(
                        $"request_id={requestId}; remote={received.RemoteEndPoint}; endpoint={server.BaseAddress}; server_id={server.ServerId}; sessions={server.Sessions?.Count ?? 0}",
                        "LanDiscovery.Response");
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    FrontendLogger.LogMessage(
                        $"request_id={requestId}; interface={candidate.Name}; receive_error={ex.SocketErrorCode}",
                        "LanDiscovery.Response");
                    break;
                }
            }
        }
        catch (SocketException ex)
        {
            FrontendLogger.LogMessage(
                $"request_id={requestId}; interface={candidate.Name}; adapter_error={ex.SocketErrorCode}",
                "LanDiscovery.Interface");
        }
    }

    private static string MaskRoomCode(string? roomCode)
    {
        if (string.IsNullOrWhiteSpace(roomCode)) return "<none>";
        return roomCode.Length <= 2
            ? new string('*', roomCode.Length)
            : $"{roomCode[0]}{new string('*', roomCode.Length - 2)}{roomCode[^1]}";
    }
}
