using System.Text.Json;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Configuration;

namespace ExamTransfer.Infrastructure;

/// <summary>
/// Persists settings that must be available before SQLite and the local server
/// are initialized. The file is deliberately outside the replaceable source
/// folder so updating the application does not erase machine configuration.
/// </summary>
public static class RuntimeConfiguration
{
    public static string ConfigurationPath
    {
        get
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData))
                programData = AppContext.BaseDirectory;
            return Path.Combine(programData, "ExamTransfer", "config", "runtime-settings.json");
        }
    }

    public static string CloudBootstrapConfigurationPath
    {
        get
        {
            var programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData))
                programData = AppContext.BaseDirectory;
            return Path.Combine(
                programData,
                "ExamTransfer",
                "config",
                "cloud-settings.json");
        }
    }

    public static IConfigurationBuilder AddExamTransferRuntimeSettings(
        this IConfigurationBuilder configuration)
    {
        return configuration
            .AddJsonFile(
                CloudBootstrapConfigurationPath,
                optional: true,
                reloadOnChange: false)
            .AddJsonFile(
                ConfigurationPath,
                optional: true,
                reloadOnChange: false);
    }

    public static void Save(UpdateSettingsRequest request)
    {
        var path = ConfigurationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";

        var payload = new
        {
            Server = new
            {
                Port = request.ServerPort,
                UseHttps = request.UseHttps
            },
            Discovery = new
            {
                Enabled = request.DiscoveryEnabled,
                Port = request.DiscoveryPort
            },
            Storage = new
            {
                RootPath = request.StorageRootPath,
                MinFreeBytes = request.MinFreeBytes
            },
            Transfer = new
            {
                ChunkSizeBytes = request.ChunkSizeBytes,
                MaxConcurrentUploads = request.MaxConcurrentUploads
            },
            Session = new
            {
                HeartbeatSeconds = request.HeartbeatSeconds,
                DisconnectAfterSeconds = request.DisconnectAfterSeconds
            },
            Cloud = new
            {
                Enabled = request.CloudEnabled,
                SupabaseUrl = request.SupabaseUrl,
                PublishableKey = request.SupabasePublishableKey,
                OrganizationId = request.OrganizationId,
                Environment = request.CloudEnvironment,
                UseResumableUploads = request.CloudUseResumableUploads,
                AccessMode = request.CloudAccessMode
            },
            Retention = new
            {
                TemporaryHours = request.TemporaryHours,
                LogsDays = request.LogsDays
            }
        };

        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
