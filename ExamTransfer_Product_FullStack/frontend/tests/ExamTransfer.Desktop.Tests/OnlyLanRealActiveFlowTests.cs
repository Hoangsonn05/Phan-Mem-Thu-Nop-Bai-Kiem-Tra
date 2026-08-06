using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Windows.Controls;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Desktop.Views;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class OnlyLanRealActiveFlowTests
{
    [WpfFact]
    public async Task RealFlow_ActiveQuestionsAreRendered()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        
        var questions = Enumerable.Range(1, 5)
            .Select(i => new QuizQuestionDto(
                Guid.NewGuid(), $"Q{i}", i, 2, false,
                [new QuizChoiceDto(Guid.NewGuid(), "A", 1), new QuizChoiceDto(Guid.NewGuid(), "B", 2)]))
            .ToList();
            
        var sessionDto = new SessionDetailDto(
            new SessionDto(sessionId, "LAN Exam", "Subj", 1, null, "123", SessionStatus.InProgress, SessionAdmissionMode.ClassMembersOnly, now.AddHours(-1), null, 60, ExamDeliveryType.MultipleChoice, SupervisionMode.None, QuizResultPolicy.Hidden, 100),
            []);
            
        var participantDto = new ParticipantDto(participantId, sessionId, "hs", "HS", "Học Sinh", ParticipantStatus.Approved, SubmissionStatus.NotStarted, null, false, now, null, null);
        
        var attemptDto = new QuizAttemptDto(
            attemptId, sessionId, participantId,
            QuizAttemptStatus.InProgress, 1,
            now.AddMinutes(-10), now.AddMinutes(60),
            null, null, 10, questions, []);
            
        var api = new FakeBackendClient();
        api.SessionDetail = sessionDto;
        api.Participant = participantDto;
        api.QuizAttemptLookup = new QuizAttemptLookupResponseDto(attemptDto);
        
        var state = new StudentSessionState { SessionId = sessionId, ParticipantId = participantId, AccessMode = SessionAccessMode.LanOnly, AccessToken = "test" };
        var publicCloud = new SupabasePublicCloudClient(new PublicCloudOptions());
        var coordinator = new StudentExamFlowCoordinator(api, publicCloud, state);
        var ticker = new FakeCountdownTicker();
        var realtime = new FakeStudentRealtimeService();
        var clock = new ServerClock(new FakeMonotonicTimeSource());
        clock.Synchronize(now);
        
        var viewModel = new StudentQuizViewModel(api, state, clock, ticker, realtime, coordinator, new FakeLocalStore([]));
        
        var view = new StudentQuizView();
        view.DataContext = viewModel;
        
        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(view);
        grid.Measure(new System.Windows.Size(1920, 1080));
        grid.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));
        
        await viewModel.InitializeAsync(CancellationToken.None);
        
        await Task.Delay(100);
        grid.UpdateLayout();
        
        var activeList = (ItemsControl)view.FindName("ActiveQuestionsList");
        Assert.NotNull(activeList);
        
        Assert.Equal(5, viewModel.Questions.Count);
        Assert.Equal(5, activeList.Items.Count);
    }
}

internal class FakeBackendClient : IBackendClient
{
    public SessionDetailDto SessionDetail { get; set; }
    public ParticipantDto Participant { get; set; }
    public QuizAttemptLookupResponseDto QuizAttemptLookup { get; set; }
    
    public bool HasTrustedAccountToken => true;
    public string BaseAddress => "http://localhost";
    
    public void SetAccountToken(string? token) {}
    public void SetParticipantToken(string? token) {}
    public void SetBearerToken(string? token) {}
    
    public Task<ApiResponse<TResponse>?> GetAsync<TResponse>(string path, CancellationToken ct = default)
    {
        if (path.Contains("/participants/")) return Task.FromResult<ApiResponse<TResponse>?>(new ApiResponse<TResponse>(true, (TResponse)(object)Participant, null));
        if (path.Contains("/attempt")) return Task.FromResult<ApiResponse<TResponse>?>(new ApiResponse<TResponse>(true, (TResponse)(object)QuizAttemptLookup, null));
        throw new NotImplementedException();
    }
    
    public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        return Task.FromResult<ApiResponse<SessionDetailDto>?>(new ApiResponse<SessionDetailDto>(true, SessionDetail, null));
    }
    
    public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<SystemStatusDto> GetSystemStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<SystemSettingsDto>?> GetSettingsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<PublicCloudStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ApiResponse<StudentSubmissionDto[]>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    
    public Task UploadChunkAsync(string path, Stream chunkStream, long offset, string? expectedHash, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DownloadFileAsync(string path, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DownloadVerifiedFileAsync(string path, string targetPath, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
}
