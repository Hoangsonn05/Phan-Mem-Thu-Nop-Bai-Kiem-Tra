using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Persistence;

public static class DbInitializer
{
    public const string SchemaVersion = "2";

    public static async Task InitializeAsync(AppDbContext db, IStoragePaths paths, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await ApplyCompatibilityMigrationsAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken);

        var schema = await db.AppSettingsSet.FirstOrDefaultAsync(x => x.Key == "schema.version", cancellationToken);
        if (schema is null)
        {
            db.AppSettingsSet.Add(new AppSetting
            {
                Key = "schema.version",
                ValueJson = $"\"{SchemaVersion}\""
            });
        }
        else
        {
            schema.ValueJson = $"\"{SchemaVersion}\"";
        }

        if (!await db.UsersSet.AnyAsync(cancellationToken))
        {
            db.UsersSet.Add(new User
            {
                Username = "admin",
                DisplayName = "Quản trị viên cục bộ",
                Role = UserRole.Admin,
                IsActive = true
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
    private static async Task ApplyCompatibilityMigrationsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(db, "sync_queue", "CloudObjectPath", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "sync_queue", "UploadUrl", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "sync_queue", "UploadOffset", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "sync_queue", "LastAttemptAtUtc", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "sync_queue", "CompletedAtUtc", "TEXT NULL", cancellationToken);

        await EnsureColumnAsync(db, "graded_attachments", "SyncStatus", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "graded_attachments", "CloudObjectPath", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "export_jobs", "SyncStatus", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "export_jobs", "CloudObjectPath", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "backups", "SyncStatus", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "backups", "CloudObjectPath", "TEXT NULL", cancellationToken);

        await EnsureColumnAsync(db, "users", "OrganizationId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "user_login_sessions", "OrganizationId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "user_login_sessions", "EncryptedRefreshToken", "TEXT NULL", cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        AppDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var check = connection.CreateCommand();
            check.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            var exists = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(
                        reader.GetString(1),
                        column,
                        StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            await reader.DisposeAsync();
            if (exists)
                return;

            await using var alter = connection.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

}
