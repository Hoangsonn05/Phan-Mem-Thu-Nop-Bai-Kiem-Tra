using System.Globalization;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Models;

public enum StudentResultKind
{
    Quiz,
    EssayFile
}

public sealed record StudentResultAttachment(
    Guid Id,
    string Name,
    long SizeBytes,
    string MimeType,
    string DownloadPath,
    Guid ResultId = default);

public sealed record StudentReturnedResult(
    Guid ResultId,
    Guid SessionId,
    Guid ParticipantId,
    string Title,
    StudentResultKind Kind,
    int? AttemptNumber,
    GradingStatus Status,
    decimal? Score,
    decimal MaxScore,
    string? Comment,
    DateTimeOffset? ReturnedAtUtc,
    SessionAccessMode SourceMode,
    IReadOnlyList<QuizQuestionReviewDto> Questions,
    IReadOnlyList<StudentResultAttachment> Attachments);

public sealed class StudentResultPresentationModel
{
    public StudentResultPresentationModel(StudentReturnedResult source)
    {
        ResultId = source.ResultId;
        SessionId = source.SessionId;
        ParticipantId = source.ParticipantId;
        Title = source.Title;
        Kind = source.Kind;
        AttemptNumber = source.AttemptNumber;
        Status = source.Status;
        Score = source.Score;
        MaxScore = source.MaxScore;
        CommentText = string.IsNullOrWhiteSpace(source.Comment) ? "Không có nhận xét." : source.Comment;
        ReturnedAtUtc = source.ReturnedAtUtc;
        SourceMode = source.SourceMode;
        Questions = source.Questions
            .OrderBy(question => question.Order)
            .Select(question => new StudentResultQuestionPresentationModel(question))
            .ToArray();
        Attachments = source.Attachments
            .Select(attachment => attachment with { ResultId = source.ResultId })
            .ToArray();
    }

    public Guid ResultId { get; }
    public Guid SessionId { get; }
    public Guid ParticipantId { get; }
    public string Title { get; }
    public StudentResultKind Kind { get; }
    public int? AttemptNumber { get; }
    public GradingStatus Status { get; }
    public decimal? Score { get; }
    public decimal MaxScore { get; }
    public string CommentText { get; }
    public DateTimeOffset? ReturnedAtUtc { get; }
    public SessionAccessMode SourceMode { get; }
    public IReadOnlyList<StudentResultQuestionPresentationModel> Questions { get; }
    public IReadOnlyList<StudentResultAttachment> Attachments { get; }
    public string TypeText => Kind == StudentResultKind.Quiz ? "Bài trắc nghiệm" : "Bài tự luận/file";
    public string AttemptText => AttemptNumber.HasValue ? $"Lần {AttemptNumber}" : "Chưa có dữ liệu attempt";
    public string StatusText => Status == GradingStatus.Returned ? "Đã trả" : Status.ToString();
    public string ScoreText => Score.HasValue
        ? $"{Score.Value.ToString("0.##", CultureInfo.CurrentCulture)}/{MaxScore.ToString("0.##", CultureInfo.CurrentCulture)}"
        : $"—/{MaxScore.ToString("0.##", CultureInfo.CurrentCulture)}";
    public string ReturnedAtText => ReturnedAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture)
        ?? "Không có dữ liệu";
    public string SourceText => SourceMode == SessionAccessMode.PublicCloud ? "PublicCloud" : "OnlyLAN";
    public bool IsQuiz => Kind == StudentResultKind.Quiz;
    public bool HasQuestions => Questions.Count > 0;
    public bool HasAttachments => Attachments.Count > 0;
}

public sealed class StudentResultQuestionPresentationModel
{
    public StudentResultQuestionPresentationModel(QuizQuestionReviewDto question)
    {
        Order = question.Order;
        Text = question.Text;
        EarnedPoints = question.EarnedPoints;
        MaxPoints = question.Points;
        var selected = question.Choices.Where(choice => choice.Selected).ToArray();
        IsBlank = selected.Length == 0;
        IsCorrect = !IsBlank && question.EarnedPoints.HasValue && question.EarnedPoints.Value == question.Points;
        OutcomeText = IsBlank ? "Bỏ trống" : IsCorrect ? "Đúng" : "Sai";
        SelectedAnswerText = IsBlank
            ? "Bỏ trống"
            : string.Join(", ", selected.OrderBy(choice => choice.Order).Select(choice => choice.Text));
        CorrectAnswerText = string.Join(", ", question.Choices
            .Where(choice => choice.Correct == true)
            .OrderBy(choice => choice.Order)
            .Select(choice => choice.Text));
    }

    public int Order { get; }
    public string Text { get; }
    public decimal? EarnedPoints { get; }
    public decimal MaxPoints { get; }
    public string OutcomeText { get; }
    public string SelectedAnswerText { get; }
    public string CorrectAnswerText { get; }
    public bool IsBlank { get; }
    public bool IsCorrect { get; }
}
