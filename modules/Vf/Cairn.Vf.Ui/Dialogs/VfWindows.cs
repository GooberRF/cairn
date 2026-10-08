using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Formats.Imaging;
using Cairn.Ui.Controls;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Ui.Views;

namespace Cairn.Vf.Ui.Dialogs;

/// <summary>What the replace-glyph window settled on: the glyph's new width, pixels and spacing.</summary>
public sealed record VfReplaceResult(int Width, byte[] Pixels, int Spacing);

/// <summary>
/// "Replace Glyph from Image": how a picture becomes the selected glyph (height fit, width, coverage source for
/// monochrome fonts, threshold, spacing), with the picture and the resulting glyph side by side.
/// </summary>
public sealed class VfReplaceGlyphWindow : Window
{
    private readonly VfFont _font;
    private readonly int _index;
    private readonly BgraImage _image;
    private readonly VfBackdrop _backdrop;
    private readonly ComboBox _fit = new() { MinWidth = 260 }, _width = new() { MinWidth = 260 }, _coverage = new() { MinWidth = 260 };
    private readonly NumericBox _threshold = new() { Minimum = 0, Maximum = 255, Step = 8, Decimals = 0, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _moveSpacing = new() { Content = "Move the spacing with the width", IsChecked = true };
    private readonly Border _result = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public VfReplaceGlyphWindow(VfFont font, int index, BgraImage image, string sourceName, VfBackdrop backdrop)
    {
        _font = font; _index = index; _image = image; _backdrop = backdrop;
        Title = "Replace Glyph from Image";
        Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");
        var g = font.Glyphs[index];
        _fit.Items.Add($"Scale to the font height ({font.Height} px)");
        _fit.Items.Add("Keep the image's size, top-aligned");
        _fit.Items.Add("Keep the image's size, bottom-aligned");
        _fit.SelectedIndex = image.Height == font.Height ? 1 : 0;
        _width.Items.Add("From the image");
        _width.Items.Add($"Keep the glyph's width ({g.Width} px), cut or padded on the right");
        _width.SelectedIndex = 0;
        foreach (var c in new[] { "Automatic (alpha, or brightness)", "Alpha (transparency)", "Brightness (light on dark)", "Darkness (dark on light)" }) _coverage.Items.Add(c);
        _coverage.SelectedIndex = 0;
        _coverage.IsEnabled = UsesCoverage;
        _coverage.ToolTip = UsesCoverage ? "Which part of the picture becomes the glyph's coverage (how solid each pixel is)" : "This font stores colours: each pixel takes the nearest colour the font can hold";
        _threshold.ToolTip = "0 keeps soft edges; 1 to 255 makes every pixel solid or clear at this coverage (or alpha)";
        _moveSpacing.ToolTip = $"Keep the gap after the glyph: spacing changes by as much as the width (now spacing {g.Spacing}, width {g.Width})";
        foreach (var (e, n) in new (FrameworkElement, string)[] { (_fit, "Height"), (_width, "Width"), (_coverage, "Coverage"), (_threshold, "Threshold"), (_moveSpacing, "Spacing") }) AutomationProperties.SetName(e, n);
        _fit.SelectionChanged += (_, _) => Update();
        _width.SelectionChanged += (_, _) => Update();
        _coverage.SelectionChanged += (_, _) => Update();
        _threshold.ValueChanged += (_, _) => Update();
        _moveSpacing.Click += (_, _) => Update();
        Content = Build(sourceName);
        Update();
    }

    /// <summary>The glyph chosen with Replace, or null.</summary>
    public VfReplaceResult? Result { get; private set; }

    /// <summary>True when the picture's coverage (not its colours) decides the pixels: monochrome fonts and white-palette indexed fonts.</summary>
    private bool UsesCoverage => _font.Format == VfPixelFormat.Mono || VfPixelConvert.IsWhitePalette(_font);

    /// <summary>The options the window shows now.</summary>
    public VfImageOptions Options => new(
        (VfCoverage)Math.Max(0, _coverage.SelectedIndex),
        (int)Math.Round(_threshold.Value),
        (VfHeightFit)Math.Max(0, _fit.SelectedIndex),
        _width.SelectedIndex == 1 ? _font.Glyphs[_index].Width : null);

    /// <summary>The glyph the window would give now.</summary>
    public VfReplaceResult Current()
    {
        var (w, pixels) = VfPixelConvert.GlyphFromImage(_font, _image, Options);
        var g = _font.Glyphs[_index];
        return new VfReplaceResult(w, pixels, _moveSpacing.IsChecked == true ? g.Spacing + w - g.Width : g.Spacing);
    }

    private UIElement Build(string sourceName)
    {
        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = string.Format(CultureInfo.CurrentCulture, "Replace {0} with {1} ({2} × {3}), converted to {4}.",
                VfReader.Describe(_font.CharacterOf(_index)), sourceName, _image.Width, _image.Height, VfFont.FormatName(_font.Format)),
        });

