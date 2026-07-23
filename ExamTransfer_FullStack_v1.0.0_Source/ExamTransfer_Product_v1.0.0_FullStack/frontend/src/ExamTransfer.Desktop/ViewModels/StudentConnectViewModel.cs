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
    private readonly ILanDiscoveryService discovery;
    private string roomCode = string.Empty;
    private readonly string displayName;
    private readonly string studentCode;
    private string className = string.Empty;
    private string classCode = string.Empty;
    private bool isScanning;
    private OpenRoomCard? selectedRoom;

    public StudentConnectViewModel(IBackendClient api, StudentSessionState state, AppAuthSessionState authState, ILanDiscoveryService? discovery = null)
    {
        this.api = api;
        this.state = state;
        this.authState = authState;
        this.discovery = discovery ?? AppServices.LanDiscovery;
        displayName = authState.CurrentAccount?.DisplayName ?? string.Empty;
        studentCode = authState.CurrentAccount?.StudentCode ?? string.Empty;
        ScanCommand = new AsyncRelayCommand(() => ScanAsync(DisposeToken), () => !IsScanning && !IsBusy);
        JoinCommand = new AsyncRelayCommand(JoinAsync, CanJoin);
    }

    public ObservableCollection<OpenRoomCard> Rooms { get; } = new();
    public IReadOnlyList<ReadinessItem> Readiness { get; } =
    [
        new("Kết nối mạng", "LAN/Wi-Fi đang hoạt động", true),
        new("Dung lượng trống", "Đủ dung lượng cho đề và bài làm", true),
        new("Quyền ghi", "Thư mục ExamTransfer có thể sử dụng", true),
        new("Định danh thiết bị", Environment.MachineName, true)
    ];

    public string RoomCode { get => roomCode; set { if (Set(ref roomCode, value)) RaiseCommands(); } }
    public string DisplayName => displayName;
    public string StudentCode => studentCode;
    public string ClassName { get => className; private set => Set(ref className, value); }
    public string ClassCode { get => classCode; set { if (Set(ref classCode, value)) RaiseCommands(); } }
    public bool IsScanning { get => isScanning; private set { if (Set(ref isScanning, value)) RaiseCommands(); } }
    public OpenRoomCard? SelectedRoom
    {
        get => selectedRoom;
        set
        {
            if (!Set(ref selectedRoom, value) || value is null) return;
            RoomCode = value.RoomCode;
            ClassName = value.ClassDisplay;
            ClassCode = value.Room.ClassCode ?? string.Empty;
            Status = $"Đã chọn phòng {value.RoomCode}";
            StatusTone = "primary";
        }
    }

    public ICommand ScanCommand { get; }
    public ICommand JoinCommand { get; }

    protected override Task LoadAsync(CancellationToken ct) => ScanAsync(ct);

    private async Task ScanAsync(CancellationToken ct)
    {
        if (IsScanning) return;
        try
        {
            IsScanning = true;
            Status = "Đang tìm phòng";
            StatusTone = "primary";
            Rooms.Clear();
            foreach (var room in await discovery.DiscoverOpenSessionsAsync(TimeSpan.FromSeconds(2), ct))
                Rooms.Add(new(room));

            SelectedRoom = Rooms.FirstOrDefault();
            if (Rooms.Count == 0)
            {
                Status = "Không tìm thấy phòng đang mở";
                StatusTone = "warning";
            }
            else
            {
                Status = $"Đã tìm thấy {Rooms.Count} phòng đang mở";
                StatusTone = "success";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "StudentConnect.Scan");
            Status = "Không tìm thấy máy giáo viên trong mạng hiện tại. Hãy kiểm tra hai máy đang sử dụng cùng mạng nội bộ và thử quét lại.";
            StatusTone = "warning";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool CanJoin() => !IsBusy
        && authState.IsStudent
        && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(StudentCode)
        && !string.IsNullOrWhiteSpace(RoomCode)
        && (SelectedRoom?.Room.ClassId is null || !string.IsNullOrWhiteSpace(ClassCode));

    private Task JoinAsync() => RunAsync("Đang gửi yêu cầu tham gia", "Yêu cầu tham gia đã được gửi; hãy mở mục Phòng chờ", async ct =>
    {
        var requestedCode = RoomCode.Trim().ToUpperInvariant();
        var room = SelectedRoom?.RoomCode.Equals(requestedCode, StringComparison.OrdinalIgnoreCase) == true
            ? SelectedRoom
            : Rooms.FirstOrDefault(x => x.RoomCode.Equals(requestedCode, StringComparison.OrdinalIgnoreCase));
        if (room is null)
        {
            if (!AppServices.PublicCloud.Configured || !AppServices.PublicCloud.Authenticated)
                throw new InvalidOperationException("Không tìm thấy phòng LAN và PublicCloud chưa có phiên Supabase hợp lệ.");
            if (!string.IsNullOrWhiteSpace(ClassCode))
            {
                var enrollment = await AppServices.PublicCloud.RequestEnrollmentAsync(ClassCode.Trim(), StudentCode.Trim(), ct);
                if (!enrollment.Status.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                {
                    Status = enrollment.Status.Equals("Rejected", StringComparison.OrdinalIgnoreCase)
                        ? "Yêu cầu ghi danh PublicCloud đã bị từ chối."
                        : "Đã gửi yêu cầu ghi danh PublicCloud; đang chờ giáo viên duyệt.";
                    StatusTone = enrollment.Status.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ? "danger" : "warning";
                    return;
                }
            }
            var cloudJoin = await AppServices.PublicCloud.JoinByRoomCodeAsync(
                requestedCode,
                Environment.MachineName + "-" + Environment.UserName,
                Environment.MachineName,
                "1.0.0",
                ct);
            state.Reset();
            state.SessionId = cloudJoin.SessionId;
            state.ParticipantId = cloudJoin.ParticipantId;
            state.ExamId = cloudJoin.ExamId;
            state.AccessToken = cloudJoin.AccessToken;
            state.RoomCode = requestedCode;
            state.StudentCode = StudentCode.Trim();
            state.DisplayName = DisplayName.Trim();
            state.AccessMode = SessionAccessMode.PublicCloud;
            api.SetParticipantToken(null);
            await AppServices.PublicRealtime.StartAsync(
                cloudJoin.SessionId,
                Environment.MachineName + "-" + Environment.UserName,
                cloudJoin.AccessToken,
                async token => _ = await AppServices.PublicCloud.GetParticipantStatusAsync(cloudJoin.ParticipantId, token),
                ct);
            Status = cloudJoin.Status == ParticipantStatus.Approved
                ? "Đã tham gia phòng PublicCloud."
                : "Đã gửi yêu cầu PublicCloud; đang chờ giáo viên duyệt.";
            StatusTone = cloudJoin.Status == ParticipantStatus.Approved ? "success" : "warning";
            return;
        }
        if (!string.IsNullOrWhiteSpace(room.Room.ClassCode)
            && !room.Room.ClassCode.Equals(ClassCode.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mã lớp không khớp với phòng đã chọn. Hãy kiểm tra lại thông tin lớp.");

        var endpoint = new Uri(room.BaseAddress);
        if (!api.TrySetBaseAddress(endpoint.GetLeftPart(UriPartial.Authority), endpoint.Port, out var endpointError))
            throw new InvalidOperationException(endpointError ?? "Không thể kết nối phòng đã chọn.");
        state.Reset();
        api.SetParticipantToken(null);
        if (!api.HasTrustedAccountToken)
            throw new InvalidOperationException("Phiên đăng nhập không thuộc máy chủ của phòng đã chọn. Hãy đăng nhập lại rồi thử tham gia.");

        var request = new JoinSessionRequest(requestedCode, StudentCode.Trim(), DisplayName.Trim(), room.ClassName, Environment.MachineName + "-" + Environment.UserName, Environment.MachineName, "1.0.0", Guid.NewGuid().ToString("N"));
        var response = ApiGuard.Require(await api.PostAsync<JoinSessionRequest, JoinSessionResponse>("api/v1/sessions/join", request, ct));
        state.ApplyJoin(response, request.RoomCode, request.StudentCode, request.DisplayName, SessionAccessMode.LanOnly, room.Room.ServerId);
        api.SetParticipantToken(response.AccessToken);
        await AppServices.StudentRealtime.StartAsync(ct);
    });

    protected override void RaiseCommands()
    {
        (ScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (JoinCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed record OpenRoomCard(OpenSessionDiscoveryDto Room)
{
    public Guid SessionId => Room.SessionId;
    public string RoomCode => Room.RoomCode;
    public string RoomName => Room.RoomName;
    public string? ClassName => Room.ClassName;
    public string ClassDisplay => string.IsNullOrWhiteSpace(Room.ClassCode) ? Room.ClassName ?? "Chưa gắn lớp" : $"{Room.ClassName} ({Room.ClassCode})";
    public string ExamTitle => Room.ExamTitle;
    public string TeacherName => Room.TeacherName;
    public string BaseAddress => Room.BaseAddress;
    public string ApprovalText => Room.RequireApproval ? "Cần giáo viên duyệt" : "Tự động duyệt";
    public string CapacityText => Room.Capacity.HasValue ? $"{Room.CurrentParticipantCount}/{Room.Capacity}" : $"{Room.CurrentParticipantCount} học sinh";
    public string StartText => Room.ScheduledStartUtc?.ToLocalTime().ToString("dd/MM HH:mm") ?? "Chưa đặt giờ";
}

public sealed record ReadinessItem(string Title, string Description, bool Ready);
