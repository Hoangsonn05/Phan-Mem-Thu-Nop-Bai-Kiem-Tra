using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExamTransfer.Shared.Contracts;

public static class DiscoveryProtocol
{
    public const string RequestMagic = "EXAMTRANSFER_DISCOVER_V1";
    public const string ProtocolVersion = "ExamTransfer/1";
    public const int DefaultPort = 5050;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParseResponse(ReadOnlySpan<byte> payload, out DiscoveryServerDto? server)
    {
        server = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<DiscoveryServerDto>(payload, Json);
            if (parsed is null
                || !string.Equals(parsed.Protocol, ProtocolVersion, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(parsed.ServerName)
                || !IPAddress.TryParse(parsed.Address, out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || parsed.Port is <= 0 or > 65535
                || string.IsNullOrWhiteSpace(parsed.Fingerprint)
                || parsed.ActiveRoomCount < 0)
            {
                return false;
            }

            server = parsed with
            {
                ServerName = parsed.ServerName.Trim(),
                Address = address.ToString(),
                Fingerprint = parsed.Fingerprint.Trim().ToLowerInvariant(),
                ServerId = string.IsNullOrWhiteSpace(parsed.ServerId)
                    ? parsed.Fingerprint.Trim().ToLowerInvariant()
                    : parsed.ServerId.Trim()
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record DiscoveryServerDto(
    string Protocol,
    string ServerName,
    string Address,
    int Port,
    string Fingerprint,
    int ActiveRoomCount,
    string Version,
    DateTimeOffset ServerNowUtc,
    string? ServerId = null)
{
    [JsonIgnore]
    public string BaseAddress => $"http://{Address}:{Port}";
}
