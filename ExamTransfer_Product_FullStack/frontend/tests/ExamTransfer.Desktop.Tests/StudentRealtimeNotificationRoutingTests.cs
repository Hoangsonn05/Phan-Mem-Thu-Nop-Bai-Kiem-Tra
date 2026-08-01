using System.IO;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentRealtimeNotificationRoutingTests
{
    [Theory]
    [InlineData(RealtimeEvents.ParticipantApproved, (int)StudentRealtimeRefreshTarget.Lifecycle, NotificationTone.Success)]
    [InlineData("ParticipantRejected", (int)StudentRealtimeRefreshTarget.Lifecycle, NotificationTone.Error)]
    [InlineData(RealtimeEvents.TeacherMessageReceived, (int)StudentRealtimeRefreshTarget.Message, NotificationTone.Information)]
    [InlineData(RealtimeEvents.SubmissionRejected, (int)StudentRealtimeRefreshTarget.Submission, NotificationTone.Warning)]
    [InlineData("ResubmitAllowed", (int)StudentRealtimeRefreshTarget.Submission, NotificationTone.Success)]
    [InlineData(RealtimeEvents.GradeReturned, (int)StudentRealtimeRefreshTarget.Results, NotificationTone.Success)]
    [InlineData(RealtimeEvents.QuizGradeReturned, (int)StudentRealtimeRefreshTarget.Results, NotificationTone.Success)]
    [InlineData(RealtimeEvents.QuizGradeReopened, (int)StudentRealtimeRefreshTarget.Results, NotificationTone.Information)]
    [InlineData("GradeReopened", (int)StudentRealtimeRefreshTarget.Results, NotificationTone.Information)]
    public void Adapter_UsesExistingEventNamesAndRequiredBehaviorGroups(
        string eventName,
        int expectedRefresh,
        NotificationTone expectedTone)
    {
        var context = Context(SessionAccessMode.LanOnly);
        var notification = Incoming(context, eventName, revision: 11);

        var adapted = new StudentRealtimeNotificationAdapter().TryAdapt(
            notification,
            context,
            out var route);

        Assert.True(adapted);
        Assert.NotNull(route);
        Assert.Equal((StudentRealtimeRefreshTarget)expectedRefresh, route.RefreshTarget);
        Assert.Equal(expectedTone, route.Notification.Tone);
        Assert.False(string.IsNullOrWhiteSpace(route.Notification.EventId));
        Assert.DoesNotContain("/10", route.Notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouteAsync_ValidSessionAndParticipantRefreshesBeforePopup()
    {
        var order = new List<string>();
        var notifications = new RecordingNotificationCenter(() => order.Add("popup"));
        var router = Router(notifications, (route, _) =>
        {
            order.Add("refresh:" + route.RefreshTarget);
            return Task.CompletedTask;
        });
        var context = Context(SessionAccessMode.LanOnly);

        var routed = await router.RouteAsync(
            Incoming(context, RealtimeEvents.ParticipantApproved, revision: 11),
            context,
            CancellationToken.None);

        Assert.True(routed);
        Assert.Equal(["refresh:Lifecycle", "popup"], order);
        Assert.Single(notifications.Items);
    }

    [Fact]
    public async Task RouteAsync_WrongParticipantIsIgnoredWithoutRefreshOrPopup()
    {
        var refreshCount = 0;
        var notifications = new RecordingNotificationCenter();
        var router = Router(notifications, (_, _) =>
        {
            refreshCount++;
            return Task.CompletedTask;
        });
        var context = Context(SessionAccessMode.LanOnly);
        var incoming = Incoming(context, RealtimeEvents.SubmissionRejected, revision: 11) with
        {
            ParticipantId = Guid.NewGuid()
        };

        Assert.False(await router.RouteAsync(incoming, context, CancellationToken.None));
        Assert.Equal(0, refreshCount);
        Assert.Empty(notifications.Items);
    }

    [Fact]
    public async Task RouteAsync_WrongSessionIsIgnoredWithoutRefreshOrPopup()
    {
        var refreshCount = 0;
        var notifications = new RecordingNotificationCenter();
        var router = Router(notifications, (_, _) =>
        {
            refreshCount++;
            return Task.CompletedTask;
        });
        var context = Context(SessionAccessMode.PublicCloud);
        var incoming = Incoming(context, RealtimeEvents.GradeReturned, revision: 11) with
        {
            SessionId = Guid.NewGuid()
        };

        Assert.False(await router.RouteAsync(incoming, context, CancellationToken.None));
        Assert.Equal(0, refreshCount);
        Assert.Empty(notifications.Items);
    }

    [Fact]
    public async Task RouteAsync_DeduplicatesEventAndRejectsOldRevisionButAcceptsNewRevision()
    {
        var refreshCount = 0;
        var notifications = new RecordingNotificationCenter();
        var router = Router(notifications, (_, _) =>
        {
            refreshCount++;
            return Task.CompletedTask;
        });
        var context = Context(SessionAccessMode.LanOnly);
        var current = Incoming(context, RealtimeEvents.TeacherMessageReceived, revision: 11);

        Assert.True(await router.RouteAsync(current, context, CancellationToken.None));
        Assert.False(await router.RouteAsync(current, context, CancellationToken.None));
        Assert.False(await router.RouteAsync(
            Incoming(context, RealtimeEvents.SubmissionRejected, revision: 10),
            context,
            CancellationToken.None));
        Assert.True(await router.RouteAsync(
            Incoming(context, RealtimeEvents.SubmissionRejected, revision: 12),
            context,
            CancellationToken.None));

        Assert.Equal(2, refreshCount);
        Assert.Equal(2, notifications.Items.Count);
    }

    [Theory]
    [InlineData(RealtimeEvents.GradeReturned, (int)StudentRealtimeRefreshTarget.Results)]
    [InlineData(RealtimeEvents.QuizGradeReturned, (int)StudentRealtimeRefreshTarget.Results)]
    [InlineData(RealtimeEvents.SubmissionRejected, (int)StudentRealtimeRefreshTarget.Submission)]
    [InlineData("ResubmitAllowed", (int)StudentRealtimeRefreshTarget.Submission)]
    public async Task RouteAsync_RequestsAuthoritativeRefreshForPersonalStateEvents(
        string eventName,
        int expectedTarget)
    {
        StudentRealtimeRefreshTarget? refreshed = null;
        var notifications = new RecordingNotificationCenter();
        var router = Router(notifications, (route, _) =>
        {
            refreshed = route.RefreshTarget;
            return Task.CompletedTask;
        });
        var context = Context(SessionAccessMode.PublicCloud);

        Assert.True(await router.RouteAsync(
            Incoming(context, eventName, revision: 11),
            context,
            CancellationToken.None));

        Assert.Equal((StudentRealtimeRefreshTarget)expectedTarget, refreshed);
        Assert.Single(notifications.Items);
    }

    [Fact]
    public async Task OnlyLanAndPublicCloudUseTheSameAdapterOutput()
    {
        var lanNotifications = new RecordingNotificationCenter();
        var cloudNotifications = new RecordingNotificationCenter();
        var lanContext = Context(SessionAccessMode.LanOnly);
        var cloudContext = lanContext with { AccessMode = SessionAccessMode.PublicCloud };

        Assert.True(await Router(lanNotifications).RouteAsync(
            Incoming(lanContext, RealtimeEvents.QuizGradeReturned, revision: 11),
            lanContext,
            CancellationToken.None));
        Assert.True(await Router(cloudNotifications).RouteAsync(
            Incoming(cloudContext, RealtimeEvents.QuizGradeReturned, revision: 11),
            cloudContext,
            CancellationToken.None));

        var lan = Assert.Single(lanNotifications.Items);
        var cloud = Assert.Single(cloudNotifications.Items);
        Assert.Equal(lan.EventId, cloud.EventId);
        Assert.Equal(lan.Title, cloud.Title);
        Assert.Equal(lan.Message, cloud.Message);
        Assert.Equal(lan.Tone, cloud.Tone);
    }

    [Fact]
    public async Task RouteAsync_FromBackgroundThreadIsSafeAndUnsupportedEventDoesNotCrash()
    {
        var notifications = new RecordingNotificationCenter();
        var router = Router(notifications);
        var context = Context(SessionAccessMode.PublicCloud);

        var routed = await Task.Run(() => router.RouteAsync(
            Incoming(context, RealtimeEvents.ParticipantApproved, revision: 11),
            context,
            CancellationToken.None));
        var unsupported = await Record.ExceptionAsync(() => router.RouteAsync(
            Incoming(context, "UnknownStudentEvent", revision: 12),
            context,
            CancellationToken.None));

        Assert.True(routed);
        Assert.Null(unsupported);
        Assert.Single(notifications.Items);
    }

    [Fact]
    public void ProductionMainViewModelRoutesThroughAdapterAndNotificationCenter()
    {
        var source = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("StudentRealtimeNotificationRouter", source, StringComparison.Ordinal);
        Assert.Contains("AppServices.Notifications", source, StringComparison.Ordinal);
        Assert.Contains("RefreshStudentRealtimeStateAsync", source, StringComparison.Ordinal);
    }

    private static StudentRealtimeNotificationRouter Router(
        INotificationCenter notifications,
        Func<StudentRealtimeRoute, CancellationToken, Task>? refresh = null) =>
        new(
            new StudentRealtimeNotificationAdapter(),
            notifications,
            refresh ?? ((_, _) => Task.CompletedTask));

    private static StudentRealtimeRouteContext Context(SessionAccessMode accessMode) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 10, accessMode);

    private static StudentRealtimeNotification Incoming(
        StudentRealtimeRouteContext context,
        string eventName,
        long revision) =>
        new(
            context.SessionId,
            eventName,
            revision,
            null,
            context.ParticipantId);

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

    private sealed class RecordingNotificationCenter(Action? onPublish = null) : INotificationCenter
    {
        private readonly object gate = new();
        public NotificationCenterViewModel ViewModel { get; } = new(() => { });
        public List<NotificationItem> Items { get; } = [];

        public bool Publish(NotificationItem notification)
        {
            lock (gate) Items.Add(notification);
            onPublish?.Invoke();
            return true;
        }

        public void Dispose() { }
    }
}
