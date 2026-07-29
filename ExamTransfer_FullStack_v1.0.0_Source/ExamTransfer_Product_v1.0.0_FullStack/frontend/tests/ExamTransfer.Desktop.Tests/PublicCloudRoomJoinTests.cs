using System.Net;
using System.Net.Http;
using System.Text;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class PublicCloudRoomJoinTests
{
    [Fact]
    public async Task ValidRoomCode_UsesCanonicalOpenJoinRpcAndReturnsPendingWaiting()
    {
        var handler = new PublicCloudJoinHandler();
        var client = new SupabasePublicCloudClient(
            new HttpClient(handler),
            supabaseUrl: "https://project.supabase.co",
            publishableKey: "publishable-test-key");
        await client.LoginAsync("student01", "password", default);

        var result = await client.JoinByRoomCodeAsync(
            "ROOM42",
            "device-1",
            "Student",
            "1.3.1",
            default);

        Assert.Contains(
            "/rest/v1/rpc/join_open_public_session_by_room_code",
            handler.Paths);
        Assert.DoesNotContain(handler.Paths, x => x.Contains("join_public_session/", StringComparison.Ordinal));
        Assert.Equal(ParticipantStatus.PendingApproval, result.ParticipantStatus);
        Assert.Equal(SessionStatus.Waiting, result.SessionStatus);
        Assert.Equal("ROOM42", result.RoomCode);
    }

    private sealed class PublicCloudJoinHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            var json = request.RequestUri.AbsolutePath.EndsWith("/auth/v1/token", StringComparison.Ordinal)
                ? """{"access_token":"cloud-token","refresh_token":"refresh-token","expires_in":3600}"""
                : request.RequestUri.AbsolutePath.EndsWith(
                    "/rpc/get_examtransfer_cloud_capabilities",
                    StringComparison.Ordinal)
                    ? """{"schemaVersion":23}"""
                    : $$"""
                    {
                      "sessionId":"{{Guid.NewGuid()}}",
                      "examId":"{{Guid.NewGuid()}}",
                      "participantId":"{{Guid.NewGuid()}}",
                      "participantStatus":"PendingApproval",
                      "sessionStatus":"Waiting",
                      "roomCode":"ROOM42",
                      "examTitle":"Cloud exam",
                      "subject":"Tin",
                      "durationMinutes":45,
                      "deliveryType":"FileSubmission",
                      "supervisionMode":"None",
                      "quizResultPolicy":"Hidden",
                      "plannedStartUtc":null,
                      "capacity":40,
                      "currentParticipantCount":1
                    }
                    """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
