using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cairn.Formats.Imaging;
using Cairn.Previews;

namespace Cairn.Vbm.Ui.Dialogs;

/// <summary>
/// Shown when images going into a bitmap are not its frame size: the resize filter and how the image is fitted (stretch,
/// keep its shape with transparent padding, or crop the centre), with a preview of the first such image as the frame
/// will hold it (after the conversion to the bitmap's pixel format).
/// </summary>
public sealed class VbmResizeWindow : Window
{
    private static readonly (VbmResizeFilter Value, string Label, string Tip)[] Filters =
    [
        (VbmResizeFilter.Nearest, "Nearest (sharp pixels)", "Each pixel copies the nearest pixel of the image: hard edges, best for pixel art"),
        (VbmResizeFilter.Bilinear, "Bilinear (smooth)", "Blends neighbouring pixels: soft, quick"),
        (VbmResizeFilter.HighQuality, "High quality", "Lanczos resampling: the sharpest smooth result"),
    ];

    private static readonly (VbmFitMode Value, string Label, string Tip)[] Fits =
    [
        (VbmFitMode.Stretch, "Stretch to the frame", "Scale to exactly the frame's size; the image's shape may change"),
        (VbmFitMode.KeepAspect, "Keep aspect (transparent padding)", "Scale to fit inside the frame keeping the image's shape; the rest of the frame is transparent"),
        (VbmFitMode.CropCentre, "Crop the centre", "Scale to cover the frame keeping the image's shape; what sticks out is cut off evenly"),
    ];

    private readonly ComboBox _filter = new() { MinWidth = 260 }, _fit = new() { MinWidth = 260 };
    private readonly CheckBox _dontAsk = new() { Content = "_Use this every time without asking", Margin = new Thickness(0, 10, 0, 0) };
    private readonly Image _before = new() { Stretch = Stretch.Uniform }, _after = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _afterLabel = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly BgraImage _sample;
    private readonly int _width, _height;
    private readonly VbmPixelFormat _format;
    private readonly uint _version;

    /// <param name="images">The images that are another size (the first is previewed).</param>
    /// <param name="width">The bitmap's frame width.</param>
    /// <param name="height">The bitmap's frame height.</param>
    /// <param name="format">The bitmap's pixel format (the preview shows the image converted to it).</param>
    /// <param name="version">The bitmap's version (1555 stores alpha inverted in version 1).</param>
    /// <param name="initial">The choice the window starts with.</param>
    public VbmResizeWindow(IReadOnlyList<(string Name, BgraImage Image)> images, int width, int height, VbmPixelFormat format, uint version, VbmResizeOptions initial)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(initial);
        if (images.Count == 0) throw new ArgumentException("At least one image is needed.", nameof(images));
        _sample = images[0].Image;
        (_width, _height, _format, _version) = (width, height, format, version);
        Title = "Resize to the frame size";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");

