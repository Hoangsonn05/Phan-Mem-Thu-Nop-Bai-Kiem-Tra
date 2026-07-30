using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Discovery;

public static class OpenSessionDiscoveryBuilder
{
    public static async Task<IReadOnlyList<OpenSessionDiscoveryDto>> BuildAsync(
        AppDbContext db,
        ExamTransferOptions options,
        string advertisedAddress,
        string serverId,
        string? roomCode,
        CancellationToken cancellationToken)
    {
        var normalizedRoomCode = string.IsNullOrWhiteSpace(roomCode)
            ? null
            : RoomCodeRules.Normalize(roomCode);
        var query = db.ExamSessionsSet
            .AsNoTracking()
            .Include(x => x.Exam)
            .Include(x => x.Participants)
            .Where(x => x.AccessMode == SessionAccessMode.LanOnly
                && x.Status == SessionStatus.Waiting
                && x.AcceptingParticipants
                && x.Exam.Status == ExamStatus.Published);
        if (normalizedRoomCode is not null)
            query = query.Where(x => x.RoomCode == normalizedRoomCode);

        var sessions = await query.ToListAsync(cancellationToken);
        sessions = sessions
            .OrderBy(x => x.PlannedStartUtc)
            .ThenBy(x => x.RoomCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var classIds = sessions
            .Where(x => x.ClassId.HasValue)
            .Select(x => x.ClassId!.Value)
            .Distinct()
            .ToList();
        var classes = await db.ClassesSet
            .AsNoTracking()
            .Where(x => classIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var teacherIds = sessions
            .Where(x => x.Exam.CreatedBy.HasValue)
            .Select(x => x.Exam.CreatedBy!.Value)
            .Distinct()
            .ToList();
        var teachers = await db.UsersSet
            .AsNoTracking()
            .Where(x => teacherIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);

        var scheme = options.Server.UseHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var baseAddress = new UriBuilder(scheme, advertisedAddress, options.Server.Port)
            .Uri.GetLeftPart(UriPartial.Authority);
        var now = DateTimeOffset.UtcNow;
        return sessions.Select(session =>
        {
            classes.TryGetValue(session.ClassId ?? Guid.Empty, out var classroom);
            var teacherName = session.Exam.CreatedBy.HasValue
                && teachers.TryGetValue(session.Exam.CreatedBy.Value, out var displayName)
                    ? displayName
                    : Environment.MachineName;
            return new OpenSessionDiscoveryDto(
                session.Id,
                RoomCodeRules.Normalize(session.RoomCode),
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
                DiscoveryProtocol.ProtocolVersion,
                session.Exam.Subject,
                session.Exam.DurationMinutes,
                session.DeliveryTypeSnapshot,
                session.SupervisionModeSnapshot,
                session.AdmissionMode,
                session.ExamId);
        }).ToList();
    }
}
