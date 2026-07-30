using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class RealtimeService(string baseUrl) : IRealtimeService, IAsyncDisposable
{
    private readonly RealtimeSessionSubscriptions subscriptions = new();
    private HubConnection? hub;

    public bool IsConnected => hub?.State == HubConnectionState.Connected;

    public event EventHandler<string>? EventReceived;
    public event EventHandler<StudentRealtimeNotification>? NotificationReceived;

    public async Task ConnectAsync(string? token = null, CancellationToken ct = default)
    {
        if (IsConnected)
        {
            return;
        }

        if (hub is not null)
        {
            await hub.DisposeAsync();
        }

        var connection = new HubConnectionBuilder()
            .WithUrl(baseUrl.TrimEnd('/') + ContractInfo.HubPath, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(token);
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)
            })
            .Build();
        hub = connection;

        connection.On<RealtimeEnvelope<TimeExtendedEvent>>(
            RealtimeEvents.TimeExtended,
            envelope =>
            {
                var payload = envelope.Payload with
                {
                    ServerNowUtc = envelope.Payload.ServerNowUtc ?? envelope.OccurredAtUtc,
                    Revision = envelope.Payload.Revision ?? envelope.Sequence
                };
                NotificationReceived?.Invoke(
                    this,
                    new(
                        envelope.SessionId,
                        RealtimeEvents.TimeExtended,
                        envelope.Sequence,
                        payload));
            });

        foreach (var eventName in typeof(RealtimeEvents)
                     .GetFields()
                     .Select(field => field.GetValue(null)?.ToString())
                     .Where(value => !string.IsNullOrWhiteSpace(value)
                         && value != RealtimeEvents.TimeExtended))
        {
            connection.On<JsonElement>(eventName!, envelope =>
            {
                var sessionId = envelope.TryGetProperty("sessionId", out var sessionElement)
                    && sessionElement.TryGetGuid(out var parsedSessionId)
                    ? parsedSessionId
                    : Guid.Empty;
                var revision = envelope.TryGetProperty("sequence", out var sequenceElement)
                    && sequenceElement.TryGetInt64(out var parsedRevision)
                    ? parsedRevision
                    : 0;
                Guid? participantId = null;
                if (envelope.TryGetProperty("payload", out var payload)
                    && payload.TryGetProperty("participantId", out var participantElement)
                    && participantElement.TryGetGuid(out var parsedParticipantId))
                    participantId = parsedParticipantId;
                NotificationReceived?.Invoke(
                    this,
                    new(
                        sessionId,
                        eventName!,
                        revision,
                        null,
                        participantId));
                EventReceived?.Invoke(this, eventName!);
            });
        }

        connection.Reconnecting += _ =>
        {
            EventReceived?.Invoke(this, "Reconnecting");
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            try
            {
                await subscriptions.RestoreAsync(
                    (sessionId, cancellationToken) => connection.InvokeAsync(
                        "SubscribeSession",
                        sessionId,
                        cancellationToken),
                    CancellationToken.None);
                EventReceived?.Invoke(this, "Reconnected");
            }
            catch (Exception ex)
            {
                FrontendLogger.Log(ex, "RealtimeService.Resubscribe");
                EventReceived?.Invoke(this, "ResubscribeFailed");
            }
        };
        connection.Closed += _ =>
        {
            EventReceived?.Invoke(this, "Disconnected");
            return Task.CompletedTask;
        };

        await connection.StartAsync(ct);
        await subscriptions.RestoreAsync(
            (sessionId, cancellationToken) => connection.InvokeAsync(
                "SubscribeSession",
                sessionId,
                cancellationToken),
            ct);
        EventReceived?.Invoke(this, "Connected");
    }

    public async Task SubscribeSessionAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        var connection = hub;
        await subscriptions.SubscribeAsync(
            sessionId,
            connection?.State == HubConnectionState.Connected,
            (id, cancellationToken) => connection!.InvokeAsync(
                "SubscribeSession",
                id,
                cancellationToken),
            ct);
    }

    public async Task UnsubscribeSessionAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var connection = hub;
        await subscriptions.UnsubscribeAsync(
            sessionId,
            connection?.State == HubConnectionState.Connected,
            (id, cancellationToken) => connection!.InvokeAsync(
                "UnsubscribeSession",
                id,
                cancellationToken),
            ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (hub is null)
        {
            return;
        }

        await hub.StopAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (hub is not null)
        {
            await hub.DisposeAsync();
            hub = null;
        }
    }
}

internal sealed class RealtimeSessionSubscriptions
{
    private readonly object gate = new();
    private readonly HashSet<Guid> sessionIds = [];

    public bool Add(Guid sessionId)
    {
        lock (gate)
            return sessionIds.Add(sessionId);
    }

    public bool Remove(Guid sessionId)
    {
        lock (gate)
            return sessionIds.Remove(sessionId);
    }

    public async Task SubscribeAsync(
        Guid sessionId,
        bool isConnected,
        Func<Guid, CancellationToken, Task> subscribe,
        CancellationToken cancellationToken)
    {
        Add(sessionId);
        if (isConnected)
            await subscribe(sessionId, cancellationToken);
    }

    public async Task UnsubscribeAsync(
        Guid sessionId,
        bool isConnected,
        Func<Guid, CancellationToken, Task> unsubscribe,
        CancellationToken cancellationToken)
    {
        Remove(sessionId);
        if (isConnected)
            await unsubscribe(sessionId, cancellationToken);
    }

    public async Task RestoreAsync(
        Func<Guid, CancellationToken, Task> subscribe,
        CancellationToken cancellationToken)
    {
        Guid[] snapshot;
        lock (gate)
            snapshot = [.. sessionIds];

        foreach (var sessionId in snapshot)
            await subscribe(sessionId, cancellationToken);
    }
}
