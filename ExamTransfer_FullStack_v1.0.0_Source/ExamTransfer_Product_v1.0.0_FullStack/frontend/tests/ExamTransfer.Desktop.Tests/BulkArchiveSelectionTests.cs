using System.IO;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class BulkArchiveSelectionTests
{
    [Fact]
    public async Task Classes_ToggleSingleAndDisjointRows_SendOnlyExactCheckedIds()
    {
        var rows = new[]
        {
            MakeClass("C-1"),
            MakeClass("C-2"),
            MakeClass("C-3")
        };
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ClassResponses = rows,
            ClassDetailResponse = MakeClassDetail(rows[0])
        };
        var dialogs = new RecordingDialogService(false);
        using var viewModel = new ClassManagementViewModel(api, dialogs);
        await viewModel.InitializeAsync(CancellationToken.None);

        AssertInitialState(viewModel.SelectedArchiveCount, viewModel.BulkArchiveCommand);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Classes[0]);
        Assert.Equal(1, viewModel.SelectedArchiveCount);
        Assert.True(viewModel.BulkArchiveCommand.CanExecute(null));
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Classes[2]);
        Assert.Equal(2, viewModel.SelectedArchiveCount);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Classes[0]);
        Assert.Equal(1, viewModel.SelectedArchiveCount);

        viewModel.SelectedClass = viewModel.Classes[1];
        Assert.False(viewModel.Classes[1].IsChecked);
        Assert.True(viewModel.Classes[2].IsChecked);
        Assert.Equal(1, viewModel.SelectedArchiveCount);

        viewModel.BulkArchiveCommand.Execute(null);
        Assert.Empty(BulkRequests(api));
        Assert.True(viewModel.Classes[2].IsChecked);

        dialogs.Result = true;
        viewModel.BulkArchiveCommand.Execute(null);
        WaitForBulkRequests(api, 1);
        Assert.Equal([rows[2].Id], BulkRequests(api)[0].Ids);
        Assert.Equal(0, viewModel.SelectedArchiveCount);

        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Classes[0]);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Classes[2]);
        viewModel.BulkArchiveCommand.Execute(null);
        WaitForBulkRequests(api, 2);
        Assert.Equal([rows[0].Id, rows[2].Id], BulkRequests(api)[1].Ids);
    }

    [Fact]
    public async Task Exams_ToggleSingleAndDisjointRows_SendOnlyExactCheckedIds()
    {
        var rows = new[]
        {
            MakeExam("Exam 1"),
            MakeExam("Exam 2"),
            MakeExam("Exam 3")
        };
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = rows,
            ExamDetailResponse = MakeExamDetail(rows[0])
        };
        var dialogs = new RecordingDialogService(false);
        using var viewModel = new ExamManagementViewModel(api, dialogs);
        await viewModel.InitializeAsync(CancellationToken.None);

        AssertInitialState(viewModel.SelectedArchiveCount, viewModel.BulkArchiveCommand);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Exams[0]);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Exams[2]);
        Assert.Equal(2, viewModel.SelectedArchiveCount);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Exams[0]);
        Assert.Equal(1, viewModel.SelectedArchiveCount);

        viewModel.SelectedExam = viewModel.Exams[1];
        Assert.False(viewModel.Exams[1].IsChecked);
        Assert.True(viewModel.Exams[2].IsChecked);

        viewModel.BulkArchiveCommand.Execute(null);
        Assert.Empty(BulkRequests(api));
        Assert.True(viewModel.Exams[2].IsChecked);

        dialogs.Result = true;
        viewModel.BulkArchiveCommand.Execute(null);
        WaitForBulkRequests(api, 1);
        Assert.Equal([rows[2].Id], BulkRequests(api)[0].Ids);

        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Exams[0]);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Exams[2]);
        viewModel.BulkArchiveCommand.Execute(null);
        WaitForBulkRequests(api, 2);
        Assert.Equal([rows[0].Id, rows[2].Id], BulkRequests(api)[1].Ids);
    }

    [Fact]
    public async Task Sessions_ToggleAndHeaderSelectOnlyFinishedOrCancelled_AndSendExactIds()
    {
        var exam = MakeExam("Published") with { Status = ExamStatus.Published };
        var rows = new[]
        {
            MakeSession("DONE-1", SessionStatus.Finished, exam.Id),
            MakeSession("WAIT-1", SessionStatus.Waiting, exam.Id),
            MakeSession("CANCEL-1", SessionStatus.Cancelled, exam.Id),
            MakeSession("RUN-1", SessionStatus.InProgress, exam.Id),
            MakeSession("PAUSE-1", SessionStatus.Paused, exam.Id),
            MakeSession("COLLECT-1", SessionStatus.Collecting, exam.Id),
            MakeSession("DRAFT-1", SessionStatus.Draft, exam.Id),
            MakeSession("DIST-1", SessionStatus.Distributing, exam.Id),
            MakeSession("ARCHIVED-1", SessionStatus.Archived, exam.Id)
        };
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = rows
        };
        var dialogs = new RecordingDialogService(false);
        using var viewModel = new SessionManagementViewModel(api, dialogs);
        await viewModel.InitializeAsync(CancellationToken.None);

        AssertInitialState(viewModel.SelectedArchiveCount, viewModel.BulkArchiveCommand);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Sessions[0]);
        Assert.Equal(1, viewModel.SelectedArchiveCount);
        Assert.False(viewModel.ToggleArchiveSelectionCommand.CanExecute(viewModel.Sessions[1]));
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Sessions[1]);
        Assert.False(viewModel.Sessions[1].IsChecked);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Sessions[2]);
        Assert.Equal(2, viewModel.SelectedArchiveCount);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Sessions[0]);
        Assert.Equal(1, viewModel.SelectedArchiveCount);

        viewModel.SelectedSession = viewModel.Sessions[3];
        Assert.False(viewModel.Sessions[3].IsChecked);
        Assert.True(viewModel.Sessions[2].IsChecked);

        viewModel.BulkArchiveCommand.Execute(null);
        Assert.Empty(BulkRequests(api));
        Assert.True(viewModel.Sessions[2].IsChecked);

        dialogs.Result = true;
        viewModel.BulkArchiveCommand.Execute(null);
        WaitForBulkRequests(api, 1);
        Assert.Equal([rows[2].Id], BulkRequests(api)[0].Ids);

        viewModel.ToggleAllVisibleArchiveSelectionCommand.Execute(null);
        Assert.True(viewModel.Sessions[0].IsChecked);
        Assert.True(viewModel.Sessions[2].IsChecked);
        Assert.All(
            new[] { 1, 3, 4, 5, 6, 7, 8 },
            index => Assert.False(viewModel.Sessions[index].IsChecked));
        Assert.True(viewModel.AllVisibleChecked);
        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Sessions[0]);
        Assert.False(viewModel.AllVisibleChecked);
        Assert.True(viewModel.Sessions[2].IsChecked);

        viewModel.ToggleArchiveSelectionCommand.Execute(viewModel.Sessions[0]);
        viewModel.BulkArchiveCommand.Execute(null);
        WaitForBulkRequests(api, 2);
        Assert.Equal([rows[0].Id, rows[2].Id], BulkRequests(api)[1].Ids);
    }

    private static void AssertInitialState(int count, System.Windows.Input.ICommand command)
    {
        Assert.Equal(0, count);
        Assert.False(command.CanExecute(null));
    }

    private static List<BulkArchiveRequest> BulkRequests(RecordingBackendClient api) =>
        api.PostRequests.OfType<BulkArchiveRequest>().ToList();

    private static void WaitForBulkRequests(RecordingBackendClient api, int count) =>
        Assert.True(SpinWait.SpinUntil(
            () => BulkRequests(api).Count >= count,
            TimeSpan.FromSeconds(2)));

    private static ClassSummaryDto MakeClass(string code) => new(
        Guid.NewGuid(),
        "Lớp " + code,
        code,
        "2026-2027",
        ClassStatus.Active,
        0,
        "rv-" + code);

    private static ClassDetailDto MakeClassDetail(ClassSummaryDto summary) => new(
        summary.Id,
        summary.Name,
        summary.Code,
        summary.SchoolYear,
        null,
        summary.Status,
        [],
        summary.RowVersion);

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

    private static ExamDetailDto MakeExamDetail(ExamSummaryDto summary) => new(
        summary.Id,
        summary.ClassId,
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

    private static SessionSummaryDto MakeSession(
        string roomCode,
        SessionStatus status,
        Guid examId) => new(
        Guid.NewGuid(),
        examId,
        "Exam",
        roomCode,
        status,
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        new(0, 0, 0, 0, 0, 0, 0),
        1,
        "rv-" + roomCode);

    private sealed class RecordingDialogService(bool result) : IDialogService
    {
        public bool Result { get; set; } = result;
        public int Calls { get; private set; }

        public bool Confirm(string title, string message)
        {
            Calls++;
            return Result;
        }
    }
}

public sealed class BulkArchiveXamlSourceTests
{
    [Fact]
    public void AllThreeRowCheckboxesUseExplicitRowCommandsAndFullButtonLabels()
    {
        var views = FindViewsDirectory();
        foreach (var fileName in new[]
                 {
                     "ClassManagementView.xaml",
                     "ExamManagementView.xaml",
                     "SessionManagementView.xaml"
                 })
        {
            var source = File.ReadAllText(Path.Combine(views, fileName));
            Assert.Contains(
                "IsChecked=\"{Binding IsChecked, Mode=OneWay}\"",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "DataContext.ToggleArchiveSelectionCommand",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "CommandParameter=\"{Binding}\"",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "DataContext.ToggleAllVisibleArchiveSelectionCommand",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "Run Text=\"Xóa mục đã chọn (\"",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "Run Text=\"{Binding SelectedArchiveCount, Mode=OneWay}\"",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "IsChecked=\"{Binding IsChecked, Mode=TwoWay}\"",
                source,
                StringComparison.Ordinal);
        }
    }

    private static string FindViewsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "frontend",
                "src",
                "ExamTransfer.Desktop",
                "Views");
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Không tìm thấy thư mục Views.");
    }
}
