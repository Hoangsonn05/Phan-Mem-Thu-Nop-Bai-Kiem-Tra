using System.Windows.Input;

namespace ExamTransfer.Desktop.Core;

public sealed class RelayCommand(
    Action execute,
    Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) =>
        ExecuteCore();

    private void ExecuteCore()
    {
        try
        {
            execute();
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, nameof(RelayCommand));
            FrontendLogger.ShowDebugDialog(ex, nameof(RelayCommand));
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null) : ICommand
{
    private bool isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isRunning = true;
        RaiseCanExecuteChanged();

        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, nameof(AsyncRelayCommand));
            FrontendLogger.ShowDebugDialog(ex, nameof(AsyncRelayCommand));
        }
        finally
        {
            isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
