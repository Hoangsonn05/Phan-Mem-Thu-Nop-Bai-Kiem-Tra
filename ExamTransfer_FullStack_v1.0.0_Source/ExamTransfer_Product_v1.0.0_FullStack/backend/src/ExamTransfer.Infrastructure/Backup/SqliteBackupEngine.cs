using ExamTransfer.Application;
using Microsoft.Data.Sqlite;

namespace ExamTransfer.Infrastructure.Backup;

public sealed class SqliteBackupEngine(IStoragePaths paths) : IBackupEngine
{
    public async Task CreateDatabaseSnapshotAsync(string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        await using var source = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly");
        await using var destination = new SqliteConnection($"Data Source={destinationPath}");
        await source.OpenAsync(cancellationToken); await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }
}
