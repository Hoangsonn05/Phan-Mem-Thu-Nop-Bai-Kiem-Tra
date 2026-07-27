using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class SessionFirstOpenFrontendTests
{
    [Fact]
    public async Task StudentScan_SeparatesRoomsFromServers_AndSelectionUsesRoomMetadata()
    {
        var room = new OpenSessionDiscoveryDto(
            Guid.NewGuid(),
            "ROOM42",
            "Phòng thi Toán",
            null,
            null,
            null,
            "Kiểm tra đại số",
            "Cô Lan",
            SessionStatus.Waiting,
            true,
            36,
            4,
            null,
            DateTimeOffset.UtcNow.AddMinutes(10),
            SessionAccessMode.LanOnly,
            "server-1",
            "Máy cô Lan",
            "http://10.10.0.8:5048",
            DateTimeOffset.UtcNow,
            "1",
            "Toán",
            45,
            ExamDeliveryType.FileSubmission,
            SupervisionMode.None,
            SessionAdmissionMode.OpenRequest,
            Guid.NewGuid());
        var server = new DiscoveryServerDto(
            "ExamTransfer.Discovery.v1",
            "Máy cô Lan",
            "10.10.0.8",
            5048,
            "fingerprint",
            1,
            "1.0.0",
            DateTimeOffset.UtcNow,
            "server-1");
        var discovery = new StubLanDiscovery([server], [room]);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow);
        using var viewModel = new StudentConnectViewModel(
            api,
            new StudentSessionState(),
            new AppAuthSessionState(),
            discovery);

        await viewModel.InitializeAsync(CancellationToken.None);

        var selected = Assert.Single(viewModel.Rooms);
        Assert.Single(viewModel.Servers);
        Assert.True(viewModel.HasRooms);
        Assert.Equal("Kiểm tra đại số", selected.ExamTitle);
        Assert.Equal("Toán", selected.Subject);
        Assert.Equal("45 phút", selected.DurationText);
        Assert.Equal("Cô Lan", selected.TeacherName);
        Assert.Equal("ROOM42", viewModel.RoomCode);
        Assert.Equal("10.10.0.8", viewModel.Ip);
        Assert.Equal("5048", viewModel.Port);
        Assert.Null(selected.Room.ClassId);
        Assert.Null(selected.Room.ClassCode);
    }

    [Fact]
    public async Task StudentScan_WithOnlyServer_ShowsRoomEmptyState()
    {
        var discovery = new StubLanDiscovery(
            [new(
                "ExamTransfer.Discovery.v1",
                "Máy giáo viên",
                "10.10.0.9",
                5048,
                "fingerprint",
                0,
                "1.0.0",
                DateTimeOffset.UtcNow)],
            []);
        using var viewModel = new StudentConnectViewModel(
            new RecordingBackendClient(DateTimeOffset.UtcNow),
            new StudentSessionState(),
            new AppAuthSessionState(),
            discovery);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Empty(viewModel.Rooms);
        Assert.Single(viewModel.Servers);
        Assert.True(viewModel.HasNoRooms);
        Assert.Null(viewModel.SelectedRoom);
    }

    [Fact]
    public async Task TeacherQuickCreate_IsOpenClasslessAndAtomic_WhileAdvancedRemainsClassBased()
    {
        var classId = Guid.NewGuid();
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            classId,
            "Kiểm tra",
            "Toán",
            45,
            ExamDeliveryType.FileSubmission,
            ExamStatus.Published,
            1,
            1,
            "exam-rv");
        var summary = new SessionSummaryDto(
            Guid.NewGuid(),
            exam.Id,
            exam.Title,
            "ROOM42",
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            "session-rv",
            AdmissionMode: SessionAdmissionMode.OpenRequest);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [],
            SessionDetailResponse = new(summary, [], "{}", Capacity: 36)
        };
        using var viewModel = new SessionManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.CreateCommand.CanExecute(null));
        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains("api/v1/sessions/create-and-open"),
            TimeSpan.FromSeconds(2)));
        var quick = Assert.IsType<CreateSessionRequest>(api.PostRequests[0]);
        Assert.Null(quick.ClassId);
        Assert.Equal(SessionAdmissionMode.OpenRequest, quick.AdmissionMode);
        Assert.False(quick.AutoApprove);

        viewModel.UseClassAdmission = true;
        Assert.True(viewModel.CreateDraftCommand.CanExecute(null));
        viewModel.CreateDraftCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains("api/v1/sessions"),
            TimeSpan.FromSeconds(2)));
        var advanced = Assert.IsType<CreateSessionRequest>(api.PostRequests[^1]);
        Assert.Equal(classId, advanced.ClassId);
        Assert.Equal(SessionAdmissionMode.ClassMembersOnly, advanced.AdmissionMode);
    }

    [Fact]
    public async Task ExamQuickCreate_DefaultsToNoClassAssignment()
    {
        var rule = new FileRuleDto([".pdf"], 1024, 2048, 1, false, true);
        var created = new ExamDetailDto(
            Guid.NewGuid(),
            null,
            "Kiểm tra nhanh",
            "Tin",
            null,
            45,
            ExamDeliveryType.FileSubmission,
            ExamStatus.Draft,
            1,
            rule,
            [],
            "exam-rv");
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [],
            ExamDetailResponse = created
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.Title = created.Title;
        viewModel.Subject = created.Subject;
        viewModel.Duration = created.DurationMinutes.ToString();

        Assert.False(viewModel.UseClassAssignment);
        Assert.Null(viewModel.SelectedClass);
        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains("api/v1/exams"),
            TimeSpan.FromSeconds(2)));
        var request = Assert.IsType<CreateExamRequest>(api.PostRequests[0]);
        Assert.Null(request.ClassId);
    }

    private sealed class StubLanDiscovery(
        IReadOnlyList<DiscoveryServerDto> servers,
        IReadOnlyList<OpenSessionDiscoveryDto> rooms) : ILanDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult(servers);

        public Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult(rooms);
    }
}
