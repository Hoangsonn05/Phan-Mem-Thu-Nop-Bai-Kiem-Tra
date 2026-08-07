using ExamTransfer.Application;
using ExamTransfer.Domain;

namespace ExamTransfer.Infrastructure.Execution.PublicCloud;

public sealed class QuizProjectionOutbox(IOutboxService outbox)
{
    public Task EnqueueChoiceDeleteAsync(
        Guid choiceId,
        CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            "quiz_choices",
            choiceId.ToString(),
            "delete",
            new { id = choiceId },
            cancellationToken: cancellationToken);

    public Task EnqueueQuestionDeleteAsync(
        Guid questionId,
        CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            "quiz_questions",
            questionId.ToString(),
            "delete",
            new { id = questionId },
            cancellationToken: cancellationToken);

    public Task EnqueueQuestionUpsertAsync(
        QuizQuestion question,
        CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            "quiz_questions",
            question.Id.ToString(),
            "upsert",
            PublicCloudProjectionPayloads.Question(question),
            cancellationToken: cancellationToken);

    public Task EnqueueChoiceUpsertAsync(
        QuizChoice choice,
        CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            "quiz_choices",
            choice.Id.ToString(),
            "upsert",
            PublicCloudProjectionPayloads.Choice(choice),
            cancellationToken: cancellationToken);

    public Task EnqueueAttemptUpsertAsync(
        QuizAttempt attempt,
        CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            "quiz_attempts",
            attempt.Id.ToString(),
            "upsert",
            AttemptCloud(attempt),
            cancellationToken: cancellationToken);

    public Task EnqueueAnswerUpsertAsync(
        QuizAnswer answer,
        CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            "quiz_answers",
            answer.Id.ToString(),
            "upsert",
            AnswerCloud(answer),
            cancellationToken: cancellationToken);

    public Task EnqueueSourceUpsertAsync(
        QuizImportSource source,
        string filePath,
        CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            "quiz_import_sources",
            source.Id.ToString(),
            "upsert",
            PublicCloudProjectionPayloads.Source(source),
            filePath,
            cancellationToken);

    private static object AttemptCloud(QuizAttempt x) => new
    {
        id = x.Id,
        session_id = x.SessionId,
        participant_id = x.ParticipantId,
        attempt_number = x.AttemptNumber,
        exam_version = x.ExamVersion,
        result_policy = x.ResultPolicySnapshot.ToString(),
        status = x.Status.ToString(),
        started_at = x.StartedAtUtc,
        deadline_at = x.DeadlineUtc,
        finalized_at = x.FinalizedAtUtc,
        auto_score = x.AutoScore,
        score = x.Score,
        max_score = x.MaxScore,
        grading_status = x.GradingStatus.ToString(),
        general_comment = x.GeneralComment,
        grader_id = x.GraderId,
        graded_at = x.GradedAtUtc,
        returned_at = x.ReturnedAtUtc,
        snapshot_json = x.SnapshotJson,
        finalize_idempotency_key = x.FinalizeIdempotencyKey,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static object AnswerCloud(QuizAnswer x) => new
    {
        id = x.Id,
        attempt_id = x.AttemptId,
        question_id = x.QuestionId,
        choice_ids = x.ChoiceIdsJson,
        revision = x.Revision,
        client_updated_at = x.ClientUpdatedAtUtc,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

}
