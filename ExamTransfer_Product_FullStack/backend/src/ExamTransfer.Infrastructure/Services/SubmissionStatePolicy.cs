using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Services;

internal static class SubmissionStatePolicy
{
    public static bool SessionAcceptsSubmission(SessionStatus status) =>
        status is SessionStatus.InProgress or SessionStatus.Collecting;

    public static bool IsActiveSubmissionStatus(SubmissionStatus status) =>
        status is SubmissionStatus.NotStarted
            or SubmissionStatus.Preparing
            or SubmissionStatus.Uploading
            or SubmissionStatus.Verifying
            or SubmissionStatus.Failed;

    public static bool IsCompletedSubmissionStatus(SubmissionStatus status) =>
        status is SubmissionStatus.Submitted
            or SubmissionStatus.LateSubmitted
            or SubmissionStatus.Rejected;

    public static bool AcceptsChunks(SubmissionStatus status) =>
        status is SubmissionStatus.NotStarted
            or SubmissionStatus.Preparing
            or SubmissionStatus.Uploading
            or SubmissionStatus.Failed;
}
