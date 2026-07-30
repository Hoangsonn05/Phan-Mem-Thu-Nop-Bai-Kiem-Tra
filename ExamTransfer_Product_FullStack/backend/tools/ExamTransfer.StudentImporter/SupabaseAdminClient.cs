using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ExamTransfer.StudentImporter;

internal sealed class SupabaseAdminClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string secretKey;
    private readonly string? publishableKey;

    public SupabaseAdminClient(Uri baseUri, string secretKey, string? publishableKey)
    {
        this.secretKey = secretKey;
        this.publishableKey = publishableKey;
        http = new HttpClient
        {
            BaseAddress = new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(90)
        };
    }

    public async Task<string> EnsureOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        using var request = CreateAdminRequest(
            HttpMethod.Get,
            $"rest/v1/organizations?select=id,name&id=eq.{organizationId:D}&limit=1");
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        EnsureSuccess(response, body, "Không thể kiểm tra organization trên Supabase.");

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() != 1)
        {
            throw new InvalidOperationException(
                $"Không tìm thấy organization_id {organizationId:D} trên Supabase.");
        }

        var row = document.RootElement[0];
        return row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? organizationId.ToString("D")
            : organizationId.ToString("D");
    }

    public async Task<IReadOnlyDictionary<string, AuthUserSnapshot>> GetUsersByEmailAsync(
        CancellationToken cancellationToken)
    {
        const int pageSize = 1000;
        var result = new Dictionary<string, AuthUserSnapshot>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= 100; page++)
        {
            using var request = CreateAdminRequest(
                HttpMethod.Get,
                $"auth/v1/admin/users?page={page}&per_page={pageSize}");
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await ReadBodyAsync(response, cancellationToken);
            EnsureSuccess(response, body, "Không thể đọc danh sách Supabase Auth users.");

            using var document = JsonDocument.Parse(body);
            var users = document.RootElement.TryGetProperty("users", out var usersElement)
                ? usersElement
                : document.RootElement;

            if (users.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Supabase trả về danh sách Auth users không hợp lệ.");

            var count = 0;
            foreach (var user in users.EnumerateArray())
            {
                count++;
                var id = GetString(user, "id");
                var email = GetString(user, "email");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(email))
                    result[email] = new AuthUserSnapshot(id, email);
            }

            if (count < pageSize)
                break;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, ProfileSnapshot>> GetProfilesByStudentCodeAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var query =
            "rest/v1/profiles?" +
            "select=id,student_code,display_name,date_of_birth,must_change_password" +
            $"&organization_id=eq.{organizationId:D}&role=eq.Student&limit=10000";

        using var request = CreateAdminRequest(HttpMethod.Get, query);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        EnsureSuccess(response, body, "Không thể đọc profiles sinh viên trên Supabase.");

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Supabase trả về profiles không hợp lệ.");

        var result = new Dictionary<string, ProfileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var id = GetString(row, "id");
            var code = GetString(row, "student_code");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(code))
                continue;

            var date = ParseDate(row, "date_of_birth");
            var mustChange = row.TryGetProperty("must_change_password", out var mustChangeElement)
                && mustChangeElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && mustChangeElement.GetBoolean();

            result[code] = new ProfileSnapshot(
                id,
                code,
                GetString(row, "display_name"),
                date,
                mustChange);
        }

        return result;
    }

    public async Task<AuthUserSnapshot> CreateUserAsync(
        StudentImportRow student,
        Guid organizationId,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = CreateAdminRequest(HttpMethod.Post, "auth/v1/admin/users");
        request.Content = JsonContent.Create(new
        {
            email = student.TechnicalEmail,
            password,
            email_confirm = true,
            user_metadata = new
            {
                display_name = student.DisplayName,
                student_code = student.StudentCode,
                source = "ExamTransfer.StudentImporter"
            },
            app_metadata = new
            {
                examtransfer_role = "Student",
                organization_id = organizationId.ToString("D")
            }
        });

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        EnsureSuccess(response, body, $"Không thể tạo Auth user {student.TechnicalEmail}.");
        return ParseAuthUser(body, student.TechnicalEmail);
    }

    public async Task UpdateExistingUserAsync(
        string userId,
        StudentImportRow student,
        Guid organizationId,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = CreateAdminRequest(
            HttpMethod.Put,
            $"auth/v1/admin/users/{Uri.EscapeDataString(userId)}");
        request.Content = JsonContent.Create(new
        {
            password,
            email_confirm = true,
            user_metadata = new
            {
                display_name = student.DisplayName,
                student_code = student.StudentCode,
                source = "ExamTransfer.StudentImporter"
            },
            app_metadata = new
            {
                examtransfer_role = "Student",
                organization_id = organizationId.ToString("D")
            }
        });

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        EnsureSuccess(response, body, $"Không thể cập nhật Auth user {student.TechnicalEmail}.");
    }

    public async Task DeleteUserAsync(string userId, CancellationToken cancellationToken)
    {
        using var request = CreateAdminRequest(
            HttpMethod.Delete,
            $"auth/v1/admin/users/{Uri.EscapeDataString(userId)}");
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        EnsureSuccess(response, body, "Không thể hoàn tác Auth user vừa tạo.");
    }

    public async Task UpsertProfileAsync(
        string userId,
        StudentImportRow student,
        Guid organizationId,
        bool mustChangePassword,
        CancellationToken cancellationToken)
    {
        using var request = CreateAdminRequest(
            HttpMethod.Post,
            "rest/v1/profiles?on_conflict=id&select=id");
        request.Headers.TryAddWithoutValidation(
            "Prefer",
            "resolution=merge-duplicates,return=representation");
        request.Content = JsonContent.Create(new
        {
            id = userId,
            organization_id = organizationId.ToString("D"),
            username = student.StudentCode,
            student_code = student.StudentCode,
            display_name = student.DisplayName,
            date_of_birth = student.DateOfBirth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            role = "Student",
            is_active = true,
            must_change_password = mustChangePassword,
            updated_at = DateTimeOffset.UtcNow
        });

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        EnsureSuccess(response, body, $"Không thể upsert profile {student.StudentCode}.");
    }

    public async Task VerifyLoginAsync(
        StudentImportRow student,
        string password,
        string expectedUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publishableKey))
            throw new InvalidOperationException("Thiếu publishable key để chạy --verify-login.");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "auth/v1/token?grant_type=password");
        request.Headers.TryAddWithoutValidation("apikey", publishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", publishableKey);
        request.Content = JsonContent.Create(new
        {
            email = student.TechnicalEmail,
            password
        });

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        EnsureSuccess(response, body, $"Đăng nhập kiểm tra thất bại cho {student.StudentCode}.");

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("user", out var user)
            || !string.Equals(GetString(user, "id"), expectedUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Đăng nhập {student.StudentCode} thành công nhưng user.id không khớp profile.");
        }
    }

    public void Dispose() => http.Dispose();

    private HttpRequestMessage CreateAdminRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("apikey", secretKey);
        // New sb_secret_* keys are API keys rather than JWTs. They must not be
        // forced into a Bearer header. Legacy service_role JWT keys still need
        // Authorization: Bearer <key>.
        if (IsLegacyJwtKey(secretKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static bool IsLegacyJwtKey(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Count(ch => ch == '.') == 2;

    private static AuthUserSnapshot ParseAuthUser(string body, string expectedEmail)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.TryGetProperty("user", out var user)
            ? user
            : document.RootElement;
        var id = GetString(root, "id");
        var email = GetString(root, "email") ?? expectedEmail;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Supabase tạo Auth user nhưng không trả về user.id.");
        return new AuthUserSnapshot(id, email);
    }

    private static DateOnly? ParseDate(JsonElement row, string propertyName)
    {
        if (!row.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(
                value.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            ? parsed
            : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<string> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        await response.Content.ReadAsStringAsync(cancellationToken);

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string body,
        string message)
    {
        if (response.IsSuccessStatusCode)
            return;

        var safeDetail = ExtractSafeError(body);
        throw new SupabaseRequestException(
            (int)response.StatusCode,
            $"{message} HTTP {(int)response.StatusCode}. {safeDetail}".Trim());
    }

    private static string ExtractSafeError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return GetString(root, "msg")
                ?? GetString(root, "message")
                ?? GetString(root, "error_description")
                ?? GetString(root, "error")
                ?? "Supabase không cung cấp chi tiết lỗi an toàn.";
        }
        catch (JsonException)
        {
            return "Supabase trả về phản hồi lỗi không đúng JSON.";
        }
    }
}

internal sealed class SupabaseRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
