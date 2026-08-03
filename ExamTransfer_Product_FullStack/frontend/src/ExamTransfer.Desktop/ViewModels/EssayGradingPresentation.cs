using System.Globalization;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class EssaySubmissionReviewRow : ObservableObject
{
    private GradingStatus status;
    private bool wasReopened;

    public EssaySubmissionReviewRow(GradingWorkItemDto workItem)
    {
        WorkItem = workItem;
        status = workItem.Status;
    }

    public GradingWorkItemDto WorkItem { get; }
    public Guid SubmissionId => WorkItem.Id;
    public GradingWorkItemType Type => WorkItem.Type;
    public string StudentCode => WorkItem.StudentCode;
    public string StudentName => WorkItem.DisplayName;
    public string ExamTitle => WorkItem.ExamTitle;
    public int AttemptNumber => WorkItem.AttemptNumber;
    public DateTimeOffset SubmittedAtUtc => WorkItem.SubmittedAtUtc;
    public bool IsLate => WorkItem.IsLate;
    public GradingStatus Status => status;
    public string StatusText => GradingStatusPresentation.ToText(status, wasReopened);

    public void ApplyStatus(GradingStatus value, bool reopened = false)
    {
        status = value;
        wasReopened = reopened;
        Raise(nameof(Status));
        Raise(nameof(StatusText));
    }
}

public sealed class EssaySubmissionDetail : ObservableObject
{
    private GradingStatus status;
    private bool wasReopened;

    public EssaySubmissionDetail(EssaySubmissionReviewRow row, GradingStatus status)
    {
        Row = row;
        this.status = status;
    }

    public EssaySubmissionReviewRow Row { get; }
    public Guid SubmissionId => Row.SubmissionId;
    public string StudentCode => Row.StudentCode;
    public string StudentName => Row.StudentName;
    public string ExamTitle => Row.ExamTitle;
    public int? AttemptNumber => Row.AttemptNumber;
    public DateTimeOffset? SubmittedAtUtc => Row.SubmittedAtUtc;
    public bool IsLate => Row.IsLate;
    public GradingStatus Status => status;
    public string StatusText => GradingStatusPresentation.ToText(status, wasReopened);

    public void ApplyStatus(GradingStatus value, bool reopened = false)
    {
        status = value;
        wasReopened = reopened;
        Row.ApplyStatus(value, reopened);
        Raise(nameof(Status));
        Raise(nameof(StatusText));
    }
}

public sealed class SubmissionFilePresentationModel : ObservableObject
{
    private string? localPath;

    public SubmissionFilePresentationModel(SubmissionFileDto file) => File = file;

    public SubmissionFileDto File { get; }
    public Guid Id => File.Id;
    public string Name => File.Name;
    public long SizeBytes => File.SizeBytes;
    public string MimeType => File.MimeType;
    public bool CanDownload => File.TransferStatus == TransferStatus.Completed;
    public string? LocalPath
    {
        get => localPath;
        set => Set(ref localPath, value);
    }
}

public sealed class GradingEditorState : ObservableObject
{
    private string scoreText = string.Empty;
    private decimal maxScore;
    private string comment = string.Empty;
    private string validationMessage = "Vui lòng nhập điểm.";
    private decimal? parsedScore;
    private bool isValid;
    private bool isDirty;
    private bool loading;

    public string ScoreText
    {
        get => scoreText;
        set
        {
            if (!Set(ref scoreText, value)) return;
            Validate();
            MarkDirty();
        }
    }

    public decimal MaxScore
    {
        get => maxScore;
        private set
        {
            if (!Set(ref maxScore, value)) return;
            Validate();
        }
    }

    public string Comment
    {
        get => comment;
        set
        {
            if (!Set(ref comment, value)) return;
            MarkDirty();
        }
    }

    public string ValidationMessage { get => validationMessage; private set => Set(ref validationMessage, value); }
    public decimal? ParsedScore { get => parsedScore; private set => Set(ref parsedScore, value); }
    public bool IsValid { get => isValid; private set => Set(ref isValid, value); }
    public bool IsDirty { get => isDirty; private set => Set(ref isDirty, value); }

    public void Load(decimal? score, decimal maximum, string? generalComment, GradingStatus status)
    {
        loading = true;
        MaxScore = maximum;
        ScoreText = score?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        Comment = generalComment ?? string.Empty;
        loading = false;
        Validate();
        IsDirty = false;
    }

    public void Clear()
    {
        loading = true;
        MaxScore = 0;
        ScoreText = string.Empty;
        Comment = string.Empty;
        loading = false;
        Validate();
        IsDirty = false;
    }

    private void MarkDirty()
    {
        if (!loading) IsDirty = true;
    }

    private void Validate()
    {
        ParsedScore = null;
        if (string.IsNullOrWhiteSpace(ScoreText))
        {
            ValidationMessage = "Vui lòng nhập điểm.";
            IsValid = false;
            return;
        }

        if (!decimal.TryParse(ScoreText, NumberStyles.Number, CultureInfo.CurrentCulture, out var value))
        {
            ValidationMessage = "Điểm phải là một số hợp lệ.";
            IsValid = false;
            return;
        }

        if (value < 0 || value > MaxScore)
        {
            ValidationMessage = $"Điểm phải từ 0 đến {MaxScore.ToString(CultureInfo.CurrentCulture)}.";
            IsValid = false;
            return;
        }

        ParsedScore = value;
        ValidationMessage = string.Empty;
        IsValid = true;
    }
}

internal static class GradingStatusPresentation
{
    public static string ToText(GradingStatus status, bool wasReopened = false) => status switch
    {
        GradingStatus.NotGraded => "Chưa chấm",
        GradingStatus.InProgress when wasReopened => "Mở lại",
        GradingStatus.InProgress => "Đang chấm",
        GradingStatus.Graded => "Đã chấm",
        GradingStatus.Returned => "Đã trả",
        _ => status.ToString()
    };
}
