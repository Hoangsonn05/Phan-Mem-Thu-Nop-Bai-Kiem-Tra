using ExamTransfer.Application;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Workers;

public sealed class HeartbeatMonitorWorker(IServiceScopeFactory scopeFactory, IOptions<ExamTransferOptions> options, ILogger<HeartbeatMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(2, options.Value.Session.HeartbeatSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var realtime = scope.ServiceProvider.GetRequiredService<IRealtimePublisher>();
                var cutoff = DateTimeOffset.UtcNow.AddSeconds(-options.Value.Session.DisconnectAfterSeconds);
                await DisconnectStaleParticipantsAsync(db, realtime, cutoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Heartbeat monitor failed"); }
        }
    }

    internal static async Task<int> DisconnectStaleParticipantsAsync(
        AppDbContext db,
        IRealtimePublisher realtime,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var activeCandidates = await db.SessionParticipantsSet
            .Include(x => x.Session)
            .Where(x =>
                x.Status != ParticipantStatus.Rejected
                && x.Status != ParticipantStatus.Disconnected
                && (x.Session.Status == SessionStatus.Waiting
                    || x.Session.Status == SessionStatus.Distributing
                    || x.Session.Status == SessionStatus.InProgress
                    || x.Session.Status == SessionStatus.Paused
                    || x.Session.Status == SessionStatus.Collecting))
            .ToListAsync(cancellationToken);
        var participants = activeCandidates
            .Where(x => x.LastSeenUtc < cutoff)
            .ToList();

        foreach (var participant in participants)
        {
            participant.Status = ParticipantStatus.Disconnected;
            participant.Session.Sequence++;
            await realtime.PublishSessionAsync(
                participant.SessionId,
                RealtimeEvents.ParticipantConnectionChanged,
                participant.Session.Sequence,
                new ParticipantConnectionChangedEvent(
                    participant.Id,
                    ConnectionState.Offline,
                    participant.LastSeenUtc ?? DateTimeOffset.UtcNow),
                cancellationToken);
        }

        if (participants.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
        return participants.Count;
    }
}
