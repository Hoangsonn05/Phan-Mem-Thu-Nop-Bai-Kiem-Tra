using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Desktop.Views;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

[CollectionDefinition("WPF bulk archive", DisableParallelization = true)]
public sealed class WpfBulkArchiveCollection;

[Collection("WPF bulk archive")]
public sealed class BulkArchiveCheckboxWpfTests
{
    [Fact]
    public void RealCheckboxWpfClick_ImmediatelyUpdatesExactRowsCountAndHeader()
    {
        RunOnSta(() =>
        {
            var application = EnsureApplicationResources();
            var first = MakeExam("First");
            var second = MakeExam("Second");
            var third = MakeExam("Third");
            var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
            {
                ExamResponses = [first, second, third],
                ExamDetailResponse = MakeDetail(first)
            };
            using var viewModel = new ExamManagementViewModel(api);
            viewModel.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

            var view = new ExamManagementView { DataContext = viewModel };
            var window = new Window
            {
                Content = view,
                Width = 1280,
                Height = 850,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10_000,
                Top = -10_000
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var grid = FindDescendants<DataGrid>(view)
                    .Single(item => ReferenceEquals(item.ItemsSource, viewModel.Exams));
                foreach (var row in viewModel.Exams)
                    grid.ScrollIntoView(row);
                window.UpdateLayout();

                var firstCheckbox = RowCheckbox(view, viewModel.Exams[0]);
                var secondCheckbox = RowCheckbox(view, viewModel.Exams[1]);
                var headerCheckbox = FindDescendants<CheckBox>(view)
                    .Single(item => ReferenceEquals(
                        item.Command,
                        viewModel.ToggleAllVisibleArchiveSelectionCommand));

                WpfClick(firstCheckbox);
                Assert.True(viewModel.Exams[0].IsChecked);
                Assert.Equal(1, viewModel.SelectedArchiveCount);
                Assert.True(viewModel.BulkArchiveCommand.CanExecute(null));

                WpfClick(secondCheckbox);
                Assert.True(viewModel.Exams[1].IsChecked);
                Assert.Equal(2, viewModel.SelectedArchiveCount);

                WpfClick(firstCheckbox);
                Assert.False(viewModel.Exams[0].IsChecked);
                Assert.True(viewModel.Exams[1].IsChecked);
                Assert.Equal(1, viewModel.SelectedArchiveCount);

                WpfClick(headerCheckbox);
                Assert.All(viewModel.Exams, row => Assert.True(row.IsChecked));
                Assert.Equal(3, viewModel.SelectedArchiveCount);
                Assert.True(viewModel.AllVisibleChecked);

                WpfClick(firstCheckbox);
                Assert.False(viewModel.Exams[0].IsChecked);
                Assert.True(viewModel.Exams[1].IsChecked);
                Assert.True(viewModel.Exams[2].IsChecked);
                Assert.Equal(2, viewModel.SelectedArchiveCount);
                Assert.False(viewModel.AllVisibleChecked);

                grid.SelectedItem = viewModel.Exams[2];
                grid.UpdateLayout();
                Assert.False(viewModel.Exams[0].IsChecked);
                Assert.True(viewModel.Exams[1].IsChecked);
                Assert.True(viewModel.Exams[2].IsChecked);
            }
            finally
            {
                window.Close();
                application.Shutdown();
            }
        });
    }

    private static CheckBox RowCheckbox(
        DependencyObject root,
        SelectableExamRow row) =>
        FindDescendants<CheckBox>(root)
            .Single(item => ReferenceEquals(item.DataContext, row)
                && ReferenceEquals(
                    item.CommandParameter,
                    row));

    private static void WpfClick(CheckBox checkBox)
    {
        Assert.True(checkBox.IsEnabled);
        var onClick = typeof(CheckBox).GetMethod(
            "OnClick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onClick);
        onClick.Invoke(checkBox, null);
        checkBox.Dispatcher.Invoke(
            static () => { },
            DispatcherPriority.DataBind);
    }

    private static Application EnsureApplicationResources()
    {
        var application = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        application.Resources["BooleanToVisibilityConverter"] =
            new BooleanToVisibilityConverter();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/ExamTransfer.Desktop;component/Themes/Palette.Light.xaml",
                UriKind.Absolute)
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/ExamTransfer.Desktop;component/Themes/Theme.xaml",
                UriKind.Absolute)
        });
        return application;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF STA test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static ExamSummaryDto MakeExam(string title) => new(
        Guid.NewGuid(),
        null,
        title,
        "Math",
        45,
        ExamDeliveryType.FileSubmission,
        ExamStatus.Draft,
        1,
        0,
        "rv-" + title);

    private static ExamDetailDto MakeDetail(ExamSummaryDto summary) => new(
        summary.Id,
        null,
        summary.Title,
        summary.Subject,
        null,
        summary.DurationMinutes,
        summary.DeliveryType,
        summary.Status,
        summary.Version,
        new FileRuleDto([".pdf"], 1024, 2048, 1, false, false),
        [],
        summary.RowVersion);
}
