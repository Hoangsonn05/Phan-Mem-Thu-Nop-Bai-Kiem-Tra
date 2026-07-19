using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class RealtimeService(string baseUrl) : IRealtimeService, IAsyncDisposable
{
    private HubConnection? hub;

    public bool IsConnected => hub?.State == HubConnectionState.Connected;

    public event EventHandler<string>? EventReceived;

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

        foreach (var eventName in typeof(RealtimeEvents)
                     .GetFields()
                     .Select(field => field.GetValue(null)?.ToString())
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            hub.On<object>(eventName!, _ => EventReceived?.Invoke(this, eventName!));
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
