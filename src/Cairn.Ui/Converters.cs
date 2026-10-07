using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Cairn.Ui;

/// <summary>Maps false (or null, or an empty string, or zero) to <see cref="Visibility.Collapsed"/>.</summary>
public sealed class TruthyToVisibilityConverter : IValueConverter
{
    /// <summary>True to invert the test, so truthy values collapse instead.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            double d => d != 0,
            _ => true,
        };
        if (Invert) truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Picks the theme brush for a diagnostic severity.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    /// <summary>True to return the soft background brush instead of the foreground one.</summary>
    public bool Soft { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = (value is Enum ? value.ToString() : null) switch
        {
            "Error" => Soft ? "Severity.ErrorSoft" : "Severity.Error",
            "Warning" => Soft ? "Severity.WarningSoft" : "Severity.Warning",
            "Info" => Soft ? "Severity.InfoSoft" : "Severity.Info",
            _ => "App.SecondaryText",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Picks the Segoe MDL2 glyph for a diagnostic severity.</summary>
public sealed class SeverityToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Enum ? value.ToString() : null) switch
        {
            "Error" => "",
            "Warning" => "",
            "Info" => "",
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Turns a full path into its file name, for recent-file menu captions.</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string path && path.Length > 0 ? System.IO.Path.GetFileName(path) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Looks a theme brush up by its resource key ("Severity.WarningSoft"), for rows whose view-model says
/// which brush applies. Used in dialogs (evaluated when the row changes; a theme switch while a dialog
/// is open repaints on the next change).
/// </summary>
public sealed class BrushKeyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryFindResource(key) is Brush brush ? brush : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Inverts a boolean (for IsEnabled bound to a read-only flag, and the like).</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}