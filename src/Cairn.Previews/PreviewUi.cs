using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Cairn.Previews;

/// <summary>Small builders for the preview views, which are made in code: themed buttons, toolbars and messages.</summary>
public static class PreviewUi
{
    public static Button Button(string text, string toolTip, RoutedEventHandler click)
    {
        var b = new Button { Content = text, ToolTip = toolTip, Margin = new Thickness(0, 0, 2, 0) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "ToolButton");
        AutomationProperties.SetName(b, toolTip);
        b.Click += click;
        return b;
    }

    public static ToggleButton Toggle(string text, string toolTip, RoutedEventHandler changed)
    {
        var t = new ToggleButton { Content = text, ToolTip = toolTip, Margin = new Thickness(0, 0, 2, 0) };
        t.SetResourceReference(FrameworkElement.StyleProperty, "ToolToggle");
        AutomationProperties.SetName(t, toolTip);
        t.Checked += changed;
        t.Unchecked += changed;
        return t;
    }

    public static TextBlock Text(string text, string? style = null)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (style is not null) t.SetResourceReference(FrameworkElement.StyleProperty, style);
        else t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        return t;
    }

    public static TextBlock Secondary(string text)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        return t;
    }

    /// <summary>A slim tool strip along the top of a preview.</summary>
    public static Border Toolbar(params UIElement[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
        foreach (var c in children) panel.Children.Add(c);
        var border = new Border { Child = panel, BorderThickness = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BackgroundProperty, "App.ChromeBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        return border;
    }

    public static Separator Divider()
    {
        var s = new Separator { Margin = new Thickness(4, 3, 4, 3) };
        s.SetResourceReference(Control.BackgroundProperty, "App.SubtleBorder");
        s.LayoutTransform = new RotateTransform(90);
        return s;
    }

    /// <summary>A centred message (empty states, errors, "no preview"), with optional extra content below it.</summary>
    public static FrameworkElement Message(string text, string? detail = null, UIElement? extra = null, bool warning = false)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16), MaxWidth = 520 };
        var head = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 13 };
        head.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Severity.Warning" : "App.SecondaryText");
        stack.Children.Add(head);
        if (!string.IsNullOrEmpty(detail))
        {
            var d = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
            d.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            stack.Children.Add(d);
        }
        if (extra is not null)
        {
            if (extra is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.Margin = new Thickness(0, 10, 0, 0); }
            stack.Children.Add(extra);
        }
        return stack;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", bytes),
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.##} MB", bytes / (1024.0 * 1024)),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.##} GB", bytes / (1024.0 * 1024 * 1024)),
    };

    public static string Time(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
}
