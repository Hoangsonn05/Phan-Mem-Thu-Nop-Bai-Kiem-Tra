using System.IO.Compression;
using System.Text.Json;

namespace ExamTransfer.Infrastructure.Backup;

public static class RestoreBootstrap
{
    public static void ApplyPendingRestore(string rootPath)
    {
        var markerPath = Path.Combine(rootPath, "restore-pending.json");
        if (!File.Exists(markerPath))
            return;

        using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
        var backupPath = document.RootElement.GetProperty("backupPath").GetString()
            ?? throw new InvalidOperationException("Restore marker thiếu backupPath.");
        var encrypted = document.RootElement.TryGetProperty("encrypted", out var encryptedElement)
            && encryptedElement.GetBoolean();

        if (!File.Exists(backupPath))
            throw new FileNotFoundException("Không tìm thấy backup pending restore.", backupPath);

        var staging = Path.Combine(rootPath, "temporary", "restore-" + Guid.NewGuid().ToString("N"));
        var decryptedZip = Path.Combine(rootPath, "temporary", "restore-decrypted-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(Path.GetDirectoryName(decryptedZip)!);

        try
        {
            var archivePath = backupPath;
            if (encrypted || BackupCrypto.IsEncrypted(backupPath))
            {
                var passphrase = BackupCrypto.GetRequiredPassphrase();
                BackupCrypto.DecryptFileAsync(
                        backupPath,
                        decryptedZip,
                        passphrase,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                archivePath = decryptedZip;
            }

            ZipFile.ExtractToDirectory(archivePath, staging);
            var restoredDb = Path.Combine(staging, "exam-transfer.db");
            if (!File.Exists(restoredDb))
                throw new InvalidDataException("Backup không có database.");

            var databasePath = Path.Combine(rootPath, "database", "exam-transfer.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            if (File.Exists(databasePath))
                File.Copy(databasePath, databasePath + ".before-restore-" + timestamp, overwrite: true);
            File.Copy(restoredDb, databasePath, overwrite: true);

            foreach (var folder in new[] { "exams", "sessions" })
            {
                var source = Path.Combine(staging, folder);
                if (!Directory.Exists(source))
                    continue;

                var destination = Path.Combine(rootPath, folder);
                if (Directory.Exists(destination))
                    Directory.Move(destination, destination + ".before-restore-" + timestamp);
                CopyDirectory(source, destination);
            }

            File.Delete(markerPath);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            if (File.Exists(decryptedZip))
                File.Delete(decryptedZip);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
