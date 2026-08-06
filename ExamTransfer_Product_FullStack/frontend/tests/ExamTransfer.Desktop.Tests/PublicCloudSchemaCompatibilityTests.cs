using System.Net;
using System.Net.Http;
using System.Text;
using ExamTransfer.Desktop.Infrastructure;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class PublicCloudSchemaCompatibilityTests
{
    [Fact]
    public async Task SchemaVersion29_IsRejected()
    {
        var client = await AuthenticatedClientAsync(
            29,
            RequiredCriticalRpcs());

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.EnsureSchemaCompatibleAsync(default));

        Assert.Equal("PUBLICCLOUD_SCHEMA_INCOMPATIBLE", error.Code);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    [Fact]
    public async Task SchemaVersion30_WithCriticalRpcs_IsAccepted()
    {
        var client = await AuthenticatedClientAsync(
            30,
            RequiredCriticalRpcs());

        await client.EnsureSchemaCompatibleAsync(default);
    }

    [Fact]
    public async Task SchemaVersion30_MissingCriticalRpc_IsRejected()
    {
        var client = await AuthenticatedClientAsync(
            30,
            [
                "get_public_student_notification_events",
                "send_public_teacher_message"
            ]);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.EnsureSchemaCompatibleAsync(default));

        Assert.Equal("PUBLICCLOUD_SCHEMA_INCOMPATIBLE", error.Code);
    }

    private static IReadOnlyList<string> RequiredCriticalRpcs() =>
    [
        "get_public_student_notification_events",
        "send_public_teacher_message",
        "get_student_results"
    ];

    private static async Task<SupabasePublicCloudClient> AuthenticatedClientAsync(
        int schemaVersion,
        IReadOnlyList<string> criticalRpcs)
    {
        var client = new SupabasePublicCloudClient(
            new HttpClient(new CapabilityHandler(schemaVersion, criticalRpcs)),
            supabaseUrl: "https://project.supabase.co",
            publishableKey: "publishable-test-key");
        await client.LoginAsync("teacher@example.test", "password", default);
        return client;
    }

    private sealed class CapabilityHandler(
        int schemaVersion,
        IReadOnlyList<string> criticalRpcs) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/auth/v1/token",
                    StringComparison.Ordinal))
                return Task.FromResult(Json(
                    """{"access_token":"token","refresh_token":"refresh","expires_in":3600}"""));

            var rpcJson = string.Join(",", criticalRpcs.Select(x => $"\"{x}\""));
            return Task.FromResult(Json(
                $$"""{"schemaVersion":{{schemaVersion}},"criticalRpcs":[{{rpcJson}}]}"""));
        }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}
