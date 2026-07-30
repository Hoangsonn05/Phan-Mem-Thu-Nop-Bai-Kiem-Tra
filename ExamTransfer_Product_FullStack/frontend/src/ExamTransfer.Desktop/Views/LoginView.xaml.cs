using System.Windows.Controls;
using ExamTransfer.Desktop.ViewModels;

namespace ExamTransfer.Desktop.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    private bool isPasswordVisible = false;
    private bool isSyncing = false;

    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (isSyncing) return;
        isSyncing = true;
        PasswordVisibleInput.Text = PasswordInput.Password;
        if (DataContext is LoginViewModel viewModel)
            viewModel.Password = PasswordInput.Password;
        isSyncing = false;
    }

    private void OnVisiblePasswordChanged(object sender, TextChangedEventArgs e)
    {
        if (isSyncing) return;
        isSyncing = true;
        PasswordInput.Password = PasswordVisibleInput.Text;
        if (DataContext is LoginViewModel viewModel)
            viewModel.Password = PasswordVisibleInput.Text;
        isSyncing = false;
    }

    private void OnTogglePasswordClick(object sender, System.Windows.RoutedEventArgs e)
    {
        isPasswordVisible = !isPasswordVisible;
        if (isPasswordVisible)
        {
            PasswordInput.Visibility = System.Windows.Visibility.Collapsed;
            PasswordVisibleInput.Visibility = System.Windows.Visibility.Visible;
            TogglePasswordIcon.Text = "\uE8D4"; // EyeSlash or Eye icon code depending on Segoe Fluent Icons, E890 is Eye, E8D4 is Hide
            TogglePasswordButton.ToolTip = "Ẩn mật khẩu";
        }
        else
        {
            PasswordInput.Visibility = System.Windows.Visibility.Visible;
            PasswordVisibleInput.Visibility = System.Windows.Visibility.Collapsed;
            TogglePasswordIcon.Text = "\uE890"; // Eye
            TogglePasswordButton.ToolTip = "Hiển thị mật khẩu";
        }
    }
}
