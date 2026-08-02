using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class StudentResultsViewModel : ProductPageBase
{
    private readonly IStudentResultsService resultsService;
    private readonly AppAuthSessionState authState;
    private readonly StudentSessionState session;
    private readonly IStudentRealtimeService realtime;
    private readonly IFolderDialogService folders;
    private CancellationTokenSource? loadCts;
    private long loadVersion;
    private Guid? observedAccountId;
    private StudentResultPresentationModel? selectedResult;
    private StudentResultAttachment? selectedAttachment;
    private bool isLoading;
    private string errorMessage = string.Empty;

    public StudentResultsViewModel(
        IStudentResultsService resultsService,
        AppAuthSessionState authState,
        StudentSessionState session,
        IStudentRealtimeService realtime,
        IFolderDialogService folders)
    {
        this.resultsService = resultsService;
        this.authState = authState;
        this.session = session;
        this.realtime = realtime;
        this.folders = folders;
        observedAccountId = authState.CurrentAccount?.UserId;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(DisposeToken), CanRefresh);
        RetryCommand = new AsyncRelayCommand(() => RefreshAsync(DisposeToken), CanRefresh);
        DownloadAttachmentCommand = new AsyncRelayCommand(DownloadAttachmentAsync, CanDownloadAttachment);
        authState.PropertyChanged += OnAuthStateChanged;
        realtime.NotificationReceived += OnRealtimeNotification;
        realtime.EventReceived += OnRealtimeEvent;
    }

    public ObservableCollection<StudentResultPresentationModel> Results { get; } = [];
    public StudentResultPresentationModel? SelectedResult
    {
        get => selectedResult;
        set
        {
            if (!Set(ref selectedResult, value)) return;
            SelectedAttachment = null;
            RaiseCommands();
        }
    }
    public StudentResultAttachment? SelectedAttachment
    {
        get => selectedAttachment;
        set
        {
            if (Set(ref selectedAttachment, value)) RaiseCommands();
        }
    }
    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (!Set(ref isLoading, value)) return;
            Raise(nameof(HasNoResults));
            Raise(nameof(HasResults));
        }
    }
    public string ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (!Set(ref errorMessage, value)) return;
            Raise(nameof(HasError));
            Raise(nameof(HasNoResults));
            Raise(nameof(HasResults));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasNoResults => !IsLoading && !HasError && Results.Count == 0;
    public bool HasResults => !IsLoading && !HasError && Results.Count > 0;
    public string EmptyStateText => "Chưa có kết quả nào được giáo viên trả.";
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand DownloadAttachmentCommand { get; }

    protected override Task LoadAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    private bool CanRefresh() => !IsDisposed && authState.IsStudent;

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var accountId = authState.CurrentAccount?.UserId;
        if (!authState.IsStudent || !accountId.HasValue)
        {
            ClearResults();
            return;
        }

        loadCts?.Cancel();
        loadCts?.Dispose();
        loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, DisposeToken);
        var version = ++loadVersion;
        var token = loadCts.Token;
        IsBusy = true;
        IsLoading = true;
        ErrorMessage = string.Empty;
        Status = "Đang tải kết quả đã trả";
        StatusTone = "primary";
        RaiseCommands();

        try
        {
            var loaded = await resultsService.GetReturnedResultsAsync(token);
            if (!IsCurrentLoad(accountId.Value, version)) return;
            var returned = loaded
                .Where(result => result.Status == GradingStatus.Returned)
                .OrderByDescending(result => result.ReturnedAtUtc)
                .Select(result => new StudentResultPresentationModel(result))
                .ToArray();
            Results.ReplaceWith(returned);
            Raise(nameof(HasResults));
            Raise(nameof(HasNoResults));
            SelectedResult = Results.FirstOrDefault();
            ErrorMessage = string.Empty;
            Status = returned.Length == 0
                ? EmptyStateText
                : $"Đã tải {returned.Length} kết quả được trả";
            StatusTone = returned.Length == 0 ? "info" : "success";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsCurrentLoad(accountId.Value, version)) return;
            Results.Clear();
            Raise(nameof(HasResults));
            SelectedResult = null;
            ErrorMessage = exception is StudentResultsIntegrationException
                ? exception.Message
                : "Không thể tải kết quả. Vui lòng thử lại.";
            Status = ErrorMessage;
            StatusTone = "danger";
            FrontendLogger.Log(exception, nameof(StudentResultsViewModel));
        }
        finally
        {
            if (IsCurrentLoad(accountId.Value, version))
            {
                IsLoading = false;
                IsBusy = false;
                Raise(nameof(HasNoResults));
                RaiseCommands();
            }
        }
    }

    private bool IsCurrentLoad(Guid accountId, long version) =>
        !IsDisposed && version == loadVersion && authState.IsStudent &&
        authState.CurrentAccount?.UserId == accountId;

    private bool CanDownloadAttachment() =>
        !IsBusy && SelectedResult?.Status == GradingStatus.Returned &&
        SelectedAttachment is not null &&
        SelectedAttachment.ResultId == SelectedResult.ResultId &&
        !string.IsNullOrWhiteSpace(SelectedAttachment.DownloadPath);

    private Task DownloadAttachmentAsync() =>
        RunAsync("Đang tải attachment", "Attachment đã được tải xuống", async cancellationToken =>
        {
            var attachment = SelectedAttachment;
            if (attachment is null || SelectedResult?.Status != GradingStatus.Returned ||
                attachment.ResultId != SelectedResult.ResultId ||
                string.IsNullOrWhiteSpace(attachment.DownloadPath))
                return;
            var folder = folders.PickFolder();
            if (string.IsNullOrWhiteSpace(folder)) return;
            var fileName = SubmissionBatchDownloader.MakeSafePathComponent(
                attachment.Name,
                "attachment",
                120);
            await resultsService.DownloadAttachmentAsync(
                attachment,
                Path.Combine(folder, fileName),
                cancellationToken);
        });

    private void OnAuthStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppAuthSessionState.CurrentAccount))
        {
            var currentId = authState.CurrentAccount?.UserId;
            if (currentId != observedAccountId)
            {
                observedAccountId = currentId;
                InvalidateLoadAndClear();
            }
            return;
        }

        if (args.PropertyName == nameof(AppAuthSessionState.AccountAccessToken) && authState.IsStudent)
            RefreshAsync(DisposeToken).SafeFireAndForget("StudentResults.AccountChanged");
    }

    private void OnRealtimeNotification(object? sender, StudentRealtimeNotification notification)
    {
        if (IsDisposed || !session.SessionId.HasValue || !session.ParticipantId.HasValue ||
            notification.SessionId != session.SessionId.Value ||
            notification.ParticipantId != session.ParticipantId.Value ||
            !IsResultEvent(notification.EventName))
            return;
        RefreshAsync(DisposeToken).SafeFireAndForget("StudentResults.RealtimeNotification");
    }

    private void OnRealtimeEvent(object? sender, string eventName)
    {
        if (IsDisposed || session.AccessMode != SessionAccessMode.PublicCloud || !IsResultEvent(eventName))
            return;
        var separator = eventName.IndexOf(':');
        if (separator > 0 && Guid.TryParse(eventName[(separator + 1)..], out var resultId) &&
            session.CurrentAttempt?.Id != resultId && Results.All(result => result.ResultId != resultId))
            return;
        RefreshAsync(DisposeToken).SafeFireAndForget("StudentResults.PublicRealtime");
    }

    private static bool IsResultEvent(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return false;
        var separator = eventName.IndexOf(':');
        var baseName = separator > 0 ? eventName[..separator] : eventName;
        return baseName is RealtimeEvents.GradeReturned or RealtimeEvents.QuizGradeReturned or
            RealtimeEvents.QuizGradeReopened or "GradeReopened" or "ResultReopened";
    }

    private void InvalidateLoadAndClear()
    {
        loadVersion++;
        loadCts?.Cancel();
        loadCts?.Dispose();
        loadCts = null;
        IsLoading = false;
        IsBusy = false;
        ClearResults();
        RaiseCommands();
    }

    private void ClearResults()
    {
        Results.Clear();
        SelectedResult = null;
        SelectedAttachment = null;
        ErrorMessage = string.Empty;
        Status = "Chưa có phiên tài khoản học sinh.";
        StatusTone = "info";
        Raise(nameof(HasNoResults));
        Raise(nameof(HasResults));
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, RetryCommand, DownloadAttachmentCommand }.OfType<AsyncRelayCommand>())
            command.RaiseCanExecuteChanged();
    }

    public override void Dispose()
    {
        authState.PropertyChanged -= OnAuthStateChanged;
        realtime.NotificationReceived -= OnRealtimeNotification;
        realtime.EventReceived -= OnRealtimeEvent;
        loadCts?.Cancel();
        loadCts?.Dispose();
        loadCts = null;
        base.Dispose();
    }
}
