using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Cairn.Viewport;

/// <summary>A small table of counts (vertices, triangles, bones…) in a corner of a 3D view, shown while its Statistics toggle is on.</summary>
public sealed class StatisticsPanel : Border
{
    private readonly Grid _grid = new();

    public StatisticsPanel()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(8);
        Padding = new Thickness(10, 6, 10, 6);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        SetResourceReference(BackgroundProperty, "Viewport.Notice");
        SetResourceReference(BorderBrushProperty, "App.Border");
        AutomationProperties.SetName(this, "Statistics");
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Child = _grid;
    }

    /// <summary>The rows shown.</summary>
    public IReadOnlyList<(string Label, string Value)> Rows { get; private set; } = [];

    /// <summary>Shows <paramref name="rows"/> (label, value).</summary>
    public void Show(IReadOnlyList<(string Label, string Value)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.SequenceEqual(Rows)) return;
        Rows = rows;
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Text(rows[i].Label, 0.75);
            label.Margin = new Thickness(0, 0, 12, 0);
            var value = Text(rows[i].Value, 1);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            _grid.Children.Add(label);
            _grid.Children.Add(value);
        }
    }

    private static TextBlock Text(string text, double opacity)
    {
        var t = new TextBlock { Text = text, FontSize = 11, Opacity = opacity };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Viewport.NoticeText");
        return t;
    }
}
