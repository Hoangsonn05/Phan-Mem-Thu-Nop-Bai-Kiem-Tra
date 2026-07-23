using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class SupabaseRealtimeService : IAsyncDisposable
{
    private static readonly int[] RetrySeconds = [1, 2, 5, 10, 30];
    private readonly string? projectUrl = Environment.GetEnvironmentVariable("EXAMTRANSFER_SUPABASE_URL")?.TrimEnd('/');
    private readonly string? publishableKey = Environment.GetEnvironmentVariable("EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY");
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private CancellationTokenSource? stopping;
    private ClientWebSocket? socket;
    private Task? loop;
    private Guid sessionId;
    private string deviceId = string.Empty;
    private string token = string.Empty;
    private Func<CancellationToken, Task>? refreshSnapshot;
    private long reference;

    public Task StartAsync(Guid session, string device, string accessToken,
        Func<CancellationToken, Task> snapshotRefresh, CancellationToken cancellationToken)
    {
        Stop();
        sessionId = session;
        deviceId = device;
        token = accessToken;
        refreshSnapshot = snapshotRefresh;
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loop = RunAsync(stopping.Token);
        return Task.CompletedTask;
    }

    public async Task BroadcastTelemetryAsync(object payload, CancellationToken cancellationToken)
    {
        var topic = $"exam-session:{sessionId}:telemetry:{deviceId}";
        await SendAsync(topic, "broadcast", new
        {
            type = "broadcast",
            @event = "telemetry",
            payload,
            @private = true
        }, cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retry = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                socket?.Dispose();
                socket = new ClientWebSocket();
                var httpUrl = new Uri(projectUrl ?? throw new InvalidOperationException("Supabase URL is missing."));
                var scheme = httpUrl.Scheme == "https" ? "wss" : "ws";
                var endpoint = new Uri($"{scheme}://{httpUrl.Authority}/realtime/v1/websocket?apikey={Uri.EscapeDataString(publishableKey ?? string.Empty)}&vsn=1.0.0");
                await socket.ConnectAsync(endpoint, cancellationToken);
                foreach (var topic in new[]
                {
                    $"exam-session:{sessionId}",
                    $"exam-session:{sessionId}:device:{deviceId}",
                    $"exam-session:{sessionId}:telemetry:{deviceId}"
                })
                {
                    await SendAsync(topic, "phx_join", new
                    {
                        config = new
                        {
                            @private = true,
                            broadcast = new { self = false, ack = true },
                            presence = new { key = deviceId }
                        },
                        access_token = token
                    }, cancellationToken);
                }
                retry = 0;
                if (refreshSnapshot is not null)
                    await refreshSnapshot(cancellationToken);
                await ReceiveUntilDisconnectedAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                var delay = RetrySeconds[Math.Min(retry++, RetrySeconds.Length - 1)];
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
            }
        }
    }

    private async Task ReceiveUntilDisconnectedAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        while (socket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return;
            while (!result.EndOfMessage)
                result = await socket.ReceiveAsync(buffer, cancellationToken);
            // Realtime messages are notifications only. Consumers refresh
            // their authoritative REST/RPC snapshot after reconnect or UI load.
        }
    }

    private async Task SendAsync(string topic, string eventName, object payload, CancellationToken cancellationToken)
    {
        if (socket?.State != WebSocketState.Open)
            throw new InvalidOperationException("Supabase Realtime is not connected.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            topic = "realtime:" + topic,
            @event = eventName,
            payload,
            @ref = Interlocked.Increment(ref reference).ToString()
        });
        await sendGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { sendGate.Release(); }
    }

    public void Stop()
    {
        stopping?.Cancel();
        stopping?.Dispose();
        stopping = null;
        socket?.Abort();
        socket?.Dispose();
        socket = null;
        loop = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
