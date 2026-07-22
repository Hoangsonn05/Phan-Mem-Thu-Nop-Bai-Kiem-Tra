using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class StudentExamViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private readonly IStudentHeartbeatService heartbeat;
    private readonly IStudentRealtimeService realtime;
    private readonly DispatcherTimer timer;
    private FileSystemWatcher? watcher;
    private SessionDetailDto? session;
    private ParticipantDto? participant;
    private TimeSpan remaining;
    private string connection = "Chưa kết nối phiên";
    private string workspaceFolder;

    public StudentExamViewModel(IBackendClient api, StudentSessionState state)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        heartbeat = AppServices.StudentHeartbeat;
        heartbeat.StateChanged += OnHeartbeatStateChanged;
        realtime = AppServices.StudentRealtime;
        realtime.EventReceived += OnRealtimeEvent;
        workspaceFolder = AppServices.Preferences.Get("exam.workspace")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ExamTransfer", "Working");

        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(DisposeToken), () => !IsBusy && state.HasSession);
        HeartbeatCommand = new AsyncRelayCommand(HeartbeatAsync, () => !IsBusy && state.HasSession);
        BrowseWorkspaceCommand = new RelayCommand(BrowseWorkspace);
        OpenWorkspaceCommand = new RelayCommand(OpenWorkspace);
        RefreshWorkspaceCommand = new AsyncRelayCommand(LoadWorkspaceAsync, () => !IsBusy);

        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += OnTick;
        timer.Start();
    }

    public ObservableCollection<ExamStep> Steps { get; } = new()
    {
        new(1, "Xác nhận tham gia", "Chưa thực hiện", false, false),
        new(2, "Nhận đề", "Chưa thực hiện", false, false),
        new(3, "Làm bài trong thư mục", "Chưa thực hiện", false, false),
        new(4, "Nộp bài", "Chưa thực hiện", false, false),
        new(5, "Biên nhận", "Chưa thực hiện", false, false)
    };

    public ObservableCollection<StudentMessage> Messages { get; } = new();
    public ObservableCollection<WorkspaceFileRow> WorkspaceFiles { get; } = new();
    public string Title => Session?.Summary.Title ?? "Chưa có kỳ thi đang hoạt động";
    public string Subject => state.ExamId.HasValue ? $"Mã đề {state.ExamId.Value.ToString("N")[..8].ToUpperInvariant()}" : "";
    public string Teacher => "Máy chủ phòng thi";
    public string RoomCode => state.RoomCode;
    public string CandidateCount => Session is null ? "0" : Session.Participants.Count.ToString();
    public string TimeLeft => $"{(int)Math.Max(0, remaining.TotalHours):00}:{Math.Max(0, remaining.Minutes):00}:{Math.Max(0, remaining.Seconds):00}";
    private DateTimeOffset? EffectiveDeadlineUtc => Participant?.EffectiveDeadlineUtc ?? Session?.Summary.EffectiveDeadlineUtc;
    public double TimeProgress => Session?.Summary.StartTimeUtc is null || EffectiveDeadlineUtc is null
        ? 0
        : Math.Clamp(remaining.TotalSeconds / Math.Max(1, (EffectiveDeadlineUtc.Value - Session.Summary.StartTimeUtc.Value).TotalSeconds) * 100, 0, 100);
    public SessionDetailDto? Session { get => session; private set { if (Set(ref session, value)) { Raise(nameof(Title)); Raise(nameof(Subject)); Raise(nameof(RoomCode)); Raise(nameof(CandidateCount)); } } }
    public ParticipantDto? Participant { get => participant; private set => Set(ref participant, value); }
    public string Connection { get => connection; private set => Set(ref connection, value); }
    public string WorkspaceFolder { get => workspaceFolder; set { if (Set(ref workspaceFolder, value)) AppServices.Preferences.Set("exam.workspace", value); } }
    public ICommand RefreshCommand { get; }
    public ICommand HeartbeatCommand { get; }
    public ICommand BrowseWorkspaceCommand { get; }
    public ICommand OpenWorkspaceCommand { get; }
    public ICommand RefreshWorkspaceCommand { get; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await LoadWorkspaceAsync();
        if (!state.HasSession)
        {
            Connection = "Chưa có phiên thi hợp lệ";
            Status = "Hãy kết nối phòng, xác thực thông tin và được giáo viên duyệt trước.";
            StatusTone = "warning";
            return;
        }

        await RunAsync("Đang đồng bộ kỳ thi", "Thông tin kỳ thi đã được cập nhật", async token =>
        {
            api.SetParticipantToken(state.AccessToken);
            Session = ApiGuard.Require(await api.GetSessionAsync(state.SessionId!.Value, token));
            Participant = ApiGuard.Require(await api.GetAsync<ParticipantDto>(
                $"api/v1/sessions/{state.SessionId}/participants/{state.ParticipantId}", token));
            state.ExamId = Session.Summary.ExamId;
            remaining = EffectiveDeadlineUtc.HasValue
                ? EffectiveDeadlineUtc.Value - DateTimeOffset.UtcNow
                : TimeSpan.Zero;
            Connection = $"Đã xác thực · {Session.Summary.Status}";
            UpdateSteps();
            RaiseTime();
        });
    }

    private Task HeartbeatAsync() => RunAsync("Đang kiểm tra kết nối", "Kết nối phòng thi ổn định", async ct =>
    {
        if (!state.SessionId.HasValue || !state.ParticipantId.HasValue) return;
        if (!await heartbeat.ProbeNowAsync(ct))
            throw new InvalidOperationException("Máy chủ chưa phản hồi; vòng kết nối nền sẽ tự thử lại.");
        Connection = "Đã kết nối máy chủ";
    });

    private void OnHeartbeatStateChanged(object? sender, StudentConnectionState value)
    {
        if (IsDisposed) return;
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Connection = value switch
        {
            StudentConnectionState.Online => "Đã kết nối máy chủ",
            StudentConnectionState.Connecting => "Đang kết nối máy chủ",
            StudentConnectionState.Reconnecting => "Mất kết nối tạm thời · đang thử lại",
            StudentConnectionState.Offline => "Ngoại tuyến · vẫn giữ phiên thi",
            StudentConnectionState.AuthenticationExpired => "Token phiên thi đã hết hạn",
            _ => "Chưa kết nối phiên"
        });
    }

    private void OnRealtimeEvent(object? sender, string eventName)
    {
        if (IsDisposed) return;
        if (eventName is RealtimeEvents.TimeExtended or RealtimeEvents.SessionStateChanged or RealtimeEvents.ParticipantApproved)
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => LoadAsync(DisposeToken).SafeFireAndForget("StudentExam.RealtimeRefresh"));
    }

    private async Task LoadWorkspaceAsync()
    {
        try
        {
            Directory.CreateDirectory(WorkspaceFolder);
            var rows = Directory.EnumerateFiles(WorkspaceFolder, "*", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new WorkspaceFileRow(info.Name, FormatBytes(info.Length), info.LastWriteTime.ToString("HH:mm dd/MM/yyyy"), info.Length <= 200L * 1024 * 1024 ? "Hợp lệ" : "Vượt giới hạn");
                }).ToArray();
            WorkspaceFiles.ReplaceWith(rows);
            StartWorkspaceWatcher();
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "StudentExam.Workspace");
            Status = "Không thể đọc thư mục làm bài. Hãy chọn thư mục khác.";
            StatusTone = "danger";
        }
    }

    private void BrowseWorkspace()
    {
        var selected = AppServices.Folders.PickFolder();
        if (string.IsNullOrWhiteSpace(selected)) return;
        WorkspaceFolder = selected;
        LoadWorkspaceAsync().SafeFireAndForget("StudentExam.BrowseWorkspace");
    }

    private void OpenWorkspace()
    {
        Directory.CreateDirectory(WorkspaceFolder);
        Process.Start(new ProcessStartInfo(WorkspaceFolder) { UseShellExecute = true });
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (IsDisposed || EffectiveDeadlineUtc is null) return;
        remaining = EffectiveDeadlineUtc.Value - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        RaiseTime();
        UpdateSteps();
    }

    private void StartWorkspaceWatcher()
    {
        watcher?.Dispose();
        watcher = new FileSystemWatcher(WorkspaceFolder)
        {
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        watcher.Created += OnWorkspaceChanged;
        watcher.Changed += OnWorkspaceChanged;
        watcher.Deleted += OnWorkspaceChanged;
        watcher.Renamed += OnWorkspaceChanged;
    }

    private void OnWorkspaceChanged(object sender, FileSystemEventArgs e)
    {
        if (IsDisposed) return;
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            LoadWorkspaceAsync().SafeFireAndForget("StudentExam.WorkspaceWatcher"));
    }

    private void RaiseTime()
    {
        Raise(nameof(TimeLeft));
        Raise(nameof(TimeProgress));
    }

    private void UpdateSteps()
    {
        if (Session is null) return;
        var active = Session.Summary.Status.ToString();
        Steps[0] = Steps[0] with { Status = "Đã xác nhận", Completed = true, Active = false };
        Steps[1] = Steps[1] with { Status = state.ExamId.HasValue ? "Sẵn sàng nhận" : "Chờ phát đề", Completed = state.ExamId.HasValue, Active = false };
        Steps[2] = Steps[2] with { Status = WorkspaceFiles.Count > 0 ? $"{WorkspaceFiles.Count} file" : "Chưa có file", Completed = false, Active = active is "InProgress" or "Paused" };
        Steps[3] = Steps[3] with { Status = remaining > TimeSpan.Zero ? "Có thể nộp" : "Đã hết giờ", Completed = state.LastSubmissionId.HasValue, Active = false };
        Steps[4] = Steps[4] with { Status = state.LastReceipt is null ? "Chưa có" : "Đã nhận", Completed = state.LastReceipt is not null, Active = false };
    }

    private static string FormatBytes(long value) => value < 1024 * 1024 ? $"{value / 1024d:N1} KB" : $"{value / 1024d / 1024d:N1} MB";

    protected override void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, HeartbeatCommand, RefreshWorkspaceCommand }.OfType<AsyncRelayCommand>())
            command.RaiseCanExecuteChanged();
    }

    public override void Dispose()
    {
        timer.Stop();
        timer.Tick -= OnTick;
        watcher?.Dispose();
        heartbeat.StateChanged -= OnHeartbeatStateChanged;
        realtime.EventReceived -= OnRealtimeEvent;
        base.Dispose();
    }
}

public sealed record ExamStep(int Number, string Title, string Status, bool Completed, bool Active);
public sealed record StudentMessage(string Title, string Description, string Time);
