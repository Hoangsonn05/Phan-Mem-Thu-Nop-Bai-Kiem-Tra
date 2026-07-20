using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class BackendClient : IBackendClient
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private readonly HttpClient http;
    private string? accountToken;
    private string? participantToken;

    public BackendClient(string baseUrl)
    {
        http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public void SetBearerToken(string? token)
    {
        SetAccountToken(token);
    }

    public void SetAccountToken(string? token)
    {
        accountToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public void SetParticipantToken(string? token)
    {
        participantToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(CancellationToken ct = default) => GetAsync<SystemStatusDto>("api/v1/system/status", ct);
    public Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(CancellationToken ct = default) => GetAsync<DashboardSummaryDto>("api/v1/dashboard/summary", ct);
    public Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(CancellationToken ct = default) => GetAsync<PagedResult<ClassSummaryDto>>("api/v1/classes", ct);
    public Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(CancellationToken ct = default) => GetAsync<PagedResult<ExamSummaryDto>>("api/v1/exams", ct);
    public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(CancellationToken ct = default) => GetAsync<PagedResult<SessionSummaryDto>>("api/v1/sessions", ct);
    public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid id, CancellationToken ct = default) => GetAsync<SessionDetailDto>($"api/v1/sessions/{id}", ct);
    public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default) => GetAsync<PagedResult<SubmissionSummaryDto>>($"api/v1/sessions/{sessionId}/submissions", ct);
    public Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default) => GetAsync<CloudSyncStatusDto>("api/v1/cloud/sync/status", ct);
    public Task<ApiResponse<SettingsDto>?> GetSettingsAsync(CancellationToken ct = default) => GetAsync<SettingsDto>("api/v1/settings", ct);

    public async Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<T>(response, ct);
    }

    public async Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        using var message = CreateRequest(HttpMethod.Post, path);
        message.Content = JsonContent.Create(request, options: Json);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<TResponse>(response, ct);
    }

    public async Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        using var message = CreateRequest(HttpMethod.Put, path);
        message.Content = JsonContent.Create(request, options: Json);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<TResponse>(response, ct);
    }

    public async Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Delete, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<TResponse>(response, ct);
    }

    public async Task<ApiResponse<object>?> UploadChunkAsync(string path, Stream content, long contentLength, string? sha256 = null, CancellationToken ct = default)
    {
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentLength = contentLength;
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = CreateRequest(HttpMethod.Put, path);
        request.Content = streamContent;
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            request.Headers.TryAddWithoutValidation("X-Chunk-Sha256", sha256);
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await Read<object>(response, ct);
    }

    public async Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await SaveResponseAsync(response, destinationPath, progress, ct);
    }

    public async Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var message = CreateRequest(HttpMethod.Post, path);
        message.Content = JsonContent.Create(request, options: Json);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await SaveResponseAsync(response, destinationPath, progress, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(accountToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        }

        if (!string.IsNullOrWhiteSpace(participantToken))
        {
            request.Headers.TryAddWithoutValidation("X-Exam-Session-Token", participantToken);
        }

        return request;
    }

    private static async Task SaveResponseAsync(HttpResponseMessage response, string destinationPath, IProgress<double>? progress, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppContext.BaseDirectory);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long readTotal = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total is > 0) progress?.Report(readTotal * 100d / total.Value);
        }
        progress?.Report(100);
    }

    private static async Task<ApiResponse<T>?> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            if (response.IsSuccessStatusCode) return null;
            return CreateProtocolError<T>(
                response,
                "Máy chủ không trả về nội dung phản hồi.");
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ApiResponse<T>>(body, Json);
            if (parsed is not null)
            {
                if (parsed.Success || parsed.Error is null) return parsed;
                var endpoint = response.RequestMessage?.RequestUri?.AbsolutePath;
                var transport = new BackendTransportDetails(
                    (int)response.StatusCode,
                    endpoint,
                    parsed.Error.Details,
                    !string.IsNullOrWhiteSpace(parsed.TraceId));
                return parsed with { Error = parsed.Error with { Details = transport } };
            }
        }
        catch (JsonException ex)
        {
            // Do not include the raw response body in logs or UI. Login responses
            // can contain access tokens and must never be exposed to the screen.
            FrontendLogger.Log(ex, $"BackendClient.Read<{typeof(T).Name}>");
            return CreateProtocolError<T>(
                response,
                "Máy chủ trả về dữ liệu không đúng định dạng mà ứng dụng hỗ trợ.");
        }

        return CreateProtocolError<T>(
            response,
            "Máy chủ trả về dữ liệu rỗng hoặc không hợp lệ.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        // ASP.NET Core serializes contract enums as strings (for example
        // role: "Admin"). The desktop client must use the same convention.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static ApiResponse<T> CreateProtocolError<T>(HttpResponseMessage response, string message)
    {
        var statusCode = (int)response.StatusCode;
        var path = response.RequestMessage?.RequestUri?.AbsolutePath;
        var endpoint = string.IsNullOrWhiteSpace(path) ? string.Empty : $" tại {path}";
        var error = new ApiError(
            "INVALID_SERVER_RESPONSE",
            $"{message} (HTTP {statusCode}{endpoint}).",
            Details: new BackendTransportDetails(statusCode, path, null, false));

        return new ApiResponse<T>(
            false,
            default,
            error,
            Guid.NewGuid().ToString("N"),
            ContractInfo.SchemaVersion);
    }
}
