using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Middleware;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (ApiException ex) { await WriteAsync(context, ex.StatusCode, ex.Code, ex.Message, ex.FieldErrors, ex.Details); }
        catch (DomainRuleException ex) { await WriteAsync(context, 409, ex.Code, ex.Message, null, null); }
        catch (DbUpdateConcurrencyException ex) { logger.LogWarning(ex, "Concurrency conflict {TraceId}", context.TraceIdentifier); await WriteAsync(context, 409, ErrorCodes.ConcurrencyConflict, "Dữ liệu đã thay đổi; tải lại trước khi lưu.", null, null); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error {TraceId}", context.TraceIdentifier);
            await WriteAsync(context, 500, "INTERNAL_ERROR", "Lỗi nội bộ. Dùng traceId để tra log.", null, null);
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string code, string message, IReadOnlyDictionary<string, string[]>? fields, object? details)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status; context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(new ApiError(code, message, fields, details), context.TraceIdentifier));
    }
}
