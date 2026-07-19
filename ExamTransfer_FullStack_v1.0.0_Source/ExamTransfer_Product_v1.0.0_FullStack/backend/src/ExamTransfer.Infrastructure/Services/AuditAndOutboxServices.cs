using System.Diagnostics;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class AuditService(
    IAppDbContext db,
    IHttpContextAccessor accessor) : IAuditService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(
        string action,
        string entityType,
        string? entityId,
        Guid? sessionId,
        object? before,
        object? after,
        CancellationToken cancellationToken = default)
    {
        var context = accessor.HttpContext;
        var traceId = context?.TraceIdentifier
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N");
        var auditLog = new AuditLog
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            SessionId = sessionId,
            ActorId = context?.User.FindFirst("sub")?.Value
                ?? context?.User.Identity?.Name,
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
            BeforeJson = before is null
                ? null
                : JsonSerializer.Serialize(before, JsonOptions),
            AfterJson = after is null
                ? null
                : JsonSerializer.Serialize(after, JsonOptions),
            TraceId = traceId
        };

        db.Add(auditLog);
        db.Add(new SyncQueueItem
        {
            EntityType = "audit_logs",
            EntityId = auditLog.Id.ToString(),
            Operation = "upsert",
            PayloadJson = JsonSerializer.Serialize(
                new
                {
                    id = auditLog.Id,
                    session_id = auditLog.SessionId,
                    actor_id = auditLog.ActorId,
                    action = auditLog.Action,
                    entity_type = auditLog.EntityType,
                    entity_id = auditLog.EntityId,
                    ip_address = auditLog.IpAddress,
                    before_json = auditLog.BeforeJson,
                    after_json = auditLog.AfterJson,
                    trace_id = auditLog.TraceId,
                    created_at = auditLog.CreatedAtUtc,
                    updated_at = auditLog.UpdatedAtUtc
                },
                JsonOptions),
            Status = SyncStatus.Pending,
            NextRetryAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class OutboxService(IAppDbContext db) : IOutboxService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task EnqueueAsync(
        string entityType,
        string entityId,
        string operation,
        object payload,
        string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = entityType.Trim().ToLowerInvariant();
        var normalizedOperation = operation.Trim().ToLowerInvariant();
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);

        // Coalesce pending updates for the same entity so an offline machine
        // does not create an unbounded queue of obsolete snapshots.
        var existingCandidates = await db.SyncQueue
            .Where(x =>
                x.EntityType == normalizedType
                && x.EntityId == entityId
                && x.Operation == normalizedOperation
                && (x.Status == SyncStatus.Pending
                    || x.Status == SyncStatus.Failed))
            .ToListAsync(cancellationToken);
        var existing = existingCandidates
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();

        if (existing is null)
        {
            db.Add(new SyncQueueItem
            {
                EntityType = normalizedType,
                EntityId = entityId,
                Operation = normalizedOperation,
                PayloadJson = payloadJson,
                FilePath = filePath,
                Status = SyncStatus.Pending,
                NextRetryAtUtc = DateTimeOffset.UtcNow
            });
        }
        else
        {
            var previousFilePath = existing.FilePath;
            var updatedFilePath = filePath ?? existing.FilePath;
            existing.PayloadJson = payloadJson;
            existing.FilePath = updatedFilePath;
            existing.Status = SyncStatus.Pending;
            existing.RetryCount = 0;
            existing.NextRetryAtUtc = DateTimeOffset.UtcNow;
            existing.LastError = null;
            existing.LeaseUntilUtc = null;
            existing.CompletedAtUtc = null;

            // A changed file invalidates a previous resumable upload URL.
            if (!string.Equals(
                    previousFilePath,
                    updatedFilePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                existing.UploadUrl = null;
                existing.UploadOffset = 0;
                existing.CloudObjectPath = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
