using System.IO;
using System.Reflection;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class RuntimeRebase03AR2FlowCharacterizationTests
{
    public static TheoryData<
        StudentExamFlowSnapshot,
        StudentExamFlowState,
        string,
        bool> LifecycleRoutes => new()
    {
        {
            Snapshot(participant: ParticipantStatus.PendingApproval, status: SessionStatus.Waiting),
            StudentExamFlowState.PendingApproval,
            "S-03",
            false
        },
        {
            Snapshot(participant: ParticipantStatus.Rejected, status: SessionStatus.Waiting),
            StudentExamFlowState.RejectedOrExpired,
            "S-01",
            false
        },
        {
            Snapshot(participant: ParticipantStatus.Approved, status: SessionStatus.Waiting),
            StudentExamFlowState.ApprovedWaiting,
            "S-03",
            false
        },
        {
            Snapshot(
                participant: ParticipantStatus.Approved,
                status: SessionStatus.InProgress,
                delivery: ExamDeliveryType.FileSubmission),
            StudentExamFlowState.ReadyToStartFileExam,
            "S-05",
            false
        },
        {
            Snapshot(
                participant: ParticipantStatus.Approved,
                status: SessionStatus.InProgress,
                delivery: ExamDeliveryType.MultipleChoice),
            StudentExamFlowState.ReadyToStartQuiz,
            "S-06",
            true
        },
        {
            Snapshot(participant: ParticipantStatus.Approved, status: SessionStatus.Collecting),
            StudentExamFlowState.ApprovedWaiting,
            "S-03",
            false
        },
        {
            Snapshot(participant: ParticipantStatus.Approved, status: SessionStatus.Finished),
            StudentExamFlowState.SessionFinished,
            "S-04",
            false
        },
        {
            Snapshot(participant: ParticipantStatus.Approved, status: SessionStatus.Cancelled),
            StudentExamFlowState.SessionFinished,
            "S-04",
            false
        },
        {
            Snapshot(participant: ParticipantStatus.Approved, status: SessionStatus.Archived),
            StudentExamFlowState.SessionFinished,
            "S-04",
            false
        }
    };

    [Theory]
    [MemberData(nameof(LifecycleRoutes))]
    public void LifecycleSnapshot_CharacterizesApprovalStartCollectingAndTerminalRoutes(
        StudentExamFlowSnapshot snapshot,
        StudentExamFlowState expectedState,
        string expectedRoute,
        bool expectedConfirmation)
    {
        var resolution = StudentExamFlowCoordinator.ResolveSnapshot(snapshot);

        Assert.Equal(expectedState, resolution.State);
        Assert.Equal(expectedRoute, resolution.RouteKey);
        Assert.Equal(expectedConfirmation, resolution.RequiresStartConfirmation);
    }

    [Theory]
    [InlineData(SessionStatus.Finished)]
    [InlineData(SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Archived)]
    public void TerminalSessionState_DoesNotStopStudentExamTicker(SessionStatus terminal)
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        clock.Synchronize(now);
        var ticker = new FakeCountdownTicker();
        var state = ActiveState(
            terminal,
            ParticipantStatus.Approved,
            ExamDeliveryType.FileSubmission,
            SubmissionStatus.NotStarted);
        using var viewModel = new StudentExamViewModel(
            new RecordingBackendClient(now),
            state,
            new FakeStudentHeartbeatService(),
            new FakeStudentRealtimeService(),
            clock,
            ticker);
        SetField(viewModel, "publicStartedAtUtc", now.AddMinutes(-40));
        SetField(viewModel, "publicDeadlineUtc", now.AddMinutes(20));
        SetField(viewModel, "publicSessionStatus", terminal.ToString());

        ticker.Fire();
        Assert.Equal("00:20:00", viewModel.TimeLeft);
        var initialProgress = viewModel.TimeProgress;

        source.Advance(TimeSpan.FromMinutes(1));
        ticker.Fire();

        Assert.True(ticker.IsRunning);
        Assert.Equal("00:19:00", viewModel.TimeLeft);
        Assert.NotEqual(initialProgress, viewModel.TimeProgress);
        Assert.Equal(terminal, state.SessionStatus);
        var route = StudentExamFlowCoordinator.ResolveSnapshot(Snapshot(
            participant: ParticipantStatus.Approved,
            status: terminal));
        Assert.Equal("S-04", route.RouteKey);
    }

    private static StudentExamFlowSnapshot Snapshot(
        ParticipantStatus participant,
        SessionStatus status,
        ExamDeliveryType delivery = ExamDeliveryType.FileSubmission,
        SubmissionStatus submission = SubmissionStatus.NotStarted) =>
        new(true, status, participant, delivery, submission, null);

    private static StudentSessionState ActiveState(
        SessionStatus status,
        ParticipantStatus participant,
        ExamDeliveryType delivery,
        SubmissionStatus submission) =>
        new()
        {
            SessionId = Guid.NewGuid(),
            ParticipantId = Guid.NewGuid(),
            AccessMode = SessionAccessMode.PublicCloud,
            AccessToken = "characterization-token",
            SessionStatus = status,
            ParticipantStatus = participant,
            DeliveryType = delivery,
            SubmissionStatus = submission
        };

    private static void SetField<T>(object target, string name, T value) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
}

