using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class SessionTokenService : ISessionTokenService
{
    private readonly byte[] _key;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public SessionTokenService(IOptions<ExamTransferOptions> options)
    {
        var configured = options.Value.Security.TokenSigningKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var seed = $"{Environment.MachineName}|ExamTransfer|session-token-v1";
            configured = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        }
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    }

    public IssuedToken IssueParticipantToken(Guid sessionId, Guid participantId, Guid userId, string deviceId, ParticipantStatus status, TimeSpan lifetime)
    {
        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        var payload = new TokenPayload(sessionId, participantId, userId, deviceId, expires.ToUnixTimeSeconds(), UserRole.Student, status);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _json);
        var body = Base64Url(payloadBytes);
        var signature = Base64Url(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(body)));
        return new IssuedToken($"{body}.{signature}", expires);
    }

    public TokenPrincipal? Validate(string token)
    {
        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(parts[0]));
        byte[] actual;
        try { actual = FromBase64Url(parts[1]); }
        catch { return null; }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

        TokenPayload? payload;
        try { payload = JsonSerializer.Deserialize<TokenPayload>(FromBase64Url(parts[0]), _json); }
        catch { return null; }
        if (payload is null) return null;
        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        if (expires <= DateTimeOffset.UtcNow) return null;
        return new TokenPrincipal(payload.SessionId, payload.ParticipantId, payload.UserId, payload.DeviceId, expires, payload.Role, payload.ParticipantStatus);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        value += (value.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(value);
    }

    private sealed record TokenPayload(Guid SessionId, Guid ParticipantId, Guid UserId, string DeviceId, long Exp, UserRole Role, ParticipantStatus ParticipantStatus);
}

