using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class PublicCloudClockTests
{
    [Fact]
    public async Task HttpsDateHeaderDoesNotSynchronizeExamClock()
    {
        var clock = new ServerClock(new FakeMonotonicTimeSource());
        var httpDate = new DateTimeOffset(2026, 7, 25, 6, 0, 0, TimeSpan.Zero);
        using var http = new HttpClient(new PublicCloudHandler(httpDate, null));
        var client = new SupabasePublicCloudClient(
            http,
            clock,
            "https://project.supabase.co",
            "publishable-key");

        await client.LoginAsync("student", "password", CancellationToken.None);

        Assert.False(clock.TryGetUtcNow(out _));
    }

    [Fact]
    public async Task TimelineRpcSynchronizesClockFromExplicitDatabaseField()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var serverUtc = new DateTimeOffset(2026, 7, 25, 7, 0, 0, TimeSpan.Zero);
        var deadlineUtc = serverUtc.AddHours(1);
        var timelineJson = JsonSerializer.Serialize(new
        {
            sessionId,
            participantId,
            sessionStatus = "InProgress",
            startedAtUtc = serverUtc.AddMinutes(-10),
            durationMinutes = 60,
            extraTimeMinutes = 10,
            effectiveDeadlineUtc = deadlineUtc,
            attemptId = (Guid?)null,
            attemptStatus = (string?)null,
            attemptDeadlineUtc = (DateTimeOffset?)null,
            serverNowUtc = serverUtc,
            revision = 42,
            updatedAtUtc = serverUtc
        });
        var clock = new ServerClock(new FakeMonotonicTimeSource());
        using var http = new HttpClient(new PublicCloudHandler(DateTimeOffset.UtcNow, timelineJson));
        var client = new SupabasePublicCloudClient(
            http,
            clock,
            "https://project.supabase.co",
            "publishable-key");
        await client.LoginAsync("student", "password", CancellationToken.None);

        var timeline = await client.GetStudentTimelineAsync(sessionId, CancellationToken.None);

        Assert.Equal(42, timeline.Revision);
        Assert.True(clock.TryGetUtcNow(out var actual));
        Assert.Equal(serverUtc, actual);
    }

    [Fact]
    public async Task MissingDatabaseServerTimeFailsWithoutWallClockFallback()
    {
        var sessionId = Guid.NewGuid();
        var timelineJson = JsonSerializer.Serialize(new
        {
            sessionId,
            participantId = Guid.NewGuid(),
            sessionStatus = "InProgress",
            durationMinutes = 60,
            extraTimeMinutes = 0,
            revision = 1,
            updatedAtUtc = DateTimeOffset.UtcNow
        });
        var clock = new ServerClock(new FakeMonotonicTimeSource());
        using var http = new HttpClient(new PublicCloudHandler(DateTimeOffset.UtcNow, timelineJson));
        var client = new SupabasePublicCloudClient(
            http,
            clock,
            "https://project.supabase.co",
            "publishable-key");
        await client.LoginAsync("student", "password", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetStudentTimelineAsync(sessionId, CancellationToken.None));

        Assert.False(clock.TryGetUtcNow(out _));
    }

    [Fact]
    public async Task QuizSyncCannotManufactureServerTimestampWhenTimelineIsInvalid()
    {
        var clock = new ServerClock(new FakeMonotonicTimeSource());
        using var http = new HttpClient(new PublicCloudHandler(DateTimeOffset.UtcNow, "{}"));
        var client = new SupabasePublicCloudClient(
            http,
            clock,
            "https://project.supabase.co",
            "publishable-key");
        await client.LoginAsync("student", "password", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SaveQuizAnswersAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                [],
                CancellationToken.None));

        Assert.False(clock.TryGetUtcNow(out _));
    }

    private sealed class PublicCloudHandler(
        DateTimeOffset? dateHeader,
        string? timelineJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = request.RequestUri?.AbsolutePath.EndsWith(
                "/auth/v1/token",
                StringComparison.Ordinal) == true
                ? """{"access_token":"access","refresh_token":"refresh","expires_in":3600}"""
                : timelineJson ?? "{}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            response.Headers.Date = dateHeader;
            return Task.FromResult(response);
        }
    }
}
