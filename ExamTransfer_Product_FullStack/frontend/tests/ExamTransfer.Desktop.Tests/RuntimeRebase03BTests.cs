using System.IO;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class FrontendLoggerWarningTests
{
    [Fact]
    public void LogWarning_UsesWarningSinkWithCurrentContextAndNoDialogPath()
    {
        var previousMode = FrontendLogger.CurrentMode;
        var previousPage = FrontendLogger.CurrentPageKey;
        try
        {
            FrontendLogger.SetContext("Student", "S-07");
            string? entry = null;
            var writes = 0;

            FrontendLogger.LogWarning(
                "queue warning",
                "RuntimeRebase03B",
                value =>
                {
                    writes++;
                    entry = value;
                });

            Assert.Equal(1, writes);
            Assert.NotNull(entry);
            Assert.Contains("timestamp_utc:", entry, StringComparison.Ordinal);
            Assert.Contains("level: Warning", entry, StringComparison.Ordinal);
            Assert.Contains("source: RuntimeRebase03B", entry, StringComparison.Ordinal);
            Assert.Contains("mode: Student", entry, StringComparison.Ordinal);
            Assert.Contains("page_key: S-07", entry, StringComparison.Ordinal);
            Assert.Contains("message: queue warning", entry, StringComparison.Ordinal);
        }
        finally
        {
            FrontendLogger.SetContext(previousMode, previousPage);
        }
    }

    [Fact]
    public void LogWarning_RecoverableSinkFailureDoesNotEscape()
    {
        var error = Record.Exception(() => FrontendLogger.LogWarning(
            "queue warning",
            "RuntimeRebase03B",
            _ => throw new IOException("fixture sink unavailable")));

        Assert.Null(error);
    }
}

public sealed class SubmissionProgressSnapshotTests
{
    [Fact]
    public void Prepared_IsNonTerminalAndUsesPreparedStage()
    {
        var snapshot = SubmissionQueueStore.CreateProgressSnapshot(
            QueueItem(SubmissionQueueStatus.Prepared));

        Assert.Equal(10d, snapshot.ProgressPercent);
        Assert.False(snapshot.IsCompleted);
        Assert.False(snapshot.IsTerminal);
    }

    [Fact]
    public void Uploading_UsesPersistedChunkCounts()
    {
        var queueId = Guid.NewGuid();
        var item = QueueItem(
            SubmissionQueueStatus.Uploading,
            queueId: queueId,
            submissionId: Guid.NewGuid(),
            chunkSizeBytes: 100,
            sizeBytes: 400,
            missingChunks: [2, 3]);

        var snapshot = SubmissionQueueStore.CreateProgressSnapshot(item);

        Assert.Equal(queueId, snapshot.QueueId);
        Assert.Equal(2, snapshot.CompletedChunks);
        Assert.Equal(4, snapshot.TotalChunks);
        Assert.Equal(50d, snapshot.ProgressPercent);
        Assert.Equal("fixed-idempotency-key", item.IdempotencyKey);
        Assert.Equal("fixed-spool.zip", item.FilePath);
    }

    [Fact]
    public void Finalizing_DoesNotReportCompleted()
    {
        var snapshot = SubmissionQueueStore.CreateProgressSnapshot(
            QueueItem(
                SubmissionQueueStatus.Finalizing,
                submissionId: Guid.NewGuid(),
                finalizeRequested: true));

        Assert.Equal(90d, snapshot.ProgressPercent);
        Assert.False(snapshot.IsCompleted);
        Assert.False(snapshot.IsTerminal);
    }

    [Fact]
    public void Completed_RequiresReceiptBeforeOneHundredPercent()
    {
        var withoutReceipt = SubmissionQueueStore.CreateProgressSnapshot(
            QueueItem(SubmissionQueueStatus.Completed));
        var withReceipt = SubmissionQueueStore.CreateProgressSnapshot(
            QueueItem(
                SubmissionQueueStatus.Completed,
                receiptReceived: true));

        Assert.Equal(95d, withoutReceipt.ProgressPercent);
        Assert.False(withoutReceipt.IsCompleted);
        Assert.False(withoutReceipt.IsTerminal);
        Assert.Equal(100d, withReceipt.ProgressPercent);
        Assert.True(withReceipt.IsCompleted);
        Assert.True(withReceipt.IsTerminal);
    }

    [Fact]
    public void Failed_KeepsRealChunkProgressAndLastError()
    {
        var snapshot = SubmissionQueueStore.CreateProgressSnapshot(
            QueueItem(
                SubmissionQueueStatus.FailedPermanent,
                submissionId: Guid.NewGuid(),
                chunkSizeBytes: 100,
                sizeBytes: 400,
                missingChunks: [2, 3],
                lastError: "fixture failure"));

        Assert.Equal(50d, snapshot.ProgressPercent);
        Assert.NotEqual(100d, snapshot.ProgressPercent);
        Assert.Equal("fixture failure", snapshot.LastError);
        Assert.False(snapshot.IsCompleted);
        Assert.True(snapshot.IsTerminal);
    }

