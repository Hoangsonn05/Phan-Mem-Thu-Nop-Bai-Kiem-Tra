using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class PublicCloudTimelineTests
{
    [Fact]
    public void QuizGradeReturnedBroadcastCarriesAttemptIdentityWithoutCorrectAnswers()
    {
        var sessionId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var json = $$"""
        {
          "topic":"realtime:exam-session:{{sessionId}}",
          "event":"broadcast",
          "payload":{
            "event":"QuizGradeReturned",
            "payload":{
              "attemptId":"{{attemptId}}",
              "sessionId":"{{sessionId}}",
              "score":8.5,
              "maxScore":10,
              "returnedAtUtc":"2026-07-28T04:00:00Z"
            }
          }
        }
        """;

        Assert.False(SupabaseRealtimeService.TryParseTimeExtended(
            json,
            sessionId,
            out var notification,
            out var eventName));
        Assert.Null(notification);
        Assert.Equal($"{RealtimeEvents.QuizGradeReturned}:{attemptId:N}", eventName);
        Assert.DoesNotContain("correct", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewerEventWinsOlderSnapshotAndAbsoluteDeadlineIsNotAddedTwice()
    {
        var clock = new ServerClock(new FakeMonotonicTimeSource());
        var coordinator = new ServerTimelineCoordinator(clock);
        var now = new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero);
        var eventDeadline = now.AddMinutes(75);

        Assert.True(coordinator.TryApply(20, eventDeadline, now));
        Assert.False(coordinator.TryApply(19, now.AddMinutes(60), now.AddSeconds(1)));
        Assert.True(coordinator.TryApply(20, eventDeadline, now.AddSeconds(2)));
        Assert.Equal(eventDeadline, coordinator.DeadlineUtc);
        Assert.Equal(20, coordinator.Revision);
    }

    [Fact]
    public void NewerSnapshotWinsOlderEvent()
    {
        var clock = new ServerClock(new FakeMonotonicTimeSource());
        var coordinator = new ServerTimelineCoordinator(clock);
        var now = new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero);

        Assert.True(coordinator.TryApply(30, now.AddMinutes(90), now));
        Assert.False(coordinator.TryApply(29, now.AddMinutes(75), now.AddSeconds(1)));
        Assert.Equal(now.AddMinutes(90), coordinator.DeadlineUtc);
    }

    [Fact]
    public void StaleServerTimeCannotMoveSynchronizedClockBackwards()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var now = new DateTimeOffset(2026, 7, 25, 8, 0, 0, TimeSpan.Zero);
        clock.Synchronize(now);
        source.Advance(TimeSpan.FromSeconds(5));

        clock.Synchronize(now.AddSeconds(2));

        Assert.True(clock.TryGetUtcNow(out var actual));
        Assert.Equal(now.AddSeconds(5), actual);
    }

    [Fact]
    public void SupabaseBroadcastParserMapsAbsoluteTypedEventAndFiltersOtherSession()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var json = $$"""
        {
          "topic":"realtime:exam-session:{{sessionId}}",
          "event":"broadcast",
          "payload":{
            "event":"TimeExtended",
            "type":"broadcast",
            "payload":{
              "participantId":"{{participantId}}",
              "attemptId":"{{attemptId}}",
              "requestId":"{{requestId}}",
              "minutes":15,
              "effectiveDeadlineUtc":"2026-07-25T10:00:00Z",
              "serverNowUtc":"2026-07-25T08:00:00Z",
              "revision":55
            }
          }
        }
        """;

        Assert.True(SupabaseRealtimeService.TryParseTimeExtended(
            json,
            sessionId,
            out var notification,
            out var fallback));
        Assert.Null(fallback);
        Assert.Equal(55, notification!.Revision);
        Assert.Equal(participantId, notification.TimeExtended!.ParticipantId);
        Assert.Equal(attemptId, notification.TimeExtended.AttemptId);
        Assert.Equal(requestId, notification.TimeExtended.RequestId);
        Assert.False(SupabaseRealtimeService.TryParseTimeExtended(
            json,
            Guid.NewGuid(),
            out _,
            out _));
    }

    [Fact]
    public void MissingRequiredRealtimeFieldRequestsOneSnapshotFallback()
    {
        var sessionId = Guid.NewGuid();
        var json = $$"""
        {
          "topic":"realtime:exam-session:{{sessionId}}",
          "event":"broadcast",
          "payload":{
            "event":"TimeExtended",
            "payload":{"participantId":"{{Guid.NewGuid()}}","revision":56}
          }
        }
        """;

        Assert.False(SupabaseRealtimeService.TryParseTimeExtended(
            json,
            sessionId,
            out var notification,
            out var fallback));
        Assert.Null(notification);
        Assert.Equal(RealtimeEvents.TimeExtended, fallback);
    }
}
