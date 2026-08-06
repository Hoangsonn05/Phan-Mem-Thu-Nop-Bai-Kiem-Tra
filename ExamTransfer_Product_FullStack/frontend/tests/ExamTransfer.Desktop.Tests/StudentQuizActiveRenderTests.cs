using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Desktop.Views;
using ExamTransfer.Shared.Contracts;
using System.Windows;
using System.Windows.Controls;
using Xunit;
using Xunit.Abstractions;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentQuizActiveRenderTests
{
    private readonly ITestOutputHelper output;

    public StudentQuizActiveRenderTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task StudentQuizViewModel_WhenLoaded_MaintainsCorrectProgressTextAndCounts()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);
        
        var questions = new List<QuizQuestionDto>();
        for (int i = 1; i <= 50; i++)
        {
            questions.Add(new QuizQuestionDto(
                Guid.NewGuid(),
                $"Question {i}",
                i,
                10,
                false,
                [
                    new QuizChoiceDto(Guid.NewGuid(), "A", 1),
                    new QuizChoiceDto(Guid.NewGuid(), "B", 2)
                ]));
        }

        var attempt = new QuizAttemptDto(
            attemptId,
            sessionId,
            participantId,
            QuizAttemptStatus.InProgress,
            1,
            now.AddMinutes(-10),
            now.AddMinutes(60),
            null,
            null,
            500,
            questions,
            []);

        var state = new StudentSessionState
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            AccessMode = SessionAccessMode.LanOnly,
            AccessToken = "test"
        };
        var api = new RecordingBackendClient(now) { QuizAttemptResponse = attempt };
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        clock.Synchronize(now);
        var ticker = new FakeCountdownTicker();
        using var realtime = new FakeStudentRealtimeService();
        using var viewModel = new StudentQuizViewModel(
            api,
            state,
            clock,
            ticker,
            realtime,
            new FixedStudentExamFlowCoordinator(state, attempt));

        output.WriteLine($"Thread ID before Init: {Environment.CurrentManagedThreadId}");
        await viewModel.InitializeAsync(CancellationToken.None);
        output.WriteLine($"Thread ID after Init: {Environment.CurrentManagedThreadId}");

        Assert.NotNull(viewModel.Attempt);
        Assert.Equal(50, viewModel.Questions.Count);
        output.WriteLine($"ProgressText: {viewModel.ProgressText}");
        output.WriteLine($"UnansweredCount: {viewModel.UnansweredCount}");
        
        // This is expected to fail initially before the fix
        Assert.Equal("Đã trả lời 0/50 câu", viewModel.ProgressText);
        Assert.Equal(50, viewModel.UnansweredCount);
        Assert.True(viewModel.CanEditAnswers);
        Assert.Null(viewModel.Review);
    }

    [Fact]
    public void StudentQuizView_WhenAttemptIsActive_Renders50Questions()
    {
        var sessionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero);
        
        var questions = new List<QuizQuestionDto>();
        for (int i = 1; i <= 50; i++)
        {
            questions.Add(new QuizQuestionDto(
                Guid.NewGuid(),
                $"Question {i}",
                i,
                10,
                false,
                [
                    new QuizChoiceDto(Guid.NewGuid(), "A", 1),
                    new QuizChoiceDto(Guid.NewGuid(), "B", 2)
                ]));
        }

        var attempt = new QuizAttemptDto(
            attemptId,
            sessionId,
            participantId,
            QuizAttemptStatus.InProgress,
            1,
            now.AddMinutes(-10),
            now.AddMinutes(60),
            null,
            null,
            500,
            questions,
            []);

        var state = new StudentSessionState
        {
            SessionId = sessionId,
            ParticipantId = participantId,
            AccessMode = SessionAccessMode.LanOnly,
            AccessToken = "test"
        };
        var api = new RecordingBackendClient(now) { QuizAttemptResponse = attempt };
        var source = new FakeMonotonicTimeSource();
        var clock = new ServerClock(source);
        clock.Synchronize(now);
        var ticker = new FakeCountdownTicker();
        using var realtime = new FakeStudentRealtimeService();
        using var viewModel = new StudentQuizViewModel(
            api,
            state,
            clock,
            ticker,
            realtime,
            new FixedStudentExamFlowCoordinator(state, attempt));

        WpfTestHost.Run(() =>
        {
            var view = new StudentQuizView { DataContext = viewModel };
            
            // Pump the dispatcher so InitializeAsync can run completely
            // WpfTestHost runs this action on the WPF thread
            viewModel.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            
            // Allow layout/bindings to update
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            
            // Find active ItemsControl
            var activeItemsControl = (ItemsControl)LogicalTreeHelper.FindLogicalNode(view, "ActiveQuestionsList");
            Assert.NotNull(activeItemsControl);
            
            // Check that it's rendered the 50 items and visible
            Assert.Equal(50, activeItemsControl.Items.Count);
            Assert.Equal(Visibility.Visible, activeItemsControl.Visibility);
            
            // Check review items control
            var reviewItemsControl = (ItemsControl)LogicalTreeHelper.FindLogicalNode(view, "ReviewQuestionsList");
            Assert.NotNull(reviewItemsControl);
            Assert.Null(reviewItemsControl.ItemsSource); // Review.Questions is null
            Assert.Equal(Visibility.Collapsed, reviewItemsControl.Visibility);
        });
    }
}
