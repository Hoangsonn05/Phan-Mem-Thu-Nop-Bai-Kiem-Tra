using System.Collections.ObjectModel;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class QuizImportViewState : ObservableObject
{
    private QuizImportPreviewDto? preview;
    private string selectedFileName = string.Empty;
    private QuizImportSourceDto? committedSource;
    private int committedQuestionCount;
    private decimal committedMaxScore;

    public QuizImportPreviewDto? Preview
    {
        get => preview;
        set
        {
            if (!Set(ref preview, value))
                return;
            Warnings.ReplaceWith(value?.Warnings ?? []);
            Errors.ReplaceWith(value?.Errors ?? []);
            PreviewQuestions.ReplaceWith(value?.Questions ?? []);
            Questions.ReplaceWith(value is null ? CommittedQuestions : PreviewQuestions);
            Raise(nameof(HasPreview));
            Raise(nameof(Summary));
            Raise(nameof(ReplaceNotice));
        }
    }

    public string SelectedFileName
    {
        get => selectedFileName;
        set => Set(ref selectedFileName, value);
    }

    public ObservableCollection<QuizImportIssueDto> Warnings { get; } = [];
    public ObservableCollection<QuizImportIssueDto> Errors { get; } = [];
    public ObservableCollection<QuizAuthoringQuestionDto> PreviewQuestions { get; } = [];
    public ObservableCollection<QuizAuthoringQuestionDto> CommittedQuestions { get; } = [];
    public ObservableCollection<QuizAuthoringQuestionDto> Questions { get; } = [];
    public bool HasPreview => Preview is not null && Preview.Errors.Count == 0;
    public bool HasCommittedSource => CommittedSource is not null;
    public QuizImportSourceDto? CommittedSource => committedSource;
    public int CommittedQuestionCount => committedQuestionCount;
    public decimal CommittedMaxScore => committedMaxScore;
    public string Summary => Preview is null
        ? CommittedSource is null
            ? "Chưa có bản xem trước."
            : $"Đã lưu {CommittedQuestionCount} câu · {CommittedMaxScore:0.##} điểm từ {CommittedSource.FileName}."
        : $"{Preview.QuestionCount} câu · {Preview.MaxScore:0.##} điểm";
    public string ReplaceNotice => Preview?.WillReplaceExisting == true
        ? "Commit sẽ thay toàn bộ câu hỏi hiện tại sau khi bạn xác nhận."
        : Preview is not null
            ? "Commit sẽ tạo bộ câu hỏi cho phiên bản hiện tại."
            : CommittedSource is not null
                ? $"Nguồn đã commit cho phiên bản {CommittedSource.ExamVersion}."
                : string.Empty;

    public void SetCommitted(
        QuizImportSourceDto? source,
        int examVersion,
        int questionCount,
        decimal maxScore,
        IReadOnlyList<QuizAuthoringQuestionDto> questions)
    {
        ValidateCommittedGraph(source, examVersion, questionCount, maxScore, questions);
        committedSource = source;
        committedQuestionCount = source is null ? 0 : questionCount;
        committedMaxScore = source is null ? 0 : maxScore;
        CommittedQuestions.ReplaceWith(questions);
        Set(ref preview, null, nameof(Preview));
        Warnings.Clear();
        Errors.Clear();
        PreviewQuestions.Clear();
        Questions.ReplaceWith(CommittedQuestions);
        SelectedFileName = string.Empty;
        Raise(nameof(HasPreview));
        Raise(nameof(HasCommittedSource));
        Raise(nameof(CommittedSource));
        Raise(nameof(CommittedQuestionCount));
        Raise(nameof(CommittedMaxScore));
        Raise(nameof(Summary));
        Raise(nameof(ReplaceNotice));
    }

    public void Clear()
    {
        committedSource = null;
        committedQuestionCount = 0;
        committedMaxScore = 0;
        Set(ref preview, null, nameof(Preview));
        Warnings.Clear();
        Errors.Clear();
        PreviewQuestions.Clear();
        CommittedQuestions.Clear();
        Questions.Clear();
        SelectedFileName = string.Empty;
        Raise(nameof(HasPreview));
        Raise(nameof(HasCommittedSource));
        Raise(nameof(CommittedSource));
        Raise(nameof(CommittedQuestionCount));
        Raise(nameof(CommittedMaxScore));
        Raise(nameof(Summary));
        Raise(nameof(ReplaceNotice));
    }

    private static void ValidateCommittedGraph(
        QuizImportSourceDto? source,
        int examVersion,
        int questionCount,
        decimal maxScore,
        IReadOnlyList<QuizAuthoringQuestionDto> questions)
    {
        if (source is null)
        {
            if (questionCount != 0 || maxScore != 0 || questions.Count != 0)
                throw new InvalidOperationException("Metadata đề trắc nghiệm không khớp: không có nguồn commit nhưng graph không rỗng.");
            return;
        }

        if (source.ExamVersion != examVersion)
            throw new InvalidOperationException("Phiên bản nguồn đề trắc nghiệm không khớp bài kiểm tra đang mở.");
        if (questions.Count != questionCount)
            throw new InvalidOperationException($"Metadata đề trắc nghiệm báo {questionCount} câu nhưng backend trả {questions.Count} câu.");
        var graphMaxScore = questions.Sum(x => x.Points);
        if (graphMaxScore != maxScore)
            throw new InvalidOperationException($"Tổng điểm graph đề trắc nghiệm là {graphMaxScore:0.##} nhưng metadata báo {maxScore:0.##}.");
    }
}
