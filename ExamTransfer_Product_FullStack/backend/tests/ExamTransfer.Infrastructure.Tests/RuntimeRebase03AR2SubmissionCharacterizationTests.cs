using System.Data.Common;
using System.Security.Cryptography;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Execution.Dispatch;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed partial class RuntimeRebase03AR2SubmissionCharacterizationTests
{
    private static readonly byte[] EmptyZip =
    [
        0x50, 0x4B, 0x05, 0x06,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0
    ];

    [Fact]
    public async Task EndSessionBetweenInitAndUpload_BlocksChunkWithoutSideEffects()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var service = CreateService(database.Context);
        var initialized = await service.Service.InitAsync(
            Request(seed, $"end-mid-upload-{Guid.NewGuid():N}"),
            CancellationToken.None);

        seed.Session.Status = SessionStatus.Finished;
        seed.Session.EndedAtUtc = DateTimeOffset.UtcNow;
        await database.Context.SaveChangesAsync();

        var temporaryPath = await database.Context.SubmissionFilesSet
            .Where(x => x.Id == initialized.FilePlans.Single().FileId)
            .Select(x => x.TemporaryPath)
            .SingleAsync();
        await using var content = new MemoryStream(EmptyZip, writable: false);
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            service.Service.UploadChunkAsync(
                initialized.SubmissionId,
                initialized.FilePlans.Single().FileId,
                0,
                content,
                content.Length,
                null,
                CancellationToken.None));

        database.Context.ChangeTracker.Clear();
        var persisted = await database.Context.SubmissionsSet
            .Include(x => x.Participant)
            .SingleAsync(x => x.Id == initialized.SubmissionId);
        Assert.Equal(ErrorCodes.SessionSubmissionNotOpen, error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(SessionStatus.Finished, seed.Session.Status);
        Assert.Equal(SubmissionStatus.Uploading, persisted.Status);
        Assert.Equal(SubmissionStatus.Uploading, persisted.Participant.SubmissionStatus);
        Assert.False(Directory.Exists(temporaryPath));
        Assert.False(Directory.Exists(service.Paths.ReceiptRoot(seed.Session.Id)));
    }

    [Theory]
    [InlineData(SessionStatus.Waiting)]
    [InlineData(SessionStatus.Finished)]
    [InlineData(SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Archived)]
    public async Task InitAfterTerminalState_IsRejected(SessionStatus terminal)
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, terminal);
        var service = CreateService(database.Context);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            service.Service.InitAsync(
                Request(seed, $"terminal-{terminal}-{Guid.NewGuid():N}"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.SessionSubmissionNotOpen, error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(await database.Context.SubmissionsSet.ToListAsync());
        Assert.Equal(SubmissionStatus.NotStarted, seed.Participant.SubmissionStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentInit_ReturnsIdempotentResultOrTypedConflict(
        bool sameIdempotencyKey)
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var sessionId = seed.Session.Id;
        var participantId = seed.Participant.Id;
        database.Context.ChangeTracker.Clear();

        var barrier = new MaxAttemptBarrier(2);
        await using var firstContext = database.CreateContext(barrier);
        await using var secondContext = database.CreateContext(barrier);
        var first = CreateService(firstContext).Service;
        var second = CreateService(secondContext).Service;
        var sharedKey = $"concurrent-{Guid.NewGuid():N}";
        var firstRequest = Request(
            sessionId,
            participantId,
            sharedKey);
        var secondRequest = Request(
            sessionId,
            participantId,
            sameIdempotencyKey
                ? sharedKey
                : $"concurrent-{Guid.NewGuid():N}");

        var outcomes = await Task.WhenAll(
            CaptureAsync(() => first.InitAsync(firstRequest, CancellationToken.None)),
            CaptureAsync(() => second.InitAsync(secondRequest, CancellationToken.None)));

        if (sameIdempotencyKey)
        {
            Assert.All(outcomes, x => Assert.Null(x.Exception));
            Assert.Single(outcomes.Select(x => x.Response!.SubmissionId).Distinct());
            Assert.All(outcomes, x => Assert.Equal(1, x.Response!.AttemptNumber));
        }
        else
        {
            var success = Assert.Single(outcomes, x => x.Response is not null);
            var failure = Assert.Single(outcomes, x => x.Exception is not null);
            Assert.Equal(1, success.Response!.AttemptNumber);
            var error = Assert.IsType<ApiException>(failure.Exception);
            Assert.Equal(ErrorCodes.SubmissionAlreadyProcessing, error.Code);
            Assert.Equal(409, error.StatusCode);
        }

        await using var verify = database.CreateContext();
        var persisted = Assert.Single(await verify.SubmissionsSet
            .Where(x => x.ParticipantId == participantId)
            .ToListAsync());
        Assert.Equal(sessionId, persisted.SessionId);
        Assert.Equal(1, persisted.AttemptNumber);
        Assert.Equal(1, await verify.SubmissionsSet.CountAsync(
            x => x.ParticipantId == participantId
                && x.Status == SubmissionStatus.Uploading));
    }

    [Fact]
    public async Task FinalizeTwice_ReturnsSameReceiptAndKeepsOneReceiptArtifact()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var service = CreateService(database.Context);
        var initialized = await service.Service.InitAsync(
            Request(seed, $"finalize-twice-{Guid.NewGuid():N}"),
            CancellationToken.None);
        await using (var content = new MemoryStream(EmptyZip, writable: false))
        {
            await service.Service.UploadChunkAsync(
                initialized.SubmissionId,
                initialized.FilePlans.Single().FileId,
                0,
                content,
                content.Length,
                null,
                CancellationToken.None);
        }

        var first = await service.Service.FinalizeAsync(
            initialized.SubmissionId,
            new("first"),
            CancellationToken.None);
        var second = await service.Service.FinalizeAsync(
            initialized.SubmissionId,
            new("second"),
            CancellationToken.None);

        Assert.Equal(first.ReceiptCode, second.ReceiptCode);
        Assert.Equal(first.ReceiptSignature, second.ReceiptSignature);
        Assert.Equal(first.ServerReceivedAtUtc, second.ServerReceivedAtUtc);
        Assert.Single(Directory.EnumerateFiles(
            service.Paths.ReceiptRoot(seed.Session.Id),
            "*.json",
            SearchOption.TopDirectoryOnly));
        Assert.Equal(1, await database.Context.SubmissionsSet.CountAsync(
            x => x.Id == initialized.SubmissionId
                && x.ReceiptCode == first.ReceiptCode));
    }

    private static async Task<InitOutcome> CaptureAsync(
        Func<Task<InitSubmissionResponse>> action)
    {
        try
        {
            return new(await action(), null);
        }
        catch (Exception ex)
        {
            return new(null, ex);
        }
    }

    private static async Task<Seed> SeedAsync(
        AppDbContext db,
        SessionStatus status)
    {
        var exam = new Exam
        {
            Title = "Runtime rebase submission",
            Subject = "Characterization",
            DurationMinutes = 60,
            Status = ExamStatus.Published,
            DeliveryType = ExamDeliveryType.FileSubmission,
            Version = 1
        };
        var now = DateTimeOffset.UtcNow;
        var session = new ExamSession
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = $"RR{Random.Shared.Next(100000, 999999)}",
            HostDeviceId = "runtime-rebase-host",
            Status = status,
            AccessMode = SessionAccessMode.LanOnly,
            DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission,
            ExamVersionSnapshot = exam.Version,
            StartedAtUtc = now.AddMinutes(-5),
            EndedAtUtc = status is SessionStatus.Finished
                or SessionStatus.Cancelled
                or SessionStatus.Archived
                    ? now
                    : null,
            Sequence = 1
        };
        var participant = new SessionParticipant
        {
            Session = session,
            SessionId = session.Id,
            StudentCode = $"RR-{Guid.NewGuid():N}"[..12],
            DisplayName = "Runtime Student",
            DeviceId = "runtime-device",
            MachineName = "runtime-machine",
            AppVersion = "characterization",
            Status = ParticipantStatus.Approved,
            ApprovedAtUtc = now.AddMinutes(-5),
            SubmissionStatus = SubmissionStatus.NotStarted,
            SourceMode = "Lan"
        };
        db.AddRange(exam, session, participant);
        await db.SaveChangesAsync();
        return new(session, participant);
    }

    private static InitSubmissionRequest Request(
        Seed seed,
        string idempotencyKey) =>
        Request(seed.Session.Id, seed.Participant.Id, idempotencyKey);

    private static InitSubmissionRequest Request(
        Guid sessionId,
        Guid participantId,
        string idempotencyKey) =>
        new(
            sessionId,
            participantId,
            idempotencyKey,
            [
                new(
                    "runtime-client-file",
                    "answer.zip",
                    EmptyZip.Length,
                    Convert.ToHexString(SHA256.HashData(EmptyZip)).ToLowerInvariant(),
                    "application/zip")
            ],
            DateTimeOffset.UtcNow);

    private static SubmissionHarness CreateService(AppDbContext db)
    {
        var root = Path.Combine(
            Path.GetDirectoryName(db.Database.GetDbConnection().DataSource)!,
            "runtime-submission-storage");
        var paths = new RuntimeStoragePaths(root);
        paths.EnsureCreated();
        var options = Options.Create(new ExamTransferOptions());
        var dispatcher = new SubmissionMutationDispatcher(
            db,
            [
                new NoOpMutationHandler(SessionAccessMode.LanOnly),
                new NoOpMutationHandler(SessionAccessMode.PublicCloud)
            ]);
        return new(
            new SubmissionService(
                db,
                paths,
                new ChunkStorage(),
                new ReceiptSigner(options),
                new NoOpAudit(),
                new NoOpOutbox(),
                new NoOpRealtime(),
                options,
                dispatcher),
            paths);
    }

    private sealed record Seed(
        ExamSession Session,
        SessionParticipant Participant);

    private sealed record InitOutcome(
        InitSubmissionResponse? Response,
        Exception? Exception);

    private sealed record SubmissionHarness(
        SubmissionService Service,
        RuntimeStoragePaths Paths);

    private sealed class MaxAttemptBarrier(int participants) : DbCommandInterceptor
    {
        private readonly CountdownEvent countdown = new(participants);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MAX(", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(
                    "AttemptNumber",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (countdown.Signal())
                    release.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReaderExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }

    private sealed class RuntimeDatabase(
        string directory,
        string path,
        AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;

        public static async Task<RuntimeDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "ExamTransfer.RuntimeRebase03A",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "runtime-rebase.db");
            var context = CreateContext(path);
            await context.Database.EnsureCreatedAsync();
            return new(directory, path, context);
        }

        public AppDbContext CreateContext(
            IInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout=5");
            if (interceptor is not null)
                builder.AddInterceptors(interceptor);
            return new(builder.Options);
        }

        private static AppDbContext CreateContext(string path) =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={path};Default Timeout=5")
                    .Options);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
                // Test cleanup is best effort only.
            }
        }
    }

    private sealed class RuntimeStoragePaths(string root) : IStoragePaths
    {
        public string RootPath { get; } = root;
        public string DatabasePath => Path.Combine(RootPath, "database", "runtime.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) =>
            Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) =>
            Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(
            Guid sessionId,
            string studentCode,
            Guid submissionId) =>
            Path.Combine(
                SessionRoot(sessionId),
                "submissions",
                studentCode,
                submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) =>
            Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }

    private sealed class NoOpMutationHandler(
        SessionAccessMode accessMode) : ISubmissionMutationHandler
    {
        public SessionAccessMode AccessMode { get; } = accessMode;
        public Task RejectAsync(
            Submission submission,
            RejectSubmissionRequest request,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task AllowResubmitAsync(
            SessionParticipant participant,
            AllowResubmitRequest request,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoOpAudit : IAuditService
    {
        public Task WriteAsync(
            string action,
            string entityType,
            string? entityId,
            Guid? sessionId,
            object? before,
            object? after,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpOutbox : IOutboxService
    {
        public Task EnqueueAsync(
            string entityType,
            string entityId,
            string operation,
            object payload,
            string? filePath = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpRealtime : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
