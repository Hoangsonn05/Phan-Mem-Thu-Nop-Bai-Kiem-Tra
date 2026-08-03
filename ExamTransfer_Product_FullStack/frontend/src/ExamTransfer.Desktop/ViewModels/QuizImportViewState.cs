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
            Questions.ReplaceWith(value?.Questions ?? []);
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
    public ObservableCollection<QuizQuestionDto> Questions { get; } = [];
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

    public void SetCommitted(QuizImportSourceDto? source, int questionCount, decimal maxScore)
    {
        committedSource = source;
        committedQuestionCount = source is null ? 0 : questionCount;
        committedMaxScore = source is null ? 0 : maxScore;
        Preview = null;
        SelectedFileName = string.Empty;
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
        Preview = null;
        SelectedFileName = string.Empty;
        Raise(nameof(HasCommittedSource));
        Raise(nameof(CommittedSource));
        Raise(nameof(CommittedQuestionCount));
        Raise(nameof(CommittedMaxScore));
        Raise(nameof(Summary));
        Raise(nameof(ReplaceNotice));
    }
}
