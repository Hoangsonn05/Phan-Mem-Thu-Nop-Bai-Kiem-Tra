using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public enum StudentExamEntryPoint { CurrentExam, QuizTab }
public enum StudentJoinPhase
{
    AuthoritativeMutation,
    RealtimeStartup,
    LifecycleResolution,
    Completed
}

public static class StudentJoinErrorCodes
{
    public const string Succeeded = "SUCCESS";
    public const string JoinMutationFailed = "JOIN_MUTATION_FAILED";
    public const string PostJoinSynchronizationFailed = "POST_JOIN_SYNCHRONIZATION_FAILED";
    public const string LifecycleResolutionFailed = "LIFECYCLE_RESOLUTION_FAILED";
    public const string AuthenticationRequired = "AUTHENTICATION_REQUIRED";
    public const string RoomNotFound = "ROOM_NOT_FOUND";
    public const string ParticipantRejected = "PARTICIPANT_REJECTED";
}

public sealed record StudentJoinOutcome(
    string Code,
    StudentJoinPhase Phase,
    bool AuthorityMutationCommitted,
    string? CauseCode = null)
{
    public bool Succeeded => Code == StudentJoinErrorCodes.Succeeded;
}

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
    CollectingSummary,
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
    void NavigateResolved(
        StudentExamEntryPoint entryPoint,
        StudentExamFlowResolution resolution) { }
    Task<StudentJoinOutcome> SynchronizeAfterJoinAsync(
        IStudentRealtimeService realtime,
        CancellationToken cancellationToken);
    void ReturnToCurrentExam();
}

