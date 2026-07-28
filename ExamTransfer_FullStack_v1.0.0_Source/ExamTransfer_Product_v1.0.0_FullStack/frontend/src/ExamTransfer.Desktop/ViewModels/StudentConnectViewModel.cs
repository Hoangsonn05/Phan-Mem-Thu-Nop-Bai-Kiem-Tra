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
    private readonly Func<CancellationToken, Task> completeLanJoin;
    private readonly Func<bool> publicCloudReady;
    private readonly Func<string?> publicCloudConfigurationError;
    private readonly Func<string, CancellationToken, Task<ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult>> joinPublicCloud;
    private readonly Func<ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult, CancellationToken, Task> completePublicCloudJoin;
    private string ip = "127.0.0.1";
    private string port = "5048";
    private string roomCode = string.Empty;
    private string displayName;
    private string studentCode;
    private string className = string.Empty;
    private string classCode = string.Empty;
    private bool isScanning;
    private SessionAccessMode selectedAccessMode = SessionAccessMode.LanOnly;
    private string validationMessage = "Nhập mã phòng để tham gia trong mạng LAN.";
    private OpenRoomCard? selectedRoom;
    private ServerCard? selectedServer;

    public StudentConnectViewModel(
        IBackendClient api,
        StudentSessionState state,
        AppAuthSessionState authState,
        ILanDiscoveryService? discovery = null,
        Func<CancellationToken, Task>? completeLanJoin = null,
        Func<bool>? publicCloudReady = null,
        Func<string, CancellationToken, Task<ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult>>? joinPublicCloud = null,
        Func<ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult, CancellationToken, Task>? completePublicCloudJoin = null,
        Func<string?>? publicCloudConfigurationError = null)
    {
        this.api = api;
        this.state = state;
        this.authState = authState;
        this.discovery = discovery ?? AppServices.LanDiscovery;
        this.completeLanJoin = completeLanJoin ?? CompleteLanJoinAsync;
        this.publicCloudReady = publicCloudReady
            ?? (() => AppServices.PublicCloud.Configured && AppServices.PublicCloud.Authenticated);
        this.publicCloudConfigurationError = publicCloudConfigurationError
            ?? (() => AppServices.PublicCloud.ConfigurationErrorCode
                ?? (AppServices.PublicCloud.Configured
                    ? "PUBLICCLOUD_AUTH_EXPIRED"
                    : "PUBLICCLOUD_NOT_CONFIGURED"));
        this.joinPublicCloud = joinPublicCloud ?? ((code, ct) =>
            AppServices.PublicCloud.JoinByRoomCodeAsync(
                code,
                Environment.MachineName + "-" + Environment.UserName,
                Environment.MachineName,
                "1.0.0",
                ct,
                () =>
                {
                    Status = "Phòng đang đồng bộ PublicCloud; ứng dụng sẽ thử lại trong thời gian giới hạn.";
                    StatusTone = "warning";
                }));
        this.completePublicCloudJoin = completePublicCloudJoin ?? CompletePublicCloudJoinAsync;
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
    public bool HasRooms => SelectedAccessMode == SessionAccessMode.LanOnly && Rooms.Count > 0;
    public bool HasNoRooms => SelectedAccessMode == SessionAccessMode.LanOnly && Rooms.Count == 0;

    public string Ip { get => ip; set { if (Set(ref ip, value)) RaiseCommands(); } }
    public string Port { get => port; set { if (Set(ref port, value)) RaiseCommands(); } }
    public string RoomCode
    {
        get => roomCode;
        set
        {
            var normalized = RoomCodeRules.Normalize(value);
            if (!Set(ref roomCode, normalized)) return;
            UpdateValidation();
            RaiseCommands();
        }
    }
    public string DisplayName { get => displayName; set { if (Set(ref displayName, value)) RaiseCommands(); } }
    public string StudentCode { get => studentCode; set { if (Set(ref studentCode, value)) RaiseCommands(); } }
    public string ClassName { get => className; set => Set(ref className, value); }
    public string ClassCode { get => classCode; set { if (Set(ref classCode, value)) RaiseCommands(); } }
    public bool IsScanning { get => isScanning; private set { if (Set(ref isScanning, value)) RaiseCommands(); } }
    public SessionAccessMode SelectedAccessMode
    {
        get => selectedAccessMode;
        set
        {
            if (!Set(ref selectedAccessMode, value)) return;
            Raise(nameof(IsLanMode));
            Raise(nameof(IsPublicCloudMode));
            Raise(nameof(HasRooms));
            Raise(nameof(HasNoRooms));
            UpdateValidation();
            RaiseCommands();
        }
    }
    public bool IsLanMode
    {
        get => SelectedAccessMode == SessionAccessMode.LanOnly;
        set
        {
            if (value) SelectedAccessMode = SessionAccessMode.LanOnly;
        }
    }
    public bool IsPublicCloudMode
    {
        get => SelectedAccessMode == SessionAccessMode.PublicCloud;
        set
        {
            if (value) SelectedAccessMode = SessionAccessMode.PublicCloud;
        }
    }
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
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
            var snapshot = await discovery.DiscoverSnapshotAsync(TimeSpan.FromSeconds(2), null, ct);
            foreach (var server in snapshot.Servers)
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
            foreach (var room in snapshot.Rooms)
                Rooms.Add(new(room));
            Raise(nameof(HasRooms));
            Raise(nameof(HasNoRooms));

            SelectedRoom = Rooms.FirstOrDefault();
            SelectedServer = Servers.FirstOrDefault();
            if (Rooms.Count == 0)
            {
                Status = "Chưa tìm thấy kỳ thi LanOnly đang chờ. Có thể quét lại hoặc nhập mã phòng.";
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
            Status = "Không tìm thấy máy giáo viên trong mạng hiện tại. Hãy kiểm tra Wi-Fi/LAN rồi quét lại.";
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
        && (RoomCodeRules.IsValid(RoomCode) || (IsLanMode && SelectedRoom is not null))
        && (SelectedAccessMode == SessionAccessMode.PublicCloud
            ? publicCloudReady()
            : true);

    private async Task JoinAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            FrontendLogger.LogMessage(
                $"selected_mode={SelectedAccessMode}; room={MaskRoomCode(RoomCode)}; phase=dispatch",
                "StudentConnect.Join");
            await StudentConnectJoinRouter.DispatchAsync(
                SelectedAccessMode,
                JoinLanAsync,
                JoinPublicCloudAsync,
                DisposeToken);
            if (!IsDisposed)
            {
                Status = "Đã gửi yêu cầu, chờ giáo viên duyệt.";
                StatusTone = "success";
            }
        }
        catch (LanJoinException ex)
        {
            ReportJoinFailure(ex.Code, ex.Message, ex);
        }
        catch (BackendApiException ex)
        {
            ReportJoinFailure(MapBackendCode(ex.ApiCode), ex.Message, ex);
        }
        catch (ExamTransfer.Desktop.Infrastructure.PublicCloudApiException ex)
        {
            ReportJoinFailure(ex.Code, PublicCloudErrorMessage(ex.Code), ex);
        }
        catch (HttpRequestException ex)
        {
            ReportJoinFailure("NETWORK_ERROR", "Không thể kết nối tới máy giáo viên.", ex);
        }
        catch (Exception ex)
        {
            ReportJoinFailure("NETWORK_ERROR", "Không thể hoàn tất yêu cầu tham gia.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task JoinLanAsync(CancellationToken ct)
    {
        var requestedCode = RoomCodeRules.Normalize(RoomCode);
        if (!RoomCodeRules.IsValid(requestedCode))
            throw new LanJoinException("ROOM_CODE_INVALID", RoomCodeRules.ValidationMessage);

        Status = "Đang tìm máy giáo viên...";
        StatusTone = "primary";
        var room = SelectedRoom?.RoomCode.Equals(requestedCode, StringComparison.OrdinalIgnoreCase) == true
            ? SelectedRoom
            : Rooms.FirstOrDefault(x => x.RoomCode.Equals(requestedCode, StringComparison.OrdinalIgnoreCase));
        if (room is null)
        {
            try
            {
                var discovered = await discovery.DiscoverByRoomCodeAsync(
                    requestedCode,
                    TimeSpan.FromSeconds(4),
                    ct);
                if (discovered is null)
                    throw new LanJoinException(
                        "DISCOVERY_TIMEOUT",
                        "Không tìm thấy mã phòng này trong mạng LAN.");
                room = new OpenRoomCard(discovered);
            }
            catch (LanDiscoveryException ex)
            {
                throw new LanJoinException(ex.Code, ex.Message, ex);
            }
        }

        Status = "Đã tìm thấy máy chủ...";
        var endpoint = new Uri(room.BaseAddress);
        if (!api.TrySetBaseAddress(
                endpoint.GetLeftPart(UriPartial.Authority),
                endpoint.Port,
                out var endpointError))
            throw new LanJoinException(
                "ENDPOINT_UNREACHABLE",
                endpointError ?? "Đã tìm thấy phòng nhưng không thể kết nối tới máy giáo viên.");

        Status = "Đang xác minh phòng...";
        LocalServerIdentityDto identity;
        try
        {
            identity = ApiGuard.Require(await api.GetAsync<LocalServerIdentityDto>(
                "api/v1/discovery/identity",
                ct));
        }
        catch (BackendApiException ex) when (ex.HttpStatusCode == 404)
        {
            throw new LanJoinException(
                DiscoveryProtocol.ProtocolMismatch,
                "Local Server không có identity endpoint V2. Hãy cập nhật đồng bộ máy giáo viên và học sinh.",
                ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LanJoinException(
                "ENDPOINT_UNREACHABLE",
                "Đã tìm thấy phòng nhưng không thể kết nối tới máy giáo viên.",
                ex);
        }
        if (!identity.Protocol.Equals(DiscoveryProtocol.ProtocolVersion, StringComparison.Ordinal))
            throw new LanJoinException(
                DiscoveryProtocol.ProtocolMismatch,
                "Local Server dùng discovery protocol không tương thích.");
        if (identity.DiscoveryPort != DiscoveryProtocol.DefaultPort)
            throw new LanJoinException(
                DiscoveryProtocol.PortMismatch,
                $"Local Server không dùng UDP {DiscoveryProtocol.DefaultPort}.");
        if (!identity.BuildId.Equals(ReleaseIdentity.BuildId, StringComparison.Ordinal))
            throw new LanJoinException(
                DiscoveryProtocol.BuildMismatch,
                "Client và Local Server không cùng BuildId. Hãy cài lại cùng bộ cài ExamTransfer.");
        if (!identity.Product.Equals("ExamTransfer.LocalServer", StringComparison.Ordinal)
            || !identity.ServerId.Equals(room.Room.ServerId, StringComparison.OrdinalIgnoreCase)
            || !identity.AdvertisedAddress.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)
            || identity.ServerPort != endpoint.Port)
            throw new LanJoinException(
                "TOKEN_SERVER_MISMATCH",
                "Máy chủ phản hồi không khớp danh tính của phòng đã tìm thấy.");

        await EnsureLocalAccountAsync(identity.ServerId, ct);
        state.Reset();
        api.SetParticipantToken(null);
        Status = "Đang gửi yêu cầu tham gia...";
        var request = new JoinSessionRequest(
            requestedCode,
            StudentCode.Trim(),
            DisplayName.Trim(),
            null,
            Environment.MachineName + "-" + Environment.UserName,
            Environment.MachineName,
            "1.0.0",
            Guid.NewGuid().ToString("N"));
        var response = ApiGuard.Require(await api.PostAsync<JoinSessionRequest, JoinSessionResponse>(
            "api/v1/sessions/join",
            request,
            ct));
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
        await completeLanJoin(ct);
    }

    private async Task EnsureLocalAccountAsync(string expectedServerId, CancellationToken ct)
    {
        if (api.HasTrustedAccountToken) return;
        if (!authState.TryGetTransientCredentials(out var account, out var password))
            throw new LanJoinException(
                "AUTH_REQUIRED",
                "Cần xác thực tài khoản học sinh trên máy giáo viên này. Hãy đăng nhập lại rồi thử tham gia.");

        try
        {
            var login = ApiGuard.Require(await api.PostAsync<AccountLoginRequest, AccountLoginResultDto>(
                "api/v1/auth/login",
                new AccountLoginRequest(
                    account,
                    password,
                    authState.CurrentAccount?.DeviceId ?? Environment.MachineName + "-" + Environment.UserName,
                    Environment.MachineName,
                    "1.0.0"),
                ct));
            if (login.RequiresStudentConfirmation || string.IsNullOrWhiteSpace(login.AccessToken))
                throw new LanJoinException("AUTH_REQUIRED", "Máy giáo viên yêu cầu xác thực tài khoản học sinh.");
            api.SetAccountToken(login.AccessToken);
            var current = ApiGuard.Require(await api.GetAsync<CurrentAccountDto>("api/v1/auth/me", ct));
            if (current.Role != UserRole.Student
                || authState.CurrentAccount is not { } original
                || !string.Equals(current.Username, original.Username, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.StudentCode, original.StudentCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.OrganizationId, original.OrganizationId, StringComparison.OrdinalIgnoreCase))
            {
                api.SetAccountToken(null);
                throw new LanJoinException(
                    "TOKEN_SERVER_MISMATCH",
                    "Tài khoản trên máy giáo viên không khớp tài khoản học sinh đang đăng nhập.");
            }
            authState.SetAuthenticated(current, login.AccessToken);
            FrontendLogger.LogMessage(
                $"server_id={expectedServerId}; phase=local_account_reauthenticated",
                "StudentConnect.Join");
        }
        catch (BackendApiException ex)
        {
            api.SetAccountToken(null);
            throw new LanJoinException(
                "AUTH_REQUIRED",
                "Không thể xác thực tài khoản học sinh trên máy giáo viên này.",
                ex);
        }
        finally
        {
            password = string.Empty;
        }
    }

    private async Task JoinPublicCloudAsync(CancellationToken ct)
    {
        var requestedCode = RoomCodeRules.Normalize(RoomCode);
        if (!RoomCodeRules.IsValid(requestedCode))
            throw new LanJoinException("ROOM_CODE_INVALID", RoomCodeRules.ValidationMessage);
        if (!publicCloudReady())
        {
            var configurationCode = publicCloudConfigurationError()
                ?? "PUBLICCLOUD_NOT_CONFIGURED";
            throw new LanJoinException(
                configurationCode,
                PublicCloudErrorMessage(configurationCode));
        }

        Status = "Đang xác minh phòng...";
        var cloudJoin = await joinPublicCloud(requestedCode, ct);
        Status = "Đang gửi yêu cầu tham gia...";
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
        await completePublicCloudJoin(cloudJoin, ct);
    }

    private static async Task CompletePublicCloudJoinAsync(
        ExamTransfer.Desktop.Infrastructure.PublicCloudJoinResult cloudJoin,
        CancellationToken ct)
    {
        await AppServices.StudentRealtime.StartAsync(ct);
        _ = await AppServices.StudentExamFlow.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            ct);
    }

    private static async Task CompleteLanJoinAsync(CancellationToken ct)
    {
        await AppServices.StudentRealtime.StartAsync(ct);
        _ = await AppServices.StudentExamFlow.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            ct);
    }

    private void UpdateValidation()
    {
        if (string.IsNullOrWhiteSpace(RoomCode))
        {
            ValidationMessage = IsPublicCloudMode
                ? "Nhập mã phòng PublicCloud."
                : "Nhập mã phòng để tham gia trong mạng LAN.";
        }
        else if (!RoomCodeRules.IsValid(RoomCode))
        {
            ValidationMessage = RoomCodeRules.ValidationMessage;
        }
        else if (IsPublicCloudMode && !publicCloudReady())
        {
            ValidationMessage = "PublicCloud chưa được cấu hình hoặc tài khoản chưa đăng nhập.";
        }
        else
        {
            ValidationMessage = string.Empty;
        }
    }

    private void ReportJoinFailure(string code, string message, Exception exception)
    {
        FrontendLogger.Log(exception, $"StudentConnect.Join.{code}");
        FrontendLogger.LogMessage(
            $"selected_mode={SelectedAccessMode}; room={MaskRoomCode(RoomCode)}; typed_error={code}",
            "StudentConnect.Join");
        Status = $"{message} (Mã lỗi: {code})";
        StatusTone = "danger";
    }

    private static string MapBackendCode(string code) => code switch
    {
        "NOT_FOUND" => "ROOM_NOT_WAITING",
        "INVALID_STATE_TRANSITION" => "ROOM_NOT_ACCEPTING",
        "UNAUTHORIZED" or "FORBIDDEN" => "AUTH_REQUIRED",
        _ => "JOIN_REJECTED"
    };

    private static string PublicCloudErrorMessage(string code) => code switch
    {
        "PUBLICCLOUD_NOT_CONFIGURED" => "PublicCloud chưa được cấu hình trên bản cài đặt này.",
        "PUBLICCLOUD_INVALID_URL" => "Địa chỉ PublicCloud không hợp lệ hoặc không dùng HTTPS.",
        "PUBLICCLOUD_INVALID_PUBLISHABLE_KEY" => "Publishable key PublicCloud không hợp lệ.",
        "PUBLICCLOUD_SCHEMA_INCOMPATIBLE" => "PublicCloud chưa đạt schema capability 22.",
        "OPEN_PUBLIC_SESSION_NOT_FOUND" => "Không tìm thấy phòng PublicCloud sau thời gian chờ đồng bộ.",
        "PUBLICCLOUD_AUTH_EXPIRED" or "PUBLICCLOUD_AUTH_INVALID" =>
            "Phiên đăng nhập PublicCloud đã hết hạn; hãy đăng nhập lại.",
        _ => "Không thể hoàn tất yêu cầu PublicCloud."
    };

    private static string MaskRoomCode(string roomCode)
    {
        if (roomCode.Length <= 2) return new string('*', roomCode.Length);
        return $"{roomCode[0]}{new string('*', roomCode.Length - 2)}{roomCode[^1]}";
    }

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

public static class StudentConnectJoinRouter
{
    public static Task DispatchAsync(
        SessionAccessMode mode,
        Func<CancellationToken, Task> lan,
        Func<CancellationToken, Task> publicCloud,
        CancellationToken cancellationToken) =>
        mode switch
        {
            SessionAccessMode.LanOnly => lan(cancellationToken),
            SessionAccessMode.PublicCloud => publicCloud(cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported session access mode: {mode}.")
        };
}

public sealed class LanJoinException : Exception
{
    public LanJoinException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

public sealed record ServerCard(string Name, string Teacher, string Ip, int Port, int LatencyMs, string Status, string Tone, int Connected, int Capacity, string Fingerprint, string Version)
{
    public string Address => $"{Ip}:{Port}";
    public string CapacityText => Capacity <= 0 ? "Sẵn sàng kết nối" : $"{Connected}/{Capacity} thiết bị";
}

public sealed record ReadinessItem(string Title, string Description, bool Ready);
