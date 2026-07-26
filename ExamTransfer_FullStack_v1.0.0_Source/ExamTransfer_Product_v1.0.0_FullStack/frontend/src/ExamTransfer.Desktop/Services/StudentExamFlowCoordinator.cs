using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public enum StudentExamEntryPoint { CurrentExam, QuizTab }
public enum StudentExamFlowState
{
    NoSession,
    PendingApproval,
    ApprovedWaiting,
    ReadyToStartFileExam,
    ReadyToStartQuiz,
    InProgressFileExam,
    InProgressQuiz,
    SubmittedFileExam,
    FinalizedQuiz,
    SessionFinished,
    RejectedOrExpired
}

public sealed record StudentExamFlowSnapshot(
    bool HasSession,
    SessionStatus? SessionStatus,
    ParticipantStatus? ParticipantStatus,
    ExamDeliveryType DeliveryType,
    SubmissionStatus SubmissionStatus,
    QuizAttemptStatus? AttemptStatus);

public sealed record StudentExamFlowResolution(
    StudentExamFlowState State,
    string RouteKey,
    bool RequiresStartConfirmation,
    string Message);

public sealed record StudentExamNavigationRequest(
    StudentExamEntryPoint EntryPoint,
    StudentExamFlowResolution Resolution);

public interface IStudentExamFlowCoordinator
{
    event EventHandler<StudentExamNavigationRequest>? NavigationRequested;
    Task<StudentExamFlowResolution> ResolveAsync(
        StudentExamEntryPoint entryPoint,
        bool startConfirmed,
        CancellationToken cancellationToken);
    void ReturnToCurrentExam();
}

