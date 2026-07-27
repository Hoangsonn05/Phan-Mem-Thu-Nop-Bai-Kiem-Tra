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
    private string ip = "127.0.0.1";
    private string port = "5048";
    private string roomCode = string.Empty;
    private string displayName;
    private string studentCode;
    private string className = string.Empty;
    private string classCode = string.Empty;
    private bool isScanning;
    private bool isLanMode = true;
    private OpenRoomCard? selectedRoom;
    private ServerCard? selectedServer;

    public StudentConnectViewModel(IBackendClient api, StudentSessionState state, AppAuthSessionState authState, ILanDiscoveryService? discovery = null)
    {
        this.api = api;
        this.state = state;
        this.authState = authState;
        this.discovery = discovery ?? AppServices.LanDiscovery;
        ip = api.BaseAddress.Host;
        port = api.BaseAddress.Port.ToString();
        displayName = authState.CurrentAccount?.DisplayName ?? string.Empty;
        studentCode = authState.CurrentAccount?.StudentCode ?? string.Empty;
        ScanCommand = new AsyncRelayCommand(() => ScanAsync(DisposeToken), () => !IsScanning && !IsBusy);
        JoinCommand = new AsyncRelayCommand(JoinAsync, CanJoin);
    }

    public ObservableCollection<OpenRoomCard> Rooms { get; } = new();
    public ObservableCollection<ServerCard> Servers { get; } = new();
    public IReadOnlyList<ReadinessItem> Readiness { get; } =
    [
        new("Kết nối mạng", "LAN/Wi-Fi đang hoạt động", true),
        new("Dung lượng trống", "Đủ dung lượng cho đề và bài làm", true),
        new("Quyền ghi", "Thư mục ExamTransfer có thể sử dụng", true),
        new("Định danh thiết bị", Environment.MachineName, true)
    ];
    public bool HasRooms => IsLanMode && Rooms.Count > 0;
    public bool HasNoRooms => IsLanMode && Rooms.Count == 0;

    public string Ip { get => ip; set { if (Set(ref ip, value)) RaiseCommands(); } }
    public string Port { get => port; set { if (Set(ref port, value)) RaiseCommands(); } }
    public string RoomCode { get => roomCode; set { if (Set(ref roomCode, value)) RaiseCommands(); } }
    public string DisplayName { get => displayName; set { if (Set(ref displayName, value)) RaiseCommands(); } }
    public string StudentCode { get => studentCode; set { if (Set(ref studentCode, value)) RaiseCommands(); } }
    public string ClassName { get => className; set => Set(ref className, value); }
    public string ClassCode { get => classCode; set { if (Set(ref classCode, value)) RaiseCommands(); } }
    public bool IsScanning { get => isScanning; private set { if (Set(ref isScanning, value)) RaiseCommands(); } }
    public bool IsLanMode
    {
        get => isLanMode;
        set
        {
            if (!Set(ref isLanMode, value)) return;
            Raise(nameof(IsPublicCloudMode));
            Raise(nameof(HasRooms));
            Raise(nameof(HasNoRooms));
            RaiseCommands();
        }
    }
    public bool IsPublicCloudMode
    {
        get => !IsLanMode;
        set
        {
            if (value) IsLanMode = false;
        }
    }
    public ServerCard? SelectedServer
    {
        get => selectedServer;
        set
        {
            if (!Set(ref selectedServer, value) || value is null) return;
            Ip = value.Ip;
            Port = value.Port.ToString();
            Status = $"Đã chọn {value.Name}";
            StatusTone = "primary";
        }
    }

    public OpenRoomCard? SelectedRoom
    {
        get => selectedRoom;
        set
        {
            if (!Set(ref selectedRoom, value) || value is null) return;
            RoomCode = value.RoomCode;
            if (Uri.TryCreate(value.BaseAddress, UriKind.Absolute, out var endpoint))
            {
                Ip = endpoint.Host;
                Port = endpoint.Port.ToString();
            }
            Status = $"Đã chọn kỳ thi {value.ExamTitle}";
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
            Status = "Đang tìm máy chủ và phòng thi trong mạng LAN";
            StatusTone = "primary";
            Servers.Clear();
            Rooms.Clear();
            var serversTask = discovery.DiscoverAsync(TimeSpan.FromSeconds(2), ct);
            var roomsTask = discovery.DiscoverOpenSessionsAsync(TimeSpan.FromSeconds(2), ct);
            await Task.WhenAll(serversTask, roomsTask);

            foreach (var server in await serversTask)
            {
                Servers.Add(new(
                    server.ServerName,
                    "Máy giáo viên",
                    server.Address,
                    server.Port,
                    0,
                    "Sẵn sàng",
                    "success",
                    server.ActiveRoomCount,
                    0,
                    server.Fingerprint,
                    server.Version));
            }
            foreach (var room in await roomsTask)
                Rooms.Add(new(room));
            Raise(nameof(HasRooms));
            Raise(nameof(HasNoRooms));

            SelectedRoom = Rooms.FirstOrDefault();
            SelectedServer = Servers.FirstOrDefault();
            if (Rooms.Count == 0)
            {
                Status = "Chưa tìm thấy kỳ thi LanOnly đang chờ. Có thể quét lại hoặc mở kết nối thủ công.";
                StatusTone = "warning";
            }
            else
            {
                Status = $"Đã tìm thấy {Servers.Count} máy chủ và {Rooms.Count} phòng đang mở";
                StatusTone = "success";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "StudentConnect.Scan");
            Status = "Không tìm thấy máy giáo viên trong mạng hiện tại. Có thể nhập IP/cổng thủ công hoặc dùng mã phòng Public Cloud.";
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
        && (IsPublicCloudMode
            ? AppServices.PublicCloud.Configured && AppServices.PublicCloud.Authenticated
            : SelectedRoom?.Room.SessionState == SessionStatus.Waiting
                || (int.TryParse(Port, out var parsedPort) && parsedPort is > 0 and <= 65535));

    private Task JoinAsync() => RunAsync("Đang gửi yêu cầu tham gia", "Yêu cầu tham gia đã được gửi; đang mở Phòng chờ", async ct =>
    {
        var requestedCode = RoomCode.Trim().ToUpperInvariant();
        if (IsPublicCloudMode)
        {
            if (!AppServices.PublicCloud.Configured || !AppServices.PublicCloud.Authenticated)
                throw new InvalidOperationException("PublicCloud chưa được cấu hình hoặc tài khoản Student chưa đăng nhập.");
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
            state.RoomCode = cloudJoin.RoomCode;
            state.StudentCode = StudentCode.Trim();
            state.DisplayName = DisplayName.Trim();
            state.AccessMode = SessionAccessMode.PublicCloud;
            state.AdmissionMode = SessionAdmissionMode.OpenRequest;
            state.ExamTitle = cloudJoin.ExamTitle;
            state.Subject = cloudJoin.Subject;
            state.DurationMinutes = cloudJoin.DurationMinutes;
            state.DeliveryType = cloudJoin.DeliveryType;
            state.SupervisionMode = cloudJoin.SupervisionMode;
            state.ResultPolicy = cloudJoin.QuizResultPolicy;
            state.ParticipantStatus = cloudJoin.ParticipantStatus;
            state.SessionStatus = cloudJoin.SessionStatus;
            api.SetParticipantToken(null);
            await AppServices.PublicRealtime.StartAsync(
                cloudJoin.SessionId,
                Environment.MachineName + "-" + Environment.UserName,
                cloudJoin.AccessToken,
                async token => _ = await AppServices.PublicCloud.GetStudentTimelineAsync(cloudJoin.SessionId, token),
                ct);
            _ = await AppServices.StudentExamFlow.ResolveAsync(
                StudentExamEntryPoint.CurrentExam,
                false,
                ct);
            return;
        }

        var room = SelectedRoom?.RoomCode.Equals(requestedCode, StringComparison.OrdinalIgnoreCase) == true
            ? SelectedRoom
            : Rooms.FirstOrDefault(x => x.RoomCode.Equals(requestedCode, StringComparison.OrdinalIgnoreCase));
        if (room is null)
        {
            if (!int.TryParse(Port, out var manualPort) || manualPort is <= 0 or > 65535)
                throw new InvalidOperationException("Cổng máy chủ không hợp lệ.");
            if (!api.TrySetBaseAddress(Ip, manualPort, out var manualEndpointError))
                throw new InvalidOperationException(manualEndpointError ?? "Địa chỉ máy chủ không hợp lệ.");
            state.Reset();
            api.SetParticipantToken(null);
            if (!api.HasTrustedAccountToken)
                throw new InvalidOperationException("Máy chủ đã thay đổi. Hãy đăng xuất và đăng nhập lại trên đúng máy chủ giáo viên trước khi tham gia phòng.");
            var manualRequest = new JoinSessionRequest(
                requestedCode,
                StudentCode.Trim(),
                DisplayName.Trim(),
                null,
                Environment.MachineName + "-" + Environment.UserName,
                Environment.MachineName,
                "1.0.0",
                Guid.NewGuid().ToString("N"));
            var manualResponse = ApiGuard.Require(await api.PostAsync<JoinSessionRequest, JoinSessionResponse>("api/v1/sessions/join", manualRequest, ct));
            state.ApplyJoin(manualResponse, manualRequest.RoomCode, manualRequest.StudentCode, manualRequest.DisplayName, SessionAccessMode.LanOnly);
            api.SetParticipantToken(manualResponse.AccessToken);
            var manualSession = ApiGuard.Require(await api.GetSessionAsync(manualResponse.SessionId, ct));
            ApplyLocalSessionSnapshot(manualSession, manualResponse.Status);
            await AppServices.StudentRealtime.StartAsync(ct);
            _ = await AppServices.StudentExamFlow.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, ct);
            return;
        }

        var endpoint = new Uri(room.BaseAddress);
        if (!api.TrySetBaseAddress(endpoint.GetLeftPart(UriPartial.Authority), endpoint.Port, out var endpointError))
            throw new InvalidOperationException(endpointError ?? "Không thể kết nối phòng đã chọn.");
        state.Reset();
        api.SetParticipantToken(null);
        if (!api.HasTrustedAccountToken)
            throw new InvalidOperationException("Phiên đăng nhập không thuộc máy chủ của phòng đã chọn. Hãy đăng nhập lại rồi thử tham gia.");

        var request = new JoinSessionRequest(requestedCode, StudentCode.Trim(), DisplayName.Trim(), null, Environment.MachineName + "-" + Environment.UserName, Environment.MachineName, "1.0.0", Guid.NewGuid().ToString("N"));
        var response = ApiGuard.Require(await api.PostAsync<JoinSessionRequest, JoinSessionResponse>("api/v1/sessions/join", request, ct));
        state.ApplyJoin(response, request.RoomCode, request.StudentCode, request.DisplayName, SessionAccessMode.LanOnly, room.Room.ServerId);
        state.ExamId = room.Room.ExamId;
        state.AdmissionMode = room.Room.AdmissionMode;
        state.ExamTitle = room.Room.ExamTitle;
        state.Subject = room.Room.Subject;
        state.DurationMinutes = room.Room.DurationMinutes;
        state.DeliveryType = room.Room.DeliveryType;
        state.SupervisionMode = room.Room.SupervisionMode;
        state.ParticipantStatus = response.Status;
        state.SessionStatus = room.Room.SessionState;
        api.SetParticipantToken(response.AccessToken);
        await AppServices.StudentRealtime.StartAsync(ct);
        _ = await AppServices.StudentExamFlow.ResolveAsync(StudentExamEntryPoint.CurrentExam, false, ct);
    });

    private void ApplyLocalSessionSnapshot(SessionDetailDto detail, ParticipantStatus participantStatus)
    {
        state.ExamId = detail.Summary.ExamId;
        state.AdmissionMode = detail.Summary.AdmissionMode;
        state.ExamTitle = detail.Summary.Title;
        state.DeliveryType = detail.Summary.DeliveryType;
        state.SupervisionMode = detail.Summary.SupervisionMode;
        state.ResultPolicy = detail.Summary.QuizResultPolicy;
        state.ParticipantStatus = participantStatus;
        state.SessionStatus = detail.Summary.Status;
    }

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
    public string Subject => Room.Subject;
    public string DurationText => Room.DurationMinutes > 0 ? $"{Room.DurationMinutes} phút" : "Chưa rõ thời lượng";
    public string DeliveryText => Room.DeliveryType == ExamDeliveryType.MultipleChoice ? "Trắc nghiệm" : "Nộp file";
    public string StatusText => Room.SessionState == SessionStatus.Waiting ? "Đang chờ" : Room.SessionState.ToString();
}

public sealed record ServerCard(string Name, string Teacher, string Ip, int Port, int LatencyMs, string Status, string Tone, int Connected, int Capacity, string Fingerprint, string Version)
{
    public string Address => $"{Ip}:{Port}";
    public string CapacityText => Capacity <= 0 ? "Sẵn sàng kết nối" : $"{Connected}/{Capacity} thiết bị";
}

public sealed record ReadinessItem(string Title, string Description, bool Ready);
