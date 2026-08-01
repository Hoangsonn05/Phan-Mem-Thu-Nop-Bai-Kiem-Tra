using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Models;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class NotificationCenterViewModel : ObservableObject
{
    private readonly RelayCommand closeCommand;
    private NotificationItem? current;

    internal NotificationCenterViewModel(Action closeCurrent)
    {
        closeCommand = new RelayCommand(closeCurrent, () => Current is not null);
    }

    public NotificationItem? Current
    {
        get => current;
        private set
        {
            if (!Set(ref current, value))
                return;
            Raise(nameof(IsVisible));
            Raise(nameof(ToneKey));
            Raise(nameof(ToneGlyph));
            closeCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsVisible => Current is not null;

    public string ToneKey => Current?.Tone switch
    {
        NotificationTone.Success => "success",
        NotificationTone.Warning => "warning",
        NotificationTone.Error => "danger",
        _ => "info"
    };

    public string ToneGlyph => Current?.Tone switch
    {
        NotificationTone.Success => "\uE73E",
        NotificationTone.Warning => "\uE7BA",
        NotificationTone.Error => "\uEA39",
        _ => "\uE946"
    };

    public ICommand CloseCommand => closeCommand;

    internal void Show(NotificationItem notification) => Current = notification;

    internal void Hide() => Current = null;
}
