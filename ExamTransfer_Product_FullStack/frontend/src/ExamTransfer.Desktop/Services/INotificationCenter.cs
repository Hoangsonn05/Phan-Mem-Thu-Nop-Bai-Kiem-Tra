using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.ViewModels;

namespace ExamTransfer.Desktop.Services;

public interface INotificationCenter : IDisposable
{
    NotificationCenterViewModel ViewModel { get; }
    bool Publish(NotificationItem notification);
}

internal interface INotificationDispatcher
{
    void Post(Action action);
}

internal interface INotificationDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}
