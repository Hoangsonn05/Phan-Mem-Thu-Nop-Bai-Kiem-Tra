using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Execution.PublicCloud;

public sealed class PublicCloudProjectionExecution(
    AppDbContext db,
    ICloudSyncSignal? cloudSyncSignal = null)
{
    public async Task<CloudProjectionReadiness> GetProjectionReadinessAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var session = await db.ExamSessionsSet
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.AccessMode })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (session.AccessMode != SessionAccessMode.PublicCloud)
            return new(
                id,
                false,
                true,
                SyncStatus.LocalOnly,
                "LAN_ONLY",
                "Phiên LAN không cần PublicCloud projection.",
                0);

        var projectionItems = await db.SyncQueueSet
            .AsNoTracking()
            .Where(x => x.EntityType == "exam_sessions" && x.EntityId == id.ToString())
            .ToListAsync(cancellationToken);
        var item = projectionItems
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();
        if (item is null)
            return new(
                id,
                true,
                false,
                SyncStatus.Pending,
                "PUBLICCLOUD_PROJECTION_PENDING",
                "Phòng đang chờ đồng bộ PublicCloud.",
                0);

        return item.Status switch
        {
            SyncStatus.Synced => new(
                id,
                true,
                true,
                item.Status,
                "PUBLICCLOUD_PROJECTION_READY",
                "Sẵn sàng — có thể chia sẻ mã phòng.",
                item.RetryCount),
            SyncStatus.Failed or SyncStatus.Conflict => new(
                id,
                true,
                false,
                item.Status,
                "PUBLICCLOUD_PROJECTION_FAILED",
                "Đồng bộ PublicCloud thất bại — dữ liệu cục bộ vẫn được giữ. Hãy thử lại.",
                item.RetryCount),
            _ => new(
                id,
                true,
                false,
                item.Status,
                "PUBLICCLOUD_PROJECTION_SYNCING",
                "Đang đồng bộ PublicCloud.",
                item.RetryCount)
        };
    }

    public async Task<CloudProjectionReadiness> RetryProjectionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var session = await db.ExamSessionsSet
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.AccessMode })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (session.AccessMode != SessionAccessMode.PublicCloud)
            return await GetProjectionReadinessAsync(id, cancellationToken);

        var projectionItems = await db.SyncQueueSet
            .Where(x => x.EntityType == "exam_sessions" && x.EntityId == id.ToString())
            .ToListAsync(cancellationToken);
        var item = projectionItems
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault()
            ?? throw new ApiException(
                ErrorCodes.Conflict,
                "Không tìm thấy outbox PublicCloud của phòng thi; dữ liệu cục bộ không bị thay đổi.",
                409);
        if (item.Status != SyncStatus.Synced)
        {
            item.Status = SyncStatus.Pending;
            item.LastError = null;
            item.LeaseUntilUtc = null;
            item.NextRetryAtUtc = DateTimeOffset.UtcNow;
            item.CompletedAtUtc = null;
            await db.SaveChangesAsync(cancellationToken);
            cloudSyncSignal?.Pulse();
        }
        return await GetProjectionReadinessAsync(id, cancellationToken);
    }
}
