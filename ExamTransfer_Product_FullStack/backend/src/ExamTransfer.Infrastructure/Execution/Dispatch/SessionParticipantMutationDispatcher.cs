using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Execution.Dispatch;

public sealed class SessionParticipantMutationDispatcher(
    AppDbContext db,
    IEnumerable<ISessionParticipantMutationHandler> handlers)
{
    private readonly IReadOnlyDictionary<SessionAccessMode, ISessionParticipantMutationHandler> _handlers =
        handlers.ToDictionary(x => x.AccessMode);

    public async Task<ParticipantDto> ApproveAsync(
        Guid sessionId,
        Guid participantId,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        var participant = await db.SessionParticipantsSet
            .Include(x => x.Session)
            .ThenInclude(x => x.Exam)
            .FirstOrDefaultAsync(
                x => x.Id == participantId && x.SessionId == sessionId,
                cancellationToken)
            ?? throw new ApiException(
                ErrorCodes.NotFound,
                "Không tìm thấy người tham gia.",
                404);

        var accessMode =
            participant.Session.AccessMode == SessionAccessMode.PublicCloud
                ? SessionAccessMode.PublicCloud
                : SessionAccessMode.LanOnly;
        var handler = _handlers[accessMode];

        return await handler.ApproveAsync(
            participant,
            mutationRequestId,
            cancellationToken);
    }

    public async Task RejectAsync(
        Guid sessionId,
        Guid participantId,
        string? reason,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        var participant = await db.SessionParticipantsSet
            .Include(x => x.Session)
            .FirstOrDefaultAsync(
                x => x.Id == participantId && x.SessionId == sessionId,
                cancellationToken)
            ?? throw new ApiException(
                ErrorCodes.NotFound,
                "Không tìm thấy người tham gia.",
                404);

        await _handlers[participant.Session.AccessMode].RejectAsync(
            participant,
            reason,
            mutationRequestId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDto>> BulkApproveAsync(
        Guid sessionId,
        BulkApproveRequest request,
        CancellationToken cancellationToken)
    {
        var requestedIds = request.ParticipantIds.Distinct().ToList();
        if (requestedIds.Count == 0)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Cần chọn ít nhất một học sinh để duyệt.");
        }

        var session = await db.ExamSessionsSet
            .Include(x => x.Exam)
            .Include(x => x.Participants)
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            ?? throw new ApiException(
                ErrorCodes.NotFound,
                "Không tìm thấy phòng thi.",
                404);

        return await _handlers[session.AccessMode].BulkApproveAsync(
            session,
            requestedIds,
            request.MutationRequestId,
            cancellationToken);
    }

    public async Task<ParticipantDto> AddExtraTimeAsync(
        Guid sessionId,
        Guid participantId,
        ExtraTimeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Minutes <= 0 || string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Số phút và lý do là bắt buộc.");
        }

        var participant = await db.SessionParticipantsSet
            .Include(x => x.Session)
            .ThenInclude(x => x.Exam)
            .FirstOrDefaultAsync(
                x => x.Id == participantId && x.SessionId == sessionId,
                cancellationToken)
            ?? throw new ApiException(
                ErrorCodes.NotFound,
                "Không tìm thấy người tham gia.",
                404);

        return await _handlers[participant.Session.AccessMode].AddExtraTimeAsync(
            participant,
            request,
            cancellationToken);
    }
}
