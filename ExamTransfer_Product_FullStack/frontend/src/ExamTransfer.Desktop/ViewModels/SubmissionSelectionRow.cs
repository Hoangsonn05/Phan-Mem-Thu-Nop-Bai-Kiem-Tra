using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class SubmissionSelectionRow : ObservableObject
{
    private bool isSelected;
    private bool resubmitAllowed;

    public SubmissionSelectionRow(SubmissionSummaryDto submission)
    {
        Submission = submission ?? throw new ArgumentNullException(nameof(submission));
        resubmitAllowed = submission.ResubmitAllowed;
    }

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    public SubmissionSummaryDto Submission { get; }
    public Guid SubmissionId => Submission.Id;
    public Guid ParticipantId => Submission.ParticipantId;
    public string StudentCode => Submission.StudentCode;
    public string StudentName => Submission.DisplayName;
    public int AttemptNumber => Submission.AttemptNumber;
    public DateTimeOffset SubmittedAt =>
        Submission.ServerReceivedAtUtc
        ?? Submission.ClientSubmittedAtUtc
        ?? DateTimeOffset.MinValue;
    public bool IsLate => Submission.IsLate;
    public SubmissionStatus Status => Submission.Status;
    public bool IsOfficial => Submission.IsOfficial;
    public bool ResubmitAllowed
    {
        get => resubmitAllowed;
        private set
        {
            if (Set(ref resubmitAllowed, value))
                Raise(nameof(CanAllowResubmit));
        }
    }
    public bool CanAllowResubmit =>
        IsOfficial
        && !ResubmitAllowed
        && Status is SubmissionStatus.Submitted
            or SubmissionStatus.LateSubmitted
            or SubmissionStatus.Rejected;
    public string? ReceiptCode => Submission.ReceiptCode;
    public int CompletedFileCount =>
        Submission.Files.Count(file => file.TransferStatus == TransferStatus.Completed);
    public bool CanDownload => CompletedFileCount > 0;

    public void MarkResubmitAllowed() => ResubmitAllowed = true;
}
