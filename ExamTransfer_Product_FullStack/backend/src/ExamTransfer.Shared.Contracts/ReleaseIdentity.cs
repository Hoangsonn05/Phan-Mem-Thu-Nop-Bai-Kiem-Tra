using System.Reflection;

namespace ExamTransfer.Shared.Contracts;

public static class ReleaseIdentity
{
    private static readonly IReadOnlyDictionary<string, string> Metadata =
        typeof(ReleaseIdentity).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Value))
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value!,
                StringComparer.Ordinal);

    public static string SemanticVersion =>
        Value("ExamTransferSemanticVersion")
        ?? typeof(ReleaseIdentity).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string BuildId =>
        Value("ExamTransferBuildId")
        ?? $"{SemanticVersion}+development";

    public static string GitCommit => Value("ExamTransferGitCommit") ?? "unknown";

    public static bool WorkingTreeDirty =>
        !bool.TryParse(Value("ExamTransferWorkingTreeDirty"), out var dirty) || dirty;

    private static string? Value(string key) =>
        Metadata.TryGetValue(key, out var value) ? value : null;
}