    internal static PendingSubmission QueueItem(
        SubmissionQueueStatus status,
        Guid? queueId = null,
        Guid? submissionId = null,
        int chunkSizeBytes = 0,
        long sizeBytes = 400,
        IReadOnlyList<int>? missingChunks = null,
        bool finalizeRequested = false,
        bool receiptReceived = false,
        string? lastError = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PendingSubmission(
            QueueId: queueId ?? Guid.NewGuid(),
            Endpoint: "http://localhost:5048",
            SessionId: Guid.NewGuid(),
            ParticipantId: Guid.NewGuid(),
            ProtectedToken: string.Empty,
            FilePath: "fixed-spool.zip",
            FileName: "answer.zip",
            SizeBytes: sizeBytes,
            Sha256: new string('a', 64),
            IdempotencyKey: "fixed-idempotency-key",
            SubmissionId: submissionId,
            ServerFileId: submissionId.HasValue ? Guid.NewGuid() : null,
            ChunkSizeBytes: chunkSizeBytes,
            MissingChunks: missingChunks ?? [],
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            QueueStatus: status,
            LastError: lastError,
            FinalizeRequested: finalizeRequested,
            ReceiptReceived: receiptReceived);
    }
}

[Collection("WPF bulk archive")]
public sealed class StudentSubmissionProgressLifecycleTests
{
    [Fact]
    public async Task EventSnapshot_UpdatesOnDispatcherAndStopsAtTerminal()
    {
        var recovery = new RecordingRecoveryService();
        StudentSubmissionViewModel? viewModel = null;
        var queueId = Guid.NewGuid();
        var uiThread = 0;
        var updateThread = 0;
        using var updated = new ManualResetEventSlim();

        WpfTestHost.Run(() =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            viewModel = new StudentSubmissionViewModel(
                new BackendClient("http://localhost:5048"),
                new StudentSessionState(),
                new AppAuthSessionState(),
                recovery);
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(StudentSubmissionViewModel.Progress)
                    && viewModel.Progress == 50d)
                {
                    updateThread = Environment.CurrentManagedThreadId;
                    updated.Set();
                }
            };
            viewModel.TrackQueue(SubmissionProgressSnapshotTests.QueueItem(
                SubmissionQueueStatus.Prepared,
                queueId: queueId));
        });

        Assert.Equal(1, recovery.ActiveProgressSubscribers);
        await Task.Run(() => recovery.Publish(
            SubmissionQueueStore.CreateProgressSnapshot(
                SubmissionProgressSnapshotTests.QueueItem(
                    SubmissionQueueStatus.Uploading,
                    queueId: queueId,
                    submissionId: Guid.NewGuid(),
                    chunkSizeBytes: 100,
                    sizeBytes: 400,
                    missingChunks: [2, 3]))));
        Assert.True(updated.Wait(TimeSpan.FromSeconds(5)));

        WpfTestHost.Run(() =>
        {
            Assert.NotNull(viewModel);
            Assert.Equal(50d, viewModel.Progress);
            Assert.Equal(uiThread, updateThread);

            recovery.Publish(SubmissionQueueStore.CreateProgressSnapshot(
                SubmissionProgressSnapshotTests.QueueItem(
                    SubmissionQueueStatus.Completed,
                    queueId: queueId,
                    receiptReceived: true)));
            Assert.Equal(100d, viewModel.Progress);
            Assert.Equal("success", viewModel.StatusTone);
            Assert.Equal(0, recovery.ActiveProgressSubscribers);

            recovery.Publish(SubmissionQueueStore.CreateProgressSnapshot(
                SubmissionProgressSnapshotTests.QueueItem(
                    SubmissionQueueStatus.FailedPermanent,
                    queueId: queueId,
                    lastError: "late fixture")));
            Assert.Equal(100d, viewModel.Progress);
            viewModel.Dispose();
        });
    }

    [Fact]
    public void Dispose_RemovesOnlySubscriptionAndIgnoresOtherQueue()
    {
        var recovery = new RecordingRecoveryService();
        var trackedQueueId = Guid.NewGuid();
        using var viewModel = new StudentSubmissionViewModel(
            new BackendClient("http://localhost:5048"),
            new StudentSessionState(),
            new AppAuthSessionState(),
            recovery);
        viewModel.TrackQueue(SubmissionProgressSnapshotTests.QueueItem(
            SubmissionQueueStatus.Prepared,
            queueId: trackedQueueId));

        Assert.Equal(1, recovery.ActiveProgressSubscribers);
        Assert.Equal(trackedQueueId, viewModel.QueueId);
        recovery.Publish(SubmissionQueueStore.CreateProgressSnapshot(
            SubmissionProgressSnapshotTests.QueueItem(
                SubmissionQueueStatus.Uploading,
                queueId: Guid.NewGuid(),
                submissionId: Guid.NewGuid(),
                chunkSizeBytes: 100,
                sizeBytes: 400,
                missingChunks: [])));
        Assert.Equal(10d, viewModel.Progress);

        viewModel.Dispose();

        Assert.Equal(0, recovery.ActiveProgressSubscribers);
        Assert.Equal(1, recovery.MaximumProgressSubscribers);
    }

    private sealed class RecordingRecoveryService : ISubmissionRecoveryService
    {
        private EventHandler<SubmissionProgressSnapshot>? progressChanged;

        public int PendingCount => 0;
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

        public void Publish(SubmissionProgressSnapshot snapshot) =>
            progressChanged?.Invoke(this, snapshot);

        public void Start()
        {
        }

        public void Trigger()
        {
        }

        public void Dispose()
        {
            progressChanged = null;
            ActiveProgressSubscribers = 0;
        }
    }
}
