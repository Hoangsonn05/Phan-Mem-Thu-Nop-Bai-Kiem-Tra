using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

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

    public bool IsConnected => socket?.State == WebSocketState.Open;
    public event EventHandler<string>? EventReceived;
    public event EventHandler<StudentRealtimeNotification>? NotificationReceived;

    public Task StartAsync(
        Guid session,
        string device,
        string accessToken,
        Func<CancellationToken, Task> snapshotRefresh,
        CancellationToken cancellationToken)
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
        var connectedBefore = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                socket?.Dispose();
                socket = new ClientWebSocket();
                var httpUrl = new Uri(projectUrl ?? throw new InvalidOperationException("Supabase URL is missing."));
                var scheme = httpUrl.Scheme == "https" ? "wss" : "ws";
                var endpoint = new Uri(
                    $"{scheme}://{httpUrl.Authority}/realtime/v1/websocket?apikey={Uri.EscapeDataString(publishableKey ?? string.Empty)}&vsn=1.0.0");
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
                if (connectedBefore)
                    EventReceived?.Invoke(this, "Reconnected");
                connectedBefore = true;
                await ReceiveUntilDisconnectedAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                EventReceived?.Invoke(this, "Reconnecting");
                var delay = RetrySeconds[Math.Min(retry++, RetrySeconds.Length - 1)];
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
            }
        }
    }

    private async Task ReceiveUntilDisconnectedAsync(CancellationToken cancellationToken)
    {
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatLoopAsync(receiveCts.Token);
        try
        {
            var buffer = new byte[32 * 1024];
            while (socket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                if (TryParseTimeExtended(json, sessionId, out var notification, out var eventName))
                    NotificationReceived?.Invoke(this, notification!);
                else if (eventName is not null)
                    EventReceived?.Invoke(this, eventName);
            }
        }
        finally
        {
            receiveCts.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) when (receiveCts.IsCancellationRequested) { }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await SendFrameAsync("phoenix", "heartbeat", new { }, cancellationToken);
    }

    public static bool TryParseTimeExtended(
        string json,
        Guid expectedSessionId,
        out StudentRealtimeNotification? notification,
        out string? eventName)
    {
        notification = null;
        eventName = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? topic;
            string? outerEvent;
            JsonElement outerPayload;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 5)
            {
                topic = root[2].GetString();
                outerEvent = root[3].GetString();
                outerPayload = root[4];
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("topic", out var topicElement)
                     && root.TryGetProperty("event", out var eventElement)
                     && root.TryGetProperty("payload", out outerPayload))
            {
                topic = topicElement.GetString();
                outerEvent = eventElement.GetString();
            }
            else
            {
                return false;
            }

            if (!string.Equals(outerEvent, "broadcast", StringComparison.Ordinal)
                || !string.Equals(
                    topic,
                    $"realtime:exam-session:{expectedSessionId}",
                    StringComparison.Ordinal))
                return false;
            if (!outerPayload.TryGetProperty("event", out var innerEventElement))
                return false;

            eventName = innerEventElement.GetString();
            if (!string.Equals(
                    eventName,
                    RealtimeEvents.TimeExtended,
                    StringComparison.Ordinal))
            {
                if (string.Equals(
                        eventName,
                        RealtimeEvents.QuizGradeReturned,
                        StringComparison.Ordinal)
                    && outerPayload.TryGetProperty("payload", out var gradePayload)
                    && TryGuid(gradePayload, "attemptId", out var returnedAttemptId))
                    eventName = $"{RealtimeEvents.QuizGradeReturned}:{returnedAttemptId:N}";
                return false;
            }

            if (!outerPayload.TryGetProperty("payload", out var payload)
                || !TryGuid(payload, "participantId", out var participantId)
                || !TryDate(payload, "serverNowUtc", out var serverNowUtc)
                || !TryInt64(payload, "revision", out var revision)
                || !(TryDate(payload, "effectiveDeadlineUtc", out var deadline)
                     || TryDate(payload, "effectiveDeadline", out deadline)))
                return false;

            _ = TryGuid(payload, "attemptId", out var attemptId);
            _ = TryGuid(payload, "requestId", out var requestId);
            var minutes = payload.TryGetProperty("minutes", out var minutesElement)
                && minutesElement.TryGetInt32(out var parsedMinutes)
                ? parsedMinutes
                : 0;
            var timeExtended = new TimeExtendedEvent(
                participantId,
                minutes,
                deadline,
                attemptId == Guid.Empty ? null : attemptId,
                serverNowUtc,
                revision,
                requestId == Guid.Empty ? null : requestId);
            notification = new(
                expectedSessionId,
                RealtimeEvents.TimeExtended,
                revision,
                timeExtended);
            eventName = null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGuid(JsonElement value, string name, out Guid parsed)
    {
        parsed = Guid.Empty;
        return value.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.TryGetGuid(out parsed);
    }

    private static bool TryDate(JsonElement value, string name, out DateTimeOffset parsed)
    {
        parsed = default;
        return value.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.TryGetDateTimeOffset(out parsed);
    }

    private static bool TryInt64(JsonElement value, string name, out long parsed)
    {
        parsed = default;
        return value.TryGetProperty(name, out var element)
            && element.TryGetInt64(out parsed);
    }

    private Task SendAsync(
        string topic,
        string eventName,
        object payload,
        CancellationToken cancellationToken) =>
        SendFrameAsync("realtime:" + topic, eventName, payload, cancellationToken);

    private async Task SendFrameAsync(
        string topic,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        if (socket?.State != WebSocketState.Open)
            throw new InvalidOperationException("Supabase Realtime is not connected.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            topic,
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
