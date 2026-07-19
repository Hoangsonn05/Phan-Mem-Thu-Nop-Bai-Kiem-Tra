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
    ILogger<SupabaseIdentityClient> logger) : IExternalIdentityProvider
{
    private readonly CloudOptions cloud = options.Value.Cloud;

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

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{supabaseUrl}/auth/v1/token?grant_type=password")
        {
            Content = JsonContent.Create(new
            {
                email = request.Account.Trim(),
                password = request.Password
            })
        };
        message.Headers.TryAddWithoutValidation("apikey", publishableKey);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", publishableKey);

        using var response = await SendAsync(
            message,
            ErrorCodes.AuthProviderUnavailable,
            "Không thể kết nối Supabase Auth.",
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
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            profile);
    }

    private async Task<ExternalApplicationProfile> GetProfileAsync(
        string supabaseUrl,
        string publishableKey,
        string accessToken,
        string providerUserId,
        CancellationToken cancellationToken)
    {
        var encodedId = Uri.EscapeDataString(providerUserId);
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"{supabaseUrl}/rest/v1/profiles?select=id,organization_id,username,display_name,student_code,role,is_active&id=eq.{encodedId}&limit=2");
        message.Headers.TryAddWithoutValidation("apikey", publishableKey);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(cloud.Schema))
            message.Headers.TryAddWithoutValidation("Accept-Profile", cloud.Schema);

        using var response = await SendAsync(
            message,
            ErrorCodes.ProfileLookupFailed,
            "Không thể tải hồ sơ ứng dụng từ Supabase.",
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
                || activeElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
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
                activeElement.GetBoolean());
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

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static ApiException InvalidAuthResponse() =>
        new(ErrorCodes.AuthResponseInvalid, "Supabase Auth trả về dữ liệu không hợp lệ.", 503);

    private static ApiException InvalidProfileResponse() =>
        new(ErrorCodes.ProfileResponseInvalid, "Hồ sơ ứng dụng trên Supabase không hợp lệ.", 503);
}
