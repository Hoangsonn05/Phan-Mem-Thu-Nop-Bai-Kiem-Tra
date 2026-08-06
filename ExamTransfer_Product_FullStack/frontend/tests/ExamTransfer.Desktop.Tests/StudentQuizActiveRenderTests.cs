using System.IO;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace ExamTransfer.Desktop.Tests;

/// <summary>
/// Tests for ET-QUIZ-ACTIVE-RENDER-LOCAL-STORE-R1:
/// Verifies that blocking or failing local store does not prevent quiz
/// questions from rendering when the student has an active attempt.
/// </summary>
public sealed class StudentQuizActiveRenderTests
{
    private readonly ITestOutputHelper output;

    public StudentQuizActiveRenderTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static (StudentSessionState state, ServerClock clock) MakeLanState(
        DateTimeOffset now,
        Guid sessionId,
        Guid participantId)
    {
        var state = new StudentSessionState
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            AccessMode = SessionAccessMode.LanOnly,
            AccessToken = "test"
        };
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        clock.Synchronize(now);
        return (state, clock);
    }

    private static QuizAttemptDto MakeAttempt(
        Guid attemptId,
        Guid sessionId,
        Guid participantId,
        DateTimeOffset now,
        int questionCount = 5,
        QuizAttemptStatus status = QuizAttemptStatus.InProgress,
        IReadOnlyList<QuizAnswerDto>? answers = null)
    {
        var questions = Enumerable.Range(1, questionCount)
            .Select(i => new QuizQuestionDto(
                Guid.NewGuid(),
                $"Câu {i}",
                i,
                2,
                false,
                [
                    new QuizChoiceDto(Guid.NewGuid(), "A", 1),
                    new QuizChoiceDto(Guid.NewGuid(), "B", 2)
                ]))
            .ToList();

        return new QuizAttemptDto(
            attemptId,
            sessionId,
            participantId,
            status,
            1,
            now.AddMinutes(-10),
            now.AddMinutes(60),
            null,
            null,
            10 * questionCount,
            questions,
            answers ?? []);
    }

    // ────────────────────────────────────────────────────────────────────────
    // A. Blocked local store: questions must appear before store completes
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BEFORE patch: Questions.Count stays 0 while local store is blocked.
    /// AFTER patch:  Questions reaches 5 and ProgressText is correct before
    ///               the local store TaskCompletionSource is resolved.
    /// </summary>
    [Fact]
    public async Task A_BlockedLocalStore_QuestionsRenderBeforeStoreCompletes()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);

        var (state, clock) = MakeLanState(now, sessionId, participantId);
        var attempt = MakeAttempt(attemptId, sessionId, participantId, now, questionCount: 5);

        // Fake store that blocks indefinitely on LoadAsync
        var blockedStore = new BlockingLocalStore();

        // SyncAsync will call PutAsync → SyncQuizAnswersResultDto.
        // With null response, ApiGuard.Require throws → caught silently in SyncAsync.
        // That is fine for this test.
        var api = new RecordingBackendClient(now)
        {
            SyncQuizAnswersResultResponse = new SyncQuizAnswersResultDto(attemptId, [], now)
        };

        using var viewModel = new StudentQuizViewModel(
            api,
            state,
            clock,
            new FakeCountdownTicker(),
            new FakeStudentRealtimeService(),
            new FixedStudentExamFlowCoordinator(state, attempt),
            blockedStore);

        // Start LoadAsync but do NOT await it yet
        var loadTask = viewModel.InitializeAsync(CancellationToken.None);

        // Yield briefly so the synchronous portion of LoadAsync can run up to
        // the point where it fires HydrateLocalAnswersAsync and proceeds.
        // We wait for Questions to reach 5 without the store completing.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (viewModel.Questions.Count < 5 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        output.WriteLine($"Questions.Count before store unblocks: {viewModel.Questions.Count}");
        output.WriteLine($"ProgressText before store unblocks: {viewModel.ProgressText}");
        output.WriteLine($"IsActiveAttemptVisible: {viewModel.IsActiveAttemptVisible}");

        // ── Core assertions (must pass before store completes) ──────────────
        Assert.Equal(5, viewModel.Questions.Count);
        Assert.Equal("Đã trả lời 0/5 câu", viewModel.ProgressText);
        Assert.True(viewModel.IsActiveAttemptVisible);
        Assert.NotNull(viewModel.Attempt);
        Assert.Equal(QuizAttemptStatus.InProgress, viewModel.Attempt!.Status);

        // Now let the store complete so the task can finish cleanly
        blockedStore.Unblock();
        await loadTask;

        // Questions still intact after store completes
        Assert.Equal(5, viewModel.Questions.Count);
        Assert.Equal("Đã trả lời 0/5 câu", viewModel.ProgressText);
    }

    [Fact]
    public async Task A_BlockedLocalStore_TickerStartsBeforeStoreCompletes()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);

        var (state, clock) = MakeLanState(now, sessionId, participantId);
        var attempt = MakeAttempt(attemptId, sessionId, participantId, now, questionCount: 5);
        var blockedStore = new BlockingLocalStore();
        var api = new RecordingBackendClient(now)
        {
            SyncQuizAnswersResultResponse = new SyncQuizAnswersResultDto(attemptId, [], now)
        };
        var ticker = new FakeCountdownTicker();

        using var viewModel = new StudentQuizViewModel(
            api, state, clock, ticker,
            new FakeStudentRealtimeService(),
            new FixedStudentExamFlowCoordinator(state, attempt),
            blockedStore);

        var loadTask = viewModel.InitializeAsync(CancellationToken.None);

        // Wait for ticker to start
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!ticker.IsRunning && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(ticker.IsRunning, "Ticker should start before local store completes");

        blockedStore.Unblock();
        await loadTask;
    }

    // ────────────────────────────────────────────────────────────────────────
    // B. Local store throws IOException / JsonException
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("IOException")]
    [InlineData("JsonException")]
    public async Task B_LocalStoreThrows_FiveQuestionsStillVisible_NoFinalize(string exceptionType)
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);

        var (state, clock) = MakeLanState(now, sessionId, participantId);
        var attempt = MakeAttempt(attemptId, sessionId, participantId, now, questionCount: 5);

        Exception toThrow = exceptionType == "IOException"
            ? new IOException("disk read error")
            : new System.Text.Json.JsonException("malformed json");

        var failingStore = new ThrowingLocalStore(toThrow);
        var api = new RecordingBackendClient(now)
        {
            SyncQuizAnswersResultResponse = new SyncQuizAnswersResultDto(attemptId, [], now)
        };

        using var viewModel = new StudentQuizViewModel(
            api, state, clock,
            new FakeCountdownTicker(),
            new FakeStudentRealtimeService(),
            new FixedStudentExamFlowCoordinator(state, attempt),
            failingStore);

        await viewModel.InitializeAsync(CancellationToken.None);

        // Give background hydration task a chance to run and fail
        await Task.Delay(100);

        output.WriteLine($"Questions.Count: {viewModel.Questions.Count}");
        output.WriteLine($"Attempt.Status: {viewModel.Attempt?.Status}");
        output.WriteLine($"StatusTone: {viewModel.StatusTone}");

        // 5 questions must be visible
        Assert.Equal(5, viewModel.Questions.Count);
        // Attempt still InProgress — no self-finalize
        Assert.NotNull(viewModel.Attempt);
        Assert.Equal(QuizAttemptStatus.InProgress, viewModel.Attempt!.Status);
        // No danger tone triggered by local store error alone
        Assert.NotEqual("danger", viewModel.StatusTone);
        // Store logged the warning — verify it was called
        Assert.True(failingStore.LoadWasCalled, "Local store LoadAsync should have been called");
    }

    // ────────────────────────────────────────────────────────────────────────
    // C. Local answers with higher revision are merged after hydration
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task C_LocalAnswersHigherRevision_MergedAfterHydration()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);

        var (state, clock) = MakeLanState(now, sessionId, participantId);

        // Create a 5-question attempt where server already has rev=1 answers
        // (but the local store has rev=2 answers for q1 and q2)
        var questions = Enumerable.Range(1, 5)
            .Select(i => new QuizQuestionDto(
                Guid.NewGuid(), $"Câu {i}", i, 2, false,
                [new QuizChoiceDto(Guid.NewGuid(), "A", 1), new QuizChoiceDto(Guid.NewGuid(), "B", 2)]))
            .ToList();

        // Server has revision=1 answer for Q0 selecting choice A
        var serverAnswers = new List<QuizAnswerDto>
        {
            new(questions[0].Id, [questions[0].Choices[0].Id], 1, now.AddMinutes(-5))
        };

        // Local store has revision=2 answer for Q0 selecting choice B,
        // and revision=1 answer for Q1 selecting choice A
        var localAnswerQ0 = new QuizAnswerDto(questions[0].Id, [questions[0].Choices[1].Id], 2, now.AddMinutes(-2));
        var localAnswerQ1 = new QuizAnswerDto(questions[1].Id, [questions[1].Choices[0].Id], 1, now.AddMinutes(-3));

        var attempt = new QuizAttemptDto(
            attemptId, sessionId, participantId,
            QuizAttemptStatus.InProgress, 1,
            now.AddMinutes(-10), now.AddMinutes(60),
            null, null, 50, questions, serverAnswers);

        var localStore = new FakeLocalStore([localAnswerQ0, localAnswerQ1]);
        var api = new RecordingBackendClient(now)
        {
            SyncQuizAnswersResultResponse = new SyncQuizAnswersResultDto(attemptId, serverAnswers, now)
        };

        using var viewModel = new StudentQuizViewModel(
            api, state, clock,
            new FakeCountdownTicker(),
            new FakeStudentRealtimeService(),
            new FixedStudentExamFlowCoordinator(state, attempt),
            localStore);

        await viewModel.InitializeAsync(CancellationToken.None);

        // Wait for hydration to propagate to the UI
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (viewModel.AnsweredCount < 2 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        output.WriteLine($"AnsweredCount after hydration: {viewModel.AnsweredCount}");
        output.WriteLine($"ProgressText: {viewModel.ProgressText}");

        // Q0: local rev=2 > server rev=1 → choice B selected
        var q0Row = viewModel.Questions.First(q => q.Id == questions[0].Id);
        Assert.False(q0Row.Choices[0].IsSelected, "Choice A should NOT be selected for Q0 (local overrides)");
        Assert.True(q0Row.Choices[1].IsSelected, "Choice B should be selected for Q0 (local rev=2 wins)");

        // Q1: local rev=1, server has no answer → choice A selected
        var q1Row = viewModel.Questions.First(q => q.Id == questions[1].Id);
        Assert.True(q1Row.Choices[0].IsSelected, "Choice A should be selected for Q1 (local answer merged)");

        // Q2–Q4: no answers → both unselected
        foreach (var q in viewModel.Questions.Skip(2))
        {
            Assert.False(q.Choices.Any(c => c.IsSelected), $"No choices should be selected for {q.Text}");
        }

        // ProgressText reflects merged state: 2 answered out of 5
        Assert.Equal(2, viewModel.AnsweredCount);
        Assert.Equal("Đã trả lời 2/5 câu", viewModel.ProgressText);
    }

    // ────────────────────────────────────────────────────────────────────────
    // D. Finalized review: Review.Questions visible; active Questions cleared
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task D_FinalizedAttempt_ReviewVisible_ActiveQuestionsCleared_NoAnswersExposed()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);

        var (state, clock) = MakeLanState(now, sessionId, participantId);
        var attempt = MakeAttempt(attemptId, sessionId, participantId, now,
            questionCount: 3,
            status: QuizAttemptStatus.Finalized);

        // Build review DTO — score NOT visible (policy: hidden), correct answers NOT visible
        var reviewQuestions = attempt.Questions
            .Select(q => new QuizQuestionReviewDto(
                q.Id, q.Text, q.Order, q.Points,
                EarnedPoints: null,
                q.Choices.Select(c => new QuizChoiceReviewDto(
                    c.Id, c.Text, c.Order,
                    Selected: false,
                    Correct: null)).ToList()))
            .ToList();
        var review = new StudentQuizReviewDto(
            attemptId,
            Score: null,
            MaxScore: 30,
            ScoreVisible: false,
            CorrectAnswersVisible: false,
            GeneralComment: null,
            Questions: reviewQuestions);

        // Wire api to return review from GET
        var api = new RecordingBackendClient(now)
        {
            StudentQuizReviewResponse = review
        };

        var fakeStore = new FakeLocalStore([]);

        using var viewModel = new StudentQuizViewModel(
            api, state, clock,
            new FakeCountdownTicker(),
            new FakeStudentRealtimeService(),
            new FixedStudentExamFlowCoordinator(state, attempt),
            fakeStore);

        await viewModel.InitializeAsync(CancellationToken.None);

        output.WriteLine($"Review: {viewModel.Review}");
        output.WriteLine($"Questions.Count: {viewModel.Questions.Count}");
        output.WriteLine($"IsReviewVisible: {viewModel.IsReviewVisible}");
        output.WriteLine($"IsActiveAttemptVisible: {viewModel.IsActiveAttemptVisible}");

        // Active Questions must be cleared (review mode)
        Assert.Empty(viewModel.Questions);
        // Review must be populated
        Assert.NotNull(viewModel.Review);
        Assert.Equal(3, viewModel.Review!.Questions.Count);
        // Review is visible, active attempt pane is NOT
        Assert.True(viewModel.IsReviewVisible);
        Assert.False(viewModel.IsActiveAttemptVisible);
        // No correct answers exposed (correctAnswersVisible = false)
        Assert.All(viewModel.Review.Questions, q =>
            Assert.All(q.Choices, c => Assert.Null(c.Correct)));
    }

    // ────────────────────────────────────────────────────────────────────────
    // E. Regression: existing tests unchanged
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E_Regression_ExistingProgressTextAndCountsUnchanged()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);

        var (state, clock) = MakeLanState(now, sessionId, participantId);
        var attempt = MakeAttempt(attemptId, sessionId, participantId, now, questionCount: 50);
        var api = new RecordingBackendClient(now)
        {
            SyncQuizAnswersResultResponse = new SyncQuizAnswersResultDto(attemptId, [], now)
        };

        using var viewModel = new StudentQuizViewModel(
            api, state, clock,
            new FakeCountdownTicker(),
            new FakeStudentRealtimeService(),
            new FixedStudentExamFlowCoordinator(state, attempt),
            new FakeLocalStore([]));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.NotNull(viewModel.Attempt);
        Assert.Equal(50, viewModel.Questions.Count);
        Assert.Equal("Đã trả lời 0/50 câu", viewModel.ProgressText);
        Assert.Equal(50, viewModel.UnansweredCount);
        Assert.True(viewModel.CanEditAnswers);
        Assert.Null(viewModel.Review);
        output.WriteLine($"ProgressText: {viewModel.ProgressText}");
    }

    [Fact]
    public async Task E_Regression_ServerAnswersAppliedBeforeLocalHydration()
    {
        // Verifies that server answers in the attempt snapshot are applied
        // immediately (not lost when local store is empty).
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);

        var (state, clock) = MakeLanState(now, sessionId, participantId);

        var questions = Enumerable.Range(1, 3)
            .Select(i => new QuizQuestionDto(
                Guid.NewGuid(), $"Q{i}", i, 2, false,
                [new QuizChoiceDto(Guid.NewGuid(), "A", 1), new QuizChoiceDto(Guid.NewGuid(), "B", 2)]))
            .ToList();

        // Server has answer for Q0 (choice A, rev=1)
        var serverAnswer = new QuizAnswerDto(questions[0].Id, [questions[0].Choices[0].Id], 1, now.AddMinutes(-5));
        var attempt = new QuizAttemptDto(
            attemptId, sessionId, participantId,
            QuizAttemptStatus.InProgress, 1,
            now.AddMinutes(-10), now.AddMinutes(60),
            null, null, 30, questions, [serverAnswer]);

        var api = new RecordingBackendClient(now)
        {
            SyncQuizAnswersResultResponse = new SyncQuizAnswersResultDto(attemptId, [serverAnswer], now)
        };

        // Local store is instant and empty
        using var viewModel = new StudentQuizViewModel(
            api, state, clock,
            new FakeCountdownTicker(),
            new FakeStudentRealtimeService(),
            new FixedStudentExamFlowCoordinator(state, attempt),
            new FakeLocalStore([]));

        await viewModel.InitializeAsync(CancellationToken.None);

        var q0Row = viewModel.Questions.First(q => q.Id == questions[0].Id);
        // Server answer should be applied
        Assert.True(q0Row.Choices[0].IsSelected, "Server answer (choice A) should be selected for Q0");
        Assert.Equal(1, viewModel.AnsweredCount);
        Assert.Equal("Đã trả lời 1/3 câu", viewModel.ProgressText);
    }

    [Fact]
    public async Task E_Regression_NoQuestionsSnapshot_RejectsWithDangerAndNoTicker()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attempt = new QuizAttemptDto(
            Guid.NewGuid(), sessionId, participantId,
            QuizAttemptStatus.InProgress, 1,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(29),
            null, null, 10,
            Questions: [],
            Answers: []);
        var state = new StudentSessionState
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            AccessMode = SessionAccessMode.LanOnly,
            AccessToken = "test"
        };
        var ticker = new FakeCountdownTicker();

        using var viewModel = new StudentQuizViewModel(
            new RecordingBackendClient(DateTimeOffset.UtcNow),
            state,
            new ServerClock(new FakeMonotonicTimeSource()),
            ticker,
            new FakeStudentRealtimeService(),
            new FixedStudentExamFlowCoordinator(state, attempt),
            new FakeLocalStore([]));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Null(viewModel.Attempt);
        Assert.Empty(viewModel.Questions);
        Assert.False(ticker.IsRunning);
        Assert.Equal("danger", viewModel.StatusTone);
        Assert.Contains(ErrorCodes.QuizAttemptSnapshotInvalid, viewModel.Status);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────

