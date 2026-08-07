using System.Text.RegularExpressions;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class StudentImporterCharacterizationTests
{
    [Fact]
    public void ProvisioningContract_NewResetAndExistingProfilesKeepExpectedPasswordGateFlag()
    {
        var source = ReadRepoFile(
            "backend",
            "tools",
            "ExamTransfer.StudentImporter",
            "Program.cs");
        var compact = Regex.Replace(source, @"\s+", string.Empty);

        Assert.Contains(
            "varmustChangePassword=createdNow||options.ResetExistingPassword||existingProfile?.MustChangePassword==true;",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "client.UpsertProfileAsync(user.Id,student,organizationId,mustChangePassword",
            compact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningContract_ConflictAndDryRunReturnBeforeAnyAccountMutation()
    {
        var source = ReadRepoFile(
            "backend",
            "tools",
            "ExamTransfer.StudentImporter",
            "Program.cs");
        var conflict = source.IndexOf(
            "Auth user và profile có UUID khác nhau",
            StringComparison.Ordinal);
        var dryRun = source.IndexOf("if (options.DryRun)", StringComparison.Ordinal);
        var create = source.IndexOf("client.CreateUserAsync(", StringComparison.Ordinal);
        var update = source.IndexOf("client.UpdateExistingUserAsync(", StringComparison.Ordinal);
        var upsert = source.IndexOf("client.UpsertProfileAsync(", StringComparison.Ordinal);

        Assert.True(conflict >= 0 && conflict < dryRun);
        Assert.True(dryRun >= 0 && dryRun < create);
        Assert.True(create < update && update < upsert);
    }

    [Fact]
    public void SupabaseProfileUpsert_UsesProvisioningDecisionWithoutEmbeddingSecrets()
    {
        var source = ReadRepoFile(
            "backend",
            "tools",
            "ExamTransfer.StudentImporter",
            "SupabaseAdminClient.cs");

        Assert.Contains("must_change_password = mustChangePassword", source, StringComparison.Ordinal);
        Assert.DoesNotContain("temporary_password", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportReport_ContainsOperationalFieldsButNoPasswordOrSecretColumns()
    {
        var source = ReadRepoFile(
            "backend",
            "tools",
            "ExamTransfer.StudentImporter",
            "ImportReportWriter.cs");
        var header = Regex.Match(source, "SourceRow,StudentCode[^\"]+").Value;

        Assert.Contains("Action,Status,UserId,Verification,Message", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellWrapper_MapsSwitchesUsesEnvironmentSecretsAndPropagatesFailure()
    {
        var source = ReadRepoFile(
            "backend",
            "scripts",
            "import-student-accounts.ps1");

        Assert.Contains("if ($DryRun) { $arguments += \"--dry-run\" }", source, StringComparison.Ordinal);
        Assert.Contains("if ($VerifyLogin) { $arguments += \"--verify-login\" }", source, StringComparison.Ordinal);
        Assert.Contains(
            "if ($ResetExistingPassword) { $arguments += \"--reset-existing-password\" }",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$env:EXAMTRANSFER_SUPABASE_SECRET_KEY", source, StringComparison.Ordinal);
        Assert.Contains("if ($LASTEXITCODE -ne 0)", source, StringComparison.Ordinal);
        Assert.Contains("Resolve-Path -LiteralPath $BackendRoot", source, StringComparison.Ordinal);
        Assert.Contains("Resolve-Path -LiteralPath $ExcelPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EXAMTRANSFER_STUDENT_TEMP_PASSWORD", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--password", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, Path.Combine(segments));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file: {Path.Combine(segments)}");
    }
}
