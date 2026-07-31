using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class ResubmitAuthorityContractTests
{
    [Fact]
    public void StudentState_NotifiesExactlyOncePerAuthorityChange_AndJoinFailsClosed()
    {
        var state = new StudentSessionState();
        var notifications = 0;
        state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StudentSessionState.ResubmitAllowed))
                notifications++;
        };

        state.ApplyResubmitAuthority(true);
        state.ApplyResubmitAuthority(true);

        Assert.True(state.ResubmitAllowed);
        Assert.Equal(1, notifications);

        state.ApplyJoin(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "participant-token",
            "ROOM42",
            "SV001",
            "Student",
            SessionAccessMode.LanOnly);

        Assert.False(state.ResubmitAllowed);
        Assert.Equal(2, notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyLanSnapshot_ProjectsResubmitAuthority(bool resubmitAllowed)
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var state = ActiveState(SessionAccessMode.LanOnly, revision: 2);
        var api = new RecordingBackendClient(now);
        SetLanSnapshot(api, state, now, revision: 2, resubmitAllowed);
        var coordinator = new StudentExamFlowCoordinator(
            api,
            new SupabasePublicCloudClient(),
            state);

        _ = await coordinator.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            CancellationToken.None);

        Assert.Equal(resubmitAllowed, state.ResubmitAllowed);
        Assert.Equal(2, state.Revision);
    }

    [Fact]
    public async Task OnlyLanOlderSnapshot_DoesNotOverwriteNewerAuthority()
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var state = ActiveState(SessionAccessMode.LanOnly, revision: 3);
        state.ApplyResubmitAuthority(true);
        var api = new RecordingBackendClient(now);
        SetLanSnapshot(api, state, now, revision: 2, resubmitAllowed: false);
        var coordinator = new StudentExamFlowCoordinator(
            api,
            new SupabasePublicCloudClient(),
            state);

        _ = await coordinator.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            CancellationToken.None);

        Assert.True(state.ResubmitAllowed);
        Assert.Equal(3, state.Revision);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task PublicTimelineParser_UsesFalseWhenAuthorityIsMissing(
        bool? projectedAuthority,
        bool expected)
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        using var http = new HttpClient(new TimelineHandler(
            new Queue<string>(
                [TimelineJson(sessionId, participantId, 2, projectedAuthority)])));
        var client = await CreateAuthenticatedClientAsync(http);

        var timeline = await client.GetStudentTimelineAsync(
            sessionId,
            CancellationToken.None);

        Assert.Equal(expected, timeline.ResubmitAllowed);
        Assert.Equal("NotStarted", timeline.SubmissionStatus);
        Assert.Equal("FileSubmission", timeline.DeliveryType);
    }

    [Fact]
    public async Task PublicTimelineParser_RejectsWrongAuthorityType()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var json = TimelineJson(sessionId, participantId, 2, null)
            .Replace(
                "\"submissionStatus\":\"NotStarted\"",
                "\"submissionStatus\":\"NotStarted\",\"resubmitAllowed\":\"true\"",
                StringComparison.Ordinal);
        using var http = new HttpClient(new TimelineHandler(new Queue<string>([json])));
        var client = await CreateAuthenticatedClientAsync(http);

        await Assert.ThrowsAsync<JsonException>(() =>
            client.GetStudentTimelineAsync(sessionId, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicCloudSnapshot_ProjectsResubmitAuthority(bool resubmitAllowed)
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        using var http = new HttpClient(new TimelineHandler(
            new Queue<string>(
                [TimelineJson(sessionId, participantId, 2, resubmitAllowed)])));
        var client = await CreateAuthenticatedClientAsync(http);
        var state = ActiveState(
            SessionAccessMode.PublicCloud,
            revision: 1,
            sessionId,
            participantId);
        var coordinator = new StudentExamFlowCoordinator(
            new RecordingBackendClient(DateTimeOffset.UtcNow),
            client,
            state);

        _ = await coordinator.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            CancellationToken.None);

        Assert.Equal(resubmitAllowed, state.ResubmitAllowed);
        Assert.Equal(2, state.Revision);
    }

    [Fact]
    public async Task PublicCloudSnapshot_ProjectsAuthority_AndRejectsStaleOverwrite()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        using var http = new HttpClient(new TimelineHandler(
            new Queue<string>(
            [
                TimelineJson(sessionId, participantId, 3, true),
                TimelineJson(sessionId, participantId, 2, false)
            ])));
        var client = await CreateAuthenticatedClientAsync(http);
        var state = ActiveState(
            SessionAccessMode.PublicCloud,
            revision: 1,
            sessionId,
            participantId);
        var coordinator = new StudentExamFlowCoordinator(
            new RecordingBackendClient(DateTimeOffset.UtcNow),
            client,
            state);

        _ = await coordinator.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            CancellationToken.None);
        Assert.True(state.ResubmitAllowed);
        Assert.Equal(3, state.Revision);

        _ = await coordinator.ResolveAsync(
            StudentExamEntryPoint.CurrentExam,
            false,
            CancellationToken.None);
        Assert.True(state.ResubmitAllowed);
        Assert.Equal(3, state.Revision);
    }

    private static StudentSessionState ActiveState(
        SessionAccessMode mode,
        long revision,
        Guid? sessionId = null,
        Guid? participantId = null) =>
        new()
        {
            SessionId = sessionId ?? Guid.NewGuid(),
            ParticipantId = participantId ?? Guid.NewGuid(),
            AccessToken = "participant-token",
            AccessMode = mode,
            SessionStatus = SessionStatus.InProgress,
            ParticipantStatus = ParticipantStatus.Approved,
            DeliveryType = ExamDeliveryType.FileSubmission,
            SubmissionStatus = SubmissionStatus.NotStarted,
            Revision = revision
        };

    private static void SetLanSnapshot(
        RecordingBackendClient api,
        StudentSessionState state,
        DateTimeOffset now,
        long revision,
        bool resubmitAllowed)
    {
        var participant = new ParticipantDto(
            Id: state.ParticipantId!.Value,
            SessionId: state.SessionId!.Value,
            StudentCode: "SV001",
            DisplayName: "Student",
            DeviceId: "device",
            MachineName: "machine",
            IpAddress: null,
            AppVersion: "1.0.0",
            Status: ParticipantStatus.Approved,
            LastSeenUtc: now,
            DownloadStatus: DownloadStatus.NotStarted,
            SubmissionStatus: SubmissionStatus.NotStarted,
            ExtraTimeMinutes: 0,
            EffectiveDeadlineUtc: now.AddMinutes(60),
            ConnectionState: ConnectionState.Online,
            ResubmitAllowed: resubmitAllowed);
        var summary = new SessionSummaryDto(
            state.SessionId.Value,
            Guid.NewGuid(),
            "Exam",
            "ROOM42",
            SessionStatus.InProgress,
            now,
            now.AddMinutes(-10),
            null,
            now.AddMinutes(60),
            new SessionCountsDto(1, 0, 1, 1, 0, 0, 0),
            revision,
            $"v{revision}",
            SessionAccessMode.LanOnly,
            false,
            ExamDeliveryType.FileSubmission);
        api.SessionDetailResponse = new(summary, [participant], "{}");
        api.ParticipantResponse = participant;
    }

    private static async Task<SupabasePublicCloudClient> CreateAuthenticatedClientAsync(
        HttpClient http)
    {
        var client = new SupabasePublicCloudClient(
            http,
            new ServerClock(new FakeMonotonicTimeSource()),
            "https://project.supabase.co",
            "publishable-key");
        await client.LoginAsync("student", "password", CancellationToken.None);
        return client;
    }

    private static string TimelineJson(
        Guid sessionId,
        Guid participantId,
        long revision,
        bool? resubmitAllowed)
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var values = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["participantId"] = participantId,
            ["participantStatus"] = "Approved",
            ["submissionStatus"] = "NotStarted",
            ["sessionStatus"] = "InProgress",
            ["deliveryType"] = "FileSubmission",
            ["startedAtUtc"] = now.AddMinutes(-10),
            ["durationMinutes"] = 60,
            ["extraTimeMinutes"] = 0,
            ["effectiveDeadlineUtc"] = now.AddMinutes(50),
            ["serverNowUtc"] = now,
            ["revision"] = revision,
            ["updatedAtUtc"] = now
        };
        if (resubmitAllowed.HasValue)
            values["resubmitAllowed"] = resubmitAllowed.Value;
        return JsonSerializer.Serialize(values);
    }

    private sealed class TimelineHandler(Queue<string> timelineResponses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = request.RequestUri?.AbsolutePath.EndsWith(
                "/auth/v1/token",
                StringComparison.Ordinal) == true
                ? """{"access_token":"access","refresh_token":"refresh","expires_in":3600}"""
                : timelineResponses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }
}