/// <summary>Blocks on LoadAsync until <see cref="Unblock"/> is called.</summary>
internal sealed class BlockingLocalStore : IQuizLocalStore
{
    private readonly TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Unblock() => tcs.TrySetResult(true);

    public async Task<IReadOnlyList<QuizAnswerDto>> LoadAsync(Guid attemptId, CancellationToken ct)
    {
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
        return [];
    }

    public Task SaveAsync(Guid attemptId, IEnumerable<QuizAnswerDto> answers, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>Always throws on LoadAsync; records whether it was called.</summary>
internal sealed class ThrowingLocalStore(Exception exception) : IQuizLocalStore
{
    public bool LoadWasCalled { get; private set; }

    public Task<IReadOnlyList<QuizAnswerDto>> LoadAsync(Guid attemptId, CancellationToken ct)
    {
        LoadWasCalled = true;
        return Task.FromException<IReadOnlyList<QuizAnswerDto>>(exception);
    }

    public Task SaveAsync(Guid attemptId, IEnumerable<QuizAnswerDto> answers, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>Returns a fixed list from LoadAsync immediately.</summary>
internal sealed class FakeLocalStore(IReadOnlyList<QuizAnswerDto> stored) : IQuizLocalStore
{
    public Task<IReadOnlyList<QuizAnswerDto>> LoadAsync(Guid attemptId, CancellationToken ct) =>
        Task.FromResult(stored);

    public Task SaveAsync(Guid attemptId, IEnumerable<QuizAnswerDto> answers, CancellationToken ct) =>
        Task.CompletedTask;
}
