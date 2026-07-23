using System.Text;
using System.Text.Json;
using ExamTransfer.Agent;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class DeviceCommandGoldenFixtureTests
{
    [Fact]
    public async Task Dotnet_signature_matches_cross_language_golden_fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "device-command-signature.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        var command = new DeviceCommandDto(
            root.GetProperty("commandId").GetGuid(),
            root.GetProperty("sessionId").GetGuid(),
            root.GetProperty("deviceId").GetString()!,
            Enum.Parse<DeviceCommandType>(root.GetProperty("commandType").GetString()!),
            root.GetProperty("payload").GetRawText(),
            root.GetProperty("createdAtUtc").GetDateTimeOffset(),
            root.GetProperty("expiresAtUtc").GetDateTimeOffset(),
            root.GetProperty("issuedBy").GetGuid(),
            root.GetProperty("signature").GetString()!);
        var verifier = new HmacDeviceCommandSignatureVerifier(
            Encoding.UTF8.GetBytes(root.GetProperty("secretUtf8").GetString()!));

        Assert.Equal(root.GetProperty("signature").GetString(), verifier.Sign(command));
        Assert.True(verifier.IsValid(command));
    }
}
