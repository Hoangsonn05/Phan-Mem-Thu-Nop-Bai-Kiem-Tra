using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Execution.Dispatch;

public sealed class SubmissionMutationDispatcher(
    AppDbContext db,
    IEnumerable<ISubmissionMutationHandler> handlers)
{
    private readonly IReadOnlyDictionary<
        SessionAccessMode,
        ISubmissionMutationHandler> _handlers =
        handlers.ToDictionary(x => x.AccessMode);

    public async Task RejectAsync(
        Guid submissionId,
        RejectSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Phải có lý do từ chối.");
        }

        var submission = await db.SubmissionsSet
            .Include(x => x.Participant)
            .ThenInclude(x => x.Session)
            .FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken)
            ?? throw new ApiException(
                ErrorCodes.NotFound,
                "Không tìm thấy bài nộp.",
                404);
        var handler = Resolve(submission.Participant.Session.AccessMode);
        await handler.RejectAsync(submission, request, cancellationToken);
    }

    public async Task AllowResubmitAsync(
        Guid participantId,
        AllowResubmitRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Phải có lý do cho nộp lại.");
        }

        var participant = await db.SessionParticipantsSet
            .Include(x => x.Session)
            .FirstOrDefaultAsync(x => x.Id == participantId, cancellationToken)
            ?? throw new ApiException(
                ErrorCodes.NotFound,
                "Không tìm thấy người tham gia.",
                404);
        var handler = Resolve(participant.Session.AccessMode);
        await handler.AllowResubmitAsync(
            participant,
            request,
            cancellationToken);
    }

    private ISubmissionMutationHandler Resolve(SessionAccessMode accessMode) =>
        accessMode switch
        {
            SessionAccessMode.LanOnly =>
                _handlers[SessionAccessMode.LanOnly],
            SessionAccessMode.PublicCloud =>
                _handlers[SessionAccessMode.PublicCloud],
            _ => throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Chế độ truy cập phòng thi không được hỗ trợ.",
                409)
        };
}
