using System.Windows.Input;
using ExamTransfer.Desktop.Core;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class ErrorPageViewModel : ObservableObject
{
    public ErrorPageViewModel(string message, string traceId, string logPath, Action retry, Action goHome)
    {
        Message = message;
        TraceId = traceId;
        LogPath = logPath;
        RetryCommand = new RelayCommand(retry);
        GoHomeCommand = new RelayCommand(goHome);
    }

    public string Title => "Không thể mở màn hình";

    public string Message { get; }

    public string TraceId { get; }

    public string LogPath { get; }

    public ICommand RetryCommand { get; }

    public ICommand GoHomeCommand { get; }
}
