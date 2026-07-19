using System.Security.Claims;
using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Hubs;

[Authorize(AuthenticationSchemes = ExamTransferAuthSchemes.Account + "," + ExamTransferAuthSchemes.ExamParticipant)]
public sealed class ExamHub(ISessionService sessions, IControlService control, AppDbContext db) : Hub
{
    public static string SessionGroup(Guid id) => $"session:{id:N}";
    public static string ParticipantGroup(Guid sessionId, Guid participantId) => $"session:{sessionId:N}:participant:{participantId:N}";

    public override async Task OnConnectedAsync()
    {
        if (TryIds(out var sessionId, out var participantId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
            await Groups.AddToGroupAsync(Context.ConnectionId, ParticipantGroup(sessionId, participantId));
        }
        await base.OnConnectedAsync();
    }

    public async Task Heartbeat(HeartbeatRequest request)
    {
        EnsureIds(out var sid, out var pid, out var deviceId);
        await sessions.HeartbeatAsync(sid, pid, deviceId, request, Context.ConnectionAborted);
    }

    public async Task ClientReady(ClientReadyRequest request)
    {
        EnsureIds(out var sid, out var pid, out _);
        var participant = await db.SessionParticipantsSet.FirstOrDefaultAsync(x => x.Id == pid && x.SessionId == sid, Context.ConnectionAborted) ?? throw new HubException(ErrorCodes.NotFound);
        participant.CapabilityJson = System.Text.Json.JsonSerializer.Serialize(request.Capabilities); await db.SaveChangesAsync(Context.ConnectionAborted);
    }

    public async Task DownloadProgress(long bytes, long totalBytes, DownloadStatus status)
    {
        EnsureIds(out var sid, out var pid, out _);
        var p = await db.SessionParticipantsSet.Include(x => x.Session).FirstOrDefaultAsync(x => x.Id == pid && x.SessionId == sid, Context.ConnectionAborted) ?? throw new HubException(ErrorCodes.NotFound);
        p.DownloadStatus = status; p.Session.Sequence++; await db.SaveChangesAsync(Context.ConnectionAborted);
        var percent = totalBytes <= 0 ? 0 : Math.Clamp(bytes * 100d / totalBytes, 0, 100);
        await Clients.Group(SessionGroup(sid)).SendAsync(RealtimeEvents.DownloadProgressChanged, new RealtimeEnvelope<DownloadProgressEvent>(Guid.NewGuid(), sid, p.Session.Sequence, DateTimeOffset.UtcNow, RealtimeEvents.DownloadProgressChanged, new DownloadProgressEvent(pid, percent, bytes, status)), Context.ConnectionAborted);
    }

    public Task ViolationReport(ViolationReportRequest request)
    {
        EnsureIds(out var sid, out var pid, out _); return control.ReportViolationAsync(sid, pid, request, Context.ConnectionAborted);
    }

    public Task PolicyApplyAck(PolicyApplyAckRequest request)
    {
        EnsureIds(out var sid, out var pid, out _); return control.PolicyAckAsync(sid, pid, request, Context.ConnectionAborted);
    }

    private bool TryIds(out Guid sessionId, out Guid participantId)
    {
        var sessionValid = Guid.TryParse(
            Context.User?.FindFirstValue("session_id"),
            out sessionId);

        var participantValid = Guid.TryParse(
            Context.User?.FindFirstValue("participant_id"),
            out participantId);

        return sessionValid && participantValid;
    }
    private void EnsureIds(out Guid sessionId, out Guid participantId, out string deviceId)
    {
        if (!TryIds(out sessionId, out participantId)) throw new HubException(ErrorCodes.Unauthorized);
        deviceId = Context.User?.FindFirstValue("device_id") ?? throw new HubException(ErrorCodes.Unauthorized);
    }
}

