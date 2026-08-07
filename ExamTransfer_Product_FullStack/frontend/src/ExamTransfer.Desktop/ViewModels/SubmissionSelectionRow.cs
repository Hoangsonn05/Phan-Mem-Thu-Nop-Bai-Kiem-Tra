using System.Globalization;
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

    public SubmissionSelectionRow(TeacherQuizAttemptSummaryDto quizAttempt)
    {
        QuizAttempt = quizAttempt ?? throw new ArgumentNullException(nameof(quizAttempt));
    }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (CanSelect)
                Set(ref isSelected, value);
        }
    }

    public SubmissionSummaryDto? Submission { get; }
    public TeacherQuizAttemptSummaryDto? QuizAttempt { get; }
    public bool IsFileSubmission => Submission is not null;
    public bool IsQuizAttempt => QuizAttempt is not null;
    public bool CanSelect => IsFileSubmission;
    public Guid ItemId => Submission?.Id ?? QuizAttempt!.Id;
    public Guid? SubmissionId => Submission?.Id;
    public Guid ParticipantId => Submission?.ParticipantId ?? QuizAttempt!.ParticipantId;
    public string StudentCode => Submission?.StudentCode ?? QuizAttempt!.StudentCode;
    public string StudentName => Submission?.DisplayName ?? QuizAttempt!.FullName;
    public int AttemptNumber => Submission?.AttemptNumber ?? QuizAttempt!.AttemptNumber;
    public DateTimeOffset SubmittedAt =>
        Submission?.ServerReceivedAtUtc
        ?? Submission?.ClientSubmittedAtUtc
        ?? QuizAttempt?.FinalizedAtUtc
        ?? DateTimeOffset.MinValue;
    public DateTimeOffset? StartedAtUtc => QuizAttempt?.StartedAtUtc;
    public DateTimeOffset? FinalizedAtUtc => QuizAttempt?.FinalizedAtUtc;
    public long? DurationSeconds => QuizAttempt?.DurationSeconds;
    public decimal? Score => QuizAttempt?.Score;
    public decimal? MaxScore => QuizAttempt?.MaxScore;
    public GradingStatus? GradingStatus => QuizAttempt?.GradingStatus;
    public QuizAttemptStatus? AttemptStatus => QuizAttempt?.Status;
    public string StatusText => Submission?.Status.ToString() ?? QuizAttempt!.Status.ToString();
    public string GradingStatusText => QuizAttempt?.GradingStatus.ToString() ?? string.Empty;
    public string ScoreText => QuizAttempt is null
        ? string.Empty
        : QuizAttempt.Score.HasValue
            ? QuizAttempt.Score.Value.ToString("0.##", CultureInfo.CurrentCulture)
            : "Không hợp lệ";
    public string MaxScoreText => QuizAttempt?.MaxScore?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
    public string ScoreSummaryText => QuizAttempt is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(MaxScoreText)
            ? ScoreText
            : $"{ScoreText} / {MaxScoreText}";
    public string StartedAtText => QuizAttempt is null
        ? string.Empty
        : QuizAttempt.StartedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
    public string FinalizedAtText => QuizAttempt?.FinalizedAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) ?? string.Empty;
    public string DurationText => DurationSeconds.HasValue
        ? TimeSpan.FromSeconds(DurationSeconds.Value).ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture)
        : string.Empty;
    public string? DataIssue => QuizAttempt?.DataIssue;
    public bool IsLate => Submission?.IsLate ?? QuizAttempt?.IsLate == true;
    public SubmissionStatus? FileSubmissionStatus => Submission?.Status;
    public bool IsOfficial => Submission?.IsOfficial == true;
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
        IsFileSubmission
        && IsOfficial
        && !ResubmitAllowed
        && FileSubmissionStatus is SubmissionStatus.Submitted
            or SubmissionStatus.LateSubmitted
            or SubmissionStatus.Rejected;
    public string? ReceiptCode => Submission?.ReceiptCode;
    public int CompletedFileCount =>
        Submission?.Files.Count(file => file.TransferStatus == TransferStatus.Completed) ?? 0;
    public bool CanDownload => CompletedFileCount > 0;

    public void MarkResubmitAllowed() => ResubmitAllowed = true;
}
