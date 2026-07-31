using ExamTransfer.Desktop.Core;

namespace ExamTransfer.Desktop.Services;

internal sealed class ProjectionRefreshCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly Func<Guid?, CancellationToken, Task> refresh;
    private readonly TimeSpan eventDelay;
    private readonly TimeSpan[] retryDelays;
    private readonly TimeSpan[] recoveryDelays;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, long> highestVersions = [];
    private Task tail = Task.CompletedTask;
    private long recoveryGeneration;
    private bool disposed;

    public ProjectionRefreshCoordinator(
        Func<Guid?, CancellationToken, Task> refresh,
        TimeSpan eventDelay,
        IReadOnlyList<TimeSpan> retryDelays,
        IReadOnlyList<TimeSpan> recoveryDelays)
    {
        this.refresh = refresh;
        this.eventDelay = eventDelay;
        this.retryDelays = [.. retryDelays];
        this.recoveryDelays = [.. recoveryDelays];
    }

    public bool Schedule(Guid sessionId, long projectionVersion)
    {
        lock (gate)
        {
            if (disposed
                || projectionVersion < 1
                || highestVersions.GetValueOrDefault(sessionId) >= projectionVersion)
                return false;

            highestVersions[sessionId] = projectionVersion;
            recoveryGeneration++;
            var cancellationToken = lifetime.Token;
            Append(() => RefreshVersionAsync(sessionId, projectionVersion, cancellationToken));
            return true;
        }
    }

    public void StartRecovery()
    {
        lock (gate)
        {
            if (disposed)
                return;
            var generation = ++recoveryGeneration;
            var cancellationToken = lifetime.Token;
            Append(() => RecoverAsync(generation, cancellationToken));
        }
    }

    private void Append(Func<Task> work) =>
        tail = tail.ContinueWith(
            _ => work(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();

    private async Task RefreshVersionAsync(
        Guid sessionId,
        long projectionVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(eventDelay, cancellationToken);
            for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
            {
                try
                {
                    await refresh(sessionId, cancellationToken);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == retryDelays.Length)
                    {
                        FrontendLogger.LogWarning(
                            $"Projection refresh stopped after {attempt + 1} attempts. SessionId={sessionId}; ProjectionVersion={projectionVersion}; Error={ex.Message}",
                            "LiveMonitor.ProjectionRefresh");
                        return;
                    }
                    await Task.Delay(retryDelays[attempt], cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RecoverAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var delay in recoveryDelays)
            {
                await Task.Delay(delay, cancellationToken);
                lock (gate)
                {
                    if (disposed || generation != recoveryGeneration)
                        return;
                }
                try
                {
                    await refresh(null, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    FrontendLogger.LogWarning(
                        $"Bounded projection recovery refresh failed. AttemptGeneration={generation}; Error={ex.Message}",
                        "LiveMonitor.ProjectionRecovery");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            recoveryGeneration++;
            lifetime.Cancel();
        }
        lifetime.Dispose();
    }
}
