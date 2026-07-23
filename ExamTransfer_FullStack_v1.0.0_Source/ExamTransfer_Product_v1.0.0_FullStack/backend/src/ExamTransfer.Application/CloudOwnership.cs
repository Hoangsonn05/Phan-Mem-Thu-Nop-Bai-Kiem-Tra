using System.Text.Json;

namespace ExamTransfer.Application;

public enum CloudEntityAuthority
{
    LocalOwned,
    CloudOwned,
    SourceModeDependent,
    Unsupported
}

public static class CloudSchemaCompatibility
{
    public const int RequiredVersion = 14;
    public static readonly IReadOnlySet<string> CriticalRpcs = new HashSet<string>(StringComparer.Ordinal)
    {
        "join_public_session",
        "init_public_submission",
        "finalize_public_submission",
        "upsert_public_device_heartbeat",
        "ack_public_device_command",
        "start_public_quiz_attempt",
        "save_public_quiz_answers",
        "finalize_public_quiz_attempt",
        "verify_public_submission_archive",
        "get_public_exam_file_download"
    };
}

public static class CloudEntityOwnershipRegistry
{
    private static readonly IReadOnlyDictionary<string, CloudEntityAuthority> Authorities =
        new Dictionary<string, CloudEntityAuthority>(StringComparer.OrdinalIgnoreCase)
        {
            ["classes"] = CloudEntityAuthority.LocalOwned,
            ["exams"] = CloudEntityAuthority.LocalOwned,
            ["exam_files"] = CloudEntityAuthority.LocalOwned,
            ["quiz_questions"] = CloudEntityAuthority.LocalOwned,
            ["quiz_choices"] = CloudEntityAuthority.LocalOwned,
            ["public_class_assignments"] = CloudEntityAuthority.LocalOwned,
            ["exam_sessions"] = CloudEntityAuthority.LocalOwned,
            ["control_policies"] = CloudEntityAuthority.LocalOwned,
            ["grades"] = CloudEntityAuthority.LocalOwned,
            ["rubric_scores"] = CloudEntityAuthority.LocalOwned,
            ["graded_attachments"] = CloudEntityAuthority.LocalOwned,
            ["audit_logs"] = CloudEntityAuthority.LocalOwned,
            ["backups"] = CloudEntityAuthority.LocalOwned,
            ["export_jobs"] = CloudEntityAuthority.LocalOwned,
            ["class_enrollment_requests"] = CloudEntityAuthority.CloudOwned,
            ["public_device_connections"] = CloudEntityAuthority.CloudOwned,
            ["public_device_commands"] = CloudEntityAuthority.CloudOwned,
            ["public_device_command_results"] = CloudEntityAuthority.CloudOwned,
            ["class_members"] = CloudEntityAuthority.SourceModeDependent,
            ["session_participants"] = CloudEntityAuthority.SourceModeDependent,
            ["violations"] = CloudEntityAuthority.SourceModeDependent,
            ["submissions"] = CloudEntityAuthority.SourceModeDependent,
            ["submission_files"] = CloudEntityAuthority.SourceModeDependent,
            ["quiz_attempts"] = CloudEntityAuthority.SourceModeDependent,
            ["quiz_answers"] = CloudEntityAuthority.SourceModeDependent
        };

    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["class"] = "classes", ["class_member"] = "class_members",
            ["exam"] = "exams", ["exam_file"] = "exam_files",
            ["session"] = "exam_sessions", ["exam_session"] = "exam_sessions",
            ["participant"] = "session_participants", ["session_participant"] = "session_participants",
            ["submission"] = "submissions", ["submission_file"] = "submission_files",
            ["grade"] = "grades", ["rubric_score"] = "rubric_scores",
            ["graded_attachment"] = "graded_attachments", ["control_policy"] = "control_policies",
            ["violation"] = "violations", ["audit"] = "audit_logs", ["audit_log"] = "audit_logs",
            ["backup"] = "backups", ["export_job"] = "export_jobs",
            ["quiz_attempt"] = "quiz_attempts", ["quiz_answer"] = "quiz_answers"
        };

    public static string Normalize(string entityType)
    {
        var normalized = entityType.Trim().ToLowerInvariant();
        return Aliases.TryGetValue(normalized, out var canonical) ? canonical : normalized;
    }

    public static CloudEntityAuthority GetAuthority(string entityType) =>
        Authorities.TryGetValue(Normalize(entityType), out var authority)
            ? authority
            : CloudEntityAuthority.Unsupported;

    public static bool MayPushToCloud(string entityType, string payloadJson)
    {
        return GetAuthority(entityType) switch
        {
            CloudEntityAuthority.LocalOwned => true,
            CloudEntityAuthority.SourceModeDependent => !IsPublicCloudPayload(payloadJson),
            _ => false
        };
    }

    private static bool IsPublicCloudPayload(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            foreach (var propertyName in new[] { "source_mode", "sourceMode", "SourceMode" })
            {
                if (root.TryGetProperty(propertyName, out var value)
                    && string.Equals(value.GetString(), "PublicCloud", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
            // Invalid payloads remain eligible so the adapter reports the real
            // serialization failure instead of silently discarding an outbox row.
        }
        return false;
    }
}

public sealed record CloudPullCursorValue(long CloudVersion, DateTimeOffset? UpdatedAtUtc, string? EntityId);
public sealed record CloudPullRecord(string EntityName, string EntityId, long CloudVersion, DateTimeOffset UpdatedAtUtc, string PayloadJson);
public sealed record CloudPullPage(IReadOnlyList<CloudPullRecord> Records, bool HasMore);

public interface IPublicCloudPullWorker
{
    Task PullOnceAsync(CancellationToken cancellationToken);
}
