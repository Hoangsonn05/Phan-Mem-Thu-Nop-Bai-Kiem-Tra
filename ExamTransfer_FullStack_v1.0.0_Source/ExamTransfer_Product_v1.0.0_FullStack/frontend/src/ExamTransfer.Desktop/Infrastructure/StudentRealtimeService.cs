using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class StudentRealtimeService : IStudentRealtimeService
{
    private readonly IBackendClient api;
    private readonly StudentSessionState session;
    private readonly SupabaseRealtimeService publicRealtime;
    private readonly SemaphoreSlim gate = new(1, 1);
    private RealtimeService? realtime;

    public bool IsConnected => session.AccessMode == ExamTransfer.Shared.Contracts.SessionAccessMode.PublicCloud
        ? publicRealtime.IsConnected
        : realtime?.IsConnected == true;
    public event EventHandler<string>? EventReceived;
    public event EventHandler<StudentRealtimeNotification>? NotificationReceived;

    public StudentRealtimeService(
        IBackendClient api,
        StudentSessionState session,
        SupabaseRealtimeService publicRealtime)
    {
        this.api = api;
        this.session = session;
        this.publicRealtime = publicRealtime;
        publicRealtime.EventReceived += Forward;
        publicRealtime.NotificationReceived += ForwardNotification;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync(ct);
            if (!session.HasSession || string.IsNullOrWhiteSpace(session.AccessToken)) return;
            if (session.AccessMode == ExamTransfer.Shared.Contracts.SessionAccessMode.PublicCloud)
                return;
            realtime = new RealtimeService(api.BaseAddress.ToString());
            realtime.EventReceived += Forward;
            realtime.NotificationReceived += ForwardNotification;
            await realtime.ConnectAsync(session.AccessToken, ct);
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (session.AccessMode == ExamTransfer.Shared.Contracts.SessionAccessMode.PublicCloud)
                publicRealtime.Stop();
            await StopCoreAsync(ct);
        }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        if (realtime is null) return;
        realtime.EventReceived -= Forward;
        realtime.NotificationReceived -= ForwardNotification;
        await realtime.DisconnectAsync(ct);
        await realtime.DisposeAsync();
        realtime = null;
    }

    private void Forward(object? sender, string eventName) => EventReceived?.Invoke(this, eventName);
    private void ForwardNotification(object? sender, StudentRealtimeNotification value) =>
        NotificationReceived?.Invoke(this, value);

    public void Dispose()
    {
        publicRealtime.EventReceived -= Forward;
        publicRealtime.NotificationReceived -= ForwardNotification;
        StopAsync().SafeFireAndForget("StudentRealtime.Dispose");
        gate.Dispose();
    }
}
