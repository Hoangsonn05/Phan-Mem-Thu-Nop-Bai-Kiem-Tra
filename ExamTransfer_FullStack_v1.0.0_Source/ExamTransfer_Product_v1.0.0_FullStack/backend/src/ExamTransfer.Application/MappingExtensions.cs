using System.Text.Json;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Application;

public static class MappingExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static FileRuleDto ParseFileRule(this Exam exam)
    {
        try
        {
            return JsonSerializer.Deserialize<FileRuleDto>(exam.FileRuleJson, JsonOptions)
                ?? DefaultFileRule();
        }
        catch
        {
            return DefaultFileRule();
        }
    }

    public static FileRuleDto DefaultFileRule() => new(
        [".pdf", ".docx", ".xlsx", ".pptx", ".zip", ".txt", ".cs", ".java", ".py"],
        100L * 1024 * 1024,
        500L * 1024 * 1024,
        20,
        false,
        true);

    public static ClassSummaryDto ToSummary(this ClassRoom entity, int studentCount) =>
        new(entity.Id, entity.Name, entity.Code, entity.SchoolYear, entity.Status, studentCount, entity.RowVersion);

    public static ClassDetailDto ToDetail(this ClassRoom entity, IReadOnlyList<StudentDto> students) =>
        new(entity.Id, entity.Name, entity.Code, entity.SchoolYear, entity.Description, entity.Status, students, entity.RowVersion);

    public static StudentDto ToDto(this ClassMember entity) =>
        new(entity.Id, entity.StudentCode, entity.DisplayName, entity.Email, entity.MetadataJson);

    public static ExamSummaryDto ToSummary(this Exam entity, int fileCount) =>
        new(entity.Id, entity.ClassId, entity.Title, entity.Subject, entity.DurationMinutes, entity.DeliveryType, entity.Status, entity.Version, fileCount, entity.RowVersion);

    public static ExamDetailDto ToDetail(this Exam entity, IReadOnlyList<FileDescriptorDto> files) =>
        new(entity.Id, entity.ClassId, entity.Title, entity.Subject, entity.Description, entity.DurationMinutes, entity.DeliveryType, entity.Status, entity.Version, entity.ParseFileRule(), files, entity.RowVersion);

    public static ParticipantDto ToDto(
        this SessionParticipant entity,
        DateTimeOffset nowUtc,
        int disconnectAfterSeconds = 20,
        DateTimeOffset? effectiveDeadlineUtc = null)
    {
        var connection = entity.Status == ParticipantStatus.Rejected
            ? ConnectionState.Offline
            : entity.LastSeenUtc is null
                ? ConnectionState.Connecting
                : nowUtc - entity.LastSeenUtc > TimeSpan.FromSeconds(disconnectAfterSeconds)
                    ? ConnectionState.Offline
                    : nowUtc - entity.LastSeenUtc > TimeSpan.FromSeconds(disconnectAfterSeconds / 2.0)
                        ? ConnectionState.Degraded
                        : ConnectionState.Online;

        return new ParticipantDto(entity.Id, entity.SessionId, entity.StudentCode, entity.DisplayName, entity.DeviceId,
            entity.MachineName, entity.IpAddress, entity.AppVersion, entity.Status, entity.LastSeenUtc,
            entity.DownloadStatus, entity.SubmissionStatus, entity.ExtraTimeMinutes, effectiveDeadlineUtc, connection);
    }

    public static SubmissionFileDto ToDto(this SubmissionFile entity, IReadOnlyList<int> chunks) =>
        new(entity.Id, entity.OriginalName, entity.SizeBytes, entity.Sha256, entity.MimeType,
            entity.TotalChunks, chunks, entity.TransferStatus,
            entity.TransferStatus == TransferStatus.Completed ? $"/api/v1/submissions/{entity.SubmissionId}/files/{entity.Id}/content" : null);

    public static ViolationDto ToDto(this Violation entity) =>
        new(entity.Id, entity.SessionId, entity.ParticipantId, entity.Type, entity.Severity, entity.OccurredAtUtc,
            entity.PayloadJson, entity.HandledAtUtc, entity.HandledBy);
}
