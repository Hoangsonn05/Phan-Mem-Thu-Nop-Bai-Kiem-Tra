using ExamTransfer.Application;
using ExamTransfer.Infrastructure;
using Microsoft.Extensions.Options;

namespace ExamTransfer.LocalServer.Workers;

/// <summary>
/// Keeps an optional Supabase user session fresh without making the LAN exam
/// workflow depend on the cloud. Refresh failures are logged and retried; they
/// never stop the host or the active local session.
/// </summary>
public sealed class CloudAuthRefreshWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ExamTransferOptions> options,
    ILogger<CloudAuthRefreshWorker> logger) : BackgroundService
{
    private readonly CloudOptions cloudOptions = options.Value.Cloud;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

                if (!cloudOptions.Enabled)
                    continue;

                using var scope = scopeFactory.CreateScope();
                var cloud = scope.ServiceProvider
                    .GetRequiredService<ICloudAdapter>();
                if (!cloud.Configured || !cloud.Authenticated)
                    continue;

                _ = await cloud.RefreshSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Supabase user session refresh failed. Local LAN operation remains available.");
            }
        }
    }
}
