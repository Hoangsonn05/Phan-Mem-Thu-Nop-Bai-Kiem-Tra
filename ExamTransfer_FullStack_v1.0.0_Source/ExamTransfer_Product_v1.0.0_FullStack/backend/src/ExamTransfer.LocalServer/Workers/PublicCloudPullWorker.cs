using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Workers;

public sealed class PublicCloudPullWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PublicCloudPullWorker> logger) : BackgroundService, IPublicCloudPullWorker
{
    private static readonly string[] EntityOrder =
    [
        "class_enrollment_requests", "class_members", "session_participants",
        "public_device_connections", "violations", "public_device_commands",
        "public_device_command_results", "submissions", "submission_files",
        "quiz_attempts", "quiz_answers"
    ];
    private static readonly int[] RetrySeconds = [5, 15, 30, 60, 120, 300];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PullOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PublicCloud pull cycle failed");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    public async Task PullOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cloud = scope.ServiceProvider.GetRequiredService<ICloudAdapter>();
        if (!cloud.CanSynchronize)
            return;
        if (!await cloud.CheckHealthAsync(cancellationToken))
        {
            await RecordFailureAsync(db, "cloud_schema", null, "schema",
                $"Cloud schema/capabilities do not match required version {CloudSchemaCompatibility.RequiredVersion}.",
                null, cancellationToken);
            return;
        }

        foreach (var entityName in EntityOrder)
        {
            var blockedUntil = await db.PublicCloudPullFailuresSet
                .Where(x => x.EntityName == entityName && x.ResolvedAtUtc == null)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Select(x => x.NextRetryAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (blockedUntil > DateTimeOffset.UtcNow)
                continue;

            try
            {
                await PullEntityAsync(db, cloud, entityName, cancellationToken);
                var failures = await db.PublicCloudPullFailuresSet
                    .Where(x => x.EntityName == entityName && x.ResolvedAtUtc == null)
                    .ToListAsync(cancellationToken);
                foreach (var failure in failures)
                    failure.ResolvedAtUtc = DateTimeOffset.UtcNow;
                if (failures.Count > 0)
                    await db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await RecordFailureAsync(db, entityName, null, Classify(ex), ex.Message, null, cancellationToken);
                logger.LogWarning(ex, "PublicCloud pull failed for {EntityName}", entityName);
            }
        }
    }

    private static async Task PullEntityAsync(
        AppDbContext db,
        ICloudAdapter cloud,
        string entityName,
        CancellationToken cancellationToken)
    {
        var cursor = await db.PublicCloudPullCursorsSet
            .SingleOrDefaultAsync(x => x.EntityName == entityName, cancellationToken);
        cursor ??= new PublicCloudPullCursor { EntityName = entityName };
        if (db.Entry(cursor).State == EntityState.Detached)
            db.PublicCloudPullCursorsSet.Add(cursor);

        for (var pageNumber = 0; pageNumber < 10; pageNumber++)
        {
            var page = await cloud.PullAsync(
                entityName,
                new CloudPullCursorValue(cursor.LastCloudVersion, cursor.LastUpdatedAtUtc, cursor.LastEntityId),
                100,
                cancellationToken);
            if (page.Records.Count == 0)
                return;

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            foreach (var record in page.Records)
            {
                var existing = await db.PublicCloudReplicaRecordsSet.SingleOrDefaultAsync(
                    x => x.EntityName == record.EntityName && x.CloudEntityId == record.EntityId,
                    cancellationToken);
                if (existing is null)
                {
                    db.PublicCloudReplicaRecordsSet.Add(new PublicCloudReplicaRecord
                    {
                        EntityName = record.EntityName,
                        CloudEntityId = record.EntityId,
                        CloudVersion = record.CloudVersion,
                        CloudUpdatedAtUtc = record.UpdatedAtUtc,
                        PayloadJson = record.PayloadJson
                    });
                }
                else if (record.CloudVersion > existing.CloudVersion)
                {
                    existing.CloudVersion = record.CloudVersion;
                    existing.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                    existing.PayloadJson = record.PayloadJson;
                }
                else if (record.CloudVersion == existing.CloudVersion
                         && !JsonEquivalent(existing.PayloadJson, record.PayloadJson))
                {
                    db.PublicCloudPullFailuresSet.Add(new PublicCloudPullFailure
                    {
                        EntityName = record.EntityName,
                        CloudEntityId = record.EntityId,
                        ErrorClass = "conflict",
                        ErrorMessage = "The same cloud_version produced different payloads; row quarantined.",
                        PayloadJson = record.PayloadJson,
                        RetryCount = RetrySeconds.Length,
                        NextRetryAtUtc = DateTimeOffset.UtcNow.AddSeconds(RetrySeconds[^1])
                    });
                }

                if (Guid.TryParse(record.EntityId, out var cloudGuid)
                    && !await db.PublicCloudIdMappingsSet.AnyAsync(
                        x => x.EntityName == record.EntityName && x.CloudEntityId == record.EntityId,
                        cancellationToken))
                {
                    db.PublicCloudIdMappingsSet.Add(new PublicCloudIdMapping
                    {
                        EntityName = record.EntityName,
                        CloudEntityId = record.EntityId,
                        LocalEntityId = cloudGuid
                    });
                }
                await ApplyTeacherProjectionAsync(db, record, cancellationToken);
            }

            var last = page.Records[^1];
            cursor.LastCloudVersion = last.CloudVersion;
            cursor.LastUpdatedAtUtc = last.UpdatedAtUtc;
            cursor.LastEntityId = last.EntityId;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (!page.HasMore)
                return;
        }
    }

