using System.Collections.ObjectModel;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class QuizImportViewState : ObservableObject
{
    private QuizImportPreviewDto? preview;
    private string selectedFileName = string.Empty;

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
    public string Summary => Preview is null
        ? "Chưa có bản xem trước."
        : $"{Preview.QuestionCount} câu · {Preview.MaxScore:0.##} điểm";
    public string ReplaceNotice => Preview?.WillReplaceExisting == true
        ? "Commit sẽ thay toàn bộ câu hỏi hiện tại sau khi bạn xác nhận."
        : "Commit sẽ tạo bộ câu hỏi cho phiên bản hiện tại.";

    public void Clear()
    {
        Preview = null;
        SelectedFileName = string.Empty;
    }
}
