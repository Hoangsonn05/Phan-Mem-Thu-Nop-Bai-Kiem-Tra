using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentExamFlowCoordinatorTests
{
    public static TheoryData<StudentExamFlowSnapshot, StudentExamFlowState, string, bool> Routes => new()
    {
        { Snapshot(false), StudentExamFlowState.NoSession, "S-01", false },
        { Snapshot(participant: ParticipantStatus.PendingApproval), StudentExamFlowState.PendingApproval, "S-03", false },
        { Snapshot(participant: ParticipantStatus.Rejected), StudentExamFlowState.RejectedOrExpired, "S-01", false },
        { Snapshot(status: SessionStatus.Waiting), StudentExamFlowState.ApprovedWaiting, "S-03", false },
        { Snapshot(status: SessionStatus.Distributing, delivery: ExamDeliveryType.FileSubmission), StudentExamFlowState.ReadyToStartFileExam, "S-05", false },
        { Snapshot(status: SessionStatus.Distributing, delivery: ExamDeliveryType.MultipleChoice), StudentExamFlowState.ApprovedWaiting, "S-03", false },
        { Snapshot(delivery: ExamDeliveryType.FileSubmission), StudentExamFlowState.ReadyToStartFileExam, "S-05", false },
        { Snapshot(delivery: ExamDeliveryType.FileSubmission, submission: SubmissionStatus.Uploading), StudentExamFlowState.InProgressFileExam, "S-07", false },
        { Snapshot(delivery: ExamDeliveryType.FileSubmission, submission: SubmissionStatus.Submitted), StudentExamFlowState.SubmittedFileExam, "S-08", false },
        { Snapshot(delivery: ExamDeliveryType.MultipleChoice), StudentExamFlowState.ReadyToStartQuiz, "S-06", true },
        { Snapshot(delivery: ExamDeliveryType.MultipleChoice, attempt: QuizAttemptStatus.InProgress), StudentExamFlowState.InProgressQuiz, "S-06", false },
        { Snapshot(delivery: ExamDeliveryType.MultipleChoice, attempt: QuizAttemptStatus.Finalized), StudentExamFlowState.FinalizedQuiz, "S-06", false },
        { Snapshot(status: SessionStatus.Collecting, delivery: ExamDeliveryType.FileSubmission), StudentExamFlowState.ReadyToStartFileExam, "S-05", false },
        { Snapshot(status: SessionStatus.Collecting, delivery: ExamDeliveryType.MultipleChoice, attempt: QuizAttemptStatus.InProgress), StudentExamFlowState.InProgressQuiz, "S-06", false },
        { Snapshot(status: SessionStatus.Collecting, delivery: ExamDeliveryType.MultipleChoice), StudentExamFlowState.CollectingSummary, "S-04", false },
        { Snapshot(status: SessionStatus.Finished), StudentExamFlowState.SessionFinished, "S-04", false }
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void ResolveSnapshot_UsesOneStateMachineForEveryEntryPoint(
        StudentExamFlowSnapshot snapshot,
        StudentExamFlowState expectedState,
        string expectedRoute,
        bool expectedConfirmation)
    {
        var currentExam = StudentExamFlowCoordinator.ResolveSnapshot(snapshot);
        var quizTab = StudentExamFlowCoordinator.ResolveSnapshot(snapshot);

        Assert.Equal(expectedState, currentExam.State);
        Assert.Equal(expectedRoute, currentExam.RouteKey);
        Assert.Equal(expectedConfirmation, currentExam.RequiresStartConfirmation);
        Assert.Equal(currentExam, quizTab);
    }

    [Fact]
    public async Task ResolveAsync_DeduplicatesNavigationAndNotifications_AndRejectsStaleRevival()
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var examId = Guid.NewGuid();
        var state = new StudentSessionState
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            AccessToken = "participant-token",
            AccessMode = SessionAccessMode.LanOnly,
            SessionStatus = SessionStatus.Waiting,
            ParticipantStatus = ParticipantStatus.PendingApproval,
            DeliveryType = ExamDeliveryType.FileSubmission,
            Revision = 1
        };
        var api = new RecordingBackendClient(now);
        var toasts = new RecordingToastService();
        var coordinator = new StudentExamFlowCoordinator(
            api,
            new SupabasePublicCloudClient(),
            state,
            toasts);
        var navigationCount = 0;
        coordinator.NavigationRequested += (_, _) => navigationCount++;

        SetSnapshot(api, sessionId, participantId, examId, SessionStatus.InProgress, 2, now);
        _ = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);
        _ = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);

        Assert.Equal(1, navigationCount);
        Assert.Single(toasts.Messages, x => x.Contains("duyệt", StringComparison.Ordinal));
        Assert.Single(toasts.Messages, x => x.Contains("bắt đầu", StringComparison.Ordinal));

        SetSnapshot(api, sessionId, participantId, examId, SessionStatus.Collecting, 3, now);
        _ = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);
        _ = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);

        Assert.Equal(2, navigationCount);
        Assert.Single(toasts.Messages, x => x.Contains("thu bài", StringComparison.Ordinal));

        SetSnapshot(api, sessionId, participantId, examId, SessionStatus.Finished, 4, now);
        _ = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);
        _ = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);
        Assert.Equal(3, navigationCount);
        Assert.Single(toasts.Messages, x => x.Contains("kết thúc", StringComparison.Ordinal));

        SetSnapshot(api, sessionId, participantId, examId, SessionStatus.InProgress, 3, now);
        var stale = await coordinator.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            default);

        Assert.Equal(StudentExamFlowState.SessionFinished, stale.State);
        Assert.Equal(3, navigationCount);
        Assert.Equal(SessionStatus.Finished, state.SessionStatus);
        Assert.Equal(4, state.Revision);
    }

    [Theory]
    [InlineData(SessionStatus.Cancelled, "hủy")]
    [InlineData(SessionStatus.Archived, "lưu trữ")]
    public async Task ResolveAsync_TerminalTransition_NotifiesAndNavigatesExactlyOnce(
        SessionStatus terminal,
        string messageFragment)
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var state = ActiveLanState(SessionStatus.InProgress, ParticipantStatus.Approved, 1);
        var api = new RecordingBackendClient(now);
        SetSnapshot(
            api,
            state.SessionId!.Value,
            state.ParticipantId!.Value,
            Guid.NewGuid(),
            terminal,
            2,
            now);
        var toasts = new RecordingToastService();
        var coordinator = new StudentExamFlowCoordinator(
            api,
            new SupabasePublicCloudClient(),
            state,
            toasts);
        var navigationCount = 0;
        coordinator.NavigationRequested += (_, _) => navigationCount++;

        var first = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);
        var repeated = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);

        Assert.Equal("S-04", first.RouteKey);
        Assert.Equal(first, repeated);
        Assert.Equal(1, navigationCount);
        Assert.Single(toasts.Messages, x => x.Contains(messageFragment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_Rejection_NotifiesAndNavigatesExactlyOnce()
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var state = ActiveLanState(SessionStatus.Waiting, ParticipantStatus.PendingApproval, 1);
        var api = new RecordingBackendClient(now);
        SetSnapshot(
            api,
            state.SessionId!.Value,
            state.ParticipantId!.Value,
            Guid.NewGuid(),
            SessionStatus.Waiting,
            2,
            now,
            ParticipantStatus.Rejected);
        var toasts = new RecordingToastService();
        var coordinator = new StudentExamFlowCoordinator(
            api,
            new SupabasePublicCloudClient(),
            state,
            toasts);
        var navigationCount = 0;
        coordinator.NavigationRequested += (_, _) => navigationCount++;

        _ = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);
        _ = await coordinator.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, default);

        Assert.Equal(1, navigationCount);
        Assert.Single(toasts.Messages, x => x.Contains("từ chối", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_DistributingFileExam_RefreshesSnapshotBeforeNavigation()
    {
        var now = new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.Zero);
        var state = ActiveLanState(SessionStatus.Waiting, ParticipantStatus.Approved, 1);
        var api = new RecordingBackendClient(now);
        SetSnapshot(
            api,
            state.SessionId!.Value,
            state.ParticipantId!.Value,
            Guid.NewGuid(),
            SessionStatus.Distributing,
            2,
            now);
        var coordinator = new StudentExamFlowCoordinator(
            api,
            new SupabasePublicCloudClient(),
            state,
            new RecordingToastService());
        StudentExamNavigationRequest? navigation = null;
        coordinator.NavigationRequested += (_, request) => navigation = request;

        var resolution = await coordinator.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            default);

        Assert.Equal(SessionStatus.Distributing, state.SessionStatus);
        Assert.Equal(2, state.Revision);
        Assert.Equal(StudentExamFlowState.ReadyToStartFileExam, resolution.State);
        Assert.Equal("S-05", resolution.RouteKey);
        Assert.Equal("S-05", navigation?.Resolution.RouteKey);
    }

    [Theory]
    [InlineData(true, StudentJoinPhase.RealtimeStartup, StudentJoinErrorCodes.PostJoinSynchronizationFailed)]
    [InlineData(false, StudentJoinPhase.LifecycleResolution, StudentJoinErrorCodes.LifecycleResolutionFailed)]
    public async Task SynchronizeAfterJoinAsync_RetainsCommittedIdentityAndReturnsTypedFailure(
        bool realtimeFails,
        StudentJoinPhase expectedPhase,
        string expectedCause)
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var state = new StudentSessionState();
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        state.ApplyJoin(
            sessionId,
            participantId,
            "participant-token",
            "ROOM42",
            "SV001",
            "Student",
            SessionAccessMode.LanOnly);
        var coordinator = new StudentExamFlowCoordinator(
            new RecordingBackendClient(now),
            new SupabasePublicCloudClient(),
            state,
            new RecordingToastService());

        var outcome = await coordinator.SynchronizeAfterJoinAsync(
            new ControllableRealtimeService(realtimeFails),
            default);

        Assert.Equal(StudentJoinErrorCodes.PostJoinSynchronizationFailed, outcome.Code);
        Assert.Equal(expectedPhase, outcome.Phase);
        Assert.Equal(expectedCause, outcome.CauseCode);
        Assert.True(outcome.AuthorityMutationCommitted);
        Assert.True(state.HasSession);
        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(participantId, state.ParticipantId);
        Assert.Equal("participant-token", state.AccessToken);
        Assert.True(state.PostJoinSynchronizationPending);
    }

    [Fact]
    public async Task SynchronizeAfterJoinAsync_PublicCloudNoAttemptBooleanTimeline_SucceedsAndKeepsPendingAuthority()
    {
        var now = new DateTimeOffset(2026, 8, 4, 8, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var examId = Guid.NewGuid();
        var state = new StudentSessionState();
        state.ApplyJoin(
            sessionId,
            participantId,
            "cloud-access-token",
            "ROOM42",
            "SV001",
            "Student",
            SessionAccessMode.PublicCloud);
        state.ParticipantStatus = ParticipantStatus.PendingApproval;
        state.SessionStatus = SessionStatus.Waiting;
        state.DeliveryType = ExamDeliveryType.FileSubmission;

        var timelineJson = JsonSerializer.Serialize(new
        {
            sessionId,
            participantId,
            participantStatus = "PendingApproval",
            submissionStatus = "NotStarted",
            sessionStatus = "Waiting",
            admissionMode = "OpenRequest",
            examId,
            examTitle = "Cloud exam",
            subject = "Tin",
            examVersion = 1,
            deliveryType = "FileSubmission",
            supervisionMode = "None",
            resultPolicy = "Hidden",
            startedAtUtc = (DateTimeOffset?)null,
            durationMinutes = 45,
            extraTimeMinutes = 0,
            effectiveDeadlineUtc = (DateTimeOffset?)null,
            attemptId = (Guid?)null,
            attemptStatus = (string?)null,
            attemptDeadlineUtc = (DateTimeOffset?)null,
            scoreVisible = false,
            score = (decimal?)null,
            maxScore = (decimal?)null,
            serverNowUtc = now,
            revision = 2,
            updatedAtUtc = now
        });
        using var handler = new TimelineResponseHandler(timelineJson);
        using var http = new HttpClient(handler);
        var publicCloud = new SupabasePublicCloudClient(
            http,
            new ServerClock(new FakeMonotonicTimeSource()),
            "https://project.supabase.co",
            "publishable-key");
        await publicCloud.LoginAsync("student", "password", CancellationToken.None);
        var coordinator = new StudentExamFlowCoordinator(
            new RecordingBackendClient(now),
            publicCloud,
            state,
            new RecordingToastService());

        var outcome = await coordinator.SynchronizeAfterJoinAsync(
            new ControllableRealtimeService(failStart: false),
            CancellationToken.None);

        Assert.Equal(StudentJoinErrorCodes.Succeeded, outcome.Code);
        Assert.Equal(StudentJoinPhase.Completed, outcome.Phase);
        Assert.Null(outcome.CauseCode);
        Assert.True(outcome.AuthorityMutationCommitted);
        Assert.True(state.JoinMutationCommitted);
        Assert.False(state.PostJoinSynchronizationPending);
        Assert.Equal(ParticipantStatus.PendingApproval, state.ParticipantStatus);
        Assert.Equal(SessionStatus.Waiting, state.SessionStatus);
        Assert.Equal(2, state.Revision);
        Assert.Equal(1, handler.TimelineCalls);
    }

    private static StudentExamFlowSnapshot Snapshot(
        bool hasSession = true,
        SessionStatus status = SessionStatus.InProgress,
        ParticipantStatus participant = ParticipantStatus.Approved,
        ExamDeliveryType delivery = ExamDeliveryType.FileSubmission,
        SubmissionStatus submission = SubmissionStatus.NotStarted,
        QuizAttemptStatus? attempt = null) =>
        new(hasSession, status, participant, delivery, submission, attempt);

    private static void SetSnapshot(
        RecordingBackendClient api,
        Guid sessionId,
        Guid participantId,
        Guid examId,
        SessionStatus status,
        long revision,
        DateTimeOffset now,
        ParticipantStatus participantStatus = ParticipantStatus.Approved)
    {
        var participant = new ParticipantDto(
            participantId,
            sessionId,
            "SV001",
            "Student",
            "device",
            "machine",
            null,
            "1.0.0",
            participantStatus,
            now,
            DownloadStatus.NotStarted,
            SubmissionStatus.NotStarted,
            0,
            now.AddMinutes(60),
            ConnectionState.Online);
        var summary = new SessionSummaryDto(
            sessionId,
            examId,
            "Exam",
            "ROOM42",
            status,
            now,
            now.AddMinutes(-10),
            IsTerminal(status) ? now : null,
            now.AddMinutes(60),
            new SessionCountsDto(1, 0, 1, 1, 0, 0, 0),
            revision,
            $"v{revision}",
            SessionAccessMode.LanOnly,
            false,
            ExamDeliveryType.FileSubmission);
        api.SessionDetailResponse = new(summary, [participant], "{}");
        api.ParticipantResponse = participant;
    }

    private static StudentSessionState ActiveLanState(
        SessionStatus sessionStatus,
        ParticipantStatus participantStatus,
        long revision) => new()
    {
        SessionId = Guid.NewGuid(),
        ParticipantId = Guid.NewGuid(),
        AccessToken = "participant-token",
        AccessMode = SessionAccessMode.LanOnly,
        SessionStatus = sessionStatus,
        ParticipantStatus = participantStatus,
        DeliveryType = ExamDeliveryType.FileSubmission,
        Revision = revision
    };

    private static bool IsTerminal(SessionStatus status) =>
        status is SessionStatus.Finished or SessionStatus.Cancelled or SessionStatus.Archived;

    private sealed class RecordingToastService : IToastService
    {
        public List<string> Messages { get; } = [];
        public void Show(string message, string tone = "info") => Messages.Add(message);
    }

    private sealed class ControllableRealtimeService(bool failStart) : IStudentRealtimeService
    {
        public bool IsConnected => !failStart;
        public event EventHandler<string>? EventReceived { add { } remove { } }
        public event EventHandler<StudentRealtimeNotification>? NotificationReceived
        {
            add { }
            remove { }
        }
        public Task StartAsync(CancellationToken ct = default) => failStart
            ? Task.FromException(new IOException("realtime unavailable"))
            : Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class TimelineResponseHandler(string timelineJson) : HttpMessageHandler
    {
        public int TimelineCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isLogin = request.RequestUri?.AbsolutePath.EndsWith(
                "/auth/v1/token",
                StringComparison.Ordinal) == true;
            if (!isLogin)
                TimelineCalls++;
            var content = isLogin
                ? """{"access_token":"access","refresh_token":"refresh","expires_in":3600}"""
                : timelineJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }
}
