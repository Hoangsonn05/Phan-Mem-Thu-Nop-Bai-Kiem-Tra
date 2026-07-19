using ExamTransfer.Application;
using ExamTransfer.Infrastructure;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Workers;

public sealed class TransferCleanupWorker(IStoragePaths paths, IOptions<ExamTransferOptions> options, ILogger<TransferCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
                var cutoff = DateTimeOffset.UtcNow.AddHours(-options.Value.Retention.TemporaryHours);
                foreach (var dir in Directory.Exists(paths.TemporaryRoot) ? Directory.EnumerateDirectories(paths.TemporaryRoot) : [])
                {
                    var info = new DirectoryInfo(dir);
                    if (info.LastWriteTimeUtc < cutoff.UtcDateTime) Directory.Delete(dir, true);
                }
                var sessionsRoot = Path.Combine(paths.RootPath, "sessions");
                if (Directory.Exists(sessionsRoot))
                {
                    foreach (var temp in Directory.EnumerateDirectories(sessionsRoot, "temporary", SearchOption.AllDirectories))
                    {
                        foreach (var dir in Directory.EnumerateDirectories(temp))
                        {
                            var info = new DirectoryInfo(dir); if (info.LastWriteTimeUtc < cutoff.UtcDateTime) Directory.Delete(dir, true);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "Temporary transfer cleanup failed"); }
        }
    }
}
