using System.IO;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentResultsTests
{
    [Fact]
    public async Task EmptyListShowsClearEmptyStateAndLoadingFinishes()
    {
        using var context = TestContext.Create();
        var pending = context.Results.DelayNext();
        using var viewModel = context.CreateViewModel();

        var initialization = viewModel.InitializeAsync(CancellationToken.None);
        await pending.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsLoading);
        pending.Complete([]);
        await initialization;

        Assert.Empty(viewModel.Results);
        Assert.True(viewModel.HasNoResults);
        Assert.Equal("Chưa có kết quả nào được giáo viên trả.", viewModel.EmptyStateText);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task QuizResultShowsScoreCommentAndQuestionOutcomes()
    {
        using var context = TestContext.Create(ReturnedQuiz());
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        var result = Assert.Single(viewModel.Results);
        Assert.Equal("Bài trắc nghiệm", result.TypeText);
        Assert.Equal("8/10", result.ScoreText);
        Assert.Equal("Tốt", result.CommentText);
        Assert.Collection(result.Questions,
            row => Assert.Equal("Đúng", row.OutcomeText),
            row => Assert.Equal("Sai", row.OutcomeText),
            row => Assert.Equal("Bỏ trống", row.OutcomeText));
    }

    [Fact]
    public async Task EssayResultShowsReturnedTimeAndDownloadsAttachment()
    {
        var essay = ReturnedEssay();
        using var context = TestContext.Create(essay);
        using var destination = new TemporaryDirectory();
        context.Folders.Folder = destination.Path;
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedResult = Assert.Single(viewModel.Results);
        viewModel.SelectedAttachment = Assert.Single(viewModel.SelectedResult.Attachments);
        Assert.True(viewModel.DownloadAttachmentCommand.CanExecute(null));
        viewModel.DownloadAttachmentCommand.Execute(null);
        await WaitUntilAsync(() => context.Results.Downloads.Count == 1 && !viewModel.IsBusy);

        var download = Assert.Single(context.Results.Downloads);
        Assert.Equal(essay.ResultId, download.ResultId);
        Assert.Equal(essay.Attachments[0].Id, download.AttachmentId);
        Assert.StartsWith(destination.Path, download.Destination, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Không có dữ liệu", viewModel.SelectedResult.ReturnedAtText);
    }

    [Fact]
    public async Task DraftAndGradedResultsAreNeverDisplayed()
    {
        var returned = ReturnedEssay();
        var draft = returned with { ResultId = Guid.NewGuid(), Status = GradingStatus.NotGraded };
        var graded = returned with { ResultId = Guid.NewGuid(), Status = GradingStatus.Graded };
        using var context = TestContext.Create(draft, graded, returned);
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(returned.ResultId, Assert.Single(viewModel.Results).ResultId);
    }

    [Fact]
    public async Task MatchingReturnedAndReopenedEventsRefreshButWrongParticipantDoesNot()
    {
        using var context = TestContext.Create(ReturnedQuiz());
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);
        var initialCalls = context.Results.LoadCalls;

        context.Realtime.Publish(new StudentRealtimeNotification(
            context.Session.SessionId!.Value,
            RealtimeEvents.GradeReturned,
            2,
            null,
            context.Session.ParticipantId));
        await WaitUntilAsync(() => context.Results.LoadCalls == initialCalls + 1);

        context.Realtime.Publish(new StudentRealtimeNotification(
            context.Session.SessionId.Value,
            "GradeReopened",
            3,
            null,
            context.Session.ParticipantId));
        await WaitUntilAsync(() => context.Results.LoadCalls == initialCalls + 2);

        context.Realtime.Publish(new StudentRealtimeNotification(
            context.Session.SessionId.Value,
            RealtimeEvents.QuizGradeReturned,
            4,
            null,
            Guid.NewGuid()));
        await Task.Delay(50);
        Assert.Equal(initialCalls + 2, context.Results.LoadCalls);
    }

    [Fact]
    public async Task PublicCloudAndOnlyLanResultsUseSamePresentationWithoutStudentIdInput()
    {
        var cloud = ReturnedQuiz() with { SourceMode = SessionAccessMode.PublicCloud };
        var lan = ReturnedEssay() with { SourceMode = SessionAccessMode.LanOnly };
        using var context = TestContext.Create(cloud, lan);
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Contains(viewModel.Results, result => result.SourceText == "PublicCloud");
        Assert.Contains(viewModel.Results, result => result.SourceText == "OnlyLAN");
        Assert.All(viewModel.Results, result => Assert.Equal("Đã trả", result.StatusText));
    }

    [Fact]
    public async Task LogoutClearsResultsAndAccountSwitchRejectsStaleResponse()
    {
        using var context = TestContext.Create(ReturnedEssay(title: "Tài khoản A"));
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.Single(viewModel.Results);

        var delayed = context.Results.DelayNext();
        viewModel.RetryCommand.Execute(null);
        await delayed.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        context.Results.DefaultResults = [ReturnedQuiz(title: "Tài khoản B")];
        context.SignIn(Guid.NewGuid());
        await WaitUntilAsync(() => viewModel.Results.SingleOrDefault()?.Title == "Tài khoản B");
        delayed.Complete([ReturnedEssay(title: "Phản hồi cũ")]);
        await Task.Delay(50);
        Assert.Equal("Tài khoản B", Assert.Single(viewModel.Results).Title);

        context.Auth.Clear();
        Assert.Empty(viewModel.Results);
        Assert.True(viewModel.HasNoResults);
    }

    [Fact]
    public async Task ErrorStateCanRetrySuccessfully()
    {
        using var context = TestContext.Create(ReturnedEssay());
        context.Results.FailuresRemaining = 1;
        using var viewModel = context.CreateViewModel();
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.HasError);
        Assert.NotEmpty(viewModel.ErrorMessage);
        Assert.True(viewModel.RetryCommand.CanExecute(null));
        viewModel.RetryCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.HasError && viewModel.Results.Count == 1 && !viewModel.IsBusy);
    }

    [Fact]
    public void NavigationViewAndIntegrationRequirementsAreWired()
    {
        var main = File.ReadAllText(FindFile("frontend", "src", "ExamTransfer.Desktop", "ViewModels", "MainViewModel.cs"));
        var window = File.ReadAllText(FindFile("frontend", "src", "ExamTransfer.Desktop", "Views", "MainWindow.xaml"));
        var view = File.ReadAllText(FindFile("frontend", "src", "ExamTransfer.Desktop", "Views", "StudentResultsView.xaml"));
        var requirements = File.ReadAllText(FindFile("B_INTEGRATION_REQUIREMENTS.md"));

        Assert.Contains("S-11", main, StringComparison.Ordinal);
        Assert.Contains("new StudentResultsViewModel", main, StringComparison.Ordinal);
        Assert.Contains("StudentResultsViewModel", window, StringComparison.Ordinal);
        Assert.Contains("StudentResultsView", window, StringComparison.Ordinal);
        Assert.Contains("DownloadAttachmentCommand", view, StringComparison.Ordinal);
        Assert.Contains("## B-09", requirements, StringComparison.Ordinal);
        Assert.Contains("tài khoản đã xác thực", requirements, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không nhận hoặc gửi `StudentId`", requirements, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chỉ liệt kê kết quả ở trạng thái `Returned`", requirements, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReturnedAtUtc", requirements, StringComparison.Ordinal);
        Assert.Contains("participant", requirements, StringComparison.OrdinalIgnoreCase);
    }

    private static StudentReturnedResult ReturnedQuiz(string title = "Quiz")
    {
        var choices = new[]
        {
            new QuizChoiceReviewDto(Guid.NewGuid(), "A", 1, true, true),
            new QuizChoiceReviewDto(Guid.NewGuid(), "B", 2, false, false)
        };
        var wrong = new[]
        {
            new QuizChoiceReviewDto(Guid.NewGuid(), "A", 1, true, false),
            new QuizChoiceReviewDto(Guid.NewGuid(), "B", 2, false, true)
        };
        var blank = new[]
        {
            new QuizChoiceReviewDto(Guid.NewGuid(), "A", 1, false, true)
        };
        return new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), title,
            StudentResultKind.Quiz, 2, GradingStatus.Returned, 8m, 10m, "Tốt",
            DateTimeOffset.UtcNow, SessionAccessMode.PublicCloud,
            [
                new(Guid.NewGuid(), "Đúng", 1, 1m, 1m, choices),
                new(Guid.NewGuid(), "Sai", 2, 1m, 0m, wrong),
                new(Guid.NewGuid(), "Trống", 3, 1m, 0m, blank)
            ], []);
    }

    private static StudentReturnedResult ReturnedEssay(string title = "Tự luận")
    {
        var id = Guid.NewGuid();
        return new(
            id, Guid.NewGuid(), Guid.NewGuid(), title,
            StudentResultKind.EssayFile, 1, GradingStatus.Returned, 7.5m, 10m,
            "Cần trình bày rõ hơn", DateTimeOffset.UtcNow, SessionAccessMode.LanOnly,
            [], [new(Guid.NewGuid(), "nhan-xet.pdf", 100, "application/pdf", $"api/v1/student/submissions/{id}/grade/attachments/file/content")]);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < timeoutAt) await Task.Delay(10);
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

    private sealed class TestContext : IDisposable
    {
        private readonly string authPath = Path.Combine(Path.GetTempPath(), $"ExamTransfer-B09-{Guid.NewGuid():N}.bin");
        private TestContext(params StudentReturnedResult[] results)
        {
            Auth = new(authPath);
            Results = new(results);
            Session = new()
            {
                SessionId = Guid.NewGuid(),
                ParticipantId = Guid.NewGuid(),
                AccessMode = SessionAccessMode.PublicCloud
            };
            SignIn(Guid.NewGuid());
        }
        public AppAuthSessionState Auth { get; }
        public RecordingStudentResultsService Results { get; }
        public StudentSessionState Session { get; }
        public RecordingRealtimeService Realtime { get; } = new();
        public RecordingFolderDialog Folders { get; } = new();
        public static TestContext Create(params StudentReturnedResult[] results) => new(results);
        public StudentResultsViewModel CreateViewModel() => new(Results, Auth, Session, Realtime, Folders);
        public void SignIn(Guid userId) => Auth.SetAuthenticated(
            new(userId, "student", "student@example.test", "Học sinh", "HS01", UserRole.Student,
                "org", Guid.NewGuid(), "device", DateTimeOffset.UtcNow.AddHours(1)),
            "token-" + userId,
            AuthSessionAuthority.Supabase);
        public void Dispose()
        {
            Auth.Clear();
            if (File.Exists(authPath)) File.Delete(authPath);
        }
    }

    private sealed class DelayedResults
    {
        private readonly TaskCompletionSource<IReadOnlyList<StudentReturnedResult>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<StudentReturnedResult>> WaitAsync() { Requested.TrySetResult(); return await completion.Task; }
        public void Complete(IReadOnlyList<StudentReturnedResult> value) => completion.TrySetResult(value);
    }

    private sealed record DownloadCall(Guid ResultId, Guid AttachmentId, string Destination);

    private sealed class RecordingStudentResultsService(params StudentReturnedResult[] results) : IStudentResultsService
    {
        private DelayedResults? delayed;
        public IReadOnlyList<StudentReturnedResult> DefaultResults { get; set; } = results;
        public int LoadCalls { get; private set; }
        public int FailuresRemaining { get; set; }
        public List<DownloadCall> Downloads { get; } = [];
        public DelayedResults DelayNext() => delayed = new();
        public async Task<IReadOnlyList<StudentReturnedResult>> GetReturnedResultsAsync(CancellationToken cancellationToken)
        {
            LoadCalls++;
            if (FailuresRemaining-- > 0) throw new InvalidOperationException("backend unavailable");
            if (delayed is { } current)
            {
                delayed = null;
                return await current.WaitAsync();
            }
            return DefaultResults;
        }
        public Task DownloadAttachmentAsync(StudentResultAttachment attachment, string destinationPath, CancellationToken cancellationToken)
        {
            Downloads.Add(new(attachment.ResultId, attachment.Id, destinationPath));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRealtimeService : IStudentRealtimeService
    {
        public bool IsConnected => true;
        public event EventHandler<string>? EventReceived;
        public event EventHandler<StudentRealtimeNotification>? NotificationReceived;
        public void Publish(StudentRealtimeNotification value) => NotificationReceived?.Invoke(this, value);
        public void Publish(string value) => EventReceived?.Invoke(this, value);
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class RecordingFolderDialog : IFolderDialogService
    {
        public string? Folder { get; set; }
        public string? PickFolder() => Folder;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ExamTransfer-B09-files-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
