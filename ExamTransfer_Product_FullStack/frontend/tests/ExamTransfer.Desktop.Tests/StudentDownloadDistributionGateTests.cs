using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentDownloadDistributionGateTests
{
    [Fact]
    public async Task DirectNavigationWhileWaiting_DoesNotRequestManifest()
    {
        var state = State(SessionStatus.Waiting);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow);
        var flow = new FixedExamFlow(new(
            StudentExamFlowState.ApprovedWaiting,
            "S-03",
            false,
            "Phiên thi chưa bắt đầu."));
        using var viewModel = new StudentDownloadViewModel(api, state, flow);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, flow.ResolveCalls);
        Assert.DoesNotContain(api.GetPaths, x => x.Contains("/manifest", StringComparison.Ordinal));
        Assert.Empty(viewModel.Files);
        Assert.Equal("warning", viewModel.StatusTone);
    }

    [Fact]
    public async Task Distributing_RefreshesAuthorityBeforeRequestingManifest()
    {
        var state = State(SessionStatus.Distributing);
        var file = new FileDescriptorDto(
            Guid.NewGuid(),
            "exam.pdf",
            4,
            new string('a', 64),
            "application/pdf");
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamManifestResponse = new(
                state.ExamId!.Value,
                1,
                DateTimeOffset.UtcNow,
                [file])
        };
        var flow = new FixedExamFlow(new(
            StudentExamFlowState.ReadyToStartFileExam,
            "S-05",
            false,
            "Mở luồng nhận đề tự luận."));
        using var viewModel = new StudentDownloadViewModel(api, state, flow);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, flow.ResolveCalls);
        Assert.Single(api.GetPaths, x => x.EndsWith("/manifest", StringComparison.Ordinal));
        Assert.Equal(file, Assert.Single(viewModel.Files));
    }

    private static StudentSessionState State(SessionStatus status) => new()
    {
        SessionId = Guid.NewGuid(),
        ParticipantId = Guid.NewGuid(),
        ExamId = Guid.NewGuid(),
        AccessToken = "participant-token",
        AccessMode = SessionAccessMode.LanOnly,
        SessionStatus = status,
        ParticipantStatus = ParticipantStatus.Approved,
        DeliveryType = ExamDeliveryType.FileSubmission
    };

    private sealed class FixedExamFlow(StudentExamFlowResolution resolution) : IStudentExamFlowCoordinator
    {
        public int ResolveCalls { get; private set; }
        public event EventHandler<StudentExamNavigationRequest>? NavigationRequested
        {
            add { }
            remove { }
        }

        public Task<StudentExamFlowResolution> ResolveAsync(
            StudentExamEntryPoint entryPoint,
            bool startConfirmed,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult(resolution);
        }

        public Task<StudentJoinOutcome> SynchronizeAfterJoinAsync(
            IStudentRealtimeService realtime,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void ReturnToCurrentExam() { }
    }
}
