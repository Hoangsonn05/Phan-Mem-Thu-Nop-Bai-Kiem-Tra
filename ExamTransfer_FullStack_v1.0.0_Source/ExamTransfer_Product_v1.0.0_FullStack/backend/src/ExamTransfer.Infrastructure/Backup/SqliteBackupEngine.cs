using ExamTransfer.Application;
using Microsoft.Data.Sqlite;

namespace ExamTransfer.Infrastructure.Backup;

public sealed class SqliteBackupEngine(IStoragePaths paths) : IBackupEngine
{
    public async Task CreateDatabaseSnapshotAsync(string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        // A pooled SQLite connection keeps the snapshot file handle alive after
        // this method returns. The backup service immediately zips and removes
        // the staging directory, so both short-lived connections must bypass
        // pooling.
        await using var source = new SqliteConnection(
            $"Data Source={paths.DatabasePath};Mode=ReadOnly;Pooling=False");
        await using var destination = new SqliteConnection(
            $"Data Source={destinationPath};Pooling=False");
        await source.OpenAsync(cancellationToken); await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }
}
