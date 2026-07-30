using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

var options = Arguments.Parse(args);
await using var connection = new HubConnectionBuilder()
    .WithUrl(options.BaseUrl.TrimEnd('/') + ContractInfo.HubPath, http =>
    {
        http.AccessTokenProvider = () => Task.FromResult<string?>(options.ParticipantToken);
    })
    .Build();

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
try
{
    await connection.StartAsync(timeout.Token);
    var capabilities = new ControlCapabilitiesDto(true, true, true, true, true);
    await connection.InvokeAsync(
        "ClientReady",
        new ClientReadyRequest("onlylan-test-client", Environment.OSVersion.VersionString, capabilities),
        timeout.Token);
    await connection.InvokeAsync(
        "PolicyApplyAck",
        new PolicyApplyAckRequest(
            options.PolicyVersion,
            PolicyApplyStatus.Applied,
            [],
            null,
            capabilities),
        timeout.Token);
    Console.WriteLine($"ONLYLAN_POLICY_ACK_OK policyVersion={options.PolicyVersion}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ONLYLAN_POLICY_ACK_FAILED type={ex.GetType().Name} message={ex.Message}");
    return 1;
}
finally
{
    if (connection.State != HubConnectionState.Disconnected)
    {
        try
        {
            await connection.StopAsync(CancellationToken.None);
        }
        catch
        {
            // The exit code above already captures the actionable failure.
        }
    }
}

internal sealed record Arguments(
    string BaseUrl,
    string ParticipantToken,
    int PolicyVersion,
    int TimeoutSeconds)
{
    public static Arguments Parse(string[] args)
    {
        string Required(string name)
        {
            var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException($"Missing required argument: {name}");
            return args[index + 1];
        }

        var baseUrl = Required("--base-url");
        var token = Required("--participant-token");
        if (!int.TryParse(Required("--policy-version"), out var version) || version <= 0)
            throw new ArgumentException("--policy-version must be a positive integer.");

        var timeout = 20;
        var timeoutIndex = Array.FindIndex(args, value => value.Equals("--timeout-seconds", StringComparison.OrdinalIgnoreCase));
        if (timeoutIndex >= 0
            && (timeoutIndex + 1 >= args.Length
                || !int.TryParse(args[timeoutIndex + 1], out timeout)
                || timeout is < 1 or > 120))
            throw new ArgumentException("--timeout-seconds must be between 1 and 120.");

        return new(baseUrl, token, version, timeout);
    }
}
