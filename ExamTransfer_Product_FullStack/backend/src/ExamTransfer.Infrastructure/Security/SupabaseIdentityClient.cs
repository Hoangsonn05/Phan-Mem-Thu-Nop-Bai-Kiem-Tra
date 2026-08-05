using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class SupabaseIdentityClient(
    HttpClient http,
    IOptions<ExamTransferOptions> options,
    ILogger<SupabaseIdentityClient> logger,
    Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
    Func<int>? retryJitterMilliseconds = null) : IExternalIdentityProvider, IExternalAccountSecurityService
{
    private readonly CloudOptions cloud = options.Value.Cloud;
    private readonly AuthOptions auth = options.Value.Auth;
    private readonly Func<TimeSpan, CancellationToken, Task> delay =
        retryDelay ?? Task.Delay;
    private readonly Func<int> jitterMilliseconds =
        retryJitterMilliseconds ?? (() => Random.Shared.Next(25, 101));

    public async Task<ExternalIdentityResult> AuthenticateAsync(AccountLoginRequest request, CancellationToken cancellationToken)
    {
        var supabaseUrl = cloud.SupabaseUrl?.TrimEnd('/');
        var publishableKey = cloud.EffectivePublishableKey;
        if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(publishableKey))
        {
            throw new ApiException(
                ErrorCodes.SupabaseNotConfigured,
                "Supabase URL và publishable key chưa được cấu hình.",
                503);
        }

        var signInEmail = ResolveSignInEmail(request.Account);
        logger.LogInformation(
            "Supabase login step ConfigPreflight Host={Host} OrganizationId={OrganizationId}.",
            Uri.TryCreate(supabaseUrl, UriKind.Absolute, out var configuredUri)
                ? configuredUri.Host
                : "<invalid>",
            cloud.OrganizationId);
        using var response = await SendAuthenticationRequestWithRetryAsync(
            () => CreatePasswordGrantMessage(
                supabaseUrl,
                publishableKey,
                signInEmail,
                request.Password),
            ErrorCodes.AuthProviderUnavailable,
            "Không thể kết nối Supabase Auth.",
            "LocalServerAuth",
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new ApiException(
                ErrorCodes.InvalidCredentials,
                "Tài khoản hoặc mật khẩu không đúng.",
                401);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                ErrorCodes.AuthProviderUnavailable,
                "Supabase Auth tạm thời không khả dụng.",
                503);
        }

        string providerUserId;
        string? email;
        string? refreshToken;
        string accessToken;
        int expiresIn;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var user = root.GetProperty("user");
            providerUserId = user.GetProperty("id").GetString() ?? string.Empty;
            accessToken = root.TryGetProperty("access_token", out var accessTokenElement)
                ? accessTokenElement.GetString() ?? string.Empty
                : string.Empty;
            email = user.TryGetProperty("email", out var emailElement) ? emailElement.GetString() : null;
            refreshToken = root.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : null;
            expiresIn = root.TryGetProperty("expires_in", out var expiresElement)
                && expiresElement.TryGetInt32(out var seconds)
                    ? seconds
                    : 3600;
        }
        catch (JsonException)
        {
            throw InvalidAuthResponse();
        }
        catch (InvalidOperationException)
        {
            throw InvalidAuthResponse();
        }
        catch (KeyNotFoundException)
        {
            throw InvalidAuthResponse();
        }

        if (string.IsNullOrWhiteSpace(providerUserId))
            throw InvalidAuthResponse();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ApiException(
                ErrorCodes.AuthAccessTokenMissing,
                "Supabase Auth không trả về access token cần thiết để đọc hồ sơ ứng dụng.",
                503);
        }

        if (!TryReadJwtIdentity(accessToken, out var tokenSubject, out var tokenExpiresAt)
            || !string.Equals(tokenSubject, providerUserId, StringComparison.OrdinalIgnoreCase)
            || tokenExpiresAt <= DateTimeOffset.UtcNow)
            throw InvalidAuthResponse();

        logger.LogInformation("Supabase password authentication succeeded; requesting the application profile.");
        var profile = await GetProfileAsync(
            supabaseUrl,
            publishableKey,
            accessToken,
            providerUserId,
            cancellationToken);

        return new ExternalIdentityResult(
            providerUserId,
            request.Account.Trim(),
            email,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn) < tokenExpiresAt
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
                : tokenExpiresAt,
            profile,
            accessToken);
    }

    public async Task ChangePasswordAsync(
        ExternalPasswordChangeRequest request,
        CancellationToken cancellationToken)
    {
        var supabaseUrl = cloud.SupabaseUrl?.TrimEnd('/');
        var publishableKey = cloud.EffectivePublishableKey;
        if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(publishableKey))
        {
            throw new ApiException(
                ErrorCodes.SupabaseNotConfigured,
                "Supabase URL và publishable key chưa được cấu hình.",
                503);
        }

        var signInEmail = ResolveSignInEmail(request.Account);
        using var signInResponse = await SendAuthenticationRequestWithRetryAsync(
            () => CreatePasswordGrantMessage(
                supabaseUrl,
                publishableKey,
                signInEmail,
                request.CurrentPassword),
            ErrorCodes.AuthProviderUnavailable,
            "Không thể kết nối Supabase Auth để xác nhận mật khẩu hiện tại.",
            "LocalServerAuth",
            cancellationToken);

        if (signInResponse.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new ApiException(
                ErrorCodes.InvalidCurrentPassword,
                "Mật khẩu hiện tại không đúng.",
                401);
        }

        if (!signInResponse.IsSuccessStatusCode)
        {
            throw new ApiException(
                ErrorCodes.AuthProviderUnavailable,
                "Supabase Auth tạm thời không khả dụng.",
                503);
        }

        var session = await ReadPasswordSessionAsync(signInResponse, cancellationToken);
        if (!string.Equals(session.ProviderUserId, request.ProviderUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                ErrorCodes.Unauthorized,
                "Phiên Supabase không thuộc tài khoản đang đăng nhập.",
                401);
        }

        using var updateMessage = new HttpRequestMessage(
            HttpMethod.Put,
            $"{supabaseUrl}/auth/v1/user")
        {
            Content = JsonContent.Create(new { password = request.NewPassword })
        };
        AddSupabaseHeaders(updateMessage, publishableKey, session.AccessToken);

        using var updateResponse = await SendAsync(
            updateMessage,
            ErrorCodes.PasswordChangeFailed,
            "Không thể cập nhật mật khẩu trên Supabase Auth.",
            cancellationToken);

        if (updateResponse.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            throw new ApiException(
                ErrorCodes.PasswordPolicyRejected,
                "Supabase từ chối mật khẩu mới. Hãy chọn mật khẩu khác mạnh hơn.",
                422);
        }

        if (updateResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ApiException(
                ErrorCodes.PasswordChangeFailed,
                "Phiên Supabase không còn quyền cập nhật mật khẩu.",
                401);
        }

        if (!updateResponse.IsSuccessStatusCode)
        {
            throw new ApiException(
                ErrorCodes.PasswordChangeFailed,
                "Không thể cập nhật mật khẩu trên Supabase Auth.",
                503);
        }

        using var profileMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"{supabaseUrl}/rest/v1/rpc/complete_own_password_change")
        {
            Content = JsonContent.Create(new { })
        };
        AddSupabaseHeaders(profileMessage, publishableKey, session.AccessToken);
        if (!string.IsNullOrWhiteSpace(cloud.Schema))
        {
            profileMessage.Headers.TryAddWithoutValidation("Accept-Profile", cloud.Schema);
            profileMessage.Headers.TryAddWithoutValidation("Content-Profile", cloud.Schema);
        }

        using var profileResponse = await SendAsync(
            profileMessage,
            ErrorCodes.PasswordChangeFailed,
            "Mật khẩu đã đổi nhưng chưa thể cập nhật trạng thái hồ sơ.",
            cancellationToken);

        if (!profileResponse.IsSuccessStatusCode)
        {
            throw new ApiException(
                ErrorCodes.PasswordChangeFailed,
                "Mật khẩu đã đổi nhưng chưa thể cập nhật trạng thái hồ sơ. Hãy đăng nhập lại bằng mật khẩu mới và thử lại.",
                503);
        }

        var completed = false;
        try
        {
            var body = await profileResponse.Content.ReadAsStringAsync(cancellationToken);
            completed = bool.TryParse(body.Trim(), out var parsed) && parsed;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            completed = false;
        }

        if (!completed)
        {
            throw new ApiException(
                ErrorCodes.PasswordChangeFailed,
                "Mật khẩu đã đổi nhưng hồ sơ sinh viên không được cập nhật. Hãy đăng nhập lại bằng mật khẩu mới và thử lại.",
                503);
        }

        logger.LogInformation(
            "Supabase password change completed for provider user {ProviderUserId}.",
            request.ProviderUserId);
    }

    private static async Task<PasswordSession> ReadPasswordSessionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var user = root.GetProperty("user");
            var providerUserId = user.GetProperty("id").GetString() ?? string.Empty;
            var accessToken = root.TryGetProperty("access_token", out var tokenElement)
                ? tokenElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(providerUserId) || string.IsNullOrWhiteSpace(accessToken))
                throw InvalidAuthResponse();

            return new PasswordSession(providerUserId, accessToken);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw InvalidAuthResponse();
        }
    }

    private static bool TryReadJwtIdentity(
        string token,
        out string? subject,
        out DateTimeOffset expiresAt)
    {
        subject = null;
        expiresAt = default;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(
                payload.Length + ((4 - payload.Length % 4) % 4),
                '=');
            using var document = JsonDocument.Parse(
                Convert.FromBase64String(payload));
            subject = document.RootElement.TryGetProperty("sub", out var sub)
                ? sub.GetString()
                : null;
            expiresAt = document.RootElement.TryGetProperty("exp", out var exp)
                && exp.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : default;
            return !string.IsNullOrWhiteSpace(subject)
                && expiresAt != default;
        }
        catch (Exception ex) when (
            ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static void AddSupabaseHeaders(
        HttpRequestMessage message,
        string publishableKey,
        string bearerToken)
    {
        message.Headers.TryAddWithoutValidation("apikey", publishableKey);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    private static HttpRequestMessage CreatePasswordGrantMessage(
        string supabaseUrl,
        string publishableKey,
        string email,
        string password)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{supabaseUrl}/auth/v1/token?grant_type=password")
        {
            Content = JsonContent.Create(new { email, password })
        };
        AddSupabaseHeaders(message, publishableKey, publishableKey);
        return message;
    }

    private HttpRequestMessage CreateProfileRequest(
        string supabaseUrl,
        string publishableKey,
        string accessToken,
        string encodedProviderUserId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"{supabaseUrl}/rest/v1/profiles?select=id,organization_id,username,display_name,student_code,date_of_birth,must_change_password,role,is_active&id=eq.{encodedProviderUserId}&limit=2");
        AddSupabaseHeaders(message, publishableKey, accessToken);
        if (!string.IsNullOrWhiteSpace(cloud.Schema))
            message.Headers.TryAddWithoutValidation("Accept-Profile", cloud.Schema);
        return message;
    }

    private async Task<ExternalApplicationProfile> GetProfileAsync(
        string supabaseUrl,
        string publishableKey,
        string accessToken,
        string providerUserId,
        CancellationToken cancellationToken)
    {
        var encodedId = Uri.EscapeDataString(providerUserId);
        using var response = await SendAuthenticationRequestWithRetryAsync(
            () => CreateProfileRequest(
                supabaseUrl,
                publishableKey,
                accessToken,
                encodedId),
            ErrorCodes.ProfileLookupFailed,
            "Không thể tải hồ sơ ứng dụng từ Supabase.",
            "LocalProfileValidation",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ApiException(
                ErrorCodes.ProfileAccessUnauthorized,
                "Supabase không chấp nhận phiên đăng nhập khi đọc hồ sơ ứng dụng.",
                401);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ApiException(
                ErrorCodes.ProfileAccessForbidden,
                "Tài khoản không có quyền đọc hồ sơ ứng dụng.",
                403);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                ErrorCodes.ProfileLookupFailed,
                "Không thể tải hồ sơ ứng dụng từ Supabase.",
                503);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var rows = document.RootElement;
            if (rows.ValueKind != JsonValueKind.Array)
                throw InvalidProfileResponse();
            if (rows.GetArrayLength() == 0)
            {
                throw new ApiException(
                    ErrorCodes.ProfileNotFound,
                    "Tài khoản chưa được cấp hồ sơ và quyền sử dụng ExamTransfer.",
                    403);
            }
            if (rows.GetArrayLength() != 1)
                throw InvalidProfileResponse();

            var row = rows[0];
            var id = GetString(row, "id");
            if (string.IsNullOrWhiteSpace(id)
                || !row.TryGetProperty("is_active", out var activeElement)
                || activeElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !row.TryGetProperty("must_change_password", out var mustChangePasswordElement)
                || mustChangePasswordElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw InvalidProfileResponse();
            }

            return new ExternalApplicationProfile(
                id,
                GetString(row, "organization_id"),
                GetString(row, "username"),
                GetString(row, "display_name"),
                GetString(row, "student_code"),
                GetString(row, "role"),
                activeElement.GetBoolean(),
                GetDateOnly(row, "date_of_birth"),
                mustChangePasswordElement.GetBoolean());
        }
        catch (ApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidProfileResponse();
        }
        catch (InvalidOperationException)
        {
            throw InvalidProfileResponse();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage message,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new ApiException(errorCode, errorMessage, 503);
        }
    }

    private async Task<HttpResponseMessage> SendAuthenticationRequestWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string errorCode,
        string errorMessage,
        string step,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var message = requestFactory();
            try
            {
                var response = await http.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!IsTransientStatus(response.StatusCode))
                    return response;

                if (attempt == maxAttempts)
                {
                    response.Dispose();
                    throw new ApiException(errorCode, errorMessage, 503);
                }

                var retryDelay = GetRetryDelay(response, attempt);
                LogRetry(step, attempt, response.StatusCode);
                response.Dispose();
                await delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException) when (attempt < maxAttempts)
            {
                LogRetry(step, attempt, null);
                await delay(GetRetryDelay(null, attempt), cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                LogRetry(step, attempt, null);
                await delay(GetRetryDelay(null, attempt), cancellationToken);
            }
            catch (TaskCanceledException)
            {
                throw new ApiException(errorCode, errorMessage, 503);
            }
            catch (HttpRequestException)
            {
                throw new ApiException(errorCode, errorMessage, 503);
            }
        }

        throw new ApiException(errorCode, errorMessage, 503);
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage? response, int failedAttempt)
    {
        var retryAfter = response?.Headers.RetryAfter;
        TimeSpan value;
        if (retryAfter?.Delta is { } delta)
        {
            value = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            value = date - DateTimeOffset.UtcNow;
        }
        else
        {
            var baseMilliseconds = failedAttempt == 1 ? 250 : 750;
            value = TimeSpan.FromMilliseconds(
                baseMilliseconds + Math.Clamp(jitterMilliseconds(), 0, 100));
        }

        if (value < TimeSpan.Zero)
            return TimeSpan.Zero;
        return value > TimeSpan.FromSeconds(5)
            ? TimeSpan.FromSeconds(5)
            : value;
    }

    private void LogRetry(
        string step,
        int attempt,
        HttpStatusCode? statusCode)
    {
        var host = Uri.TryCreate(cloud.SupabaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : "<missing>";
        logger.LogWarning(
            "Supabase auth retry Step={Step} Host={Host} OrganizationId={OrganizationId} HttpStatus={HttpStatus} Attempt={Attempt}.",
            step,
            host,
            cloud.OrganizationId,
            statusCode.HasValue ? (int)statusCode.Value : null,
            attempt);
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private string ResolveSignInEmail(string account)
    {
        var value = account.Trim();
        if (value.Contains('@'))
            return value;

        if (value.Length is < 1 or > 32 || value.Any(ch => ch is < '0' or > '9'))
        {
            throw new ApiException(
                ErrorCodes.InvalidCredentials,
                "Tài khoản hoặc mật khẩu không đúng.",
                401);
        }

        var domain = auth.StudentEmailDomain?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain) || domain.Any(char.IsWhiteSpace))
        {
            throw new ApiException(
                ErrorCodes.SupabaseNotConfigured,
                "Tên miền email kỹ thuật của sinh viên chưa được cấu hình hợp lệ.",
                503);
        }

        return $"{value}@{domain}".ToLowerInvariant();
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateOnly? GetDateOnly(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(
                property.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var value))
        {
            throw InvalidProfileResponse();
        }

        return value;
    }

    private static ApiException InvalidAuthResponse() =>
        new(ErrorCodes.AuthResponseInvalid, "Supabase Auth trả về dữ liệu không hợp lệ.", 503);

    private static ApiException InvalidProfileResponse() =>
        new(ErrorCodes.ProfileResponseInvalid, "Hồ sơ ứng dụng trên Supabase không hợp lệ.", 503);
    private sealed record PasswordSession(string ProviderUserId, string AccessToken);
}
