using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Agent;

public interface IDeviceCommandSignatureVerifier
{
    bool IsValid(DeviceCommandDto command);
}

public sealed class HmacDeviceCommandSignatureVerifier(byte[] key) : IDeviceCommandSignatureVerifier
{
    private readonly byte[] key = key.Length >= 32
        ? key.ToArray()
        : throw new ArgumentException("Khóa ký lệnh thiết bị phải có ít nhất 32 byte.", nameof(key));

    public bool IsValid(DeviceCommandDto command)
    {
        if (string.IsNullOrWhiteSpace(command.Signature)) return false;
        byte[] supplied;
        try { supplied = Convert.FromHexString(command.Signature); }
        catch (FormatException) { return false; }

        byte[] expected;
        try { expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonicalize(command))); }
        catch (JsonException) { return false; }
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    public string Sign(DeviceCommandDto command) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonicalize(command)))).ToLowerInvariant();

    private static string Canonicalize(DeviceCommandDto command) => string.Join('\n',
        command.CommandId.ToString("D"),
        command.SessionId.ToString("D"),
        command.DeviceId.Normalize(NormalizationForm.FormC),
        command.CommandType.ToString().Normalize(NormalizationForm.FormC),
        CanonicalizeJson(command.PayloadJson),
        command.CreatedAtUtc.ToUniversalTime().ToString("O"),
        command.ExpiresAtUtc.ToUniversalTime().ToString("O"),
        command.IssuedBy.ToString("D"));

    private static string CanonicalizeJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .OrderBy(x => x.Name.Normalize(NormalizationForm.FormC), StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name.Normalize(NormalizationForm.FormC));
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString()?.Normalize(NormalizationForm.FormC));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

public sealed record PolicyLease(Guid SessionId, int PolicyVersion, DateTimeOffset ExpiresAtUtc);

public interface IProcessedCommandStore
{
    Task<bool> TryBeginAsync(Guid commandId, CancellationToken cancellationToken);
}

public sealed class FileProcessedCommandStore(string path) : IProcessedCommandStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<bool> TryBeginAsync(Guid commandId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            HashSet<Guid> processed;
            if (File.Exists(path))
            {
                await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                processed = await JsonSerializer.DeserializeAsync<HashSet<Guid>>(read, JsonOptions, cancellationToken) ?? [];
            }
            else processed = [];
            if (!processed.Add(commandId)) return false;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = path + ".tmp";
            await using (var write = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(write, processed, JsonOptions, cancellationToken);
                await write.FlushAsync(cancellationToken);
                write.Flush(true);
            }
            File.Move(temporaryPath, path, true);
            return true;
        }
        finally { gate.Release(); }
    }
}

public interface IPolicyLeaseStore
{
    Task<PolicyLease?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(PolicyLease lease, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}

public sealed class FilePolicyLeaseStore(string path) : IPolicyLeaseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PolicyLease?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<PolicyLease>(stream, JsonOptions, cancellationToken);
    }

    public async Task SaveAsync(PolicyLease lease, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, lease, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(true);
        }
        File.Move(temporaryPath, path, true);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}

public sealed class PublicDeviceCommandProcessor(
    string deviceId,
    Guid sessionId,
    IDeviceCommandSignatureVerifier signatureVerifier,
    IExamControlAgent agent,
    IPolicyLeaseStore leaseStore,
    IProcessedCommandStore processedCommandStore)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<DeviceCommandResultDto> ProcessAsync(DeviceCommandDto command, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(command.DeviceId, deviceId, StringComparison.Ordinal) || command.SessionId != sessionId)
                return Result(command, DeviceCommandStatus.Ignored, nowUtc, null, "DEVICE_COMMAND_TARGET_MISMATCH", "Lệnh không dành cho thiết bị hoặc phiên này.");
            if (command.ExpiresAtUtc <= nowUtc)
                return Result(command, DeviceCommandStatus.Expired, nowUtc, null, ErrorCodes.DeviceCommandExpired, "Lệnh đã hết hạn.");
            if (!signatureVerifier.IsValid(command))
                return Result(command, DeviceCommandStatus.Failed, nowUtc, nowUtc, "DEVICE_COMMAND_SIGNATURE_INVALID", "Chữ ký lệnh thiết bị không hợp lệ.");
            if (!await processedCommandStore.TryBeginAsync(command.CommandId, cancellationToken))
                return Result(command, DeviceCommandStatus.Ignored, nowUtc, null, "DEVICE_COMMAND_DUPLICATE", "Lệnh đã được xử lý trước đó.");

            try
            {
                switch (command.CommandType)
                {
                    case DeviceCommandType.ApplyPolicy:
                    case DeviceCommandType.UpdatePolicy:
                    {
                        var policy = JsonSerializer.Deserialize<ControlPolicyDto>(command.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                            ?? throw new InvalidDataException("Payload policy trống.");
                        if (policy.SessionId != sessionId)
                            return Result(command, DeviceCommandStatus.Failed, nowUtc, nowUtc, "POLICY_SESSION_MISMATCH", "Policy không thuộc phiên hiện tại.");
                        var applied = await agent.ApplyAsync(policy, cancellationToken);
                        if (applied.Status != PolicyApplyStatus.Applied)
                            return Result(command, DeviceCommandStatus.Failed, nowUtc, nowUtc, "POLICY_APPLY_FAILED", applied.Error ?? string.Join(", ", applied.UnsupportedRules));
                        var ttlExpiry = nowUtc.AddMinutes(Math.Max(1, policy.TtlMinutes));
                        await leaseStore.SaveAsync(new PolicyLease(sessionId, policy.Version, ttlExpiry < command.ExpiresAtUtc ? ttlExpiry : command.ExpiresAtUtc), cancellationToken);
                        break;
                    }
                    case DeviceCommandType.ClearPolicy:
                    case DeviceCommandType.UnlockExamApplication:
                    case DeviceCommandType.EndDeviceSession:
                        await agent.RemoveAsync(sessionId, cancellationToken);
                        await leaseStore.ClearAsync(cancellationToken);
                        break;
                    default:
                        return Result(command, DeviceCommandStatus.Ignored, nowUtc, null, "DEVICE_COMMAND_UNSUPPORTED", "Agent hiện tại chưa hỗ trợ loại lệnh này.");
                }
                return Result(command, DeviceCommandStatus.Executed, nowUtc, nowUtc, null, null);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
            {
                return Result(command, DeviceCommandStatus.Failed, nowUtc, nowUtc, "DEVICE_COMMAND_PAYLOAD_INVALID", ex.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RemoveExpiredPolicyAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var lease = await leaseStore.LoadAsync(cancellationToken);
            if (lease is null || lease.ExpiresAtUtc > nowUtc) return false;
            await agent.RemoveAsync(lease.SessionId, cancellationToken);
            await leaseStore.ClearAsync(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static DeviceCommandResultDto Result(DeviceCommandDto command, DeviceCommandStatus status, DateTimeOffset receivedAtUtc, DateTimeOffset? executedAtUtc, string? code, string? message) =>
        new(command.CommandId, command.DeviceId, status, receivedAtUtc, executedAtUtc, code, message);
}
