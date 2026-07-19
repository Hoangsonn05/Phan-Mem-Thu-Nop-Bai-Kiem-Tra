using ExamTransfer.Application;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/auth")]
public sealed class AuthController(IAccountAuthenticationService auth) : ApiControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AccountLoginResultDto>>> Login(AccountLoginRequest request, CancellationToken ct) =>
        Data(await auth.LoginAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct));

    [HttpPost("student/confirm")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AccountLoginResultDto>>> ConfirmStudent(StudentIdentityConfirmRequest request, CancellationToken ct) =>
        Data(await auth.ConfirmStudentAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct));

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = ExamTransferAuthSchemes.Account)]
    public async Task<ActionResult<ApiResponse<CurrentAccountDto>>> Me(CancellationToken ct) =>
        Data(await auth.GetCurrentAsync(CurrentAccountPrincipal(), ct));

    [HttpPost("change-password")]
    [Authorize(AuthenticationSchemes = ExamTransferAuthSchemes.Account)]
    public async Task<ActionResult<ApiResponse<PasswordChangeResultDto>>> ChangePassword(
        ChangePasswordRequest request,
        CancellationToken ct) =>
        Data(await auth.ChangePasswordAsync(CurrentAccountPrincipal(), request, ct));

    [HttpPost("heartbeat")]
    [Authorize(AuthenticationSchemes = ExamTransferAuthSchemes.Account)]
    public async Task<ActionResult<ApiResponse<AccountHeartbeatResponse>>> Heartbeat(AccountHeartbeatRequest request, CancellationToken ct) =>
        Data(await auth.HeartbeatAsync(CurrentAccountPrincipal(), request, ct));

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = ExamTransferAuthSchemes.Account)]
    public async Task<ActionResult<ApiResponse<object>>> Logout(LogoutRequest request, CancellationToken ct)
    {
        await auth.LogoutAsync(CurrentAccountPrincipal(), request, ct);
        return EmptyData();
    }

    private AccountTokenPrincipal CurrentAccountPrincipal()
    {
        var userId = RequiredGuidClaim("sub");
        var loginSessionId = RequiredGuidClaim("login_session_id");
        var deviceId = User.FindFirst("device_id")?.Value
            ?? throw new ApiException(ErrorCodes.DeviceMismatch, "Token thiếu device_id.", 401);
        var roleValue = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        if (!Enum.TryParse<UserRole>(roleValue, out var role))
            throw new ApiException(ErrorCodes.Unauthorized, "Token thiếu role hợp lệ.", 401);
        var expiresAt = User.FindFirst("expires_at")?.Value is { } raw
            && long.TryParse(raw, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.UtcNow;

        return new AccountTokenPrincipal(
            userId,
            loginSessionId,
            role,
            User.FindFirst("organization_id")?.Value,
            deviceId,
            expiresAt);
    }
}
