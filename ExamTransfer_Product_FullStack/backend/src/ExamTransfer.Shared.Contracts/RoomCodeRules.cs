namespace ExamTransfer.Shared.Contracts;

public static class RoomCodeRules
{
    public const int MinimumLength = 4;
    public const int MaximumLength = 12;
    public const string GeneratedAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const string ValidationMessage = "Mã phòng phải có từ 4 đến 12 ký tự.";

    public static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    public static bool IsValid(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length is >= MinimumLength and <= MaximumLength;
    }
}
