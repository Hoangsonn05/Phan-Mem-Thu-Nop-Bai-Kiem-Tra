using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Desktop.Views;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

[Collection("WPF bulk archive")]
public sealed class StudentConnectWpfTests
{
    [Fact]
    public void PublicCloudRoomCode_RealControlUpdatesImmediatelyAndSurvivesModeToggle()
    {
        WpfTestHost.Run(() =>
        {
            var auth = StudentAuth();
            using var viewModel = new StudentConnectViewModel(
                new BackendClient("http://localhost:5048"),
                new StudentSessionState(),
                auth,
                new EmptyDiscovery(),
                publicCloudReady: () => true,
                joinPublicCloud: (_, _) => throw new InvalidOperationException("Join is not executed by this control-tree test."));
            var view = new StudentConnectView { DataContext = viewModel };
            var window = HiddenWindow(view);
            try
            {
                window.Show();
                viewModel.SelectedAccessMode = SessionAccessMode.PublicCloud;
                FlushBindings(view);

                var input = Assert.IsType<TextBox>(view.FindName("RoomCodeInput"));
                var validation = Assert.IsType<TextBlock>(view.FindName("RoomCodeValidation"));
                var join = Assert.IsType<Button>(view.FindName("JoinButton"));
                Assert.True(input.IsVisible);
                Assert.True(input.IsEnabled);
                Assert.False(input.IsReadOnly);
                Assert.False(join.IsEnabled);
                Assert.True(validation.IsVisible);

                input.Text = " room42 ";
                FlushBindings(view);

                Assert.Equal("ROOM42", viewModel.RoomCode);
                Assert.True(viewModel.JoinCommand.CanExecute(null));
                Assert.True(join.IsEnabled);

                viewModel.SelectedAccessMode = SessionAccessMode.LanOnly;
                FlushBindings(view);
                Assert.True(input.IsVisible);
                Assert.Equal("ROOM42", input.Text);

                viewModel.SelectedAccessMode = SessionAccessMode.PublicCloud;
                FlushBindings(view);
                Assert.True(input.IsVisible);
                Assert.Equal("ROOM42", viewModel.RoomCode);
                Assert.Single(
                    FindDescendants<TextBox>(view),
                    box => string.Equals(
                        System.Windows.Automation.AutomationProperties.GetName(box),
                        "Mã phòng",
                        StringComparison.Ordinal));

                input.Clear();
                FlushBindings(view);
                Assert.False(viewModel.JoinCommand.CanExecute(null));
                Assert.False(join.IsEnabled);
                Assert.False(string.IsNullOrWhiteSpace(validation.Text));
            }
            finally
            {
                window.Close();
                auth.Clear();
            }
        });
    }

    private static Window HiddenWindow(object content) => new()
    {
        Content = content,
        Width = 1280,
        Height = 850,
        ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10_000,
        Top = -10_000
    };

    private static void FlushBindings(DispatcherObject value)
    {
        value.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        if (value is FrameworkElement element)
            element.UpdateLayout();
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private static AppAuthSessionState StudentAuth()
    {
        var auth = new AppAuthSessionState();
        auth.SetAuthenticated(
            new CurrentAccountDto(
                Guid.NewGuid(),
                "student01",
                null,
                "Học sinh",
                "HS001",
                UserRole.Student,
                null,
                Guid.NewGuid(),
                "device-1",
                DateTimeOffset.UtcNow.AddHours(1),
                new DateOnly(2010, 1, 1)),
            "account-token");
        return auth;
    }

    private sealed class EmptyDiscovery : ILanDiscoveryService
    {
        public Task<LanDiscoverySnapshot> DiscoverSnapshotAsync(
            TimeSpan timeout,
            string? roomCode = null,
            CancellationToken ct = default) =>
            Task.FromResult(new LanDiscoverySnapshot([], [], "test", 0));

        public Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoveryServerDto>>([]);

        public Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenSessionDiscoveryDto>>([]);

        public Task<OpenSessionDiscoveryDto?> DiscoverByRoomCodeAsync(
            string roomCode,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<OpenSessionDiscoveryDto?>(null);
    }
}
