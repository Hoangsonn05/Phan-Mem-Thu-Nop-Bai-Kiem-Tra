using ExamTransfer.Desktop.Services;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class ServerClockTests
{
    [Fact]
    public void UnsynchronizedClock_DoesNotFallBackToLocalWallClock()
    {
        var clock = new ServerClock(new FakeMonotonicTimeSource());

        Assert.False(clock.TryGetUtcNow(out _));
        Assert.Equal("--:--:--", ServerCountdown.Format(ServerCountdown.Remaining(
            clock,
            DateTimeOffset.UtcNow.AddMinutes(10))));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(60)]
    public void FirstSync_UsesMonotonicElapsedTime(int elapsedSeconds)
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var synchronizedUtc = new DateTimeOffset(2026, 7, 25, 1, 2, 3, TimeSpan.Zero);
        clock.Synchronize(synchronizedUtc);

        source.Advance(TimeSpan.FromSeconds(elapsedSeconds));

        Assert.True(clock.TryGetUtcNow(out var actual));
        Assert.Equal(synchronizedUtc.AddSeconds(elapsedSeconds), actual);
    }

    [Fact]
    public void Resync_ReplacesAnchorAndDeadlineExtensionUsesNewSnapshot()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var firstServerUtc = new DateTimeOffset(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);
        clock.Synchronize(firstServerUtc);
        source.Advance(TimeSpan.FromSeconds(10));
        clock.Synchronize(firstServerUtc.AddSeconds(30));

        var originalDeadline = firstServerUtc.AddSeconds(60);
        var extendedDeadline = originalDeadline.AddMinutes(5);

        Assert.Equal(TimeSpan.FromSeconds(30), ServerCountdown.Remaining(clock, originalDeadline));
        Assert.Equal(TimeSpan.FromMinutes(5.5), ServerCountdown.Remaining(clock, extendedDeadline));
    }

    [Fact]
    public void DeadlineAlreadyPassed_IsClampedToZero()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var serverUtc = new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero);
        clock.Synchronize(serverUtc);

        Assert.Equal(TimeSpan.Zero, ServerCountdown.Remaining(clock, serverUtc.AddSeconds(-1)));
        Assert.Equal("00:00:00", ServerCountdown.Format(TimeSpan.Zero));
    }

    [Fact]
    public void LocalWallClockChangesCannotAffectSynchronizedClock()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var serverUtc = new DateTimeOffset(2026, 7, 25, 4, 0, 0, TimeSpan.Zero);
        clock.Synchronize(serverUtc);

        _ = DateTimeOffset.UtcNow.AddYears(20);
        source.Advance(TimeSpan.FromSeconds(4));

        Assert.True(clock.TryGetUtcNow(out var actual));
        Assert.Equal(serverUtc.AddSeconds(4), actual);
    }
}

internal sealed class FakeMonotonicTimeSource : IMonotonicTimeSource
{
    private long milliseconds;

    public long GetTimestamp() => milliseconds;

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeSpan.FromMilliseconds(endingTimestamp - startingTimestamp);

    public void Advance(TimeSpan elapsed) => milliseconds += (long)elapsed.TotalMilliseconds;
}
