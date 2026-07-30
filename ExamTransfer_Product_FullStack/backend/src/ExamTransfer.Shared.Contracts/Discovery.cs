using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExamTransfer.Shared.Contracts;

public static class DiscoveryProtocol
{
    public const string ProtocolVersion = "ExamTransfer/2";
    public const int DefaultPort = 40550;
    public const string Accepted = "ACCEPTED";
    public const string MalformedResponse = "DISCOVERY_RESPONSE_INVALID";
    public const string ProtocolMismatch = "DISCOVERY_PROTOCOL_MISMATCH";
    public const string PortMismatch = "DISCOVERY_PORT_MISMATCH";
    public const string BuildMismatch = "CLIENT_SERVER_BUILD_MISMATCH";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static byte[] CreateRequest(string requestId, string? roomCode = null)
    {
        if (!IsValidRequestId(requestId))
            throw new ArgumentException("Discovery request ID must be a 32-character hexadecimal nonce.", nameof(requestId));

        var normalizedRoomCode = string.IsNullOrWhiteSpace(roomCode)
            ? null
            : RoomCodeRules.Normalize(roomCode);
        if (normalizedRoomCode is not null && !RoomCodeRules.IsValid(normalizedRoomCode))
            throw new ArgumentException(RoomCodeRules.ValidationMessage, nameof(roomCode));

        return JsonSerializer.SerializeToUtf8Bytes(
            new DiscoveryRequestDto(ProtocolVersion, requestId.ToLowerInvariant(), normalizedRoomCode),
            Json);
    }

    public static bool TryParseRequest(ReadOnlySpan<byte> payload, out DiscoveryRequestDto? request)
    {
        request = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<DiscoveryRequestDto>(payload, Json);
            if (parsed is null
                || !string.Equals(parsed.Protocol, ProtocolVersion, StringComparison.Ordinal)
                || !IsValidRequestId(parsed.RequestId))
                return false;

            var roomCode = string.IsNullOrWhiteSpace(parsed.RoomCode)
                ? null
                : RoomCodeRules.Normalize(parsed.RoomCode);
            if (roomCode is not null && !RoomCodeRules.IsValid(roomCode))
                return false;

            request = parsed with
            {
                RequestId = parsed.RequestId.ToLowerInvariant(),
                RoomCode = roomCode
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseResponse(
        ReadOnlySpan<byte> payload,
        string expectedRequestId,
        out DiscoveryServerDto? server)
    {
        var result = ValidateResponse(
            payload,
            expectedRequestId,
            ReleaseIdentity.BuildId,
            DefaultPort);
        server = result.Server;
        return result.Code == Accepted;
    }

    public static DiscoveryResponseValidation ValidateResponse(
        ReadOnlySpan<byte> payload,
        string expectedRequestId,
        string expectedBuildId,
        int expectedDiscoveryPort)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<DiscoveryServerDto>(payload, Json);
            if (parsed is null)
                return new(MalformedResponse, null);
            if (!string.Equals(parsed.Protocol, ProtocolVersion, StringComparison.Ordinal))
                return new(ProtocolMismatch, null);
            if (parsed.DiscoveryPort != expectedDiscoveryPort)
                return new(PortMismatch, null);
            if (string.IsNullOrWhiteSpace(parsed.BuildId)
                || !string.Equals(parsed.BuildId, expectedBuildId, StringComparison.Ordinal))
                return new(BuildMismatch, null);
            if (!string.Equals(parsed.RequestId, expectedRequestId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(parsed.ServerName)
                || !IPAddress.TryParse(parsed.Address, out var address)
                || !IsUsableEndpointAddress(address)
                || parsed.Port is <= 0 or > 65535
                || string.IsNullOrWhiteSpace(parsed.Fingerprint)
                || string.IsNullOrWhiteSpace(parsed.ServerId)
                || parsed.ActiveRoomCount < 0)
            {
                return new(MalformedResponse, null);
            }

            var server = parsed with
            {
                ServerName = parsed.ServerName.Trim(),
                Address = address.ToString(),
                Fingerprint = parsed.Fingerprint.Trim().ToLowerInvariant(),
                ServerId = parsed.ServerId.Trim(),
                RequestId = parsed.RequestId!.Trim().ToLowerInvariant(),
                Sessions = NormalizeSessions(parsed, address)
            };
            return new(Accepted, server);
        }
        catch (JsonException)
        {
            return new(MalformedResponse, null);
        }
    }

    public static bool IsUsableEndpointAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork
            || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.Broadcast))
            return false;

        var bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static IReadOnlyList<OpenSessionDiscoveryDto> NormalizeSessions(
        DiscoveryServerDto server,
        IPAddress advertisedAddress)
    {
        if (server.Sessions is null || server.Sessions.Count == 0)
            return [];

        var authority = new UriBuilder(Uri.UriSchemeHttp, advertisedAddress.ToString(), server.Port).Uri;
        return server.Sessions
            .Where(x => x.ServerId.Equals(server.ServerId, StringComparison.OrdinalIgnoreCase)
                && x.AccessMode == SessionAccessMode.LanOnly
                && x.SessionState == SessionStatus.Waiting
                && Uri.TryCreate(x.BaseAddress, UriKind.Absolute, out var endpoint)
                && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
                && endpoint.Host.Equals(authority.Host, StringComparison.OrdinalIgnoreCase)
                && endpoint.Port == authority.Port)
            .Select(x => x with
            {
                RoomCode = RoomCodeRules.Normalize(x.RoomCode),
                BaseAddress = authority.GetLeftPart(UriPartial.Authority),
                ProtocolVersion = ProtocolVersion
            })
            .ToList();
    }

    private static bool IsValidRequestId(string? value) =>
        value?.Length == 32 && value.All(Uri.IsHexDigit);
}

public sealed record DiscoveryRequestDto(
    string Protocol,
    string RequestId,
    string? RoomCode);

public sealed record DiscoveryServerDto(
    string Protocol,
    string ServerName,
    string Address,
    int Port,
    string Fingerprint,
    int ActiveRoomCount,
    string Version,
    DateTimeOffset ServerNowUtc,
    string? ServerId = null,
    string? RequestId = null,
    IReadOnlyList<OpenSessionDiscoveryDto>? Sessions = null,
    string? BuildId = null,
    int DiscoveryPort = 0,
    string? SemanticVersion = null)
{
    [JsonIgnore]
    public string BaseAddress => $"http://{Address}:{Port}";
}

public sealed record LocalServerIdentityDto(
    string Product,
    string ServerId,
    string Protocol,
    int DiscoveryPort,
    string BuildId,
    string SemanticVersion,
    string AdvertisedAddress,
    int ServerPort);

public sealed record DiscoveryResponseValidation(
    string Code,
    DiscoveryServerDto? Server);
