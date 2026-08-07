using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer.Workers;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class CloudSyncWorkerRuntimeHotfixTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CloudSyncWorker_PublicCloudQuizGraphLargerThanBatch_SinglePostCommitPulse_DrainsToReady()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizGraphAsync(
            database.Context,
            questionCount: 6,
            choicesPerQuestion: 4);
        AddProjectionRows(database.Context, fixture);
        await database.Context.SaveChangesAsync();

        var signal = new CloudSyncSignal();
        var cloud = new RecordingPushCloud();
        var options = WorkerOptions(batchSize: 20);
        await using var provider = CreateProvider(database.Path, cloud);
        using var worker = CreateWorker(provider, options, signal);

        await worker.StartAsync(default);
        signal.Pulse();
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var verify = database.CreateContext();
                return await verify.SyncQueueSet.AllAsync(
                    item => item.Status == SyncStatus.Synced);
            });
        }
        finally
        {
            await worker.StopAsync(default);
        }

        await using var readinessContext = database.CreateContext();
        var readiness = await new PublicCloudProjectionExecution(readinessContext)
            .GetProjectionReadinessAsync(fixture.Session.Id, default);
        Assert.True(readiness.Ready);
        Assert.Equal("PUBLICCLOUD_QUIZ_PROJECTION_READY", readiness.Code);
        Assert.Equal(32, cloud.PushCount);
        Assert.Equal(32, cloud.PushedEntities.Distinct().Count());
    }

    [Fact]
    public async Task CloudSyncWorker_BacklogLargerThanTwoBatches_DrainsBoundedly()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        AddPendingRows(database.Context, count: 105);
        await database.Context.SaveChangesAsync();

        var signal = new CloudSyncSignal();
        var cloud = new RecordingPushCloud();
        var options = WorkerOptions(batchSize: 20);
        await using var provider = CreateProvider(database.Path, cloud);
        using var worker = CreateWorker(provider, options, signal);

        await worker.StartAsync(default);
        signal.Pulse();
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var verify = database.CreateContext();
                return await verify.SyncQueueSet.CountAsync(
                    item => item.Status == SyncStatus.Synced) == 105;
            });
        }
        finally
        {
            await worker.StopAsync(default);
        }

        Assert.Equal(105, cloud.PushCount);
    }

    [Fact]
    public async Task CloudSyncWorker_ExpiredSyncingLease_IsReclaimedButActiveLeaseIsSkipped()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var expired = QueueItem(SyncStatus.Syncing, now.AddMinutes(-2));
        expired.LeaseUntilUtc = now.AddMinutes(-1);
        expired.LastAttemptAtUtc = now.AddMinutes(-2);
        var active = QueueItem(SyncStatus.Syncing, now.AddMinutes(-1));
        active.LeaseUntilUtc = now.AddMinutes(1);
        active.LastAttemptAtUtc = now.AddMinutes(-1);
        database.Context.SyncQueueSet.AddRange(expired, active);
        await database.Context.SaveChangesAsync();

        var signal = new CloudSyncSignal();
        var cloud = new RecordingPushCloud();
        var options = WorkerOptions(batchSize: 20);
        await using var provider = CreateProvider(database.Path, cloud);
        using var worker = CreateWorker(provider, options, signal);

        await worker.StartAsync(default);
        signal.Pulse();
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var verify = database.CreateContext();
                return await verify.SyncQueueSet.AnyAsync(
                    item => item.Id == expired.Id
                        && item.Status == SyncStatus.Synced);
            });
        }
        finally
        {
            await worker.StopAsync(default);
        }

        await using var assertionContext = database.CreateContext();
        var expiredResult = await assertionContext.SyncQueueSet.SingleAsync(
            item => item.Id == expired.Id);
        var activeResult = await assertionContext.SyncQueueSet.SingleAsync(
            item => item.Id == active.Id);
        Assert.Equal(SyncStatus.Synced, expiredResult.Status);
        Assert.Null(expiredResult.LeaseUntilUtc);
        Assert.Equal(SyncStatus.Syncing, activeResult.Status);
        Assert.Equal(active.LeaseUntilUtc, activeResult.LeaseUntilUtc);
        Assert.Single(cloud.PushedEntities);
        Assert.Contains(expired.EntityId, cloud.PushedEntities.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudSyncWorker_HealthFalse_DoesNotPushOrMarkSynced()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var pending = QueueItem(SyncStatus.Pending, DateTimeOffset.UtcNow);
        database.Context.SyncQueueSet.Add(pending);
        await database.Context.SaveChangesAsync();

        var signal = new CloudSyncSignal();
        var cloud = new RecordingPushCloud { HealthResult = false };
        var options = WorkerOptions(batchSize: 20);
        await using var provider = CreateProvider(database.Path, cloud);
        using var worker = CreateWorker(provider, options, signal);

        await worker.StartAsync(default);
        signal.Pulse();
        try
        {
            await WaitUntilAsync(() => Task.FromResult(cloud.HealthChecks == 1));
            await Task.Delay(100);
        }
        finally
        {
            await worker.StopAsync(default);
        }

        await using var assertionContext = database.CreateContext();
        var persisted = await assertionContext.SyncQueueSet.SingleAsync(
            item => item.Id == pending.Id);
        Assert.Equal(1, cloud.HealthChecks);
        Assert.Equal(0, cloud.PushCount);
        Assert.Equal(SyncStatus.Pending, persisted.Status);
        Assert.Null(persisted.LastAttemptAtUtc);
        Assert.Null(persisted.CompletedAtUtc);
    }

    [Fact]
    public async Task CloudSyncWorker_IneligibleRows_DoNotBlockEligibleOrBusyLoop()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var futureRetry = QueueItem(SyncStatus.Failed, now.AddMinutes(-3));
        futureRetry.NextRetryAtUtc = now.AddMinutes(1);
        futureRetry.RetryCount = 3;
        var conflict = QueueItem(SyncStatus.Conflict, now.AddMinutes(-2));
        var sourceOwned = QueueItem(SyncStatus.Pending, now.AddMinutes(-1));
        sourceOwned.EntityType = "quiz_answers";
        sourceOwned.PayloadJson = """{"source_mode":"PublicCloud"}""";
        var eligible = QueueItem(SyncStatus.Pending, now);
        database.Context.SyncQueueSet.AddRange(
            futureRetry,
            conflict,
            sourceOwned,
            eligible);
        await database.Context.SaveChangesAsync();

        var signal = new CloudSyncSignal();
        var cloud = new RecordingPushCloud();
        var options = WorkerOptions(batchSize: 20);
        await using var provider = CreateProvider(database.Path, cloud);
        using var worker = CreateWorker(provider, options, signal);

        await worker.StartAsync(default);
        signal.Pulse();
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var verify = database.CreateContext();
                return await verify.SyncQueueSet.CountAsync(
                    item => item.Status == SyncStatus.Synced) == 2;
            });
            await Task.Delay(100);
        }
        finally
        {
            await worker.StopAsync(default);
        }

        await using var assertionContext = database.CreateContext();
        Assert.Equal(
            SyncStatus.Failed,
            (await assertionContext.SyncQueueSet.SingleAsync(
                item => item.Id == futureRetry.Id)).Status);
        Assert.Equal(
            SyncStatus.Conflict,
            (await assertionContext.SyncQueueSet.SingleAsync(
                item => item.Id == conflict.Id)).Status);
        Assert.Equal(
            SyncStatus.Synced,
            (await assertionContext.SyncQueueSet.SingleAsync(
                item => item.Id == sourceOwned.Id)).Status);
        Assert.Equal(
            SyncStatus.Synced,
            (await assertionContext.SyncQueueSet.SingleAsync(
                item => item.Id == eligible.Id)).Status);
        Assert.Equal(1, cloud.HealthChecks);
        Assert.Equal(1, cloud.PushCount);
        Assert.Contains(eligible.EntityId, cloud.PushedEntities.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudSyncWorker_CancellationDuringDrain_StopsPromptly()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        database.Context.SyncQueueSet.Add(QueueItem(
            SyncStatus.Pending,
            DateTimeOffset.UtcNow));
        await database.Context.SaveChangesAsync();

        var signal = new CloudSyncSignal();
        var cloud = new BlockingPushCloud();
        var options = WorkerOptions(batchSize: 20);
        await using var provider = CreateProvider(database.Path, cloud);
        using var worker = CreateWorker(provider, options, signal);

        await worker.StartAsync(default);
        signal.Pulse();
        await cloud.PushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var elapsed = Stopwatch.StartNew();
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StopAsync(stopTimeout.Token);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
    }

    private static async Task<QuizFixture> SeedQuizGraphAsync(
        AppDbContext db,
        int questionCount,
        int choicesPerQuestion)
    {
        var exam = new Exam
        {
            Title = "Runtime hotfix quiz",
            Subject = "Test",
            DurationMinutes = 45,
            Status = ExamStatus.Published,
            DeliveryType = ExamDeliveryType.MultipleChoice,
            Version = 1
        };
        var session = new ExamSession
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = "HOTFIX",
            Status = SessionStatus.Waiting,
            HostDeviceId = "host",
            AccessMode = SessionAccessMode.PublicCloud,
            AdmissionMode = SessionAdmissionMode.OpenRequest,
            DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice,
            ExamVersionSnapshot = exam.Version,
            AcceptingParticipants = true
        };
        var questions = Enumerable.Range(1, questionCount)
            .Select(index => new QuizQuestion
            {
                Exam = exam,
                ExamId = exam.Id,
                Version = exam.Version,
                Order = index,
                Text = $"Question {index}",
                Points = 1,
                Multiple = false
            })
            .ToList();
        var choices = questions
            .SelectMany(question => Enumerable.Range(1, choicesPerQuestion)
                .Select(index => new QuizChoice
                {
                    Question = question,
                    QuestionId = question.Id,
                    Order = index,
                    Text = $"Choice {index}",
                    IsCorrect = index == 1
                }))
            .ToList();
        db.ExamSessionsSet.Add(session);
        db.QuizQuestionsSet.AddRange(questions);
        db.QuizChoicesSet.AddRange(choices);
        await db.SaveChangesAsync();
        return new(exam, session, questions, choices);
    }

    private static void AddProjectionRows(AppDbContext db, QuizFixture fixture)
    {
        var rows = new List<(string EntityType, Guid EntityId, object Payload)>
        {
            ("exams", fixture.Exam.Id, PublicCloudProjectionPayloads.Exam(fixture.Exam))
        };
        rows.AddRange(fixture.Questions.Select(question =>
            ("quiz_questions", question.Id, PublicCloudProjectionPayloads.Question(question))));
        rows.AddRange(fixture.Choices.Select(choice =>
            ("quiz_choices", choice.Id, PublicCloudProjectionPayloads.Choice(choice))));
        rows.Add((
            "exam_sessions",
            fixture.Session.Id,
            PublicCloudProjectionPayloads.Session(fixture.Session)));

        var createdAtUtc = DateTimeOffset.UtcNow;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            db.SyncQueueSet.Add(new SyncQueueItem
            {
                EntityType = row.EntityType,
                EntityId = row.EntityId.ToString(),
                Operation = "upsert",
                PayloadJson = JsonSerializer.Serialize(row.Payload, JsonOptions),
                Status = SyncStatus.Pending,
                NextRetryAtUtc = createdAtUtc,
                CreatedAtUtc = createdAtUtc.AddTicks(index)
            });
        }
    }

    private static void AddPendingRows(AppDbContext db, int count)
    {
        var createdAtUtc = DateTimeOffset.UtcNow;
        for (var index = 0; index < count; index++)
        {
            db.SyncQueueSet.Add(new SyncQueueItem
            {
                EntityType = "exams",
                EntityId = Guid.NewGuid().ToString(),
                Operation = "upsert",
                PayloadJson = "{}",
                Status = SyncStatus.Pending,
                NextRetryAtUtc = createdAtUtc,
                CreatedAtUtc = createdAtUtc.AddTicks(index)
            });
        }
    }

    private static SyncQueueItem QueueItem(
        SyncStatus status,
        DateTimeOffset createdAtUtc) =>
        new()
        {
            EntityType = "exams",
            EntityId = Guid.NewGuid().ToString(),
            Operation = "upsert",
            PayloadJson = "{}",
            Status = status,
            NextRetryAtUtc = createdAtUtc,
            CreatedAtUtc = createdAtUtc
        };

    private static IOptions<ExamTransferOptions> WorkerOptions(int batchSize) =>
        Options.Create(new ExamTransferOptions
        {
            Cloud = new CloudOptions
            {
                Enabled = true,
                WorkerBatchSize = batchSize,
                WorkerIntervalSeconds = 30
            }
        });

    private static ServiceProvider CreateProvider(
        string databasePath,
        ICloudAdapter cloud)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder =>
            builder.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<ICloudAdapter>(cloud);
        return services.BuildServiceProvider();
    }

    private static CloudSyncWorker CreateWorker(
        ServiceProvider provider,
        IOptions<ExamTransferOptions> options,
        ICloudSyncSignal signal) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            signal,
            NullLogger<CloudSyncWorker>.Instance);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (await condition()) return;
            await Task.Delay(20);
        }

        Assert.True(await condition());
    }

    private sealed record QuizFixture(
        Exam Exam,
        ExamSession Session,
        IReadOnlyList<QuizQuestion> Questions,
        IReadOnlyList<QuizChoice> Choices);

    private class RecordingPushCloud : ICloudAdapter
    {
        private int healthChecks;
        private int pushCount;

        public bool HealthResult { get; init; } = true;
        public ConcurrentQueue<string> PushedEntities { get; } = new();
        public int HealthChecks => Volatile.Read(ref healthChecks);
        public int PushCount => Volatile.Read(ref pushCount);
        public bool Enabled => true;
        public bool Configured => true;
        public bool Authenticated => true;
        public bool CanSynchronize => true;
        public CloudLoginResult? CurrentSession => null;

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref healthChecks);
            return Task.FromResult(HealthResult);
        }

        public Task<CloudPreflightResult> PreflightAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task<CloudPushResult> PushAsync(
            SyncQueueItem item,
            Func<CancellationToken, Task>? checkpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref pushCount);
            PushedEntities.Enqueue($"{item.EntityType}/{item.EntityId}");
            return Task.FromResult(new CloudPushResult(false, null, "test", 0));
        }

        public Task<CloudLoginResult> LoginAsync(
            string email,
            string password,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CloudLoginResult?> RefreshSessionAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<CloudLoginResult?>(null);

        public Task LogoutAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudBackupDescriptor>>([]);

        public Task DownloadObjectAsync(
            string cloudObjectPath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingPushCloud : RecordingPushCloud
    {
        public TaskCompletionSource PushStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<CloudPushResult> PushAsync(
            SyncQueueItem item,
            Func<CancellationToken, Task>? checkpoint,
            CancellationToken cancellationToken)
        {
            PushStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
