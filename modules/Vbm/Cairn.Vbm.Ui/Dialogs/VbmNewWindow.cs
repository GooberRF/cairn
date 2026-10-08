using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Formats.Imaging;
using Cairn.Ui.Controls;

namespace Cairn.Vbm.Ui.Dialogs;

/// <summary>What the new-bitmap window settled on.</summary>
public sealed record VbmNewSettings(VbmPixelFormat Format, int Fps, int MipLevels, int Width, int Height);

/// <summary>
/// "New VBM": the images picked become the frames, in the order listed. Offers the size (the first image's by default;
/// the others are resized to it), the pixel format (suggested from the images' alpha), the frame rate and the mip levels.
/// </summary>
public sealed class VbmNewWindow : Window
{
    private readonly ComboBox _format = new() { MinWidth = 260 }, _mips = new() { MinWidth = 260 };
    private readonly NumericBox _fps = new() { Minimum = 0, Maximum = VbmEditing.MaxFps, Step = 1, Decimals = 0, Suffix = " fps", Width = 120, Value = 15, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly NumericBox _width = new() { Minimum = 1, Maximum = 4096, Step = 1, Decimals = 0, Width = 90 }, _height = new() { Minimum = 1, Maximum = 4096, Step = 1, Decimals = 0, Width = 90 };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly IReadOnlyList<(string Name, BgraImage Image)> _images;
    private readonly VbmResizeOptions _resize;

    /// <param name="images">The frames' images, in order, with their file names.</param>
    /// <param name="fps">The frame rate to start with.</param>
    /// <param name="format">The pixel format to start with; null = the one suggested for the images.</param>
    /// <param name="mipmaps">True to start with mipmaps (down to 16 pixels), false with none.</param>
    /// <param name="resize">How images of another size will be fitted (named in the note).</param>
    public VbmNewWindow(IReadOnlyList<(string Name, BgraImage Image)> images, int fps = VbmSettings.FallbackFps, VbmPixelFormat? format = null,
        bool mipmaps = false, VbmResizeOptions? resize = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0) throw new ArgumentException("At least one image is needed.", nameof(images));
        _images = images;
        _resize = resize ?? VbmResizeOptions.Default;
        Title = "New VBM";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");

        var suggested = VbmEditing.SuggestFormat(images.Select(i => i.Image));
        foreach (var f in new[] { VbmPixelFormat.Argb1555, VbmPixelFormat.Argb4444, VbmPixelFormat.Rgb565 })
            _format.Items.Add(new ComboBoxItem { Content = f.DisplayName() + (f == suggested ? " (suggested)" : ""), Tag = f });
        _format.SelectedItem = _format.Items.OfType<ComboBoxItem>().First(i => (VbmPixelFormat)i.Tag == (format ?? suggested));
        _format.ToolTip = "1555: on/off transparency; 4444: 16 levels of transparency; 565: opaque, best colour";
        _fps.Value = Math.Clamp(fps, 0, VbmEditing.MaxFps);
        _width.Value = images[0].Image.Width;
        _height.Value = images[0].Image.Height;
        foreach (var (box, name) in new (Control, string)[] { (_format, "Pixel format"), (_mips, "Mip levels"), (_fps, "Frame rate"), (_width, "Width"), (_height, "Height") })
            AutomationProperties.SetName(box, name);
        _mips.ToolTip = "Smaller copies of each frame for distant surfaces; interface images need none";
        FillMips();
        if (mipmaps) _mips.SelectedIndex = VbmResize.DefaultMipLevels(images[0].Image.Width, images[0].Image.Height) - 1;
        _width.ValueChanged += (_, _) => { FillMips(); Update(); };
        _height.ValueChanged += (_, _) => { FillMips(); Update(); };
        Content = Build();
        Update();
    }

    /// <summary>The settings chosen with Create, or null.</summary>
    public VbmNewSettings? Result { get; private set; }

    /// <summary>The settings the window shows now.</summary>
    public VbmNewSettings Current() => new(
        _format.SelectedItem is ComboBoxItem { Tag: VbmPixelFormat f } ? f : VbmPixelFormat.Argb4444,
        (int)Math.Round(_fps.Value),
        _mips.SelectedItem is ComboBoxItem { Tag: int m } ? m : 1,
        (int)Math.Round(_width.Value), (int)Math.Round(_height.Value));

    private void FillMips()
    {
        int keep = _mips.SelectedItem is ComboBoxItem { Tag: int m } ? m : 1;
        int w = Math.Max(1, (int)Math.Round(_width.Value)), h = Math.Max(1, (int)Math.Round(_height.Value));
        _mips.Items.Clear();
        for (int levels = 1; levels <= VbmFile.MaxMipLevels(w, h); levels++)
        {
            var (lw, lh) = VbmFile.LevelSize(w, h, levels - 1);
            _mips.Items.Add(new ComboBoxItem
            {
                Tag = levels,
                Content = levels == 1 ? "1 (no mipmaps)" : string.Format(CultureInfo.CurrentCulture, "{0} (down to {1} x {2})", levels, lw, lh),
            });
        }
        _mips.SelectedIndex = Math.Clamp(keep, 1, _mips.Items.Count) - 1;
    }

    private UIElement Build()
    {
        var stack = new StackPanel { Margin = new Thickness(14) };
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = _images.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, "One frame from {0} ({1} x {2}).", _images[0].Name, _images[0].Image.Width, _images[0].Image.Height)
                : string.Format(CultureInfo.CurrentCulture, "{0} frames, in this order: {1}{2}", _images.Count,
                    string.Join(", ", _images.Take(4).Select(i => i.Name)), _images.Count > 4 ? ", ..." : "."),
        };
        stack.Children.Add(intro);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void Add(string label, UIElement content)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 12, 5) };
            text.SetResourceReference(StyleProperty, "DialogLabel");
            Grid.SetRow(text, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            grid.Children.Add(text);
            grid.Children.Add(content);
            row++;
        }
        var by = new TextBlock { Text = "x", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        Add("Size", new StackPanel { Orientation = Orientation.Horizontal, Children = { _width, by, _height } });
        Add("Pixel format", _format);
        Add("Frame rate", _fps);
        Add("Mip levels", _mips);
        stack.Children.Add(grid);
        _note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        stack.Children.Add(_note);

        var create = new Button { Content = "_Create", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Make the bitmap in a new tab" };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, ToolTip = "Close without making a bitmap" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in new[] { create, cancel }) { b.SetResourceReference(StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        create.Click += (_, _) => { Result = Current(); DialogResult = true; };
        stack.Children.Add(buttons);
        return stack;
    }

    private void Update()
    {
        var s = Current();
        int resized = _images.Count(i => i.Image.Width != s.Width || i.Image.Height != s.Height);
        _note.Text = resized == 0 ? "Every image already has this size."
            : string.Format(CultureInfo.CurrentCulture, "{0} image{1} will be resized to {2} x {3} ({4}, {5}; change this in Settings > Volition bitmaps).",
                resized, resized == 1 ? "" : "s", s.Width, s.Height,
                _resize.Filter switch { VbmResizeFilter.Nearest => "nearest", VbmResizeFilter.Bilinear => "bilinear", _ => "high quality" },
                _resize.Fit switch { VbmFitMode.KeepAspect => "keeping the aspect", VbmFitMode.CropCentre => "cropping the centre", _ => "stretched" });
    }
}
