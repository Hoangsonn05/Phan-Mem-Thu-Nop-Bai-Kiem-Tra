using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Desktop.Views;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentQuizViewBindingTests
{
    [Fact]
    public void ActiveQuestionTemplate_BindsReadOnlyStateWithoutXamlException()
    {
        WpfTestHost.Run(() =>
        {
            var question = new QuizQuestionState(
                Guid.NewGuid(),
                "Question text",
                1,
                2.5m,
                false);
            question.Choices.Add(new QuizChoiceState(
                Guid.NewGuid(),
                "Choice text",
                false,
                () => { }));

            var viewModel = new QuizBindingModel();
            viewModel.Questions.Add(question);

            var view = new StudentQuizView { DataContext = viewModel };
            var window = new Window
            {
                Content = view,
                Width = 1000,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var list = Assert.IsType<ItemsControl>(view.FindName("ActiveQuestionsList"));
                Assert.Single(list.Items);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private sealed class QuizBindingModel
    {
        public ObservableCollection<QuizQuestionState> Questions { get; } = [];
        public bool IsActiveAttemptVisible => true;
        public bool IsReviewVisible => false;
    }
}
