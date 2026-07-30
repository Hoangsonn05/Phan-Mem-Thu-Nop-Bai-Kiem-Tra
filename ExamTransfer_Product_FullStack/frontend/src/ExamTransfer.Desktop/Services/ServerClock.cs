using System.Diagnostics;
using System.Windows.Threading;

namespace ExamTransfer.Desktop.Services;

public interface IMonotonicTimeSource
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);
}

public sealed class StopwatchTimeSource : IMonotonicTimeSource
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        Stopwatch.GetElapsedTime(startingTimestamp, endingTimestamp);
}

public interface IServerClock
{
    bool IsSynchronized { get; }
    bool TryGetUtcNow(out DateTimeOffset utcNow);
    void Synchronize(DateTimeOffset serverNowUtc);
}

public sealed class ServerClock(IMonotonicTimeSource monotonicTimeSource) : IServerClock
{
    private readonly object gate = new();
    private DateTimeOffset serverNowUtc;
    private long synchronizedAtTimestamp;
    private bool synchronized;

    public ServerClock() : this(new StopwatchTimeSource())
    {
    }

    public bool IsSynchronized
    {
        get
        {
            lock (gate)
            {
                return synchronized;
            }
        }
    }

    public bool TryGetUtcNow(out DateTimeOffset utcNow)
    {
        lock (gate)
        {
            if (!synchronized)
            {
                utcNow = default;
                return false;
            }

            var currentTimestamp = monotonicTimeSource.GetTimestamp();
            var elapsed = monotonicTimeSource.GetElapsedTime(synchronizedAtTimestamp, currentTimestamp);
            utcNow = serverNowUtc + (elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
            return true;
        }
    }

    public void Synchronize(DateTimeOffset serverNowUtc)
    {
        lock (gate)
        {
            var currentTimestamp = monotonicTimeSource.GetTimestamp();
            var candidateUtc = serverNowUtc.ToUniversalTime();
            if (synchronized)
            {
                var elapsed = monotonicTimeSource.GetElapsedTime(
                    synchronizedAtTimestamp,
                    currentTimestamp);
                var currentUtc = this.serverNowUtc
                    + (elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
                if (candidateUtc < currentUtc)
                {
                    return;
                }
            }

            this.serverNowUtc = candidateUtc;
            synchronizedAtTimestamp = currentTimestamp;
            synchronized = true;
        }
    }
}

public sealed class ServerTimelineCoordinator(IServerClock clock)
{
    private readonly object gate = new();
    private long revision = -1;
    private DateTimeOffset? deadlineUtc;

    public long Revision
    {
        get { lock (gate) return revision; }
    }

    public DateTimeOffset? DeadlineUtc
    {
        get { lock (gate) return deadlineUtc; }
    }

    public bool TryApply(
        long candidateRevision,
        DateTimeOffset candidateDeadlineUtc,
        DateTimeOffset serverNowUtc)
    {
        lock (gate)
        {
            var normalizedDeadline = candidateDeadlineUtc.ToUniversalTime();
            if (candidateRevision < revision)
            {
                return false;
            }
            if (candidateRevision == revision
                && deadlineUtc.HasValue
                && deadlineUtc.Value != normalizedDeadline)
            {
                return false;
            }

            revision = candidateRevision;
            deadlineUtc = normalizedDeadline;
            clock.Synchronize(serverNowUtc);
            return true;
        }
    }
}

public static class ServerCountdown
{
    public static TimeSpan? Remaining(IServerClock clock, DateTimeOffset? deadlineUtc)
    {
        if (deadlineUtc is null || !clock.TryGetUtcNow(out var serverNowUtc))
        {
            return null;
        }

        var remaining = deadlineUtc.Value.ToUniversalTime() - serverNowUtc;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    public static string Format(TimeSpan? remaining)
    {
        if (remaining is null)
        {
            return "--:--:--";
        }

        var value = remaining.Value < TimeSpan.Zero ? TimeSpan.Zero : remaining.Value;
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }
}

public interface ICountdownTicker : IDisposable
{
    event EventHandler? Tick;
    bool IsRunning { get; }
    void Start();
    void Stop();
}

public interface ICountdownTickerFactory
{
    ICountdownTicker Create(TimeSpan interval);
}

public sealed class DispatcherCountdownTickerFactory : ICountdownTickerFactory
{
    public ICountdownTicker Create(TimeSpan interval) => new DispatcherCountdownTicker(interval);
}

public sealed class DispatcherCountdownTicker : ICountdownTicker
{
    private readonly DispatcherTimer timer;
    private bool disposed;

    public DispatcherCountdownTicker(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += ForwardTick;
    }

    public event EventHandler? Tick;
    public bool IsRunning => !disposed && timer.IsEnabled;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        timer.Start();
    }

    public void Stop()
    {
        if (!disposed)
        {
            timer.Stop();
        }
    }

    private void ForwardTick(object? sender, EventArgs e) => Tick?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Stop();
        timer.Tick -= ForwardTick;
        Tick = null;
    }
}
