using System.Windows;
using ExamTransfer.Desktop.Core;

namespace ExamTransfer.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        if (AppProfile.IsNamed)
            Title = $"{Title} [{AppProfile.DisplayName}]";
    }
}
