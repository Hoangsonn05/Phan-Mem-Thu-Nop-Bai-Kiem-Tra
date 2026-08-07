using ExamTransfer.Domain;

namespace ExamTransfer.Infrastructure.Execution.PublicCloud;

internal static class PublicCloudProjectionPayloads
{
    public static object Exam(Exam x) => new
    {
        id = x.Id,
        class_id = x.ClassId,
        title = x.Title,
        subject = x.Subject,
        description = x.Description,
        duration_minutes = x.DurationMinutes,
        delivery_type = x.DeliveryType.ToString(),
        quiz_result_policy = x.QuizResultPolicy.ToString(),
        supervision_mode = x.SupervisionMode.ToString(),
        file_rule_json = x.FileRuleJson,
        status = x.Status.ToString(),
        version = x.Version,
        created_by = x.CreatedBy,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    public static object Session(ExamSession x) => new
    {
        id = x.Id,
        exam_id = x.ExamId,
        class_id = x.ClassId,
        room_code = x.RoomCode,
        status = x.Status.ToString(),
        host_device_id = x.HostDeviceId,
        planned_start_at = x.PlannedStartUtc,
        started_at = x.StartedAtUtc,
        ended_at = x.EndedAtUtc,
        delivery_type = x.DeliveryTypeSnapshot.ToString(),
        supervision_mode = x.SupervisionModeSnapshot.ToString(),
        quiz_result_policy = x.QuizResultPolicySnapshot.ToString(),
        exam_version = x.ExamVersionSnapshot,
        settings_json = x.SettingsJson,
        auto_approve = x.AutoApprove,
        access_mode = x.AccessMode.ToString(),
        admission_mode = x.AdmissionMode.ToString(),
        capacity = x.Capacity,
        accepting_participants = x.AcceptingParticipants,
        sequence = x.Sequence,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    public static object Question(QuizQuestion x) => new
    {
        id = x.Id,
        exam_id = x.ExamId,
        version = x.Version,
        sort_order = x.Order,
        question_text = x.Text,
        points = x.Points,
        multiple = x.Multiple,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    public static object Choice(QuizChoice x) => new
    {
        id = x.Id,
        question_id = x.QuestionId,
        sort_order = x.Order,
        choice_text = x.Text,
        is_correct = x.IsCorrect,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    public static object Source(QuizImportSource x) => new
    {
        id = x.Id,
        exam_id = x.ExamId,
        exam_version = x.ExamVersion,
        original_name = x.OriginalName,
        mime_type = x.MimeType,
        size_bytes = x.SizeBytes,
        sha256 = x.Sha256,
        status = x.Status,
        created_by = x.CreatedBy,
        imported_at = x.ImportedAtUtc,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };
}
