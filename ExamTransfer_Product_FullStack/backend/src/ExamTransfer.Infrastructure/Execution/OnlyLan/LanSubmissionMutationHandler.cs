using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Execution.OnlyLan;

public sealed class LanSubmissionMutationHandler(
    AppDbContext db,
    IAuditService audit,
    IOutboxService outbox,
    IRealtimePublisher realtime) : ISubmissionMutationHandler
{
    public SessionAccessMode AccessMode => SessionAccessMode.LanOnly;

    public async Task RejectAsync(
        Submission submission,
        RejectSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (submission.Status is not (
            SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted))
        {
            throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Chỉ từ chối bài đã nộp.",
                409);
        }

        submission.Status = SubmissionStatus.Rejected;
        submission.TeacherRejectReason = request.Reason;
        submission.Participant.SubmissionStatus = SubmissionStatus.Rejected;
        submission.Participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "SubmissionRejected",
            nameof(Submission),
            submission.Id.ToString(),
            submission.SessionId,
            null,
            request,
            cancellationToken);
        await outbox.EnqueueAsync(
            "submissions",
            submission.Id.ToString(),
            "upsert",
            SubmissionMutationPayloads.ToCloud(submission),
            cancellationToken: cancellationToken);
        await realtime.PublishParticipantAsync(
            submission.SessionId,
            submission.ParticipantId,
            RealtimeEvents.SubmissionRejected,
            submission.Participant.Session.Sequence,
            new SubmissionRejectedEvent(submission.Id, request.Reason),
            cancellationToken);
    }

    public async Task AllowResubmitAsync(
        SessionParticipant participant,
        AllowResubmitRequest request,
        CancellationToken cancellationToken)
    {
        participant.ResubmitAllowed = true;
        participant.ResubmitReason = request.Reason;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "ResubmitAllowed",
            nameof(SessionParticipant),
            participant.Id.ToString(),
            participant.SessionId,
            null,
            request,
            cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            SubmissionMutationPayloads.ToCloud(participant),
            cancellationToken: cancellationToken);
    }
}
