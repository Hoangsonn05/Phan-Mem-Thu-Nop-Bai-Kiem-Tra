using System.IO;
using System.Xml.Linq;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class SubmissionCenterResponsiveLayoutTests
{
    [Fact]
    public void ProductionXaml_UsesModeSpecificResponsiveLayoutsAndBoundKpis()
    {
        var path = FindFile(
            "frontend", "src", "ExamTransfer.Desktop", "Views", "SubmissionCenterView.xaml");
        var document = XDocument.Load(path);
        var root = Assert.IsType<XElement>(document.Root);
        var presentation = root.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var fileLayout = FindNamed(root, x, "FileSubmissionLayout");
        var quizLayout = FindNamed(root, x, "QuizSubmissionLayout");
        var fileActionPanel = FindNamed(root, x, "FileActionPanel");
        var fileGrid = FindNamed(root, x, "FileSubmissionGrid");
        var quizGrid = FindNamed(root, x, "QuizSubmissionGrid");
        var dataGrids = root.Descendants(presentation + "DataGrid").ToArray();

        Assert.Equal(2, dataGrids.Length);
        Assert.Contains(fileActionPanel, fileLayout.Descendants());
        Assert.DoesNotContain(fileActionPanel, quizLayout.Descendants());
        Assert.Contains(
            fileLayout.Descendants(presentation + "ColumnDefinition"),
            column => (string?)column.Attribute("MinWidth") == "300"
                && (string?)column.Attribute("MaxWidth") == "340");
        Assert.Empty(quizLayout.Descendants(presentation + "ColumnDefinition"));
        Assert.True(ContainsBinding(fileGrid, "IsSelected"));
        Assert.True(ContainsBinding(fileGrid, "ReceiptCode"));
        Assert.False(ContainsBinding(fileGrid, "ScoreSummaryText"));
        Assert.False(ContainsBinding(quizGrid, "IsSelected"));
        Assert.False(ContainsBinding(quizGrid, "ReceiptCode"));
        Assert.True(ContainsBinding(quizGrid, "ScoreSummaryText"));
        Assert.True(ContainsBinding(quizGrid, "DataIssue"));
        Assert.All(dataGrids, grid =>
            Assert.Equal(
                "Auto",
                (string?)grid.Attributes().Single(attribute =>
                    attribute.Name.LocalName == "ScrollViewer.HorizontalScrollBarVisibility")));

        var source = File.ReadAllText(path);
        Assert.Contains("x:Name=\"FileSubmissionGrid\"", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QuizSubmissionGrid\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SubmittedCount}\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding NotSubmittedCount}\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding LateCount}\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding IssueCount}\"", source, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"NoWrap\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"—\"", source, StringComparison.Ordinal);
    }

    private static bool ContainsBinding(XElement element, string propertyName) =>
        element.DescendantsAndSelf()
            .Attributes()
            .Any(attribute => attribute.Value.Contains(propertyName, StringComparison.Ordinal));

    private static XElement FindNamed(XElement root, XNamespace x, string name) =>
        Assert.Single(root.Descendants(), element =>
            string.Equals((string?)element.Attribute(x + "Name"), name, StringComparison.Ordinal));

    private static string FindFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
