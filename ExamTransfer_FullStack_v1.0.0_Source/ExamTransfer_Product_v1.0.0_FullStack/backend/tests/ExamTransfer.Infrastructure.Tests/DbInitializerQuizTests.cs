using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class DbInitializerQuizTests
{
    [Fact]
    public async Task InitializeAsync_CreatesQuizSchemaAndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExamTransfer.DbInitTests", Guid.NewGuid().ToString("N"));
        var paths = new Paths(root);
        try
        {
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={paths.DatabasePath}").Options))
            {
                await DbInitializer.InitializeAsync(db, paths);
                await DbInitializer.InitializeAsync(db, paths);

                Assert.Equal(4, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name LIKE 'quiz_%'").SingleAsync());
                Assert.Equal("\"6\"", (await db.AppSettingsSet.SingleAsync(x => x.Key == "schema.version")).ValueJson);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Paths(string root) : IStoragePaths
    {
        public string RootPath { get; } = root;
        public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) => Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) => Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) => Path.Combine(SessionRoot(sessionId), studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) => Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() { Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!); Directory.CreateDirectory(BackupRoot); Directory.CreateDirectory(ExportRoot); Directory.CreateDirectory(TemporaryRoot); }
    }
}
