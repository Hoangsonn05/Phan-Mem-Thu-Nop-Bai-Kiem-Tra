using System.Text.Json;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Execution;

public sealed class DeviceStatusReadExecution(AppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DeviceControlStatusDto>> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var rows = await db.DevicePolicyStatusesSet.AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderByDescending(x => x.PolicyVersion)
            .ToListAsync(cancellationToken);
        var latestLocal = rows
            .GroupBy(x => x.ParticipantId)
            .ToDictionary(x => x.Key, x => x.First());
        var connections = await db.PublicDeviceConnectionsSet.AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.ParticipantId)
            .ToListAsync(cancellationToken);
        if (connections.Count == 0)
            return latestLocal.Values.OrderBy(x => x.ParticipantId).Select(ToDto).ToList();

        var deviceIds = connections.Select(x => x.DeviceId).Distinct().ToList();
        var commands = await db.PublicDeviceCommandsSet.AsNoTracking()
            .Where(x => x.SessionId == sessionId && deviceIds.Contains(x.DeviceId))
            .ToListAsync(cancellationToken);
        var latestCommand = commands
            .GroupBy(x => x.DeviceId)
            .ToDictionary(
                x => x.Key,
                x => x.OrderByDescending(command => command.IssuedAtUtc).First(),
                StringComparer.Ordinal);
        var commandIds = latestCommand.Values.Select(x => x.Id).ToList();
        var results = await db.PublicDeviceCommandResultsSet.AsNoTracking()
            .Where(x => commandIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return connections.Select(connection =>
        {
            latestLocal.TryGetValue(connection.ParticipantId, out var local);
            latestCommand.TryGetValue(connection.DeviceId, out var command);
            var result = command is not null && results.TryGetValue(command.Id, out var found)
                ? found
                : null;
            var capabilities = local is null
                ? new ControlCapabilitiesDto(false, false, false, false, false)
                : DeserializeCapabilities(local.CapabilityJson);
            var policyStatus = local?.Status
                ?? (Enum.TryParse<PolicyApplyStatus>(connection.PolicyState, true, out var parsed)
                    ? parsed
                    : PolicyApplyStatus.NotRequested);
            return new DeviceControlStatusDto(
                connection.ParticipantId,
                local?.PolicyVersion ?? 0,
                capabilities,
                policyStatus,
                local?.Error ?? result?.ErrorMessage,
                connection.CloudUpdatedAtUtc ?? connection.UpdatedAtUtc,
                connection.DeviceId,
                connection.ConnectionState,
                connection.HeartbeatAtUtc,
                connection.PolicyState,
                connection.LockState,
                connection.AppVersion,
                connection.AgentVersion,
                result?.Status,
                result?.ErrorMessage);
        }).ToList();
    }

    private static DeviceControlStatusDto ToDto(DevicePolicyStatus status)
    {
        var capabilities = DeserializeCapabilities(status.CapabilityJson);
        return new(
            status.ParticipantId,
            status.PolicyVersion,
            capabilities,
            status.Status,
            status.Error,
            status.UpdatedAtUtc);
    }

    private static ControlCapabilitiesDto DeserializeCapabilities(string json) =>
        JsonSerializer.Deserialize<ControlCapabilitiesDto>(json, JsonOptions)
        ?? new(false, false, false, false, false);
}
