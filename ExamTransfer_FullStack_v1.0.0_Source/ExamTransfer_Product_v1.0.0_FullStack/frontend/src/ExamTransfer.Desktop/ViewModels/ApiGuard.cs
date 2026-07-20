using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

internal static class ApiGuard
{
    public static T Require<T>(ApiResponse<T>? response)
    {
        if (response?.Success == true && response.Data is not null)
        {
            return response.Data;
        }

        var error = response?.Error;
        var transport = error?.Details as BackendTransportDetails;
        throw new BackendApiException(
            error?.Code ?? "INVALID_SERVER_RESPONSE",
            error?.Message ?? "Máy chủ không trả về dữ liệu hợp lệ.",
            transport?.HasBackendTrace == true ? response?.TraceId : null,
            error?.FieldErrors,
            transport?.BackendDetails ?? error?.Details,
            transport?.HttpStatusCode,
            transport?.Endpoint);
    }
}

internal sealed record BackendTransportDetails(
    int HttpStatusCode,
    string? Endpoint,
    object? BackendDetails,
    bool HasBackendTrace);

internal sealed class BackendApiException(
    string apiCode,
    string message,
    string? backendTraceId,
    IReadOnlyDictionary<string, string[]>? fieldErrors,
    object? details,
    int? httpStatusCode,
    string? endpoint) : Exception(message)
{
    public string ApiCode { get; } = apiCode;
    public string? BackendTraceId { get; } = backendTraceId;
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; } = fieldErrors;
    public object? Details { get; } = details;
    public int? HttpStatusCode { get; } = httpStatusCode;
    public string? Endpoint { get; } = endpoint;

    public override string ToString() =>
        $"{base.ToString()}{Environment.NewLine}api_code: {ApiCode}{Environment.NewLine}" +
        $"http_status: {HttpStatusCode?.ToString() ?? "unknown"}{Environment.NewLine}" +
        $"endpoint: {Endpoint ?? "unknown"}{Environment.NewLine}" +
        $"backend_trace_id: {BackendTraceId ?? "unavailable"}";
}
