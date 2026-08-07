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
}
