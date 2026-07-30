using ExamTransfer.Application;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Storage;

public sealed class StoragePaths : IStoragePaths
{
    public StoragePaths(IOptions<ExamTransferOptions> options)
    {
        var configured = options.Value.Storage.RootPath;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData)) programData = AppContext.BaseDirectory;
        configured = configured.Replace("%ProgramData%", programData, StringComparison.OrdinalIgnoreCase)
            .Replace('/', Path.DirectorySeparatorChar);
        RootPath = Path.GetFullPath(configured);
    }

    public string RootPath { get; }
    public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
    public string BackupRoot => Path.Combine(RootPath, "database", "backups");
    public string ExportRoot => Path.Combine(RootPath, "exports");
    public string TemporaryRoot => Path.Combine(RootPath, "temporary");
    public string ExamVersionRoot(Guid examId, int version) => Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
    public string SessionRoot(Guid sessionId) => Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
    public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) => Path.Combine(SessionRoot(sessionId), "submissions", SanitizeSegment(studentCode), submissionId.ToString("N"));
    public string ReceiptRoot(Guid sessionId) => Path.Combine(SessionRoot(sessionId), "receipts");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        Directory.CreateDirectory(BackupRoot);
        Directory.CreateDirectory(ExportRoot);
        Directory.CreateDirectory(TemporaryRoot);
        Directory.CreateDirectory(Path.Combine(RootPath, "logs"));
        Directory.CreateDirectory(Path.Combine(RootPath, "certificates"));
        Directory.CreateDirectory(Path.Combine(RootPath, "updates"));
    }

    public static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(result) ? "unnamed" : result.Length > 80 ? result[..80] : result;
    }
}
