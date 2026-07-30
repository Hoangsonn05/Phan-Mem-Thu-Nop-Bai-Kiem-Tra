using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Execution;

public interface ISessionParticipantMutationHandler
{
    SessionAccessMode AccessMode { get; }

    Task<ParticipantDto> ApproveAsync(
        SessionParticipant participant,
        Guid mutationRequestId,
        CancellationToken cancellationToken);
}

internal static class SessionParticipantMutationRules
{
    internal static TimeSpan ParticipantTokenLifetime(
        ExamTransferOptions options,
        ExamSession session)
    {
        var minimumMinutes = Math.Max(60, options.Security.TokenMinutes);
        var examMinutes = Math.Max(1, session.Exam.DurationMinutes);
        return TimeSpan.FromMinutes(Math.Max(minimumMinutes, examMinutes + 180));
    }

    internal static DateTimeOffset? ParticipantDeadline(
        SessionParticipant participant) =>
        participant.Session.StartedAtUtc?.AddMinutes(
            participant.Session.Exam.DurationMinutes + participant.ExtraTimeMinutes);

    internal static ParticipantDto ToMutationDto(
        ExamTransferOptions options,
        SessionParticipant participant,
        CloudParticipantMutationResult result)
    {
        var current = participant.ToDto(
            DateTimeOffset.UtcNow,
            options.Session.DisconnectAfterSeconds,
            result.EffectiveDeadlineUtc ?? ParticipantDeadline(participant));
        return current with
        {
            Status = result.Status,
            ExtraTimeMinutes = result.ExtraTimeMinutes,
            EffectiveDeadlineUtc =
                result.EffectiveDeadlineUtc ?? current.EffectiveDeadlineUtc
        };
    }

    internal static object ToCloud(SessionParticipant participant) => new
    {
        id = participant.Id,
        session_id = participant.SessionId,
        user_id = participant.UserId,
        student_code = participant.StudentCode,
        display_name = participant.DisplayName,
        class_name = participant.ClassName,
        device_id = participant.DeviceId,
        machine_name = participant.MachineName,
        ip_address = participant.IpAddress,
        app_version = participant.AppVersion,
        status = participant.Status.ToString(),
        joined_at = participant.JoinedAtUtc,
        approved_at = participant.ApprovedAtUtc,
        last_seen_at = participant.LastSeenUtc,
        download_status = participant.DownloadStatus.ToString(),
        submission_status = participant.SubmissionStatus.ToString(),
        extra_time_minutes = participant.ExtraTimeMinutes,
        resubmit_allowed = participant.ResubmitAllowed,
        resubmit_reason = participant.ResubmitReason,
        capability_json = participant.CapabilityJson,
        created_at = participant.CreatedAtUtc,
        updated_at = participant.UpdatedAtUtc
    };
}
