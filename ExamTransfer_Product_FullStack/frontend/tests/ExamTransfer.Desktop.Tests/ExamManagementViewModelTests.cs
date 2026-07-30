using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class ExamManagementViewModelTests
{
    [Fact]
    public void MultipleChoiceClone_CallsCloneApi_AndPublishedDurationIsNotEditable()
    {
        var sourceId = Guid.NewGuid();
        var cloneId = Guid.NewGuid();
        var rule = new FileRuleDto([".docx"], 1024, 2048, 1, false, false);
        var source = new ExamSummaryDto(
            sourceId,
            null,
            "Quiz source",
            "Math",
            45,
            ExamDeliveryType.MultipleChoice,
            ExamStatus.Draft,
            1,
            0,
            "source-rv",
            QuizResultPolicy.Hidden,
            SupervisionMode.Standard,
            true,
            1);
        var cloneSummary = source with
        {
            Id = cloneId,
            Title = "Quiz source - Bản sao",
            RowVersion = "clone-rv"
        };
        var cloneDetail = new ExamDetailDto(
            cloneId,
            null,
            cloneSummary.Title,
            cloneSummary.Subject,
            null,
            cloneSummary.DurationMinutes,
            cloneSummary.DeliveryType,
            ExamStatus.Draft,
            1,
            rule,
            [],
            cloneSummary.RowVersion,
            cloneSummary.QuizResultPolicy,
            cloneSummary.SupervisionMode,
            null,
            1);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamSummaryResponse = cloneSummary,
            ExamDetailResponse = cloneDetail
        };
        using var viewModel = new ExamManagementViewModel(api)
        {
            SelectedExam = new SelectableExamRow(source),
            DeliveryType = ExamDeliveryType.MultipleChoice
        };

        Assert.True(viewModel.CloneCommand.CanExecute(null));
        viewModel.CloneCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains($"api/v1/exams/{sourceId}/clone"),
            TimeSpan.FromSeconds(2)));

        viewModel.SelectedExam = new SelectableExamRow(
            source with { Status = ExamStatus.Published });
        Assert.False(viewModel.IsPolicyEditable);
    }

    [Fact]
    public async Task LegacyClassBoundEdit_PreservesClass_ButNewExamIsClassless()
    {
        var classId = Guid.NewGuid();
        var examId = Guid.NewGuid();
        var rule = new FileRuleDto([".pdf"], 1024, 2048, 1, false, false);
        var summary = new ExamSummaryDto(
            examId,
            classId,
            "Legacy",
            "Math",
            45,
            ExamDeliveryType.FileSubmission,
            ExamStatus.Draft,
            1,
            0,
            "rv-1");
        var detail = new ExamDetailDto(
            examId,
            classId,
            summary.Title,
            summary.Subject,
            null,
            45,
            summary.DeliveryType,
            summary.Status,
            1,
            rule,
            [],
            summary.RowVersion);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [summary],
            ExamDetailResponse = detail
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.True(viewModel.IsCreatingNew);
        Assert.Null(viewModel.SelectedExam);
        viewModel.SelectedExam = viewModel.Exams.Single();
        await viewModel.LoadSelectedExamAsync();
        Assert.True(viewModel.IsEditingExisting);

        viewModel.Title = "Legacy updated";
        viewModel.SaveCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PutPaths.Contains($"api/v1/exams/{examId}"),
            TimeSpan.FromSeconds(2)));
        Assert.Equal(classId, Assert.IsType<UpdateExamRequest>(api.PutRequests[0]).ClassId);

        viewModel.NewExamCommand.Execute(null);
        Assert.True(viewModel.IsCreatingNew);
        Assert.Null(viewModel.SelectedExam);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        viewModel.Title = "New classless exam";
        viewModel.Subject = "Math";
        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains("api/v1/exams"),
            TimeSpan.FromSeconds(2)));
        var create = Assert.IsType<CreateExamRequest>(
            api.PostRequests.First(request => request is CreateExamRequest));
        Assert.Null(create.ClassId);
    }

    [Fact]
    public async Task RefreshInCreateMode_DoesNotSelectFirstExamOrOverwriteDraft()
    {
        var first = MakeExamSummary(Guid.NewGuid(), "Existing");
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [first]
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.Title = "Unsaved draft";
        viewModel.Subject = "Physics";

        viewModel.RefreshCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(2)));

        Assert.True(viewModel.IsCreatingNew);
        Assert.Null(viewModel.SelectedExam);
        Assert.Equal("Unsaved draft", viewModel.Title);
        Assert.Equal("Physics", viewModel.Subject);
        Assert.False(viewModel.ImportQuizCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExamBulkSelection_SelectAllCountsAndRefreshClearsChecks()
    {
        var first = MakeExamSummary(Guid.NewGuid(), "First");
        var second = MakeExamSummary(Guid.NewGuid(), "Second");
        var detail = new ExamDetailDto(
            first.Id,
            null,
            first.Title,
            first.Subject,
            null,
            first.DurationMinutes,
            first.DeliveryType,
            first.Status,
            first.Version,
            new FileRuleDto([".pdf"], 1024, 2048, 1, false, false),
            [],
            first.RowVersion);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [first, second],
            ExamDetailResponse = detail
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.False(viewModel.BulkArchiveCommand.CanExecute(null));
        viewModel.ToggleAllVisibleArchiveSelectionCommand.Execute(null);
        Assert.Equal(2, viewModel.SelectedArchiveCount);
        Assert.True(viewModel.BulkArchiveCommand.CanExecute(null));

        viewModel.Exams[0].IsChecked = false;
        Assert.Equal(1, viewModel.SelectedArchiveCount);
        viewModel.RefreshCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => viewModel.SelectedArchiveCount == 0 && !viewModel.IsBusy,
            TimeSpan.FromSeconds(2)));
        Assert.False(viewModel.BulkArchiveCommand.CanExecute(null));
    }

    private static ExamSummaryDto MakeExamSummary(Guid id, string title) => new(
        id,
        null,
        title,
        "Math",
        45,
        ExamDeliveryType.FileSubmission,
        ExamStatus.Draft,
        1,
        0,
        "rv-" + id);
}
