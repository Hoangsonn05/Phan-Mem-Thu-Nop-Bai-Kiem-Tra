using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

internal enum StudentRealtimeRefreshTarget
{
    Lifecycle,
    Message,
    Submission,
    Results
}

internal sealed record StudentRealtimeRouteContext(
    Guid SessionId,
    Guid ParticipantId,
    long Revision,
    SessionAccessMode AccessMode);

internal sealed record StudentRealtimeRoute(
    string SourceEventName,
    StudentRealtimeRefreshTarget RefreshTarget,
    NotificationItem Notification);

internal sealed class StudentRealtimeNotificationAdapter
{
    private static readonly TimeSpan NormalDismissAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ImportantDismissAfter = TimeSpan.FromSeconds(8);

    public bool TryAdapt(
        StudentRealtimeNotification source,
        StudentRealtimeRouteContext context,
        out StudentRealtimeRoute? route)
    {
        route = null;
        var eventName = source.EventName?.Trim();
        if (string.IsNullOrWhiteSpace(eventName)
            || source.SessionId == Guid.Empty
            || context.SessionId == Guid.Empty
            || context.ParticipantId == Guid.Empty
            || source.Revision <= 0)
            return false;

        var descriptor = BaseEventName(eventName) switch
        {
            RealtimeEvents.ParticipantApproved => new Descriptor(
                "Đã được duyệt vào phòng",
                "Giáo viên đã duyệt yêu cầu tham gia của bạn.",
                NotificationTone.Success,
                StudentRealtimeRefreshTarget.Lifecycle),
            "ParticipantRejected" => new Descriptor(
                "Yêu cầu tham gia bị từ chối",
                "Giáo viên đã từ chối yêu cầu tham gia của bạn.",
                NotificationTone.Error,
                StudentRealtimeRefreshTarget.Lifecycle),
            RealtimeEvents.TeacherMessageReceived => new Descriptor(
                "Tin nhắn mới từ giáo viên",
                "Bạn vừa nhận được một tin nhắn mới từ giáo viên.",
                NotificationTone.Information,
                StudentRealtimeRefreshTarget.Message),
            RealtimeEvents.SubmissionRejected => new Descriptor(
                "Bài nộp bị từ chối",
                "Trạng thái bài nộp đã thay đổi. Hãy kiểm tra lại thông tin chính thức.",
                NotificationTone.Warning,
                StudentRealtimeRefreshTarget.Submission),
            "ResubmitAllowed" => new Descriptor(
                "Đã được phép nộp lại",
                "Quyền nộp lại đã được cập nhật. Bạn có thể kiểm tra và gửi attempt mới.",
                NotificationTone.Success,
                StudentRealtimeRefreshTarget.Submission),
            RealtimeEvents.GradeReturned => new Descriptor(
                "Kết quả bài tự luận đã có",
                "Giáo viên đã trả kết quả. Hãy mở kết quả chính thức để xem chi tiết.",
                NotificationTone.Success,
                StudentRealtimeRefreshTarget.Results),
            RealtimeEvents.QuizGradeReturned => new Descriptor(
                "Kết quả trắc nghiệm đã có",
                "Giáo viên đã trả kết quả. Hãy mở kết quả chính thức để xem chi tiết.",
                NotificationTone.Success,
                StudentRealtimeRefreshTarget.Results),
            RealtimeEvents.QuizGradeReopened or "GradeReopened" => new Descriptor(
                "Kết quả đang được mở lại",
                "Giáo viên đã mở lại kết quả để điều chỉnh. Dữ liệu chính thức đã được làm mới.",
                NotificationTone.Information,
                StudentRealtimeRefreshTarget.Results),
            _ => null
        };
        if (descriptor is null)
            return false;

        var eventId = string.Join(
            ':',
            "student-realtime",
            source.SessionId.ToString("N"),
            context.ParticipantId.ToString("N"),
            source.Revision,
            eventName);
        if (string.IsNullOrWhiteSpace(eventId))
            return false;

        var dismissAfter = descriptor.Tone is NotificationTone.Warning or NotificationTone.Error
            ? ImportantDismissAfter
            : NormalDismissAfter;
        route = new(
            eventName,
            descriptor.RefreshTarget,
            new(
                eventId,
                descriptor.Title,
                descriptor.Message,
                descriptor.Tone,
                DateTimeOffset.UtcNow,
                dismissAfter,
                AutoDismiss: true));
        return true;
    }

    public static bool IsSupportedEventName(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            return false;
        return BaseEventName(eventName.Trim()) is
            RealtimeEvents.ParticipantApproved or
            "ParticipantRejected" or
            RealtimeEvents.TeacherMessageReceived or
            RealtimeEvents.SubmissionRejected or
            "ResubmitAllowed" or
            RealtimeEvents.GradeReturned or
            RealtimeEvents.QuizGradeReturned or
            RealtimeEvents.QuizGradeReopened or
            "GradeReopened";
    }

    private static string BaseEventName(string eventName)
    {
        var separator = eventName.IndexOf(':');
        return separator > 0 ? eventName[..separator] : eventName;
    }

    private sealed record Descriptor(
        string Title,
        string Message,
        NotificationTone Tone,
        StudentRealtimeRefreshTarget RefreshTarget);
}

internal sealed class StudentRealtimeNotificationRouter(
    StudentRealtimeNotificationAdapter adapter,
    INotificationCenter notifications,
    Func<StudentRealtimeRoute, CancellationToken, Task> refreshAuthoritativeState)
{
    private readonly object gate = new();
    private readonly SemaphoreSlim routeGate = new(1, 1);
    private readonly HashSet<string> handledEventIds = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid SessionId, Guid ParticipantId), long> highestRevisions = new();

    public async Task<bool> RouteAsync(
        StudentRealtimeNotification source,
        StudentRealtimeRouteContext context,
        CancellationToken cancellationToken)
    {
        if (source.SessionId != context.SessionId
            || source.ParticipantId.HasValue && source.ParticipantId != context.ParticipantId)
            return false;

        await routeGate.WaitAsync(cancellationToken);
        try
        {
            var identity = (context.SessionId, context.ParticipantId);
            long highestRevision;
            lock (gate)
                highestRevision = Math.Max(
                    context.Revision,
                    highestRevisions.GetValueOrDefault(identity));
            if (source.Revision <= highestRevision
                || !adapter.TryAdapt(source, context, out var route)
                || route is null)
                return false;

            lock (gate)
            {
                if (!handledEventIds.Add(route.Notification.EventId))
                    return false;
            }

            try
            {
                await refreshAuthoritativeState(route, cancellationToken);
            }
            catch
            {
                lock (gate)
                    handledEventIds.Remove(route.Notification.EventId);
                throw;
            }

            lock (gate)
                highestRevisions[identity] = source.Revision;
            return notifications.Publish(route.Notification);
        }
        finally
        {
            routeGate.Release();
        }
    }
}
