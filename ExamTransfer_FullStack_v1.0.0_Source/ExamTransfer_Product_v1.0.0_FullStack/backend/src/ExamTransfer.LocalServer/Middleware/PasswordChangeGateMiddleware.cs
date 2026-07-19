using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.LocalServer.Middleware;

public sealed class PasswordChangeGateMiddleware(RequestDelegate next)
{
    private static readonly PathString[] AllowedPaths =
    [
        new("/health"),
        new("/api/v1/system/status"),
        new("/api/v1/auth/me"),
        new("/api/v1/auth/change-password"),
        new("/api/v1/auth/heartbeat"),
        new("/api/v1/auth/logout")
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var passwordChangeRequired = string.Equals(
            context.User.FindFirst("password_change_required")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (context.User.Identity?.IsAuthenticated == true
            && context.User.IsInRole(UserRole.Student.ToString())
            && passwordChangeRequired
            && !IsAllowed(context.Request.Path))
        {
            throw new ApiException(
                ErrorCodes.PasswordChangeRequired,
                "Bạn phải đổi mật khẩu tạm trước khi sử dụng chức năng học sinh.",
                403);
        }

        await next(context);
    }

    private static bool IsAllowed(PathString path) =>
        AllowedPaths.Any(allowed => path.Equals(allowed));
}
