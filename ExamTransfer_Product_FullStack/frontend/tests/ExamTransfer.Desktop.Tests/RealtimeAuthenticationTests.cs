using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class RealtimeAuthenticationTests
{
    [Fact]
    public async Task ParticipantRealtimeConnection_UsesExamSessionHeaderNotBearer()
    {
        const string participantToken = "participant-transport-token";
        await using var server = new NegotiateCaptureServer();
        var options = UnconfiguredPublicCloudOptions();
        await using var publicRealtime = new SupabaseRealtimeService(options);
        var state = new StudentSessionState
        {
            SessionId = Guid.NewGuid(),
            ParticipantId = Guid.NewGuid(),
            AccessMode = SessionAccessMode.LanOnly,
            AccessToken = participantToken
        };
        using var realtime = new StudentRealtimeService(
            new BackendClient(server.BaseUrl),
            state,
            publicRealtime,
            new SupabasePublicCloudClient(optionsProvider: options));

        var connectTask = realtime.StartAsync();
        var request = await server.CaptureAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await connectTask);

        Assert.StartsWith($"{ContractInfo.HubPath}/negotiate", request.Target, StringComparison.Ordinal);
        Assert.Equal(participantToken, request.Headers["X-Exam-Session-Token"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain(participantToken, request.Target, StringComparison.Ordinal);
        Assert.DoesNotContain(participantToken, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountRealtimeConnection_KeepsAuthorizationBearer()
    {
        const string accountToken = "account-transport-token";
        await using var server = new NegotiateCaptureServer();
        await using var realtime = new RealtimeService(server.BaseUrl);
        var binding = new TeacherRealtimeSessionBinding(realtime);

        var connectTask = binding.EnsureAsync(accountToken, null, CancellationToken.None);
        var request = await server.CaptureAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await connectTask);

        Assert.StartsWith($"{ContractInfo.HubPath}/negotiate", request.Target, StringComparison.Ordinal);
        Assert.Equal($"Bearer {accountToken}", request.Headers["Authorization"]);
        Assert.False(request.Headers.ContainsKey("X-Exam-Session-Token"));
        Assert.DoesNotContain(accountToken, request.Target, StringComparison.Ordinal);
        Assert.DoesNotContain(accountToken, error.ToString(), StringComparison.Ordinal);
    }

    private static FixedPublicCloudRuntimeOptionsProvider UnconfiguredPublicCloudOptions() =>
        new(new PublicCloudRuntimeOptions(
            null,
            null,
            "TEST_NOT_CONFIGURED",
            "Test"));

    private sealed class NegotiateCaptureServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);

        public NegotiateCaptureServer()
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            BaseUrl = $"http://127.0.0.1:{endpoint.Port}";
        }

        public string BaseUrl { get; }

        public async Task<CapturedRequest> CaptureAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException("Missing HTTP request line.");
            var requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length < 2)
                throw new InvalidOperationException($"Invalid HTTP request line: {requestLine}");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await reader.ReadLineAsync(timeout.Token);
                if (string.IsNullOrEmpty(line))
                    break;
                var separator = line.IndexOf(':');
                if (separator > 0)
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            return new CapturedRequest(requestParts[1], headers);
        }

        public ValueTask DisposeAsync()
        {
            listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record CapturedRequest(
        string Target,
        IReadOnlyDictionary<string, string> Headers);
}
