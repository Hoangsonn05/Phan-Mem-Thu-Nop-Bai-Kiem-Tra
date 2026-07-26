using System.IO;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class DashboardCountdownTests
{
    [Fact]
    public async Task TickUpdatesObservableTimeWithoutNetworkAndDisposeStopsCallbacks()
    {
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        var ticker = new FakeCountdownTicker();
        var serverUtc = new DateTimeOffset(2026, 7, 25, 5, 0, 0, TimeSpan.Zero);
        var api = new RecordingBackendClient(serverUtc);
        var viewModel = new DashboardViewModel(api, clock, ticker);

        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.Equal("00:01:40", viewModel.ActiveSession?.TimeLeft);
        Assert.Equal(1, api.DashboardRequests);

        source.Advance(TimeSpan.FromSeconds(10));
        ticker.Fire();

        Assert.Equal("00:01:30", viewModel.ActiveSession?.TimeLeft);
        Assert.Equal(1, api.DashboardRequests);

        viewModel.Dispose();
        var afterDispose = viewModel.ActiveSession?.TimeLeft;
        source.Advance(TimeSpan.FromSeconds(10));
        ticker.Fire();

        Assert.True(ticker.Disposed);
        Assert.Equal(afterDispose, viewModel.ActiveSession?.TimeLeft);
        Assert.Equal(1, api.DashboardRequests);
    }
}

internal sealed class FakeCountdownTicker : ICountdownTicker
{
    private EventHandler? tick;

    public event EventHandler? Tick
    {
        add => tick += value;
        remove => tick -= value;
    }

    public bool IsRunning { get; private set; }
    public bool Disposed { get; private set; }

    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;
    public void Fire() => tick?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        Disposed = true;
        IsRunning = false;
        tick = null;
    }
}

internal sealed class RecordingBackendClient(DateTimeOffset serverUtc) : IBackendClient
{
    public int DashboardRequests { get; private set; }
    public QuizAttemptDto? QuizAttemptResponse { get; init; }
    public ExamSummaryDto? ExamSummaryResponse { get; init; }
    public ExamDetailDto? ExamDetailResponse { get; init; }
    public List<string> PostPaths { get; } = [];
    public Uri BaseAddress { get; } = new("https://localhost/");
    public bool HasTrustedAccountToken => false;

    public Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(CancellationToken ct = default)
    {
        DashboardRequests++;
        var session = new SessionSummaryDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Kỳ thi",
            "ROOM",
            SessionStatus.InProgress,
            serverUtc,
            serverUtc.AddMinutes(-1),
            null,
            serverUtc.AddSeconds(100),
            new SessionCountsDto(10, 0, 10, 8, 2, 0, 0),
            1,
            "v1");
        return Task.FromResult<ApiResponse<DashboardSummaryDto>?>(ApiResponse<DashboardSummaryDto>.Ok(
            new DashboardSummaryDto(1, 1, 1, 0, 0, [session], []),
            "test"));
    }

    public bool TrySetBaseAddress(string hostOrUrl, int port, out string? error) { error = null; return true; }
    public Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SystemStatusDto>?>(null);
    public Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(CancellationToken ct = default) =>
        Task.FromResult<ApiResponse<PagedResult<ClassSummaryDto>>?>(
            ExamSummaryResponse is null
                ? null
                : ApiResponse<PagedResult<ClassSummaryDto>>.Ok(new([], 1, 50, 0), "test"));
    public Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(CancellationToken ct = default) =>
        Task.FromResult<ApiResponse<PagedResult<ExamSummaryDto>>?>(
            ExamSummaryResponse is null
                ? null
                : ApiResponse<PagedResult<ExamSummaryDto>>.Ok(new([ExamSummaryResponse], 1, 50, 1), "test"));
    public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<SessionSummaryDto>>?>(null);
    public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ApiResponse<SessionDetailDto>?>(null);
    public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult<ApiResponse<PagedResult<SubmissionSummaryDto>>?>(null);
    public Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<CloudSyncStatusDto>?>(null);
    public Task<ApiResponse<SettingsDto>?> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult<ApiResponse<SettingsDto>?>(null);
    public Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        if (ExamDetailResponse is not null && typeof(T) == typeof(ExamDetailDto))
            return Task.FromResult<ApiResponse<T>?>(
                ApiResponse<T>.Ok((T)(object)ExamDetailResponse, "test"));
        return Task.FromResult<ApiResponse<T>?>(null);
    }
    public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        PostPaths.Add(path);
        if (ExamDetailResponse is not null && typeof(TResponse) == typeof(ExamDetailDto))
            return Task.FromResult<ApiResponse<TResponse>?>(
                ApiResponse<TResponse>.Ok((TResponse)(object)ExamDetailResponse, "test"));
        if (QuizAttemptResponse is not null && typeof(TResponse) == typeof(QuizAttemptDto))
            return Task.FromResult<ApiResponse<TResponse>?>(
                ApiResponse<TResponse>.Ok((TResponse)(object)QuizAttemptResponse, "test"));
        return Task.FromResult<ApiResponse<TResponse>?>(null);
    }
    public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
    public Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default) => Task.FromResult<ApiResponse<TResponse>?>(null);
    public Task<ApiResponse<object>?> UploadChunkAsync(string path, Stream content, long contentLength, string? sha256 = null, CancellationToken ct = default) => Task.FromResult<ApiResponse<object>?>(null);
    public Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task DownloadVerifiedFileAsync(string path, string destinationPath, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    public void SetBearerToken(string? token) { }
    public void SetAccountToken(string? token) { }
    public void SetParticipantToken(string? token) { }
}
