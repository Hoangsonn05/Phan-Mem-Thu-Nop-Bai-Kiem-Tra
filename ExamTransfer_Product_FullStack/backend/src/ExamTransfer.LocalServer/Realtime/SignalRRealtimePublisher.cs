using ExamTransfer.Application;
using ExamTransfer.LocalServer.Hubs;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ExamTransfer.LocalServer.Realtime;

public sealed class SignalRRealtimePublisher(IHubContext<ExamHub> hub) : IRealtimePublisher
{
    public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default)
    {
        var envelope = new RealtimeEnvelope<T>(Guid.NewGuid(), sessionId, sequence, DateTimeOffset.UtcNow, eventName, payload);
        return hub.Clients.Group(ExamHub.SessionGroup(sessionId)).SendAsync(eventName, envelope, cancellationToken);
    }

    public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default)
    {
        var envelope = new RealtimeEnvelope<T>(Guid.NewGuid(), sessionId, sequence, DateTimeOffset.UtcNow, eventName, payload);
        return hub.Clients.Group(ExamHub.ParticipantGroup(sessionId, participantId)).SendAsync(eventName, envelope, cancellationToken);
    }
}
