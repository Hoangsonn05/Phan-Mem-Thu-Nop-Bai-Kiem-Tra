using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Services;

internal static class QuizStudentResultVisibility
{
    public static bool IsScoreVisible(QuizAttempt attempt) =>
        HasPersistedAuthoritativeScore(attempt)
        && (IsManuallyReturned(attempt) || IsAutoPublished(attempt));

    public static bool AreCorrectAnswersVisible(QuizAttempt attempt) =>
        HasPersistedAuthoritativeScore(attempt) && IsManuallyReturned(attempt);

    public static DateTimeOffset? ResultVisibleAtUtc(QuizAttempt attempt)
    {
        if (!HasPersistedAuthoritativeScore(attempt))
            return null;
        if (IsManuallyReturned(attempt))
            return attempt.ReturnedAtUtc!.Value.ToUniversalTime();
        return IsAutoPublished(attempt)
            ? attempt.FinalizedAtUtc!.Value.ToUniversalTime()
            : null;
    }

    private static bool IsManuallyReturned(QuizAttempt attempt) =>
        attempt.Status == QuizAttemptStatus.Finalized
        && attempt.GradingStatus == GradingStatus.Returned
        && IsUtc(attempt.ReturnedAtUtc);

    private static bool IsAutoPublished(QuizAttempt attempt) =>
        attempt.Status == QuizAttemptStatus.Finalized
        && attempt.ResultPolicySnapshot == QuizResultPolicy.ShowAfterSubmission
        && attempt.GradingStatus == GradingStatus.Graded
        && !attempt.ReturnedAtUtc.HasValue
        && IsUtc(attempt.FinalizedAtUtc);

    private static bool HasPersistedAuthoritativeScore(QuizAttempt attempt) =>
        IsUtc(attempt.StartedAtUtc)
        && IsUtc(attempt.FinalizedAtUtc)
        && attempt.FinalizedAtUtc >= attempt.StartedAtUtc
        && attempt.Score.HasValue
        && attempt.AutoScore.HasValue
        && attempt.Score == attempt.AutoScore
        && attempt.MaxScore == QuizGradeAuthoritativeScoring.RequiredMaxScore
        && attempt.Score >= 0
        && attempt.Score <= attempt.MaxScore;

    private static bool IsUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    private static bool IsUtc(DateTimeOffset? value) =>
        value.HasValue && IsUtc(value.Value);
}
