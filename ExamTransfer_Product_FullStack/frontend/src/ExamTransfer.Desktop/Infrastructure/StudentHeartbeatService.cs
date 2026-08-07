using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class StudentHeartbeatService : IStudentHeartbeatService
{
    private static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly IBackendClient api;
    private readonly IPublicCloudHeartbeatClient publicCloud;
    private readonly StudentSessionState session;
    private readonly IServerClock serverClock;
    private readonly Func<string> deviceId;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object gate = new();
    private CancellationTokenSource? loopCts;
    private StudentConnectionState state = StudentConnectionState.Stopped;

    public StudentHeartbeatService(
        IBackendClient api,
        IPublicCloudHeartbeatClient publicCloud,
        StudentSessionState session,
        IServerClock serverClock,
        Func<string>? deviceId = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.api = api;
        this.publicCloud = publicCloud;
        this.session = session;
        this.serverClock = serverClock;
        this.deviceId = deviceId
            ?? (() => Environment.MachineName + "-" + Environment.UserName);
        this.delay = delay ?? Task.Delay;
        session.SessionChanged += OnSessionChanged;
    }

    public StudentConnectionState State { get { lock (gate) return state; } }
    public event EventHandler<StudentConnectionState>? StateChanged;

    public void Start()
    {
        lock (gate)
        {
            StopCore();
            if (!session.HasSession || string.IsNullOrWhiteSpace(session.AccessToken)) return;
            loopCts = new CancellationTokenSource();
            SetStateCore(StudentConnectionState.Connecting);
            _ = RunAsync(loopCts.Token);
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            StopCore();
            SetStateCore(StudentConnectionState.Stopped);
        }
    }

    public async Task<bool> ProbeNowAsync(CancellationToken ct = default)
    {
        return await SendHeartbeatAsync(ct) == HeartbeatAttemptResult.Success;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!session.HasSession || string.IsNullOrWhiteSpace(session.AccessToken)) break;
                var result = await SendHeartbeatAsync(ct);
                if (result == HeartbeatAttemptResult.Success)
                {
                    failures = 0;
                    SetState(StudentConnectionState.Online);
                    await delay(HealthyInterval, ct);
                    continue;
                }
                if (result == HeartbeatAttemptResult.NotActive)
                {
                    failures = 0;
                    SetState(StudentConnectionState.Connecting);
                    await delay(HealthyInterval, ct);
                    continue;
                }
                if (result == HeartbeatAttemptResult.AuthenticationExpired)
                {
                    SetState(StudentConnectionState.AuthenticationExpired);
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                FrontendLogger.Log(ex, "StudentHeartbeat");
            }

            failures++;
            SetState(failures >= 3 ? StudentConnectionState.Offline : StudentConnectionState.Reconnecting);
            try
            {
                await delay(RetryDelays[Math.Min(failures - 1, RetryDelays.Length - 1)], ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<HeartbeatAttemptResult> SendHeartbeatAsync(CancellationToken ct)
    {
        if (!session.SessionId.HasValue
            || !session.ParticipantId.HasValue
            || string.IsNullOrWhiteSpace(session.AccessToken))
            return HeartbeatAttemptResult.Retry;

        if (session.AccessMode == SessionAccessMode.PublicCloud)
        {
            if (session.ParticipantStatus is not (
                    ParticipantStatus.Approved or ParticipantStatus.Disconnected))
                return HeartbeatAttemptResult.NotActive;
            try
            {
                _ = await publicCloud.UpsertDeviceHeartbeatAsync(
                    session.SessionId.Value,
                    deviceId(),
                    ConnectionState.Online,
                    ct);
                return HeartbeatAttemptResult.Success;
            }
            catch (PublicCloudApiException ex) when (
                ex.Code is "PUBLICCLOUD_AUTH_EXPIRED" or "PUBLICCLOUD_AUTH_INVALID"
                || ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return HeartbeatAttemptResult.AuthenticationExpired;
            }
        }

        if (session.AccessMode != SessionAccessMode.LanOnly)
            throw new InvalidOperationException(
                $"Unsupported student heartbeat access mode: {session.AccessMode}.");

        api.SetParticipantToken(session.AccessToken);
        var response = await api.PostAsync<HeartbeatRequest, HeartbeatResponse>(
            $"api/v1/sessions/{session.SessionId}/participants/{session.ParticipantId}/heartbeat",
            new HeartbeatRequest("Ready", ClientNowUtc(), 0), ct);
        if (response?.Success == true && response.Data is not null)
        {
            serverClock.Synchronize(response.Data.ServerNowUtc);
            return HeartbeatAttemptResult.Success;
        }
        return response?.Error?.Code is ErrorCodes.Unauthorized or ErrorCodes.ParticipantTokenRequired
            ? HeartbeatAttemptResult.AuthenticationExpired
            : HeartbeatAttemptResult.Retry;
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (session.HasSession) Start(); else Stop();
    }

    private DateTimeOffset ClientNowUtc() =>
        serverClock.TryGetUtcNow(out var serverNowUtc) ? serverNowUtc : DateTimeOffset.UtcNow;

    private void StopCore()
    {
        loopCts?.Cancel();
        loopCts?.Dispose();
        loopCts = null;
    }

    private void SetState(StudentConnectionState value)
    {
        lock (gate) SetStateCore(value);
    }

    private void SetStateCore(StudentConnectionState value)
    {
        if (state == value) return;
        state = value;
        StateChanged?.Invoke(this, value);
    }

    public void Dispose()
    {
        session.SessionChanged -= OnSessionChanged;
        Stop();
    }

    private enum HeartbeatAttemptResult
    {
        Success,
        Retry,
        NotActive,
        AuthenticationExpired
    }
}
