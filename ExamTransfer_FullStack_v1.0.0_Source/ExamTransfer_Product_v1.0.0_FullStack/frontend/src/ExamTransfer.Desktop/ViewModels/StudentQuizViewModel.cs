using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class StudentQuizViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState session;
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private readonly Dictionary<Guid, QuizAnswerDto> localAnswers = [];
    private QuizAttemptDto? attempt;
    private bool applying;

    public StudentQuizViewModel(IBackendClient api, StudentSessionState session)
    {
        this.api = api; this.session = session;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy && session.HasSession);
        SyncCommand = new AsyncRelayCommand(() => SyncAsync(DisposeToken, true), () => !IsBusy && Attempt is not null && Attempt.Status == QuizAttemptStatus.InProgress);
        FinalizeCommand = new AsyncRelayCommand(FinalizeAsync, () => !IsBusy && Attempt is not null && Attempt.Status == QuizAttemptStatus.InProgress);
    }

    public ObservableCollection<QuizQuestionState> Questions { get; } = new();
    public QuizAttemptDto? Attempt { get => attempt; private set { if (Set(ref attempt, value)) { Raise(nameof(Result)); RaiseCommands(); } } }
    public string Result => Attempt?.Status == QuizAttemptStatus.Finalized ? $"Đã chốt · {Attempt.Score:0.##}/{Attempt.MaxScore:0.##} điểm" : "Đáp án được lưu cục bộ và tự đồng bộ khi có mạng";
    public ICommand RefreshCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand FinalizeCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (!session.HasSession) { Status = "Hãy tham gia phòng trước."; StatusTone = "warning"; return; }
        await RunAsync("Đang mở bài trắc nghiệm", "Bài trắc nghiệm đã sẵn sàng", async token =>
        {
            if (session.AccessMode == SessionAccessMode.PublicCloud)
                Attempt = await AppServices.PublicCloud.StartQuizAttemptAsync(session.SessionId!.Value, token);
            else
            {
                api.SetParticipantToken(session.AccessToken);
                Attempt = ApiGuard.Require(await api.PostAsync<object, QuizAttemptDto>($"api/v1/student/quiz/sessions/{session.SessionId}/attempt", new { }, token));
            }
            localAnswers.Clear();
            foreach (var answer in Attempt.Answers) localAnswers[answer.QuestionId] = answer;
            foreach (var answer in await QuizLocalStore.LoadAsync(Attempt.Id, token))
                if (!localAnswers.TryGetValue(answer.QuestionId, out var current) || answer.Revision > current.Revision) localAnswers[answer.QuestionId] = answer;
            ApplyQuestions();
            if (Attempt.Status == QuizAttemptStatus.InProgress) await SyncAsync(token, false);
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
        if (applying || Attempt?.Status != QuizAttemptStatus.InProgress) return;
        applying = true;
        if (!question.Multiple)
        {
            var selected = question.Choices.LastOrDefault(x => x.IsSelected);
            foreach (var choice in question.Choices.Where(x => x != selected)) choice.IsSelected = false;
        }
        applying = false;
        var revision = localAnswers.TryGetValue(question.Id, out var previous) ? previous.Revision + 1 : 1;
        localAnswers[question.Id] = new(question.Id, question.Choices.Where(x => x.IsSelected).Select(x => x.Id).ToList(), revision, DateTimeOffset.UtcNow);
        QuizLocalStore.SaveAsync(Attempt.Id, localAnswers.Values, DisposeToken).SafeFireAndForget("Quiz.LocalSave");
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
                ? await AppServices.PublicCloud.SaveQuizAnswersAsync(Attempt.Id, payload, ct)
                : ApiGuard.Require(await api.PutAsync<SyncQuizAnswersRequest, SyncQuizAnswersResultDto>(
                    $"api/v1/student/quiz/attempts/{Attempt.Id}/answers", new(payload), ct));
            foreach (var answer in response.Answers) localAnswers[answer.QuestionId] = answer;
            await QuizLocalStore.SaveAsync(Attempt.Id, localAnswers.Values, ct);
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
        if (Attempt is null || !AppServices.Dialogs.Confirm("Chốt bài trắc nghiệm", "Sau khi chốt sẽ không thể sửa đáp án. Tiếp tục?")) return;
        await SyncAsync(ct, true);
        var idempotencyKey = Guid.NewGuid().ToString("N");
        Attempt = session.AccessMode == SessionAccessMode.PublicCloud
            ? await AppServices.PublicCloud.FinalizeQuizAttemptAsync(Attempt.Id, idempotencyKey, ct)
            : ApiGuard.Require(await api.PostAsync<FinalizeQuizAttemptRequest, QuizAttemptDto>(
                $"api/v1/student/quiz/attempts/{Attempt.Id}/finalize", new(idempotencyKey, DateTimeOffset.UtcNow), ct));
        Raise(nameof(Result));
    });

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, SyncCommand, FinalizeCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
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
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExamTransfer", "quiz-outbox");
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
