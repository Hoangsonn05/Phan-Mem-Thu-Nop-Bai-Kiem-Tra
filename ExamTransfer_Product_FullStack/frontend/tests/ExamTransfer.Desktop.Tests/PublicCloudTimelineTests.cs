using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using System.Text.Json.Nodes;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class PublicCloudTimelineTests
{
    [Fact]
    public void QuizGradeReturnedDeviceSignalUsesExactlyOneDispatchPath()
    {
        var sessionId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var transportId = Guid.NewGuid();
        const string deviceId = "device-owner";
        var json = $$"""
        {
          "topic":"realtime:exam-session:{{sessionId}}:device:{{deviceId}}",
          "event":"broadcast",
          "payload":{
            "event":"QuizGradeReturned",
            "meta":{"id":"{{transportId}}"},
            "payload":{
              "id":"{{transportId}}",
              "eventType":"QuizGradeReturned",
              "attemptId":"{{attemptId}}",
              "sessionId":"{{sessionId}}"
            }
          }
        }
        """;

        Assert.True(SupabaseRealtimeService.TryParseBroadcast(
            json,
            sessionId,
            deviceId,
            out var notification,
            out var eventName));
        Assert.Null(notification);
        Assert.Equal($"{RealtimeEvents.QuizGradeReturned}:{attemptId:N}", eventName);
        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answer", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacySessionWideOrLeakyGradeBroadcastIsRejected()
    {
        var sessionId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var transportId = Guid.NewGuid();
        var json = $$"""
        {
          "topic":"realtime:exam-session:{{sessionId}}",
          "event":"broadcast",
          "payload":{
            "event":"QuizGradeReturned",
            "meta":{"id":"{{transportId}}"},
            "payload":{
              "id":"{{transportId}}",
              "eventType":"QuizGradeReturned",
              "attemptId":"{{attemptId}}",
              "sessionId":"{{sessionId}}",
              "score":8.5
            }
          }
        }
        """;

        Assert.False(SupabaseRealtimeService.TryParseBroadcast(
            json,
            sessionId,
            "device-owner",
            out var notification,
            out var eventName));
        Assert.Null(notification);
        Assert.Null(eventName);
    }

    [Fact]
    public void PhoenixArrayGradeReopenedSignalRejectsPeerDevice()
    {
        var sessionId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var transportId = Guid.NewGuid();
        const string ownerDevice = "device-owner";
        var json = $$"""
        [null,"7","realtime:exam-session:{{sessionId}}:device:{{ownerDevice}}","broadcast",{
          "event":"QuizGradeReopened",
          "meta":{"id":"{{transportId}}"},
          "payload":{
            "id":"{{transportId}}",
            "eventType":"QuizGradeReopened",
            "attemptId":"{{attemptId}}",
            "sessionId":"{{sessionId}}"
          }
        }]
        """;

        Assert.True(SupabaseRealtimeService.TryParseBroadcast(
            json,
            sessionId,
            ownerDevice,
            out var notification,
            out var eventName));
        Assert.Null(notification);
        Assert.Equal($"{RealtimeEvents.QuizGradeReopened}:{attemptId:N}", eventName);
        Assert.False(SupabaseRealtimeService.TryParseBroadcast(
            json,
            sessionId,
            "device-peer",
            out _,
            out _));
    }

    [Fact]
    public void GradeVisibilitySignalRejectsInvalidTransportOrApplicationContract()
    {
        var sessionId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var transportId = Guid.NewGuid();
        const string ownerDevice = "device-owner";
        var ownerTopic = $"realtime:exam-session:{sessionId}:device:{ownerDevice}";

        string Frame(
            bool includeMeta = true,
            bool includeMetaId = true,
            string? metaId = null,
            bool includePayloadId = true,
            string? payloadId = null,
            string? topic = null,
            Guid? payloadSessionId = null,
            string eventType = RealtimeEvents.QuizGradeReturned,
            string? extraKey = null)
        {
            var gradePayload = new JsonObject
            {
                ["eventType"] = eventType,
                ["attemptId"] = attemptId.ToString(),
                ["sessionId"] = (payloadSessionId ?? sessionId).ToString()
            };
            if (includePayloadId)
                gradePayload["id"] = payloadId ?? transportId.ToString();
            if (extraKey is not null)
                gradePayload[extraKey] = "forbidden";

            var payload = new JsonObject
            {
                ["event"] = RealtimeEvents.QuizGradeReturned,
                ["type"] = "broadcast",
                ["payload"] = gradePayload
            };
            if (includeMeta)
            {
                var metadata = new JsonObject();
                if (includeMetaId)
                    metadata["id"] = metaId ?? transportId.ToString();
                payload["meta"] = metadata;
            }

            return new JsonObject
            {
                ["topic"] = topic ?? ownerTopic,
                ["event"] = "broadcast",
                ["payload"] = payload
            }.ToJsonString();
        }

        var rejectedFrames = new Dictionary<string, string>
        {
            ["missing meta"] = Frame(includeMeta: false),
            ["missing meta.id"] = Frame(includeMetaId: false),
            ["missing payload.id"] = Frame(includePayloadId: false),
            ["invalid meta.id"] = Frame(metaId: "not-a-uuid"),
            ["invalid payload.id"] = Frame(payloadId: "not-a-uuid"),
            ["mismatched transport IDs"] = Frame(payloadId: Guid.NewGuid().ToString()),
            ["unknown fifth key"] = Frame(extraKey: "unexpected"),
            ["score"] = Frame(extraKey: "score"),
            ["maxScore"] = Frame(extraKey: "maxScore"),
            ["correctAnswers"] = Frame(extraKey: "correctAnswers"),
            ["answers"] = Frame(extraKey: "answers"),
            ["legacy session topic"] = Frame(topic: $"realtime:exam-session:{sessionId}"),
            ["peer device topic"] = Frame(
                topic: $"realtime:exam-session:{sessionId}:device:device-peer"),
            ["wrong sessionId"] = Frame(payloadSessionId: Guid.NewGuid()),
            ["invalid eventType"] = Frame(eventType: "QuizGradePublished")
        };

        foreach (var (reason, frame) in rejectedFrames)
        {
            Assert.False(SupabaseRealtimeService.TryParseBroadcast(
                frame,
                sessionId,
                ownerDevice,
                out var notification,
                out var eventName));
            Assert.Null(notification);
            Assert.Null(eventName);
        }
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

        Assert.True(SupabaseRealtimeService.TryParseBroadcast(
            json,
            sessionId,
            "device-owner",
            out var notification,
            out var fallback));
        Assert.Null(fallback);
        Assert.Equal(55, notification!.Revision);
        Assert.Equal(participantId, notification.TimeExtended!.ParticipantId);
        Assert.Equal(attemptId, notification.TimeExtended.AttemptId);
        Assert.Equal(requestId, notification.TimeExtended.RequestId);
        Assert.False(SupabaseRealtimeService.TryParseBroadcast(
            json,
            Guid.NewGuid(),
            "device-owner",
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

        Assert.True(SupabaseRealtimeService.TryParseBroadcast(
            json,
            sessionId,
            "device-owner",
            out var notification,
            out var fallback));
        Assert.Null(notification);
        Assert.Equal(RealtimeEvents.TimeExtended, fallback);
    }
}
