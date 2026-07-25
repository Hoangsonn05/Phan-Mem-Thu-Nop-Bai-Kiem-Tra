using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Middleware;

public sealed class LanAccessMiddleware(RequestDelegate next, ILogger<LanAccessMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AppDbContext db, ILanAccessPolicy policy)
    {
        var sessionClaim = context.User.FindFirst("session_id")?.Value;
        if (Guid.TryParse(sessionClaim, out var sessionId))
        {
            var accessMode = await db.ExamSessionsSet.AsNoTracking()
                .Where(x => x.Id == sessionId)
                .Select(x => (SessionAccessMode?)x.AccessMode)
                .FirstOrDefaultAsync(context.RequestAborted);
            if (accessMode == SessionAccessMode.LanOnly
                && !policy.IsAllowed(context.Connection.RemoteIpAddress?.ToString()))
            {
                logger.LogWarning(
                    "LAN request denied. RemoteIp={RemoteIp}; Method={Method}; Path={Path}; TraceId={TraceId}",
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.TraceIdentifier);
                throw new ApiException(ErrorCodes.LanAccessDenied, "Thiết bị không nằm trong mạng nội bộ được phép của phòng thi.", 403);
            }
        }

        await next(context);
    }
}
