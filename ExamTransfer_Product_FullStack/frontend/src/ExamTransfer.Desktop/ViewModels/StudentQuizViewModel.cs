using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class StudentQuizViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState session;
    private readonly IServerClock serverClock;
    private readonly ServerTimelineCoordinator timelineCoordinator;
    private readonly ICountdownTicker ticker;
    private readonly IStudentRealtimeService realtime;
    private readonly IStudentExamFlowCoordinator flowCoordinator;
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private readonly Dictionary<Guid, QuizAnswerDto> localAnswers = [];
    private QuizAttemptDto? attempt;
    private StudentQuizReviewDto? review;
    private TimeSpan? remaining;
    private bool applying;
    private int expiredSnapshotRefreshRequested;
    private int realtimeSnapshotRefreshRequested;
    private string? lastGradeSignal;

    public StudentQuizViewModel(IBackendClient api, StudentSessionState session)
        : this(
            api,
            session,
            AppServices.ServerClock,
            AppServices.CountdownTickers.Create(TimeSpan.FromSeconds(1)),
            AppServices.StudentRealtime,
            AppServices.StudentExamFlow)
    {
    }

    public StudentQuizViewModel(
        IBackendClient api,
        StudentSessionState session,
        IServerClock serverClock,
        ICountdownTicker ticker,
        IStudentRealtimeService realtime,
        IStudentExamFlowCoordinator? flowCoordinator = null)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock));
        timelineCoordinator = new ServerTimelineCoordinator(this.serverClock);
        this.ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
        this.realtime = realtime ?? throw new ArgumentNullException(nameof(realtime));
        this.flowCoordinator = flowCoordinator ?? AppServices.StudentExamFlow;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy && session.HasSession);
        SyncCommand = new AsyncRelayCommand(() => SyncAsync(DisposeToken, true), () => !IsBusy && CanEditAnswers);
        FinalizeCommand = new AsyncRelayCommand(FinalizeAsync, () => !IsBusy && Attempt is not null && Attempt.Status == QuizAttemptStatus.InProgress);
        ReviewCommand = new AsyncRelayCommand(
            () => ReviewAsync(DisposeToken),
            () => !IsBusy && Attempt?.Status == QuizAttemptStatus.Finalized);
        ExitQuizCommand = new RelayCommand(
            () => this.flowCoordinator.ReturnToCurrentExam(),
            () => Attempt?.Status == QuizAttemptStatus.Finalized);
        ticker.Tick += OnTick;
        ticker.Start();
        realtime.EventReceived += OnRealtimeEvent;
        realtime.NotificationReceived += OnRealtimeNotification;
    }

    public ObservableCollection<QuizQuestionState> Questions { get; } = new();
    public StudentQuizReviewDto? Review
    {
        get => review;
        private set
        {
            if (Set(ref review, value))
            {
                Raise(nameof(ReviewSummary));
                Raise(nameof(ReviewComment));
            }
        }
    }
    public QuizAttemptDto? Attempt
    {
        get => attempt;
        private set
        {
            if (Set(ref attempt, value))
            {
                UpdateCountdown();
                Raise(nameof(Result));
                Raise(nameof(AnsweredCount));
                Raise(nameof(UnansweredCount));
                Raise(nameof(ProgressText));
                RaiseCommands();
            }
        }
    }
    public string Result => Attempt?.Status == QuizAttemptStatus.Finalized
        ? Attempt.ScoreVisible && Attempt.Score.HasValue
            ? $"Đã nộp · {Attempt.Score:0.##}/{Attempt.MaxScore:0.##} điểm"
            : "Đã nộp bài thành công"
        : "Đáp án được lưu cục bộ và tự đồng bộ khi có mạng";
    public int AnsweredCount => Questions.Count(x => x.Choices.Any(choice => choice.IsSelected));
    public string ReviewSummary => Review is null
        ? string.Empty
        : Review.ScoreVisible && Review.Score.HasValue
            ? $"Điểm đã công bố: {Review.Score:0.##}/{Review.MaxScore:0.##}"
            : "Bài làm chỉ đọc · điểm chưa được công bố";
    public string ReviewComment => string.IsNullOrWhiteSpace(Review?.GeneralComment)
        ? string.Empty
        : $"Nhận xét: {Review.GeneralComment}";
    public int UnansweredCount => Math.Max(0, Questions.Count - AnsweredCount);
    public string ProgressText => $"Đã trả lời {AnsweredCount}/{Questions.Count} câu";
    public string TimeLeft => ServerCountdown.Format(remaining);
    public string ClockStatus => serverClock.IsSynchronized ? "Đã đồng bộ giờ máy chủ" : "Chưa đồng bộ giờ máy chủ";
    public bool CanEditAnswers => Attempt?.Status == QuizAttemptStatus.InProgress
        && remaining is { } value
        && value > TimeSpan.Zero;
    public ICommand RefreshCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand FinalizeCommand { get; }
    public ICommand ReviewCommand { get; }
    public ICommand ExitQuizCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (!session.HasSession) { Status = "Hãy tham gia phòng trước."; StatusTone = "warning"; return; }
        await RunAsync("Đang mở bài trắc nghiệm", "Bài trắc nghiệm đã sẵn sàng", async token =>
        {
            var resolution = await flowCoordinator.ResolveAsync(
                StudentExamEntryPoint.QuizTab,
                false,
                token);
            if (resolution.RequiresStartConfirmation)
            {
                if (!AppServices.Dialogs.Confirm(
                        "Bắt đầu bài trắc nghiệm",
                        "Sau khi xác nhận, máy chủ sẽ tạo đúng một lượt làm bài và bắt đầu tính thời gian. Bắt đầu ngay?"))
                    return;
                resolution = await flowCoordinator.ResolveAsync(
                    StudentExamEntryPoint.QuizTab,
                    true,
                    token);
            }
            if (resolution.RouteKey != "S-06")
            {
                Status = resolution.Message;
                StatusTone = "warning";
                return;
            }
            var loadedAttempt = session.CurrentAttempt
                ?? throw new InvalidDataException("Coordinator chưa cung cấp snapshot lượt làm bài an toàn.");
            Attempt = loadedAttempt;
            Review = null;
            localAnswers.Clear();
            foreach (var answer in Attempt.Answers) localAnswers[answer.QuestionId] = answer;
            foreach (var answer in await QuizLocalStore.LoadAsync(Attempt.Id, token))
                if (!localAnswers.TryGetValue(answer.QuestionId, out var current) || answer.Revision > current.Revision) localAnswers[answer.QuestionId] = answer;
            ApplyQuestions();
            if (Attempt.Status == QuizAttemptStatus.InProgress) await SyncAsync(token, false);
            else await LoadReviewCoreAsync(token);
            UpdateCountdown();
            Interlocked.Exchange(
                ref expiredSnapshotRefreshRequested,
                remaining is { } value && value <= TimeSpan.Zero ? 1 : 0);
        });
    }

    private void ApplyQuestions()
    {
        if (Attempt is null) return;
        applying = true;
        Questions.Clear();
        foreach (var question in Attempt.Questions)
        {
            localAnswers.TryGetValue(question.Id, out var answer);
            var selected = answer?.ChoiceIds.ToHashSet() ?? [];
            var row = new QuizQuestionState(question.Id, question.Text, question.Order, question.Points, question.Multiple);
            foreach (var choice in question.Choices) row.Choices.Add(new QuizChoiceState(choice.Id, choice.Text, selected.Contains(choice.Id), () => ChoiceChanged(row)));
            Questions.Add(row);
        }
        applying = false;
    }

    private void ChoiceChanged(QuizQuestionState question)
    {
        var activeAttempt = Attempt;
        if (applying || activeAttempt is null || !CanEditAnswers) return;
        applying = true;
        if (!question.Multiple)
        {
            var selected = question.Choices.LastOrDefault(x => x.IsSelected);
            foreach (var choice in question.Choices.Where(x => x != selected)) choice.IsSelected = false;
        }
        applying = false;
        var revision = localAnswers.TryGetValue(question.Id, out var previous) ? previous.Revision + 1 : 1;
        localAnswers[question.Id] = new(
            question.Id,
            question.Choices.Where(x => x.IsSelected).Select(x => x.Id).ToList(),
            revision,
            RequiredServerNowUtc());
        Raise(nameof(AnsweredCount));
        Raise(nameof(UnansweredCount));
        Raise(nameof(ProgressText));
        QuizLocalStore.SaveAsync(activeAttempt.Id, localAnswers.Values, DisposeToken).SafeFireAndForget("Quiz.LocalSave");
        SyncAsync(DisposeToken, false).SafeFireAndForget("Quiz.AutoSync");
    }

    private async Task SyncAsync(CancellationToken ct, bool showStatus)
    {
        if (Attempt is null || Attempt.Status != QuizAttemptStatus.InProgress) return;
        await syncGate.WaitAsync(ct);
        try
        {
            var payload = localAnswers.Values.OrderBy(x => x.QuestionId).ToList();
            var response = session.AccessMode == SessionAccessMode.PublicCloud
                ? await AppServices.PublicCloud.SaveQuizAnswersAsync(
                    session.SessionId!.Value,
                    Attempt.Id,
                    payload,
                    ct)
                : ApiGuard.Require(await api.PutAsync<SyncQuizAnswersRequest, SyncQuizAnswersResultDto>(
                    $"api/v1/student/quiz/attempts/{Attempt.Id}/answers", new(payload), ct));
            serverClock.Synchronize(response.ServerNowUtc);
            foreach (var answer in response.Answers) localAnswers[answer.QuestionId] = answer;
            await QuizLocalStore.SaveAsync(Attempt.Id, localAnswers.Values, ct);
            UpdateCountdown();
            if (showStatus) { Status = "Đã đồng bộ đáp án với máy chủ"; StatusTone = "success"; }
        }
        catch when (!showStatus)
        {
            Status = "Đang ngoại tuyến · đáp án vẫn được lưu trên máy này"; StatusTone = "warning";
        }
        finally { syncGate.Release(); }
    }

    private Task FinalizeAsync() => RunAsync("Đang chốt bài", "Bài trắc nghiệm đã được chấm trên máy chủ", async ct =>
    {
        if (Attempt is null) return;
        var unanswered = UnansweredCount > 0
            ? $"Còn {UnansweredCount} câu chưa trả lời. "
            : string.Empty;
        if (!AppServices.Dialogs.Confirm(
                "Chốt bài trắc nghiệm",
                $"{unanswered}Sau khi chốt sẽ không thể sửa đáp án. Tiếp tục?"))
            return;
        await SyncAsync(ct, true);
        var idempotencyKey = Guid.NewGuid().ToString("N");
        Attempt = session.AccessMode == SessionAccessMode.PublicCloud
            ? await AppServices.PublicCloud.FinalizeQuizAttemptAsync(Attempt.Id, idempotencyKey, ct)
            : ApiGuard.Require(await api.PostAsync<FinalizeQuizAttemptRequest, QuizAttemptDto>(
                $"api/v1/student/quiz/attempts/{Attempt.Id}/finalize", new(idempotencyKey, RequiredServerNowUtc()), ct));
        session.CurrentAttempt = Attempt;
        await LoadReviewCoreAsync(ct);
        Raise(nameof(Result));
    });

    private Task ReviewAsync(CancellationToken _) =>
        RunAsync(
            "Đang tải bài trắc nghiệm đã làm",
            "Đã tải bài làm ở chế độ chỉ đọc",
            LoadReviewCoreAsync);

    private async Task LoadReviewCoreAsync(CancellationToken ct)
    {
        if (Attempt?.Status != QuizAttemptStatus.Finalized)
            return;
        Review = session.AccessMode == SessionAccessMode.PublicCloud
            ? await AppServices.PublicCloud.GetQuizAttemptReviewAsync(Attempt.Id, ct)
            : ApiGuard.Require(await api.GetAsync<StudentQuizReviewDto>(
                $"api/v1/student/quiz/attempts/{Attempt.Id}/review",
                ct));
        Questions.Clear();
        Raise(nameof(AnsweredCount));
        Raise(nameof(UnansweredCount));
        Raise(nameof(ProgressText));
    }

    internal Task RefreshAuthoritativeReviewAsync(CancellationToken cancellationToken) =>
        Attempt is null || IsDisposed
            ? Task.CompletedTask
            : LoadReviewCoreAsync(cancellationToken);

    private DateTimeOffset RequiredServerNowUtc()
    {
        if (serverClock.TryGetUtcNow(out var serverNowUtc))
        {
            return serverNowUtc;
        }

        throw new InvalidOperationException(
            "Chưa đồng bộ giờ máy chủ; không thể tạo mốc thời gian đáp án hoặc chốt bài.");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        var wasPositive = remaining is { } previous && previous > TimeSpan.Zero;
        UpdateCountdown();
        if (wasPositive
            && remaining is { } current
            && current <= TimeSpan.Zero
            && Attempt?.Status == QuizAttemptStatus.InProgress
            && Interlocked.Exchange(ref expiredSnapshotRefreshRequested, 1) == 0)
        {
            LoadAsync(DisposeToken).SafeFireAndForget("StudentQuiz.ExpiredSnapshotRefresh");
        }
    }

    private void OnRealtimeEvent(object? sender, string eventName)
    {
        if (IsDisposed)
        {
            return;
        }

        if (eventName is RealtimeEvents.TimeExtended or "Reconnected")
            RequestSnapshotResync();
        else if (IsCurrentGradeVisibilityEvent(eventName))
            RequestGradeReviewRefresh(eventName);
    }

    private bool IsCurrentGradeVisibilityEvent(string eventName)
    {
        if (Attempt is null)
            return false;
        if (eventName is RealtimeEvents.QuizGradeReturned
            or RealtimeEvents.QuizGradeReopened)
            return true;
        return string.Equals(
                   eventName,
                   $"{RealtimeEvents.QuizGradeReturned}:{Attempt.Id:N}",
                   StringComparison.Ordinal)
               || string.Equals(
                   eventName,
                   $"{RealtimeEvents.QuizGradeReopened}:{Attempt.Id:N}",
                   StringComparison.Ordinal);
    }

    private void RequestGradeReviewRefresh(string signal)
    {
        if (string.Equals(
                Interlocked.Exchange(ref lastGradeSignal, signal),
                signal,
                StringComparison.Ordinal))
            return;
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await LoadReviewCoreAsync(DisposeToken);
                if (Review?.ScoreVisible == true && Review.Score.HasValue)
                    AppServices.Toasts.Show(
                        $"Giáo viên đã công bố điểm trắc nghiệm: {Review.Score:0.##}/10",
                        "success");
            }
            catch (Exception ex)
            {
                _ = Interlocked.CompareExchange(
                    ref lastGradeSignal,
                    null,
                    signal);
                ReportFailure(ex);
            }
        });
    }

    private void OnRealtimeNotification(object? sender, StudentRealtimeNotification notification)
    {
        if (IsDisposed)
            return;
        var payload = notification.TimeExtended;
        if (notification.SessionId != session.SessionId
            || (payload?.ParticipantId.HasValue == true
                && payload.ParticipantId != session.ParticipantId)
            || (payload?.AttemptId.HasValue == true
                && Attempt is not null
                && payload.AttemptId != Attempt.Id))
            return;
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (payload is null
                || !payload.ServerNowUtc.HasValue
                || !payload.Revision.HasValue
                || !payload.AttemptId.HasValue)
                RequestSnapshotResync();
            else
                _ = TryApplyTimeExtended(notification);
        });
    }

    public bool TryApplyTimeExtended(StudentRealtimeNotification notification)
    {
        var payload = notification.TimeExtended;
        if (IsDisposed
            || notification.EventName != RealtimeEvents.TimeExtended
            || notification.SessionId != session.SessionId
            || payload is null
            || payload.ParticipantId != session.ParticipantId
            || !payload.ServerNowUtc.HasValue
            || !payload.Revision.HasValue)
            return false;
        if (Attempt is null
            || !payload.AttemptId.HasValue
            || payload.AttemptId != Attempt.Id)
            return false;
        if (!timelineCoordinator.TryApply(
                payload.Revision.Value,
                payload.EffectiveDeadlineUtc,
                payload.ServerNowUtc.Value)
            || Attempt is null)
            return false;
        Attempt = Attempt with { DeadlineUtc = payload.EffectiveDeadlineUtc };
        Interlocked.Exchange(ref expiredSnapshotRefreshRequested, 0);
        UpdateCountdown();
        return true;
    }

    private QuizAttemptDto ApplyTimelineSnapshot(
        QuizAttemptDto loadedAttempt,
        PublicStudentTimeline timeline)
    {
        if (timeline.SessionId != session.SessionId
            || timeline.ParticipantId != session.ParticipantId
            || timeline.AttemptId != loadedAttempt.Id
            || !timeline.AttemptDeadlineUtc.HasValue)
            throw new InvalidDataException("PublicCloud quiz timeline không khớp attempt hiện tại.");
        if (timelineCoordinator.TryApply(
                timeline.Revision,
                timeline.AttemptDeadlineUtc.Value,
                timeline.ServerNowUtc))
            return loadedAttempt with { DeadlineUtc = timeline.AttemptDeadlineUtc.Value };
        return timelineCoordinator.DeadlineUtc.HasValue
            ? loadedAttempt with { DeadlineUtc = timelineCoordinator.DeadlineUtc.Value }
            : loadedAttempt;
    }

    private void RequestSnapshotResync()
    {
        if (Interlocked.Exchange(ref realtimeSnapshotRefreshRequested, 1) != 0)
            return;
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try { await LoadAsync(DisposeToken); }
            finally { Interlocked.Exchange(ref realtimeSnapshotRefreshRequested, 0); }
        });
    }

    private void UpdateCountdown()
    {
        remaining = ServerCountdown.Remaining(serverClock, Attempt?.DeadlineUtc);
        Raise(nameof(TimeLeft));
        Raise(nameof(ClockStatus));
        Raise(nameof(CanEditAnswers));
        RaiseCommands();
    }

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, SyncCommand, FinalizeCommand, ReviewCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
        (ExitQuizCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public override void Dispose()
    {
        realtime.EventReceived -= OnRealtimeEvent;
        realtime.NotificationReceived -= OnRealtimeNotification;
        ticker.Tick -= OnTick;
        ticker.Dispose();
        base.Dispose();
    }
}

