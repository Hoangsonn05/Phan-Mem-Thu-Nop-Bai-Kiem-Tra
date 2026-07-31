using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public static class SubmissionEligibilityReasonCodes
{
    public const string Allowed = "ALLOWED";
    public const string NoActiveSession = "NO_ACTIVE_SESSION";
    public const string ParticipantNotApproved = "PARTICIPANT_NOT_APPROVED";
    public const string SessionNotAcceptingSubmissions = "SESSION_NOT_ACCEPTING_SUBMISSIONS";
    public const string WrongDeliveryType = "WRONG_DELIVERY_TYPE";
    public const string InvalidFile = "INVALID_FILE";
    public const string SubmissionAlreadyProcessing = "SUBMISSION_ALREADY_PROCESSING";
    public const string SubmissionAlreadyCompleted = "SUBMISSION_ALREADY_COMPLETED";
    public const string ResubmitNotAllowed = "RESUBMIT_NOT_ALLOWED";
    public const string Busy = "BUSY";
}

public sealed record SubmissionEligibilityInput(
    bool IsBusy,
    bool HasSession,
    Guid? SessionId,
    Guid? ParticipantId,
    ParticipantStatus? ParticipantStatus,
    SessionStatus? SessionStatus,
    ExamDeliveryType DeliveryType,
    bool HasValidFile,
    bool HasActiveQueue,
    bool HasSuccessfulReceipt,
    SubmissionStatus SubmissionStatus,
    bool ResubmitAllowed);

public sealed record SubmissionEligibilityDecision(
    bool Allowed,
    string ReasonCode,
    string UserMessage);

public static class SubmissionEligibilityPolicy
{
    public static SubmissionEligibilityDecision Evaluate(SubmissionEligibilityInput input)
    {
        if (input.IsBusy)
            return Denied(
                SubmissionEligibilityReasonCodes.Busy,
                "Hệ thống đang xử lý thao tác nộp bài trước đó.");
        if (!input.HasSession
            || !input.SessionId.HasValue
            || !input.ParticipantId.HasValue)
            return Denied(
                SubmissionEligibilityReasonCodes.NoActiveSession,
                "Hãy tham gia phòng thi trước khi nộp bài.");
        if (input.ParticipantStatus != ParticipantStatus.Approved)
            return Denied(
                SubmissionEligibilityReasonCodes.ParticipantNotApproved,
                "Chỉ học sinh đã được duyệt mới có thể nộp bài.");
        if (input.SessionStatus is not (ExamTransfer.Shared.Contracts.SessionStatus.InProgress
                or ExamTransfer.Shared.Contracts.SessionStatus.Collecting))
            return Denied(
                SubmissionEligibilityReasonCodes.SessionNotAcceptingSubmissions,
                "Phòng thi hiện không nhận bài nộp.");
        if (input.DeliveryType != ExamDeliveryType.FileSubmission)
            return Denied(
                SubmissionEligibilityReasonCodes.WrongDeliveryType,
                "Bài thi trắc nghiệm không sử dụng luồng nộp file.");
        if (!input.HasValidFile)
            return Denied(
                SubmissionEligibilityReasonCodes.InvalidFile,
                "Hãy chọn một file ZIP, RAR hoặc 7Z hợp lệ trước khi nộp.");
        if (input.HasActiveQueue)
            return Denied(
                SubmissionEligibilityReasonCodes.SubmissionAlreadyProcessing,
                "Bài nộp đang được xử lý; hệ thống sẽ tự gửi tiếp từ bản đã lưu.");

        var hasFinalizedAttempt = input.HasSuccessfulReceipt
            || input.SubmissionStatus is ExamTransfer.Shared.Contracts.SubmissionStatus.Submitted
                or ExamTransfer.Shared.Contracts.SubmissionStatus.LateSubmitted
                or ExamTransfer.Shared.Contracts.SubmissionStatus.Rejected;
        if (hasFinalizedAttempt && !input.ResubmitAllowed)
        {
            return input.HasSuccessfulReceipt
                ? Denied(
                    SubmissionEligibilityReasonCodes.SubmissionAlreadyCompleted,
                    "Bài nộp đã có biên nhận; giáo viên chưa cho phép nộp lại.")
                : Denied(
                    SubmissionEligibilityReasonCodes.ResubmitNotAllowed,
                    "Bài đã được nộp; giáo viên chưa cho phép nộp lại.");
        }

        return new(
            true,
            SubmissionEligibilityReasonCodes.Allowed,
            "Bài làm đủ điều kiện để nộp.");
    }

    private static SubmissionEligibilityDecision Denied(
        string reasonCode,
        string userMessage) =>
        new(false, reasonCode, userMessage);
}
