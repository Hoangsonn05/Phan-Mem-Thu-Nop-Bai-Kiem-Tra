using System.Windows;
using System.Windows.Threading;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.ViewModels;

namespace ExamTransfer.Desktop.Services;

public sealed class NotificationCenter : INotificationCenter
{
    private static readonly TimeSpan DefaultAutoDismissAfter = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private readonly Queue<NotificationItem> pending = new();
    private readonly HashSet<string> seenEventIds = new(StringComparer.Ordinal);
    private readonly INotificationDispatcher dispatcher;
    private readonly INotificationDelay delay;
    private NotificationItem? current;
    private CancellationTokenSource? autoDismissCts;
    private bool disposed;

    public NotificationCenter()
        : this(new WpfNotificationDispatcher(), new TaskNotificationDelay())
    {
    }

    internal NotificationCenter(
        INotificationDispatcher dispatcher,
        INotificationDelay delay)
    {
        this.dispatcher = dispatcher;
        this.delay = delay;
        ViewModel = new NotificationCenterViewModel(CloseCurrent);
    }

    public NotificationCenterViewModel ViewModel { get; }

    public bool Publish(NotificationItem notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var eventId = notification.EventId?.Trim() ?? string.Empty;

        lock (gate)
        {
            if (disposed)
                return false;
            if (eventId.Length > 0 && !seenEventIds.Add(eventId))
            {
                FrontendLogger.LogMessage(
                    $"Notification duplicate ignored: {eventId}",
                    nameof(NotificationCenter));
                return false;
            }

            pending.Enqueue(notification);
        }

        FrontendLogger.LogMessage(
            $"Notification queued [{notification.Tone}] {eventId}: {notification.Title}",
            nameof(NotificationCenter));
        dispatcher.Post(ShowNextOnDispatcher);
        return true;
    }

    private void ShowNextOnDispatcher()
    {
        NotificationItem notification;
        CancellationTokenSource? timer = null;

        lock (gate)
        {
            if (disposed || current is not null || pending.Count == 0)
                return;

            notification = pending.Dequeue();
            current = notification;
            if (notification.AutoDismiss)
            {
                timer = new CancellationTokenSource();
                autoDismissCts = timer;
            }
        }

        ViewModel.Show(notification);
        if (timer is not null)
            _ = AutoDismissAsync(notification, ResolveDismissDelay(notification), timer.Token);
    }

    private async Task AutoDismissAsync(
        NotificationItem expected,
        TimeSpan dismissAfter,
        CancellationToken cancellationToken)
    {
        try
        {
            await delay.WaitAsync(dismissAfter, cancellationToken);
            dispatcher.Post(() => CloseOnDispatcher(expected));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FrontendLogger.Log(exception, "NotificationCenter.AutoDismiss");
        }
    }

    private static TimeSpan ResolveDismissDelay(NotificationItem notification) =>
        notification.AutoDismissAfter is { } configured && configured > TimeSpan.Zero
            ? configured
            : DefaultAutoDismissAfter;

    private void CloseCurrent() => dispatcher.Post(() => CloseOnDispatcher(expected: null));

    private void CloseOnDispatcher(NotificationItem? expected)
    {
        CancellationTokenSource? timer;

        lock (gate)
        {
            if (disposed
                || current is null
                || expected is not null && !ReferenceEquals(current, expected))
                return;

            current = null;
            timer = autoDismissCts;
            autoDismissCts = null;
        }

        timer?.Cancel();
        timer?.Dispose();
        ViewModel.Hide();
        ShowNextOnDispatcher();
    }

    public void Dispose()
    {
        CancellationTokenSource? timer;

        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            pending.Clear();
            seenEventIds.Clear();
            current = null;
            timer = autoDismissCts;
            autoDismissCts = null;
        }

        timer?.Cancel();
        timer?.Dispose();
        dispatcher.Post(ViewModel.Hide);
        FrontendLogger.LogMessage("Notification center disposed.", nameof(NotificationCenter));
    }

    private sealed class WpfNotificationDispatcher : INotificationDispatcher
    {
        public void Post(Action action)
        {
            var applicationDispatcher = Application.Current?.Dispatcher;
            if (applicationDispatcher is null
                || applicationDispatcher.HasShutdownStarted
                || applicationDispatcher.HasShutdownFinished)
            {
                action();
                return;
            }

            if (applicationDispatcher.CheckAccess())
                action();
            else
                _ = applicationDispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
        }
    }

    private sealed class TaskNotificationDelay : INotificationDelay
    {
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }
}