        foreach (var (value, label, tip) in Filters) _filter.Items.Add(new ComboBoxItem { Content = label, Tag = value, ToolTip = tip });
        foreach (var (value, label, tip) in Fits) _fit.Items.Add(new ComboBoxItem { Content = label, Tag = value, ToolTip = tip });
        AutomationProperties.SetName(_filter, "Resize filter");
        AutomationProperties.SetName(_fit, "Fit");
        _filter.ToolTip = "How the pixels are resampled";
        _fit.ToolTip = "What happens when the image has another shape than the frame";
        _dontAsk.ToolTip = "Images of another size then use this choice straight away; Settings > Volition bitmaps turns the question back on";
        Select(initial);
        _filter.SelectionChanged += (_, _) => UpdatePreview();
        _fit.SelectionChanged += (_, _) => UpdatePreview();
        Content = Build(images);
        UpdatePreview();
    }

    /// <summary>The choice made with Resize, or null.</summary>
    public VbmResizeOptions? Result { get; private set; }

    /// <summary>True when "use this every time" was ticked.</summary>
    public bool DontAskAgain => _dontAsk.IsChecked == true;

    /// <summary>The choice the window shows now.</summary>
    public VbmResizeOptions Current() => new(
        _filter.SelectedItem is ComboBoxItem { Tag: VbmResizeFilter f } ? f : VbmResizeFilter.HighQuality,
        _fit.SelectedItem is ComboBoxItem { Tag: VbmFitMode m } ? m : VbmFitMode.Stretch);

    /// <summary>Shows <paramref name="options"/> (tests and screenshots).</summary>
    public void Select(VbmResizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _filter.SelectedItem = _filter.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, options.Filter));
        _fit.SelectedItem = _fit.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, options.Fit));
    }

    /// <summary>The frame the preview shows (tests).</summary>
    public BgraImage PreviewFrame { get; private set; } = null!;

    private UIElement Build(IReadOnlyList<(string Name, BgraImage Image)> images)
    {
        var c = CultureInfo.CurrentCulture;
        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = (images.Count == 1
                ? string.Format(c, "{0} is {1} x {2}", images[0].Name, _sample.Width, _sample.Height)
                : string.Format(c, "{0} images are another size (the first, {1}, is {2} x {3})", images.Count, images[0].Name, _sample.Width, _sample.Height))
                + string.Format(c, "; this bitmap's frames are {0} x {1}.", _width, _height),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void Add(string label, FrameworkElement content)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 12, 5) };
            text.SetResourceReference(StyleProperty, "DialogLabel");
            Grid.SetRow(text, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            content.Margin = new Thickness(0, 4, 0, 4);
            content.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(text);
            grid.Children.Add(content);
            row++;
        }
        Add("Filter", _filter);
        Add("Fit", _fit);
        stack.Children.Add(grid);

        var previews = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        var beforeLabel = new TextBlock { Text = string.Format(c, "Image ({0} x {1})", _sample.Width, _sample.Height), Margin = new Thickness(0, 0, 0, 4) };
        _afterLabel.Text = string.Format(c, "Frame ({0} x {1}, {2})", _width, _height, VbmDocumentShort(_format));
        foreach (var label in new[] { beforeLabel, _afterLabel }) label.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _before.Source = ImageData.ToBitmap(_sample, false);
        RenderOptions.SetBitmapScalingMode(_before, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_after, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(_before, "Image");
        AutomationProperties.SetName(_after, "Frame preview");
        var left = Panel(beforeLabel, _before, _sample.Width, _sample.Height);
        var right = Panel(_afterLabel, _after, _width, _height);
        Grid.SetColumn(right, 2);
        previews.Children.Add(left);
        previews.Children.Add(right);
        stack.Children.Add(previews);
        stack.Children.Add(_dontAsk);

        var ok = new Button { Content = "_Resize", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Resize the images this way and use them" };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, ToolTip = "Leave the bitmap as it is" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in new[] { ok, cancel }) { b.SetResourceReference(StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        ok.Click += (_, _) => { Result = Current(); DialogResult = true; };
        stack.Children.Add(buttons);
        return stack;
    }

    /// <summary>A label over a checkerboard box of up to 230 x 170 holding <paramref name="image"/> (scaled up in whole steps, down to fit).</summary>
    private static StackPanel Panel(TextBlock label, Image image, int width, int height)
    {
        const double MaxW = 230, MaxH = 170;
        double scale = Math.Min(MaxW / width, MaxH / height);
        if (scale > 1) scale = Math.Floor(scale);
        image.Width = Math.Max(1, Math.Round(width * scale));
        image.Height = Math.Max(1, Math.Round(height * scale));
        var checker = new Border { Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        checker.SetResourceReference(Border.BackgroundProperty, "Preview.CheckerBrush");
        var box = new Border { Height = MaxH + 12, BorderThickness = new Thickness(1), Child = checker, Padding = new Thickness(5) };
        box.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        box.SetResourceReference(Border.BackgroundProperty, "Preview.Background");
        return new StackPanel { Children = { label, box } };
    }

    private static string VbmDocumentShort(VbmPixelFormat format) => format switch
    {
        VbmPixelFormat.Argb1555 => "1555",
        VbmPixelFormat.Argb4444 => "4444",
        _ => "565",
    };

    private void UpdatePreview()
    {
        var fitted = VbmResize.Fit(_sample, _width, _height, Current());
        // as the frame will hold it: through the pixel format and back
        PreviewFrame = VbmEncoder.DecodeLevel(VbmEncoder.EncodeLevel(fitted, _format, _version), _width, _height, _format, _version);
        _after.Source = ImageData.ToBitmap(PreviewFrame, false);
    }
}
