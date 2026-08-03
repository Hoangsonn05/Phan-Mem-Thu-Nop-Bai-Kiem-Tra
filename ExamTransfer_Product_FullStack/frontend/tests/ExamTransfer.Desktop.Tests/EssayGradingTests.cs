using System.Globalization;
using System.IO;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class EssayGradingTests
{
    [Fact]
    public async Task DelayedAuthoritativeListYieldsImmediatelyAndDoesNotCallLegacyOrDetail()
    {
        var data = GradingTestData.Create(GradingStatus.NotGraded);
        var api = new GradingBackendClient(data);
        var delayed = api.DelayPage(1);
        using var viewModel = CreateViewModel(api);

        var initialization = viewModel.InitializeAsync(CancellationToken.None);
        await delayed.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(initialization.IsCompleted);
        Assert.True(viewModel.IsBusy);
        Assert.Empty(viewModel.Queue);
        Assert.Null(viewModel.SelectedWorkItem);
        Assert.Equal(0, api.LegacyQueueRequests);
        Assert.Equal(0, api.DetailRequests);

        delayed.Complete();
        await initialization;

        Assert.False(viewModel.IsBusy);
        Assert.Single(viewModel.Queue);
        Assert.Single(api.WorkItemPaths);
    }

    [Fact]
    public async Task EmptyQueueAndApiFailureBothResetBusyState()
    {
        var emptyApi = new GradingBackendClient();
        using var empty = CreateViewModel(emptyApi);
        await empty.InitializeAsync(CancellationToken.None);
        Assert.Empty(empty.Queue);
        Assert.False(empty.IsBusy);

        var failingApi = new GradingBackendClient { ListFailure = new InvalidOperationException("delayed failure") };
        using var failing = CreateViewModel(failingApi);
        await failing.InitializeAsync(CancellationToken.None);
        Assert.False(failing.IsBusy);
        Assert.Equal("danger", failing.StatusTone);
    }

    [Fact]
    public async Task DisposedViewModelDoesNotApplyDelayedListResponse()
    {
        var api = new GradingBackendClient(GradingTestData.Create(GradingStatus.NotGraded));
        var delayed = api.DelayPage(1);
        var viewModel = CreateViewModel(api);
        var initialization = viewModel.InitializeAsync(CancellationToken.None);
        await delayed.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.Dispose();
        await initialization;

        Assert.True(delayed.CancellationObserved);
        Assert.Empty(viewModel.Queue);
        Assert.Null(viewModel.SelectedWorkItem);
    }

    [Fact]
    public async Task HundredRowsAreBoundedAndChangingPageCancelsPreviousRequest()
    {
        var data = Enumerable.Range(1, 101)
            .Select(index => GradingTestData.Create(GradingStatus.NotGraded, $"HS{index:D3}"))
            .ToArray();
        var api = new GradingBackendClient(data);
        using var viewModel = CreateViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(100, viewModel.Queue.Count);
        Assert.Equal(101, viewModel.TotalCount);
        Assert.True(viewModel.CanMoveNext);

        var delayedSecondPage = api.DelayPage(2);
        viewModel.NextPageCommand.Execute(null);
        await delayedSecondPage.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, viewModel.CurrentPage);
        Assert.True(viewModel.IsBusy);

        viewModel.PreviousPageCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CurrentPage == 1 && !viewModel.IsBusy);

        Assert.True(delayedSecondPage.CancellationObserved);
        Assert.Equal(100, viewModel.Queue.Count);
        Assert.Equal(2, api.WorkItemPaths.Count(path => path.Contains("page=1", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SelectingSubmissionRequiresExplicitOpenBeforeLoadingDetailAndFiles()
    {
        var data = GradingTestData.Create(GradingStatus.NotGraded);
        var api = new GradingBackendClient(data);
        using var viewModel = CreateViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedWorkItem = viewModel.Queue.Single();
        Assert.Null(viewModel.Detail);
        Assert.Equal(0, api.DetailRequests);
        Assert.True(viewModel.OpenWorkItemCommand.CanExecute(null));
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Detail is not null && !viewModel.IsDetailLoading);

        Assert.Equal(data.WorkItem.Id, viewModel.Detail!.SubmissionId);
        Assert.Equal(data.WorkItem.DisplayName, viewModel.Detail.StudentName);
        Assert.Equal(data.Submission.AttemptNumber, viewModel.Detail.AttemptNumber);
        Assert.Equal(data.Submission.ServerReceivedAtUtc, viewModel.Detail.SubmittedAtUtc);
        Assert.Equal(data.Submission.IsLate, viewModel.Detail.IsLate);
        Assert.Equal(data.Grade.Status, viewModel.Detail.Status);
        Assert.Equal(data.Submission.Files.Count, viewModel.Files.Count);
        Assert.DoesNotContain(api.DetailPaths, path => path.Contains("preview", StringComparison.OrdinalIgnoreCase));
        Assert.False(viewModel.SaveGradeCommand.CanExecute(null));
    }

    [Fact]
    public async Task RapidSelectionIgnoresStaleDetailResponse()
    {
        var first = GradingTestData.Create(GradingStatus.NotGraded, "HS001");
        var second = GradingTestData.Create(GradingStatus.Graded, "HS002");
        var api = new GradingBackendClient(first, second);
        var delayed = api.DelayGrade(first.WorkItem.Id);
        using var viewModel = CreateViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedWorkItem = viewModel.Queue.Single(row => row.SubmissionId == first.WorkItem.Id);
        viewModel.OpenWorkItemCommand.Execute(null);
        await delayed.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedWorkItem = viewModel.Queue.Single(row => row.SubmissionId == second.WorkItem.Id);
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Detail?.SubmissionId == second.WorkItem.Id);
        delayed.Complete(first.Grade);
        await Task.Delay(50);

        Assert.Equal(second.WorkItem.Id, viewModel.Detail?.SubmissionId);
        Assert.Equal(second.WorkItem.DisplayName, viewModel.Detail?.StudentName);
        Assert.Equal(second.Grade.Score?.ToString(CultureInfo.CurrentCulture), viewModel.Editor.ScoreText);
    }

    [Fact]
    public async Task DownloadAndOpenUseIdentifierAndOnlyExistingLocalFile()
    {
        using var destination = new TemporaryDirectory();
        var data = GradingTestData.Create(GradingStatus.NotGraded);
        var api = new GradingBackendClient(data);
        var files = new RecordingLocalFileLauncher();
        var folders = new RecordingFolderDialog(destination.Path);
        using var viewModel = CreateViewModel(api, folders: folders, localFiles: files);
        await LoadSelectedAsync(viewModel);
        viewModel.SelectedFile = viewModel.Files[0];

        viewModel.DownloadFileCommand.Execute(null);
        await WaitUntilAsync(() => api.Downloads.Count == 1 && !viewModel.IsBusy);

        var download = Assert.Single(api.Downloads);
        Assert.Contains($"submissions/{data.WorkItem.Id}/files/{data.Submission.Files[0].Id}/content", download.Path, StringComparison.Ordinal);
        Assert.Equal(1, folders.PickCount);
        Assert.Equal(download.Destination, viewModel.SelectedFile.LocalPath);

        files.ExistingPaths.Add(download.Destination);
        Assert.True(viewModel.OpenLocalFileCommand.CanExecute(null));
        viewModel.OpenLocalFileCommand.Execute(null);
        Assert.Equal(download.Destination, Assert.Single(files.OpenedPaths));

        files.ExistingPaths.Clear();
        Assert.False(viewModel.OpenLocalFileCommand.CanExecute(null));
        viewModel.OpenLocalFileCommand.Execute(null);
        Assert.Single(files.OpenedPaths);
        Assert.Contains("không tồn tại", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditorValidationUsesCurrentCultureAndRejectsInvalidNumbers()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("vi-VN");
            var editor = new GradingEditorState();
            editor.Load(null, 10m, string.Empty, GradingStatus.NotGraded);

            editor.ScoreText = "8,5";
            Assert.True(editor.IsValid);
            Assert.Equal(8.5m, editor.ParsedScore);

            foreach (var invalid in new[] { "", "NaN", "Infinity", "-1", "10,1" })
            {
                editor.ScoreText = invalid;
                Assert.False(editor.IsValid);
                Assert.False(string.IsNullOrWhiteSpace(editor.ValidationMessage));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task InvalidScoreDisablesSaveAndDoesNotSendRequest()
    {
        var data = GradingTestData.Create(GradingStatus.NotGraded);
        var api = new GradingBackendClient(data);
        using var viewModel = CreateViewModel(api);
        await LoadSelectedAsync(viewModel);

        viewModel.Editor.ScoreText = "not-a-number";
        Assert.False(viewModel.SaveGradeCommand.CanExecute(null));
        viewModel.SaveGradeCommand.Execute(null);

        Assert.Equal(0, api.SaveRequests);
        Assert.Equal(0, api.ReturnRequests);
    }

    [Fact]
    public async Task SaveCreatesDraftWithoutReturningThenReturnPublishesSeparately()
    {
        var data = GradingTestData.Create(GradingStatus.NotGraded);
        var api = new GradingBackendClient(data);
        var dialogs = new RecordingDialogService { Result = true };
        using var viewModel = CreateViewModel(api, dialogs: dialogs);
        await LoadSelectedAsync(viewModel);
        var score = 8.5m;
        viewModel.Editor.ScoreText = score.ToString(CultureInfo.CurrentCulture);
        viewModel.Editor.Comment = "Nhận xét nhiều dòng\nDòng hai";

        Assert.True(viewModel.SaveGradeCommand.CanExecute(null));
        Assert.False(viewModel.ReturnGradeCommand.CanExecute(null));
        viewModel.SaveGradeCommand.Execute(null);
        await WaitUntilAsync(() => api.SaveRequests == 1 && !viewModel.IsBusy);

        Assert.Equal(0, api.ReturnRequests);
        Assert.Equal(GradingStatus.Graded, viewModel.Detail?.Status);
        Assert.Equal(score, viewModel.Grade?.Score);
        Assert.True(viewModel.ReturnGradeCommand.CanExecute(null));
        Assert.False(viewModel.Editor.IsDirty);

        viewModel.ReturnGradeCommand.Execute(null);
        await WaitUntilAsync(() => api.ReturnRequests == 1 && !viewModel.IsBusy);
        Assert.Equal(GradingStatus.Returned, viewModel.Detail?.Status);
        Assert.False(viewModel.SaveGradeCommand.CanExecute(null));
        Assert.True(viewModel.ReopenGradeCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReopenReturnedGradeEnablesEditingWithoutReturningAgain()
    {
        var data = GradingTestData.Create(GradingStatus.Returned);
        var api = new GradingBackendClient(data);
        using var viewModel = CreateViewModel(api);
        await LoadSelectedAsync(viewModel);

        Assert.True(viewModel.ReopenGradeCommand.CanExecute(null));
        Assert.False(viewModel.SaveGradeCommand.CanExecute(null));
        viewModel.ReopenGradeCommand.Execute(null);
        await WaitUntilAsync(() => api.ReopenRequests == 1 && !viewModel.IsBusy);

        Assert.Equal(GradingStatus.InProgress, viewModel.Detail?.Status);
        Assert.Equal("Mở lại", viewModel.Detail?.StatusText);
        Assert.True(viewModel.SaveGradeCommand.CanExecute(null));
        Assert.False(viewModel.ReturnGradeCommand.CanExecute(null));
        Assert.Equal(0, api.ReturnRequests);
    }

    [Fact]
    public void ProductionXamlContainsEssayListDetailFilesAndDistinctCommands()
    {
        var xaml = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Views", "GradingCenterView.xaml"));
        var launcher = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Services", "LocalFileLauncher.cs"));

        foreach (var binding in new[]
        {
            "StudentCode", "StudentName", "AttemptNumber", "SubmittedAtUtc", "IsLate", "StatusText",
            "SaveGradeCommand", "ReturnGradeCommand", "ReopenGradeCommand",
            "DownloadFileCommand", "OpenLocalFileCommand", "OpenWorkItemCommand"
        })
            Assert.Contains(binding, xaml, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", launcher, StringComparison.Ordinal);
        Assert.Contains("File.Exists", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Supabase", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EnableRowVirtualization=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizationMode=\"Recycling\"", xaml, StringComparison.Ordinal);
    }

    private static GradingCenterViewModel CreateViewModel(
        GradingBackendClient api,
        IFolderDialogService? folders = null,
        IDialogService? dialogs = null,
        ILocalFileLauncher? localFiles = null) =>
        new(api, folders, dialogs, localFiles);

    private static async Task LoadSelectedAsync(GradingCenterViewModel viewModel)
    {
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedWorkItem = Assert.Single(viewModel.Queue);
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Detail is not null && !viewModel.IsDetailLoading);
    }

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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ExamTransfer-B07-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed class RecordingFolderDialog(string? folder) : IFolderDialogService
    {
        public int PickCount { get; private set; }
        public string? PickFolder() { PickCount++; return folder; }
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public bool Result { get; set; }
        public bool Confirm(string title, string message) => Result;
    }

    private sealed class RecordingLocalFileLauncher : ILocalFileLauncher
    {
        public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> OpenedPaths { get; } = [];
        public bool Exists(string path) => ExistingPaths.Contains(path);
        public void Open(string path) => OpenedPaths.Add(path);
    }

    private sealed record DownloadCall(string Path, string Destination);

    private sealed class DelayedGrade
    {
        private readonly TaskCompletionSource<GradeDto> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GradeDto> WaitAsync() { Requested.TrySetResult(); return await completion.Task; }
        public void Complete(GradeDto grade) => completion.TrySetResult(grade);
    }

    private sealed class DelayedWorkItems(PagedResult<GradingWorkItemDto> result)
    {
        private readonly TaskCompletionSource<PagedResult<GradingWorkItemDto>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async Task<PagedResult<GradingWorkItemDto>> WaitAsync(CancellationToken cancellationToken)
        {
            Requested.TrySetResult();
            using var registration = cancellationToken.Register(() =>
            {
                CancellationObserved = true;
                completion.TrySetCanceled(cancellationToken);
            });
            return await completion.Task;
        }

        public void Complete() => completion.TrySetResult(result);
    }

    private sealed class GradingBackendClient(params GradingTestData[] data) : IBackendClient
    {
        private readonly Dictionary<Guid, GradingTestData> byId = data.ToDictionary(item => item.WorkItem.Id);
        private readonly Dictionary<Guid, DelayedGrade> delays = [];
        private readonly Dictionary<int, DelayedWorkItems> pageDelays = [];
        public int SaveRequests { get; private set; }
        public int ReturnRequests { get; private set; }
        public int ReopenRequests { get; private set; }
        public int DetailRequests { get; private set; }
        public int LegacyQueueRequests { get; private set; }
        public Exception? ListFailure { get; set; }
        public List<string> WorkItemPaths { get; } = [];
        public List<string> DetailPaths { get; } = [];
        public List<DownloadCall> Downloads { get; } = [];
        public Uri BaseAddress { get; } = new("http://localhost:5048/");
        public bool HasTrustedAccountToken => true;
        public DelayedGrade DelayGrade(Guid id) => delays[id] = new();
        public DelayedWorkItems DelayPage(int page) =>
            pageDelays[page] = new(BuildPage(page));

        public Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default)
        {
            if (typeof(T) == typeof(PagedResult<GradingWorkItemDto>))
            {
                WorkItemPaths.Add(path);
                if (ListFailure is not null) throw ListFailure;
                var page = ReadPage(path);
                if (pageDelays.TryGetValue(page, out var delayedPage))
                    return DelayedWorkItemsResult<T>(delayedPage, ct);
                return Result<T>(BuildPage(page));
            }
            if (typeof(T) == typeof(PagedResult<SubmissionSummaryDto>))
            {
                LegacyQueueRequests++;
                return Result<T>(new PagedResult<SubmissionSummaryDto>(data.Select(x => x.Submission).ToArray(), 1, 100, data.Length));
            }
            var id = Guid.Parse(path.Split('/')[4]);
            if (typeof(T) == typeof(GradeDto))
            {
                DetailRequests++;
                DetailPaths.Add(path);
                if (delays.TryGetValue(id, out var delayed))
                    return DelayedResult<T>(delayed);
                return Result<T>(byId[id].Grade);
            }
            return Task.FromResult<ApiResponse<T>?>(null);
        }

        private static async Task<ApiResponse<T>?> DelayedResult<T>(DelayedGrade delayed) =>
            ApiResponse<T>.Ok((T)(object)await delayed.WaitAsync(), "test");

        private static async Task<ApiResponse<T>?> DelayedWorkItemsResult<T>(
            DelayedWorkItems delayed,
            CancellationToken cancellationToken) =>
            ApiResponse<T>.Ok((T)(object)await delayed.WaitAsync(cancellationToken), "test");

        private PagedResult<GradingWorkItemDto> BuildPage(int page) => new(
            data.Skip((page - 1) * 100).Take(100).Select(x => x.WorkItem).ToArray(),
            page,
            100,
            data.Length);

        private static int ReadPage(string path)
        {
            var marker = "page=";
            var start = path.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return 1;
            start += marker.Length;
            var end = path.IndexOf('&', start);
            var value = end < 0 ? path[start..] : path[start..end];
            return int.Parse(value, CultureInfo.InvariantCulture);
        }

        public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
        {
            SaveRequests++;
            var id = Guid.Parse(path.Split('/')[4]);
            var current = byId[id];
            var save = Assert.IsType<SaveGradeRequest>(request);
            var updated = current.Grade with
            {
                Status = GradingStatus.Graded,
                Score = save.Score,
                MaxScore = save.MaxScore,
                GeneralComment = save.GeneralComment,
                RowVersion = "saved"
            };
            byId[id] = current with { Grade = updated };
            return Result<TResponse>(updated);
        }

        public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
        {
            var id = Guid.Parse(path.Split('/')[4]);
            var current = byId[id];
            GradeDto updated;
            if (path.EndsWith("/return", StringComparison.Ordinal))
            {
                ReturnRequests++;
                updated = current.Grade with { Status = GradingStatus.Returned, ReturnedAtUtc = DateTimeOffset.UtcNow, RowVersion = "returned" };
            }
            else
            {
                ReopenRequests++;
                updated = current.Grade with { Status = GradingStatus.InProgress, ReturnedAtUtc = null, RowVersion = "reopened" };
            }
            byId[id] = current with { Grade = updated };
            return Result<TResponse>(updated);
        }

        private static Task<ApiResponse<T>?> Result<T>(object value) =>
            Task.FromResult<ApiResponse<T>?>(ApiResponse<T>.Ok((T)value, "test"));

        public Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Downloads.Add(new(path, destinationPath));
            return Task.CompletedTask;
        }

        public bool TrySetBaseAddress(string hostOrUrl, int port, out string? error) { error = null; return true; }
        public Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SystemStatusDto>?>(null);
        public Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<DashboardSummaryDto>?>(null);
        public Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<ClassSummaryDto>>?>(null);
        public Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<ExamSummaryDto>>?>(null);
        public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<SessionSummaryDto>>?>(null);
        public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ApiResponse<SessionDetailDto>?>(null);
        public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<SubmissionSummaryDto>>?>(null);
        public Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<CloudSyncStatusDto>?>(null);
        public Task<ApiResponse<SettingsDto>?> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SettingsDto>?>(null);
        public Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
        public Task<ApiResponse<object>?> UploadChunkAsync(string path, Stream content, long contentLength, string? sha256 = null, CancellationToken ct = default) => Task.FromResult<ApiResponse<object>?>(null);
        public Task DownloadVerifiedFileAsync(string path, string destinationPath, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public void SetBearerToken(string? token) { }
        public void SetAccountToken(string? token) { }
        public void SetParticipantToken(string? token) { }
    }

    private sealed record GradingTestData(
        GradingWorkItemDto WorkItem,
        SubmissionSummaryDto Submission,
        GradeDto Grade)
    {
        public static GradingTestData Create(GradingStatus status, string studentCode = "HS001")
        {
            var id = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var participantId = Guid.NewGuid();
            var submitted = DateTimeOffset.UtcNow.AddMinutes(-5);
            var files = new[]
            {
                new SubmissionFileDto(Guid.NewGuid(), "essay.docx", 1234, "sha", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 1, [0], TransferStatus.Completed, null),
                new SubmissionFileDto(Guid.NewGuid(), "source.unknown", 456, "sha2", "application/octet-stream", 1, [0], TransferStatus.Completed, null)
            };
            var work = new GradingWorkItemDto(
                id, GradingWorkItemType.FileSubmission, sessionId, participantId,
                studentCode, "Học sinh " + studentCode, "Bài tự luận", submitted,
                status, null, status == GradingStatus.NotGraded ? null : 7.5m, 10m, files[0].Id,
                Guid.NewGuid(), 2, true);
            var submission = new SubmissionSummaryDto(
                id, sessionId, participantId, studentCode, work.DisplayName, 2,
                SubmissionStatus.Submitted, submitted.AddSeconds(-2), submitted,
                submitted.AddMinutes(-1), true, "RC", true, files);
            var grade = new GradeDto(
                id, status, work.Score, 10m, [], "Nhận xét", [],
                status == GradingStatus.Returned ? DateTimeOffset.UtcNow : null,
                status == GradingStatus.NotGraded ? "new" : "rv")
            {
                SubmissionFiles = files
            };
            return new(work, submission, grade);
        }
    }
}
