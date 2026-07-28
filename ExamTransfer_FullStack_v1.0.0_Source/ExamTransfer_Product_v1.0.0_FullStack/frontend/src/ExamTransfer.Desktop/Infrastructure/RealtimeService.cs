using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class RealtimeService(string baseUrl) : IRealtimeService, IAsyncDisposable
{
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

        hub = new HubConnectionBuilder()
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

        hub.On<RealtimeEnvelope<TimeExtendedEvent>>(
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
            hub.On<JsonElement>(eventName!, envelope =>
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

        hub.Reconnecting += _ =>
        {
            EventReceived?.Invoke(this, "Reconnecting");
            return Task.CompletedTask;
        };
        hub.Reconnected += _ =>
        {
            EventReceived?.Invoke(this, "Reconnected");
            return Task.CompletedTask;
        };
        hub.Closed += _ =>
        {
            EventReceived?.Invoke(this, "Disconnected");
            return Task.CompletedTask;
        };

        await hub.StartAsync(ct);
        EventReceived?.Invoke(this, "Connected");
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
