using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using System.Net;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentTimelineViewModelTests
{
    [Fact]
    public void StudentExamAppliesOnlyNewAbsoluteDeadlineForCurrentParticipant()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var state = new StudentSessionState
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            AccessMode = SessionAccessMode.PublicCloud,
            AccessToken = "test"
        };
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var ticker = new FakeCountdownTicker();
        using var realtime = new FakeStudentRealtimeService();
        using var heartbeat = new FakeStudentHeartbeatService();
        using var viewModel = new StudentExamViewModel(
            new RecordingBackendClient(DateTimeOffset.UtcNow),
            state,
            heartbeat,
            realtime,
            clock,
            ticker);
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);
        var deadline = now.AddMinutes(70);

        Assert.True(viewModel.TryApplyTimeExtended(Notification(
            sessionId,
            participantId,
            null,
            20,
            now,
            deadline)));
        Assert.Equal("01:10:00", viewModel.TimeLeft);
        Assert.False(viewModel.TryApplyTimeExtended(Notification(
            sessionId,
            participantId,
            null,
            19,
            now.AddSeconds(1),
            now.AddMinutes(60))));
        Assert.Equal("01:10:00", viewModel.TimeLeft);
        Assert.False(viewModel.TryApplyTimeExtended(Notification(
            Guid.NewGuid(),
            participantId,
            null,
            21,
            now.AddSeconds(2),
            now.AddMinutes(80))));
    }

    [Fact]
    public async Task StudentQuizAppliesAbsoluteDeadlineWithoutSecondTimerOrNetworkRefresh()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);
        var state = new StudentSessionState
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            AccessMode = SessionAccessMode.LanOnly,
            AccessToken = "test"
        };
        var attempt = new QuizAttemptDto(
            attemptId,
            sessionId,
            participantId,
            QuizAttemptStatus.InProgress,
            1,
            now.AddMinutes(-10),
            now.AddMinutes(60),
            null,
            null,
            10,
            [Question()],
            []);
        var api = new RecordingBackendClient(now) { QuizAttemptResponse = attempt };
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        clock.Synchronize(now);
        var ticker = new FakeCountdownTicker();
        using var realtime = new FakeStudentRealtimeService();
        using var viewModel = new StudentQuizViewModel(
            api,
            state,
            clock,
            ticker,
            realtime,
            new FixedStudentExamFlowCoordinator(state, attempt));
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.TryApplyTimeExtended(Notification(
            sessionId,
            participantId,
            attemptId,
            30,
            now,
            now.AddMinutes(75))));
        Assert.Equal(now.AddMinutes(75), viewModel.Attempt!.DeadlineUtc);
        Assert.Equal("01:15:00", viewModel.TimeLeft);
        Assert.True(ticker.IsRunning);

        Assert.False(viewModel.TryApplyTimeExtended(Notification(
            sessionId,
            participantId,
            attemptId,
            29,
            now.AddSeconds(1),
            now.AddMinutes(65))));
        Assert.Equal(now.AddMinutes(75), viewModel.Attempt.DeadlineUtc);
        Assert.True(ticker.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StudentQuizRejectsMissingQuestionsWithoutStartingTickerOrReadyState(bool nullQuestions)
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attempt = new QuizAttemptDto(
            Guid.NewGuid(),
            sessionId,
            participantId,
            QuizAttemptStatus.InProgress,
            1,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(29),
            null,
            null,
            10,
            nullQuestions ? null! : [],
            []);
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
            new FixedStudentExamFlowCoordinator(state, attempt));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Null(viewModel.Attempt);
        Assert.Empty(viewModel.Questions);
        Assert.False(ticker.IsRunning);
        Assert.Equal("danger", viewModel.StatusTone);
        Assert.Contains("Không thể tải nội dung bài trắc nghiệm", viewModel.Status);
        Assert.Contains(ErrorCodes.QuizAttemptSnapshotInvalid, viewModel.Status);
        Assert.DoesNotContain("đã sẵn sàng", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ErrorCodes.QuizAttemptSnapshotInvalid, "Dữ liệu đề thi chưa hợp lệ")]
    [InlineData(ErrorCodes.QuizHasNoQuestions, "Đề trắc nghiệm chưa có câu hỏi")]
    public async Task StudentQuizMapsIntegrityCodesToBusinessErrors(
        string errorCode,
        string expectedMessage)
    {
        var state = new StudentSessionState
        {
            SessionId = Guid.NewGuid(),
            ParticipantId = Guid.NewGuid(),
            AccessMode = SessionAccessMode.PublicCloud,
            AccessToken = "test"
        };
        var ticker = new FakeCountdownTicker();
        using var viewModel = new StudentQuizViewModel(
            new RecordingBackendClient(DateTimeOffset.UtcNow),
            state,
            new ServerClock(new FakeMonotonicTimeSource()),
            ticker,
            new FakeStudentRealtimeService(),
            new ThrowingStudentExamFlowCoordinator(new PublicCloudApiException(
                errorCode,
                errorCode,
                HttpStatusCode.Conflict)));

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(ticker.IsRunning);
        Assert.Equal("danger", viewModel.StatusTone);
        Assert.Contains(expectedMessage, viewModel.Status);
        Assert.Contains(errorCode, viewModel.Status);
        Assert.DoesNotContain("mạng", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static QuizQuestionDto Question() =>
        new(
            Guid.NewGuid(),
            "Question",
            1,
            10,
            false,
            [
                new QuizChoiceDto(Guid.NewGuid(), "A", 1),
                new QuizChoiceDto(Guid.NewGuid(), "B", 2)
            ]);

    private static StudentRealtimeNotification Notification(
        Guid sessionId,
        Guid participantId,
        Guid? attemptId,
        long revision,
        DateTimeOffset serverNowUtc,
        DateTimeOffset deadlineUtc) =>
        new(
            sessionId,
            RealtimeEvents.TimeExtended,
            revision,
            new TimeExtendedEvent(
                participantId,
                0,
                deadlineUtc,
                attemptId,
                serverNowUtc,
                revision,
                Guid.NewGuid()));
}

internal sealed class FixedStudentExamFlowCoordinator(
    StudentSessionState state,
    QuizAttemptDto attempt) : IStudentExamFlowCoordinator
{
    public event EventHandler<StudentExamNavigationRequest>? NavigationRequested
    {
        add { }
        remove { }
    }

    public Task<StudentExamFlowResolution> ResolveAsync(
        StudentExamEntryPoint entryPoint,
        bool startConfirmed,
        CancellationToken cancellationToken)
    {
        state.CurrentAttempt = attempt;
        return Task.FromResult(new StudentExamFlowResolution(
            StudentExamFlowState.InProgressQuiz,
            "S-06",
            false,
            "resume"));
    }

    public Task<StudentJoinOutcome> SynchronizeAfterJoinAsync(
        IStudentRealtimeService realtime,
        CancellationToken cancellationToken) =>
        Task.FromResult(new StudentJoinOutcome(
            StudentJoinErrorCodes.Succeeded,
            StudentJoinPhase.Completed,
            true));

    public void ReturnToCurrentExam() { }
}

internal sealed class ThrowingStudentExamFlowCoordinator(Exception exception)
    : IStudentExamFlowCoordinator
{
    public event EventHandler<StudentExamNavigationRequest>? NavigationRequested
    {
        add { }
        remove { }
    }

    public Task<StudentExamFlowResolution> ResolveAsync(
        StudentExamEntryPoint entryPoint,
        bool startConfirmed,
        CancellationToken cancellationToken) =>
        Task.FromException<StudentExamFlowResolution>(exception);

    public Task<StudentJoinOutcome> SynchronizeAfterJoinAsync(
        IStudentRealtimeService realtime,
        CancellationToken cancellationToken) =>
        Task.FromResult(new StudentJoinOutcome(
            StudentJoinErrorCodes.Succeeded,
            StudentJoinPhase.Completed,
            true));

    public void ReturnToCurrentExam() { }
}

internal sealed class FakeStudentRealtimeService : IStudentRealtimeService
{
    public bool IsConnected => true;
    public event EventHandler<string>? EventReceived { add { } remove { } }
    public event EventHandler<StudentRealtimeNotification>? NotificationReceived { add { } remove { } }
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Dispose() { }
}

internal sealed class FakeStudentHeartbeatService : IStudentHeartbeatService
{
    public StudentConnectionState State => StudentConnectionState.Online;
    public event EventHandler<StudentConnectionState>? StateChanged { add { } remove { } }
    public void Start() { }
    public void Stop() { }
    public Task<bool> ProbeNowAsync(CancellationToken ct = default) => Task.FromResult(true);
    public void Dispose() { }
}
