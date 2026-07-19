using System.Windows;
using System.Windows.Controls;
using ExamTransfer.Desktop.ViewModels;

namespace ExamTransfer.Desktop.Views;

public partial class SettingsPageView : UserControl
{
    public SettingsPageView()
    {
        InitializeComponent();
    }

    private void CloudPasswordBox_OnPasswordChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is SettingsPageViewModel viewModel
            && sender is PasswordBox passwordBox)
        {
            viewModel.CloudPassword = passwordBox.Password;
        }
    }
}
