using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class SupabaseAuthenticationRetryTests
{
    [Fact]
    public async Task UnauthorizedPasswordGrant_IsNotRetried()
    {
        var handler = new SequencedAuthHandler(HttpStatusCode.Unauthorized);
        var client = Client(handler);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.AuthenticateAccountAsync(
                "teacher@example.test",
                "wrong-password",
                "device-1",
                default));

        Assert.Equal(ErrorCodes.InvalidCredentials, error.Code);
        Assert.Equal(1, handler.AuthCalls);
        Assert.Equal(0, handler.ProfileCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TransientStatusThenSuccess_RetriesBoundedly(
        HttpStatusCode transientStatus)
    {
        var delays = new List<TimeSpan>();
        var transient = new HttpResponseMessage(transientStatus);
        if (transientStatus == HttpStatusCode.TooManyRequests)
        {
            transient.Headers.RetryAfter = new RetryConditionHeaderValue(
                TimeSpan.FromSeconds(10));
        }
        var handler = new SequencedAuthHandler(transient, HttpStatusCode.OK);
        var client = Client(handler, delays);

        var result = await client.AuthenticateAccountAsync(
            "teacher@example.test",
            "correct-password",
            "device-1",
            default);

        Assert.Equal(UserRole.Teacher, result.Account.Role);
        Assert.Equal(2, handler.AuthCalls);
        Assert.Single(delays);
        Assert.InRange(delays[0], TimeSpan.Zero, TimeSpan.FromSeconds(5));
        if (transientStatus == HttpStatusCode.TooManyRequests)
            Assert.Equal(TimeSpan.FromSeconds(5), delays[0]);
    }

    [Fact]
    public async Task NetworkFailureThenSuccess_IsRetried()
    {
        var delays = new List<TimeSpan>();
        var handler = new SequencedAuthHandler(
            new HttpRequestException("fixture network reset"),
            HttpStatusCode.OK);
        var client = Client(handler, delays);

        var result = await client.AuthenticateAccountAsync(
            "teacher@example.test",
            "correct-password",
            "device-1",
            default);

        Assert.Equal(UserRole.Teacher, result.Account.Role);
        Assert.Equal(2, handler.AuthCalls);
        Assert.Single(delays);
    }

    [Fact]
    public async Task ProfileTransientFailureThenSuccess_IsRetried()
    {
        var delays = new List<TimeSpan>();
        var handler = new SequencedAuthHandler(HttpStatusCode.OK)
        {
            ProfileOutcomes = new Queue<HttpStatusCode>(
                [HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK])
        };
        var client = Client(handler, delays);

        var result = await client.AuthenticateAccountAsync(
            "teacher@example.test",
            "correct-password",
            "device-1",
            default);

        Assert.Equal(UserRole.Teacher, result.Account.Role);
        Assert.Equal(1, handler.AuthCalls);
        Assert.Equal(2, handler.ProfileCalls);
        Assert.Single(delays);
    }

    [Fact]
    public async Task ThreeTransientFailures_StopWithProviderUnavailable()
    {
        var delays = new List<TimeSpan>();
        var handler = new SequencedAuthHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable);
        var client = Client(handler, delays);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.AuthenticateAccountAsync(
                "teacher@example.test",
                "correct-password",
                "device-1",
                default));

        Assert.Equal(ErrorCodes.AuthProviderUnavailable, error.Code);
        Assert.Equal(3, handler.AuthCalls);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task UserCancellation_StopsWithoutRetry()
    {
        var handler = new SequencedAuthHandler(HttpStatusCode.OK);
        var client = Client(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.AuthenticateAccountAsync(
                "teacher@example.test",
                "correct-password",
                "device-1",
                cancellation.Token));

        Assert.Equal(0, handler.AuthCalls);
    }

    [Fact]
    public async Task ConcurrentLogins_DoNotMixAccountState()
    {
        var handler = new ConcurrentAccountHandler();
        var client = Client(handler);
        var accounts = Enumerable.Range(1, 30)
            .Select(index => $"teacher{index:D2}@example.test")
            .ToArray();

        var results = await Task.WhenAll(accounts.Select(account =>
            client.AuthenticateAccountAsync(
                account,
                "correct-password",
                "device-1",
                default)));

        Assert.Equal(30, handler.AuthCalls);
        Assert.Equal(30, handler.ProfileCalls);
        Assert.Equal(30, results.Select(result => result.Account.ProviderUserId).Distinct().Count());
        Assert.Equal(
            accounts.Order(StringComparer.Ordinal),
            results.Select(result => result.Account.Username).Order(StringComparer.Ordinal));
    }

    private static SupabasePublicCloudClient Client(
        HttpMessageHandler handler,
        List<TimeSpan>? delays = null)
    {
        var options = new FixedPublicCloudRuntimeOptionsProvider(
            new PublicCloudRuntimeOptions(
                new Uri("https://project.supabase.test"),
                "sb_publishable_test_key",
                null,
                "Test",
                SequencedAuthHandler.OrganizationId));
        return new SupabasePublicCloudClient(
            new HttpClient(handler),
            optionsProvider: options,
            delay: (duration, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays?.Add(duration);
                return Task.CompletedTask;
            },
            retryJitterMilliseconds: () => 0);
    }

    private static string Jwt(Guid subject)
    {
        static string Encode(object value) =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return $"{Encode(new { alg = "none" })}.{Encode(new
        {
            sub = subject,
            session_id = Guid.NewGuid(),
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        })}.signature";
    }

    private static HttpResponseMessage AuthSuccess(Guid userId, string email) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                access_token = Jwt(userId),
                refresh_token = "fixture-refresh-token",
                expires_in = 3600,
                user = new { id = userId, email }
            })
        };

    private static HttpResponseMessage ProfileSuccess(Guid userId, string email) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[]
            {
                new
                {
                    id = userId,
                    organization_id = SequencedAuthHandler.OrganizationId,
                    username = email,
                    display_name = "Giáo viên",
                    student_code = (string?)null,
                    date_of_birth = (string?)null,
                    must_change_password = false,
                    role = "Teacher",
                    is_active = true
                }
            })
        };

    private sealed class SequencedAuthHandler(params object[] outcomes) : HttpMessageHandler
    {
        public static readonly Guid OrganizationId =
            Guid.Parse("7bce49ea-6b33-4be0-ab90-3835f0f75a54");
        private static readonly Guid UserId =
            Guid.Parse("11f88943-ab77-4052-b4f1-83c13fb5dc93");
        private readonly Queue<object> authOutcomes = new(outcomes);

        public Queue<HttpStatusCode> ProfileOutcomes { get; init; } = new();
        public int AuthCalls { get; private set; }
        public int ProfileCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/auth/v1/token")
            {
                AuthCalls++;
                var outcome = authOutcomes.Count == 0
                    ? HttpStatusCode.OK
                    : authOutcomes.Dequeue();
                if (outcome is Exception exception)
                    return Task.FromException<HttpResponseMessage>(exception);
                if (outcome is HttpResponseMessage response)
                    return Task.FromResult(response);
                var status = (HttpStatusCode)outcome;
                return Task.FromResult(status == HttpStatusCode.OK
                    ? AuthSuccess(UserId, "teacher@example.test")
                    : new HttpResponseMessage(status));
            }

            if (request.RequestUri.AbsolutePath == "/rest/v1/profiles")
            {
                ProfileCalls++;
                var status = ProfileOutcomes.Count == 0
                    ? HttpStatusCode.OK
                    : ProfileOutcomes.Dequeue();
                return Task.FromResult(status == HttpStatusCode.OK
                    ? ProfileSuccess(UserId, "teacher@example.test")
                    : new HttpResponseMessage(status));
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }
    }

    private sealed class ConcurrentAccountHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<Guid, string> accounts = new();
        private Guid? pendingProfile;
        public int AuthCalls { get; private set; }
        public int ProfileCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath == "/auth/v1/token")
            {
                if (pendingProfile.HasValue)
                    throw new InvalidOperationException("Concurrent auth state was interleaved.");
                AuthCalls++;
                using var body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                var email = body.RootElement.GetProperty("email").GetString()!;
                var userId = Guid.NewGuid();
                accounts[userId] = email;
                pendingProfile = userId;
                return AuthSuccess(userId, email);
            }

            if (request.RequestUri.AbsolutePath == "/rest/v1/profiles")
            {
                ProfileCalls++;
                var query = request.RequestUri.Query;
                var marker = "id=eq.";
                var start = query.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0)
                    throw new InvalidOperationException("Profile request omitted the user id.");
                var value = query[(start + marker.Length)..].Split('&')[0];
                var userId = Guid.Parse(Uri.UnescapeDataString(value));
                if (pendingProfile != userId || !accounts.TryGetValue(userId, out var email))
                    throw new InvalidOperationException("Profile request used another login's identity.");
                pendingProfile = null;
                return ProfileSuccess(userId, email);
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }
    }
}
