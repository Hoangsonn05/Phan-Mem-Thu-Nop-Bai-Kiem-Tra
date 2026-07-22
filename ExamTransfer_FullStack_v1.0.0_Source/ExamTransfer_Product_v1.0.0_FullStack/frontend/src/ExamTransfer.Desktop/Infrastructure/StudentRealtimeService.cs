using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class StudentRealtimeService(IBackendClient api, StudentSessionState session) : IStudentRealtimeService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RealtimeService? realtime;

    public bool IsConnected => realtime?.IsConnected == true;
    public event EventHandler<string>? EventReceived;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync(ct);
            if (!session.HasSession || string.IsNullOrWhiteSpace(session.AccessToken)) return;
            realtime = new RealtimeService(api.BaseAddress.ToString());
            realtime.EventReceived += Forward;
            await realtime.ConnectAsync(session.AccessToken, ct);
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await StopCoreAsync(ct); }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        if (realtime is null) return;
        realtime.EventReceived -= Forward;
        await realtime.DisconnectAsync(ct);
        await realtime.DisposeAsync();
        realtime = null;
    }

    private void Forward(object? sender, string eventName) => EventReceived?.Invoke(this, eventName);

    public void Dispose()
    {
        StopAsync().SafeFireAndForget("StudentRealtime.Dispose");
        gate.Dispose();
    }
}
