using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Workers;

public sealed class CloudSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ExamTransferOptions> options,
    ILogger<CloudSyncWorker> logger) : BackgroundService
{
    private readonly CloudOptions cloudOptions = options.Value.Cloud;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(
                        Math.Max(2, cloudOptions.WorkerIntervalSeconds)),
                    stoppingToken);

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<AppDbContext>();
                var cloud = scope.ServiceProvider
                    .GetRequiredService<ICloudAdapter>();

                if (!cloud.CanSynchronize)
                    continue;

                var now = DateTimeOffset.UtcNow;
                var items = await db.SyncQueueSet
                    .Where(x =>
                        (x.Status == SyncStatus.Pending
                            || x.Status == SyncStatus.Failed)
                        && (x.NextRetryAtUtc == null
                            || x.NextRetryAtUtc <= now)
                        && (x.LeaseUntilUtc == null
                            || x.LeaseUntilUtc < now))
                    .OrderBy(x => x.CreatedAtUtc)
                    .Take(Math.Clamp(
                        cloudOptions.WorkerBatchSize,
                        1,
                        100))
                    .ToListAsync(stoppingToken);

                foreach (var item in items)
                {
                    item.Status = SyncStatus.Syncing;
                    item.LeaseUntilUtc = now.AddMinutes(
                        Math.Max(2, cloudOptions.LeaseMinutes));
                    item.LastAttemptAtUtc = DateTimeOffset.UtcNow;
                    await MarkProjectionStatusAsync(
                        db,
                        item,
                        SyncStatus.Syncing,
                        null,
                        stoppingToken);
                    await db.SaveChangesAsync(stoppingToken);

                    try
                    {
                        var result = await cloud.PushAsync(
                            item,
                            ct => db.SaveChangesAsync(ct),
                            stoppingToken);
                        item.Status = SyncStatus.Synced;
                        item.LastError = null;
                        item.LeaseUntilUtc = null;
                        item.NextRetryAtUtc = null;
                        item.CloudObjectPath = result.CloudObjectPath;
                        item.CompletedAtUtc = DateTimeOffset.UtcNow;
                        await MarkProjectionStatusAsync(
                            db,
                            item,
                            SyncStatus.Synced,
                            result.CloudObjectPath,
                            stoppingToken);
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        item.Status = SyncStatus.Failed;
                        item.RetryCount++;
                        item.LastError = ex.Message;
                        item.LeaseUntilUtc = null;
                        item.NextRetryAtUtc =
                            DateTimeOffset.UtcNow.AddSeconds(
                                Math.Min(
                                    3600,
                                    Math.Pow(
                                        2,
                                        Math.Min(item.RetryCount, 10))));
                        await MarkProjectionStatusAsync(
                            db,
                            item,
                            SyncStatus.Failed,
                            item.CloudObjectPath,
                            stoppingToken);
                        logger.LogWarning(
                            ex,
                            "Cloud sync failed for {EntityType}/{EntityId}",
                            item.EntityType,
                            item.EntityId);
                    }

                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cloud sync worker failed");
            }
        }
    }

    private static async Task MarkProjectionStatusAsync(
        AppDbContext db,
        SyncQueueItem item,
        SyncStatus status,
        string? cloudObjectPath,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(item.EntityId, out var id))
            return;

        switch (item.EntityType.Trim().ToLowerInvariant())
        {
            case "exam_file":
            case "exam_files":
            {
                var entity = await db.ExamFilesSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
            case "submission_file":
            case "submission_files":
            {
                var entity = await db.SubmissionFilesSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
            case "graded_attachment":
            case "graded_attachments":
            {
                var entity = await db.GradedAttachmentsSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
            case "export_job":
            case "export_jobs":
            {
                var entity = await db.ExportJobsSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
            case "backup":
            case "backups":
            {
                var entity = await db.BackupsSet
                    .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (entity is not null)
                {
                    entity.SyncStatus = status;
                    if (!string.IsNullOrWhiteSpace(cloudObjectPath))
                        entity.CloudObjectPath = cloudObjectPath;
                }
                break;
            }
        }
    }
}
