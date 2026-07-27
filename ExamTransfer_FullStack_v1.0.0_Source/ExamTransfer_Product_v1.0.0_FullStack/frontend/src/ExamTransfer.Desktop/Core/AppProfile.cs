namespace ExamTransfer.Desktop.Core;

public static class AppProfile
{
    public const string EnvironmentVariableName = "EXAMTRANSFER_PROFILE";

    public static string? Name { get; } = ResolveName();

    public static bool IsNamed => Name is not null;

    public static string LocalDataRoot { get; } = ResolveLocalDataRoot();

    public static string DisplayName => Name ?? "default";

    public static string PreferenceVariable(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return Name is null
            ? $"EXAMTRANSFER_PREF_{key}"
            : $"EXAMTRANSFER_PREF_{Name.ToUpperInvariant()}_{key}";
    }

    private static string? ResolveName()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.Length > 64 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '_' and not '-'))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariableName} chỉ được chứa chữ cái ASCII, chữ số, dấu gạch ngang hoặc gạch dưới và dài tối đa 64 ký tự.");
        }

        return value.ToLowerInvariant();
    }

    private static string ResolveLocalDataRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExamTransfer");

        return Name is null
            ? root
            : Path.Combine(root, "profiles", Name);
    }
}