    private static async Task ApplyTeacherProjectionAsync(
        AppDbContext db,
        CloudPullRecord record,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(record.EntityId, out var id)) return;
        using var document = JsonDocument.Parse(record.PayloadJson);
        var row = document.RootElement;
        switch (record.EntityName)
        {
            case "session_participants":
            {
                var entity = await db.SessionParticipantsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return;
                entity ??= new SessionParticipant { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.SessionParticipantsSet.Add(entity);
                entity.SessionId = GuidValue(row, "session_id");
                entity.UserId = NullableGuid(row, "user_id");
                entity.StudentCode = StringValue(row, "student_code");
                entity.DisplayName = StringValue(row, "display_name");
                entity.ClassName = NullableString(row, "class_name");
                entity.DeviceId = NullableString(row, "device_id") ?? string.Empty;
                entity.MachineName = NullableString(row, "machine_name") ?? string.Empty;
                entity.IpAddress = NullableString(row, "ip_address");
                entity.AppVersion = NullableString(row, "app_version") ?? string.Empty;
                entity.Status = EnumValue<ParticipantStatus>(row, "status");
                entity.JoinedAtUtc = DateValue(row, "joined_at", record.UpdatedAtUtc);
                entity.ApprovedAtUtc = NullableDate(row, "approved_at");
                entity.LastSeenUtc = NullableDate(row, "last_seen_at");
                entity.DownloadStatus = EnumValue(row, "download_status", DownloadStatus.NotStarted);
                entity.SubmissionStatus = EnumValue(row, "submission_status", SubmissionStatus.NotStarted);
                entity.ExtraTimeMinutes = IntValue(row, "extra_time_minutes");
                entity.ResubmitAllowed = BoolValue(row, "resubmit_allowed");
                entity.ResubmitReason = NullableString(row, "resubmit_reason");
                entity.CapabilityJson = RawOrNull(row, "capability_json");
                entity.SourceMode = "PublicCloud";
                entity.CloudVersion = record.CloudVersion;
                entity.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                entity.CloudSyncState = "Pulled";
                break;
            }
            case "submissions":
            {
                var entity = await db.SubmissionsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return;
                entity ??= new Submission { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.SubmissionsSet.Add(entity);
                entity.SessionId = GuidValue(row, "session_id");
                entity.ParticipantId = GuidValue(row, "participant_id");
                entity.AttemptNumber = IntValue(row, "attempt_number");
                entity.IdempotencyKey = NullableString(row, "idempotency_key") ?? string.Empty;
                entity.Status = EnumValue<SubmissionStatus>(row, "status");
                entity.ClientSubmittedAtUtc = DateValue(row, "client_submitted_at", record.UpdatedAtUtc);
                entity.ServerReceivedAtUtc = NullableDate(row, "server_received_at");
                entity.DeadlineUtc = DateValue(row, "deadline_at", record.UpdatedAtUtc);
                entity.IsLate = BoolValue(row, "is_late");
                entity.IsOfficial = BoolValue(row, "is_official");
                entity.ReceiptCode = NullableString(row, "receipt_code");
                entity.ReceiptSignature = NullableString(row, "receipt_signature");
                entity.TeacherRejectReason = NullableString(row, "teacher_reject_reason");
                entity.ClientNote = NullableString(row, "client_note");
                entity.SourceMode = "PublicCloud";
                entity.CloudVersion = record.CloudVersion;
                entity.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                entity.CloudSyncState = "Pulled";
                break;
            }
            case "submission_files":
            {
                var entity = await db.SubmissionFilesSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return;
                entity ??= new SubmissionFile { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.SubmissionFilesSet.Add(entity);
                entity.SubmissionId = GuidValue(row, "submission_id");
                entity.ClientFileId = NullableString(row, "client_file_id") ?? id.ToString("N");
                entity.OriginalName = StringValue(row, "name");
                entity.StoredName = NullableString(row, "stored_name") ?? entity.OriginalName;
                entity.MimeType = NullableString(row, "mime_type") ?? "application/octet-stream";
                entity.SizeBytes = LongValue(row, "size_bytes");
                entity.Sha256 = StringValue(row, "sha256");
                entity.TransferStatus = EnumValue(row, "transfer_status", TransferStatus.Queued);
                entity.SyncStatus = SyncStatus.Synced;
                entity.CloudObjectPath = NullableString(row, "cloud_object_path");
                entity.SourceMode = "PublicCloud";
                entity.CloudVersion = record.CloudVersion;
                entity.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                entity.CloudSyncState = "Pulled";
                break;
            }
            case "violations":
            {
                var entity = await db.ViolationsSet.FindAsync([id], cancellationToken);
                if (entity is not null && entity.CloudVersion >= record.CloudVersion) return;
                entity ??= new Violation { Id = id };
                if (db.Entry(entity).State == EntityState.Detached) db.ViolationsSet.Add(entity);
                entity.SessionId = GuidValue(row, "session_id");
                entity.ParticipantId = GuidValue(row, "participant_id");
                entity.Type = StringValue(row, "type");
                entity.Severity = EnumValue<ViolationSeverity>(row, "severity");
                entity.PayloadJson = RawOrNull(row, "payload_json");
                entity.OccurredAtUtc = DateValue(row, "occurred_at", record.UpdatedAtUtc);
                entity.HandledAtUtc = NullableDate(row, "handled_at");
                entity.HandledBy = NullableGuid(row, "handled_by");
                entity.SourceMode = "PublicCloud";
                entity.CloudVersion = record.CloudVersion;
                entity.CloudUpdatedAtUtc = record.UpdatedAtUtc;
                entity.CloudSyncState = "Pulled";
                break;
            }
        }
    }

    private static string StringValue(JsonElement row, string name) =>
        NullableString(row, name) ?? throw new JsonException($"Required field {name} is missing.");
    private static string? NullableString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static Guid GuidValue(JsonElement row, string name) => Guid.Parse(StringValue(row, name));
    private static Guid? NullableGuid(JsonElement row, string name) =>
        Guid.TryParse(NullableString(row, name), out var value) ? value : null;
    private static DateTimeOffset DateValue(JsonElement row, string name, DateTimeOffset fallback) =>
        NullableDate(row, name) ?? fallback;
    private static DateTimeOffset? NullableDate(JsonElement row, string name) =>
        DateTimeOffset.TryParse(NullableString(row, name), out var value) ? value : null;
    private static int IntValue(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static long LongValue(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static bool BoolValue(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? RawOrNull(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetRawText() : null;
    private static T EnumValue<T>(JsonElement row, string name, T fallback = default) where T : struct, Enum =>
        Enum.TryParse<T>(NullableString(row, name), true, out var value) ? value : fallback;

    private static bool JsonEquivalent(string left, string right)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return string.Equals(left, right, StringComparison.Ordinal); }
    }

    private static string Classify(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "auth",
        HttpRequestException => "network",
        JsonException => "validation",
        DbUpdateConcurrencyException => "conflict",
        _ when exception.Message.Contains("schema", StringComparison.OrdinalIgnoreCase) => "schema",
        _ => "unexpected"
    };

    private static async Task RecordFailureAsync(
        AppDbContext db,
        string entityName,
        string? cloudEntityId,
        string errorClass,
        string message,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        var failure = await db.PublicCloudPullFailuresSet
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(x => x.EntityName == entityName
                && x.CloudEntityId == cloudEntityId && x.ResolvedAtUtc == null,
                cancellationToken);
        if (failure is null)
        {
            failure = new PublicCloudPullFailure
            {
                EntityName = entityName,
                CloudEntityId = cloudEntityId,
                ErrorClass = errorClass,
                ErrorMessage = message,
                PayloadJson = payloadJson
            };
            db.PublicCloudPullFailuresSet.Add(failure);
        }
        else
        {
            failure.ErrorClass = errorClass;
            failure.ErrorMessage = message;
            failure.PayloadJson = payloadJson ?? failure.PayloadJson;
        }
        failure.RetryCount++;
        failure.NextRetryAtUtc = DateTimeOffset.UtcNow.AddSeconds(
            RetrySeconds[Math.Min(failure.RetryCount - 1, RetrySeconds.Length - 1)]);
        await db.SaveChangesAsync(cancellationToken);
    }
}