public sealed class StudentExamFlowCoordinator(
    IBackendClient api,
    SupabasePublicCloudClient publicCloud,
    StudentSessionState state,
    IToastService? toasts = null) : IStudentExamFlowCoordinator
{
    private readonly IToastService toasts = toasts ?? new ToastService();
    private readonly object lifecycleSync = new();
    private readonly SemaphoreSlim resolutionGate = new(1, 1);
    private readonly HashSet<string> publishedNavigation = new(StringComparer.Ordinal);
    private readonly HashSet<string> publishedNotifications = new(StringComparer.Ordinal);
    private Guid? trackedSessionId;

    public event EventHandler<StudentExamNavigationRequest>? NavigationRequested;

    public async Task<StudentJoinOutcome> SynchronizeAfterJoinAsync(
        IStudentRealtimeService realtime,
        CancellationToken cancellationToken)
    {
        try
        {
            await realtime.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            state.MarkPostJoinSynchronizationFailed();
            LogPostJoinFailure(StudentJoinPhase.RealtimeStartup, ex);
            return new(
                StudentJoinErrorCodes.PostJoinSynchronizationFailed,
                StudentJoinPhase.RealtimeStartup,
                state.JoinMutationCommitted,
                StudentJoinErrorCodes.PostJoinSynchronizationFailed);
        }

        try
        {
            _ = await ResolveAsync(
                StudentExamEntryPoint.CurrentExam,
                false,
                cancellationToken);
            state.MarkPostJoinSynchronizationSucceeded();
            return new(
                StudentJoinErrorCodes.Succeeded,
                StudentJoinPhase.Completed,
                state.JoinMutationCommitted);
        }
        catch (Exception ex)
        {
            state.MarkPostJoinSynchronizationFailed();
            LogPostJoinFailure(StudentJoinPhase.LifecycleResolution, ex);
            return new(
                StudentJoinErrorCodes.PostJoinSynchronizationFailed,
                StudentJoinPhase.LifecycleResolution,
                state.JoinMutationCommitted,
                StudentJoinErrorCodes.LifecycleResolutionFailed);
        }
    }

    public void ReturnToCurrentExam() =>
        Publish(
            StudentExamEntryPoint.QuizTab,
            new(
                StudentExamFlowState.FinalizedQuiz,
                "S-04",
                false,
                "Quay về Kỳ thi hiện tại."));

    public void NavigateResolved(
        StudentExamEntryPoint entryPoint,
        StudentExamFlowResolution resolution) =>
        Publish(entryPoint, resolution);

    public async Task<StudentExamFlowResolution> ResolveAsync(
        StudentExamEntryPoint entryPoint,
        bool startConfirmed,
        CancellationToken cancellationToken)
    {
        await resolutionGate.WaitAsync(cancellationToken);
        try
        {
            return await ResolveCoreAsync(
                entryPoint,
                startConfirmed,
                cancellationToken);
        }
        finally
        {
            resolutionGate.Release();
        }
    }

    private async Task<StudentExamFlowResolution> ResolveCoreAsync(
        StudentExamEntryPoint entryPoint,
        bool startConfirmed,
        CancellationToken cancellationToken)
    {
        if (!state.HasSession)
            return Publish(entryPoint, ResolveSnapshot(new(
                false, null, null, ExamDeliveryType.FileSubmission,
                SubmissionStatus.NotStarted, null)));

        EnsureTrackedSession();
        var previous = CurrentSnapshot();
        QuizAttemptDto? attempt;
        StudentExamFlowSnapshot snapshot;
        if (state.AccessMode == SessionAccessMode.PublicCloud)
        {
            var timeline = await publicCloud.GetStudentTimelineAsync(
                state.SessionId!.Value,
                cancellationToken);
            if (timeline.ParticipantId != state.ParticipantId)
                throw new InvalidDataException("PublicCloud timeline không thuộc lượt dự thi hiện tại.");
            if (IsStaleSnapshot(
                    ParseEnum<SessionStatus>(timeline.SessionStatus),
                    timeline.Revision))
                return Publish(entryPoint, ResolveSnapshot(CurrentSnapshot()));
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
            if (IsStaleSnapshot(detail.Summary.Status, detail.Summary.Sequence))
                return Publish(entryPoint, ResolveSnapshot(CurrentSnapshot()));
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
            state.ApplyResubmitAuthority(participant.ResubmitAllowed);
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
        PublishTransitionNotification(previous, snapshot, state.Revision);
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
        if (snapshot.SessionStatus is SessionStatus.Finished or SessionStatus.Archived or SessionStatus.Cancelled)
            return new(StudentExamFlowState.SessionFinished, "S-04", false, "Phiên thi đã kết thúc.");
        if (snapshot.ParticipantStatus == ParticipantStatus.PendingApproval)
            return new(StudentExamFlowState.PendingApproval, "S-03", false, "Lượt dự thi đang chờ giáo viên duyệt.");
        if (snapshot.ParticipantStatus is ParticipantStatus.Rejected or ParticipantStatus.NotConnected)
            return new(StudentExamFlowState.RejectedOrExpired, "S-01", false, "Lượt dự thi đã bị từ chối hoặc hết hiệu lực.");
        if (snapshot.ParticipantStatus != ParticipantStatus.Approved
            || snapshot.SessionStatus is not (
                SessionStatus.InProgress
                or SessionStatus.Paused
                or SessionStatus.Collecting))
            return new(StudentExamFlowState.ApprovedWaiting, "S-03", false, "Phiên thi chưa bắt đầu.");

        if (snapshot.DeliveryType == ExamDeliveryType.MultipleChoice)
        {
            if (snapshot.SessionStatus == SessionStatus.Collecting
                && snapshot.AttemptStatus is null)
                return new(
                    StudentExamFlowState.CollectingSummary,
                    "S-04",
                    false,
                    "Phiên thi đang thu bài; không thể bắt đầu lượt trắc nghiệm mới.");
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

    public static bool IsLifecycleProgressionEvent(string eventName) =>
        eventName is
            RealtimeEvents.ParticipantApproved or
            "ParticipantRejected" or
            RealtimeEvents.SessionStateChanged or
            "SessionStarted" or
            "SessionCollecting" or
            "SessionFinished" or
            "SessionCancelled" or
            "SessionArchived" or
            "Reconnected";

    private void ApplyPublicTimeline(PublicStudentTimeline timeline, QuizAttemptDto? attempt)
    {
        state.ExamId = timeline.ExamId;
        state.ExamVersion = timeline.ExamVersion;
        state.DeliveryType = ParseEnum(timeline.DeliveryType, ExamDeliveryType.FileSubmission);
        state.SupervisionMode = ParseEnum(timeline.SupervisionMode, SupervisionMode.None);
        state.ResultPolicy = ParseEnum(timeline.ResultPolicy, QuizResultPolicy.Hidden);
        state.AdmissionMode = ParseEnum(timeline.AdmissionMode, SessionAdmissionMode.ClassMembersOnly);
        state.ExamTitle = timeline.ExamTitle ?? state.ExamTitle;
        state.Subject = timeline.Subject ?? state.Subject;
        state.DurationMinutes = timeline.DurationMinutes;
        state.SessionStatus = ParseEnum<SessionStatus>(timeline.SessionStatus);
        state.ParticipantStatus = ParseEnum<ParticipantStatus>(timeline.ParticipantStatus);
        state.SubmissionStatus = ParseEnum(timeline.SubmissionStatus, SubmissionStatus.NotStarted);
        state.ApplyResubmitAuthority(timeline.ResubmitAllowed);
        state.CurrentAttempt = attempt;
        state.Revision = timeline.Revision;
    }

    private StudentExamFlowResolution Publish(
        StudentExamEntryPoint entryPoint,
        StudentExamFlowResolution resolution)
    {
        EnsureTrackedSession();
        state.RouteIntent = resolution.RouteKey;
        var key = $"{state.SessionId:N}:{state.Revision}:{resolution.RouteKey}";
        var shouldPublish = false;
        lock (lifecycleSync)
            shouldPublish = publishedNavigation.Add(key);
        if (shouldPublish)
            NavigationRequested?.Invoke(this, new(entryPoint, resolution));
        return resolution;
    }

    private StudentExamFlowSnapshot CurrentSnapshot() => new(
        state.HasSession,
        state.SessionStatus,
        state.ParticipantStatus,
        state.DeliveryType,
        state.SubmissionStatus,
        state.CurrentAttempt?.Status);

    private bool IsStaleSnapshot(SessionStatus? incomingStatus, long incomingRevision) =>
        incomingRevision < state.Revision
        || (incomingRevision == state.Revision
            && IsTerminal(state.SessionStatus)
            && !IsTerminal(incomingStatus));

    private void EnsureTrackedSession()
    {
        lock (lifecycleSync)
        {
            if (trackedSessionId == state.SessionId)
                return;
            trackedSessionId = state.SessionId;
            publishedNavigation.Clear();
            publishedNotifications.Clear();
        }
    }

    private void PublishTransitionNotification(
        StudentExamFlowSnapshot previous,
        StudentExamFlowSnapshot current,
        long revision)
    {
        if (previous.ParticipantStatus == ParticipantStatus.PendingApproval
            && current.ParticipantStatus == ParticipantStatus.Approved)
            NotifyOnce("approved", previous, current, revision, "Giáo viên đã duyệt yêu cầu tham gia.", "success");
        if (previous.ParticipantStatus == ParticipantStatus.PendingApproval
            && current.ParticipantStatus == ParticipantStatus.Rejected)
            NotifyOnce("rejected", previous, current, revision, "Giáo viên đã từ chối yêu cầu tham gia.", "danger");
        if (previous.SessionStatus is SessionStatus.Waiting or SessionStatus.Distributing
            && current.SessionStatus is SessionStatus.InProgress or SessionStatus.Paused)
            NotifyOnce("started", previous, current, revision, "Phiên thi đã bắt đầu.", "success");
        if (previous.SessionStatus is SessionStatus.InProgress or SessionStatus.Paused
            && current.SessionStatus == SessionStatus.Collecting)
            NotifyOnce("collecting", previous, current, revision, "Phiên thi đã chuyển sang thu bài.", "warning");
        if (!IsTerminal(previous.SessionStatus)
            && current.SessionStatus == SessionStatus.Finished)
            NotifyOnce("finished", previous, current, revision, "Phiên thi đã kết thúc.", "info");
        if (!IsTerminal(previous.SessionStatus)
            && current.SessionStatus == SessionStatus.Cancelled)
            NotifyOnce("cancelled", previous, current, revision, "Phiên thi đã bị hủy.", "warning");
        if (!IsTerminal(previous.SessionStatus)
            && current.SessionStatus == SessionStatus.Archived)
            NotifyOnce("archived", previous, current, revision, "Phiên thi đã được lưu trữ.", "info");
    }

    private void NotifyOnce(
        string transition,
        StudentExamFlowSnapshot previous,
        StudentExamFlowSnapshot current,
        long revision,
        string message,
        string tone)
    {
        var key = string.Join(
            ':',
            state.SessionId,
            transition,
            previous.ParticipantStatus,
            current.ParticipantStatus,
            previous.SessionStatus,
            current.SessionStatus,
            revision);
        var shouldPublish = false;
        lock (lifecycleSync)
            shouldPublish = publishedNotifications.Add(key);
        if (shouldPublish)
            toasts.Show(message, tone);
    }

    private void LogPostJoinFailure(StudentJoinPhase phase, Exception exception)
    {
        FrontendLogger.Log(exception, $"StudentLifecycle.{phase}");
        FrontendLogger.LogMessage(
            $"mode={state.AccessMode}; phase={phase}; session_id={state.SessionId}; "
            + $"participant_id={state.ParticipantId}; authority_mutation_committed="
            + $"{(state.JoinMutationCommitted ? "yes" : "no")}; "
            + $"exception_source={exception.GetType().Name}",
            "StudentLifecycle.PostJoin");
    }

    private static bool IsTerminal(SessionStatus? status) =>
        status is SessionStatus.Finished or SessionStatus.Cancelled or SessionStatus.Archived;

    private static T? ParseEnum<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) ? parsed : null;

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
}
