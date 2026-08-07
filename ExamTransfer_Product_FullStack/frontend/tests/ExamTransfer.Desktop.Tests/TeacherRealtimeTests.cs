using System.IO;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

[CollectionDefinition("Teacher realtime", DisableParallelization = true)]
public sealed class TeacherRealtimeCollection;

[Collection("Teacher realtime")]
public sealed class TeacherRealtimeTests
{
    [Fact]
    public async Task SubscriptionStore_RestoresEveryDesiredSessionAfterReconnect()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var subscriptions = new RealtimeSessionSubscriptions();
        subscriptions.Add(first);
        subscriptions.Add(second);
        var invoked = new List<Guid>();

        await subscriptions.RestoreAsync(
            (sessionId, _) =>
            {
                invoked.Add(sessionId);
                return Task.CompletedTask;
            },
            default);
        await subscriptions.RestoreAsync(
            (sessionId, _) =>
            {
                invoked.Add(sessionId);
                return Task.CompletedTask;
            },
            default);

        Assert.Equal(2, invoked.Count(x => x == first));
        Assert.Equal(2, invoked.Count(x => x == second));

        subscriptions.Remove(first);
        invoked.Clear();
        await subscriptions.RestoreAsync(
            (sessionId, _) =>
            {
                invoked.Add(sessionId);
                return Task.CompletedTask;
            },
            default);
        Assert.Equal([second], invoked);
    }

    [Fact]
    public async Task SubscriptionStore_FailedInvokesRemainRetrySafe()
    {
        var sessionId = Guid.NewGuid();
        var subscriptions = new RealtimeSessionSubscriptions();
        var subscribeAttempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subscriptions.SubscribeAsync(
                sessionId,
                true,
                (_, _) =>
                {
                    subscribeAttempts++;
                    throw new InvalidOperationException("transient");
                },
                default));
        await subscriptions.SubscribeAsync(
            sessionId,
            true,
            (_, _) =>
            {
                subscribeAttempts++;
                return Task.CompletedTask;
            },
            default);

        Assert.Equal(2, subscribeAttempts);
        var restored = new List<Guid>();
        await subscriptions.RestoreAsync(
            (id, _) =>
            {
                restored.Add(id);
                return Task.CompletedTask;
            },
            default);
        Assert.Equal([sessionId], restored);

        var unsubscribeAttempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subscriptions.UnsubscribeAsync(
                sessionId,
                true,
                (_, _) =>
                {
                    unsubscribeAttempts++;
                    throw new InvalidOperationException("transient");
                },
                default));
        await subscriptions.UnsubscribeAsync(
            sessionId,
            true,
            (_, _) =>
            {
                unsubscribeAttempts++;
                return Task.CompletedTask;
            },
            default);

        Assert.Equal(2, unsubscribeAttempts);
        restored.Clear();
        await subscriptions.RestoreAsync(
            (id, _) =>
            {
                restored.Add(id);
                return Task.CompletedTask;
            },
            default);
        Assert.Empty(restored);
    }

    [Fact]
    public async Task SessionBinding_StopDisconnectsWhenUnsubscribeFails()
    {
        var sessionId = Guid.NewGuid();
        var realtime = new FakeTeacherRealtime
        {
            FailNextUnsubscribe = true
        };
        var binding = new TeacherRealtimeSessionBinding(realtime);
        await binding.SelectAsync(sessionId, default);

        await binding.StopAsync();

        Assert.False(realtime.IsConnected);
        Assert.Equal(1, realtime.DisconnectCount);
        Assert.Empty(realtime.Subscribed);
    }

    [Fact]
    public async Task Lobby_ParticipantJoinedRefreshesSelectedSessionOnlyAndDebounces()
    {
        var session = CreateSession(SessionStatus.Waiting);
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        var viewModel = new LobbyViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        await WaitForAsync(() => realtime.Subscribed.Contains(session.Id));
        Assert.Equal(1, backend.SessionDetailRequests);

        realtime.Raise(
            new(
                Guid.NewGuid(),
                RealtimeEvents.ParticipantJoined,
                1,
                null));
        await Task.Delay(250);
        Assert.Equal(1, backend.SessionDetailRequests);

        backend.Participants =
        [
            CreateParticipant(session.Id)
        ];
        for (var i = 0; i < 3; i++)
        {
            realtime.Raise(
                new(
                    session.Id,
                    RealtimeEvents.ParticipantJoined,
                    i + 2,
                    null));
        }

        await WaitForAsync(() => backend.SessionDetailRequests == 2);
        await Task.Delay(200);
        Assert.Equal(2, backend.SessionDetailRequests);
        Assert.Single(viewModel.Participants);

        viewModel.Dispose();
        await WaitForAsync(() => realtime.Unsubscribed.Contains(session.Id));
        Assert.False(realtime.IsConnected);
    }

    [Fact]
    public async Task LiveMonitor_FiltersOtherSessionsBeforeRefreshingSnapshot()
    {
        var session = CreateSession(SessionStatus.InProgress);
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        using var viewModel = new LiveMonitorViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        await WaitForAsync(() => realtime.Subscribed.Contains(session.Id));
        Assert.Equal(1, backend.SessionDetailRequests);

        realtime.Raise(
            new(
                Guid.NewGuid(),
                RealtimeEvents.ParticipantConnectionChanged,
                2,
                null));
        await Task.Delay(250);
        Assert.Equal(1, backend.SessionDetailRequests);

        realtime.Raise(
            new(
                session.Id,
                RealtimeEvents.ParticipantConnectionChanged,
                3,
                null));
        await WaitForAsync(() => backend.SessionDetailRequests == 2);
        Assert.Contains(
            viewModel.Events,
            item => item.Description == RealtimeEvents.ParticipantConnectionChanged);
    }

    [Fact]
    public async Task LiveMonitor_EarlyRealtimeRefreshStaysStaleUntilAPostCommitLocalPulseArrives()
    {
        var session = CreateSession(SessionStatus.InProgress);
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        using var viewModel = new LiveMonitorViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        await WaitForAsync(() => realtime.Subscribed.Contains(session.Id));
        Assert.Equal(1, backend.SessionDetailRequests);
        Assert.Empty(viewModel.Participants);

        realtime.Raise(new(
            session.Id,
            RealtimeEvents.ParticipantJoined,
            1,
            null));
        await WaitForAsync(() => backend.SessionDetailRequests == 2);
        Assert.Empty(viewModel.Participants);

        backend.Participants = [CreateParticipant(session.Id)];
        await Task.Delay(350);

        Assert.Equal(2, backend.SessionDetailRequests);
        Assert.Empty(viewModel.Participants);

        realtime.Raise(new(
            session.Id,
            RealtimeEvents.ParticipantJoined,
            2,
            null));
        await WaitForAsync(() => backend.SessionDetailRequests == 3);
        Assert.Single(viewModel.Participants);
    }

    [Fact]
    public async Task LiveMonitor_OnlyLanNotificationsDebounceAndStopAfterDispose()
    {
        var session = CreateSession(SessionStatus.InProgress);
        Assert.Equal(SessionAccessMode.LanOnly, session.AccessMode);
        var backend = new TeacherRealtimeBackend(session)
        {
            Participants = [CreateParticipant(session.Id)]
        };
        var realtime = new FakeTeacherRealtime();
        var viewModel = new LiveMonitorViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        await WaitForAsync(() => realtime.Subscribed.Contains(session.Id));
        Assert.Equal(1, backend.SessionDetailRequests);

        for (var index = 0; index < 3; index++)
        {
            realtime.Raise(new(
                session.Id,
                RealtimeEvents.ParticipantConnectionChanged,
                index + 1,
                null));
        }
        await WaitForAsync(() => backend.SessionDetailRequests == 2);
        await Task.Delay(250);
        Assert.Equal(2, backend.SessionDetailRequests);

        realtime.Raise(new(
            Guid.NewGuid(),
            RealtimeEvents.ParticipantConnectionChanged,
            4,
            null));
        await Task.Delay(250);
        Assert.Equal(2, backend.SessionDetailRequests);

        viewModel.Dispose();
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.ParticipantConnectionChanged,
            5,
            null));
        await Task.Delay(250);
        Assert.Equal(2, backend.SessionDetailRequests);
    }

    [Fact]
    public async Task ProjectionCoordinator_DeduplicatesVersionsSerializesAndBoundsRetries()
    {
        var calls = 0;
        var concurrent = 0;
        var maxConcurrent = 0;
        using var coordinator = new ProjectionRefreshCoordinator(
            async (_, _) =>
            {
                var active = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, active);
                var call = Interlocked.Increment(ref calls);
                try
                {
                    await Task.Delay(10);
                    if (call <= 2)
                        throw new InvalidOperationException("transient snapshot failure");
                }
                finally
                {
                    Interlocked.Decrement(ref concurrent);
                }
            },
            TimeSpan.Zero,
            [TimeSpan.Zero, TimeSpan.Zero],
            []);
        var sessionId = Guid.NewGuid();

        Assert.True(coordinator.Schedule(sessionId, 10));
        Assert.False(coordinator.Schedule(sessionId, 10));
        Assert.False(coordinator.Schedule(sessionId, 9));
        await WaitForAsync(() => calls == 3);
        Assert.True(coordinator.Schedule(sessionId, 11));
        await WaitForAsync(() => calls == 4);

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task ProjectionCoordinator_RecoveryIsBoundedAndDisposeCancelsPendingWork()
    {
        var calls = 0;
        using (var coordinator = new ProjectionRefreshCoordinator(
                   (_, _) =>
                   {
                       Interlocked.Increment(ref calls);
                       return Task.CompletedTask;
                   },
                   TimeSpan.Zero,
                   [],
                   [TimeSpan.Zero, TimeSpan.Zero]))
        {
            coordinator.StartRecovery();
            await WaitForAsync(() => calls == 2);
            await Task.Delay(50);
            Assert.Equal(2, calls);
        }

        var cancelledCalls = 0;
        var cancelled = new ProjectionRefreshCoordinator(
            (_, _) =>
            {
                Interlocked.Increment(ref cancelledCalls);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1),
            [],
            [TimeSpan.FromSeconds(1)]);
        cancelled.StartRecovery();
        cancelled.Dispose();
        await Task.Delay(100);
        Assert.Equal(0, cancelledCalls);
    }

    [Fact]
    public async Task LiveMonitor_PublicCloudProjectionEventRefreshesVisibleParticipantOncePerNewVersion()
    {
        var session = CreateSession(SessionStatus.InProgress, SessionAccessMode.PublicCloud);
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        var viewModel = new LiveMonitorViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        await WaitForAsync(() => backend.SessionDetailRequests == 3);

        backend.Participants = [CreateParticipant(session.Id)];
        var update10 = new PublicCloudProjectionUpdatedEvent(
            session.Id,
            PublicCloudProjectionEntityTypes.SessionParticipant,
            10);
        for (var index = 0; index < 3; index++)
            realtime.Raise(new(
                session.Id,
                RealtimeEvents.PublicCloudProjectionUpdated,
                10,
                null,
                null,
                update10));
        realtime.Raise(new(
            Guid.NewGuid(),
            RealtimeEvents.PublicCloudProjectionUpdated,
            99,
            null,
            null,
            update10 with { SessionId = Guid.NewGuid(), ProjectionVersion = 99 }));

        await WaitForAsync(() => backend.SessionDetailRequests == 4);
        Assert.Single(viewModel.Participants);
        await Task.Delay(250);
        Assert.Equal(4, backend.SessionDetailRequests);

        var update11 = update10 with { ProjectionVersion = 11 };
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.PublicCloudProjectionUpdated,
            11,
            null,
            null,
            update11));
        await WaitForAsync(() => backend.SessionDetailRequests == 5);

        realtime.RaiseEvent("Reconnected");
        await WaitForAsync(() => backend.SessionDetailRequests == 7);
        await Task.Delay(200);
        Assert.Equal(7, backend.SessionDetailRequests);

        viewModel.Dispose();
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.PublicCloudProjectionUpdated,
            12,
            null,
            null,
            update11 with { ProjectionVersion = 12 }));
        await Task.Delay(250);
        Assert.Equal(7, backend.SessionDetailRequests);
    }

    [Fact]
    public async Task Lobby_PublicCloudProjectionEventRefreshesParticipantOncePerNewVersion()
    {
        // Arrange – Lobby với session PublicCloud đang chờ
        var session = CreateSession(SessionStatus.Waiting, SessionAccessMode.PublicCloud);
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        var viewModel = new LobbyViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        await WaitForAsync(() => realtime.Subscribed.Contains(session.Id));
        Assert.Equal(1, backend.SessionDetailRequests);
        Assert.Empty(viewModel.Participants);

        // Act – event sai SessionId không trigger refresh
        var update10 = new PublicCloudProjectionUpdatedEvent(
            session.Id,
            PublicCloudProjectionEntityTypes.SessionParticipant,
            10);
        realtime.Raise(new(
            Guid.NewGuid(),
            RealtimeEvents.PublicCloudProjectionUpdated,
            10,
            null,
            null,
            update10 with { SessionId = Guid.NewGuid(), ProjectionVersion = 10 }));
        await Task.Delay(250);
        Assert.Equal(1, backend.SessionDetailRequests);

        // Act – 3 event cùng version (deduplicated) → chỉ 1 request
        backend.Participants = [CreateParticipant(session.Id)];
        for (var index = 0; index < 3; index++)
            realtime.Raise(new(
                session.Id,
                RealtimeEvents.PublicCloudProjectionUpdated,
                10,
                null,
                null,
                update10));

        await WaitForAsync(() => backend.SessionDetailRequests == 2);
        Assert.Single(viewModel.Participants);
        await Task.Delay(200);
        Assert.Equal(2, backend.SessionDetailRequests);

        // Act – version mới → refresh thêm 1 lần
        var update11 = update10 with { ProjectionVersion = 11 };
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.PublicCloudProjectionUpdated,
            11,
            null,
            null,
            update11));
        await WaitForAsync(() => backend.SessionDetailRequests == 3);

        // Act – LanOnly event không trigger refresh trong Lobby PublicCloud
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.ParticipantJoined,
            12,
            null));
        await WaitForAsync(() => backend.SessionDetailRequests == 4);

        // Act – Dispose → event sau không trigger
        viewModel.Dispose();
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.PublicCloudProjectionUpdated,
            13,
            null,
            null,
            update11 with { ProjectionVersion = 13 }));
        await Task.Delay(250);
        Assert.Equal(4, backend.SessionDetailRequests);
    }

    [Fact]
    public async Task Lobby_PublicCloudProjection_WrongEntityType_DoesNotRefresh()
    {
        var session = CreateSession(SessionStatus.Waiting, SessionAccessMode.PublicCloud);
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        using var viewModel = new LobbyViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        await WaitForAsync(() => realtime.Subscribed.Contains(session.Id));
        Assert.Equal(1, backend.SessionDetailRequests);

        // Event với EntityType khác SessionParticipant → không refresh
        var update = new PublicCloudProjectionUpdatedEvent(
            session.Id,
            "SessionResult",   // entity type khác
            5);
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.PublicCloudProjectionUpdated,
            5,
            null,
            null,
            update));
        await Task.Delay(250);
        Assert.Equal(1, backend.SessionDetailRequests);
    }

    [Fact]
    public async Task Lobby_LanOnly_DoesNotRespondToPublicCloudProjection()
    {
        // LanOnly session không được phản ứng với PublicCloudProjectionUpdated
        var session = CreateSession(SessionStatus.Waiting, SessionAccessMode.LanOnly);
        Assert.Equal(SessionAccessMode.LanOnly, session.AccessMode);
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        using var viewModel = new LobbyViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        await WaitForAsync(() => realtime.Subscribed.Contains(session.Id));
        Assert.Equal(1, backend.SessionDetailRequests);

        var update = new PublicCloudProjectionUpdatedEvent(
            session.Id,
            PublicCloudProjectionEntityTypes.SessionParticipant,
            10);
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.PublicCloudProjectionUpdated,
            10,
            null,
            null,
            update));
        await Task.Delay(250);
        // LanOnly không được xử lý PublicCloudProjectionUpdated
        Assert.Equal(1, backend.SessionDetailRequests);
    }

    [Fact]
    public async Task SubmissionCenter_RealtimeSubscribeFailureDoesNotBlockSnapshotLoad()
    {
        var session = CreateSession(
            SessionStatus.Collecting,
            SessionAccessMode.PublicCloud);
        var submission = CreateSubmission(session.Id);
        var backend = new TeacherRealtimeBackend(session)
        {
            Submissions = [submission]
        };
        var realtime = new FakeTeacherRealtime
        {
            SubscribeFailure = new InvalidOperationException("realtime unavailable")
        };

        using var viewModel = new SubmissionCenterViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);

        Assert.Equal(1, backend.SubmissionRequests);
        Assert.Equal(submission.Id, Assert.Single(viewModel.Submissions).SubmissionId);
    }

    [Fact]
    public async Task SubmissionCenter_SubmissionAcceptedRefreshesSelectedSessionOnlyAndDebounces()
    {
        var session = CreateSession(SessionStatus.Collecting);
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        using var viewModel = new SubmissionCenterViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        await WaitForAsync(() => realtime.Subscribed.Contains(session.Id));
        Assert.Equal(1, backend.SubmissionRequests);

        realtime.Raise(
            new(
                Guid.NewGuid(),
                RealtimeEvents.SubmissionAccepted,
                1,
                null));
        await Task.Delay(250);
        Assert.Equal(1, backend.SubmissionRequests);

        backend.Submissions =
        [
            CreateSubmission(session.Id)
        ];
        for (var i = 0; i < 3; i++)
        {
            realtime.Raise(
                new(
                    session.Id,
                    RealtimeEvents.SubmissionAccepted,
                    i + 2,
                    null));
        }

        await WaitForAsync(() => backend.SubmissionRequests == 2);
        await Task.Delay(200);
        Assert.Equal(2, backend.SubmissionRequests);
        Assert.Single(viewModel.Submissions);
    }

    [Fact]
    public async Task SubmissionCenter_QuizAttemptFinalizedRefreshesQuizProjectionOnly()
    {
        var session = CreateSession(SessionStatus.Collecting) with
        {
            DeliveryType = ExamDeliveryType.MultipleChoice
        };
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        using var viewModel = new SubmissionCenterViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        Assert.Equal(1, backend.QuizAttemptRequests);
        Assert.Equal(0, backend.SubmissionRequests);

        backend.QuizAttempts = [CreateQuizAttempt(session.Id)];
        realtime.Raise(new(
            Guid.NewGuid(),
            RealtimeEvents.QuizAttemptFinalized,
            2,
            null));
        await Task.Delay(250);
        Assert.Equal(1, backend.QuizAttemptRequests);

        for (var index = 0; index < 3; index++)
        {
            realtime.Raise(new(
                session.Id,
                RealtimeEvents.QuizAttemptFinalized,
                index + 3,
                null));
        }

        await WaitForAsync(() => backend.QuizAttemptRequests == 2);
        Assert.True(Assert.Single(viewModel.Submissions).IsQuizAttempt);
        Assert.Equal(0, backend.SubmissionRequests);
    }

    [Fact]
    public async Task SessionManagement_QuizAttemptFinalizedRefreshesSubmittedCount()
    {
        var session = CreateSession(SessionStatus.InProgress) with
        {
            DeliveryType = ExamDeliveryType.MultipleChoice
        };
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        using var viewModel = new SessionManagementViewModel(
            backend,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 1,
            realtime: realtime);
        await viewModel.InitializeAsync(default);
        Assert.Equal(0, viewModel.SelectedSession?.Counts.Submitted);

        backend.Session = session with
        {
            Counts = session.Counts with { Submitted = 1 }
        };
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.QuizAttemptFinalized,
            2,
            null));

        await WaitForAsync(() => backend.SessionRequests == 2);
        Assert.Equal(1, viewModel.SelectedSession?.Counts.Submitted);
    }

    [Fact]
    public async Task SubmissionCenter_PublicCloudQuizProjectionRefreshesSelectedSessionOnly()
    {
        var session = CreateSession(
            SessionStatus.Collecting,
            SessionAccessMode.PublicCloud) with
        {
            DeliveryType = ExamDeliveryType.MultipleChoice
        };
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        using var viewModel = new SubmissionCenterViewModel(backend, realtime);
        await viewModel.InitializeAsync(default);
        Assert.Equal(1, backend.QuizAttemptRequests);

        backend.QuizAttempts = [CreateQuizAttempt(session.Id)];
        realtime.Raise(new(
            Guid.NewGuid(),
            RealtimeEvents.PublicCloudProjectionUpdated,
            20,
            null,
            ProjectionUpdated: new(
                Guid.NewGuid(),
                PublicCloudProjectionEntityTypes.QuizAttempt,
                20)));
        await Task.Delay(250);
        Assert.Equal(1, backend.QuizAttemptRequests);

        realtime.Raise(new(
            session.Id,
            RealtimeEvents.PublicCloudProjectionUpdated,
            21,
            null,
            ProjectionUpdated: new(
                session.Id,
                PublicCloudProjectionEntityTypes.QuizAttempt,
                21)));

        await WaitForAsync(() => backend.QuizAttemptRequests == 2);
        Assert.True(Assert.Single(viewModel.Submissions).IsQuizAttempt);
    }

    [Fact]
    public async Task SessionManagement_PublicCloudQuizProjectionRefreshesSubmittedCount()
    {
        var session = CreateSession(
            SessionStatus.InProgress,
            SessionAccessMode.PublicCloud) with
        {
            DeliveryType = ExamDeliveryType.MultipleChoice
        };
        var backend = new TeacherRealtimeBackend(session);
        var realtime = new FakeTeacherRealtime();
        using var viewModel = new SessionManagementViewModel(
            backend,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 1,
            realtime: realtime);
        await viewModel.InitializeAsync(default);
        Assert.Equal(0, viewModel.SelectedSession?.Counts.Submitted);

        backend.Session = session with
        {
            Counts = session.Counts with { Submitted = 1 }
        };
        realtime.Raise(new(
            session.Id,
            RealtimeEvents.PublicCloudProjectionUpdated,
            22,
            null,
            ProjectionUpdated: new(
                session.Id,
                PublicCloudProjectionEntityTypes.QuizAttempt,
                22)));

        await WaitForAsync(() => backend.SessionRequests == 2);
        Assert.Equal(1, viewModel.SelectedSession?.Counts.Submitted);
    }

    private static SessionSummaryDto CreateSession(
        SessionStatus status,
        SessionAccessMode accessMode = SessionAccessMode.LanOnly) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Realtime exam",
            "RT1234",
            status,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new SessionCountsDto(0, 0, 0, 0, 0, 0, 0),
            1,
            "v1",
            accessMode);

    private static ParticipantDto CreateParticipant(Guid sessionId) =>
        new(
            Guid.NewGuid(),
            sessionId,
            "SV001",
            "Student",
            "device",
            "machine",
            "127.0.0.1",
            "1.2.0",
            ParticipantStatus.PendingApproval,
            DateTimeOffset.UtcNow,
            DownloadStatus.NotStarted,
            SubmissionStatus.NotStarted,
            0,
            null,
            ConnectionState.Online);

    private static SubmissionSummaryDto CreateSubmission(Guid sessionId) =>
        new(
            Guid.NewGuid(),
            sessionId,
            Guid.NewGuid(),
            "SV001",
            "Student",
            1,
            SubmissionStatus.Submitted,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(30),
            false,
            "RECEIPT",
            true,
            []);

    private static TeacherQuizAttemptSummaryDto CreateQuizAttempt(Guid sessionId)
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        return new(
            Guid.NewGuid(),
            sessionId,
            Guid.NewGuid(),
            "SV-QUIZ",
            "Quiz Student",
            1,
            QuizAttemptStatus.Finalized,
            GradingStatus.Graded,
            8m,
            10m,
            startedAt,
            startedAt.AddMinutes(5),
            300,
            false);
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        int timeoutMilliseconds = 3000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class FakeTeacherRealtime : IRealtimeService
    {
        public bool IsConnected { get; private set; } = true;
        public bool FailNextUnsubscribe { get; set; }
        public Exception? SubscribeFailure { get; set; }
        public int DisconnectCount { get; private set; }
        public HashSet<Guid> Subscribed { get; } = [];
        public List<Guid> Unsubscribed { get; } = [];
        public event EventHandler<string>? EventReceived;
        public event EventHandler<StudentRealtimeNotification>? NotificationReceived;

        public Task ConnectAsync(
            string? token = null,
            CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SubscribeSessionAsync(
            Guid sessionId,
            CancellationToken ct = default)
        {
            if (SubscribeFailure is not null)
                throw SubscribeFailure;
            Subscribed.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task UnsubscribeSessionAsync(
            Guid sessionId,
            CancellationToken ct = default)
        {
            Subscribed.Remove(sessionId);
            Unsubscribed.Add(sessionId);
            if (FailNextUnsubscribe)
            {
                FailNextUnsubscribe = false;
                throw new InvalidOperationException("transient unsubscribe failure");
            }
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            IsConnected = false;
            DisconnectCount++;
            EventReceived?.Invoke(this, "Disconnected");
            return Task.CompletedTask;
        }

        public void Raise(StudentRealtimeNotification notification) =>
            NotificationReceived?.Invoke(this, notification);

        public void RaiseEvent(string eventName) =>
            EventReceived?.Invoke(this, eventName);
    }

    private sealed class TeacherRealtimeBackend(
        SessionSummaryDto session) : IBackendClient
    {
        public SessionSummaryDto Session { get; set; } = session;
        public IReadOnlyList<ParticipantDto> Participants { get; set; } = [];
        public IReadOnlyList<SubmissionSummaryDto> Submissions { get; set; } = [];
        public IReadOnlyList<TeacherQuizAttemptSummaryDto> QuizAttempts { get; set; } = [];
        public int SessionRequests { get; private set; }
        public int SessionDetailRequests { get; private set; }
        public int SubmissionRequests { get; private set; }
        public int QuizAttemptRequests { get; private set; }
        public Uri BaseAddress { get; } = new("http://localhost:5048/");
        public bool HasTrustedAccountToken => true;

        public bool TrySetBaseAddress(
            string hostOrUrl,
            int port,
            out string? error)
        {
            error = null;
            return true;
        }

        public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(
            CancellationToken ct = default)
        {
            SessionRequests++;
            return Task.FromResult<ApiResponse<PagedResult<SessionSummaryDto>>?>(
                ApiResponse<PagedResult<SessionSummaryDto>>.Ok(
                    new([Session], 1, 50, 1),
                    "test"));
        }

        public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(
            Guid id,
            CancellationToken ct = default)
        {
            SessionDetailRequests++;
            return Task.FromResult<ApiResponse<SessionDetailDto>?>(
                ApiResponse<SessionDetailDto>.Ok(
                    new(Session, Participants, "{}"),
                    "test"));
        }

        public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(
            Guid sessionId,
            CancellationToken ct = default)
        {
            SubmissionRequests++;
            return Task.FromResult<ApiResponse<PagedResult<SubmissionSummaryDto>>?>(
                ApiResponse<PagedResult<SubmissionSummaryDto>>.Ok(
                    new(Submissions, 1, 50, Submissions.Count),
                    "test"));
        }

        public Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<SystemStatusDto>?>(null);
        public Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<DashboardSummaryDto>?>(null);
        public Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<PagedResult<ClassSummaryDto>>?>(null);
        public Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<PagedResult<ExamSummaryDto>>?>(
                ApiResponse<PagedResult<ExamSummaryDto>>.Ok(
                    new([
                        new ExamSummaryDto(
                            Session.ExamId,
                            null,
                            Session.Title,
                            "Realtime",
                            30,
                            Session.DeliveryType,
                            ExamStatus.Published,
                            Session.ExamVersion,
                            0,
                            "exam-rv")
                    ], 1, 50, 1),
                    "test"));
        public Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<CloudSyncStatusDto>?>(null);
        public Task<ApiResponse<SettingsDto>?> GetSettingsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<SettingsDto>?>(null);
        public Task<ApiResponse<T>?> GetAsync<T>(
            string path,
            CancellationToken ct = default)
        {
            if (path.EndsWith("/quiz-attempts", StringComparison.Ordinal))
            {
                QuizAttemptRequests++;
                var response = ApiResponse<IReadOnlyList<TeacherQuizAttemptSummaryDto>>.Ok(
                    QuizAttempts,
                    "test");
                return Task.FromResult<ApiResponse<T>?>(
                    (ApiResponse<T>)(object)response);
            }
            return Task.FromResult<ApiResponse<T>?>(null);
        }
        public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(
            string path,
            TRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(
            string path,
            TRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(
            string path,
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<object>?> UploadChunkAsync(
            string path,
            Stream content,
            long contentLength,
            string? sha256 = null,
            CancellationToken ct = default) =>
            Task.FromResult<ApiResponse<object>?>(null);
        public Task DownloadFileAsync(
            string path,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task DownloadVerifiedFileAsync(
            string path,
            string destinationPath,
            string expectedSha256,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task PostDownloadFileAsync<TRequest>(
            string path,
            TRequest request,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;
        public void SetBearerToken(string? token) { }
        public void SetAccountToken(string? token) { }
        public void SetParticipantToken(string? token) { }
    }
}