public sealed class QuizQuestionState(Guid id, string text, int order, decimal points, bool multiple)
{
    public Guid Id { get; } = id; public string Text { get; } = text; public int Order { get; } = order; public decimal Points { get; } = points; public bool Multiple { get; } = multiple;
    public ObservableCollection<QuizChoiceState> Choices { get; } = new();
}

public sealed class QuizChoiceState : ObservableObject
{
    private bool selected; private readonly Action changed;
    public QuizChoiceState(Guid id, string text, bool selected, Action changed) { Id = id; Text = text; this.selected = selected; this.changed = changed; }
    public Guid Id { get; } public string Text { get; }
    public bool IsSelected { get => selected; set { if (Set(ref selected, value)) changed(); } }
}

internal static class QuizLocalStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string Root => Path.Combine(AppProfile.LocalDataRoot, "quiz-outbox");
    private static string PathFor(Guid attemptId) => Path.Combine(Root, attemptId.ToString("N") + ".json");
    public static async Task<IReadOnlyList<QuizAnswerDto>> LoadAsync(Guid attemptId, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var path = PathFor(attemptId); if (!File.Exists(path)) return [];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return await JsonSerializer.DeserializeAsync<List<QuizAnswerDto>>(stream, Json, ct) ?? [];
        }
        finally { Gate.Release(); }
    }
    public static async Task SaveAsync(Guid attemptId, IEnumerable<QuizAnswerDto> answers, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Root); var path = PathFor(attemptId); var temporary = path + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await JsonSerializer.SerializeAsync(stream, answers.ToList(), Json, ct);
            File.Move(temporary, path, true);
        }
        finally { Gate.Release(); }
    }
}
