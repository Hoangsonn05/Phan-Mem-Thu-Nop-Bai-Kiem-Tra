namespace ExamTransfer.Desktop.Models;

public sealed record NavigationItem(
    string Key,
    string Title,
    string Group,
    string Description,
    string Glyph,
    string? Badge = null,
    bool IsAvailable = true,
    string? UnavailableMessage = null);

public enum AppMode
{
    None,
    Teacher,
    Student
}
