using System.Globalization;

namespace ExamTransfer.StudentImporter;

internal sealed record StudentImportRow(
    int SourceRow,
    string StudentCode,
    string DisplayName,
    DateOnly DateOfBirth,
    string TechnicalEmail);

internal sealed record AuthUserSnapshot(string Id, string Email);

internal sealed record ProfileSnapshot(
    string Id,
    string StudentCode,
    string? DisplayName,
    DateOnly? DateOfBirth,
    bool MustChangePassword);

internal sealed record ProvisioningResult(
    int SourceRow,
    string StudentCode,
    string DisplayName,
    string DateOfBirth,
    string TechnicalEmail,
    string Action,
    string Status,
    string? UserId,
    string Verification,
    string Message)
{
    public static ProvisioningResult Failed(StudentImportRow row, string action, string message) =>
        new(
            row.SourceRow,
            row.StudentCode,
            row.DisplayName,
            row.DateOfBirth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            row.TechnicalEmail,
            action,
            "Failed",
            null,
            "NotRun",
            message);
}

internal sealed record RuntimeCloudSettings(
    string? SupabaseUrl,
    string? PublishableKey,
    string? OrganizationId,
    string? StudentEmailDomain);
