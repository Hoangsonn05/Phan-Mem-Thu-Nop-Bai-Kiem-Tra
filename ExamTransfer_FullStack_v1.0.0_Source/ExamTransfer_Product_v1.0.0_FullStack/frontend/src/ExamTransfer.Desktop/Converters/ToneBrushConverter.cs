using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ExamTransfer.Desktop.Converters;

public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value?.ToString()?.Trim().ToLowerInvariant() ?? "neutral";
        var suffix = parameter?.ToString()?.Equals("soft", StringComparison.OrdinalIgnoreCase) == true
            ? "SoftBrush"
            : "Brush";

        var key = tone switch
        {
            "primary" => "Primary" + suffix,
            "accent" => "Accent" + suffix,
            "success" => "Success" + suffix,
            "warning" => "Warning" + suffix,
            "danger" => "Danger" + suffix,
            "info" => "Info" + suffix,
            _ => parameter?.ToString()?.Equals("soft", StringComparison.OrdinalIgnoreCase) == true
                ? "SurfaceSubtleBrush"
                : "TextSecondaryBrush"
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
