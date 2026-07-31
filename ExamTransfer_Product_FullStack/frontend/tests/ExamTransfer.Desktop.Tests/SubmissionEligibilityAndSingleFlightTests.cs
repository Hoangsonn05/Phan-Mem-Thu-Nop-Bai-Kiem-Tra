using System.IO;
using System.Reflection;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class SubmissionEligibilityPolicyTests
{
    public static TheoryData<string, SubmissionEligibilityInput, bool, string> Cases => new()
    {
        { "no session", Eligible() with { HasSession = false, SessionId = null, ParticipantId = null }, false, SubmissionEligibilityReasonCodes.NoActiveSession },
        { "no participant", Eligible() with { HasSession = false, ParticipantId = null }, false, SubmissionEligibilityReasonCodes.NoActiveSession },
        { "pending", Eligible() with { ParticipantStatus = ParticipantStatus.PendingApproval }, false, SubmissionEligibilityReasonCodes.ParticipantNotApproved },
        { "rejected", Eligible() with { ParticipantStatus = ParticipantStatus.Rejected }, false, SubmissionEligibilityReasonCodes.ParticipantNotApproved },
        { "approved waiting", Eligible() with { SessionStatus = SessionStatus.Waiting }, false, SubmissionEligibilityReasonCodes.SessionNotAcceptingSubmissions },
        { "in progress file", Eligible(), true, SubmissionEligibilityReasonCodes.Allowed },
        { "collecting file", Eligible() with { SessionStatus = SessionStatus.Collecting }, true, SubmissionEligibilityReasonCodes.Allowed },
        { "finished", Eligible() with { SessionStatus = SessionStatus.Finished }, false, SubmissionEligibilityReasonCodes.SessionNotAcceptingSubmissions },
        { "cancelled", Eligible() with { SessionStatus = SessionStatus.Cancelled }, false, SubmissionEligibilityReasonCodes.SessionNotAcceptingSubmissions },
        { "archived", Eligible() with { SessionStatus = SessionStatus.Archived }, false, SubmissionEligibilityReasonCodes.SessionNotAcceptingSubmissions },
        { "quiz", Eligible() with { DeliveryType = ExamDeliveryType.MultipleChoice }, false, SubmissionEligibilityReasonCodes.WrongDeliveryType },
        { "invalid file", Eligible() with { HasValidFile = false }, false, SubmissionEligibilityReasonCodes.InvalidFile },
        { "busy", Eligible() with { IsBusy = true }, false, SubmissionEligibilityReasonCodes.Busy },
        { "active queue", Eligible() with { HasActiveQueue = true }, false, SubmissionEligibilityReasonCodes.SubmissionAlreadyProcessing },
        { "receipt without resubmit", Eligible() with { HasSuccessfulReceipt = true }, false, SubmissionEligibilityReasonCodes.SubmissionAlreadyCompleted },
        { "receipt with resubmit", Eligible() with { HasSuccessfulReceipt = true, ResubmitAllowed = true }, true, SubmissionEligibilityReasonCodes.Allowed },
        { "submitted without resubmit", Eligible() with { SubmissionStatus = SubmissionStatus.Submitted }, false, SubmissionEligibilityReasonCodes.ResubmitNotAllowed },
        { "submitted with resubmit", Eligible() with { SubmissionStatus = SubmissionStatus.Submitted, ResubmitAllowed = true }, true, SubmissionEligibilityReasonCodes.Allowed },
        { "rejected submission without resubmit", Eligible() with { SubmissionStatus = SubmissionStatus.Rejected }, false, SubmissionEligibilityReasonCodes.ResubmitNotAllowed },
        { "rejected submission with resubmit", Eligible() with { SubmissionStatus = SubmissionStatus.Rejected, ResubmitAllowed = true }, true, SubmissionEligibilityReasonCodes.Allowed },
        { "resubmit but active", Eligible() with { HasSuccessfulReceipt = true, ResubmitAllowed = true, HasActiveQueue = true }, false, SubmissionEligibilityReasonCodes.SubmissionAlreadyProcessing }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Evaluate_UsesFailClosedSubmissionPolicy(
        string scenario,
        SubmissionEligibilityInput input,
        bool expectedAllowed,
        string expectedReason)
    {
        var decision = SubmissionEligibilityPolicy.Evaluate(input);

        Assert.Equal(expectedAllowed, decision.Allowed);
        Assert.Equal(expectedReason, decision.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(decision.UserMessage));
        Assert.False(string.IsNullOrWhiteSpace(scenario));
    }

    private static SubmissionEligibilityInput Eligible() => new(
        IsBusy: false,
        HasSession: true,
        SessionId: Guid.NewGuid(),
        ParticipantId: Guid.NewGuid(),
        ParticipantStatus: ParticipantStatus.Approved,
        SessionStatus: SessionStatus.InProgress,
        DeliveryType: ExamDeliveryType.FileSubmission,
        HasValidFile: true,
        HasActiveQueue: false,
        HasSuccessfulReceipt: false,
        SubmissionStatus: SubmissionStatus.NotStarted,
        ResubmitAllowed: false);
}

[CollectionDefinition("Submission queue storage", DisableParallelization = true)]
public sealed class SubmissionQueueStorageCollection;

[Collection("Submission queue storage")]
public sealed class SubmissionQueueSingleFlightTests
{
    [Fact]
    public async Task ConcurrentPrepare_SameBusinessKeyCreatesOnePersistentQueueAndSpool()
    {
        using var fixture = new QueueStorageFixture();
        var source = fixture.CreateArchive("answer.zip", 0x11);
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            PrepareAsync(source, ownerId, sessionId, participantId)));

        Assert.Equal(1, results.Count(result => result.Created));
        Assert.Equal(7, results.Count(result => !result.Created));
        Assert.Single(results.Select(result => result.Submission.QueueId).Distinct());
        Assert.Single(results.Select(result => result.Submission.IdempotencyKey).Distinct());
        Assert.Single(results.Select(result => result.Submission.FilePath).Distinct());
        Assert.Single(await SubmissionQueueStore.LoadAsync(CancellationToken.None));
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "submission-spool")));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "submission-spool"), "*", SearchOption.AllDirectories));
        Assert.Equal(0, SubmissionQueueStore.ActiveBusinessKeyLockCount);
    }

    [Fact]
    public async Task SequentialPrepareAndDifferentSelectedFile_ReturnExistingWithoutCopyOrOverwrite()
    {
        using var fixture = new QueueStorageFixture();
        var firstSource = fixture.CreateArchive("first.zip", 0x21);
        var secondSource = fixture.CreateArchive("second.zip", 0x42);
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        var first = await PrepareAsync(firstSource, ownerId, sessionId, participantId);
        var originalBytes = await File.ReadAllBytesAsync(first.Submission.FilePath);
        var second = await PrepareAsync(secondSource, ownerId, sessionId, participantId);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Submission.QueueId, second.Submission.QueueId);
        Assert.Equal(first.Submission.IdempotencyKey, second.Submission.IdempotencyKey);
        Assert.Equal(first.Submission.FilePath, second.Submission.FilePath);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(second.Submission.FilePath));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "submission-spool"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TerminalQueueAllowsNewIdentityAndKeepsOldSpoolHistory()
    {
        using var fixture = new QueueStorageFixture();
        var source = fixture.CreateArchive("answer.zip", 0x31);
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var first = await PrepareAsync(source, ownerId, sessionId, participantId);
        await SubmissionQueueStore.SaveAsync(
            first.Submission with { QueueStatus = SubmissionQueueStatus.FailedPermanent },
            CancellationToken.None);

        var second = await PrepareAsync(source, ownerId, sessionId, participantId);
        var persisted = await SubmissionQueueStore.LoadAsync(CancellationToken.None);

        Assert.True(second.Created);
        Assert.NotEqual(first.Submission.QueueId, second.Submission.QueueId);
        Assert.NotEqual(first.Submission.IdempotencyKey, second.Submission.IdempotencyKey);
        Assert.NotEqual(first.Submission.FilePath, second.Submission.FilePath);
        Assert.True(File.Exists(first.Submission.FilePath));
        Assert.True(File.Exists(second.Submission.FilePath));
        Assert.Equal(2, persisted.Count);
        Assert.False(SubmissionQueueStore.IsActiveQueue(
            first.Submission with { QueueStatus = SubmissionQueueStatus.FailedPermanent }));
        Assert.True(SubmissionQueueStore.IsActiveQueue(second.Submission));
    }

    [Fact]
    public async Task PersistentActiveQueueSurvivesStoreAndViewModelRecreation()
    {
        using var fixture = new QueueStorageFixture();
        var source = fixture.CreateArchive("answer.zip", 0x51);
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var prepared = await PrepareAsync(source, ownerId, sessionId, participantId);
        var state = EligibleState(sessionId, participantId);
        var recovery = new RecordingRecoveryService();

        using (SubmissionQueueStore.UseStorageRootForTests(fixture.Root))
        using (var viewModel = new StudentSubmissionViewModel(
            new BackendClient("http://localhost:5048"),
            state,
            new AppAuthSessionState(Path.Combine(fixture.Root, "auth-recreated.bin")),
            recovery))
        {
            await viewModel.InitializeAsync(CancellationToken.None);

            Assert.Equal(prepared.Submission.QueueId, viewModel.QueueId);
            Assert.Equal(prepared.Submission.FilePath, viewModel.SelectedPath);
            Assert.False(viewModel.SubmitCommand.CanExecute(null));
            Assert.Equal(1, recovery.TriggerCount);
            Assert.Equal(1, recovery.ActiveProgressSubscribers);
        }
        Assert.Equal(0, recovery.ActiveProgressSubscribers);
    }

    [Fact]
    public async Task DifferentSessionOrParticipantCreatesIndependentQueues()
    {
        using var fixture = new QueueStorageFixture();
        var source = fixture.CreateArchive("answer.zip", 0x61);
        var ownerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        var results = await Task.WhenAll(
            PrepareAsync(source, ownerId, sessionId, participantId),
            PrepareAsync(source, ownerId, sessionId, Guid.NewGuid()),
            PrepareAsync(source, ownerId, Guid.NewGuid(), participantId));

        Assert.All(results, result => Assert.True(result.Created));
        Assert.Equal(3, results.Select(result => result.Submission.QueueId).Distinct().Count());
        Assert.Equal(3, (await SubmissionQueueStore.LoadAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public void CompletedWithoutReceiptRemainsActiveButReceiptAndFailureAreTerminal()
    {
        var completedWithoutReceipt = SubmissionProgressSnapshotTests.QueueItem(
            SubmissionQueueStatus.Completed);
        var completedWithReceipt = SubmissionProgressSnapshotTests.QueueItem(
            SubmissionQueueStatus.Completed,
            receiptReceived: true);

        Assert.True(SubmissionQueueStore.IsActiveQueue(completedWithoutReceipt));
        Assert.False(SubmissionQueueStore.IsActiveQueue(completedWithReceipt));
        Assert.False(SubmissionQueueStore.IsActiveQueue(
            SubmissionProgressSnapshotTests.QueueItem(SubmissionQueueStatus.Expired)));
        Assert.False(SubmissionQueueStore.IsActiveQueue(
            SubmissionProgressSnapshotTests.QueueItem(SubmissionQueueStatus.FailedPermanent)));
    }

    private static Task<SubmissionPreparationResult> PrepareAsync(
        string source,
        Guid ownerId,
        Guid sessionId,
        Guid participantId) =>
        SubmissionQueueStore.PrepareOrGetActiveAsync(
            source,
            "http://localhost:5048",
            ownerId,
            "HS001",
            sessionId,
            participantId,
            "ROOM42",
            SessionAccessMode.LanOnly,
            "server-1",
            "participant-token",
            CancellationToken.None);

    private static StudentSessionState EligibleState(Guid sessionId, Guid participantId) => new()
    {
        SessionId = sessionId,
        ParticipantId = participantId,
        ParticipantStatus = ParticipantStatus.Approved,
        SessionStatus = SessionStatus.InProgress,
        DeliveryType = ExamDeliveryType.FileSubmission,
        SubmissionStatus = SubmissionStatus.NotStarted
    };
}

[Collection("Submission queue storage")]
public sealed class StudentSubmissionSingleFlightViewModelTests
{
    [Fact]
    public async Task SubmitAsync_RechecksPolicyAndDoesNotPrepareAfterSessionTurnsTerminal()
    {
        using var fixture = new QueueStorageFixture();
        var source = fixture.CreateArchive("answer.zip", 0x71);
        var state = EligibleState();
        var recovery = new RecordingRecoveryService();
        using var viewModel = CreateViewModel(fixture, state, recovery, authenticated: false);
        SelectValidFile(viewModel, source);
        Assert.True(viewModel.SubmitCommand.CanExecute(null));

        state.SessionStatus = SessionStatus.Finished;
        await viewModel.SubmitAsync();

        Assert.False(viewModel.SubmitCommand.CanExecute(null));
        Assert.Empty(await SubmissionQueueStore.LoadAsync(CancellationToken.None));
        Assert.Equal(0, recovery.TriggerCount);
    }

    [Fact]
    public async Task ImmediateAndSequentialSubmitCreateOneQueueTriggerAndSubscription()
    {
        using var fixture = new QueueStorageFixture();
        var source = fixture.CreateArchive("answer.zip", 0x81);
        var state = EligibleState();
        var recovery = new RecordingRecoveryService();
        using var viewModel = CreateViewModel(fixture, state, recovery, authenticated: true);
        SelectValidFile(viewModel, source);

        await Task.WhenAll(
            viewModel.SubmitAsync(),
            viewModel.SubmitAsync(),
            viewModel.SubmitAsync());
        var first = Assert.Single(await SubmissionQueueStore.LoadAsync(CancellationToken.None));
        await viewModel.SubmitAsync();
        var afterSequential = Assert.Single(await SubmissionQueueStore.LoadAsync(CancellationToken.None));

        Assert.Equal(first.QueueId, afterSequential.QueueId);
        Assert.Equal(first.IdempotencyKey, afterSequential.IdempotencyKey);
        Assert.Equal(first.FilePath, afterSequential.FilePath);
        Assert.Equal(1, recovery.TriggerCount);
        Assert.Equal(1, recovery.ActiveProgressSubscribers);
        Assert.Equal(1, recovery.MaximumProgressSubscribers);
        Assert.False(viewModel.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task PersistentActiveRace_AttachesExistingWithoutRecoveryTriggerOrSpoolReplacement()
    {
        using var fixture = new QueueStorageFixture();
        var firstSource = fixture.CreateArchive("first.zip", 0x91);
        var secondSource = fixture.CreateArchive("second.zip", 0xA2);
        var state = EligibleState();
        var ownerId = Guid.NewGuid();
        var existing = await SubmissionQueueStore.PrepareOrGetActiveAsync(
            firstSource,
            "http://localhost:5048",
            ownerId,
            "HS001",
            state.SessionId!.Value,
            state.ParticipantId!.Value,
            "ROOM42",
            SessionAccessMode.LanOnly,
            "server-1",
            "participant-token",
            CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(existing.Submission.FilePath);
        var recovery = new RecordingRecoveryService();
        using var viewModel = CreateViewModel(fixture, state, recovery, authenticated: true, userId: ownerId);
        SelectValidFile(viewModel, secondSource);

        await viewModel.SubmitAsync();

        Assert.Equal(existing.Submission.QueueId, viewModel.QueueId);
        Assert.Equal(existing.Submission.FilePath, viewModel.SelectedPath);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(existing.Submission.FilePath));
        Assert.Equal(0, recovery.TriggerCount);
        Assert.Equal(1, recovery.ActiveProgressSubscribers);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "submission-spool"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void AuthorityAndLifecycleChangesRaiseAndRecomputeCanExecute()
    {
        using var fixture = new QueueStorageFixture();
        var source = fixture.CreateArchive("answer.zip", 0xB3);
        var state = EligibleState();
        state.SubmissionStatus = SubmissionStatus.Submitted;
        var recovery = new RecordingRecoveryService();
        using var viewModel = CreateViewModel(fixture, state, recovery, authenticated: false);
        SelectValidFile(viewModel, source);
        var notifications = 0;
        viewModel.SubmitCommand.CanExecuteChanged += (_, _) => notifications++;

        Assert.False(viewModel.SubmitCommand.CanExecute(null));
        state.ApplyResubmitAuthority(true);
        Assert.True(viewModel.SubmitCommand.CanExecute(null));
        state.SessionStatus = SessionStatus.Archived;
        Assert.False(viewModel.SubmitCommand.CanExecute(null));

        Assert.True(notifications >= 2);
    }

    [Fact]
    public void CompletedReceiptBlocksUntilAuthoritativeResubmitIsAllowed()
    {
        using var fixture = new QueueStorageFixture();
        var source = fixture.CreateArchive("answer.zip", 0xC4);
        var state = EligibleState();
        var recovery = new RecordingRecoveryService();
        using var viewModel = CreateViewModel(fixture, state, recovery, authenticated: false);
        SelectValidFile(viewModel, source);
        viewModel.TrackQueue(SubmissionProgressSnapshotTests.QueueItem(
            SubmissionQueueStatus.Completed,
            receiptReceived: true));

        Assert.Equal(100d, viewModel.Progress);
        Assert.False(viewModel.SubmitCommand.CanExecute(null));
        state.ApplyResubmitAuthority(true);
        Assert.True(viewModel.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task CompletedQueue_DeniesWithoutAuthorityThenCreatesOneNewResubmitIdentity()
    {
        using var fixture = new QueueStorageFixture();
        var firstSource = fixture.CreateArchive("first.zip", 0xD5);
        var secondSource = fixture.CreateArchive("second.zip", 0xE6);
        var state = EligibleState();
        state.SubmissionStatus = SubmissionStatus.Submitted;
        var ownerId = Guid.NewGuid();
        var first = await SubmissionQueueStore.PrepareOrGetActiveAsync(
            firstSource,
            "http://localhost:5048",
            ownerId,
            "HS001",
            state.SessionId!.Value,
            state.ParticipantId!.Value,
            "ROOM42",
            SessionAccessMode.LanOnly,
            "server-1",
            "participant-token",
            CancellationToken.None);
        var completed = first.Submission with
        {
            QueueStatus = SubmissionQueueStatus.Completed,
            ReceiptReceived = true,
            CompletedAtUtc = DateTimeOffset.UtcNow
        };
        await SubmissionQueueStore.SaveAsync(completed, CancellationToken.None);
        var receipt = new ReceiptDto(
            Guid.NewGuid(),
            $"receipt-{Guid.NewGuid():N}",
            "signature",
            DateTimeOffset.UtcNow,
            false,
            []);
        await SubmissionQueueStore.StoreReceiptAsync(receipt, CancellationToken.None);
        var receiptPath = Path.Combine(
            fixture.Root,
            "receipts",
            $"receipt-{receipt.ReceiptCode}.json");
        var recovery = new RecordingRecoveryService();
        using var viewModel = CreateViewModel(
            fixture,
            state,
            recovery,
            authenticated: true,
            userId: ownerId);
        SelectValidFile(viewModel, secondSource);
        viewModel.TrackQueue(completed);

        await viewModel.SubmitAsync();
        Assert.Single(await SubmissionQueueStore.LoadAsync(CancellationToken.None));
        Assert.Equal(0, recovery.TriggerCount);

        state.ApplyResubmitAuthority(true);
        await viewModel.SubmitAsync();
        var persisted = await SubmissionQueueStore.LoadAsync(CancellationToken.None);
        var resubmit = Assert.Single(
            persisted,
            item => item.QueueId != completed.QueueId);

        Assert.Equal(2, persisted.Count);
        Assert.NotEqual(completed.IdempotencyKey, resubmit.IdempotencyKey);
        Assert.NotEqual(completed.FilePath, resubmit.FilePath);
        Assert.True(File.Exists(completed.FilePath));
        Assert.True(File.Exists(receiptPath));
        Assert.Equal(1, recovery.TriggerCount);
        Assert.Equal(1, recovery.ActiveProgressSubscribers);
    }

    private static StudentSubmissionViewModel CreateViewModel(
        QueueStorageFixture fixture,
        StudentSessionState state,
        RecordingRecoveryService recovery,
        bool authenticated,
        Guid? userId = null)
    {
        var auth = new AppAuthSessionState(Path.Combine(
            fixture.Root,
            $"auth-{Guid.NewGuid():N}.bin"));
        if (authenticated)
        {
            var effectiveUserId = userId ?? Guid.NewGuid();
            auth.SetAuthenticated(
                new CurrentAccountDto(
                    effectiveUserId,
                    "HS001",
                    "hs001@example.test",
                    "Học sinh",
                    "HS001",
                    UserRole.Student,
                    Guid.NewGuid().ToString("D"),
                    Guid.NewGuid(),
                    "device-1",
                    DateTimeOffset.UtcNow.AddHours(1),
                    ProviderUserId: effectiveUserId.ToString("D")),
                "test-access-token");
        }

        return new StudentSubmissionViewModel(
            new BackendClient("http://localhost:5048"),
            state,
            auth,
            recovery);
    }

    private static StudentSessionState EligibleState() => new()
    {
        SessionId = Guid.NewGuid(),
        ParticipantId = Guid.NewGuid(),
        ParticipantStatus = ParticipantStatus.Approved,
        SessionStatus = SessionStatus.InProgress,
        DeliveryType = ExamDeliveryType.FileSubmission,
        SubmissionStatus = SubmissionStatus.NotStarted
    };

    private static void SelectValidFile(StudentSubmissionViewModel viewModel, string path)
    {
        SetProperty(viewModel, nameof(StudentSubmissionViewModel.SelectedPath), path);
        SetProperty(viewModel, nameof(StudentSubmissionViewModel.IsFileValid), true);
    }

    private static void SetProperty<T>(object target, string name, T value) =>
        target.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(target, value);
}

internal sealed class QueueStorageFixture : IDisposable
{
    private readonly IDisposable scope;

    public QueueStorageFixture()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "examtransfer-03d-r1",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        scope = SubmissionQueueStore.UseStorageRootForTests(Root);
    }

    public string Root { get; }

    public string CreateArchive(string name, byte marker)
    {
        var path = Path.Combine(Root, $"source-{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, marker]);
        return path;
    }

    public void Dispose()
    {
        scope.Dispose();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

internal sealed class RecordingRecoveryService : ISubmissionRecoveryService
{
    private EventHandler<SubmissionProgressSnapshot>? progressChanged;

    public int PendingCount => 0;
    public int TriggerCount { get; private set; }
    public int ActiveProgressSubscribers { get; private set; }
    public int MaximumProgressSubscribers { get; private set; }
    public event EventHandler<int>? PendingCountChanged
    {
        add { }
        remove { }
    }
    public event EventHandler<SubmissionProgressSnapshot>? ProgressChanged
    {
        add
        {
            progressChanged += value;
            ActiveProgressSubscribers++;
            MaximumProgressSubscribers = Math.Max(
                MaximumProgressSubscribers,
                ActiveProgressSubscribers);
        }
        remove
        {
            progressChanged -= value;
            ActiveProgressSubscribers--;
        }
    }

    public void Start() { }
    public void Trigger() => TriggerCount++;
    public void Dispose() => progressChanged = null;
}
