using System.Security.Cryptography;
using System.Text;
using ExamTransfer.Application;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.LocalServer.Discovery;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/discovery")]
[AllowAnonymous]
public sealed class DiscoveryController(
    AppDbContext db,
    ILanAccessPolicy lanAccessPolicy,
    IOptions<ExamTransferOptions> options) : ApiControllerBase
{
    [HttpGet("open-sessions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<OpenSessionDiscoveryDto>>>> OpenSessions(CancellationToken ct)
    {
        var remoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!lanAccessPolicy.IsAllowed(remoteAddress))
            throw new ApiException(ErrorCodes.LanAccessDenied, "Thiết bị không nằm trong mạng nội bộ được phép.", 403);

        var endpoint = LanNetworkConfiguration.ResolveAdvertisedEndpoint(
            options.Value,
            HttpContext.Connection.RemoteIpAddress);
        if (!endpoint.Ready)
            throw new ApiException(
                endpoint.Code,
                "Cấu hình địa chỉ LAN của máy giáo viên chưa hoàn chỉnh; máy chủ không quảng bá endpoint không an toàn.",
                503);
        var serverId = MachineId();
        var result = await OpenSessionDiscoveryBuilder.BuildAsync(
            db,
            options.Value,
            endpoint.Address!,
            serverId,
            null,
            ct);

        return Data<IReadOnlyList<OpenSessionDiscoveryDto>>(result);
    }

    [HttpGet("identity")]
    public ActionResult<ApiResponse<LocalServerIdentityDto>> Identity()
    {
        var remoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!lanAccessPolicy.IsAllowed(remoteAddress))
            throw new ApiException(ErrorCodes.LanAccessDenied, "Thiết bị không nằm trong mạng nội bộ được phép.", 403);

        var endpoint = LanNetworkConfiguration.ResolveAdvertisedEndpoint(
            options.Value,
            HttpContext.Connection.RemoteIpAddress);
        if (!endpoint.Ready)
            throw new ApiException(
                endpoint.Code,
                "Cấu hình địa chỉ LAN của máy giáo viên chưa hoàn chỉnh.",
                503);

        return Data(new LocalServerIdentityDto(
            "ExamTransfer.LocalServer",
            MachineId(),
            DiscoveryProtocol.ProtocolVersion,
            options.Value.Discovery.Port,
            ReleaseIdentity.BuildId,
            ReleaseIdentity.SemanticVersion,
            endpoint.Address!,
            options.Value.Server.Port));
    }

    internal static string MachineId() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|ExamTransfer|discovery")))[..16].ToLowerInvariant();
}
