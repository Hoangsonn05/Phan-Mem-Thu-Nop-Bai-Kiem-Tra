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
    private readonly StudentSessionState session;
    private readonly object gate = new();
    private CancellationTokenSource? loopCts;
    private StudentConnectionState state = StudentConnectionState.Stopped;

    public StudentHeartbeatService(IBackendClient api, StudentSessionState session)
    {
        this.api = api;
        this.session = session;
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
        if (!session.SessionId.HasValue || !session.ParticipantId.HasValue || string.IsNullOrWhiteSpace(session.AccessToken))
            return false;
        api.SetParticipantToken(session.AccessToken);
        var response = await api.PostAsync<HeartbeatRequest, object>(
            $"api/v1/sessions/{session.SessionId}/participants/{session.ParticipantId}/heartbeat",
            new HeartbeatRequest("Ready", DateTimeOffset.UtcNow, 0), ct);
        return response?.Success == true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!session.HasSession || string.IsNullOrWhiteSpace(session.AccessToken)) break;
                api.SetParticipantToken(session.AccessToken);
                var response = await api.PostAsync<HeartbeatRequest, object>(
                    $"api/v1/sessions/{session.SessionId}/participants/{session.ParticipantId}/heartbeat",
                    new HeartbeatRequest("Ready", DateTimeOffset.UtcNow, 0), ct);
                if (response?.Success == true)
                {
                    failures = 0;
                    SetState(StudentConnectionState.Online);
                    await Task.Delay(HealthyInterval, ct);
                    continue;
                }

                if (response?.Error?.Code is ErrorCodes.Unauthorized or ErrorCodes.ParticipantTokenRequired)
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
            await Task.Delay(RetryDelays[Math.Min(failures - 1, RetryDelays.Length - 1)], ct);
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (session.HasSession) Start(); else Stop();
    }

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
}
