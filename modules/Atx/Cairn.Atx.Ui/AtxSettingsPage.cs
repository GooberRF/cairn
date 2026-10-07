using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Cairn.Ui.Modules;

namespace Cairn.Atx.Ui;

/// <summary>Settings &gt; Animated textures: the new-file template and the preview background.</summary>
internal sealed partial class AtxSettingsPage(AtxWorkspace workspace) : ISettingsPage
{
    // Wording and controls as in 1.1.0's Settings dialog (NEW FILES, PREVIEW BACKGROUND groups).
    private static readonly (PreviewBackground Value, string Label)[] BackgroundChoices =
    [
        (PreviewBackground.Checkerboard, "Checkerboard"),
        (PreviewBackground.Black, "Black"),
        (PreviewBackground.White, "White"),
        (PreviewBackground.Custom, "Custom colour"),
    ];

    internal readonly RadioButton Minimal = new()
    {
        Content = new AccessText { Text = "_Minimal" }, GroupName = "AtxTemplate", Margin = new Thickness(0, 0, 16, 0),
        ToolTip = "Just the settings a new texture needs",
    };
    internal readonly RadioButton Commented = new()
    {
        Content = new AccessText { Text = "With e_xplanatory comments" }, GroupName = "AtxTemplate",
        ToolTip = "The same settings with a comment above each one",
    };
    internal readonly ComboBox Background = new() { Width = 176, MinHeight = 30, ToolTip = "What the preview shows behind a frame" };
    internal readonly TextBox Custom = new() { Width = 110, Height = 28, Margin = new Thickness(12, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "A colour such as #202020" };
    private readonly Border _swatchFrame = new() { Width = 28, Height = 28, Margin = new Thickness(8, 0, 0, 0), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
    private readonly Border _swatch = new() { Margin = new Thickness(1), CornerRadius = new CornerRadius(2) };
    private readonly TextBlock _invalid = new() { Text = "Not a colour", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private FrameworkElement? _view;

    public string Title => "Animated textures";

    public FrameworkElement View => _view ??= Build();

    private FrameworkElement Build()
    {
        foreach (var (value, label) in BackgroundChoices) Background.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        Background.SelectionChanged += (_, _) => UpdateCustom();
        Custom.TextChanged += (_, _) => UpdateCustom();
        _swatchFrame.Child = _swatch;
        _swatchFrame.SetResourceReference(Border.BorderBrushProperty, "App.Border");
        _invalid.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Error");
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Group(string heading, params UIElement[] row)
        {
            var title = new TextBlock { Text = heading, Margin = new Thickness(0, 10, 0, 8), FontSize = 11 };
            title.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            panel.Children.Add(title);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var element in row) line.Children.Add(element);
            panel.Children.Add(line);
        }
        Group("NEW FILES", Minimal, Commented);
        Group("PREVIEW BACKGROUND", Background, Custom, _swatchFrame, _invalid);
        return panel;
    }

    private bool IsCustom => Background.SelectedItem is ComboBoxItem { Tag: PreviewBackground.Custom };

    private void UpdateCustom()
    {
        bool custom = IsCustom, valid = ColorText().IsMatch(Custom.Text.Trim());
        Custom.IsEnabled = custom;
        _swatchFrame.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        _invalid.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        _swatch.Background = valid && System.Windows.Media.ColorConverter.ConvertFromString("#" + Custom.Text.Trim().TrimStart('#')) is System.Windows.Media.Color c
            ? new System.Windows.Media.SolidColorBrush(c) : null;
    }

    public void Load()
    {
        _ = View;
        var s = workspace.AtxSettings;
        Minimal.IsChecked = s.NewFileTemplate == NewFileTemplateKind.Minimal;
        Commented.IsChecked = s.NewFileTemplate == NewFileTemplateKind.Commented;
        Background.SelectedItem = Background.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, s.PreviewBackground));
        Custom.Text = s.PreviewCustomColor;
        UpdateCustom();
    }

    public void Commit()
    {
        var s = workspace.AtxSettings;
        s.NewFileTemplate = Commented.IsChecked == true ? NewFileTemplateKind.Commented : NewFileTemplateKind.Minimal;
        if (Background.SelectedItem is ComboBoxItem { Tag: PreviewBackground b }) s.PreviewBackground = b;
        if (ColorText().IsMatch(Custom.Text.Trim())) s.PreviewCustomColor = Custom.Text.Trim();
        workspace.InvalidateImageCaches();
    }

    [GeneratedRegex("^#?([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex ColorText();
}
