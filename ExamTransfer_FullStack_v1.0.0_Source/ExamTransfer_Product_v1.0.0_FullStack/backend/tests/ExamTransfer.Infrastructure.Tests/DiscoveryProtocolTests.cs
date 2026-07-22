using System.Text;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class DiscoveryProtocolTests
{
    [Fact]
    public void TryParseResponse_AcceptsValidProtocolPayload()
    {
        var json = """
            {"protocol":"ExamTransfer/1","serverName":"Phòng máy 1","address":"192.168.10.5","port":5049,"fingerprint":"abc123","activeRoomCount":2,"version":"1.0.0","serverNowUtc":"2026-07-22T08:00:00Z"}
            """;

        var parsed = DiscoveryProtocol.TryParseResponse(Encoding.UTF8.GetBytes(json), out var server);

        Assert.True(parsed);
        Assert.NotNull(server);
        Assert.Equal("192.168.10.5", server.Address);
        Assert.Equal(5049, server.Port);
        Assert.Equal(2, server.ActiveRoomCount);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"protocol\":\"Other/1\",\"serverName\":\"x\",\"address\":\"192.168.1.2\",\"port\":5049,\"fingerprint\":\"f\"}")]
    [InlineData("{\"protocol\":\"ExamTransfer/1\",\"serverName\":\"x\",\"address\":\"not-an-ip\",\"port\":5049,\"fingerprint\":\"f\"}")]
    [InlineData("{\"protocol\":\"ExamTransfer/1\",\"serverName\":\"x\",\"address\":\"192.168.1.2\",\"port\":70000,\"fingerprint\":\"f\"}")]
    public void TryParseResponse_RejectsMalformedOrUntrustedPayload(string json)
    {
        Assert.False(DiscoveryProtocol.TryParseResponse(Encoding.UTF8.GetBytes(json), out var server));
        Assert.Null(server);
    }
}
