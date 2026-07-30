using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class DiscoveryProtocolTests
{
    private const string RequestId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Request_RoundTripsNonceAndNormalizedRoomFilter()
    {
        var payload = DiscoveryProtocol.CreateRequest(RequestId.ToUpperInvariant(), " room42 ");

        Assert.True(DiscoveryProtocol.TryParseRequest(payload, out var request));
        Assert.NotNull(request);
        Assert.Equal(RequestId, request.RequestId);
        Assert.Equal("ROOM42", request.RoomCode);
    }

    [Fact]
    public void TryParseResponse_AcceptsMatchingNonceAndSafeEndpoint()
    {
        var json = $$"""
            {"protocol":"{{DiscoveryProtocol.ProtocolVersion}}","serverName":"Phòng máy 1","address":"192.168.10.5","port":5048,"fingerprint":"abc123","activeRoomCount":2,"version":"1.0.0","serverNowUtc":"2026-07-22T08:00:00Z","serverId":"server-1","requestId":"{{RequestId}}","sessions":[],"buildId":"{{ReleaseIdentity.BuildId}}","discoveryPort":{{DiscoveryProtocol.DefaultPort}},"semanticVersion":"{{ReleaseIdentity.SemanticVersion}}"}
            """;

        var parsed = DiscoveryProtocol.TryParseResponse(
            Encoding.UTF8.GetBytes(json),
            RequestId,
            out var server);

        Assert.True(parsed);
        Assert.NotNull(server);
        Assert.Equal("192.168.10.5", server.Address);
        Assert.Equal(5048, server.Port);
        Assert.Equal(2, server.ActiveRoomCount);
    }

    [Fact]
    public void FixedV2Port_Is40550()
    {
        Assert.Equal("ExamTransfer/2", DiscoveryProtocol.ProtocolVersion);
        Assert.Equal(40550, DiscoveryProtocol.DefaultPort);
    }

    [Fact]
    public void ValidateResponse_ReturnsTypedProtocolAndBuildMismatch()
    {
        var wrongProtocol = $$"""
            {"protocol":"ExamTransfer/1","serverName":"Teacher","address":"192.168.1.7","port":5048,"fingerprint":"f","activeRoomCount":0,"version":"1.3.2","serverNowUtc":"2026-07-28T08:00:00Z","serverId":"server-1","requestId":"{{RequestId}}","sessions":[],"buildId":"{{ReleaseIdentity.BuildId}}","discoveryPort":40550}
            """;
        var wrongBuild = $$"""
            {"protocol":"ExamTransfer/2","serverName":"Teacher","address":"192.168.1.7","port":5048,"fingerprint":"f","activeRoomCount":0,"version":"1.3.2","serverNowUtc":"2026-07-28T08:00:00Z","serverId":"server-1","requestId":"{{RequestId}}","sessions":[],"buildId":"same-version-different-build","discoveryPort":40550}
            """;

        Assert.Equal(
            DiscoveryProtocol.ProtocolMismatch,
            DiscoveryProtocol.ValidateResponse(
                Encoding.UTF8.GetBytes(wrongProtocol),
                RequestId,
                ReleaseIdentity.BuildId,
                DiscoveryProtocol.DefaultPort).Code);
        Assert.Equal(
            DiscoveryProtocol.BuildMismatch,
            DiscoveryProtocol.ValidateResponse(
                Encoding.UTF8.GetBytes(wrongBuild),
                RequestId,
                ReleaseIdentity.BuildId,
                DiscoveryProtocol.DefaultPort).Code);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("""{"protocol":"Other/1","serverName":"x","address":"192.168.1.2","port":5048,"fingerprint":"f","serverId":"s","requestId":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"protocol":"ExamTransfer/2","serverName":"x","address":"not-an-ip","port":5048,"fingerprint":"f","serverId":"s","requestId":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"protocol":"ExamTransfer/2","serverName":"x","address":"127.0.0.1","port":5048,"fingerprint":"f","serverId":"s","requestId":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"protocol":"ExamTransfer/2","serverName":"x","address":"0.0.0.0","port":5048,"fingerprint":"f","serverId":"s","requestId":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"protocol":"ExamTransfer/2","serverName":"x","address":"192.168.1.2","port":70000,"fingerprint":"f","serverId":"s","requestId":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"protocol":"ExamTransfer/2","serverName":"x","address":"192.168.1.2","port":5048,"fingerprint":"f","serverId":"s","requestId":"ffffffffffffffffffffffffffffffff"}""")]
    public void TryParseResponse_RejectsMalformedProtocolNonceOrEndpoint(string json)
    {
        Assert.False(DiscoveryProtocol.TryParseResponse(
            Encoding.UTF8.GetBytes(json),
            RequestId,
            out var server));
        Assert.Null(server);
    }

    [Fact]
    public void MultiNicFixture_SelectsWifiSubnetAndComputesDirectedBroadcast()
    {
        var adapters = new[]
        {
            Candidate("vmnet1", "VMware Network Adapter VMnet1", NetworkInterfaceType.Ethernet, "192.168.144.1", 24, false, 10),
            Candidate("vmnet8", "VMware Network Adapter VMnet8", NetworkInterfaceType.Ethernet, "192.168.46.1", 24, false, 20),
            Candidate("wsl", "vEthernet WSL Hyper-V", NetworkInterfaceType.Ethernet, "172.19.16.1", 20, false, 5),
            Candidate("wifi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211, "192.168.1.7", 24, true, 25)
        };

        var selected = LanIpv4Network.SelectUsable(adapters, IPAddress.Parse("192.168.1.20"));

        var wifi = Assert.Single(selected);
        Assert.Equal("192.168.1.7", wifi.Address.ToString());
        Assert.Equal("192.168.1.255", wifi.DirectedBroadcast.ToString());
        Assert.Equal("172.31.143.255", LanIpv4Network.GetDirectedBroadcast(
            IPAddress.Parse("172.31.128.199"),
            20).ToString());
    }

    [Fact]
    public async Task RealUdpSocket_RequestResponse_ParsesExactSession()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var sessionId = Guid.NewGuid();
        var examId = Guid.NewGuid();
        var room = new OpenSessionDiscoveryDto(
            sessionId,
            "ROOM42",
            "Phòng thi",
            null,
            null,
            null,
            "Kiểm tra",
            "Cô Lan",
            SessionStatus.Waiting,
            true,
            40,
            0,
            null,
            null,
            SessionAccessMode.LanOnly,
            "server-1",
            "Teacher",
            "http://192.168.1.7:5048",
            DateTimeOffset.UtcNow,
            DiscoveryProtocol.ProtocolVersion,
            "Tin",
            45,
            ExamDeliveryType.FileSubmission,
            SupervisionMode.None,
            SessionAdmissionMode.OpenRequest,
            examId);

        var responder = Task.Run(async () =>
        {
            var received = await server.ReceiveAsync();
            Assert.True(DiscoveryProtocol.TryParseRequest(received.Buffer, out var request));
            Assert.Equal("ROOM42", request!.RoomCode);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryServerDto(
                DiscoveryProtocol.ProtocolVersion,
                "Teacher",
                "192.168.1.7",
                5048,
                "fingerprint",
                1,
                "1.3.1",
                DateTimeOffset.UtcNow,
                "server-1",
                request.RequestId,
                [room],
                ReleaseIdentity.BuildId,
                DiscoveryProtocol.DefaultPort,
                ReleaseIdentity.SemanticVersion));
            await server.SendAsync(payload, received.RemoteEndPoint);
        });

        await client.SendAsync(
            DiscoveryProtocol.CreateRequest(RequestId, "ROOM42"),
            serverEndpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var response = await client.ReceiveAsync(timeout.Token);
        await responder;

        Assert.True(DiscoveryProtocol.TryParseResponse(
            response.Buffer,
            RequestId,
            out var parsed));
        var parsedRoom = Assert.Single(parsed!.Sessions!);
        Assert.Equal(sessionId, parsedRoom.SessionId);
        Assert.Equal(examId, parsedRoom.ExamId);
        Assert.Equal("http://192.168.1.7:5048", parsedRoom.BaseAddress);
    }

    private static LanIpv4Interface Candidate(
        string id,
        string description,
        NetworkInterfaceType type,
        string address,
        int prefix,
        bool gateway,
        int metric) =>
        new(
            id,
            id,
            description,
            type,
            OperationalStatus.Up,
            IPAddress.Parse(address),
            prefix,
            gateway,
            metric);
}