public sealed class StudentExamFlowCoordinator(
    IBackendClient api,
    SupabasePublicCloudClient publicCloud,
    StudentSessionState state) : IStudentExamFlowCoordinator
{
    public event EventHandler<StudentExamNavigationRequest>? NavigationRequested;

    public void ReturnToCurrentExam() =>
        Publish(
            StudentExamEntryPoint.QuizTab,
            new(
                StudentExamFlowState.FinalizedQuiz,
                "S-04",
                false,
                "Quay về Kỳ thi hiện tại."));

    public async Task<StudentExamFlowResolution> ResolveAsync(
        StudentExamEntryPoint entryPoint,
        bool startConfirmed,
        CancellationToken cancellationToken)
    {
        if (!state.HasSession)
            return Publish(entryPoint, ResolveSnapshot(new(
                false, null, null, ExamDeliveryType.FileSubmission,
                SubmissionStatus.NotStarted, null)));

        QuizAttemptDto? attempt;
        StudentExamFlowSnapshot snapshot;
        if (state.AccessMode == SessionAccessMode.PublicCloud)
        {
            var timeline = await publicCloud.GetStudentTimelineAsync(
                state.SessionId!.Value,
                cancellationToken);
            if (timeline.ParticipantId != state.ParticipantId)
                throw new InvalidDataException("PublicCloud timeline không thuộc lượt dự thi hiện tại.");
            attempt = timeline.AttemptId.HasValue
                ? await publicCloud.GetQuizAttemptAsync(timeline.AttemptId.Value, cancellationToken)
                : null;
            ApplyPublicTimeline(timeline, attempt);
            snapshot = new(
                true,
                ParseEnum<SessionStatus>(timeline.SessionStatus),
                ParseEnum<ParticipantStatus>(timeline.ParticipantStatus),
                ParseEnum(timeline.DeliveryType, ExamDeliveryType.FileSubmission),
                ParseEnum(timeline.SubmissionStatus, SubmissionStatus.NotStarted),
                attempt?.Status);
        }
        else
        {
            api.SetParticipantToken(state.AccessToken);
            var detail = ApiGuard.Require(await api.GetSessionAsync(state.SessionId!.Value, cancellationToken));
            var participant = ApiGuard.Require(await api.GetAsync<ParticipantDto>(
                $"api/v1/sessions/{state.SessionId}/participants/{state.ParticipantId}",
                cancellationToken));
            state.ExamId = detail.Summary.ExamId;
            state.ExamVersion = detail.Summary.ExamVersion;
            state.DeliveryType = detail.Summary.DeliveryType;
            state.SupervisionMode = detail.Summary.SupervisionMode;
            state.ResultPolicy = detail.Summary.QuizResultPolicy;
            state.SessionStatus = detail.Summary.Status;
            state.ParticipantStatus = participant.Status;
            state.SubmissionStatus = participant.SubmissionStatus;
            attempt = detail.Summary.DeliveryType == ExamDeliveryType.MultipleChoice
                ? ApiGuard.Require(await api.GetAsync<QuizAttemptLookupDto>(
                    $"api/v1/student/quiz/sessions/{state.SessionId}/attempt",
                    cancellationToken)).Attempt
                : null;
            state.CurrentAttempt = attempt;
            state.Revision = detail.Summary.Sequence;
            snapshot = new(
                true,
                detail.Summary.Status,
                participant.Status,
                detail.Summary.DeliveryType,
                participant.SubmissionStatus,
                attempt?.Status);
        }

        var resolution = ResolveSnapshot(snapshot);
        if (resolution.RequiresStartConfirmation && !startConfirmed)
        {
            state.RouteIntent = resolution.RouteKey;
            return resolution;
        }
        if (resolution.State == StudentExamFlowState.ReadyToStartQuiz && startConfirmed)
        {
            attempt = state.AccessMode == SessionAccessMode.PublicCloud
                ? await publicCloud.StartQuizAttemptAsync(state.SessionId!.Value, cancellationToken)
                : ApiGuard.Require(await api.PostAsync<object, QuizAttemptDto>(
                    $"api/v1/student/quiz/sessions/{state.SessionId}/attempt",
                    new { },
                    cancellationToken));
            state.CurrentAttempt = attempt;
            resolution = ResolveSnapshot(snapshot with { AttemptStatus = attempt.Status });
        }

        state.RouteIntent = resolution.RouteKey;
        return Publish(entryPoint, resolution);
    }

    public static StudentExamFlowResolution ResolveSnapshot(StudentExamFlowSnapshot snapshot)
    {
        if (!snapshot.HasSession)
            return new(StudentExamFlowState.NoSession, "S-01", false, "Hãy kết nối phòng thi.");
        if (snapshot.ParticipantStatus == ParticipantStatus.PendingApproval)
            return new(StudentExamFlowState.PendingApproval, "S-03", false, "Lượt dự thi đang chờ giáo viên duyệt.");
        if (snapshot.ParticipantStatus is ParticipantStatus.Rejected or ParticipantStatus.NotConnected)
            return new(StudentExamFlowState.RejectedOrExpired, "S-01", false, "Lượt dự thi đã bị từ chối hoặc hết hiệu lực.");
        if (snapshot.SessionStatus is SessionStatus.Finished or SessionStatus.Archived or SessionStatus.Cancelled)
            return new(StudentExamFlowState.SessionFinished, "S-04", false, "Phiên thi đã kết thúc.");
        if (snapshot.ParticipantStatus != ParticipantStatus.Approved
            || snapshot.SessionStatus is not (SessionStatus.InProgress or SessionStatus.Paused))
            return new(StudentExamFlowState.ApprovedWaiting, "S-04", false, "Phiên thi chưa bắt đầu.");

        if (snapshot.DeliveryType == ExamDeliveryType.MultipleChoice)
        {
            return snapshot.AttemptStatus switch
            {
                QuizAttemptStatus.InProgress => new(
                    StudentExamFlowState.InProgressQuiz, "S-06", false, "Tiếp tục bài trắc nghiệm đang làm."),
                QuizAttemptStatus.Finalized => new(
                    StudentExamFlowState.FinalizedQuiz, "S-06", false, "Bài trắc nghiệm đã nộp."),
                _ => new(
                    StudentExamFlowState.ReadyToStartQuiz, "S-06", true, "Xác nhận trước khi bắt đầu nhận đề trắc nghiệm.")
            };
        }

        return snapshot.SubmissionStatus switch
        {
            SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted => new(
                StudentExamFlowState.SubmittedFileExam, "S-08", false, "Mở biên nhận bài đã nộp."),
            SubmissionStatus.Preparing or SubmissionStatus.Uploading or SubmissionStatus.Verifying => new(
                StudentExamFlowState.InProgressFileExam, "S-07", false, "Tiếp tục gửi bài tự luận."),
            _ => new(
                StudentExamFlowState.ReadyToStartFileExam, "S-05", false, "Mở luồng nhận đề tự luận.")
        };
    }

    private void ApplyPublicTimeline(PublicStudentTimeline timeline, QuizAttemptDto? attempt)
    {
        state.ExamId = timeline.ExamId;
        state.ExamVersion = timeline.ExamVersion;
        state.DeliveryType = ParseEnum(timeline.DeliveryType, ExamDeliveryType.FileSubmission);
        state.SupervisionMode = ParseEnum(timeline.SupervisionMode, SupervisionMode.None);
        state.ResultPolicy = ParseEnum(timeline.ResultPolicy, QuizResultPolicy.Hidden);
        state.SessionStatus = ParseEnum<SessionStatus>(timeline.SessionStatus);
        state.ParticipantStatus = ParseEnum<ParticipantStatus>(timeline.ParticipantStatus);
        state.SubmissionStatus = ParseEnum(timeline.SubmissionStatus, SubmissionStatus.NotStarted);
        state.CurrentAttempt = attempt;
        state.Revision = timeline.Revision;
    }

    private StudentExamFlowResolution Publish(
        StudentExamEntryPoint entryPoint,
        StudentExamFlowResolution resolution)
    {
        state.RouteIntent = resolution.RouteKey;
        NavigationRequested?.Invoke(this, new(entryPoint, resolution));
        return resolution;
    }

    private static T? ParseEnum<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) ? parsed : null;

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
}
