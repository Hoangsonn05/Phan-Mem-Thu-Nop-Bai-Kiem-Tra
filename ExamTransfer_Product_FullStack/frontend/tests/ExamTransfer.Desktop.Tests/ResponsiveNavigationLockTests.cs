using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Desktop.Views;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class ResponsiveNavigationLockTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    [Theory]
    [InlineData("SessionManagementView.xaml", "Tạo và mở kỳ thi", "CreateCommand")]
    [InlineData("StudentSubmissionView.xaml", "Bắt đầu nộp bài", "SubmitCommand")]
    public void PrimaryAction_IsPinnedOutsideScrollableContent(
        string fileName,
        string buttonText,
        string commandName)
    {
        var document = XDocument.Load(FindView(fileName));
        var button = document.Descendants(Presentation + "Button")
            .Single(element =>
                (string?)element.Attribute("Content") == buttonText
                || element.Descendants(Presentation + "TextBlock")
                    .Any(text => (string?)text.Attribute("Text") == buttonText));

        Assert.Equal("1", (string?)button.Attribute("Grid.Row"));
        Assert.Contains(commandName, (string?)button.Attribute("Command"), StringComparison.Ordinal);
        Assert.DoesNotContain(button.Ancestors(), element => element.Name == Presentation + "ScrollViewer");

        var actionGrid = button.Parent;
        Assert.NotNull(actionGrid);
        Assert.Equal(Presentation + "Grid", actionGrid.Name);
        Assert.Contains(
            actionGrid.Elements(Presentation + "ScrollViewer"),
            scroll => (string?)scroll.Attribute("Grid.Row") == "0");
    }

    [Fact]
    public void SubmissionGuidance_HasItsOwnVerticalScrollRegion()
    {
        var document = XDocument.Load(FindView("StudentSubmissionView.xaml"));
        var heading = document.Descendants(Presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "Quy trình xác nhận");

        Assert.Contains(
            heading.Ancestors(),
            element => element.Name == Presentation + "ScrollViewer"
                && (string?)element.Attribute("VerticalScrollBarVisibility") == "Auto");
    }

    [Theory]
    [InlineData("session", "Tạo và mở kỳ thi")]
    [InlineData("submission", "Bắt đầu nộp bài")]
    public void PrimaryAction_RemainsInsideA1280By720Viewport(string viewName, string buttonText)
    {
        WpfTestHost.Run(() =>
        {
            UserControl view = viewName switch
            {
                "session" => new SessionManagementView(),
                "submission" => new StudentSubmissionView(),
                _ => throw new InvalidOperationException(viewName)
            };
            var window = new Window
            {
                Content = view,
                Width = 1280,
                Height = 720,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10_000,
                Top = -10_000
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var button = FindDescendants<Button>(view)
                    .Single(element => ButtonText(element) == buttonText);
                var bounds = button.TransformToAncestor(view)
                    .TransformBounds(new Rect(button.RenderSize));

                Assert.True(button.ActualHeight > 0);
                Assert.True(bounds.Top >= 0);
                Assert.True(bounds.Bottom <= view.ActualHeight + 0.5);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TeacherNavigation_LocksOnlyT02AndT15()
    {
        var items = MainViewModel.TeacherItems();
        var locked = items.Where(item => !item.IsAvailable).ToArray();

        Assert.Equal(["T-02", "T-15"], locked.Select(item => item.Key).ToArray());
        Assert.All(locked, item =>
        {
            Assert.Equal("Khóa", item.Badge);
            Assert.Equal(MainViewModel.UnavailableFeatureMessage, item.UnavailableMessage);
        });
        Assert.All(
            items.Where(item => item.Key is not ("T-02" or "T-15")),
            item => Assert.True(item.IsAvailable));
    }

    [Fact]
    public void LockedSelection_KeepsPreviousItemAndBuildsExactNotification()
    {
        var current = new NavigationItem("T-01", "Current", "Group", "Description", "Glyph");
        var locked = MainViewModel.TeacherItems().Single(item => item.Key == "T-02");

        var accepted = MainViewModel.TryResolveSelection(current, locked, out var resolved);
        var notification = MainViewModel.CreateUnavailableNotification(locked);

        Assert.False(accepted);
        Assert.Same(current, resolved);
        Assert.Equal(MainViewModel.UnavailableFeatureMessage, notification.Title);
        Assert.Equal(MainViewModel.UnavailableFeatureMessage, notification.Message);
    }

    [Fact]
    public void NavigationGuard_PrecedesPageCreationAndLockedItemsRemainClickable()
    {
        var source = File.ReadAllText(FindSource("ViewModels", "MainViewModel.cs"));
        var navigateStart = source.IndexOf("private void NavigateSafely", StringComparison.Ordinal);
        var guard = source.IndexOf("if (!item.IsAvailable)", navigateStart, StringComparison.Ordinal);
        var pageCreation = source.IndexOf("CreatePage(item)", navigateStart, StringComparison.Ordinal);
        var theme = XDocument.Load(FindSource("Themes", "Theme.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sidebarItem = theme.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "SidebarItem");
        var sidebarMarkup = sidebarItem.ToString(SaveOptions.DisableFormatting);

        Assert.True(navigateStart >= 0 && guard > navigateStart && pageCreation > guard);
        Assert.Contains("Binding IsAvailable", sidebarMarkup, StringComparison.Ordinal);
        Assert.Contains("Opacity\" Value=\"0.46", sidebarMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"IsEnabled\" Value=\"False", sidebarMarkup, StringComparison.Ordinal);
    }

    private static string FindView(string fileName) =>
        FindFile("frontend", "src", "ExamTransfer.Desktop", "Views", fileName);

    private static string ButtonText(Button button) =>
        button.Content as string
        ?? FindDescendants<TextBlock>(button)
            .Select(element => element.Text)
            .FirstOrDefault(text => text is "Tạo và mở kỳ thi" or "Bắt đầu nộp bài")
        ?? string.Empty;

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private static string FindSource(params string[] segments) =>
        FindFile(["frontend", "src", "ExamTransfer.Desktop", .. segments]);

    private static string FindFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
