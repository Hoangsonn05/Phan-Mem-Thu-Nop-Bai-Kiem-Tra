using System.Security.Cryptography;
using System.Text;
using ExamTransfer.Application;
using Microsoft.Extensions.Caching.Memory;

namespace ExamTransfer.Infrastructure.Security;

public sealed class LoginChallengeService(IMemoryCache cache) : ILoginChallengeService
{
    private const string Prefix = "login-challenge:";

    public IssuedToken IssueChallenge(Guid userId, string deviceId, string machineName, TimeSpan lifetime)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        cache.Set(
            Prefix + Hash(token),
            new LoginChallenge(userId, deviceId, machineName, expires),
            expires);
        return new IssuedToken(token, expires);
    }

    public LoginChallenge? ValidateAndConsume(string challengeToken, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(challengeToken) || string.IsNullOrWhiteSpace(deviceId))
            return null;

        var key = Prefix + Hash(challengeToken);
        if (!cache.TryGetValue<LoginChallenge>(key, out var challenge) || challenge is null)
            return null;

        if (!string.Equals(challenge.DeviceId, deviceId, StringComparison.Ordinal))
            return null;

        cache.Remove(key);
        return challenge.ExpiresAtUtc > DateTimeOffset.UtcNow ? challenge : null;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
