namespace ExamTransfer.Shared.Contracts;

public sealed record QuizImportFileRequest(string FileName, string Base64Content);
public sealed record QuizImportResultDto(Guid ExamId, int Version, int QuestionCount, decimal MaxScore);
public sealed record QuizChoiceDto(Guid Id, string Text, int Order);
public sealed record QuizQuestionDto(Guid Id, string Text, int Order, decimal Points, bool Multiple, IReadOnlyList<QuizChoiceDto> Choices);
public sealed record QuizAttemptDto(Guid Id, Guid SessionId, Guid ParticipantId, QuizAttemptStatus Status, int ExamVersion, DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineUtc, DateTimeOffset? FinalizedAtUtc, decimal? Score, decimal MaxScore, IReadOnlyList<QuizQuestionDto> Questions, IReadOnlyList<QuizAnswerDto> Answers);
public sealed record QuizAnswerDto(Guid QuestionId, IReadOnlyList<Guid> ChoiceIds, long Revision, DateTimeOffset ClientUpdatedAtUtc);
public sealed record SyncQuizAnswersRequest(IReadOnlyList<QuizAnswerDto> Answers);
public sealed record SyncQuizAnswersResultDto(Guid AttemptId, IReadOnlyList<QuizAnswerDto> Answers, DateTimeOffset ServerNowUtc);
public sealed record FinalizeQuizAttemptRequest(string IdempotencyKey, DateTimeOffset ClientFinalizedAtUtc);

public sealed record QuizImportDocument(IReadOnlyList<QuizImportQuestion> Questions);
public sealed record QuizImportQuestion(string Text, decimal Points, bool Multiple, IReadOnlyList<string> Choices, IReadOnlyList<int> CorrectChoiceIndexes);