public sealed class RuntimeRebase03AR2SubmissionFrontendCharacterizationTests
{
    public static TheoryData<
        bool,
        ParticipantStatus?,
        SessionStatus?,
        ExamDeliveryType,
        SubmissionStatus,
        bool> EligibilityCases => new()
    {
        { false, null, null, ExamDeliveryType.FileSubmission, SubmissionStatus.NotStarted, false },
        { true, ParticipantStatus.PendingApproval, SessionStatus.Waiting, ExamDeliveryType.FileSubmission, SubmissionStatus.NotStarted, true },
        { true, ParticipantStatus.Approved, SessionStatus.Waiting, ExamDeliveryType.FileSubmission, SubmissionStatus.NotStarted, true },
        { true, ParticipantStatus.Approved, SessionStatus.InProgress, ExamDeliveryType.FileSubmission, SubmissionStatus.NotStarted, true },
        { true, ParticipantStatus.Approved, SessionStatus.Finished, ExamDeliveryType.FileSubmission, SubmissionStatus.NotStarted, true },
        { true, ParticipantStatus.Approved, SessionStatus.Cancelled, ExamDeliveryType.FileSubmission, SubmissionStatus.NotStarted, true },
        { true, ParticipantStatus.Approved, SessionStatus.InProgress, ExamDeliveryType.FileSubmission, SubmissionStatus.Submitted, true },
        { true, ParticipantStatus.Approved, SessionStatus.InProgress, ExamDeliveryType.MultipleChoice, SubmissionStatus.NotStarted, true }
    };

    [Theory]
    [MemberData(nameof(EligibilityCases))]
    public void SubmitCanExecute_OnlyUsesFileBusyAndHasSession(
        bool hasSession,
        ParticipantStatus? participant,
        SessionStatus? session,
        ExamDeliveryType delivery,
        SubmissionStatus submission,
        bool expected)
    {
        var state = new StudentSessionState
        {
            SessionId = hasSession ? Guid.NewGuid() : null,
            ParticipantId = hasSession ? Guid.NewGuid() : null,
            ParticipantStatus = participant,
            SessionStatus = session,
            DeliveryType = delivery,
            SubmissionStatus = submission
        };
        using var viewModel = new StudentSubmissionViewModel(
            new BackendClient("http://localhost:5048"),
            state,
            new AppAuthSessionState(Path.Combine(
                Path.GetTempPath(),
                $"runtime-rebase-auth-{Guid.NewGuid():N}.bin")),
            new NoOpSubmissionRecoveryService());
        SetProperty(viewModel, nameof(StudentSubmissionViewModel.SelectedPath), "answer.zip");
        SetProperty(viewModel, nameof(StudentSubmissionViewModel.IsFileValid), true);

        Assert.Equal(expected, viewModel.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task CommandImmediateSpam_IsSingleFlight_ButThreeSequentialExecutionsAllRun()
    {
        var calls = 0;
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async () =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
        });

        command.Execute(null);
        command.Execute(null);
        command.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => calls == 1, TimeSpan.FromSeconds(1)));
        Assert.False(command.CanExecute(null));
        release.TrySetResult();
        Assert.True(SpinWait.SpinUntil(
            () => command.CanExecute(null),
            TimeSpan.FromSeconds(1)));

        for (var index = 0; index < 3; index++)
        {
            command.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => command.CanExecute(null),
                TimeSpan.FromSeconds(1)));
        }

        Assert.Equal(4, calls);
        await Task.CompletedTask;
    }

    [Fact]
    public void ActiveQueueSnapshot_DoesNotChangeSubmitEligibility()
    {
        var state = new StudentSessionState
        {
            SessionId = Guid.NewGuid(),
            ParticipantId = Guid.NewGuid(),
            ParticipantStatus = ParticipantStatus.Approved,
            SessionStatus = SessionStatus.InProgress,
            DeliveryType = ExamDeliveryType.FileSubmission
        };
        using var viewModel = new StudentSubmissionViewModel(
            new BackendClient("http://localhost:5048"),
            state,
            new AppAuthSessionState(Path.Combine(
                Path.GetTempPath(),
                $"runtime-rebase-auth-{Guid.NewGuid():N}.bin")),
            new NoOpSubmissionRecoveryService());
        SetProperty(viewModel, nameof(StudentSubmissionViewModel.SelectedPath), "answer.zip");
        SetProperty(viewModel, nameof(StudentSubmissionViewModel.IsFileValid), true);
        viewModel.TrackQueue(SubmissionProgressSnapshotTests.QueueItem(
            SubmissionQueueStatus.Uploading,
            submissionId: Guid.NewGuid(),
            chunkSizeBytes: 100,
            sizeBytes: 400,
            missingChunks: [2, 3]));

        Assert.True(viewModel.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void QueuePreparationSource_HasNoSessionParticipantSingleFlightLookup()
    {
        var source = ReadRepositoryFile(
            "frontend",
            "src",
            "ExamTransfer.Desktop",
            "Infrastructure",
            "SubmissionQueueStore.cs");
        var prepare = source[
            source.IndexOf("public static async Task<PendingSubmission> PrepareAsync", StringComparison.Ordinal)
            ..source.IndexOf("public static async Task<IReadOnlyList<PendingSubmission>> LoadAsync", StringComparison.Ordinal)];

        Assert.Contains("var queueId = Guid.NewGuid();", prepare, StringComparison.Ordinal);
        Assert.Contains("Guid.NewGuid().ToString(\"N\")", prepare, StringComparison.Ordinal);
        Assert.Contains("Directory.CreateDirectory(queueDirectory)", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionId ==", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("ParticipantId ==", prepare, StringComparison.Ordinal);
    }

    private static void SetProperty<T>(object target, string name, T value) =>
        target.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(target, value);

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = segments.Aggregate(directory.FullName, Path.Combine);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private sealed class NoOpSubmissionRecoveryService : ISubmissionRecoveryService
    {
        public int PendingCount => 0;
        public event EventHandler<int>? PendingCountChanged
        {
            add { }
            remove { }
        }
        public event EventHandler<SubmissionProgressSnapshot>? ProgressChanged
        {
            add { }
            remove { }
        }
        public void Start() { }
        public void Trigger() { }
        public void Dispose() { }
    }
}
