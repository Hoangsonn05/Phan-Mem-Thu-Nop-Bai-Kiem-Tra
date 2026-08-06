using System.IO;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class ExamManagementViewModelTests
{
    [Fact]
    public void SelectableExamRow_FileSubmission_SummarizesFileCount()
    {
        var row = new SelectableExamRow(MakeExamSummary(
            Guid.NewGuid(),
            "File exam",
            fileCount: 2));

        Assert.Equal(ExamDeliveryType.FileSubmission, row.DeliveryType);
        Assert.Equal(2, row.FileCount);
        Assert.Equal("2 file", row.ContentSummaryText);
    }

    [Fact]
    public void SelectableExamRow_CommittedMultipleChoice_SummarizesQuestionCountInsteadOfFiles()
    {
        var row = new SelectableExamRow(MakeExamSummary(
            Guid.NewGuid(),
            "Quiz exam",
            deliveryType: ExamDeliveryType.MultipleChoice,
            fileCount: 0,
            hasCommittedQuizSource: true,
            quizQuestionCount: 50));

        Assert.Equal(ExamDeliveryType.MultipleChoice, row.DeliveryType);
        Assert.Equal(0, row.FileCount);
        Assert.True(row.HasCommittedQuizSource);
        Assert.Equal(50, row.QuizQuestionCount);
        Assert.Equal("50 câu", row.ContentSummaryText);
    }

    [Fact]
    public void SelectableExamRow_EmptyMultipleChoice_SummarizesZeroQuestions()
    {
        var row = new SelectableExamRow(MakeExamSummary(
            Guid.NewGuid(),
            "Empty quiz",
            deliveryType: ExamDeliveryType.MultipleChoice,
            hasCommittedQuizSource: false,
            quizQuestionCount: 0));

        Assert.False(row.HasCommittedQuizSource);
        Assert.Equal(0, row.QuizQuestionCount);
        Assert.Equal("0 câu", row.ContentSummaryText);
    }

    [Fact]
    public async Task RefreshExams_PreservesQuizContentSummary()
    {
        var summary = MakeExamSummary(
            Guid.NewGuid(),
            "Refresh quiz",
            deliveryType: ExamDeliveryType.MultipleChoice,
            hasCommittedQuizSource: true,
            quizQuestionCount: 50);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [summary]
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.Equal("50 câu", Assert.Single(viewModel.Exams).ContentSummaryText);

        viewModel.RefreshCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(2)));

        Assert.Equal("50 câu", Assert.Single(viewModel.Exams).ContentSummaryText);
    }

    [Fact]
    public void SelectableExamRow_PreservesIdentityArchiveAndSelectionBehavior()
    {
        var id = Guid.NewGuid();
        var row = new SelectableExamRow(MakeExamSummary(id, "Selection exam"));
        var selectionChanges = 0;
        row.SelectionChanged += (_, _) => selectionChanges++;

        row.IsChecked = true;

        Assert.Equal(id, row.Id);
        Assert.Equal("Selection exam", row.Title);
        Assert.Equal(ExamStatus.Draft, row.Status);
        Assert.True(row.CanArchive);
        Assert.True(row.IsChecked);
        Assert.Equal(1, selectionChanges);

        var archived = new SelectableExamRow(MakeExamSummary(
            Guid.NewGuid(),
            "Archived exam",
            status: ExamStatus.Archived));
        archived.IsChecked = true;
        Assert.False(archived.CanArchive);
        Assert.False(archived.IsChecked);
    }

    [Fact]
    public void ExamManagementView_UsesContentSummaryColumn()
    {
        var source = File.ReadAllText(FindExamManagementView());

        Assert.Contains("Header=\"NỘI DUNG\"", source, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding ContentSummaryText}\"", source, StringComparison.Ordinal);
        Assert.Contains("Đảo thứ tự câu hỏi và đáp án cho từng học sinh", source, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding QuizShuffleEnabled, Mode=TwoWay}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"FILE\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding=\"{Binding FileCount}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QuizImport_CommittedStateReplacesPreviewWithAuthoritativeSummary()
    {
        var previewQuestions = new[]
        {
            new QuizAuthoringQuestionDto(
                Guid.NewGuid(),
                "Preview question 1",
                1,
                4.00m,
                false,
                [new QuizAuthoringChoiceDto(Guid.NewGuid(), "Choice A", 1, true)]),
            new QuizAuthoringQuestionDto(
                Guid.NewGuid(),
                "Preview question 2",
                2,
                6.00m,
                false,
                [new QuizAuthoringChoiceDto(Guid.NewGuid(), "Choice B", 1, true)])
        };
        var state = new QuizImportViewState
        {
            SelectedFileName = "C:\\temporary\\preview.docx",
            Preview = new QuizImportPreviewDto(
                "preview-token",
                "preview.docx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                "preview-hash",
                2,
                10.00m,
                previewQuestions,
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
        var committedQuestions = new[]
        {
            new QuizAuthoringQuestionDto(
                Guid.NewGuid(),
                "Committed question 1",
                1,
                4.00m,
                false,
                [
                    new QuizAuthoringChoiceDto(Guid.NewGuid(), "Correct", 1, true),
                    new QuizAuthoringChoiceDto(Guid.NewGuid(), "Wrong", 2, false)
                ]),
            new QuizAuthoringQuestionDto(
                Guid.NewGuid(),
                "Committed question 2",
                2,
                6.00m,
                false,
                [new QuizAuthoringChoiceDto(Guid.NewGuid(), "Answer", 1, true)])
        };

        state.SetCommitted(source, 3, 2, 10.00m, committedQuestions);

        Assert.False(state.HasPreview);
        Assert.Equal(string.Empty, state.SelectedFileName);
        Assert.True(state.HasCommittedSource);
        Assert.Equal(source, state.CommittedSource);
        Assert.Equal(2, state.CommittedQuestionCount);
        Assert.Equal(10.00m, state.CommittedMaxScore);
        Assert.Equal(2, state.Questions.Count);
        Assert.Equal("Committed question 1", state.Questions[0].Text);
        Assert.True(state.Questions[0].Choices[0].IsCorrect);
        Assert.Empty(state.PreviewQuestions);
        Assert.Equal(2, state.CommittedQuestions.Count);
        Assert.Contains("committed.docx", state.Summary, StringComparison.Ordinal);
        Assert.Contains("2", state.Summary, StringComparison.Ordinal);
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
            2,
            true);
        var committedQuestions = new[]
        {
            new QuizAuthoringQuestionDto(
                Guid.NewGuid(),
                "Persisted question 1",
                1,
                4.00m,
                false,
                [new QuizAuthoringChoiceDto(Guid.NewGuid(), "Đúng", 1, true)]),
            new QuizAuthoringQuestionDto(
                Guid.NewGuid(),
                "Persisted question 2",
                2,
                6.00m,
                false,
                [new QuizAuthoringChoiceDto(Guid.NewGuid(), "Sai", 1, false)])
        };
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
            2,
            10.00m,
            true)
        {
            QuizQuestions = committedQuestions
        };
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
        Assert.True(viewModel.QuizShuffleEnabled);
        Assert.True(viewModel.IsQuizShuffleEditable);
        Assert.Contains("persisted.docx", viewModel.QuizImport.Summary, StringComparison.Ordinal);
        Assert.Equal(2, viewModel.QuizImport.Questions.Count);
        Assert.Equal("Persisted question 1", viewModel.QuizImport.Questions[0].Text);
        Assert.True(viewModel.CanPublish);

        viewModel.RefreshCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => !viewModel.IsBusy, TimeSpan.FromSeconds(2)));
        Assert.True(viewModel.QuizImport.HasCommittedSource);
        Assert.Equal(2, viewModel.QuizImport.CommittedQuestionCount);
        Assert.Equal(10.00m, viewModel.QuizImport.CommittedMaxScore);
        Assert.Equal(2, viewModel.QuizImport.Questions.Count);

        viewModel.Title = "Quiz metadata updated";
        viewModel.SaveCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PutPaths.Contains($"api/v1/exams/{examId}") && !viewModel.IsBusy,
            TimeSpan.FromSeconds(2)));
        var request = Assert.IsType<UpdateExamRequest>(api.PutRequests.Single());
        Assert.Equal(ExamDeliveryType.MultipleChoice, request.DeliveryType);
        Assert.True(request.QuizShuffleEnabled);
        Assert.Equal("quiz-row-version", request.RowVersion);
        Assert.True(viewModel.QuizImport.HasCommittedSource);
        Assert.Equal(2, viewModel.QuizImport.CommittedQuestionCount);
    }

    [Fact]
    public async Task QuizImport_CommitFailureKeepsPreviewQuestions()
    {
        var summary = MakeExamSummary(
            Guid.NewGuid(),
            "Commit failure",
            deliveryType: ExamDeliveryType.MultipleChoice);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [summary],
            ExamDetailResponse = QuizDetail(summary, null, [])
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedExam = viewModel.Exams.Single();
        await viewModel.LoadSelectedExamAsync();
        viewModel.QuizImport.Preview = Preview([AuthoringQuestion("Unsaved preview", 1, 10.00m)]);

        viewModel.CommitQuizCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains($"api/v1/exams/{summary.Id}/quiz-import/commit") && !viewModel.IsBusy,
            TimeSpan.FromSeconds(2)));

        Assert.True(viewModel.QuizImport.HasPreview);
        Assert.False(viewModel.QuizImport.HasCommittedSource);
        Assert.Single(viewModel.QuizImport.Questions);
        Assert.Equal("Unsaved preview", viewModel.QuizImport.Questions[0].Text);
        Assert.Equal("danger", viewModel.StatusTone);
    }

    [Fact]
    public async Task QuizImport_CommitSuccessUsesAuthoritativeGraphBeforeRefresh()
    {
        var questions = new[]
        {
            AuthoringQuestion("Committed first", 1, 4.00m),
            AuthoringQuestion("Committed second", 2, 6.00m)
        };
        var source = QuizSource("committed.docx", 1);
        var summary = MakeExamSummary(
            Guid.NewGuid(),
            "Commit success",
            deliveryType: ExamDeliveryType.MultipleChoice,
            hasCommittedQuizSource: true,
            quizQuestionCount: 2);
        var detail = QuizDetail(summary, source, questions);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [summary],
            ExamDetailResponse = detail,
            QuizImportResultResponse = new QuizImportResultDto(
                summary.Id,
                summary.Version,
                2,
                10.00m,
                source,
                summary.RowVersion)
            {
                Questions = questions
            }
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedExam = viewModel.Exams.Single();
        await viewModel.LoadSelectedExamAsync();
        viewModel.QuizImport.Preview = Preview(
            [AuthoringQuestion("Temporary first", 1, 4.00m), AuthoringQuestion("Temporary second", 2, 6.00m)]);

        viewModel.CommitQuizCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains($"api/v1/exams/{summary.Id}/quiz-import/commit") && !viewModel.IsBusy,
            TimeSpan.FromSeconds(2)));

        Assert.False(viewModel.QuizImport.HasPreview);
        Assert.Equal(questions.Select(x => x.Id), viewModel.QuizImport.Questions.Select(x => x.Id));
        Assert.Equal("Committed first", viewModel.QuizImport.Questions[0].Text);
        Assert.True(viewModel.QuizImport.Questions[0].Choices[0].IsCorrect);
        Assert.Equal(2, viewModel.SelectedExam?.QuizQuestionCount);
    }

    [Fact]
    public async Task QuizImport_SelectingAnotherExamClearsOldGraphBeforeApplyingNewDetail()
    {
        var first = MakeExamSummary(
            Guid.NewGuid(),
            "Quiz A",
            ExamDeliveryType.MultipleChoice,
            hasCommittedQuizSource: true,
            quizQuestionCount: 1);
        var second = MakeExamSummary(
            Guid.NewGuid(),
            "Quiz B",
            ExamDeliveryType.MultipleChoice,
            hasCommittedQuizSource: true,
            quizQuestionCount: 1);
        var api = new RecordingBackendClient(DateTimeOffset.UtcNow)
        {
            ExamResponses = [first, second],
            ExamDetailResponse = QuizDetail(first, QuizSource("a.docx", 1), [AuthoringQuestion("Question A", 1, 10.00m)])
        };
        using var viewModel = new ExamManagementViewModel(api);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedExam = viewModel.Exams.Single(x => x.Id == first.Id);
        await viewModel.LoadSelectedExamAsync();
        Assert.Equal("Question A", Assert.Single(viewModel.QuizImport.Questions).Text);

        api.ExamDetailResponse = QuizDetail(second, QuizSource("b.docx", 1), [AuthoringQuestion("Question B", 1, 10.00m)]);
        viewModel.SelectedExam = viewModel.Exams.Single(x => x.Id == second.Id);
        Assert.Empty(viewModel.QuizImport.Questions);
        await viewModel.LoadSelectedExamAsync();

        Assert.Equal("Question B", Assert.Single(viewModel.QuizImport.Questions).Text);
        Assert.DoesNotContain(viewModel.QuizImport.Questions, question => question.Text == "Question A");
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
        viewModel.IsMultipleChoice = true;
        viewModel.QuizShuffleEnabled = true;
        viewModel.CreateCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(
            () => api.PostPaths.Contains("api/v1/exams"),
            TimeSpan.FromSeconds(2)));
        var create = Assert.IsType<CreateExamRequest>(
            api.PostRequests.First(request => request is CreateExamRequest));
        Assert.Null(create.ClassId);
        Assert.True(create.QuizShuffleEnabled);
        viewModel.IsFileSubmission = true;
        Assert.False(viewModel.QuizShuffleEnabled);
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

    private static ExamSummaryDto MakeExamSummary(
        Guid id,
        string title,
        ExamDeliveryType deliveryType = ExamDeliveryType.FileSubmission,
        ExamStatus status = ExamStatus.Draft,
        int fileCount = 0,
        bool hasCommittedQuizSource = false,
        int quizQuestionCount = 0) => new(
        id,
        null,
        title,
        "Math",
        45,
        deliveryType,
        status,
        1,
        fileCount,
        "rv-" + id,
        HasCommittedQuizSource: hasCommittedQuizSource,
        QuizQuestionCount: quizQuestionCount);

    private static QuizImportPreviewDto Preview(IReadOnlyList<QuizAuthoringQuestionDto> questions) =>
        new(
            "preview-token",
            "preview.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "preview-hash",
            questions.Count,
            questions.Sum(x => x.Points),
            questions,
            [],
            [],
            DateTimeOffset.UtcNow.AddMinutes(20),
            false);

    private static QuizImportSourceDto QuizSource(string fileName, int examVersion) =>
        new(
            Guid.NewGuid(),
            fileName,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            1024,
            "source-hash-" + fileName,
            examVersion,
            "Committed",
            DateTimeOffset.UtcNow);

    private static QuizAuthoringQuestionDto AuthoringQuestion(string text, int order, decimal points) =>
        new(
            Guid.NewGuid(),
            text,
            order,
            points,
            false,
            [
                new QuizAuthoringChoiceDto(Guid.NewGuid(), "Correct", 1, true),
                new QuizAuthoringChoiceDto(Guid.NewGuid(), "Wrong", 2, false)
            ]);

    private static ExamDetailDto QuizDetail(
        ExamSummaryDto summary,
        QuizImportSourceDto? source,
        IReadOnlyList<QuizAuthoringQuestionDto> questions) =>
        new(
            summary.Id,
            summary.ClassId,
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
            questions.Count,
            questions.Sum(x => x.Points))
        {
            QuizQuestions = questions
        };

    private static string FindExamManagementView()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "frontend",
                "src",
                "ExamTransfer.Desktop",
                "Views",
                "ExamManagementView.xaml");
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException("Không tìm thấy ExamManagementView.xaml.");
    }

    [Fact]
    public void QuizImport_MetadataMismatchKeepsPreviewAndRejectsFalseCommittedState()
    {
        var previewQuestion = AuthoringQuestion("Preview remains", 1, 10.00m);
        var state = new QuizImportViewState
        {
            Preview = Preview([previewQuestion])
        };
        var source = QuizSource("committed.docx", 1);

        var error = Assert.Throws<InvalidOperationException>(() =>
            state.SetCommitted(source, 1, 2, 10.00m, [previewQuestion]));

        Assert.Contains("2", error.Message, StringComparison.Ordinal);
        Assert.True(state.HasPreview);
        Assert.False(state.HasCommittedSource);
        Assert.Single(state.Questions);
        Assert.Equal("Preview remains", state.Questions[0].Text);
    }
}
