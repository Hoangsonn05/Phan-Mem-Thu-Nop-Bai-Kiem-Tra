using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Agent;

public interface IExamControlAgent
{
    string AgentVersion { get; }
    ControlCapabilitiesDto GetCapabilities();
    Task<AgentApplyResult> ApplyAsync(ControlPolicyDto policy, CancellationToken cancellationToken);
    Task RemoveAsync(Guid sessionId, CancellationToken cancellationToken);
}

public sealed record AgentApplyResult(PolicyApplyStatus Status, IReadOnlyList<string> UnsupportedRules, string? Error);

/// <summary>
/// Baseline adapter. It deliberately does not alter Windows. The later Student Agent project can replace this
/// implementation while preserving the exact backend/frontend contracts.
/// </summary>
public sealed class CapabilityOnlyAgent : IExamControlAgent
{
    public string AgentVersion => "1.0.0-baseline";
    public ControlCapabilitiesDto GetCapabilities() => new(false, true, false, false, false);

    public Task<AgentApplyResult> ApplyAsync(ControlPolicyDto policy, CancellationToken cancellationToken)
    {
        var unsupported = new List<string>();
        if (policy.Fullscreen) unsupported.Add("fullscreen");
        if (!string.Equals(policy.ClipboardRule, "None", StringComparison.OrdinalIgnoreCase)) unsupported.Add("clipboardControl");
        if (policy.AllowedProcesses.Count > 0 || policy.BlockedProcesses.Count > 0) unsupported.Add("processControl");
        if (!string.Equals(policy.NetworkRule, "None", StringComparison.OrdinalIgnoreCase)) unsupported.Add("networkControl");
        return Task.FromResult(new AgentApplyResult(unsupported.Count == 0 ? PolicyApplyStatus.Applied : PolicyApplyStatus.Unsupported, unsupported, unsupported.Count == 0 ? null : "Baseline agent chỉ báo capability; chưa can thiệp hệ điều hành."));
    }

    public Task RemoveAsync(Guid sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
}
