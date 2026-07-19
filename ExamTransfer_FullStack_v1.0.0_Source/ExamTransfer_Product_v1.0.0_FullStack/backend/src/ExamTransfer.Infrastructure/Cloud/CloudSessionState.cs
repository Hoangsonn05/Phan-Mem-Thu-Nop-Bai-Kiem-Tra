using System.Text.Json;
using ExamTransfer.Application;
using Microsoft.AspNetCore.DataProtection;

namespace ExamTransfer.Infrastructure.Cloud;

public sealed class CloudSessionState
{
    private readonly object gate = new();
    private readonly IDataProtector protector;
    private readonly string sessionPath;
    private CloudSessionSnapshot? current;

    public CloudSessionState(
        IDataProtectionProvider protectionProvider,
        IStoragePaths paths)
    {
        protector = protectionProvider.CreateProtector(
            "ExamTransfer.Supabase.Session.v1");
        sessionPath = Path.Combine(
            paths.RootPath,
            "config",
            "cloud-session.protected");
        current = Load();
    }

    public CloudSessionSnapshot? Snapshot
    {
        get
        {
            lock (gate)
                return current;
        }
    }

    public void Save(CloudSessionSnapshot snapshot) =>
        Set(snapshot, persist: true);

    public void Set(
        CloudSessionSnapshot snapshot,
        bool persist)
    {
        lock (gate)
        {
            current = snapshot;
            if (!persist)
            {
                if (File.Exists(sessionPath))
                    File.Delete(sessionPath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
            var json = JsonSerializer.Serialize(snapshot);
            var protectedPayload = protector.Protect(json);
            var temporaryPath = sessionPath + ".tmp";
            File.WriteAllText(temporaryPath, protectedPayload);
            File.Move(temporaryPath, sessionPath, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            current = null;
            if (File.Exists(sessionPath))
                File.Delete(sessionPath);
        }
    }

    private CloudSessionSnapshot? Load()
    {
        try
        {
            if (!File.Exists(sessionPath))
                return null;

            var protectedPayload = File.ReadAllText(sessionPath);
            var json = protector.Unprotect(protectedPayload);
            return JsonSerializer.Deserialize<CloudSessionSnapshot>(json);
        }
        catch
        {
            // A corrupted or machine-incompatible token cache must never block
            // the local LAN workflow. The user can sign in again.
            try
            {
                if (File.Exists(sessionPath))
                    File.Delete(sessionPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }

            return null;
        }
    }
}

public sealed record CloudSessionSnapshot(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string UserId,
    string Email,
    string? OrganizationId,
    string? Role);
