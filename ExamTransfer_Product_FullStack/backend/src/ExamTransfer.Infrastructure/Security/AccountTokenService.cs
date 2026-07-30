using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class AccountTokenService : IAccountTokenService
{
    private readonly byte[] key;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    public AccountTokenService(IOptions<ExamTransferOptions> options)
    {
        var configured = options.Value.Security.TokenSigningKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var seed = $"{Environment.MachineName}|ExamTransfer|account-token-v1";
            configured = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        }

        key = SHA256.HashData(Encoding.UTF8.GetBytes(configured + "|account"));
    }

    public IssuedToken IssueAccountToken(Guid userId, Guid loginSessionId, UserRole role, string? organizationId, string deviceId, TimeSpan lifetime)
    {
        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        var payload = new AccountTokenPayload(
            userId,
            loginSessionId,
            role,
            organizationId,
            deviceId,
            expires.ToUnixTimeSeconds());
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, json));
        var signature = Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body)));
        return new IssuedToken($"{body}.{signature}", expires);
    }

    public AccountTokenPrincipal? ValidateAccountToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        if (parts[0].Length == 0 || parts[1].Length == 0) return null;

        byte[] actual;
        try
        {
            actual = FromBase64Url(parts[1]);
        }
        catch
        {
            return null;
        }

        // Kiểm tra canonical Base64Url: re-encode lại phải giống hệt phần signature gốc.
        // Điều này ngăn chặn bypass bằng cách dùng các ký tự Base64 tương đương về mặt
        // byte nhưng khác nhau về chuỗi ký tự (vd: ký tự cuối của 43-char base64url
        // chỉ mang 4 bits ý nghĩa, nên 4 ký tự khác nhau có thể decode ra cùng bytes).
        if (!string.Equals(parts[1], Base64Url(actual), StringComparison.Ordinal)) return null;

        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(parts[0]));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

        AccountTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AccountTokenPayload>(FromBase64Url(parts[0]), json);
        }
        catch
        {
            return null;
        }

        if (payload is null) return null;
        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        if (expires <= DateTimeOffset.UtcNow) return null;

        return new AccountTokenPrincipal(
            payload.UserId,
            payload.LoginSessionId,
            payload.Role,
            payload.OrganizationId,
            payload.DeviceId,
            expires);
    }

    public string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        value += (value.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(value);
    }

    private sealed record AccountTokenPayload(
        Guid UserId,
        Guid LoginSessionId,
        UserRole Role,
        string? OrganizationId,
        string DeviceId,
        long Exp);
}
