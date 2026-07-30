using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Execution.PublicCloud;

public sealed class PublicCloudSessionParticipantMutationHandler(
    IOptions<ExamTransferOptions> options,
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

    private ICloudAdapter RequireCloud() =>
        cloud ?? throw new ApiException(
            ErrorCodes.CloudOffline,
            "PublicCloud chưa được cấu hình cho thao tác giáo viên.",
            503);
}
