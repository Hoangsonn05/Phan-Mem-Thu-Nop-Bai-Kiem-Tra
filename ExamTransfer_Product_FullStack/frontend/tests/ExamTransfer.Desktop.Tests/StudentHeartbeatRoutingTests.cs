using System.Net;
using System.Net.Http;
using System.Text.Json;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentHeartbeatRoutingTests
{
    [Fact]
    public async Task PublicCloudApprovedParticipant_UsesCloudHeartbeatAuthority()
    {
        var backend = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var publicCloud = new RecordingPublicCloudHeartbeatClient();
        var session = CreateSession(SessionAccessMode.PublicCloud);
        using var heartbeat = new StudentHeartbeatService(
            backend,
            publicCloud,
            session,
            new ServerClock(),
            () => "stable-device");

        _ = await heartbeat.ProbeNowAsync();

        Assert.DoesNotContain(
            $"api/v1/sessions/{session.SessionId}/participants/{session.ParticipantId}/heartbeat",
            backend.PostPaths);
        var call = Assert.Single(publicCloud.Calls);
        Assert.Equal(session.SessionId, call.SessionId);
        Assert.Equal("stable-device", call.DeviceId);
        Assert.Equal(ConnectionState.Online, call.ConnectionState);
    }

    [Fact]
    public async Task OnlyLanApprovedParticipant_StillUsesLocalHeartbeatEndpoint()
    {
        var backend = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var publicCloud = new RecordingPublicCloudHeartbeatClient();
        var session = CreateSession(SessionAccessMode.LanOnly);
        using var heartbeat = new StudentHeartbeatService(
            backend,
            publicCloud,
            session,
            new ServerClock());

        _ = await heartbeat.ProbeNowAsync();

        Assert.Contains(
            $"api/v1/sessions/{session.SessionId}/participants/{session.ParticipantId}/heartbeat",
            backend.PostPaths);
        Assert.Empty(publicCloud.Calls);
    }

    [Fact]
    public async Task PublicCloudPendingApproval_DoesNotBecomeAuthenticationExpired()
    {
        var backend = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var publicCloud = new RecordingPublicCloudHeartbeatClient();
        var session = CreateSession(SessionAccessMode.PublicCloud);
        session.ParticipantStatus = ParticipantStatus.PendingApproval;
        using var heartbeat = new StudentHeartbeatService(
            backend,
            publicCloud,
            session,
            new ServerClock());

        var result = await heartbeat.ProbeNowAsync();

        Assert.False(result);
        Assert.NotEqual(StudentConnectionState.AuthenticationExpired, heartbeat.State);
        Assert.Empty(publicCloud.Calls);
        Assert.Empty(backend.PostPaths);
        Assert.True(session.HasSession);
    }

    [Fact]
    public async Task PublicCloudHeartbeat_UsesStableDeviceId()
    {
        var backend = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var publicCloud = new RecordingPublicCloudHeartbeatClient();
        var session = CreateSession(SessionAccessMode.PublicCloud);
        using var heartbeat = new StudentHeartbeatService(
            backend,
            publicCloud,
            session,
            new ServerClock(),
            () => "stable-device");

        _ = await heartbeat.ProbeNowAsync();
        _ = await heartbeat.ProbeNowAsync();

        Assert.Equal(2, publicCloud.Calls.Count);
        Assert.All(publicCloud.Calls, call => Assert.Equal("stable-device", call.DeviceId));
    }

    [Fact]
    public async Task PublicCloudParticipant_AfterTeacherApproval_StartsHeartbeat()
    {
        var backend = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var publicCloud = new RecordingPublicCloudHeartbeatClient();
        var session = CreateSession(SessionAccessMode.PublicCloud);
        session.ParticipantStatus = ParticipantStatus.PendingApproval;
        var pendingDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePending = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCalls = 0;
        async Task ControlledDelay(TimeSpan _, CancellationToken ct)
        {
            if (Interlocked.Increment(ref delayCalls) == 1)
            {
                pendingDelay.TrySetResult();
                await releasePending.Task.WaitAsync(ct);
                return;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        using var heartbeat = new StudentHeartbeatService(
            backend,
            publicCloud,
            session,
            new ServerClock(),
            () => "stable-device",
            ControlledDelay);

        heartbeat.Start();
        await pendingDelay.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(publicCloud.Calls);

        session.ParticipantStatus = ParticipantStatus.Approved;
        releasePending.TrySetResult();
        await WaitUntilAsync(() => publicCloud.Calls.Count == 1);

        Assert.Equal(StudentConnectionState.Online, heartbeat.State);
        Assert.True(session.HasSession);
        heartbeat.Stop();
    }

    [Fact]
    public async Task PublicCloudTransientFailure_RetainsSessionAndReconnects()
    {
        var backend = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var publicCloud = new RecordingPublicCloudHeartbeatClient
        {
            Handler = (_, _, _, _) => Task.FromException<Guid>(
                new HttpRequestException("temporary network failure"))
        };
        var session = CreateSession(SessionAccessMode.PublicCloud);
        using var heartbeat = new StudentHeartbeatService(
            backend,
            publicCloud,
            session,
            new ServerClock(),
            delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        heartbeat.Start();
        await WaitUntilAsync(() => heartbeat.State == StudentConnectionState.Reconnecting);

        Assert.True(session.HasSession);
        Assert.NotEqual(StudentConnectionState.AuthenticationExpired, heartbeat.State);
        Assert.Empty(backend.PostPaths);
        heartbeat.Stop();
    }

    [Fact]
    public async Task PublicCloudAuthExpired_IsHandledSeparatelyWithoutClearingSession()
    {
        var backend = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var publicCloud = new RecordingPublicCloudHeartbeatClient
        {
            Handler = (_, _, _, _) => Task.FromException<Guid>(
                new PublicCloudApiException(
                    "PUBLICCLOUD_AUTH_EXPIRED",
                    "expired",
                    HttpStatusCode.Unauthorized))
        };
        var session = CreateSession(SessionAccessMode.PublicCloud);
        using var heartbeat = new StudentHeartbeatService(
            backend,
            publicCloud,
            session,
            new ServerClock());

        heartbeat.Start();
        await WaitUntilAsync(() =>
            heartbeat.State == StudentConnectionState.AuthenticationExpired);

        Assert.True(session.HasSession);
        Assert.Empty(backend.PostPaths);
    }

    [Fact]
    public async Task PublicCloudSessionSwitch_StopsOldHeartbeatIdentity()
    {
        var backend = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var publicCloud = new RecordingPublicCloudHeartbeatClient();
        var session = CreateSession(SessionAccessMode.PublicCloud);
        var oldSessionId = session.SessionId;
        using var heartbeat = new StudentHeartbeatService(
            backend,
            publicCloud,
            session,
            new ServerClock(),
            delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        heartbeat.Start();
        await WaitUntilAsync(() => publicCloud.Calls.Count == 1);

        session.Reset();
        var newSessionId = Guid.NewGuid();
        session.SessionId = newSessionId;
        session.ParticipantId = Guid.NewGuid();
        session.AccessToken = "new-authority-token";
        session.AccessMode = SessionAccessMode.PublicCloud;
        session.ParticipantStatus = ParticipantStatus.Approved;
        heartbeat.Start();
        await WaitUntilAsync(() => publicCloud.Calls.Count == 2);

        Assert.Equal(oldSessionId, publicCloud.Calls[0].SessionId);
        Assert.Equal(newSessionId, publicCloud.Calls[1].SessionId);
        heartbeat.Stop();
    }

    [Fact]
    public async Task PublicCloudLogout_StopsHeartbeatWithoutClearingByService()
    {
        var backend = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var publicCloud = new RecordingPublicCloudHeartbeatClient();
        var session = CreateSession(SessionAccessMode.PublicCloud);
        using var heartbeat = new StudentHeartbeatService(
            backend,
            publicCloud,
            session,
            new ServerClock(),
            delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        heartbeat.Start();
        await WaitUntilAsync(() => publicCloud.Calls.Count == 1);

        session.Reset();
        await WaitUntilAsync(() => heartbeat.State == StudentConnectionState.Stopped);
        var callsAfterLogout = publicCloud.Calls.Count;
        await Task.Delay(50);

        Assert.False(session.HasSession);
        Assert.Equal(callsAfterLogout, publicCloud.Calls.Count);
    }

    [Fact]
    public async Task SupabaseHeartbeat_UsesAuthenticatedRpcContract()
    {
        var sessionId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var handler = new PublicCloudHeartbeatHandler(connectionId);
        using var http = new HttpClient(handler);
        var client = new SupabasePublicCloudClient(
            http,
            supabaseUrl: "https://project.supabase.test",
            publishableKey: "publishable-key");
        await client.LoginAsync("student", "password", CancellationToken.None);

        var result = await client.UpsertDeviceHeartbeatAsync(
            sessionId,
            "stable-device",
            ConnectionState.Online,
            CancellationToken.None);

        Assert.Equal(connectionId, result);
        Assert.Equal(
            "/rest/v1/rpc/upsert_public_device_heartbeat",
            handler.RpcPath);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("access", handler.AuthorizationParameter);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(handler.RpcBody));
        Assert.Equal(
            sessionId,
            payload.RootElement.GetProperty("p_session_id").GetGuid());
        Assert.Equal(
            "stable-device",
            payload.RootElement.GetProperty("p_device_id").GetString());
        Assert.Equal(
            "Online",
            payload.RootElement.GetProperty("p_connection_state").GetString());
        Assert.Equal(
            ReleaseIdentity.SemanticVersion,
            payload.RootElement.GetProperty("p_app_version").GetString());
    }

    private static StudentSessionState CreateSession(SessionAccessMode accessMode) => new()
    {
        SessionId = Guid.NewGuid(),
        ParticipantId = Guid.NewGuid(),
        AccessToken = "authority-token",
        AccessMode = accessMode,
        ParticipantStatus = ParticipantStatus.Approved
    };

    private sealed class RecordingPublicCloudHeartbeatClient : IPublicCloudHeartbeatClient
    {
        public List<HeartbeatCall> Calls { get; } = [];
        public Func<Guid, string, ConnectionState, CancellationToken, Task<Guid>>? Handler { get; init; }

        public Task<Guid> UpsertDeviceHeartbeatAsync(
            Guid sessionId,
            string deviceId,
            ConnectionState connectionState,
            CancellationToken cancellationToken)
        {
            Calls.Add(new(sessionId, deviceId, connectionState));
            return Handler?.Invoke(
                    sessionId,
                    deviceId,
                    connectionState,
                    cancellationToken)
                ?? Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class PublicCloudHeartbeatHandler(Guid connectionId) : HttpMessageHandler
    {
        public string? RpcPath { get; private set; }
        public string? RpcBody { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/auth/v1/token")
            {
                return Json(
                    HttpStatusCode.OK,
                    "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}");
            }

            RpcPath = request.RequestUri?.AbsolutePath;
            RpcBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Json(HttpStatusCode.OK, $"\"{connectionId:D}\"");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed record HeartbeatCall(
        Guid SessionId,
        string DeviceId,
        ConnectionState ConnectionState);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for heartbeat state.");
            await Task.Delay(10);
        }
    }
}
