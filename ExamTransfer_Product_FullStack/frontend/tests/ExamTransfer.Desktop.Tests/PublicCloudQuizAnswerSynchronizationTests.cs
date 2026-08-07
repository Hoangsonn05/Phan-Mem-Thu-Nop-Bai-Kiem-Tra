using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class PublicCloudQuizAnswerSynchronizationTests
{
    [Fact]
    public async Task ShowAfterSubmission_FinalizeAndReviewExposeScoreButMaskAnswerKey()
    {
        var handler = new QuizResultHandler(publishScore: true);
        var client = new SupabasePublicCloudClient(
            new HttpClient(handler),
            supabaseUrl: "https://project.supabase.co",
            publishableKey: "publishable-test-key");
        await client.LoginAsync("student@example.test", "password", default);

        var attempt = await client.FinalizeQuizAttemptAsync(
            handler.AttemptId,
            "pc3-frontend-finalize",
            default);
        var review = await client.GetQuizAttemptReviewAsync(handler.AttemptId, default);

        Assert.True(attempt.ScoreVisible);
        Assert.Equal(7.5m, attempt.Score);
        Assert.Equal(10m, attempt.MaxScore);
        Assert.True(review.ScoreVisible);
        Assert.Equal(7.5m, review.Score);
        Assert.False(review.CorrectAnswersVisible);
        Assert.All(review.Questions.SelectMany(x => x.Choices), choice => Assert.Null(choice.Correct));
        Assert.Null(review.GeneralComment);
        Assert.Equal(1, handler.FinalizeCalls);
        Assert.Equal(1, handler.ReviewCalls);
    }

    [Fact]
    public async Task Hidden_FinalizeKeepsAuthoritativeScoreMasked()
    {
        var handler = new QuizResultHandler(publishScore: false);
        var client = new SupabasePublicCloudClient(
            new HttpClient(handler),
            supabaseUrl: "https://project.supabase.co",
            publishableKey: "publishable-test-key");
        await client.LoginAsync("student@example.test", "password", default);

        var attempt = await client.FinalizeQuizAttemptAsync(
            handler.AttemptId,
            "pc3-frontend-hidden",
            default);

        Assert.False(attempt.ScoreVisible);
        Assert.Null(attempt.Score);
        Assert.Equal(10m, attempt.MaxScore);
    }

    [Fact]
    public async Task SaveQuizAnswers_ReturnsAuthoritativeAttemptAnswers()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var choiceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 7, 9, 0, 0, TimeSpan.Zero);
        var handler = new QuizHandler(
            sessionId,
            participantId,
            attemptId,
            questionId,
            choiceId,
            now);
        var client = new SupabasePublicCloudClient(
            new HttpClient(handler),
            supabaseUrl: "https://project.supabase.co",
            publishableKey: "publishable-test-key");
        await client.LoginAsync("student@example.test", "password", default);

        var result = await client.SaveQuizAnswersAsync(
            sessionId,
            attemptId,
            [new QuizAnswerDto(questionId, [choiceId], 1, now.AddMinutes(-1))],
            default);

        var answer = Assert.Single(result.Answers);
        Assert.Equal(questionId, answer.QuestionId);
        Assert.Empty(answer.ChoiceIds);
        Assert.Equal(2, answer.Revision);
        Assert.Equal(1, handler.SaveCalls);
        Assert.Equal(1, handler.AttemptSnapshotCalls);
    }

    private sealed class QuizHandler(
        Guid sessionId,
        Guid participantId,
        Guid attemptId,
        Guid questionId,
        Guid choiceId,
        DateTimeOffset now) : HttpMessageHandler
    {
        public int SaveCalls { get; private set; }
        public int AttemptSnapshotCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/v1/token", StringComparison.Ordinal))
                return Task.FromResult(Json(new
                {
                    access_token = "token",
                    refresh_token = "refresh",
                    expires_in = 3600
                }));
            if (path.EndsWith("/rpc/save_public_quiz_answers", StringComparison.Ordinal))
            {
                SaveCalls++;
                return Task.FromResult(JsonRaw("2"));
            }
            if (path.EndsWith("/rpc/get_public_quiz_attempt", StringComparison.Ordinal))
            {
                AttemptSnapshotCalls++;
                return Task.FromResult(Json(new
                {
                    id = attemptId,
                    sessionId,
                    participantId,
                    status = "InProgress",
                    examVersion = 1,
                    resultPolicy = "Hidden",
                    startedAtUtc = now.AddMinutes(-1),
                    deadlineUtc = now.AddMinutes(30),
                    finalizedAtUtc = (DateTimeOffset?)null,
                    scoreVisible = false,
                    score = (decimal?)null,
                    maxScore = 10,
                    questions = new[]
                    {
                        new
                        {
                            id = questionId,
                            sortOrder = 1,
                            questionText = "Question",
                            points = 10,
                            multiple = false,
                            choices = new[]
                            {
                                new { id = choiceId, sortOrder = 1, choiceText = "A" },
                                new { id = Guid.NewGuid(), sortOrder = 2, choiceText = "B" }
                            }
                        }
                    },
                    answers = new[]
                    {
                        new
                        {
                            questionId,
                            choiceIds = Array.Empty<Guid>(),
                            revision = 2,
                            clientUpdatedAtUtc = now
                        }
                    }
                }));
            }
            if (path.EndsWith("/rpc/get_public_student_timeline", StringComparison.Ordinal))
                return Task.FromResult(Json(new
                {
                    sessionId,
                    participantId,
                    sessionStatus = "InProgress",
                    startedAtUtc = now.AddMinutes(-1),
                    durationMinutes = 30,
                    extraTimeMinutes = 0,
                    effectiveDeadlineUtc = now.AddMinutes(30),
                    attemptId,
                    attemptStatus = "InProgress",
                    attemptDeadlineUtc = now.AddMinutes(30),
                    serverNowUtc = now,
                    revision = 1,
                    updatedAtUtc = now
                }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object value) =>
            JsonRaw(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        private static HttpResponseMessage JsonRaw(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    private sealed class QuizResultHandler(bool publishScore) : HttpMessageHandler
    {
        private readonly Guid sessionId = Guid.NewGuid();
        private readonly Guid participantId = Guid.NewGuid();
        private readonly Guid questionId = Guid.NewGuid();
        private readonly Guid correctChoiceId = Guid.NewGuid();
        private readonly Guid wrongChoiceId = Guid.NewGuid();
        private readonly DateTimeOffset now = new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

        public Guid AttemptId { get; } = Guid.NewGuid();
        public int FinalizeCalls { get; private set; }
        public int ReviewCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/v1/token", StringComparison.Ordinal))
                return Task.FromResult(Json(new
                {
                    access_token = "token",
                    refresh_token = "refresh",
                    expires_in = 3600
                }));
            if (path.EndsWith("/rpc/finalize_public_quiz_attempt", StringComparison.Ordinal))
            {
                FinalizeCalls++;
                return Task.FromResult(Json(AttemptSnapshot()));
            }
            if (path.EndsWith("/rpc/get_public_student_timeline", StringComparison.Ordinal))
                return Task.FromResult(Json(new
                {
                    sessionId,
                    participantId,
                    sessionStatus = "Finished",
                    startedAtUtc = now.AddMinutes(-15),
                    durationMinutes = 30,
                    extraTimeMinutes = 0,
                    effectiveDeadlineUtc = now.AddMinutes(15),
                    attemptId = AttemptId,
                    attemptStatus = "Finalized",
                    attemptDeadlineUtc = now.AddMinutes(15),
                    serverNowUtc = now,
                    revision = 2,
                    updatedAtUtc = now
                }));
            if (path.EndsWith("/rpc/get_public_quiz_attempt_review", StringComparison.Ordinal))
            {
                ReviewCalls++;
                return Task.FromResult(Json(new
                {
                    attemptId = AttemptId,
                    score = publishScore ? 7.5m : (decimal?)null,
                    maxScore = 10m,
                    scoreVisible = publishScore,
                    correctAnswersVisible = false,
                    generalComment = "must stay hidden",
                    questions = new[]
                    {
                        new
                        {
                            id = questionId,
                            sortOrder = 1,
                            questionText = "Weighted question",
                            points = 7.5m,
                            multiple = false,
                            choices = new[]
                            {
                                new { id = correctChoiceId, sortOrder = 1, choiceText = "A", correct = (bool?)true },
                                new { id = wrongChoiceId, sortOrder = 2, choiceText = "B", correct = (bool?)false }
                            }
                        }
                    },
                    answers = new[]
                    {
                        new
                        {
                            questionId,
                            choiceIds = new[] { correctChoiceId },
                            revision = 1,
                            clientUpdatedAtUtc = now.AddMinutes(-1)
                        }
                    }
                }));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private object AttemptSnapshot() => new
        {
            id = AttemptId,
            sessionId,
            participantId,
            status = "Finalized",
            examVersion = 1,
            resultPolicy = publishScore ? "ShowAfterSubmission" : "Hidden",
            startedAtUtc = now.AddMinutes(-15),
            deadlineUtc = now.AddMinutes(15),
            finalizedAtUtc = now,
            scoreVisible = publishScore,
            score = publishScore ? 7.5m : (decimal?)null,
            maxScore = 10m,
            questions = new[]
            {
                new
                {
                    id = questionId,
                    sortOrder = 1,
                    questionText = "Weighted question",
                    points = 7.5m,
                    multiple = false,
                    choices = new[]
                    {
                        new { id = correctChoiceId, sortOrder = 1, choiceText = "A" },
                        new { id = wrongChoiceId, sortOrder = 2, choiceText = "B" }
                    }
                }
            },
            answers = new[]
            {
                new
                {
                    questionId,
                    choiceIds = new[] { correctChoiceId },
                    revision = 1,
                    clientUpdatedAtUtc = now.AddMinutes(-1)
                }
            }
        };

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
    }
}
