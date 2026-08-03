namespace ExamTransfer.Shared.Contracts;

public sealed record GradingWorkItemDto(
    Guid Id,
    GradingWorkItemType Type,
    Guid SessionId,
    Guid ParticipantId,
    string StudentCode,
    string DisplayName,
    string ExamTitle,
    DateTimeOffset SubmittedAtUtc,
    GradingStatus Status,
    decimal? AutoScore,
    decimal? Score,
    decimal MaxScore,
    Guid? PrimaryFileId = null,
    Guid ExamId = default,
    int AttemptNumber = 1,
    bool IsLate = false);

public sealed record QuizChoiceReviewDto(
    Guid Id,
    string Text,
    int Order,
    bool Selected,
    bool? Correct);

public sealed record QuizQuestionReviewDto(
    Guid Id,
    string Text,
    int Order,
    decimal Points,
    decimal? EarnedPoints,
    IReadOnlyList<QuizChoiceReviewDto> Choices);

public sealed record QuizGradeDetailDto(
    Guid AttemptId,
    Guid SessionId,
    Guid ParticipantId,
    string StudentCode,
    string DisplayName,
    string ExamTitle,
    decimal? AutoScore,
    decimal? Score,
    decimal MaxScore,
    GradingStatus Status,
    string? GeneralComment,
    Guid? GraderId,
    DateTimeOffset? GradedAtUtc,
    DateTimeOffset? ReturnedAtUtc,
    string RowVersion,
    IReadOnlyList<QuizQuestionReviewDto> Questions);

public sealed record SaveQuizGradeRequest(
    decimal? Score,
    string? GeneralComment,
    string RowVersion,
    Guid MutationRequestId = default);
public sealed record ReturnQuizGradeRequest(
    string? Message,
    string RowVersion,
    Guid MutationRequestId = default);
public sealed record ReopenQuizGradeRequest(
    string Reason,
    string RowVersion,
    Guid MutationRequestId = default);

public sealed record StudentQuizReviewDto(
    Guid AttemptId,
    decimal? Score,
    decimal MaxScore,
    bool ScoreVisible,
    bool CorrectAnswersVisible,
    string? GeneralComment,
    IReadOnlyList<QuizQuestionReviewDto> Questions);

public sealed record SubmissionPreviewEntryDto(
    string Key,
    string Name,
    long SizeBytes,
    long CompressedSizeBytes,
    bool PreviewSupported,
    string? UnsupportedReason);

public sealed record SubmissionPreviewManifestDto(
    Guid SubmissionId,
    Guid FileId,
    string FileName,
    bool IsArchive,
    IReadOnlyList<SubmissionPreviewEntryDto> Entries);

public sealed record SubmissionPreviewDto(
    Guid SubmissionId,
    Guid FileId,
    string Name,
    string ContentType,
    string Content,
    bool Truncated,
    bool IsSourceCode,
    string? EntryKey);
