using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class OnlyLanAccountLeaseTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task StudentWithTrustedLocalAccountToken_SendsHeartbeat()
    {
        using var auth = new AuthFixture(UserRole.Student);
        var handler = new HeartbeatHandler(HttpStatusCode.OK);
        var api = new BackendClient("http://192.168.1.7:5048", handler);
        api.SetAccountToken("local-student-token");

        await RunSingleHeartbeatCycleAsync(api, auth.State);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("local-student-token", handler.Authorization);
        Assert.True(api.HasTrustedAccountToken);
    }

    [Fact]
    public async Task StudentWithoutLocalAccountToken_SkipsHeartbeat()
    {
        using var auth = new AuthFixture(UserRole.Student);
        var handler = new HeartbeatHandler(HttpStatusCode.OK);
        var api = new BackendClient("http://192.168.1.7:5048", handler);

        await RunSingleHeartbeatCycleAsync(api, auth.State);

        Assert.Equal(0, handler.Calls);
        Assert.True(auth.State.IsAuthenticated);
    }

    [Fact]
    public async Task PublicCloudStudentWithoutLocalToken_DoesNotSendLocalHeartbeat()
    {
        using var auth = new AuthFixture(UserRole.Student);
        var handler = new HeartbeatHandler(HttpStatusCode.OK);
        var api = new BackendClient("http://192.168.1.7:5048", handler);

        await RunSingleHeartbeatCycleAsync(api, auth.State);

        Assert.Equal(0, handler.Calls);
        Assert.True(auth.State.IsStudent);
        Assert.True(auth.State.IsAuthenticated);
        Assert.False(api.HasTrustedAccountToken);
    }

    [Fact]
    public async Task StudentHeartbeatUnauthorized_ClearsOnlyLocalAccountToken()
    {
        using var auth = new AuthFixture(UserRole.Student);
        var handler = new HeartbeatHandler(HttpStatusCode.Unauthorized);
        var api = new BackendClient("http://192.168.1.7:5048", handler);
        api.SetAccountToken("expired-local-token");
        var globalLogoutCalls = 0;

        await RunSingleHeartbeatCycleAsync(
            api,
            auth.State,
            () =>
            {
                globalLogoutCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, handler.Calls);
        Assert.False(api.HasTrustedAccountToken);
        Assert.True(auth.State.IsAuthenticated);
        Assert.True(auth.State.IsStudent);
        Assert.Equal(0, globalLogoutCalls);
    }

    [Fact]
    public async Task StudentHeartbeatServerError_RetainsTokenAndAuthentication()
    {
        using var auth = new AuthFixture(UserRole.Student);
        var handler = new HeartbeatHandler(HttpStatusCode.InternalServerError);
        var api = new BackendClient("http://192.168.1.7:5048", handler);
        api.SetAccountToken("local-student-token");

        await RunSingleHeartbeatCycleAsync(api, auth.State);

        Assert.Equal(1, handler.Calls);
        Assert.True(api.HasTrustedAccountToken);
        Assert.True(auth.State.IsAuthenticated);
    }

    [Fact]
    public async Task StudentHeartbeatTimeout_RetainsTokenAndAuthentication()
    {
        using var auth = new AuthFixture(UserRole.Student);
        var handler = new TimeoutHeartbeatHandler();
        var api = new BackendClient("http://192.168.1.7:5048", handler);
        api.SetAccountToken("local-student-token");

        await RunSingleHeartbeatCycleAsync(api, auth.State);

        Assert.Equal(1, handler.Calls);
        Assert.True(api.HasTrustedAccountToken);
        Assert.True(auth.State.IsAuthenticated);
    }

    [Fact]
    public async Task TeacherHeartbeatBehavior_RemainsUnchanged()
    {
        using var auth = new AuthFixture(UserRole.Teacher);
        var handler = new HeartbeatHandler(HttpStatusCode.InternalServerError);
        var api = new BackendClient("http://192.168.1.7:5048", handler);
        api.SetAccountToken("teacher-local-token");
        var globalLogoutCalls = 0;

        await RunSingleHeartbeatCycleAsync(
            api,
            auth.State,
            () =>
            {
                globalLogoutCalls++;
                auth.State.Clear();
                api.SetAccountToken(null);
                return Task.CompletedTask;
            });

        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, globalLogoutCalls);
        Assert.False(auth.State.IsAuthenticated);
        Assert.False(api.HasTrustedAccountToken);
    }

    [Fact]
    public void HeartbeatLoop_DoesNotDuplicateAfterNavigationOrRefresh()
    {
        var source = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "ViewModels", "MainViewModel.cs"));
        var start = source.IndexOf("private void StartAccountHeartbeat()", StringComparison.Ordinal);
        var end = source.IndexOf(
            "internal static async Task RunAccountHeartbeatLoopAsync",
            start,
            StringComparison.Ordinal);
        var method = source[start..end];

        Assert.True(method.IndexOf("accountHeartbeatCts?.Cancel();", StringComparison.Ordinal)
            < method.IndexOf("new CancellationTokenSource()", StringComparison.Ordinal));
        Assert.True(method.IndexOf("accountHeartbeatCts?.Dispose();", StringComparison.Ordinal)
            < method.IndexOf("new CancellationTokenSource()", StringComparison.Ordinal));
        Assert.Equal(1, CountOccurrences(method, "RunAccountHeartbeatLoopAsync("));
    }

    [Fact]
    public void JoinBare401_ReauthenticatesOnceAndSucceeds()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.Unauthorized, JoinOutcome.Success);

        ExecuteJoin(fixture, expectedJoinCalls: 2);

        Assert.Equal(1, fixture.Handler.LoginCalls);
        Assert.True(fixture.State.JoinMutationCommitted);
        Assert.Equal(fixture.ParticipantId, fixture.State.ParticipantId);
        Assert.True(fixture.Api.HasTrustedAccountToken);
    }

    [Fact]
    public void JoinUnauthorized_RetryUsesSameClientNonce()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.Unauthorized, JoinOutcome.Success);

        ExecuteJoin(fixture, expectedJoinCalls: 2);

        Assert.Equal(2, fixture.Handler.JoinNonces.Count);
        Assert.Equal(fixture.Handler.JoinNonces[0], fixture.Handler.JoinNonces[1]);
        Assert.Equal(1, fixture.Handler.LoginCalls);
    }

    [Fact]
    public void JoinUnauthorizedTwice_DoesNotRetryThirdTime()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.Unauthorized, JoinOutcome.Unauthorized);

        ExecuteJoin(fixture, expectedJoinCalls: 2);

        Assert.Equal(2, fixture.Handler.JoinCalls);
        Assert.Equal(1, fixture.Handler.LoginCalls);
        Assert.False(fixture.State.JoinMutationCommitted);
        Assert.False(fixture.State.HasSession);
    }

    [Fact]
    public void JoinTimeout_DoesNotRetry()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.Timeout);

        ExecuteJoin(fixture, expectedJoinCalls: 1);

        Assert.Equal(1, fixture.Handler.JoinCalls);
        Assert.Equal(0, fixture.Handler.LoginCalls);
        Assert.False(fixture.State.JoinMutationCommitted);
    }

    [Fact]
    public void JoinServer500_DoesNotRetry()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.ServerError);

        ExecuteJoin(fixture, expectedJoinCalls: 1);

        Assert.Equal(1, fixture.Handler.JoinCalls);
        Assert.Equal(0, fixture.Handler.LoginCalls);
        Assert.False(fixture.State.JoinMutationCommitted);
    }

    [Fact]
    public void JoinForbidden_DoesNotRetry()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.Forbidden);

        ExecuteJoin(fixture, expectedJoinCalls: 1);

        Assert.Equal(1, fixture.Handler.JoinCalls);
        Assert.Equal(0, fixture.Handler.LoginCalls);
        Assert.False(fixture.State.JoinMutationCommitted);
    }

    [Fact]
    public void IdentityMismatch_DoesNotRetryOrJoin()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.Success, identityMismatch: true);

        fixture.ViewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !fixture.ViewModel.IsBusy
                && fixture.ViewModel.Status.Contains("TOKEN_SERVER_MISMATCH", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3)));

        Assert.Equal(0, fixture.Handler.JoinCalls);
        Assert.Equal(0, fixture.Handler.LoginCalls);
        Assert.False(fixture.State.JoinMutationCommitted);
    }

    [Fact]
    public void ParticipantTokenClearedBeforeLocalLogin()
    {
        using var fixture = JoinFixture.Create(
            JoinOutcome.Success,
            hasInitialAccountToken: false);
        fixture.Api.SetParticipantToken("old-room-participant-token");

        ExecuteJoin(fixture, expectedJoinCalls: 1);

        Assert.Equal(1, fixture.Handler.LoginCalls);
        Assert.Single(fixture.Handler.LoginParticipantTokens);
        Assert.Null(fixture.Handler.LoginParticipantTokens[0]);
    }

    [Fact]
    public void JoinSuccess_AppliesStateExactlyOnce()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.Success);
        var committedTransitions = 0;
        fixture.State.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StudentSessionState.JoinMutationCommitted)
                && fixture.State.JoinMutationCommitted)
                committedTransitions++;
        };

        ExecuteJoin(fixture, expectedJoinCalls: 1);

        Assert.Equal(1, fixture.Handler.JoinCalls);
        Assert.Equal(1, committedTransitions);
        Assert.Equal(1, fixture.CompletionCalls);
        Assert.Equal(fixture.ParticipantId, fixture.State.ParticipantId);
    }

    [Fact]
    public void LeaveRoomThenJoinSecondRoomAfterExpiredLease_Succeeds()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.Unauthorized, JoinOutcome.Success);
        ApplyCharacterizedLeaveSideEffects(fixture);

        ExecuteJoin(fixture, expectedJoinCalls: 2);

        Assert.Equal(1, fixture.Handler.LoginCalls);
        Assert.Equal(2, fixture.Handler.JoinCalls);
        Assert.Equal(fixture.ParticipantId, fixture.State.ParticipantId);
        Assert.True(fixture.State.JoinMutationCommitted);
    }

    [Fact]
    public void LeaveRoomThenJoinSecondRoomWithValidLease_DoesNotRelogin()
    {
        using var fixture = JoinFixture.Create(JoinOutcome.Success);
        ApplyCharacterizedLeaveSideEffects(fixture);

        ExecuteJoin(fixture, expectedJoinCalls: 1);

        Assert.Equal(0, fixture.Handler.LoginCalls);
        Assert.Equal(1, fixture.Handler.JoinCalls);
        Assert.Equal(fixture.ParticipantId, fixture.State.ParticipantId);
    }

    private static async Task RunSingleHeartbeatCycleAsync(
        IBackendClient api,
        AppAuthSessionState authState,
        Func<Task>? clearAuthToLogin = null)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var delayCalls = 0;
        await MainViewModel.RunAccountHeartbeatLoopAsync(
            api,
            authState,
            (_, token) =>
            {
                if (Interlocked.Increment(ref delayCalls) == 1)
                    return Task.CompletedTask;
                cancellation.Cancel();
                return Task.FromCanceled(token);
            },
            clearAuthToLogin ?? (() => Task.CompletedTask),
            cancellation.Token);
    }

    private static void ExecuteJoin(JoinFixture fixture, int expectedJoinCalls)
    {
        Assert.True(fixture.ViewModel.JoinCommand.CanExecute(null));
        fixture.ViewModel.JoinCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => !fixture.ViewModel.IsBusy
                && fixture.Handler.JoinCalls == expectedJoinCalls,
            TimeSpan.FromSeconds(3)));
    }

    private static void ApplyCharacterizedLeaveSideEffects(JoinFixture fixture)
    {
        var source = File.ReadAllText(FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "ViewModels", "ProductModules.cs"));
        var start = source.IndexOf("private void Leave()", StringComparison.Ordinal);
        var end = source.IndexOf("private void SubscribeRealtime", start, StringComparison.Ordinal);
        var method = source[start..end];
        var stop = method.IndexOf("realtime.StopAsync()", StringComparison.Ordinal);
        var reset = method.IndexOf("state.Reset();", StringComparison.Ordinal);
        var clearParticipant = method.IndexOf("api.SetParticipantToken(null);", StringComparison.Ordinal);

        Assert.True(stop >= 0 && stop < reset && reset < clearParticipant);
        fixture.State.ApplyJoin(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "participant-token-room-1",
            "ROOM01",
            "HS001",
            "Student",
            SessionAccessMode.LanOnly,
            "server-1");
        fixture.Api.SetParticipantToken("participant-token-room-1");
        fixture.State.Reset();
        fixture.Api.SetParticipantToken(null);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string FindFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = segments.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private static OpenSessionDiscoveryDto Room(Guid sessionId, Guid examId) =>
        new(
            sessionId,
            "ROOM42",
            "Room 2",
            null,
            null,
            null,
            "Exam",
            "Teacher",
            SessionStatus.Waiting,
            true,
            40,
            0,
            null,
            null,
            SessionAccessMode.LanOnly,
            "server-1",
            "Teacher",
            "http://192.168.1.7:5048",
            DateTimeOffset.UtcNow,
            DiscoveryProtocol.ProtocolVersion,
            "Subject",
            45,
            ExamDeliveryType.FileSubmission,
            SupervisionMode.None,
            SessionAdmissionMode.OpenRequest,
            examId);

    private enum JoinOutcome
    {
        Success,
        Unauthorized,
        Forbidden,
        ServerError,
        Timeout
    }

    private sealed class AuthFixture : IDisposable
    {
        private readonly string directory;

        public AuthFixture(UserRole role, bool includeTransientCredentials = false)
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                "examtransfer-account-lease-" + Guid.NewGuid().ToString("N"));
            var providerId = Guid.NewGuid();
            Account = new CurrentAccountDto(
                providerId,
                role == UserRole.Student ? "student01" : "teacher01",
                null,
                role == UserRole.Student ? "Student" : "Teacher",
                role == UserRole.Student ? "HS001" : null,
                role,
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid(),
                "device-1",
                DateTimeOffset.UtcNow.AddHours(1),
                role == UserRole.Student ? new DateOnly(2010, 1, 1) : null,
                ProviderUserId: providerId.ToString("D"));
            State = new AppAuthSessionState(Path.Combine(directory, "session.bin"));
            State.SetAuthenticated(
                Account,
                role == UserRole.Student ? "supabase-session-token" : "teacher-local-token",
                role == UserRole.Student
                    ? AuthSessionAuthority.Supabase
                    : AuthSessionAuthority.LocalServer);
            if (includeTransientCredentials)
                State.SetTransientCredentials(Account.Username, "temporary-password");
        }

        public AppAuthSessionState State { get; }
        public CurrentAccountDto Account { get; }

        public void Dispose()
        {
            State.Clear();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private sealed class JoinFixture : IDisposable
    {
        private readonly AuthFixture auth;

        private JoinFixture(
            AuthFixture auth,
            BackendClient api,
            AccountLeaseJoinHandler handler,
            StudentSessionState state,
            StudentConnectViewModel viewModel,
            Guid participantId,
            Func<int> completionCalls)
        {
            this.auth = auth;
            Api = api;
            Handler = handler;
            State = state;
            ViewModel = viewModel;
            ParticipantId = participantId;
            getCompletionCalls = completionCalls;
        }

        private readonly Func<int> getCompletionCalls;
        public BackendClient Api { get; }
        public AccountLeaseJoinHandler Handler { get; }
        public StudentSessionState State { get; }
        public StudentConnectViewModel ViewModel { get; }
        public Guid ParticipantId { get; }
        public int CompletionCalls => getCompletionCalls();

        public static JoinFixture Create(
            JoinOutcome firstOutcome,
            JoinOutcome? secondOutcome = null,
            bool identityMismatch = false,
            bool hasInitialAccountToken = true)
        {
            var auth = new AuthFixture(UserRole.Student, includeTransientCredentials: true);
            var room = Room(Guid.NewGuid(), Guid.NewGuid());
            var participantId = Guid.NewGuid();
            var outcomes = secondOutcome.HasValue
                ? new[] { firstOutcome, secondOutcome.Value }
                : new[] { firstOutcome };
            var handler = new AccountLeaseJoinHandler(
                room,
                participantId,
                auth.Account,
                outcomes,
                identityMismatch);
            var api = new BackendClient("http://192.168.1.7:5048", handler);
            if (hasInitialAccountToken)
                api.SetAccountToken("stale-or-valid-account-token");
            var state = new StudentSessionState();
            var completionCalls = 0;
            var viewModel = new StudentConnectViewModel(
                api,
                state,
                auth.State,
                new RecordingDiscovery(room),
                _ =>
                {
                    completionCalls++;
                    return Task.CompletedTask;
                });
            viewModel.RoomCode = room.RoomCode;
            return new(
                auth,
                api,
                handler,
                state,
                viewModel,
                participantId,
                () => completionCalls);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            auth.Dispose();
        }
    }

    private sealed class HeartbeatHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/auth/heartbeat", request.RequestUri!.AbsolutePath);
            Calls++;
            Authorization = request.Headers.Authorization?.Parameter;
            var response = new HttpResponseMessage(statusCode);
            response.Content = statusCode == HttpStatusCode.OK
                ? JsonContent.Create(
                    ApiResponse<AccountHeartbeatResponse>.Ok(
                        new AccountHeartbeatResponse(
                            true,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow.AddMinutes(2),
                            30),
                        "heartbeat-trace"),
                    options: Json)
                : new StringContent(string.Empty);
            return Task.FromResult(response);
        }
    }

    private sealed class TimeoutHeartbeatHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("simulated heartbeat timeout"));
        }
    }

    private sealed class RecordingDiscovery(OpenSessionDiscoveryDto room) : ILanDiscoveryService
    {
        public Task<LanDiscoverySnapshot> DiscoverSnapshotAsync(
            TimeSpan timeout,
            string? roomCode = null,
            CancellationToken ct = default) =>
            Task.FromResult(new LanDiscoverySnapshot([], [room], "request", 1));

        public Task<IReadOnlyList<DiscoveryServerDto>> DiscoverAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoveryServerDto>>([]);

        public Task<IReadOnlyList<OpenSessionDiscoveryDto>> DiscoverOpenSessionsAsync(
            TimeSpan timeout,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OpenSessionDiscoveryDto>>([room]);

        public Task<OpenSessionDiscoveryDto?> DiscoverByRoomCodeAsync(
            string roomCode,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult<OpenSessionDiscoveryDto?>(room);
    }

    private sealed class AccountLeaseJoinHandler(
        OpenSessionDiscoveryDto room,
        Guid participantId,
        CurrentAccountDto currentAccount,
        IReadOnlyList<JoinOutcome> outcomes,
        bool identityMismatch) : HttpMessageHandler
    {
        private readonly Queue<JoinOutcome> remaining = new(outcomes);

        public int JoinCalls { get; private set; }
        public int LoginCalls { get; private set; }
        public List<string> JoinNonces { get; } = [];
        public List<string?> LoginParticipantTokens { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/discovery/identity", StringComparison.Ordinal))
            {
                return Ok(new LocalServerIdentityDto(
                    "ExamTransfer.LocalServer",
                    identityMismatch ? "other-server" : room.ServerId,
                    DiscoveryProtocol.ProtocolVersion,
                    DiscoveryProtocol.DefaultPort,
                    ReleaseIdentity.BuildId,
                    ReleaseIdentity.SemanticVersion,
                    "192.168.1.7",
                    5048));
            }

            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                LoginCalls++;
                LoginParticipantTokens.Add(ParticipantToken(request));
                return Ok(new AccountLoginResultDto(
                    true,
                    false,
                    null,
                    currentAccount.UserId,
                    currentAccount.DisplayName,
                    currentAccount.StudentCode,
                    UserRole.Student,
                    currentAccount.OrganizationId,
                    "refreshed-account-token",
                    DateTimeOffset.UtcNow.AddHours(1),
                    currentAccount.DeviceId));
            }

            if (path.EndsWith("/auth/me", StringComparison.Ordinal))
                return Ok(currentAccount);

            if (!path.EndsWith("/sessions/join", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            JoinCalls++;
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            var join = JsonSerializer.Deserialize<JoinSessionRequest>(json, Json)
                ?? throw new JsonException("Join request body was empty.");
            JoinNonces.Add(join.Nonce);
            var outcome = remaining.Count > 0 ? remaining.Dequeue() : JoinOutcome.Success;
            if (outcome == JoinOutcome.Timeout)
                throw new TaskCanceledException("simulated join timeout");
            if (outcome != JoinOutcome.Success)
            {
                var status = outcome switch
                {
                    JoinOutcome.Unauthorized => HttpStatusCode.Unauthorized,
                    JoinOutcome.Forbidden => HttpStatusCode.Forbidden,
                    _ => HttpStatusCode.InternalServerError
                };
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(string.Empty),
                    RequestMessage = request
                };
            }

            var participant = new ParticipantDto(
                participantId,
                room.SessionId,
                "HS001",
                "Student",
                "device-1",
                "student-pc",
                "192.168.1.20",
                ReleaseIdentity.SemanticVersion,
                ParticipantStatus.PendingApproval,
                DateTimeOffset.UtcNow,
                DownloadStatus.NotStarted,
                SubmissionStatus.NotStarted,
                0,
                null,
                ConnectionState.Online);
            return Ok(new JoinSessionResponse(
                room.SessionId,
                participantId,
                ParticipantStatus.PendingApproval,
                "participant-token-room-2",
                DateTimeOffset.UtcNow.AddHours(1),
                participant));
        }

        private static string? ParticipantToken(HttpRequestMessage request) =>
            request.Headers.TryGetValues("X-Exam-Session-Token", out var values)
                ? values.SingleOrDefault()
                : null;

        private static HttpResponseMessage Ok(object data) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    ApiResponse<object>.Ok(data, "test-trace"),
                    options: Json)
            };
    }
}
