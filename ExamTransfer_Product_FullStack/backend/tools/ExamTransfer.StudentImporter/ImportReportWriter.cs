using System.Text;

namespace ExamTransfer.StudentImporter;

internal static class ImportReportWriter
{
    public static string Write(
        string? requestedPath,
        IReadOnlyCollection<ProvisioningResult> results)
    {
        var path = requestedPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Directory.GetCurrentDirectory(),
                "artifacts",
                $"student-import-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        }

        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var builder = new StringBuilder();
        builder.AppendLine(
            "SourceRow,StudentCode,DisplayName,DateOfBirth,TechnicalEmail,Action,Status,UserId,Verification,Message");

        foreach (var row in results)
        {
            builder.AppendJoin(',',
                Csv(row.SourceRow.ToString()),
                Csv(row.StudentCode),
                Csv(row.DisplayName),
                Csv(row.DateOfBirth),
                Csv(row.TechnicalEmail),
                Csv(row.Action),
                Csv(row.Status),
                Csv(row.UserId ?? string.Empty),
                Csv(row.Verification),
                Csv(row.Message));
            builder.AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
