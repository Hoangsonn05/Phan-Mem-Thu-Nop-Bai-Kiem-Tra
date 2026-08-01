namespace ExamTransfer.Desktop.Models;

public enum NotificationTone
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record NotificationItem(
    string EventId,
    string Title,
    string Message,
    NotificationTone Tone,
    DateTimeOffset OccurredAtUtc,
    TimeSpan? AutoDismissAfter,
    bool AutoDismiss);
