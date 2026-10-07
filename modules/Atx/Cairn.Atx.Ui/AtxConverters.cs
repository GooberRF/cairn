using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Cairn.Atx.Linting;

namespace Cairn.Atx.Ui;

/// <summary>
/// Bold for a frame that overrides the texture frame time, normal for one that inherits it.
/// </summary>
public sealed class OverrideWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Full-strength text for an overridden frame time, the secondary colour for an inherited one, so
/// the list shows at a glance which frames carry their own timing.
/// </summary>
public sealed class OverrideBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is bool b && b ? "App.Text" : "App.SecondaryText";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
