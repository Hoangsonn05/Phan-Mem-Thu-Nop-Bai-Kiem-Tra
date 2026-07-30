using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Execution.PublicCloud;

public sealed class PublicCloudSessionParticipantMutationHandler(
    IOptions<ExamTransferOptions> options,
    IRealtimePublisher realtime,
    ICloudAdapter? cloud = null) : ISessionParticipantMutationHandler
{
    private readonly ExamTransferOptions _options = options.Value;

    public SessionAccessMode AccessMode => SessionAccessMode.PublicCloud;

    public async Task<ParticipantDto> ApproveAsync(
        SessionParticipant participant,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        if (mutationRequestId == Guid.Empty)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Thiếu MutationRequestId.");
        }

        var result = await RequireCloud().ApprovePublicParticipantAsync(
            participant.SessionId,
            participant.Id,
            mutationRequestId,
            cancellationToken);
        return SessionParticipantMutationRules.ToMutationDto(
            _options,
            participant,
            result);
    }

    public async Task RejectAsync(
        SessionParticipant participant,
        string? reason,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        ValidateMutationRequestId(mutationRequestId);
        _ = await RequireCloud().RejectPublicParticipantAsync(
            participant.SessionId,
            participant.Id,
            reason,
            mutationRequestId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDto>> BulkApproveAsync(
        ExamSession session,
        IReadOnlyList<Guid> requestedIds,
        Guid mutationRequestId,
        CancellationToken cancellationToken)
    {
        ValidateMutationRequestId(mutationRequestId);
        var result = await RequireCloud().BulkApprovePublicParticipantsAsync(
            session.Id,
            requestedIds,
            mutationRequestId,
            cancellationToken);
        var localById = session.Participants.ToDictionary(x => x.Id);
        return result.Participants
            .Where(x => localById.ContainsKey(x.ParticipantId))
            .Select(x => SessionParticipantMutationRules.ToMutationDto(
                _options,
                localById[x.ParticipantId],
                x))
            .ToList();
    }

    public async Task<ParticipantDto> AddExtraTimeAsync(
        SessionParticipant participant,
        ExtraTimeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateMutationRequestId(request.MutationRequestId);
        var result = await RequireCloud().AddPublicParticipantExtraTimeAsync(
            participant.SessionId,
            participant.Id,
            request.Minutes,
            request.Reason,
            request.MutationRequestId,
            cancellationToken);
        if (!result.EffectiveDeadlineUtc.HasValue
            || !result.ServerNowUtc.HasValue
            || !result.Revision.HasValue)
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Supabase không trả đủ contract thời gian PublicCloud.",
                502);
        }

        await realtime.PublishSessionAsync(
            participant.SessionId,
            RealtimeEvents.TimeExtended,
            result.Revision.Value,
            new TimeExtendedEvent(
                participant.Id,
                request.Minutes,
                result.EffectiveDeadlineUtc.Value,
                result.AttemptId,
                result.ServerNowUtc,
                result.Revision,
                result.RequestId ?? request.MutationRequestId),
            cancellationToken);
        return SessionParticipantMutationRules.ToMutationDto(
            _options,
            participant,
            result);
    }

    private ICloudAdapter RequireCloud() =>
        cloud ?? throw new ApiException(
            ErrorCodes.CloudOffline,
            "PublicCloud chưa được cấu hình cho thao tác giáo viên.",
            503);

    private static void ValidateMutationRequestId(Guid mutationRequestId)
    {
        if (mutationRequestId == Guid.Empty)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Thiếu MutationRequestId.");
        }
    }
}
