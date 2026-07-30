using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Execution.PublicCloud;

public sealed class PublicCloudSubmissionMutationHandler(
    ICloudAdapter? cloud = null) : ISubmissionMutationHandler
{
    public SessionAccessMode AccessMode => SessionAccessMode.PublicCloud;

    public async Task RejectAsync(
        Submission submission,
        RejectSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateMutationRequestId(request.MutationRequestId);
        _ = await RequireCloud().RejectPublicSubmissionAsync(
            submission.Id,
            request.Reason,
            request.MutationRequestId,
            cancellationToken);
    }

    public async Task AllowResubmitAsync(
        SessionParticipant participant,
        AllowResubmitRequest request,
        CancellationToken cancellationToken)
    {
        ValidateMutationRequestId(request.MutationRequestId);
        _ = await RequireCloud().AllowPublicResubmissionAsync(
            participant.Id,
            request.Reason,
            request.MutationRequestId,
            cancellationToken);
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
