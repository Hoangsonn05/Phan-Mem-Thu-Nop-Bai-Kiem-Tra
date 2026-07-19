using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1")]
public sealed class SystemController(ISystemService system, ICloudAdapter cloud) : ApiControllerBase
{
    [HttpGet("system/status")][AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SystemStatusDto>>> Status(CancellationToken ct) => Data(await system.GetStatusAsync(ct));

    [HttpPost("system/preflight")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SystemStatusDto>>> Preflight(CancellationToken ct) => Data(await system.PreflightAsync(ct));

    [HttpGet("system/diagnostics")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Diagnostics(CancellationToken ct) => Data(await system.GetDiagnosticsAsync(ct));

    [HttpGet("dashboard/summary")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<DashboardSummaryDto>>> Dashboard(CancellationToken ct) => Data(await system.GetDashboardAsync(ct));

    [HttpGet("settings")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SettingsDto>>> Settings(CancellationToken ct) => Data(await system.GetSettingsAsync(ct));

    [HttpPut("settings")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<SettingsDto>>> UpdateSettings(UpdateSettingsRequest request, CancellationToken ct) => Data(await system.UpdateSettingsAsync(request, ct));

    [HttpGet("cloud/preflight")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<CloudPreflightDto>>> CloudPreflight(CancellationToken ct)
    {
        var result = await cloud.PreflightAsync(ct);
        return Data(new CloudPreflightDto(
            result.Enabled,
            result.Configured,
            result.Reachable,
            result.SecretConfigured,
            result.KeyMode,
            result.OrganizationId,
            result.UploadStrategy,
            result.Errors,
            result.Warnings,
            result.AccessMode,
            result.Authenticated,
            result.AuthenticatedEmail,
            result.CanSynchronize));
    }

    [HttpGet("cloud/sync/status")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<CloudSyncStatusDto>>> CloudStatus(CancellationToken ct) => Data(await system.GetCloudStatusAsync(ct));

    [HttpPost("cloud/sync")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> CloudSync(CancellationToken ct) { await system.TriggerCloudSyncAsync(ct); return EmptyData(); }

    [HttpGet("cloud/auth/session")][Authorize(Policy = "TeacherOrAdmin")]
    public ActionResult<ApiResponse<CloudSessionDto>> CloudSession()
    {
        var current = cloud.CurrentSession;
        return Data(new CloudSessionDto(
            current is not null,
            current?.UserId,
            current?.Email,
            current?.ExpiresAtUtc,
            current?.OrganizationId,
            current?.Role));
    }

    [HttpPost("cloud/auth/login")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<CloudSessionDto>>> Login(
        LoginRequest request,
        CancellationToken ct)
    {
        var result = await cloud.LoginAsync(
            request.Email,
            request.Password,
            ct);
        return Data(new CloudSessionDto(
            true,
            result.UserId,
            result.Email,
            result.ExpiresAtUtc,
            result.OrganizationId,
            result.Role));
    }

    [HttpPost("cloud/auth/refresh")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<CloudSessionDto>>> RefreshCloudSession(
        CancellationToken ct)
    {
        var result = await cloud.RefreshSessionAsync(ct);
        return Data(new CloudSessionDto(
            result is not null,
            result?.UserId,
            result?.Email,
            result?.ExpiresAtUtc,
            result?.OrganizationId,
            result?.Role));
    }

    [HttpPost("cloud/auth/logout")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Logout(CancellationToken ct) { await cloud.LogoutAsync(ct); return EmptyData(); }

    [HttpGet("preferences/mode")][AllowAnonymous]
    public ActionResult<ApiResponse<object>> GetMode() => Data<object>(new { mode = "Teacher", remember = false });

    [HttpPut("preferences/mode")][AllowAnonymous]
    public ActionResult<ApiResponse<object>> SetMode(SetModeRequest request) => Data<object>(request);
}
