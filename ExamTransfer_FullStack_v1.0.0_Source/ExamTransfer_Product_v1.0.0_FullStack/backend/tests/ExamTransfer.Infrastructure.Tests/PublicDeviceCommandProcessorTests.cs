using System.Text.Json;
using ExamTransfer.Agent;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class PublicDeviceCommandProcessorTests
{
    [Fact]
    public async Task ProcessAsync_RejectsExpiredAndInvalidSignatureCommands()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        var verifier = new HmacDeviceCommandSignatureVerifier(Enumerable.Repeat((byte)7, 32).ToArray());
        var processor = new PublicDeviceCommandProcessor("device-1", sessionId, verifier, new RecordingAgent(), new MemoryLeaseStore(), new MemoryProcessedCommandStore());

        var expired = Command(sessionId, "device-1", now.AddMinutes(-2), now.AddMinutes(-1), "bad");
        var expiredResult = await processor.ProcessAsync(expired, now);
        var invalid = Command(sessionId, "device-1", now, now.AddMinutes(2), "bad");
        var invalidResult = await processor.ProcessAsync(invalid, now);

        Assert.Equal(DeviceCommandStatus.Expired, expiredResult.Status);
        Assert.Equal(ErrorCodes.DeviceCommandExpired, expiredResult.ErrorCode);
        Assert.Equal(DeviceCommandStatus.Failed, invalidResult.Status);
        Assert.Equal("DEVICE_COMMAND_SIGNATURE_INVALID", invalidResult.ErrorCode);
    }

    [Fact]
    public async Task ApplyPolicy_IsIdempotent_AndExpiredLeaseRemovesPolicy()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        var verifier = new HmacDeviceCommandSignatureVerifier(Enumerable.Repeat((byte)9, 32).ToArray());
        var agent = new RecordingAgent();
        var leases = new MemoryLeaseStore();
        var processed = new MemoryProcessedCommandStore();
        var processor = new PublicDeviceCommandProcessor("device-1", sessionId, verifier, agent, leases, processed);
        var policy = new ControlPolicyDto(sessionId, 3, false, "Monitor", "None", [], [], "None", true, 1, "v3");
        var unsigned = Command(sessionId, "device-1", now, now.AddMinutes(5), "", JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var command = unsigned with { Signature = verifier.Sign(unsigned) };

        var first = await processor.ProcessAsync(command, now);
        var duplicate = await processor.ProcessAsync(command, now.AddSeconds(1));
        var removed = await processor.RemoveExpiredPolicyAsync(now.AddMinutes(2));

        Assert.Equal(DeviceCommandStatus.Executed, first.Status);
        Assert.Equal(DeviceCommandStatus.Ignored, duplicate.Status);
        Assert.Equal(1, agent.ApplyCount);
        Assert.True(removed);
        Assert.Equal(1, agent.RemoveCount);
        Assert.Null(await leases.LoadAsync(default));
    }

    [Fact]
    public async Task ProcessedCommandJournal_PreventsReplayAfterProcessorRestart()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        var verifier = new HmacDeviceCommandSignatureVerifier(Enumerable.Repeat((byte)5, 32).ToArray());
        var agent = new RecordingAgent();
        var processed = new MemoryProcessedCommandStore();
        var policy = new ControlPolicyDto(sessionId, 1, false, "Monitor", "None", [], [], "None", true, 5, "v1");
        var unsigned = Command(sessionId, "device-1", now, now.AddMinutes(5), "", JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var command = unsigned with { Signature = verifier.Sign(unsigned) };

        var first = await new PublicDeviceCommandProcessor("device-1", sessionId, verifier, agent, new MemoryLeaseStore(), processed).ProcessAsync(command, now);
        var replay = await new PublicDeviceCommandProcessor("device-1", sessionId, verifier, agent, new MemoryLeaseStore(), processed).ProcessAsync(command, now.AddSeconds(1));

        Assert.Equal(DeviceCommandStatus.Executed, first.Status);
        Assert.Equal(DeviceCommandStatus.Ignored, replay.Status);
        Assert.Equal(1, agent.ApplyCount);
    }

    [Fact]
    public async Task FileProcessedCommandStore_PersistsCommandIdsAcrossInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "examtransfer-command-journal-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "processed.json");
        var commandId = Guid.NewGuid();
        try
        {
            Assert.True(await new FileProcessedCommandStore(path).TryBeginAsync(commandId, default));
            Assert.False(await new FileProcessedCommandStore(path).TryBeginAsync(commandId, default));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static DeviceCommandDto Command(Guid sessionId, string deviceId, DateTimeOffset created, DateTimeOffset expires, string signature, string payload = "{}") =>
        new(Guid.NewGuid(), sessionId, deviceId, DeviceCommandType.ApplyPolicy, payload, created, expires, Guid.NewGuid(), signature);

    private sealed class RecordingAgent : IExamControlAgent
    {
        public int ApplyCount { get; private set; }
        public int RemoveCount { get; private set; }
        public string AgentVersion => "test";
        public ControlCapabilitiesDto GetCapabilities() => new(false, true, false, false, false);
        public Task<AgentApplyResult> ApplyAsync(ControlPolicyDto policy, CancellationToken cancellationToken)
        {
            ApplyCount++;
            return Task.FromResult(new AgentApplyResult(PolicyApplyStatus.Applied, [], null));
        }
        public Task RemoveAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            RemoveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryLeaseStore : IPolicyLeaseStore
    {
        private PolicyLease? lease;
        public Task<PolicyLease?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(lease);
        public Task SaveAsync(PolicyLease value, CancellationToken cancellationToken) { lease = value; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellationToken) { lease = null; return Task.CompletedTask; }
    }

    private sealed class MemoryProcessedCommandStore : IProcessedCommandStore
    {
        private readonly HashSet<Guid> processed = [];
        public Task<bool> TryBeginAsync(Guid commandId, CancellationToken cancellationToken) =>
            Task.FromResult(processed.Add(commandId));
    }
}
