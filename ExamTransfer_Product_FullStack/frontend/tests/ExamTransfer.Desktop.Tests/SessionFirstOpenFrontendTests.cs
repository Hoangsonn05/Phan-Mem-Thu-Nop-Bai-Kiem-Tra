using System.IO;
using System.Net.Http;
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
    public async Task TeacherQuickCreate_IsAlwaysOpenClasslessNonAutoApprove()
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
        viewModel.AutoApprove = true;

        Assert.True(viewModel.CreateCommand.CanExecute(null));
        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains("api/v1/sessions/create-and-open"),
            TimeSpan.FromSeconds(2)));
        var quick = Assert.IsType<CreateSessionRequest>(api.PostRequests[0]);
        Assert.Null(quick.ClassId);
        Assert.Equal(SessionAdmissionMode.OpenRequest, quick.AdmissionMode);
        Assert.False(quick.AutoApprove);
        Assert.Equal("{\"autoApprove\":false}", quick.SettingsJson);
    }

    [Fact]
    public async Task LargePublicCloudQuiz_HealthyPendingBeyondInitialWait_EventuallyBecomesReadyWithoutManualRetry()
    {
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            null,
            "Bai trac nghiem lon",
            "Tin",
            45,
            ExamDeliveryType.MultipleChoice,
            ExamStatus.Published,
            1,
            50,
            "exam-rv");
        var sessionId = Guid.NewGuid();
        var session = new SessionSummaryDto(
            sessionId,
            exam.Id,
            exam.Title,
            "LARGEQUIZ",
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            "session-rv",
            SessionAccessMode.PublicCloud,
            AdmissionMode: SessionAdmissionMode.OpenRequest);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [],
            SessionDetailResponse = new(session, [], "{}", Capacity: 36)
        };
        for (var index = 0; index < 3; index++)
        {
            api.ProjectionResponses.Enqueue(new(
                sessionId,
                true,
                false,
                SyncStatus.Pending,
                "PUBLICCLOUD_QUIZ_PROJECTION_PENDING",
                "Dang dong bo noi dung trac nghiem len PublicCloud.",
                0));
        }
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            true,
            SyncStatus.Synced,
            "PUBLICCLOUD_QUIZ_PROJECTION_READY",
            "Noi dung trac nghiem da san sang tren PublicCloud.",
            0));
        using var viewModel = new SessionManagementViewModel(
            api,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 3);
        await viewModel.InitializeAsync(CancellationToken.None);
        api.SessionResponses = [session];
        viewModel.AccessMode = SessionAccessMode.PublicCloud;

        viewModel.CreateCommand.Execute(null);

        Assert.True(SpinWait.SpinUntil(
            () => viewModel.CanShareRoomCode,
            TimeSpan.FromSeconds(2)));
        Assert.Equal(
            "Noi dung trac nghiem da san sang tren PublicCloud.",
            viewModel.ProjectionStatus);
        Assert.Single(api.PostPaths, path => path == "api/v1/sessions/create-and-open");
        Assert.DoesNotContain(
            api.PostPaths,
            path => path.EndsWith("/cloud-projection/retry", StringComparison.Ordinal));
        Assert.Equal(
            4,
            api.GetPaths.Count(path => path.EndsWith("/cloud-projection", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ManualRefresh_TransientReadFailure_PreservesReadyAndRecoversWithoutRetry()
    {
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            null,
            "Bai trac nghiem",
            "Tin",
            30,
            ExamDeliveryType.MultipleChoice,
            ExamStatus.Published,
            1,
            20,
            "exam-rv");
        var sessionId = Guid.NewGuid();
        var session = new SessionSummaryDto(
            sessionId,
            exam.Id,
            exam.Title,
            "READYROOM",
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            "session-rv",
            SessionAccessMode.PublicCloud,
            AdmissionMode: SessionAdmissionMode.OpenRequest);
        var calls = 0;
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [session]
        };
        api.ProjectionHandler = (_, _) => ++calls switch
        {
            1 => Task.FromResult<ApiResponse<CloudProjectionReadinessView>?>(
                ApiResponse<CloudProjectionReadinessView>.Ok(new(
                    sessionId,
                    true,
                    true,
                    SyncStatus.Synced,
                    "PUBLICCLOUD_QUIZ_PROJECTION_READY",
                    "Ready before refresh.",
                    0), "test")),
            2 => Task.FromException<ApiResponse<CloudProjectionReadinessView>?>(
                new HttpRequestException("temporary network failure")),
            _ => Task.FromResult<ApiResponse<CloudProjectionReadinessView>?>(
                ApiResponse<CloudProjectionReadinessView>.Ok(new(
                    sessionId,
                    true,
                    true,
                    SyncStatus.Synced,
                    "PUBLICCLOUD_QUIZ_PROJECTION_READY",
                    "Ready after transient refresh failure.",
                    0), "test"))
        };
        using var viewModel = new SessionManagementViewModel(
            api,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 1);
        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.True(viewModel.CanShareRoomCode);

        viewModel.RefreshCommand.Execute(null);

        Assert.True(SpinWait.SpinUntil(
            () => calls >= 3 && viewModel.CanShareRoomCode,
            TimeSpan.FromSeconds(2)));
        Assert.Equal("Ready after transient refresh failure.", viewModel.ProjectionStatus);
        Assert.DoesNotContain(
            api.PostPaths,
            path => path.EndsWith("/cloud-projection/retry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectionMonitor_SessionSwitchIgnoresLateReadyFromOldSession()
    {
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            null,
            "Bai trac nghiem",
            "Tin",
            30,
            ExamDeliveryType.MultipleChoice,
            ExamStatus.Published,
            1,
            20,
            "exam-rv");
        var first = PublicCloudSession(exam, "FIRSTROOM");
        var second = PublicCloudSession(exam, "SECONDROOM");
        var firstCalls = 0;
        var secondCalls = 0;
        var lateFirstReady = new TaskCompletionSource<ApiResponse<CloudProjectionReadinessView>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [first, second]
        };
        api.ProjectionHandler = (path, _) =>
        {
            if (path.Contains(first.Id.ToString(), StringComparison.Ordinal))
            {
                firstCalls++;
                if (firstCalls == 1)
                {
                    return Task.FromResult<ApiResponse<CloudProjectionReadinessView>?>(
                        ApiResponse<CloudProjectionReadinessView>.Ok(new(
                            first.Id,
                            true,
                            false,
                            SyncStatus.Pending,
                            "PUBLICCLOUD_QUIZ_PROJECTION_PENDING",
                            "First pending.",
                            0), "test"));
                }
                return lateFirstReady.Task;
            }

            secondCalls++;
            return Task.FromResult<ApiResponse<CloudProjectionReadinessView>?>(
                ApiResponse<CloudProjectionReadinessView>.Ok(new(
                    second.Id,
                    true,
                    true,
                    SyncStatus.Synced,
                    "PUBLICCLOUD_QUIZ_PROJECTION_READY",
                    "Second ready.",
                    0), "test"));
        };
        using var viewModel = new SessionManagementViewModel(
            api,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 1);
        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => firstCalls == 2, TimeSpan.FromSeconds(2)));

        viewModel.SelectedSession = viewModel.Sessions.Single(row => row.Id == second.Id);

        Assert.True(SpinWait.SpinUntil(
            () => secondCalls == 1 && viewModel.CanShareRoomCode,
            TimeSpan.FromSeconds(2)));
        Assert.Equal("Second ready.", viewModel.ProjectionStatus);
        lateFirstReady.SetResult(ApiResponse<CloudProjectionReadinessView>.Ok(new(
            first.Id,
            true,
            true,
            SyncStatus.Synced,
            "PUBLICCLOUD_QUIZ_PROJECTION_READY",
            "Late first ready.",
            0), "test"));
        await Task.Delay(50);

        Assert.Equal(second.Id, viewModel.SelectedSession?.Id);
        Assert.True(viewModel.CanShareRoomCode);
        Assert.Equal("Second ready.", viewModel.ProjectionStatus);
        Assert.Equal(2, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public async Task ProjectionRead_ForbiddenFailureIsTypedAndDoesNotEnableRetry()
    {
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            null,
            "Bai trac nghiem",
            "Tin",
            30,
            ExamDeliveryType.MultipleChoice,
            ExamStatus.Published,
            1,
            20,
            "exam-rv");
        var session = PublicCloudSession(exam, "FORBIDDENROOM");
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [session]
        };
        api.ProjectionHandler = (path, _) =>
            Task.FromResult<ApiResponse<CloudProjectionReadinessView>?>(new(
                false,
                null,
                new ApiError(
                    "FORBIDDEN",
                    "Projection read is forbidden.",
                    Details: new BackendTransportDetails(403, path, null, false)),
                "test",
                ContractInfo.SchemaVersion));
        using var viewModel = new SessionManagementViewModel(
            api,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 1);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.CanShareRoomCode);
        Assert.False(viewModel.CanRetryProjection);
        Assert.Equal("danger", viewModel.ProjectionTone);
        Assert.Contains("FORBIDDEN", viewModel.ProjectionStatus, StringComparison.Ordinal);
        Assert.Single(api.GetPaths, path => path.EndsWith("/cloud-projection", StringComparison.Ordinal));
    }

    private static SessionSummaryDto PublicCloudSession(ExamSummaryDto exam, string roomCode) =>
        new(
            Guid.NewGuid(),
            exam.Id,
            exam.Title,
            roomCode,
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            $"{roomCode}-rv",
            SessionAccessMode.PublicCloud,
            AdmissionMode: SessionAdmissionMode.OpenRequest);

    [Fact]
    public async Task PublicCloudRoomConflict_RequiresNewCodeAndStaysUnshareableUntilReady()
    {
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            null,
            "Kiểm tra",
            "Toán",
            45,
            ExamDeliveryType.FileSubmission,
            ExamStatus.Published,
            1,
            1,
            "exam-rv");
        var sessionId = Guid.NewGuid();
        var conflicted = new SessionSummaryDto(
            sessionId,
            exam.Id,
            exam.Title,
            "PUB133",
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            "session-rv-1",
            SessionAccessMode.PublicCloud,
            AdmissionMode: SessionAdmissionMode.OpenRequest);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [],
            SessionDetailResponse = new(conflicted, [], "{}", Capacity: 36)
        };
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            false,
            SyncStatus.Conflict,
            ErrorCodes.RoomCodeConflict,
            "Mã phòng PublicCloud đang được sử dụng.",
            1));
        using var viewModel = new SessionManagementViewModel(
            api,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 3);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.AccessMode = SessionAccessMode.PublicCloud;

        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => viewModel.CanRecoverRoomCode,
            TimeSpan.FromSeconds(2)));
        Assert.False(viewModel.CanShareRoomCode);
        Assert.False(viewModel.CanRetryProjection);
        Assert.True(viewModel.RecoverRoomCodeCommand.CanExecute(null));

        var recovered = conflicted with { RoomCode = "NEW133", RowVersion = "session-rv-2" };
        api.SessionDetailResponse = new(recovered, [], "{}", Capacity: 36);
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            false,
            SyncStatus.Pending,
            "PUBLICCLOUD_PROJECTION_PENDING",
            "Đang đồng bộ.",
            0));
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            true,
            SyncStatus.Synced,
            "PUBLICCLOUD_READY",
            "Sẵn sàng.",
            0));
        viewModel.RecoverRoomCodeCommand.Execute(null);

        Assert.True(SpinWait.SpinUntil(
            () => api.PutPaths.Contains($"api/v1/sessions/{sessionId}/room-code")
                && viewModel.CanShareRoomCode,
            TimeSpan.FromSeconds(2)));
        var request = Assert.IsType<ChangePublicCloudRoomCodeRequest>(Assert.Single(api.PutRequests));
        Assert.Null(request.NewRoomCode);
        Assert.Equal("session-rv-1", request.RowVersion);
        Assert.Equal("NEW133", viewModel.RoomCode);
        Assert.False(viewModel.CanRecoverRoomCode);
    }

    [Fact]
    public async Task ExistingPublicCloudConflict_RestoresShareLockAndUsesSelectedRowVersion()
    {
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            null,
            "Kiểm tra đã lưu",
            "Tin",
            30,
            ExamDeliveryType.FileSubmission,
            ExamStatus.Published,
            1,
            1,
            "exam-rv");
        var sessionId = Guid.NewGuid();
        var existing = new SessionSummaryDto(
            sessionId,
            exam.Id,
            exam.Title,
            "EXIST133",
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            "persisted-row-version",
            SessionAccessMode.PublicCloud,
            AdmissionMode: SessionAdmissionMode.OpenRequest);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [existing]
        };
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            false,
            SyncStatus.Conflict,
            ErrorCodes.RoomCodeConflict,
            "Mã phòng PublicCloud đang được sử dụng.",
            1));
        using var viewModel = new SessionManagementViewModel(
            api,
            projectionDelay: (_, _) => Task.CompletedTask,
            projectionPollAttempts: 2);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.CanShareRoomCode);
        Assert.True(viewModel.CanRecoverRoomCode);
        Assert.False(viewModel.CanRetryProjection);
        var recovered = existing with { RoomCode = "RESTORED133", RowVersion = "next-row-version" };
        api.SessionDetailResponse = new(recovered, [], "{}", Capacity: 36);
        api.ProjectionResponses.Enqueue(new(
            sessionId,
            true,
            true,
            SyncStatus.Synced,
            "PUBLICCLOUD_READY",
            "Sẵn sàng.",
            0));

        viewModel.RecoverRoomCodeCommand.Execute(null);

        Assert.True(SpinWait.SpinUntil(
            () => api.PutPaths.Contains($"api/v1/sessions/{sessionId}/room-code")
                && viewModel.CanShareRoomCode,
            TimeSpan.FromSeconds(2)));
        var request = Assert.IsType<ChangePublicCloudRoomCodeRequest>(Assert.Single(api.PutRequests));
        Assert.Null(request.NewRoomCode);
        Assert.Equal("persisted-row-version", request.RowVersion);
    }

    [Fact]
    public async Task PublicCloudQuizStartNotReady_ShowsFocusedRetryMessage()
    {
        var exam = new ExamSummaryDto(
            Guid.NewGuid(),
            null,
            "Bài trắc nghiệm",
            "Tin",
            30,
            ExamDeliveryType.MultipleChoice,
            ExamStatus.Published,
            1,
            2,
            "exam-rv");
        var session = new SessionSummaryDto(
            Guid.NewGuid(),
            exam.Id,
            exam.Title,
            "QUIZSTART",
            SessionStatus.Waiting,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            new(0, 0, 0, 0, 0, 0, 0),
            1,
            "session-rv",
            SessionAccessMode.PublicCloud,
            AdmissionMode: SessionAdmissionMode.OpenRequest);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [exam],
            SessionResponses = [session],
            PostErrorResponse = new(
                ErrorCodes.PublicCloudQuizProjectionNotReady,
                "backend detail must not replace the focused message")
        };
        api.ProjectionResponses.Enqueue(new(
            session.Id,
            true,
            false,
            SyncStatus.Pending,
            "PUBLICCLOUD_QUIZ_PROJECTION_PENDING",
            "Đang đồng bộ nội dung trắc nghiệm lên PublicCloud.",
            0));
        using var viewModel = new SessionManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.StartCommand.Execute(null);

        Assert.True(SpinWait.SpinUntil(
            () => viewModel.Status.Contains(
                "Nội dung trắc nghiệm chưa đồng bộ xong",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)));
        Assert.Equal("danger", viewModel.StatusTone);
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

        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains("api/v1/exams"),
            TimeSpan.FromSeconds(2)));
        var request = Assert.IsType<CreateExamRequest>(api.PostRequests[0]);
        Assert.Null(request.ClassId);
    }

    [Fact]
    public void ProductionXaml_HasNoAdvancedClassCreationOrTechnicalSettings()
    {
        var views = FindViewsDirectory();
        var exam = File.ReadAllText(Path.Combine(views, "ExamManagementView.xaml"));
        var session = File.ReadAllText(Path.Combine(views, "SessionManagementView.xaml"));
        var settings = File.ReadAllText(Path.Combine(views, "SettingsPageView.xaml"));

        Assert.DoesNotContain("Nâng cao — Gắn với lớp học", exam, StringComparison.Ordinal);
        Assert.DoesNotContain("Nâng cao — Luồng lớp học", session, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "Publishable", "Project URL", "Organization", "Secret key",
                     "CloudAccessMode", "TrustedServer", "UserSession",
                     "Discovery port", "Chunk", "Concurrent uploads", "Supabase password"
                 })
        {
            Assert.DoesNotContain(forbidden, settings, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SelectableRows_KeepCheckboxStateSeparateAndEnforceSessionEligibility()
    {
        var finished = new SelectableSessionRow(Session(SessionStatus.Finished));
        var cancelled = new SelectableSessionRow(Session(SessionStatus.Cancelled));
        var running = new SelectableSessionRow(Session(SessionStatus.InProgress));

        finished.IsChecked = true;
        cancelled.IsChecked = true;
        running.IsChecked = true;

        Assert.True(finished.IsChecked);
        Assert.True(cancelled.IsChecked);
        Assert.False(running.IsChecked);
        Assert.False(running.CanArchive);
    }

    private static SessionSummaryDto Session(SessionStatus status) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Bài kiểm tra",
        status + "-ROOM",
        status,
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        new(0, 0, 0, 0, 0, 0, 0),
        1,
        "rv");

    private static string FindViewsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "frontend",
                "src",
                "ExamTransfer.Desktop",
                "Views");
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Không tìm thấy thư mục Views của frontend.");
    }

    private sealed class StubLanDiscovery(
        IReadOnlyList<DiscoveryServerDto> servers,
        IReadOnlyList<OpenSessionDiscoveryDto> rooms) : ILanDiscoveryService
    {
        public Task<LanDiscoverySnapshot> DiscoverSnapshotAsync(
            TimeSpan timeout,
            string? roomCode = null,
            CancellationToken ct = default) =>
            Task.FromResult(new LanDiscoverySnapshot(servers, rooms, "test-request", servers.Count));

        public Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult(servers);

        public Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult(rooms);

        public Task<OpenSessionDiscoveryDto?> DiscoverByRoomCodeAsync(
            string roomCode,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<OpenSessionDiscoveryDto?>(
                rooms.SingleOrDefault(x => x.RoomCode.Equals(roomCode, StringComparison.OrdinalIgnoreCase)));
    }
}
