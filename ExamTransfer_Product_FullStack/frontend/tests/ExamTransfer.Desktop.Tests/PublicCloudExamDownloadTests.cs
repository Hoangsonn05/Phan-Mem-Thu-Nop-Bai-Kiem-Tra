using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class PublicCloudExamDownloadTests
{
    [Fact]
    public async Task ManifestSuccessReturnsFileIdAndVerificationMetadata()
    {
        var fileId = Guid.NewGuid();
        using var http = new HttpClient(new ManifestHandler(fileId));
        var client = Client(http);
        await client.LoginAsync("student", "password", CancellationToken.None);

        var files = await client.ListExamFilesAsync(Guid.NewGuid(), CancellationToken.None);

        var file = Assert.Single(files);
        Assert.Equal(fileId, file.Id);
        Assert.Equal("exam.pdf", file.Name);
        Assert.Equal(12, file.SizeBytes);
        Assert.Equal(new string('a', 64), file.Sha256);
    }

    [Theory]
    [InlineData("{\"error\":\"SIGNED_URL_FAILED\"}", "SIGNED_URL_FAILED")]
    [InlineData("{\"code\":\"PUBLIC_EXAM_METADATA_FAILED\",\"message\":\"ignored\"}", "PUBLIC_EXAM_METADATA_FAILED")]
    [InlineData("not-json", "PUBLICCLOUD_HTTP_502")]
    public async Task StructuredEdgeErrorsPreserveSafeCode(string body, string expectedCode)
    {
        using var http = new HttpClient(new EdgeErrorHandler(body, HttpStatusCode.BadGateway));
        var client = Client(http);
        await client.LoginAsync("student", "password", CancellationToken.None);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.GetExamFileUrlAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
    }

    [Fact]
    public async Task OverlongEdgeErrorFallsBackToHttpCode()
    {
        var body = $"{{\"error\":\"{new string('A', 81)}\"}}";
        using var http = new HttpClient(new EdgeErrorHandler(body, HttpStatusCode.BadGateway));
        var client = Client(http);
        await client.LoginAsync("student", "password", CancellationToken.None);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.GetExamFileUrlAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("PUBLICCLOUD_HTTP_502", error.Code);
    }

    [Fact]
    public async Task DownloadVerifiedAsync_AcceptsMatchingSizeAndHash()
    {
        var bytes = Encoding.UTF8.GetBytes("public cloud exam");
        using var http = new HttpClient(new DownloadHandler(bytes, HttpStatusCode.OK));
        var client = Client(http);
        var root = TemporaryDirectory();
        try
        {
            var destination = Path.Combine(root, "exam.pdf");
            await client.DownloadVerifiedAsync(ExamFile(bytes), destination, CancellationToken.None);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            Assert.False(System.IO.File.Exists(destination + ".partial"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadVerifiedAsync_RejectsSizeAndHashMismatch()
    {
        var bytes = Encoding.UTF8.GetBytes("public cloud exam");
        var root = TemporaryDirectory();
        try
        {
            using (var sizeHttp = new HttpClient(new DownloadHandler(bytes, HttpStatusCode.OK)))
            {
                var sizeClient = Client(sizeHttp);
                var wrongSize = ExamFile(bytes) with { SizeBytes = bytes.Length + 1 };
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    sizeClient.DownloadVerifiedAsync(
                        wrongSize,
                        Path.Combine(root, "size.pdf"),
                        CancellationToken.None));
            }

            using var hashHttp = new HttpClient(new DownloadHandler(bytes, HttpStatusCode.OK));
            var hashClient = Client(hashHttp);
            var wrongHash = ExamFile(bytes) with { Sha256 = new string('0', 64) };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                hashClient.DownloadVerifiedAsync(
                    wrongHash,
                    Path.Combine(root, "hash.pdf"),
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadVerifiedAsync_Server200ResetsExistingPartial()
    {
        var bytes = Encoding.UTF8.GetBytes("complete exam payload");
        var handler = new DownloadHandler(bytes, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = Client(http);
        var root = TemporaryDirectory();
        try
        {
            var destination = Path.Combine(root, "exam.pdf");
            await File.WriteAllBytesAsync(destination + ".partial", Encoding.UTF8.GetBytes("stale"));

            await client.DownloadVerifiedAsync(ExamFile(bytes), destination, CancellationToken.None);

            Assert.Equal(5, handler.RangeOffset);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadVerifiedAsync_Server206AppendsExistingPartial()
    {
        var bytes = Encoding.UTF8.GetBytes("complete exam payload");
        const int offset = 8;
        var handler = new DownloadHandler(bytes[offset..], HttpStatusCode.PartialContent);
        using var http = new HttpClient(handler);
        var client = Client(http);
        var root = TemporaryDirectory();
        try
        {
            var destination = Path.Combine(root, "exam.pdf");
            await File.WriteAllBytesAsync(destination + ".partial", bytes[..offset]);

            await client.DownloadVerifiedAsync(ExamFile(bytes), destination, CancellationToken.None);

            Assert.Equal(offset, handler.RangeOffset);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SignedUrlDownloadFailureUsesStructuredSafeCode()
    {
        using var http = new HttpClient(new DownloadHandler([], HttpStatusCode.NotFound));
        var client = Client(http);
        var root = TemporaryDirectory();
        try
        {
            var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
                client.DownloadVerifiedAsync(
                    new PublicExamFileUrl(new Uri("https://project.supabase.test/storage/v1/object/sign/redacted"), 180, "exam.pdf", 1, new string('0', 64)),
                    Path.Combine(root, "exam.pdf"),
                    CancellationToken.None));

            Assert.Equal("SIGNED_URL_DOWNLOAD_FAILED", error.Code);
            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
            Assert.DoesNotContain("storage/v1", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProductPageDisplaysPublicCloudCodeStatusAndTraceWithoutSecretResponse()
    {
        using var page = new FailurePage();
        await page.FailAsync(new PublicCloudApiException(
            "STORAGE_SIGN_FAILED",
            "https://project.supabase.test/object?token=secret",
            HttpStatusCode.BadGateway));

        Assert.Contains("STORAGE_SIGN_FAILED", page.Status, StringComparison.Ordinal);
        Assert.Contains("HTTP 502", page.Status, StringComparison.Ordinal);
        Assert.Contains("Mã tra cứu", page.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", page.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", page.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductPageKeepsOnlyLanBackendErrorDisplay()
    {
        using var page = new FailurePage();
        await page.FailAsync(new BackendApiException(
            "ONLYLAN_ERROR",
            "OnlyLAN message",
            "backend-trace",
            null,
            null,
            409,
            "/api/v1/test"));

        Assert.Contains("OnlyLAN message", page.Status, StringComparison.Ordinal);
        Assert.Contains("ONLYLAN_ERROR", page.Status, StringComparison.Ordinal);
        Assert.Contains("HTTP 409", page.Status, StringComparison.Ordinal);
        Assert.Contains("backend-trace", page.Status, StringComparison.Ordinal);
    }

    private static SupabasePublicCloudClient Client(HttpClient http) =>
        new(http, supabaseUrl: "https://project.supabase.test", publishableKey: "publishable-key");

    private static PublicExamFileUrl ExamFile(byte[] bytes) =>
        new(
            new Uri("https://project.supabase.test/storage/v1/object/sign/redacted"),
            180,
            "exam.pdf",
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ExamTransfer.PublicCloudExamDownload.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class EdgeErrorHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/auth/v1/token")
            {
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}"));
            }
            return Task.FromResult(Json(status, body));
        }
    }

    private sealed class ManifestHandler(Guid fileId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/auth/v1/token")
            {
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}"));
            }

            var body = $"[{{\"id\":\"{fileId:D}\",\"name\":\"exam.pdf\",\"size_bytes\":12,\"sha256\":\"{new string('a', 64)}\",\"mime_type\":\"application/pdf\"}}]";
            return Task.FromResult(Json(HttpStatusCode.OK, body));
        }
    }

    private sealed class DownloadHandler(byte[] bytes, HttpStatusCode status) : HttpMessageHandler
    {
        public long? RangeOffset { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RangeOffset = request.Headers.Range?.Ranges.SingleOrDefault()?.From;
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(bytes),
                RequestMessage = request
            };
            if (status == HttpStatusCode.PartialContent)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    RangeOffset ?? 0,
                    (RangeOffset ?? 0) + bytes.Length - 1,
                    (RangeOffset ?? 0) + bytes.Length);
            }
            return Task.FromResult(response);
        }
    }

    private sealed class FailurePage : ProductPageBase
    {
        public Task FailAsync(Exception error) =>
            RunAsync("working", "success", _ => Task.FromException(error));

        protected override Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
