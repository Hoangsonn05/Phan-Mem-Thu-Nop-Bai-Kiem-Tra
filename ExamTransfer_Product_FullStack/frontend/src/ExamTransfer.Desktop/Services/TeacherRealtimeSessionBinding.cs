using ExamTransfer.Desktop.Core;

namespace ExamTransfer.Desktop.Services;

internal sealed class TeacherRealtimeSessionBinding(IRealtimeService realtime)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Guid? subscribedSessionId;

    public async Task EnsureAsync(
        string? accountToken,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!realtime.IsConnected && !string.IsNullOrWhiteSpace(accountToken))
                await realtime.ConnectAsync(accountToken, cancellationToken);
            await SelectCoreAsync(sessionId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SelectAsync(
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await SelectCoreAsync(sessionId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (subscribedSessionId.HasValue)
            {
                try
                {
                    await realtime.UnsubscribeSessionAsync(
                        subscribedSessionId.Value,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogCleanupFailure(
                        ex,
                        "TeacherRealtimeSessionBinding.Unsubscribe");
                }
                finally
                {
                    subscribedSessionId = null;
                }
            }

            try
            {
                await realtime.DisconnectAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogCleanupFailure(
                    ex,
                    "TeacherRealtimeSessionBinding.Disconnect");
            }

            if (realtime is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    await asyncDisposable.DisposeAsync();
                }
                catch (Exception ex)
                {
                    LogCleanupFailure(
                        ex,
                        "TeacherRealtimeSessionBinding.Dispose");
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static void LogCleanupFailure(Exception exception, string source)
    {
        try
        {
            FrontendLogger.Log(exception, source);
        }
        catch
        {
            // Cleanup must continue even when the profile log directory is
            // unavailable (for example during shutdown or restricted tests).
        }
    }

    private async Task SelectCoreAsync(
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        if (subscribedSessionId == sessionId)
            return;

        if (subscribedSessionId.HasValue)
            await realtime.UnsubscribeSessionAsync(
                subscribedSessionId.Value,
                cancellationToken);
        if (sessionId.HasValue)
            await realtime.SubscribeSessionAsync(
                sessionId.Value,
                cancellationToken);
        subscribedSessionId = sessionId;
    }
}

internal sealed class RealtimeRefreshDebouncer(
    TimeSpan delay,
    string operation) : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? pending;
    private bool disposed;

    public void Schedule(Func<Task> refresh)
    {
        CancellationTokenSource current;
        lock (gate)
        {
            if (disposed)
                return;
            pending?.Cancel();
            current = new CancellationTokenSource();
            pending = current;
        }
        RunAsync(current, refresh).SafeFireAndForget(operation);
    }

    private async Task RunAsync(
        CancellationTokenSource current,
        Func<Task> refresh)
    {
        try
        {
            await Task.Delay(delay, current.Token);
            await refresh();
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, current))
                    pending = null;
            }
            current.Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            pending?.Cancel();
            pending = null;
        }
    }
}
