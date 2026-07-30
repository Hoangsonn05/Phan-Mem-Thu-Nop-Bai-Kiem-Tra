using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Execution.OnlyLan;

public sealed class LanSessionParticipantMutationHandler(
    AppDbContext db,
    ISessionTokenService tokens,
    IAuditService audit,
    IOutboxService outbox,
    IRealtimePublisher realtime,
    IOptions<ExamTransferOptions> options) : ISessionParticipantMutationHandler
{
    private readonly ExamTransferOptions _options = options.Value;

    public SessionAccessMode AccessMode => SessionAccessMode.LanOnly;

    public async Task<ParticipantDto> ApproveAsync(
        SessionParticipant participant,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        if (participant.Session.Status != SessionStatus.Waiting)
        {
            throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Chỉ duyệt trong phòng chờ.",
                409);
        }

        participant.Status = ParticipantStatus.Approved;
        participant.ApprovedAtUtc = DateTimeOffset.UtcNow;
        participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);

        var issued = tokens.IssueParticipantToken(
            participant.SessionId,
            participant.Id,
            participant.UserId ?? Guid.Empty,
            participant.DeviceId,
            participant.Status,
            SessionParticipantMutationRules.ParticipantTokenLifetime(
                _options,
                participant.Session));

        await audit.WriteAsync(
            "ParticipantApproved",
            nameof(SessionParticipant),
            participant.Id.ToString(),
            participant.SessionId,
            null,
            SessionParticipantMutationRules.ToCloud(participant),
            cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            SessionParticipantMutationRules.ToCloud(participant),
            cancellationToken: cancellationToken);
        await realtime.PublishParticipantAsync(
            participant.SessionId,
            participant.Id,
            RealtimeEvents.ParticipantApproved,
            participant.Session.Sequence,
            new ParticipantApprovedEvent(participant.Id, issued.ExpiresAtUtc),
            cancellationToken);

        return participant.ToDto(
            DateTimeOffset.UtcNow,
            _options.Session.DisconnectAfterSeconds,
            SessionParticipantMutationRules.ParticipantDeadline(participant));
    }
}
