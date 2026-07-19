using System.Windows.Controls;
using ExamTransfer.Desktop.ViewModels;

namespace ExamTransfer.Desktop.Views;

public partial class ChangePasswordView : UserControl
{
    public ChangePasswordView()
    {
        InitializeComponent();
    }

    private void OnCurrentPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ChangePasswordViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.CurrentPassword = passwordBox.Password;
    }

    private void OnNewPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ChangePasswordViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.NewPassword = passwordBox.Password;
    }

    private void OnConfirmPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ChangePasswordViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.ConfirmPassword = passwordBox.Password;
    }
}
