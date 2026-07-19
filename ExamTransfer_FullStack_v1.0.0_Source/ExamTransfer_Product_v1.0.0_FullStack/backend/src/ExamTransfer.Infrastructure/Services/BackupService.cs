using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Backup;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class BackupService(
    AppDbContext db,
    IStoragePaths paths,
    IBackupEngine engine,
    IChunkStorage chunks,
    IAuditService audit,
    IOutboxService outbox,
    ICloudAdapter cloud) : IBackupService
{
    public async Task<BackupDto> CreateAsync(
        CreateBackupRequest request,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var extension = request.Encrypt ? ".zip.etb" : ".zip";
        var name = $"examtransfer-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{id.ToString("N")[..8]}{extension}";
        var finalPath = Path.Combine(paths.BackupRoot, name);
        var staging = Path.Combine(paths.TemporaryRoot, "backup-" + id.ToString("N"));
        var plainZip = Path.Combine(paths.TemporaryRoot, "backup-plain-" + id.ToString("N") + ".zip");

        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(paths.BackupRoot);
        Directory.CreateDirectory(paths.TemporaryRoot);

        var record = new BackupRecord
        {
            Id = id,
            RelativePath = Path.GetRelativePath(paths.RootPath, finalPath),
            Encrypted = request.Encrypt,
            Status = BackupStatus.Creating,
            SchemaVersion = DbInitializer.SchemaVersion
        };

        db.BackupsSet.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var dbCopy = Path.Combine(staging, "exam-transfer.db");
            await engine.CreateDatabaseSnapshotAsync(dbCopy, cancellationToken);

            var manifest = new
            {
                id,
                schemaVersion = DbInitializer.SchemaVersion,
                createdAtUtc = DateTimeOffset.UtcNow,
                includeFiles = request.IncludeFiles,
                encrypted = request.Encrypt,
                passwordHint = request.PasswordHint
            };
            await File.WriteAllTextAsync(
                Path.Combine(staging, "backup-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            if (request.IncludeFiles)
            {
                foreach (var folder in new[] { "exams", "sessions" })
                {
                    var source = Path.Combine(paths.RootPath, folder);
                    if (Directory.Exists(source))
                        CopyDirectory(source, Path.Combine(staging, folder));
                }
            }

            if (File.Exists(plainZip))
                File.Delete(plainZip);
            if (File.Exists(finalPath))
                File.Delete(finalPath);

            ZipFile.CreateFromDirectory(
                staging,
                plainZip,
                CompressionLevel.Fastest,
                includeBaseDirectory: false);

            if (request.Encrypt)
            {
                var passphrase = BackupCrypto.GetRequiredPassphrase();
                await BackupCrypto.EncryptFileAsync(
                    plainZip,
                    finalPath,
                    passphrase,
                    cancellationToken);
                File.Delete(plainZip);
            }
            else
            {
                File.Move(plainZip, finalPath, overwrite: true);
            }

            record.SizeBytes = new FileInfo(finalPath).Length;
            record.Sha256 = await chunks.ComputeSha256Async(finalPath, cancellationToken);
            record.Status = BackupStatus.Ready;
            await db.SaveChangesAsync(cancellationToken);

            await audit.WriteAsync(
                "BackupCreated",
                nameof(BackupRecord),
                record.Id.ToString(),
                null,
                null,
                new
                {
                    record.SizeBytes,
                    record.Sha256,
                    request.IncludeFiles,
                    record.Encrypted
                },
                cancellationToken);

            await outbox.EnqueueAsync(
                "backups",
                record.Id.ToString(),
                "upsert",
                ToCloud(record, name),
                finalPath,
                cancellationToken);

            return ToDto(record);
        }
        catch (Exception ex)
        {
            record.Status = BackupStatus.Failed;
            record.Error = ex.Message;
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            if (File.Exists(plainZip))
                File.Delete(plainZip);
        }
    }

    public async Task<IReadOnlyList<BackupDto>> ListAsync(
        CancellationToken cancellationToken)
    {
        if (cloud.Enabled && cloud.Configured)
        {
            try
            {
                var cloudBackups = await cloud.ListBackupsAsync(
                    cancellationToken);
                foreach (var remote in cloudBackups)
                {
                    var local = await db.BackupsSet.FirstOrDefaultAsync(
                        x => x.Id == remote.Id,
                        cancellationToken);
                    if (local is null)
                    {
                        local = new BackupRecord
                        {
                            Id = remote.Id,
                            RelativePath = Path.Combine(
                                "database",
                                "backups",
                                remote.FileName),
                            SchemaVersion = remote.SchemaVersion,
                            SizeBytes = remote.SizeBytes,
                            Sha256 = remote.Sha256,
                            Encrypted = remote.Encrypted,
                            Status = Enum.TryParse<BackupStatus>(
                                remote.Status,
                                ignoreCase: true,
                                out var parsedStatus)
                                    ? parsedStatus
                                    : BackupStatus.Ready,
                            SyncStatus = SyncStatus.Synced,
                            CloudObjectPath = remote.CloudObjectPath,
                            CreatedAtUtc = remote.CreatedAtUtc,
                            UpdatedAtUtc = remote.UpdatedAtUtc
                        };
                        db.BackupsSet.Add(local);
                    }
                    else
                    {
                        local.CloudObjectPath = remote.CloudObjectPath;
                        local.SyncStatus = SyncStatus.Synced;
                        local.SizeBytes = remote.SizeBytes;
                        local.Sha256 = remote.Sha256;
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                // Cloud catalog is supplementary. Local backup listing must
                // remain available while Supabase is offline.
            }
        }

        return (await db.BackupsSet
                .AsNoTracking()
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken))
            .Select(ToDto)
            .ToList();
    }

    public async Task<BackupDto> ValidateAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await db.BackupsSet.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy backup.", 404);
        var fullPath = Path.Combine(paths.RootPath, record.RelativePath);

        if (!File.Exists(fullPath)
            && !string.IsNullOrWhiteSpace(record.CloudObjectPath)
            && cloud.Enabled
            && cloud.Configured)
        {
            try
            {
                await cloud.DownloadObjectAsync(
                    record.CloudObjectPath,
                    fullPath,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                record.Status = BackupStatus.Invalid;
                record.Error = "Không tải được backup từ cloud: " + ex.Message;
                await db.SaveChangesAsync(cancellationToken);
                return ToDto(record);
            }
        }

        if (!File.Exists(fullPath))
        {
            record.Status = BackupStatus.Invalid;
            record.Error = "File backup không tồn tại ở local hoặc cloud.";
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(record);
        }

        var hash = await chunks.ComputeSha256Async(fullPath, cancellationToken);
        if (!hash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            record.Status = BackupStatus.Invalid;
            record.Error = "Checksum SHA-256 không khớp.";
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(record);
        }

        var validationZip = fullPath;
        var temporaryZip = Path.Combine(paths.TemporaryRoot, "validate-" + record.Id.ToString("N") + ".zip");
        try
        {
            if (record.Encrypted || BackupCrypto.IsEncrypted(fullPath))
            {
                var passphrase = BackupCrypto.GetRequiredPassphrase();
                await BackupCrypto.DecryptFileAsync(
                    fullPath,
                    temporaryZip,
                    passphrase,
                    cancellationToken);
                validationZip = temporaryZip;
            }

            using var archive = ZipFile.OpenRead(validationZip);
            var databaseEntry = archive.GetEntry("exam-transfer.db");
            var manifestEntry = archive.GetEntry("backup-manifest.json");
            if (databaseEntry is null || manifestEntry is null)
                throw new InvalidDataException("Backup thiếu database hoặc manifest.");

            await using var manifestStream = manifestEntry.Open();
            using var manifestDocument = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
            var schemaVersion = manifestDocument.RootElement
                .GetProperty("schemaVersion")
                .GetString();
            if (!string.Equals(schemaVersion, record.SchemaVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Schema version trong manifest không khớp metadata.");

            record.Status = BackupStatus.Ready;
            record.Error = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or CryptographicException)
        {
            record.Status = BackupStatus.Invalid;
            record.Error = ex.Message;
        }
        finally
        {
            if (File.Exists(temporaryZip))
                File.Delete(temporaryZip);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(record);
    }

    public async Task<RestoreScheduledDto> ScheduleRestoreAsync(
        Guid id,
        RestoreBackupRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ConfirmationText, "RESTORE", StringComparison.Ordinal))
            throw new ApiException(ErrorCodes.ValidationFailed, "ConfirmationText phải là RESTORE.");

        var hasActiveSession = await db.ExamSessionsSet.AnyAsync(
            x => x.Status == SessionStatus.Waiting
                || x.Status == SessionStatus.InProgress
                || x.Status == SessionStatus.Paused
                || x.Status == SessionStatus.Collecting,
            cancellationToken);
        if (hasActiveSession)
            throw new ApiException(ErrorCodes.Conflict, "Không thể restore khi có phòng thi đang hoạt động.", 409);

        var record = await db.BackupsSet.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy backup.", 404);
        if ((await ValidateAsync(id, cancellationToken)).Status != BackupStatus.Ready)
            throw new ApiException(ErrorCodes.ValidationFailed, "Backup không hợp lệ.");

        var marker = Path.Combine(paths.RootPath, "restore-pending.json");
        await File.WriteAllTextAsync(
            marker,
            JsonSerializer.Serialize(new
            {
                backupPath = Path.Combine(paths.RootPath, record.RelativePath),
                backupId = id,
                encrypted = record.Encrypted
            }),
            cancellationToken);

        record.Status = BackupStatus.RestorePending;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "BackupRestoreScheduled",
            nameof(BackupRecord),
            record.Id.ToString(),
            null,
            null,
            new { record.Encrypted },
            cancellationToken);

        return new RestoreScheduledDto(
            id,
            true,
            "Đã lên lịch khôi phục. Đóng và mở lại LocalServer để áp dụng trước khi database được mở.");
    }

    public async Task<(string Path, string FileName)> GetDownloadAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var record = await db.BackupsSet
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy backup.", 404);
        var fullPath = Path.Combine(paths.RootPath, record.RelativePath);
        if (!File.Exists(fullPath))
            throw new ApiException(ErrorCodes.NotFound, "File backup không tồn tại.", 404);
        return (fullPath, Path.GetFileName(fullPath));
    }

    private static BackupDto ToDto(BackupRecord x) =>
        new(
            x.Id,
            Path.GetFileName(x.RelativePath),
            x.SizeBytes,
            x.Sha256,
            x.SchemaVersion,
            x.Encrypted,
            x.Status,
            x.CreatedAtUtc);

    private static object ToCloud(BackupRecord record, string fileName) => new
    {
        id = record.Id,
        file_name = fileName,
        size_bytes = record.SizeBytes,
        sha256 = record.Sha256,
        schema_version = record.SchemaVersion,
        encrypted = record.Encrypted,
        status = record.Status.ToString(),
        error = record.Error,
        created_at = record.CreatedAtUtc,
        updated_at = record.UpdatedAtUtc
    };

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
