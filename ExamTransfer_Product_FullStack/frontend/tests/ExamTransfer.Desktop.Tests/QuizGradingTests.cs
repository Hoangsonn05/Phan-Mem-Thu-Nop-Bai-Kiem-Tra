using System.Globalization;
using System.IO;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class QuizGradingTests
{
    [Fact]
    public async Task HundredQuizAttemptsStayBoundedAndDoNotLoadAnswersOnNavigation()
    {
        var data = Enumerable.Range(1, 100)
            .Select(index => QuizData.Create(studentCode: $"Q{index:D3}"))
            .ToArray();
        var api = new QuizBackendClient(data);
        using var viewModel = CreateViewModel(api);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(100, viewModel.Queue.Count);
        Assert.Null(viewModel.SelectedWorkItem);
        Assert.Empty(viewModel.QuizQuestions);
        Assert.DoesNotContain(api.GetPaths, path => path.Contains("quiz-attempts/", StringComparison.Ordinal));
        Assert.DoesNotContain(api.GetPaths, path => path.Contains("grading/queue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitOpenLoadsFiftyQuestionQuizWithoutBlockingListNavigation()
    {
        var questions = Enumerable.Range(1, 50)
            .Select(index => new QuizQuestionReviewDto(
                Guid.NewGuid(),
                $"Câu hỏi {index}",
                index,
                0.2m,
                0.2m,
                [
                    new QuizChoiceReviewDto(Guid.NewGuid(), "Đúng", 1, true, true),
                    new QuizChoiceReviewDto(Guid.NewGuid(), "Sai", 2, false, false)
                ]))
            .ToArray();
        var data = QuizData.Create(questions: questions);
        var api = new QuizBackendClient(data);
        using var viewModel = CreateViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedWorkItem = Assert.Single(viewModel.Queue);
        Assert.Empty(viewModel.QuizQuestions);
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.QuizQuestions.Count == 50 && !viewModel.IsDetailLoading);

        Assert.Equal(50, viewModel.QuizQuestions.Count);
    }

    [Fact]
    public void QuestionRowsPresentAuthoritativeCorrectWrongBlankAndMultipleAnswers()
    {
        var review = new QuizReviewPresentationModel(QuizData.Create().Quiz!);

        Assert.Equal(3, review.Questions.Count);
        Assert.Equal("Đúng", review.Questions[0].OutcomeText);
        Assert.True(review.Questions[0].IsCorrect);
        Assert.Contains("A. Hà Nội", review.Questions[0].StudentSelectionText, StringComparison.Ordinal);
        Assert.Contains("C. Đà Nẵng", review.Questions[0].StudentSelectionText, StringComparison.Ordinal);
        Assert.Contains("A. Hà Nội", review.Questions[0].CorrectAnswerText, StringComparison.Ordinal);
        Assert.Contains("C. Đà Nẵng", review.Questions[0].CorrectAnswerText, StringComparison.Ordinal);

        Assert.Equal("Sai", review.Questions[1].OutcomeText);
        Assert.True(review.Questions[1].IsIncorrect);
        Assert.Equal(0m, review.Questions[1].EarnedPoints);

        Assert.Equal("Bỏ trống", review.Questions[2].OutcomeText);
        Assert.True(review.Questions[2].IsBlank);
        Assert.Equal("Bỏ trống", review.Questions[2].StudentSelectionText);
    }

    [Fact]
    public void ReviewUsesAuthoritativeScoresAndHandlesEmptyQuiz()
    {
        var data = QuizData.Create(questions: []);
        var review = new QuizReviewPresentationModel(data.Quiz!);

        Assert.Equal(data.Quiz!.AutoScore, review.AutoScore);
        Assert.Equal(data.Quiz.Score, review.FinalScore);
        Assert.Equal(data.Quiz.MaxScore, review.MaxScore);
        Assert.Empty(review.Questions);
        Assert.True(review.HasNoQuestions);
        Assert.Equal("Bài trắc nghiệm chưa có câu hỏi.", review.EmptyStateText);
    }

    [Fact]
    public async Task SelectingQuizRequiresExplicitOpenBeforeLoadingReview()
    {
        var data = QuizData.Create();
        var api = new QuizBackendClient(data);
        using var viewModel = CreateViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedWorkItem = Assert.Single(viewModel.Queue);
        Assert.Null(viewModel.QuizReview);
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.QuizReview is not null && !viewModel.IsDetailLoading);

        Assert.Equal(data.Quiz!.AttemptId, viewModel.QuizReview!.AttemptId);
        Assert.Equal(data.Quiz.AutoScore, viewModel.QuizReview.AutoScore);
        Assert.Equal(data.Quiz.Score, viewModel.QuizReview.FinalScore);
        Assert.Equal(data.Quiz.MaxScore, viewModel.Editor.MaxScore);
        Assert.Equal(data.Quiz.Score?.ToString(CultureInfo.CurrentCulture), viewModel.Editor.ScoreText);
        Assert.Equal(data.Quiz.GeneralComment, viewModel.Editor.Comment);
        Assert.Equal(3, viewModel.QuizQuestions.Count);
        Assert.Empty(viewModel.Files);
    }

    [Fact]
    public async Task QuizValidationBlocksInvalidSaveAndSaveDoesNotReturnOrChangeAutoScore()
    {
        var data = QuizData.Create(status: GradingStatus.NotGraded);
        var api = new QuizBackendClient(data);
        using var viewModel = CreateViewModel(api);
        await LoadSelectedAsync(viewModel, data.WorkItem.Id);

        viewModel.Editor.ScoreText = (data.Quiz!.MaxScore + 0.1m).ToString(CultureInfo.CurrentCulture);
        Assert.False(viewModel.SaveGradeCommand.CanExecute(null));
        viewModel.SaveGradeCommand.Execute(null);
        Assert.Equal(0, api.SaveRequests);

        viewModel.Editor.ScoreText = 3.5m.ToString(CultureInfo.CurrentCulture);
        viewModel.Editor.Comment = "Nhận xét quiz";
        Assert.True(viewModel.SaveGradeCommand.CanExecute(null));
        viewModel.SaveGradeCommand.Execute(null);
        await WaitUntilAsync(() => api.SaveRequests == 1 && !viewModel.IsBusy);

        Assert.Equal(0, api.ReturnRequests);
        Assert.Equal(3.5m, api.LastSave?.Score);
        Assert.Equal(data.Quiz.AutoScore, viewModel.QuizReview?.AutoScore);
        Assert.Equal(3.5m, viewModel.QuizReview?.FinalScore);
        Assert.Equal(GradingStatus.Graded, viewModel.Detail?.Status);
        Assert.True(viewModel.ReturnGradeCommand.CanExecute(null));
    }

    [Fact]
    public async Task QuizReturnAndReopenRemainDistinctAndUpdateCommandState()
    {
        var data = QuizData.Create(status: GradingStatus.Graded);
        var api = new QuizBackendClient(data);
        var dialogs = new RecordingDialogService();
        using var viewModel = CreateViewModel(api, dialogs);
        await LoadSelectedAsync(viewModel, data.WorkItem.Id);

        Assert.True(viewModel.ReturnGradeCommand.CanExecute(null));
        viewModel.ReturnGradeCommand.Execute(null);
        await WaitUntilAsync(() => api.ReturnRequests == 1 && !viewModel.IsBusy);
        Assert.Equal(GradingStatus.Returned, viewModel.QuizReview?.Status);
        Assert.True(viewModel.ReopenGradeCommand.CanExecute(null));
        Assert.False(viewModel.SaveGradeCommand.CanExecute(null));

        viewModel.ReopenGradeCommand.Execute(null);
        await WaitUntilAsync(() => api.ReopenRequests == 1 && !viewModel.IsBusy);
        Assert.Equal(GradingStatus.InProgress, viewModel.QuizReview?.Status);
        Assert.Equal("Mở lại", viewModel.QuizReview?.StatusText);
        Assert.True(viewModel.SaveGradeCommand.CanExecute(null));
        Assert.Equal(1, api.ReturnRequests);
    }

    [Fact]
    public async Task SwitchingFileAndQuizClearsIncompatiblePresentationState()
    {
        var quiz = QuizData.Create(studentCode: "QUIZ01");
        var file = QuizData.CreateFile(studentCode: "FILE01");
        var api = new QuizBackendClient(quiz, file);
        using var viewModel = CreateViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedWorkItem = viewModel.Queue.Single(row => row.SubmissionId == quiz.WorkItem.Id);
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.QuizReview is not null);
        Assert.NotEmpty(viewModel.QuizQuestions);
        Assert.Empty(viewModel.Files);

        viewModel.SelectedWorkItem = viewModel.Queue.Single(row => row.SubmissionId == file.WorkItem.Id);
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Grade?.SubmissionId == file.WorkItem.Id && !viewModel.IsDetailLoading);
        Assert.Null(viewModel.QuizReview);
        Assert.Null(viewModel.QuizGrade);
        Assert.Empty(viewModel.QuizQuestions);
        Assert.Single(viewModel.Files);
        Assert.Equal(file.Grade!.GeneralComment, viewModel.Editor.Comment);

        viewModel.SelectedWorkItem = viewModel.Queue.Single(row => row.SubmissionId == quiz.WorkItem.Id);
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.QuizReview?.AttemptId == quiz.WorkItem.Id && !viewModel.IsDetailLoading);
        Assert.Null(viewModel.Grade);
        Assert.Empty(viewModel.Files);
        Assert.Null(viewModel.SelectedFile);
        Assert.Equal(quiz.Quiz!.GeneralComment, viewModel.Editor.Comment);
    }

    [Fact]
    public async Task StaleQuizResponseCannotOverwriteNewFileSelection()
    {
        var quiz = QuizData.Create(studentCode: "QUIZ01");
        var file = QuizData.CreateFile(studentCode: "FILE01");
        var api = new QuizBackendClient(quiz, file);
        var delayed = api.DelayQuiz(quiz.WorkItem.Id);
        using var viewModel = CreateViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.SelectedWorkItem = viewModel.Queue.Single(row => row.SubmissionId == quiz.WorkItem.Id);
        viewModel.OpenWorkItemCommand.Execute(null);
        await delayed.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedWorkItem = viewModel.Queue.Single(row => row.SubmissionId == file.WorkItem.Id);
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Grade?.SubmissionId == file.WorkItem.Id);
        delayed.Complete(quiz.Quiz!);
        await Task.Delay(50);

        Assert.Null(viewModel.QuizReview);
        Assert.Null(viewModel.QuizGrade);
        Assert.Equal(file.WorkItem.Id, viewModel.Detail?.SubmissionId);
        Assert.Equal(file.Grade!.GeneralComment, viewModel.Editor.Comment);
    }

    [Fact]
    public void ProductionXamlContainsQuizScoresAnswersOutcomesAndEmptyState()
    {
        var xaml = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Views", "GradingCenterView.xaml"));

        foreach (var expected in new[]
        {
            "QuizReview.AutoScore", "QuizReview.FinalScore", "QuizReview.MaxScore",
            "OptionsText", "StudentSelectionText", "CorrectAnswerText", "OutcomeText",
            "EarnedPoints", "HasNoQuestions", "EmptyStateText",
            "SaveGradeCommand", "ReturnGradeCommand", "ReopenGradeCommand"
        })
            Assert.Contains(expected, xaml, StringComparison.Ordinal);
    }

    private static GradingCenterViewModel CreateViewModel(
        QuizBackendClient api,
        IDialogService? dialogs = null) =>
        new(api, new EmptyFolderDialog(), dialogs ?? new RecordingDialogService(), new EmptyLocalFileLauncher());

    private static async Task LoadSelectedAsync(GradingCenterViewModel viewModel, Guid id)
    {
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedWorkItem = viewModel.Queue.Single(row => row.SubmissionId == id);
        viewModel.OpenWorkItemCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Detail?.SubmissionId == id && !viewModel.IsDetailLoading);
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

    private sealed class EmptyFolderDialog : IFolderDialogService { public string? PickFolder() => null; }
    private sealed class RecordingDialogService : IDialogService { public bool Confirm(string title, string message) => true; }
    private sealed class EmptyLocalFileLauncher : ILocalFileLauncher
    {
        public bool Exists(string path) => false;
        public void Open(string path) { }
    }

    private sealed class DelayedQuiz
    {
        private readonly TaskCompletionSource<QuizGradeDetailDto> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<QuizGradeDetailDto> WaitAsync() { Requested.TrySetResult(); return await completion.Task; }
        public void Complete(QuizGradeDetailDto quiz) => completion.TrySetResult(quiz);
    }

    private sealed class QuizBackendClient(params QuizData[] data) : IBackendClient
    {
        private readonly Dictionary<Guid, QuizData> byId = data.ToDictionary(item => item.WorkItem.Id);
        private readonly Dictionary<Guid, DelayedQuiz> delays = [];
        public int SaveRequests { get; private set; }
        public int ReturnRequests { get; private set; }
        public int ReopenRequests { get; private set; }
        public SaveQuizGradeRequest? LastSave { get; private set; }
        public List<string> GetPaths { get; } = [];
        public Uri BaseAddress { get; } = new("http://localhost:5048/");
        public bool HasTrustedAccountToken => true;
        public DelayedQuiz DelayQuiz(Guid id) => delays[id] = new();

        public Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default)
        {
            GetPaths.Add(path);
            if (typeof(T) == typeof(PagedResult<GradingWorkItemDto>))
                return Result<T>(new PagedResult<GradingWorkItemDto>(data.Select(item => item.WorkItem).ToArray(), 1, 100, data.Length));
            if (typeof(T) == typeof(PagedResult<SubmissionSummaryDto>))
                return Result<T>(new PagedResult<SubmissionSummaryDto>(data.Where(item => item.Submission is not null).Select(item => item.Submission!).ToArray(), 1, 100, data.Count(item => item.Submission is not null)));

            var id = Guid.Parse(path.Split('/')[4]);
            if (typeof(T) == typeof(QuizGradeDetailDto))
            {
                if (delays.TryGetValue(id, out var delayed)) return DelayedResult<T>(delayed);
                return Result<T>(byId[id].Quiz!);
            }
            if (typeof(T) == typeof(GradeDto)) return Result<T>(byId[id].Grade!);
            return Task.FromResult<ApiResponse<T>?>(null);
        }

        private static async Task<ApiResponse<T>?> DelayedResult<T>(DelayedQuiz delayed) =>
            ApiResponse<T>.Ok((T)(object)await delayed.WaitAsync(), "test");

        public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
        {
            var id = Guid.Parse(path.Split('/')[4]);
            var current = byId[id];
            if (request is SaveQuizGradeRequest quizSave)
            {
                SaveRequests++;
                LastSave = quizSave;
                var updated = current.Quiz! with
                {
                    Status = GradingStatus.Graded,
                    Score = quizSave.Score,
                    GeneralComment = quizSave.GeneralComment,
                    RowVersion = "saved"
                };
                byId[id] = current with { Quiz = updated };
                return Result<TResponse>(updated);
            }
            throw new InvalidOperationException("Unexpected save request.");
        }

        public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
        {
            var id = Guid.Parse(path.Split('/')[4]);
            var current = byId[id];
            var isReturn = path.EndsWith("/return", StringComparison.Ordinal);
            if (isReturn) ReturnRequests++; else ReopenRequests++;
            var updated = current.Quiz! with
            {
                Status = isReturn ? GradingStatus.Returned : GradingStatus.InProgress,
                ReturnedAtUtc = isReturn ? DateTimeOffset.UtcNow : null,
                RowVersion = isReturn ? "returned" : "reopened"
            };
            byId[id] = current with { Quiz = updated };
            return Result<TResponse>(updated);
        }

        private static Task<ApiResponse<T>?> Result<T>(object value) =>
            Task.FromResult<ApiResponse<T>?>(ApiResponse<T>.Ok((T)value, "test"));

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
        public Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DownloadVerifiedFileAsync(string path, string destinationPath, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public void SetBearerToken(string? token) { }
        public void SetAccountToken(string? token) { }
        public void SetParticipantToken(string? token) { }
    }

    private sealed record QuizData(
        GradingWorkItemDto WorkItem,
        QuizGradeDetailDto? Quiz,
        SubmissionSummaryDto? Submission,
        GradeDto? Grade)
    {
        public static QuizData Create(
            GradingStatus status = GradingStatus.Graded,
            string studentCode = "QUIZ01",
            IReadOnlyList<QuizQuestionReviewDto>? questions = null)
        {
            var id = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var participantId = Guid.NewGuid();
            questions ??=
            [
                Question(1, "Thành phố trực thuộc trung ương?", 2m, 2m,
                    Choice(1, "Hà Nội", true, true), Choice(2, "Huế", false, false), Choice(3, "Đà Nẵng", true, true)),
                Question(2, "2 + 2 = ?", 1m, 0m,
                    Choice(1, "3", true, false), Choice(2, "4", false, true)),
                Question(3, "Màu của bầu trời?", 1m, 0m,
                    Choice(1, "Xanh", false, true), Choice(2, "Đỏ", false, false))
            ];
            var submitted = DateTimeOffset.UtcNow.AddMinutes(-3);
            var work = new GradingWorkItemDto(
                id, GradingWorkItemType.QuizAttempt, sessionId, participantId,
                studentCode, "Học sinh " + studentCode, "Bài trắc nghiệm", submitted,
                status, 2m, status == GradingStatus.NotGraded ? null : 3m, 4m,
                null, Guid.NewGuid(), 1, false);
            var quiz = new QuizGradeDetailDto(
                id, sessionId, participantId, work.StudentCode, work.DisplayName, work.ExamTitle,
                2m, work.Score, 4m, status, "Nhận xét quiz", Guid.NewGuid(), submitted,
                status == GradingStatus.Returned ? DateTimeOffset.UtcNow : null, "quiz-rv", questions);
            return new(work, quiz, null, null);
        }

        public static QuizData CreateFile(string studentCode)
        {
            var id = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var participantId = Guid.NewGuid();
            var submitted = DateTimeOffset.UtcNow.AddMinutes(-2);
            var file = new SubmissionFileDto(Guid.NewGuid(), "essay.docx", 100, "sha", "application/octet-stream", 1, [0], TransferStatus.Completed, null);
            var work = new GradingWorkItemDto(
                id, GradingWorkItemType.FileSubmission, sessionId, participantId,
                studentCode, "Học sinh " + studentCode, "Bài file", submitted,
                GradingStatus.Graded, null, 8m, 10m, file.Id,
                Guid.NewGuid(), 1, false);
            var submission = new SubmissionSummaryDto(
                id, sessionId, participantId, studentCode, work.DisplayName, 1,
                SubmissionStatus.Submitted, submitted, submitted, submitted.AddMinutes(-1), false, "RC", true, [file]);
            var grade = new GradeDto(id, GradingStatus.Graded, 8m, 10m, [], "Nhận xét file", [], null, "file-rv")
            {
                SubmissionFiles = [file]
            };
            return new(work, null, submission, grade);
        }

        private static QuizQuestionReviewDto Question(
            int order,
            string text,
            decimal points,
            decimal? earned,
            params QuizChoiceReviewDto[] choices) =>
            new(Guid.NewGuid(), text, order, points, earned, choices);

        private static QuizChoiceReviewDto Choice(int order, string text, bool selected, bool? correct) =>
            new(Guid.NewGuid(), text, order, selected, correct);
    }
}
