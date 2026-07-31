using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Execution;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class DeviceStatusReadExecutionTests
{
    [Fact]
    public void Source_contract_keeps_read_execution_concrete_and_facade_delegating()
    {
        var execution = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/src/ExamTransfer.Infrastructure/Execution/DeviceStatusReadExecution.cs");
        var controlService = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/src/ExamTransfer.Infrastructure/Services/ControlService.cs");
        var methodStart = controlService.IndexOf(
            "public Task<IReadOnlyList<DeviceControlStatusDto>> GetDeviceStatusAsync",
            StringComparison.Ordinal);
        var methodEnd = controlService.IndexOf(
            "public async Task<PagedResult<ViolationDto>> GetViolationsAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = controlService[methodStart..methodEnd];

        Assert.DoesNotContain("ExamSessionsSet", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionParticipantsSet", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessMode", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("IAuditService", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("IOutboxService", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("IRealtimePublisher", execution, StringComparison.Ordinal);
        Assert.Contains("if (connections.Count == 0)", execution, StringComparison.Ordinal);
        Assert.Contains("_deviceStatusRead.GetAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("DevicePolicyStatusesSet", method, StringComparison.Ordinal);
        Assert.DoesNotContain("PublicDeviceConnectionsSet", method, StringComparison.Ordinal);
        Assert.Equal(
            new[] { typeof(ExamTransfer.Infrastructure.Persistence.AppDbContext) },
            typeof(DeviceStatusReadExecution)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(x => x.ParameterType)
                .ToArray());
    }

    [Fact]
    public async Task Unknown_session_without_status_or_connection_returns_empty_without_writes()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var execution = new DeviceStatusReadExecution(database.Context);
        var before = await CountsAsync(database.Context);

        var result = await execution.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(before, await CountsAsync(database.Context));
        Assert.DoesNotContain(
            database.Context.ChangeTracker.Entries(),
            x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    [Fact]
    public async Task Local_status_without_connection_returns_latest_local_mapping()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        database.Context.DevicePolicyStatusesSet.AddRange(
            new DevicePolicyStatus
            {
                SessionId = sessionId,
                ParticipantId = participantId,
                PolicyVersion = 1,
                CapabilityJson = "{}",
                Status = PolicyApplyStatus.Applying,
                Error = "older"
            },
            new DevicePolicyStatus
            {
                SessionId = sessionId,
                ParticipantId = participantId,
                PolicyVersion = 2,
                CapabilityJson = JsonSerializer.Serialize(
                    new ControlCapabilitiesDto(true, true, false, true, false)),
                Status = PolicyApplyStatus.Applied,
                Error = null
            });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var latest = await database.Context.DevicePolicyStatusesSet
            .AsNoTracking()
            .SingleAsync(x => x.SessionId == sessionId && x.PolicyVersion == 2);

        var result = Assert.Single(
            await new DeviceStatusReadExecution(database.Context)
                .GetAsync(sessionId, CancellationToken.None));

        Assert.Equal(participantId, result.ParticipantId);
        Assert.Equal(2, result.PolicyVersion);
        Assert.Equal(PolicyApplyStatus.Applied, result.Status);
        Assert.True(result.Capabilities.Fullscreen);
        Assert.True(result.Capabilities.FocusMonitoring);
        Assert.True(result.Capabilities.ProcessControl);
        Assert.Equal(latest.UpdatedAtUtc, result.UpdatedAtUtc);
        Assert.Null(result.DeviceId);
        Assert.Equal(ConnectionState.Offline, result.ConnectionState);
        Assert.Null(result.HeartbeatAtUtc);
        Assert.Null(result.LastCommandStatus);
        Assert.Empty(database.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Lan_session_with_connection_preserves_cloud_merge_and_latest_command_result()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.LanOnly);
        var now = DateTimeOffset.UtcNow;
        var cloudUpdatedAt = now.AddMinutes(-1);
        var local = new DevicePolicyStatus
        {
            SessionId = participant.SessionId,
            ParticipantId = participant.Id,
            PolicyVersion = 7,
            CapabilityJson = JsonSerializer.Serialize(
                new ControlCapabilitiesDto(true, false, true, false, true)),
            Status = PolicyApplyStatus.Applied,
            Error = "local-policy-error"
        };
        var connection = new PublicDeviceConnection
        {
            SessionId = participant.SessionId,
            ParticipantId = participant.Id,
            UserId = Guid.NewGuid(),
            DeviceId = "cloud-device",
            ConnectionState = ConnectionState.Online,
            HeartbeatAtUtc = now,
            PolicyState = nameof(PolicyApplyStatus.Failed),
            LockState = "Locked",
            AppVersion = "2.0",
            AgentVersion = "3.0",
            CloudUpdatedAtUtc = cloudUpdatedAt
        };
        var olderCommand = new PublicDeviceCommand
        {
            SessionId = participant.SessionId,
            DeviceId = connection.DeviceId,
            CommandType = DeviceCommandType.LockExamApplication,
            IssuedAtUtc = now.AddMinutes(-2),
            ExpiresAtUtc = now.AddMinutes(3),
            IssuedBy = Guid.NewGuid(),
            Signature = "older"
        };
        var latestCommand = new PublicDeviceCommand
        {
            SessionId = participant.SessionId,
            DeviceId = connection.DeviceId,
            CommandType = DeviceCommandType.UnlockExamApplication,
            IssuedAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(4),
            IssuedBy = Guid.NewGuid(),
            Signature = "latest"
        };
        database.Context.AddRange(
            local,
            connection,
            olderCommand,
            latestCommand,
            new PublicDeviceCommandResult
            {
                Id = olderCommand.Id,
                DeviceId = connection.DeviceId,
                Status = DeviceCommandStatus.Executed,
                ReceivedAtUtc = now.AddMinutes(-2)
            },
            new PublicDeviceCommandResult
            {
                Id = latestCommand.Id,
                DeviceId = connection.DeviceId,
                Status = DeviceCommandStatus.Failed,
                ReceivedAtUtc = now.AddMinutes(-1),
                ErrorMessage = "latest-command-error"
            });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var before = await CountsAsync(database.Context);

        var result = Assert.Single(
            await new DeviceStatusReadExecution(database.Context)
                .GetAsync(participant.SessionId, CancellationToken.None));

        Assert.Equal(participant.Id, result.ParticipantId);
        Assert.Equal(local.PolicyVersion, result.PolicyVersion);
        Assert.Equal(local.Status, result.Status);
        Assert.Equal(local.Error, result.Error);
        Assert.True(result.Capabilities.Fullscreen);
        Assert.True(result.Capabilities.ClipboardControl);
        Assert.True(result.Capabilities.NetworkControl);
        Assert.Equal(connection.DeviceId, result.DeviceId);
        Assert.Equal(connection.ConnectionState, result.ConnectionState);
        Assert.Equal(connection.HeartbeatAtUtc, result.HeartbeatAtUtc);
        Assert.Equal(connection.PolicyState, result.PolicyState);
        Assert.Equal(connection.LockState, result.LockState);
        Assert.Equal(connection.AppVersion, result.AppVersion);
        Assert.Equal(connection.AgentVersion, result.AgentVersion);
        Assert.Equal(cloudUpdatedAt, result.UpdatedAtUtc);
        Assert.Equal(DeviceCommandStatus.Failed, result.LastCommandStatus);
        Assert.Equal("latest-command-error", result.LastCommandError);
        Assert.Equal(before, await CountsAsync(database.Context));
        Assert.Empty(database.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ControlService_status_uses_injected_read_execution()
    {
        await using var facadeDatabase = await PublicCloudTestHarness.CreateDatabaseAsync();
        await using var executionDatabase = await PublicCloudTestHarness.CreateDatabaseAsync();
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        executionDatabase.Context.DevicePolicyStatusesSet.Add(new DevicePolicyStatus
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            PolicyVersion = 4,
            Status = PolicyApplyStatus.Applied
        });
        await executionDatabase.Context.SaveChangesAsync();
        var facade = new ControlService(
            facadeDatabase.Context,
            new AuditService(facadeDatabase.Context, new HttpContextAccessor()),
            new NoOpRealtimePublisher(),
            new OutboxService(facadeDatabase.Context),
            new DeviceStatusReadExecution(executionDatabase.Context));

        var result = Assert.Single(
            await facade.GetDeviceStatusAsync(sessionId, CancellationToken.None));

        Assert.Equal(participantId, result.ParticipantId);
        Assert.Empty(await facadeDatabase.Context.DevicePolicyStatusesSet.ToListAsync());
    }

    private static async Task<(
        int Statuses,
        int Connections,
        int Commands,
        int Results,
        int Outbox,
        int Audits)> CountsAsync(
            ExamTransfer.Infrastructure.Persistence.AppDbContext db) =>
        (
            await db.DevicePolicyStatusesSet.CountAsync(),
            await db.PublicDeviceConnectionsSet.CountAsync(),
            await db.PublicDeviceCommandsSet.CountAsync(),
            await db.PublicDeviceCommandResultsSet.CountAsync(),
            await db.SyncQueueSet.CountAsync(),
            await db.AuditLogsSet.CountAsync()
        );

    private sealed class NoOpRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
