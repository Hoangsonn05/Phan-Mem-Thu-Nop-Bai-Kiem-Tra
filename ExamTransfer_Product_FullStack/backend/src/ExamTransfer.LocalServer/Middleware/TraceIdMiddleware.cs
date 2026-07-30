namespace ExamTransfer.LocalServer.Middleware;

public sealed class TraceIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers["X-Trace-Id"].FirstOrDefault();
        context.TraceIdentifier = string.IsNullOrWhiteSpace(incoming) ? Guid.NewGuid().ToString("N") : incoming[..Math.Min(incoming.Length, 128)];
        context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
        await next(context);
    }
}
