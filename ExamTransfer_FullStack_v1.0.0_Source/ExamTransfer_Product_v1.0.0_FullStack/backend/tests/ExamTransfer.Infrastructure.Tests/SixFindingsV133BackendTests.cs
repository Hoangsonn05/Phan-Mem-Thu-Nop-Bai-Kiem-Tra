using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer.Workers;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class SixFindingsV133BackendTests
{
    [Fact]
    public async Task CloudSyncSignal_CoalescesBurstAndKeepsPeriodicTimeout()
    {
        var signal = new CloudSyncSignal();
        signal.Pulse();
        signal.Pulse();
        signal.Pulse();

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(1), default));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(30), default));
    }

    [Fact]
    public async Task PublicCreate_WakesWorkerAndBecomesExplicitlyShareReady()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var signal = new CloudSyncSignal();
        var cloud = new SuccessfulPushCloud();
        var options = Options.Create(new ExamTransferOptions
        {
            Cloud = new CloudOptions
            {
                Enabled = true,
                WorkerIntervalSeconds = 30,
                WorkerBatchSize = 10
            }
        });
        var service = CreateSessionService(
            database.Context,
            cloud,
            signal,
            options);
        var detail = await service.CreateAndOpenAsync(
            Request(exam.Id),
            "teacher-device",
            default);

        var pending = await service.GetProjectionReadinessAsync(
            detail.Summary.Id,
            default);
        Assert.False(pending.Ready);
        Assert.Equal(SyncStatus.Pending, pending.Status);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder =>
            builder.UseSqlite($"Data Source={database.Path}"));
        services.AddSingleton<ICloudAdapter>(cloud);
        await using var provider = services.BuildServiceProvider();
        var worker = new CloudSyncWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            signal,
            NullLogger<CloudSyncWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(async () =>
            {
                await using var verify = database.CreateContext();
                return await verify.SyncQueueSet.AnyAsync(
                    item => item.EntityType == "exam_sessions"
                        && item.EntityId == detail.Summary.Id.ToString()
                        && item.Status == SyncStatus.Synced);
            });
        }
        finally
        {
            await worker.StopAsync(default);
            worker.Dispose();
        }

        await using var readinessContext = database.CreateContext();
        var readinessService = CreateSessionService(
            readinessContext,
            cloud,
            signal,
            options);
        var ready = await readinessService.GetProjectionReadinessAsync(
            detail.Summary.Id,
            default);
        Assert.True(ready.Ready);
        Assert.Equal(SyncStatus.Synced, ready.Status);
        Assert.Equal("PUBLICCLOUD_PROJECTION_READY", ready.Code);

        var sessionProjection = Assert.Single(
            await readinessContext.SyncQueueSet
                .Where(item => item.EntityType == "exam_sessions"
                    && item.EntityId == detail.Summary.Id.ToString())
                .ToListAsync());
        Assert.Equal(SyncStatus.Synced, sessionProjection.Status);
        Assert.Equal(0, sessionProjection.RetryCount);

        var auditItems = await readinessContext.SyncQueueSet
            .Where(item => item.EntityType == "audit_logs")
            .ToListAsync();
        Assert.Equal(2, auditItems.Count);
        Assert.Contains(
            auditItems,
            item => item.PayloadJson.Contains(
                "\"action\":\"SessionCreated\"",
                StringComparison.Ordinal));
        Assert.Contains(
            auditItems,
            item => item.PayloadJson.Contains(
                "\"action\":\"SessionStateChanged\"",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectionFailureAndRetry_PreserveLocalWaitingAndSingleOutbox()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var exam = await SeedPublishedExamAsync(database.Context);
        var signal = new CloudSyncSignal();
        var options = Options.Create(new ExamTransferOptions());
        var service = CreateSessionService(
            database.Context,
            new SuccessfulPushCloud(),
            signal,
            options);
        var detail = await service.CreateAndOpenAsync(
            Request(exam.Id),
            "teacher-device",
            default);
        var item = await database.Context.SyncQueueSet.SingleAsync(
            row => row.EntityType == "exam_sessions"
                && row.EntityId == detail.Summary.Id.ToString());
        item.Status = SyncStatus.Failed;
        item.LastError = "simulated transport failure";
        item.RetryCount = 2;
        await database.Context.SaveChangesAsync();

        var failed = await service.GetProjectionReadinessAsync(
            detail.Summary.Id,
            default);
        Assert.False(failed.Ready);
        Assert.Equal("PUBLICCLOUD_PROJECTION_FAILED", failed.Code);
        Assert.DoesNotContain("simulated", failed.Message, StringComparison.OrdinalIgnoreCase);

        var retrying = await service.RetryProjectionAsync(
            detail.Summary.Id,
            default);
        Assert.Equal(SyncStatus.Pending, retrying.Status);
        Assert.False(retrying.Ready);
        Assert.Equal(
            1,
            await database.Context.SyncQueueSet.CountAsync(
                row => row.EntityType == "exam_sessions"
                    && row.EntityId == detail.Summary.Id.ToString()));
        var local = await database.Context.ExamSessionsSet.SingleAsync(
            row => row.Id == detail.Summary.Id);
        Assert.Equal(SessionStatus.Waiting, local.Status);
        Assert.Equal(SessionAccessMode.PublicCloud, local.AccessMode);
    }

    private static SessionService CreateSessionService(
        AppDbContext db,
        ICloudAdapter cloud,
        ICloudSyncSignal signal,
        IOptions<ExamTransferOptions> options) =>
        new(
            db,
            new SessionTokenService(options),
            new AuditService(db, new HttpContextAccessor()),
            new OutboxService(db, signal),
            new NoOpRealtime(),
            options,
            NullLogger<SessionService>.Instance,
            cloud: cloud,
            cloudSyncSignal: signal);

    private static async Task<Exam> SeedPublishedExamAsync(AppDbContext db)
    {
        var exam = new Exam
        {
            Id = Guid.NewGuid(),
            Title = "Projection readiness",
            Subject = "Test",
            DurationMinutes = 45,
            Status = ExamStatus.Published,
            DeliveryType = ExamDeliveryType.FileSubmission
        };
        db.ExamsSet.Add(exam);
        await db.SaveChangesAsync();
        return exam;
    }

    private static CreateSessionRequest Request(Guid examId) => new(
        examId,
        null,
        DateTimeOffset.UtcNow.AddMinutes(5),
        "{}",
        false,
        36,
        "PUB133",
        SessionAccessMode.PublicCloud,
        SessionAdmissionMode.OpenRequest);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200 && !await condition(); i++)
            await Task.Delay(10);
        Assert.True(await condition());
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

    private sealed class SuccessfulPushCloud : ICloudAdapter
    {
        public bool Enabled => true;
        public bool Configured => true;
        public bool Authenticated => true;
        public bool CanSynchronize => true;
        public CloudLoginResult? CurrentSession => null;
        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CloudPushResult> PushAsync(
            SyncQueueItem item,
            Func<CancellationToken, Task>? checkpoint,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new CloudPushResult(false, null, "test", 0));
        }
        public Task<CloudLoginResult> LoginAsync(
            string email,
            string password,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CloudLoginResult?>(null);
        public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudBackupDescriptor>>([]);
        public Task DownloadObjectAsync(
            string cloudObjectPath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
