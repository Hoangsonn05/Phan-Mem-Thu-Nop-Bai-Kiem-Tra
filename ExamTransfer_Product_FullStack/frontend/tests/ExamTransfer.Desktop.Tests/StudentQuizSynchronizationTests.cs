using System.Net;
using System.Net.Http;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentQuizSynchronizationTests
{
    [Fact]
    public async Task Load_LocalRevisionHigherThanServer_ResyncsExactlyOnce()
    {
        var fixture = CreateFixture();
        var server = Answer(fixture.Question, 0, 1, fixture.Now.AddMinutes(-2));
        var local = Answer(fixture.Question, 1, 2, fixture.Now.AddMinutes(-1));
        var attempt = fixture.Attempt with { Answers = [server] };
        var api = new RecordingBackendClient(fixture.Now)
        {
            SyncQuizAnswersResultResponse = new(fixture.Attempt.Id, [server], fixture.Now)
        };
        var localStore = new ControlledLocalStore([local]);

        using var viewModel = CreateViewModel(fixture, attempt, api, localStore);
        await viewModel.InitializeAsync(CancellationToken.None);
        localStore.Release();

        await WaitUntilAsync(() => api.PutRequests
            .OfType<SyncQuizAnswersRequest>()
            .Count(request => request.Answers.Any(answer => answer.Revision == 2)) == 1);

        Assert.Equal(1, api.PutRequests
            .OfType<SyncQuizAnswersRequest>()
            .Count(request => request.Answers.Any(answer => answer.Revision == 2)));
        Assert.True(viewModel.Questions.Single().Choices[1].IsSelected);
    }

    [Fact]
    public async Task SyncResponse_LowerRevision_DoesNotOverwriteNewerLocalAnswer()
    {
        var fixture = CreateFixture();
        var revision1 = Answer(fixture.Question, 0, 1, fixture.Now.AddMinutes(-2));
        var attempt = fixture.Attempt with { Answers = [revision1] };
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<SyncQuizAnswersRequest>();
        var api = new RecordingBackendClient(fixture.Now);
        api.SyncQuizAnswersHandler = async (request, ct) =>
        {
            lock (requests) requests.Add(request);
            if (requests.Count == 1)
            {
                firstRequestStarted.TrySetResult();
                await releaseFirstResponse.Task.WaitAsync(ct);
                return new(fixture.Attempt.Id, [revision1], fixture.Now);
            }

            return new(fixture.Attempt.Id, request.Answers, fixture.Now);
        };

        using var viewModel = CreateViewModel(fixture, attempt, api, new FakeLocalStore([]));
        var loadTask = viewModel.InitializeAsync(CancellationToken.None);
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var row = viewModel.Questions.Single();
        row.Choices[1].IsSelected = true;
        releaseFirstResponse.TrySetResult();

        await loadTask;
        await WaitUntilAsync(() =>
        {
            lock (requests) return requests.Count >= 2;
        });

        SyncQuizAnswersRequest second;
        lock (requests) second = requests[1];
        Assert.Contains(second.Answers, answer =>
            answer.QuestionId == fixture.Question.Id
            && answer.Revision == 2
            && answer.ChoiceIds.SequenceEqual([fixture.Question.Choices[1].Id]));
        Assert.False(row.Choices[0].IsSelected);
        Assert.True(row.Choices[1].IsSelected);
    }

    [Fact]
    public async Task AutoSync_ValidationFailure_IsNotReportedAsOffline()
    {
        var fixture = CreateFixture();
        var api = new RecordingBackendClient(fixture.Now)
        {
            SyncQuizAnswersResultResponse = new(fixture.Attempt.Id, [], fixture.Now)
        };
        using var viewModel = CreateViewModel(fixture, fixture.Attempt, api, new FakeLocalStore([]));
        await viewModel.InitializeAsync(CancellationToken.None);
        var initialRequestCount = api.PutRequests.Count;
        api.PutErrorResponse = new(
            "QUIZ_CHOICE_INVALID",
            "The selected choice is invalid.",
            Details: new BackendTransportDetails(422, "/quiz/answers", null, true));

        viewModel.Questions.Single().Choices[0].IsSelected = true;
        await WaitUntilAsync(() => api.PutRequests.Count > initialRequestCount);

        Assert.DoesNotContain("ngoai tuyen", RemoveDiacritics(viewModel.Status), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("QUIZ_CHOICE_INVALID", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal("danger", viewModel.StatusTone);
        Assert.Equal(1, viewModel.AnsweredCount);
    }

    [Fact]
    public async Task AutoSync_TransportFailure_IsReportedAsOffline()
    {
        var fixture = CreateFixture();
        var api = new RecordingBackendClient(fixture.Now)
        {
            SyncQuizAnswersResultResponse = new(fixture.Attempt.Id, [], fixture.Now)
        };
        using var viewModel = CreateViewModel(fixture, fixture.Attempt, api, new FakeLocalStore([]));
        await viewModel.InitializeAsync(CancellationToken.None);
        api.SyncQuizAnswersResultResponse = null;
        api.SyncQuizAnswersHandler = (_, _) => Task.FromException<SyncQuizAnswersResultDto>(
            new HttpRequestException("network unavailable", null, HttpStatusCode.ServiceUnavailable));

        viewModel.Questions.Single().Choices[0].IsSelected = true;
        await WaitUntilAsync(() => viewModel.StatusTone == "warning");

        Assert.Contains("ngoai tuyen", RemoveDiacritics(viewModel.Status), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, viewModel.AnsweredCount);
    }

    [Fact]
    public async Task SelectedThenCleared_CreatesEmptySelectionWithHigherRevision()
    {
        var fixture = CreateFixture();
        var api = new RecordingBackendClient(fixture.Now)
        {
            SyncQuizAnswersResultResponse = new(fixture.Attempt.Id, [], fixture.Now)
        };
        using var viewModel = CreateViewModel(fixture, fixture.Attempt, api, new FakeLocalStore([]));
        await viewModel.InitializeAsync(CancellationToken.None);
        var choice = viewModel.Questions.Single().Choices[0];

        choice.IsSelected = true;
        await WaitUntilAsync(() => api.PutRequests.OfType<SyncQuizAnswersRequest>()
            .Any(request => request.Answers.Any(answer => answer.Revision == 1)));
        choice.IsSelected = false;
        await WaitUntilAsync(() => api.PutRequests.OfType<SyncQuizAnswersRequest>()
            .Any(request => request.Answers.Any(answer => answer.Revision == 2)));

        var clear = api.PutRequests.OfType<SyncQuizAnswersRequest>()
            .SelectMany(request => request.Answers)
            .Single(answer => answer.Revision == 2);
        Assert.Empty(clear.ChoiceIds);
        Assert.Equal(0, viewModel.AnsweredCount);
    }

    private static StudentQuizViewModel CreateViewModel(
        Fixture fixture,
        QuizAttemptDto attempt,
        RecordingBackendClient api,
        IQuizLocalStore localStore) =>
        new(
            api,
            fixture.State,
            fixture.Clock,
            new FakeCountdownTicker(),
            new FakeStudentRealtimeService(),
            new FixedStudentExamFlowCoordinator(fixture.State, attempt),
            localStore);

    private static Fixture CreateFixture()
    {
        var now = new DateTimeOffset(2026, 8, 7, 8, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var question = new QuizQuestionDto(
            Guid.NewGuid(),
            "Question",
            1,
            10,
            false,
            [
                new QuizChoiceDto(Guid.NewGuid(), "A", 1),
                new QuizChoiceDto(Guid.NewGuid(), "B", 2)
            ]);
        var state = new StudentSessionState
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            AccessMode = SessionAccessMode.LanOnly,
            AccessToken = "test"
        };
        var clock = new ServerClock(new FakeMonotonicTimeSource());
        clock.Synchronize(now);
        var attempt = new QuizAttemptDto(
            Guid.NewGuid(), sessionId, participantId, QuizAttemptStatus.InProgress, 1,
            now.AddMinutes(-1), now.AddMinutes(30), null, null, 10, [question], []);
        return new(state, clock, attempt, question, now);
    }

    private static QuizAnswerDto Answer(
        QuizQuestionDto question,
        int choiceIndex,
        long revision,
        DateTimeOffset updatedAt) =>
        new(question.Id, [question.Choices[choiceIndex].Id], revision, updatedAt);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "The expected asynchronous condition was not reached.");
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        return new string(normalized.Where(character =>
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
            != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    private sealed record Fixture(
        StudentSessionState State,
        ServerClock Clock,
        QuizAttemptDto Attempt,
        QuizQuestionDto Question,
        DateTimeOffset Now);

    private sealed class ControlledLocalStore(IReadOnlyList<QuizAnswerDto> answers) : IQuizLocalStore
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => release.TrySetResult();

        public async Task<IReadOnlyList<QuizAnswerDto>> LoadAsync(Guid attemptId, CancellationToken ct)
        {
            await release.Task.WaitAsync(ct);
            return answers;
        }

        public Task SaveAsync(Guid attemptId, IEnumerable<QuizAnswerDto> saved, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
