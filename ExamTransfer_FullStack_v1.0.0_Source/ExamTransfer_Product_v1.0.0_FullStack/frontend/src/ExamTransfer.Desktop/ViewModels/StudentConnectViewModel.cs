using System.Collections.ObjectModel;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class StudentConnectViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private readonly StudentSessionState state;
    private readonly AppAuthSessionState authState;
    private string ip = "127.0.0.1";
    private string port = "5048";
    private string roomCode = string.Empty;
    private string displayName;
    private string studentCode;
    private string className = string.Empty;
    private bool isScanning;
    private ServerCard? selectedServer;

    public StudentConnectViewModel(IBackendClient api, StudentSessionState state, AppAuthSessionState authState)
    {
        this.api = api;
        this.state = state;
        this.authState = authState;
        displayName = authState.CurrentAccount?.DisplayName ?? string.Empty;
        studentCode = authState.CurrentAccount?.StudentCode ?? string.Empty;
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning && !IsBusy);
        JoinCommand = new AsyncRelayCommand(JoinAsync, CanJoin);
    }

    public ObservableCollection<ServerCard> Servers { get; } = new();
    public IReadOnlyList<ReadinessItem> Readiness { get; } = new ReadinessItem[]
    {
        new("Kết nối mạng", "LAN/Wi-Fi đang hoạt động", true),
        new("Dung lượng trống", "Đủ dung lượng cho đề và bài làm", true),
        new("Quyền ghi", "Thư mục ExamTransfer có thể sử dụng", true),
        new("Định danh thiết bị", Environment.MachineName, true)
    };

    public string Ip { get => ip; set { if (Set(ref ip, value)) RaiseCommands(); } }
    public string Port { get => port; set { if (Set(ref port, value)) RaiseCommands(); } }
    public string RoomCode { get => roomCode; set { if (Set(ref roomCode, value)) RaiseCommands(); } }
    public string DisplayName { get => displayName; set { if (Set(ref displayName, value)) RaiseCommands(); } }
    public string StudentCode { get => studentCode; set { if (Set(ref studentCode, value)) RaiseCommands(); } }
    public string ClassName { get => className; set => Set(ref className, value); }
    public bool IsScanning { get => isScanning; private set { if (Set(ref isScanning, value)) RaiseCommands(); } }
    public ServerCard? SelectedServer
    {
        get => selectedServer;
        set
        {
            if (Set(ref selectedServer, value) && value is not null)
            {
                Ip = value.Ip;
                Port = value.Port.ToString();
                RoomCode = value.RoomCode;
                Status = $"Đã chọn {value.Name}";
                StatusTone = "primary";
            }
        }
    }

    public ICommand ScanCommand { get; }
    public ICommand JoinCommand { get; }

    protected override Task LoadAsync(CancellationToken ct) => ScanAsync();

    private async Task ScanAsync()
    {
        if (IsScanning) return;
        try
        {
            IsScanning = true;
            Status = "Đang kiểm tra máy chủ trong mạng LAN";
            StatusTone = "primary";
            Servers.Clear();
            var health = await api.GetSystemStatusAsync(DisposeToken);
            if (health?.Success == true)
            {
                Servers.Add(new("Máy chủ ExamTransfer", "Giáo viên", Ip, int.TryParse(Port, out var p) ? p : 5048, 1, RoomCode, "Sẵn sàng", "success", 0, 0));
                SelectedServer = Servers[0];
                Status = "Đã tìm thấy máy chủ ExamTransfer";
                StatusTone = "success";
            }
            else
            {
                Status = "Chưa tìm thấy máy chủ tự động; có thể nhập IP và cổng thủ công";
                StatusTone = "warning";
            }
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "StudentConnect.Scan");
            Status = "Không tìm thấy máy chủ tự động; hãy nhập IP, cổng và mã phòng";
            StatusTone = "warning";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool CanJoin() => !IsBusy && authState.IsStudent && !string.IsNullOrWhiteSpace(DisplayName) && !string.IsNullOrWhiteSpace(StudentCode) && !string.IsNullOrWhiteSpace(RoomCode) && int.TryParse(Port, out var parsedPort) && parsedPort is > 0 and <= 65535;

    private Task JoinAsync() => RunAsync("Đang gửi yêu cầu tham gia", "Yêu cầu tham gia đã được gửi; hãy mở mục Phòng chờ", async ct =>
    {
        var request = new JoinSessionRequest(RoomCode.Trim().ToUpperInvariant(), StudentCode.Trim(), DisplayName.Trim(), string.IsNullOrWhiteSpace(ClassName) ? null : ClassName.Trim(), Environment.MachineName + "-" + Environment.UserName, Environment.MachineName, "1.0.0", Guid.NewGuid().ToString("N"));
        var response = ApiGuard.Require(await api.PostAsync<JoinSessionRequest, JoinSessionResponse>("api/v1/sessions/join", request, ct));
        state.ApplyJoin(response, request.RoomCode, request.StudentCode, request.DisplayName);
        api.SetParticipantToken(response.AccessToken);
    });

    protected override void RaiseCommands()
    {
        (ScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (JoinCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed record ServerCard(string Name, string Teacher, string Ip, int Port, int LatencyMs, string RoomCode, string Status, string Tone, int Connected, int Capacity)
{
    public string Address => $"{Ip}:{Port}";
    public string CapacityText => Capacity <= 0 ? "Sẵn sàng kết nối" : $"{Connected}/{Capacity} thiết bị";
}
public sealed record ReadinessItem(string Title, string Description, bool Ready);
