using System.Windows;
using ExamTransfer.Desktop.Core;
using Microsoft.Win32;

namespace ExamTransfer.Desktop.Services;

public sealed class FileDialogService : IFileDialogService
{
    public string? PickFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

public sealed class FolderDialogService : IFolderDialogService
{
    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog();
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}

public sealed class ClipboardService : IClipboardService
{
    public void SetText(string text) => Clipboard.SetText(text);
}

public sealed class ToastService : IToastService
{
    public void Show(string message, string tone = "info")
    {
        FrontendLogger.LogMessage($"Toast[{tone}] {message}", "ToastService");
    }
}

public sealed class LocalPreferenceService : ILocalPreferenceService
{
    public string? Get(string key) => Environment.GetEnvironmentVariable($"EXAMTRANSFER_PREF_{key}");

    public void Set(string key, string value) =>
        Environment.SetEnvironmentVariable($"EXAMTRANSFER_PREF_{key}", value, EnvironmentVariableTarget.User);
}
