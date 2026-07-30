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
}