        var previews = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        double scale = Math.Min(1.0, Math.Min(260.0 / _image.Width, 150.0 / _image.Height));
        if (scale < 1) scale = Math.Max(scale, 0.05); else scale = Math.Min(8, Math.Floor(Math.Min(260.0 / _image.Width, 150.0 / _image.Height)));
        var src = BitmapSource.Create(_image.Width, _image.Height, 96, 96, PixelFormats.Bgra32, null, _image.Pixels, _image.Stride);
        src.Freeze();
        var picture = new Image { Source = src, Width = _image.Width * scale, Height = _image.Height * scale, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(picture, scale >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        var pictureHost = new Border { Child = picture, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        VfImages.SetBackdrop(pictureHost, VfBackdrop.Checker);
        previews.Children.Add(Captioned("Picture", pictureHost, 0));
        previews.Children.Add(Captioned("Glyph", _result, 1));
        AutomationProperties.SetName(_result, "Resulting glyph");
        stack.Children.Add(previews);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        VfForm.AddRow(grid, "Height", _fit);
        VfForm.AddRow(grid, "Width", _width);
        VfForm.AddRow(grid, "Coverage from", _coverage);
        VfForm.AddRow(grid, "Threshold", _threshold);
        VfForm.AddRow(grid, "Spacing", _moveSpacing);
        stack.Children.Add(grid);
        _note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        stack.Children.Add(_note);
        stack.Children.Add(VfForm.Buttons(this, "_Replace", "Replace the glyph (one undo step)", () => Result = Current()));
        return stack;
    }

    private static StackPanel Captioned(string caption, UIElement content, int column)
    {
        var t = new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(StyleProperty, "PaneHeaderText");
        var s = new StackPanel { Children = { t, content } };
        Grid.SetColumn(s, column);
        return s;
    }

    private void Update()
    {
        if (!IsInitialized && _result is null) return;
        var r = Current();
        var preview = _font with { };
        preview = VfEdits.WithGlyphPixels(preview, _index, r.Width, r.Pixels);
        var bitmap = VfRender.Glyph(preview, _index);
        int zoom = Math.Clamp(Math.Min(150 / Math.Max(1, _font.Height), 260 / Math.Max(1, r.Width)), 1, 16);
        _result.Width = Math.Max(1, r.Width) * zoom;
        _result.Height = Math.Max(1, _font.Height) * zoom;
        VfImages.SetBackdrop(_result, _backdrop);
        _result.Child = bitmap.Width > 0 ? VfImages.Image(bitmap, zoom) : null;
        var coverage = UsesCoverage ? VfPixelConvert.Resolve(_image, Options.Coverage) : VfCoverage.Auto;
        _note.Text = string.Format(CultureInfo.CurrentCulture, "The glyph becomes {0} × {1} px with spacing {2}{3}.", r.Width, _font.Height, r.Spacing,
            !UsesCoverage ? "" : coverage switch
            {
                VfCoverage.Alpha => "; coverage from the picture's transparency",
                VfCoverage.DarkOnLight => "; coverage from dark pixels on a light background",
                _ => "; coverage from bright pixels on a dark background",
            });
    }
}

/// <summary>A small form: labelled rows (number boxes, drop-downs, check boxes) and OK/Cancel, for the font's simple prompts.</summary>
public sealed class VfFormWindow : Window
{
    private readonly Grid _grid = new();
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _stack = new() { Margin = new Thickness(14) };

    public VfFormWindow(string title, string intro, string okText, string okTip)
    {
        Title = title;
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _stack.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        _stack.Children.Add(_grid);
        _note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _stack.Children.Add(_note);
        _stack.Children.Add(VfForm.Buttons(this, okText, okTip, () => { }));
        Content = _stack;
    }

    /// <summary>The line under the rows.</summary>
    public string Note { get => _note.Text; set => _note.Text = value; }

    /// <summary>Adds a whole-number row.</summary>
    public NumericBox AddNumber(string label, int value, int min, int max, string tip, string suffix = "")
    {
        var box = new NumericBox { Minimum = min, Maximum = max, Step = 1, Decimals = 0, Value = value, Suffix = suffix, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = tip };
        VfForm.AddRow(_grid, label, box);
        return box;
    }

    /// <summary>Adds a drop-down row.</summary>
    public ComboBox AddChoice(string label, IEnumerable<string> items, int selected, string tip)
    {
        var box = new ComboBox { MinWidth = 260, ToolTip = tip };
        foreach (var i in items) box.Items.Add(i);
        box.SelectedIndex = selected;
        VfForm.AddRow(_grid, label, box);
        return box;
    }

    /// <summary>Adds a check box row.</summary>
    public CheckBox AddCheck(string label, string text, bool value, string tip)
    {
        var box = new CheckBox { Content = text, IsChecked = value, ToolTip = tip };
        VfForm.AddRow(_grid, label, box);
        return box;
    }
}

/// <summary>Shared pieces of the font windows.</summary>
internal static class VfForm
{
    public static void AddRow(Grid grid, string label, FrameworkElement content)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 12, 5) };
        text.SetResourceReference(FrameworkElement.StyleProperty, "DialogLabel");
        Grid.SetRow(text, row);
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);
        content.Margin = new Thickness(0, 4, 0, 4);
        content.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(content, label);
        grid.Children.Add(text);
        grid.Children.Add(content);
    }

    public static StackPanel Buttons(Window window, string okText, string okTip, Action accept)
    {
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = okTip };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in new[] { ok, cancel }) { b.SetResourceReference(FrameworkElement.StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        ok.Click += (_, _) => { accept(); window.DialogResult = true; };
        return buttons;
    }
}
