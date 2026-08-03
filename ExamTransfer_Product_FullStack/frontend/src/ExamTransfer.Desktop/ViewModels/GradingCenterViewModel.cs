using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class GradingCenterViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly IFolderDialogService folders;
    private readonly IDialogService dialogs;
    private readonly ILocalFileLauncher localFiles;
    private CancellationTokenSource? listLoadCts;
    private long listLoadVersion;
    private CancellationTokenSource? detailLoadCts;
    private long detailLoadVersion;
    private EssaySubmissionReviewRow? selectedWorkItem;
    private EssaySubmissionDetail? detail;
    private SubmissionFilePresentationModel? selectedFile;
    private GradeDto? grade;
    private QuizGradeDetailDto? quizGrade;
    private QuizReviewPresentationModel? quizReview;
    private bool isDetailLoading;
    private int currentPage = 1;
    private int loadedPage = 1;
    private int totalCount;

    private const int PageSize = 100;

    public GradingCenterViewModel(
        IBackendClient api,
        IFolderDialogService? folders = null,
        IDialogService? dialogs = null,
        ILocalFileLauncher? localFiles = null)
    {
        this.api = api;
        this.folders = folders ?? AppServices.Folders;
        this.dialogs = dialogs ?? AppServices.Dialogs;
        this.localFiles = localFiles ?? new LocalFileLauncher();
        Editor.PropertyChanged += OnEditorPropertyChanged;

        RefreshCommand = new AsyncRelayCommand(() => LoadPageAsync(CurrentPage, DisposeToken), () => !IsBusy);
        PreviousPageCommand = new AsyncRelayCommand(() => ChangePageAsync(CurrentPage - 1), () => CanMovePrevious);
        NextPageCommand = new AsyncRelayCommand(() => ChangePageAsync(CurrentPage + 1), () => CanMoveNext);
        OpenWorkItemCommand = new RelayCommand(
            () => OpenSelectedWorkItemAsync().SafeFireAndForget("GradingCenter.OpenSelection"),
            CanOpenWorkItem);
        SaveGradeCommand = new AsyncRelayCommand(SaveGradeAsync, CanSaveGrade);
        ReturnGradeCommand = new AsyncRelayCommand(ReturnGradeAsync, CanReturnGrade);
        ReopenGradeCommand = new AsyncRelayCommand(ReopenGradeAsync, CanReopenGrade);
        DownloadFileCommand = new AsyncRelayCommand(DownloadFileAsync, CanDownloadFile);
        OpenLocalFileCommand = new RelayCommand(OpenLocalFile, CanOpenLocalFile);
    }

    public ObservableCollection<EssaySubmissionReviewRow> Queue { get; } = [];
    public ObservableCollection<SubmissionFilePresentationModel> Files { get; } = [];
    public ObservableCollection<RubricScoreDto> Rubric { get; } = [];
    public ObservableCollection<QuizQuestionReviewRow> QuizQuestions { get; } = [];
    public GradingEditorState Editor { get; } = new();

    public EssaySubmissionReviewRow? SelectedWorkItem
    {
        get => selectedWorkItem;
        set
        {
            if (!Set(ref selectedWorkItem, value)) return;
            CancelAndClearDetail();
            RaiseCommands();
        }
    }

    public EssaySubmissionDetail? Detail
    {
        get => detail;
        private set => Set(ref detail, value);
    }

    public SubmissionFilePresentationModel? SelectedFile
    {
        get => selectedFile;
        set
        {
            if (Set(ref selectedFile, value)) RaiseCommands();
        }
    }

    public GradeDto? Grade { get => grade; private set => Set(ref grade, value); }
    public QuizGradeDetailDto? QuizGrade { get => quizGrade; private set => Set(ref quizGrade, value); }
    public QuizReviewPresentationModel? QuizReview { get => quizReview; private set => Set(ref quizReview, value); }
    public bool IsDetailLoading
    {
        get => isDetailLoading;
        private set
        {
            if (Set(ref isDetailLoading, value)) RaiseCommands();
        }
    }
    public bool IsQuizAttempt => SelectedWorkItem?.Type == GradingWorkItemType.QuizAttempt;
    public bool IsFileSubmission => SelectedWorkItem?.Type == GradingWorkItemType.FileSubmission;
    public int CurrentPage
    {
        get => currentPage;
        private set
        {
            if (Set(ref currentPage, value))
            {
                Raise(nameof(PageLabel));
                Raise(nameof(CanMovePrevious));
                Raise(nameof(CanMoveNext));
            }
        }
    }
    public int TotalCount
    {
        get => totalCount;
        private set
        {
            if (Set(ref totalCount, value))
            {
                Raise(nameof(TotalPages));
                Raise(nameof(PageLabel));
                Raise(nameof(CanMoveNext));
            }
        }
    }
    public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public string PageLabel => $"Trang {CurrentPage}/{TotalPages} · {TotalCount} bài";
    public bool CanMovePrevious => CurrentPage > 1;
    public bool CanMoveNext => CurrentPage < TotalPages;

    public ICommand RefreshCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand OpenWorkItemCommand { get; }
    public ICommand SaveGradeCommand { get; }
    public ICommand ReturnGradeCommand { get; }
    public ICommand ReopenGradeCommand { get; }
    public ICommand DownloadFileCommand { get; }
    public ICommand OpenLocalFileCommand { get; }

    protected override Task LoadAsync(CancellationToken ct) => LoadPageAsync(1, ct);

    private Task ChangePageAsync(int page) => LoadPageAsync(page, DisposeToken);

    private async Task LoadPageAsync(int requestedPage, CancellationToken cancellationToken)
    {
        requestedPage = Math.Max(1, requestedPage);
        listLoadCts?.Cancel();
        listLoadCts?.Dispose();
        listLoadVersion++;
        var version = listLoadVersion;
        listLoadCts = CancellationTokenSource.CreateLinkedTokenSource(DisposeToken, cancellationToken);
        var token = listLoadCts.Token;
        CurrentPage = requestedPage;
        SelectedWorkItem = null;

        try
        {
            IsBusy = true;
            Status = "Đang tải hàng đợi chấm";
            StatusTone = "primary";
            var workItems = ApiGuard.Require(await api.GetAsync<PagedResult<GradingWorkItemDto>>(
                $"api/v1/grading/work-items?page={requestedPage}&pageSize={PageSize}", token));
            if (!IsCurrentListLoad(version)) return;

            Queue.ReplaceWith(workItems.Items.Select(item => new EssaySubmissionReviewRow(item)));
            loadedPage = workItems.Page;
            CurrentPage = workItems.Page;
            TotalCount = workItems.TotalCount;
            Status = workItems.Items.Count == 0
                ? "Không có bài nộp cần chấm."
                : "Hàng đợi chấm bài đã được cập nhật";
            StatusTone = "success";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentListLoad(version))
            {
                CurrentPage = loadedPage;
                ReportFailure(exception);
            }
        }
        finally
        {
            if (IsCurrentListLoad(version))
            {
                IsBusy = false;
                RaiseCommands();
            }
        }
    }

    private bool IsCurrentListLoad(long version) => !IsDisposed && version == listLoadVersion;

    private bool CanOpenWorkItem() => SelectedWorkItem is not null && !IsBusy && !IsDetailLoading;

    private Task OpenSelectedWorkItemAsync()
    {
        var row = SelectedWorkItem;
        if (row is null) return Task.CompletedTask;

        CancelAndClearDetail();
        detailLoadCts = CancellationTokenSource.CreateLinkedTokenSource(DisposeToken);
        IsDetailLoading = true;
        return LoadDetailAsync(row, detailLoadVersion, detailLoadCts.Token);
    }

    private void CancelAndClearDetail()
    {
        detailLoadCts?.Cancel();
        detailLoadCts?.Dispose();
        detailLoadCts = null;
        detailLoadVersion++;

        Detail = null;
        Grade = null;
        QuizGrade = null;
        QuizReview = null;
        Files.Clear();
        Rubric.Clear();
        QuizQuestions.Clear();
        SelectedFile = null;
        Editor.Clear();
        Raise(nameof(IsQuizAttempt));
        Raise(nameof(IsFileSubmission));

        IsDetailLoading = false;
    }

    private async Task LoadDetailAsync(
        EssaySubmissionReviewRow row,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            if (row.Type == GradingWorkItemType.QuizAttempt)
            {
                var loadedQuiz = ApiGuard.Require(await api.GetAsync<QuizGradeDetailDto>(
                    $"api/v1/grading/quiz-attempts/{row.SubmissionId}", cancellationToken));
                if (!IsCurrentSelection(row, version)) return;
                Detail = new(row, loadedQuiz.Status);
                ApplyQuizGrade(loadedQuiz);
            }
            else
            {
                var loadedGrade = ApiGuard.Require(await api.GetAsync<GradeDto>(
                    $"api/v1/grading/submissions/{row.SubmissionId}", cancellationToken));
                if (!IsCurrentSelection(row, version)) return;
                Grade = loadedGrade;
                Rubric.ReplaceWith(loadedGrade.RubricScores);
                Files.ReplaceWith(loadedGrade.SubmissionFiles.Select(file =>
                    new SubmissionFilePresentationModel(file)));
                Detail = new(row, loadedGrade.Status);
                Editor.Load(loadedGrade.Score, loadedGrade.MaxScore, loadedGrade.GeneralComment, loadedGrade.Status);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentSelection(row, version)) ReportFailure(exception);
        }
        finally
        {
            if (IsCurrentSelection(row, version))
            {
                IsDetailLoading = false;
                RaiseCommands();
            }
        }
    }

    private bool IsCurrentSelection(EssaySubmissionReviewRow row, long version) =>
        !IsDisposed && version == detailLoadVersion && ReferenceEquals(row, SelectedWorkItem);

    private bool CanSaveGrade() =>
        !IsBusy && !IsDetailLoading && Detail is not null &&
        Detail.Status != GradingStatus.Returned && Editor.IsValid;

    private Task SaveGradeAsync() =>
        RunAsync("Đang lưu bản chấm", "Điểm và nhận xét đã được lưu, chưa trả cho học sinh", async ct =>
        {
            var row = SelectedWorkItem;
            var currentDetail = Detail;
            if (row is null || currentDetail is null || !Editor.IsValid || Editor.ParsedScore is not { } score)
                return;

            if (row.Type == GradingWorkItemType.QuizAttempt)
            {
                var updated = ApiGuard.Require(await api.PutAsync<SaveQuizGradeRequest, QuizGradeDetailDto>(
                    $"api/v1/grading/quiz-attempts/{row.SubmissionId}",
                    new(score, Editor.Comment, QuizGrade?.RowVersion ?? string.Empty, Guid.NewGuid()),
                    ct));
                if (!ReferenceEquals(row, SelectedWorkItem)) return;
                ApplyQuizGrade(updated);
                return;
            }

            var request = new SaveGradeRequest(
                score,
                Editor.MaxScore,
                Rubric.ToArray(),
                Editor.Comment,
                Grade?.RowVersion ?? "new");
            var saved = ApiGuard.Require(await api.PutAsync<SaveGradeRequest, GradeDto>(
                $"api/v1/grading/submissions/{row.SubmissionId}", request, ct));
            if (!ReferenceEquals(row, SelectedWorkItem)) return;
            Grade = saved;
            Rubric.ReplaceWith(saved.RubricScores);
            ApplyGrade(saved.Status, saved.Score, saved.MaxScore, saved.GeneralComment);
        });

    private bool CanReturnGrade() =>
        !IsBusy && !IsDetailLoading && Detail?.Status == GradingStatus.Graded &&
        Editor.IsValid && !Editor.IsDirty;

    private Task ReturnGradeAsync() =>
        RunAsync("Đang trả kết quả", "Kết quả đã được trả cho học sinh", async ct =>
        {
            var row = SelectedWorkItem;
            if (row is null || Detail?.Status != GradingStatus.Graded || Editor.IsDirty ||
                !dialogs.Confirm("Trả kết quả", "Công bố điểm và nhận xét cho học sinh?"))
                return;

            if (row.Type == GradingWorkItemType.QuizAttempt)
            {
                var returned = ApiGuard.Require(await api.PostAsync<ReturnQuizGradeRequest, QuizGradeDetailDto>(
                    $"api/v1/grading/quiz-attempts/{row.SubmissionId}/return",
                    new("Kết quả đã được công bố.", QuizGrade?.RowVersion ?? string.Empty, Guid.NewGuid()),
                    ct));
                if (!ReferenceEquals(row, SelectedWorkItem)) return;
                ApplyQuizGrade(returned);
                return;
            }

            var returnedGrade = ApiGuard.Require(await api.PostAsync<ReturnGradeRequest, GradeDto>(
                $"api/v1/grading/submissions/{row.SubmissionId}/return",
                new("Kết quả đã được công bố."), ct));
            if (!ReferenceEquals(row, SelectedWorkItem)) return;
            Grade = returnedGrade;
            ApplyGrade(returnedGrade.Status, returnedGrade.Score, returnedGrade.MaxScore, returnedGrade.GeneralComment);
        });

    private bool CanReopenGrade() =>
        !IsBusy && !IsDetailLoading && Detail?.Status == GradingStatus.Returned;

    private Task ReopenGradeAsync() =>
        RunAsync("Đang mở lại kết quả", "Kết quả đã được mở lại để chỉnh sửa", async ct =>
        {
            var row = SelectedWorkItem;
            if (row is null || Detail?.Status != GradingStatus.Returned) return;

            if (row.Type == GradingWorkItemType.QuizAttempt)
            {
                var reopened = ApiGuard.Require(await api.PostAsync<ReopenQuizGradeRequest, QuizGradeDetailDto>(
                    $"api/v1/grading/quiz-attempts/{row.SubmissionId}/reopen",
                    new("Điều chỉnh theo rà soát của giáo viên.", QuizGrade?.RowVersion ?? string.Empty, Guid.NewGuid()),
                    ct));
                if (!ReferenceEquals(row, SelectedWorkItem)) return;
                ApplyQuizGrade(reopened, true);
                return;
            }

            var reopenedGrade = ApiGuard.Require(await api.PostAsync<ReopenGradeRequest, GradeDto>(
                $"api/v1/grading/submissions/{row.SubmissionId}/reopen",
                new("Điều chỉnh theo rà soát của giáo viên."), ct));
            if (!ReferenceEquals(row, SelectedWorkItem)) return;
            Grade = reopenedGrade;
            ApplyGrade(reopenedGrade.Status, reopenedGrade.Score, reopenedGrade.MaxScore, reopenedGrade.GeneralComment, true);
        });

    private void ApplyGrade(
        GradingStatus status,
        decimal? score,
        decimal maximum,
        string? generalComment,
        bool reopened = false)
    {
        Detail?.ApplyStatus(status, reopened);
        Editor.Load(score, maximum, generalComment, status);
        RaiseCommands();
    }

    private void ApplyQuizGrade(QuizGradeDetailDto updated, bool reopened = false)
    {
        QuizGrade = updated;
        QuizReview = new(updated, reopened);
        QuizQuestions.ReplaceWith(QuizReview.Questions);
        ApplyGrade(updated.Status, updated.Score, updated.MaxScore, updated.GeneralComment, reopened);
    }

    private bool CanDownloadFile() =>
        !IsBusy && !IsDetailLoading && Detail is not null && SelectedFile?.CanDownload == true;

    private Task DownloadFileAsync() =>
        RunAsync("Đang tải tệp bài làm", "Tệp bài làm đã được tải xuống", async ct =>
        {
            var currentDetail = Detail;
            var file = SelectedFile;
            if (currentDetail is null || file?.CanDownload != true) return;
            var folder = folders.PickFolder();
            if (string.IsNullOrWhiteSpace(folder)) return;

            var safeName = SubmissionBatchDownloader.MakeSafePathComponent(file.Name, "bai-lam", 120);
            var destination = Path.Combine(folder, safeName);
            await api.DownloadFileAsync(
                $"api/v1/submissions/{currentDetail.SubmissionId}/files/{file.Id}/content",
                destination,
                null,
                ct);
            if (ReferenceEquals(currentDetail, Detail) && ReferenceEquals(file, SelectedFile))
                file.LocalPath = destination;
        });

    private bool CanOpenLocalFile() =>
        SelectedFile?.LocalPath is { Length: > 0 } path && localFiles.Exists(path);

    private void OpenLocalFile()
    {
        var path = SelectedFile?.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !localFiles.Exists(path))
        {
            Status = "Tệp đã tải không tồn tại. Vui lòng tải lại.";
            StatusTone = "warning";
            RaiseCommands();
            return;
        }

        localFiles.Open(path);
        Status = "Đã mở tệp bằng ứng dụng mặc định.";
        StatusTone = "success";
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e) => RaiseCommands();

    protected override void RaiseCommands()
    {
        foreach (var command in new[]
        {
            RefreshCommand, PreviousPageCommand, NextPageCommand,
            SaveGradeCommand, ReturnGradeCommand, ReopenGradeCommand, DownloadFileCommand
        }.OfType<AsyncRelayCommand>())
            command.RaiseCanExecuteChanged();
        (OpenWorkItemCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenLocalFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public override void Dispose()
    {
        Editor.PropertyChanged -= OnEditorPropertyChanged;
        listLoadCts?.Cancel();
        listLoadCts?.Dispose();
        listLoadCts = null;
        detailLoadCts?.Cancel();
        detailLoadCts?.Dispose();
        detailLoadCts = null;
        base.Dispose();
    }
}
