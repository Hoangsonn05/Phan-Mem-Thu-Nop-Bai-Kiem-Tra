using System.Windows.Controls;

namespace ExamTransfer.Desktop.Views;

public partial class ExamManagementView : UserControl
{
    public ExamManagementView()
    {
        InitializeComponent();
    }

    private async void OnExamSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.ExamManagementViewModel viewModel)
            await viewModel.LoadSelectedExamAsync();
    }
}
