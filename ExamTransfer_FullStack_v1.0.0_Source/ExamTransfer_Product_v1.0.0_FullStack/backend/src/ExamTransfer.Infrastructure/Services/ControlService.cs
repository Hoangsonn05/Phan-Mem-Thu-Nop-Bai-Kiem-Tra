using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class ControlService(
    AppDbContext db,
    IAuditService audit,
    IRealtimePublisher realtime,
    IOutboxService outbox) : IControlService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ControlPolicyDto?> GetPolicyAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var entity = await db.ControlPoliciesSet.AsNoTracking().Where(x => x.SessionId == sessionId).OrderByDescending(x => x.Version).FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<ControlPolicyDto> SavePolicyAsync(Guid sessionId, SaveControlPolicyRequest request, CancellationToken cancellationToken)
    {
        _ = await db.ExamSessionsSet.FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (request.TtlMinutes is < 1 or > 1440) throw new ApiException(ErrorCodes.ValidationFailed, "TTL policy phải từ 1 đến 1440 phút.");
        var latest = await db.ControlPoliciesSet.Where(x => x.SessionId == sessionId).OrderByDescending(x => x.Version).FirstOrDefaultAsync(cancellationToken);
        if (latest is not null && request.RowVersion is not null && latest.RowVersion != request.RowVersion) throw new ApiException(ErrorCodes.ConcurrencyConflict, "Policy đã thay đổi.", 409, details: new { current = ToDto(latest) });
        var version = (latest?.Version ?? 0) + 1;
        var policy = new ControlPolicy { SessionId = sessionId, Version = version, Status = PolicyApplyStatus.NotRequested, PolicyJson = JsonSerializer.Serialize(request, JsonOptions) };
        db.ControlPoliciesSet.Add(policy); await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("ControlPolicySaved", nameof(ControlPolicy), policy.Id.ToString(), sessionId, latest is null ? null : ToDto(latest), ToDto(policy), cancellationToken);
        await outbox.EnqueueAsync(
            "control_policies",
            policy.Id.ToString(),
            "upsert",
            ToCloud(policy),
            cancellationToken: cancellationToken);
        return ToDto(policy);
    }

    public async Task ApplyPolicyAsync(Guid sessionId, ApplyControlPolicyRequest request, CancellationToken cancellationToken)
    {
        var policy = await db.ControlPoliciesSet.Where(x => x.SessionId == sessionId).OrderByDescending(x => x.Version).FirstOrDefaultAsync(cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Chưa có policy.", 404);
        var participants = await db.SessionParticipantsSet.Where(x => x.SessionId == sessionId && x.Status == ParticipantStatus.Approved && (request.ParticipantIds == null || request.ParticipantIds.Contains(x.Id))).ToListAsync(cancellationToken);
        foreach (var p in participants)
        {
            var status = await db.DevicePolicyStatusesSet.FirstOrDefaultAsync(x => x.ParticipantId == p.Id && x.PolicyVersion == policy.Version, cancellationToken);
            if (status is null) db.DevicePolicyStatusesSet.Add(new DevicePolicyStatus { SessionId = sessionId, ParticipantId = p.Id, PolicyVersion = policy.Version, CapabilityJson = p.CapabilityJson ?? "{}", Status = PolicyApplyStatus.Applying });
            else status.Status = PolicyApplyStatus.Applying;
        }
        policy.Status = PolicyApplyStatus.Applying; var session = await db.ExamSessionsSet.FirstAsync(x => x.Id == sessionId, cancellationToken); session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var p in participants) await realtime.PublishParticipantAsync(sessionId, p.Id, RealtimeEvents.ControlPolicyChanged, session.Sequence, new ControlPolicyChangedEvent(policy.Version, PolicyApplyStatus.Applying), cancellationToken);
        await audit.WriteAsync("ControlPolicyApplyRequested", nameof(ControlPolicy), policy.Id.ToString(), sessionId, null, new { policy.Version, participants = participants.Select(x => x.Id) }, cancellationToken);
        await outbox.EnqueueAsync(
            "control_policies",
            policy.Id.ToString(),
            "upsert",
            ToCloud(policy),
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceControlStatusDto>> GetDeviceStatusAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var rows = await db.DevicePolicyStatusesSet.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.ParticipantId).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<PagedResult<ViolationDto>> GetViolationsAsync(Guid sessionId, ViolationSeverity? severity, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.ViolationsSet.AsNoTracking().Where(x => x.SessionId == sessionId);
        if (severity.HasValue) query = query.Where(x => x.Severity == severity.Value);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.OccurredAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(x => x.ToDto()).ToList(), page, pageSize, total);
    }

    public async Task<ViolationDto> ReportViolationAsync(Guid sessionId, Guid participantId, ViolationReportRequest request, CancellationToken cancellationToken)
    {
        var participant = await db.SessionParticipantsSet.Include(x => x.Session).FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        var dedupeFrom = request.OccurredAtUtc.AddSeconds(-5);
        var duplicate = await db.ViolationsSet.AnyAsync(x => x.SessionId == sessionId && x.ParticipantId == participantId && x.Type == request.Type && x.OccurredAtUtc >= dedupeFrom, cancellationToken);
        if (duplicate) throw new ApiException(ErrorCodes.Conflict, "Vi phạm trùng trong cửa sổ chống spam.", 409);
        var entity = new Violation { SessionId = sessionId, ParticipantId = participantId, Type = request.Type, Severity = request.Severity, OccurredAtUtc = request.OccurredAtUtc, PayloadJson = request.PayloadJson };
        db.ViolationsSet.Add(entity); participant.Session.Sequence++; await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishSessionAsync(sessionId, RealtimeEvents.ViolationDetected, participant.Session.Sequence, entity.ToDto(), cancellationToken);
        await outbox.EnqueueAsync(
            "violations",
            entity.Id.ToString(),
            "upsert",
            ToCloud(entity),
            cancellationToken: cancellationToken);
        return entity.ToDto();
    }

    public async Task AcknowledgeAsync(Guid violationId, Guid? actorId, CancellationToken cancellationToken)
    {
        var entity = await db.ViolationsSet.FirstOrDefaultAsync(x => x.Id == violationId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy vi phạm.", 404);
        entity.HandledAtUtc = DateTimeOffset.UtcNow; entity.HandledBy = actorId; await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("ViolationAcknowledged", nameof(Violation), entity.Id.ToString(), entity.SessionId, null, new { entity.HandledAtUtc, actorId }, cancellationToken);
        await outbox.EnqueueAsync(
            "violations",
            entity.Id.ToString(),
            "upsert",
            ToCloud(entity),
            cancellationToken: cancellationToken);
    }

    public async Task PolicyAckAsync(Guid sessionId, Guid participantId, PolicyApplyAckRequest request, CancellationToken cancellationToken)
    {
        var participant = await db.SessionParticipantsSet.FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        participant.CapabilityJson = JsonSerializer.Serialize(request.Capabilities, JsonOptions);
        var status = await db.DevicePolicyStatusesSet.FirstOrDefaultAsync(x => x.ParticipantId == participantId && x.PolicyVersion == request.PolicyVersion, cancellationToken);
        if (status is null)
        {
            status = new DevicePolicyStatus { SessionId = sessionId, ParticipantId = participantId, PolicyVersion = request.PolicyVersion };
            db.DevicePolicyStatusesSet.Add(status);
        }
        status.CapabilityJson = JsonSerializer.Serialize(request.Capabilities, JsonOptions); status.Status = request.Status; status.Error = request.Error ?? (request.UnsupportedRules.Count > 0 ? string.Join(',', request.UnsupportedRules) : null);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ControlActionAsync(Guid participantId, ControlActionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiException(ErrorCodes.ValidationFailed, "Thao tác kiểm soát phải có lý do.");
        var participant = await db.SessionParticipantsSet.Include(x => x.Session).FirstOrDefaultAsync(x => x.Id == participantId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        participant.Session.Sequence++; await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishParticipantAsync(participant.SessionId, participant.Id, "ControlActionRequested", participant.Session.Sequence, request, cancellationToken);
        await audit.WriteAsync("ControlActionRequested", nameof(SessionParticipant), participant.Id.ToString(), participant.SessionId, null, request, cancellationToken);
    }

    private static ControlPolicyDto ToDto(ControlPolicy x)
    {
        var r = JsonSerializer.Deserialize<SaveControlPolicyRequest>(x.PolicyJson, JsonOptions) ?? new(false, "None", "None", [], [], "None", true, 60, null);
        return new(x.SessionId, x.Version, r.Fullscreen, r.FocusRule, r.ClipboardRule, r.AllowedProcesses, r.BlockedProcesses, r.NetworkRule, r.EmergencyExit, r.TtlMinutes, x.RowVersion);
    }
    private static DeviceControlStatusDto ToDto(DevicePolicyStatus x)
    {
        var c = JsonSerializer.Deserialize<ControlCapabilitiesDto>(x.CapabilityJson, JsonOptions) ?? new(false, false, false, false, false);
        return new(x.ParticipantId, x.PolicyVersion, c, x.Status, x.Error, x.UpdatedAtUtc);
    }

    private static object ToCloud(ControlPolicy x) => new
    {
        id = x.Id,
        session_id = x.SessionId,
        version = x.Version,
        status = x.Status.ToString(),
        policy_json = x.PolicyJson,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static object ToCloud(Violation x) => new
    {
        id = x.Id,
        session_id = x.SessionId,
        participant_id = x.ParticipantId,
        type = x.Type,
        severity = x.Severity.ToString(),
        occurred_at = x.OccurredAtUtc,
        payload_json = x.PayloadJson,
        handled_at = x.HandledAtUtc,
        handled_by = x.HandledBy,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

}
