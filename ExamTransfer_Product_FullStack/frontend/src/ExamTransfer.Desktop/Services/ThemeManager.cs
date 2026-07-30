using System.Windows;

namespace ExamTransfer.Desktop.Services;

public static class ThemeManager
{
    private const string LightSource = "/ExamTransfer.Desktop;component/Themes/Palette.Light.xaml";
    private const string DarkSource = "/ExamTransfer.Desktop;component/Themes/Palette.Dark.xaml";

    public static bool IsDark { get; private set; }

    public static string CurrentLabel => IsDark ? "Chế độ sáng" : "Chế độ tối";

    public static void Toggle() => Apply(!IsDark);

    public static void Apply(bool dark)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Palette.", StringComparison.OrdinalIgnoreCase) == true);

        var next = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkSource : LightSource, UriKind.Relative)
        };

        if (palette is null)
        {
            dictionaries.Insert(0, next);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(palette)] = next;
        }

        IsDark = dark;
    }
}
