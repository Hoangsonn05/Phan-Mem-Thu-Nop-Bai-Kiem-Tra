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
            SelectedExam = source,
            DeliveryType = ExamDeliveryType.MultipleChoice
        };

        Assert.True(viewModel.CloneCommand.CanExecute(null));
        viewModel.CloneCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains($"api/v1/exams/{sourceId}/clone"),
            TimeSpan.FromSeconds(2)));

        viewModel.SelectedExam = source with { Status = ExamStatus.Published };
        Assert.False(viewModel.IsPolicyEditable);
    }
}
