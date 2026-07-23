using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ExamTransfer.Application;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
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

        var sessions = await db.ExamSessionsSet
            .AsNoTracking()
            .Include(x => x.Exam)
            .Include(x => x.Participants)
            .Where(x => x.AccessMode == SessionAccessMode.LanOnly
                && x.Status == SessionStatus.Waiting
                && x.AcceptingParticipants)
            .ToListAsync(ct);
        sessions = sessions
            .OrderBy(x => x.PlannedStartUtc)
            .ThenBy(x => x.RoomCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var classIds = sessions.Where(x => x.ClassId.HasValue).Select(x => x.ClassId!.Value).Distinct().ToList();
        var classes = await db.ClassesSet.AsNoTracking()
            .Where(x => classIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var teacherIds = sessions.Where(x => x.Exam.CreatedBy.HasValue).Select(x => x.Exam.CreatedBy!.Value).Distinct().ToList();
        var teachers = await db.UsersSet.AsNoTracking()
            .Where(x => teacherIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);

        var address = options.Value.Server.PreferredIp ?? GetLanIp();
        var baseAddress = $"{(options.Value.Server.UseHttps ? "https" : "http")}://{address}:{options.Value.Server.Port}";
        var serverId = MachineId();
        var now = DateTimeOffset.UtcNow;
        var result = sessions.Select(session =>
        {
            classes.TryGetValue(session.ClassId ?? Guid.Empty, out var classroom);
            var teacherName = session.Exam.CreatedBy.HasValue
                && teachers.TryGetValue(session.Exam.CreatedBy.Value, out var displayName)
                    ? displayName
                    : Environment.MachineName;
            return new OpenSessionDiscoveryDto(
                session.Id,
                session.RoomCode,
                classroom?.Name ?? session.Exam.Title,
                session.ClassId,
                classroom?.Code,
                classroom?.Name,
                session.Exam.Title,
                teacherName,
                session.Status,
                !session.AutoApprove,
                session.Capacity,
                session.Participants.Count(x => x.Status != ParticipantStatus.Rejected),
                session.StartedAtUtc,
                session.PlannedStartUtc,
                session.AccessMode,
                serverId,
                Environment.MachineName,
                baseAddress,
                now,
                DiscoveryProtocol.ProtocolVersion);
        }).ToList();

        return Data<IReadOnlyList<OpenSessionDiscoveryDto>>(result);
    }

    private static string GetLanIp()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var address = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address));
            if (address is not null) return address.Address.ToString();
        }
        return IPAddress.Loopback.ToString();
    }

    private static string MachineId() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|ExamTransfer|discovery")))[..16].ToLowerInvariant();
}
