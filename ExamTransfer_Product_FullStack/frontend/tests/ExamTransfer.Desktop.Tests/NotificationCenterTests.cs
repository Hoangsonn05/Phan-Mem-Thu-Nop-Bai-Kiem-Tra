using System.Collections.Concurrent;
using System.IO;
using System.Xml.Linq;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.Services;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class NotificationCenterTests
{
    [Fact]
    public void Publish_OneNotificationShowsNonModalPopupState()
    {
        using var center = CreateCenter();
        var item = Notification("event-1", "Thông báo", NotificationTone.Information);

        Assert.True(center.Publish(item));

        Assert.Same(item, center.ViewModel.Current);
        Assert.True(center.ViewModel.IsVisible);
        Assert.Equal("info", center.ViewModel.ToneKey);
    }

    [Fact]
    public void Publish_MultipleNotificationsUsesFifoOrder()
    {
        using var center = CreateCenter();
        var first = Notification("event-1", "Thứ nhất", NotificationTone.Information);
        var second = Notification("event-2", "Thứ hai", NotificationTone.Success);
        var third = Notification("event-3", "Thứ ba", NotificationTone.Warning);

        center.Publish(first);
        center.Publish(second);
        center.Publish(third);

        Assert.Same(first, center.ViewModel.Current);
        center.ViewModel.CloseCommand.Execute(null);
        Assert.Same(second, center.ViewModel.Current);
        center.ViewModel.CloseCommand.Execute(null);
        Assert.Same(third, center.ViewModel.Current);
    }

    [Fact]
    public async Task AutoDismiss_ClosesCurrentAndShowsNext()
    {
        var delay = new ControlledNotificationDelay();
        using var center = CreateCenter(delay: delay);
        var first = Notification("event-1", "Tự đóng", NotificationTone.Information, autoDismiss: true);
        var second = Notification("event-2", "Tiếp theo", NotificationTone.Success);
        center.Publish(first);
        center.Publish(second);

        delay.CompleteNext();
        await WaitUntilAsync(() => ReferenceEquals(center.ViewModel.Current, second));

        Assert.Same(second, center.ViewModel.Current);
        Assert.True(center.ViewModel.IsVisible);
    }

    [Fact]
    public void CloseCommand_ClosesManuallyWithoutChangingApplicationFocus()
    {
        using var center = CreateCenter();
        center.Publish(Notification("event-1", "Đóng tay", NotificationTone.Warning));

        center.ViewModel.CloseCommand.Execute(null);

        Assert.Null(center.ViewModel.Current);
        Assert.False(center.ViewModel.IsVisible);
    }

    [Fact]
    public void Publish_DeduplicatesNonEmptyEventIdButAllowsIndependentEmptyIds()
    {
        using var center = CreateCenter();
        var original = Notification(" same-id ", "Gốc", NotificationTone.Information);
        var duplicate = Notification("same-id", "Trùng", NotificationTone.Error);

        Assert.True(center.Publish(original));
        Assert.False(center.Publish(duplicate));
        Assert.True(center.Publish(Notification("", "Không ID 1", NotificationTone.Information)));
        Assert.True(center.Publish(Notification("   ", "Không ID 2", NotificationTone.Information)));

        center.ViewModel.CloseCommand.Execute(null);
        Assert.Equal("Không ID 1", center.ViewModel.Current?.Title);
        center.ViewModel.CloseCommand.Execute(null);
        Assert.Equal("Không ID 2", center.ViewModel.Current?.Title);
    }

    [Fact]
    public async Task Publish_FromBackgroundThreadMarshalsViewModelChangesToDispatcher()
    {
        var dispatcher = new QueuedNotificationDispatcher();
        using var center = CreateCenter(dispatcher);
        var item = Notification("background", "Nền", NotificationTone.Success);

        var accepted = await Task.Run(() => center.Publish(item));

        Assert.True(accepted);
        Assert.Null(center.ViewModel.Current);
        Assert.True(dispatcher.PendingCount > 0);
        dispatcher.Drain();
        Assert.Same(item, center.ViewModel.Current);
        Assert.Equal(Environment.CurrentManagedThreadId, dispatcher.LastExecutionThreadId);
    }

    [Fact]
    public async Task Dispose_WhileWaitingCancelsTimerClearsQueueAndRejectsLaterNotifications()
    {
        var delay = new ControlledNotificationDelay();
        var center = CreateCenter(delay: delay);
        center.Publish(Notification("event-1", "Đang chờ", NotificationTone.Information, autoDismiss: true));
        center.Publish(Notification("event-2", "Trong queue", NotificationTone.Warning));

        center.Dispose();
        await WaitUntilAsync(() => delay.CancellationObserved);

        Assert.False(center.ViewModel.IsVisible);
        Assert.Null(center.ViewModel.Current);
        Assert.True(delay.CancellationObserved);
        Assert.False(center.Publish(Notification("event-3", "Sau dispose", NotificationTone.Error)));
        Assert.Null(center.ViewModel.Current);
    }

    [Theory]
    [InlineData(NotificationTone.Information, "info")]
    [InlineData(NotificationTone.Success, "success")]
    [InlineData(NotificationTone.Warning, "warning")]
    [InlineData(NotificationTone.Error, "danger")]
    public void Severity_MapsAllRequiredTones(NotificationTone tone, string expectedToneKey)
    {
        using var center = CreateCenter();

        center.Publish(Notification("tone", "Mức độ", tone));

        Assert.Equal(expectedToneKey, center.ViewModel.ToneKey);
    }

    [Fact]
    public void MainWindow_HostsFocusSafeOverlayWithoutModalOrActivationCalls()
    {
        var mainWindowPath = FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Views", "MainWindow.xaml");
        var notificationViewPath = FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Views", "NotificationCenterView.xaml");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var notificationView = File.ReadAllText(notificationViewPath);

        Assert.NotNull(XDocument.Load(mainWindowPath).Root);
        Assert.NotNull(XDocument.Load(notificationViewPath).Root);
        Assert.Contains("<views:NotificationCenterView", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AppServices.Notifications", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Panel.ZIndex=\"1000\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"False\"", notificationView, StringComparison.Ordinal);
        Assert.Contains("IsTabStop=\"False\"", notificationView, StringComparison.Ordinal);
        Assert.Contains("CloseCommand", notificationView, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", notificationView, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", mainWindow + notificationView, StringComparison.Ordinal);
        Assert.DoesNotContain("Activate", mainWindow + notificationView, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionService_LogsAndDoesNotReferenceRealtimeProviders()
    {
        var source = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Services", "NotificationCenter.cs"));

        Assert.Contains("FrontendLogger.LogMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Supabase", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MessageBox", source, StringComparison.Ordinal);
    }

    private static NotificationCenter CreateCenter(
        INotificationDispatcher? dispatcher = null,
        INotificationDelay? delay = null) =>
        new(
            dispatcher ?? new ImmediateNotificationDispatcher(),
            delay ?? new ControlledNotificationDelay());

    private static NotificationItem Notification(
        string eventId,
        string title,
        NotificationTone tone,
        bool autoDismiss = false) =>
        new(
            eventId,
            title,
            title + " message",
            tone,
            DateTimeOffset.UtcNow,
            autoDismiss ? TimeSpan.FromSeconds(3) : null,
            autoDismiss);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < timeoutAt)
            await Task.Delay(10);
        Assert.True(predicate());
    }

    private static string FindFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private sealed class ImmediateNotificationDispatcher : INotificationDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class QueuedNotificationDispatcher : INotificationDispatcher
    {
        private readonly ConcurrentQueue<Action> actions = new();
        public int PendingCount => actions.Count;
        public int LastExecutionThreadId { get; private set; }
        public void Post(Action action) => actions.Enqueue(action);
        public void Drain()
        {
            while (actions.TryDequeue(out var action))
            {
                LastExecutionThreadId = Environment.CurrentManagedThreadId;
                action();
            }
        }
    }

    private sealed class ControlledNotificationDelay : INotificationDelay
    {
        private readonly ConcurrentQueue<TaskCompletionSource> completions = new();
        public bool CancellationObserved { get; private set; }

        public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completions.Enqueue(completion);
            try
            {
                await completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public void CompleteNext()
        {
            Assert.True(SpinWait.SpinUntil(
                () => completions.TryDequeue(out var completion) && Complete(completion),
                TimeSpan.FromSeconds(2)));
        }

        private static bool Complete(TaskCompletionSource completion)
        {
            completion.TrySetResult();
            return true;
        }
    }
}
