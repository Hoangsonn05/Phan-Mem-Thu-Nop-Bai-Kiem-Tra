using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class ExamManagementViewModelTests
{
    [Fact]
    public void QuizImport_CommittedStateReplacesPreviewWithAuthoritativeSummary()
    {
        var state = new QuizImportViewState
        {
            SelectedFileName = "C:\\temporary\\preview.docx",
            Preview = new QuizImportPreviewDto(
                "preview-token",
                "preview.docx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                "preview-hash",
                50,
                10.00m,
                [],
                [],
                [],
                DateTimeOffset.UtcNow.AddMinutes(20),
                false)
        };
        var source = new QuizImportSourceDto(
            Guid.NewGuid(),
            "committed.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            1024,
            "committed-hash",
            3,
            "Committed",
            DateTimeOffset.UtcNow);

        state.SetCommitted(source, 50, 10.00m);

        Assert.False(state.HasPreview);
        Assert.Equal(string.Empty, state.SelectedFileName);
        Assert.True(state.HasCommittedSource);
        Assert.Equal(source, state.CommittedSource);
        Assert.Equal(50, state.CommittedQuestionCount);
        Assert.Equal(10.00m, state.CommittedMaxScore);
        Assert.Contains("committed.docx", state.Summary, StringComparison.Ordinal);
        Assert.Contains("50", state.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Chưa có bản xem trước", state.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuizImport_CommittedSummarySurvivesRefreshAndMetadataSave()
    {
        var examId = Guid.NewGuid();
        var source = new QuizImportSourceDto(
            Guid.NewGuid(),
            "persisted.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            2048,
            "persisted-hash",
            1,
            "Committed",
            DateTimeOffset.UtcNow);
        var summary = new ExamSummaryDto(
            examId,
            null,
            "Quiz persisted",
            "Math",
            45,
            ExamDeliveryType.MultipleChoice,
            ExamStatus.Draft,
            1,
            0,
            "quiz-row-version",
            QuizResultPolicy.Hidden,
            SupervisionMode.Standard,
            true,
            50);
        var detail = new ExamDetailDto(
            examId,
            null,
            summary.Title,
            summary.Subject,
            null,
            summary.DurationMinutes,
            summary.DeliveryType,
            summary.Status,
            summary.Version,
            new FileRuleDto([".docx"], 1024, 2048, 1, false, false),
            [],
            summary.RowVersion,
            summary.QuizResultPolicy,
            summary.SupervisionMode,
            source,
            50,
            10.00m);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [summary],
            ExamDetailResponse = detail
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedExam = viewModel.Exams.Single();
        await viewModel.LoadSelectedExamAsync();

        Assert.True(viewModel.QuizImport.HasCommittedSource);
        Assert.Contains("persisted.docx", viewModel.QuizImport.Summary, StringComparison.Ordinal);
        Assert.True(viewModel.CanPublish);

        viewModel.RefreshCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(2)));
        Assert.True(viewModel.QuizImport.HasCommittedSource);
        Assert.Equal(50, viewModel.QuizImport.CommittedQuestionCount);
        Assert.Equal(10.00m, viewModel.QuizImport.CommittedMaxScore);

        viewModel.Title = "Quiz metadata updated";
        viewModel.SaveCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PutPaths.Contains($"api/v1/exams/{examId}") && !viewModel.IsBusy,
            TimeSpan.FromSeconds(2)));
        var request = Assert.IsType<UpdateExamRequest>(api.PutRequests.Single());
        Assert.Equal(ExamDeliveryType.MultipleChoice, request.DeliveryType);
        Assert.Equal("quiz-row-version", request.RowVersion);
        Assert.True(viewModel.QuizImport.HasCommittedSource);
        Assert.Equal(50, viewModel.QuizImport.CommittedQuestionCount);
    }

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
