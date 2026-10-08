using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Formats.Imaging;
using Cairn.Ui.Controls;
using Cairn.Ui.Modules;

namespace Cairn.Vbm.Ui;

/// <summary>
/// Settings &gt; Volition bitmaps: what the New VBM window starts with (frame rate, pixel format, mipmaps) and how images
/// of another size are fitted to a bitmap's frames (also remembered from the resize window).
/// </summary>
public sealed class VbmSettingsPage : ISettingsPage
{
    private static readonly (VbmPixelFormat? Value, string Label)[] Formats =
    [
        (null, "Suggested from the images"),
        (VbmPixelFormat.Argb1555, VbmPixelFormat.Argb1555.DisplayName()),
        (VbmPixelFormat.Argb4444, VbmPixelFormat.Argb4444.DisplayName()),
        (VbmPixelFormat.Rgb565, VbmPixelFormat.Rgb565.DisplayName()),
    ];

    private static readonly (VbmResizeFilter Value, string Label)[] Filters =
        [(VbmResizeFilter.Nearest, "Nearest (sharp pixels)"), (VbmResizeFilter.Bilinear, "Bilinear (smooth)"), (VbmResizeFilter.HighQuality, "High quality")];

    private static readonly (VbmFitMode Value, string Label)[] Fits =
        [(VbmFitMode.Stretch, "Stretch to the frame"), (VbmFitMode.KeepAspect, "Keep aspect (transparent padding)"), (VbmFitMode.CropCentre, "Crop the centre")];

    private readonly VbmSettings _settings;
    private readonly StackPanel _view = new() { Margin = new Thickness(16) };

    /// <summary>The frame rate box (tests).</summary>
    internal readonly NumericBox Fps = new() { Minimum = 0, Maximum = VbmEditing.MaxFps, Step = 1, Decimals = 0, Suffix = " fps", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly ComboBox Format = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly CheckBox Mipmaps = new() { Content = "Make _mipmaps for new bitmaps" };
    internal readonly ComboBox Filter = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly ComboBox Fit = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly CheckBox Ask = new() { Content = "_Ask each time, with a preview" };

    public VbmSettingsPage(VbmSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        foreach (var (value, label) in Formats) Format.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        foreach (var (value, label) in Filters) Filter.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        foreach (var (value, label) in Fits) Fit.Items.Add(new ComboBoxItem { Content = label, Tag = value });

        Section("New bitmaps");
        Pair(("Frame rate", Fps, "The frame rate the New VBM window starts with"),
            ("Pixel format", Format, "Suggested: 565 when every pixel is opaque, 1555 when pixels are only fully see-through or solid, else 4444"));
        Check(Mipmaps, "Halves each frame down to 16 pixels, as the game's own textures do; interface images need none. "
            + "Frames added to or replaced in a bitmap always get that bitmap's mip levels, made again from the new image.");
        Section("Images of another size");
        Pair(("Filter", Filter, "How the pixels are resampled: nearest keeps hard pixel edges, bilinear and high quality blend"),
            ("Fit", Fit, "What happens when the image has another shape than the bitmap's frames"));
        Note("Used when an image is added, replaced, dropped or pasted at another size than the bitmap's frames.", 0);
        Check(Ask, "Shows the filter and fit with a preview before resizing; off, the choice above is used straight away.");
    }

    /// <summary>Two labelled controls side by side (tool tips explain them).</summary>
    private void Pair(params (string Label, Control Control, string Tip)[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (label, control, tip) in items)
        {
            var text = new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 2) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
            AutomationProperties.SetName(control, label);
            AutomationProperties.SetHelpText(control, tip);
            control.ToolTip = tip;
            row.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 20, 0), Children = { text, control } });
        }
        _view.Children.Add(row);
    }

    public string Title => "Volition bitmaps";

    public FrameworkElement View => _view;

    public void Load()
    {
        Fps.Value = _settings.DefaultFps;
        Pick(Format, _settings.DefaultFormat);
        Mipmaps.IsChecked = _settings.Mipmaps;
        var resize = _settings.Resize;
        Pick(Filter, resize.Filter);
        Pick(Fit, resize.Fit);
        Ask.IsChecked = _settings.AskResize;
    }

    public void Commit()
    {
        _settings.DefaultFps = (int)Math.Round(Fps.Value);
        if (Format.SelectedItem is ComboBoxItem format) _settings.DefaultFormat = format.Tag as VbmPixelFormat?;
        _settings.Mipmaps = Mipmaps.IsChecked == true;
        if (Filter.SelectedItem is ComboBoxItem { Tag: VbmResizeFilter filter } && Fit.SelectedItem is ComboBoxItem { Tag: VbmFitMode fit })
            _settings.Resize = new VbmResizeOptions(filter, fit);
        _settings.AskResize = Ask.IsChecked == true;
    }

    private static void Pick(ComboBox box, object? value) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value)) ?? box.Items[0];

    private void Section(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _view.Children.Count == 0 ? 0 : 16, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _view.Children.Add(t);
    }

    private void Check(CheckBox box, string note)
    {
        box.Margin = new Thickness(0, 6, 0, 2);
        box.ToolTip = note;
        AutomationProperties.SetHelpText(box, note);
        _view.Children.Add(box);
        Note(note, 24);
    }

    private void Note(string text, double indent)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(indent, 0, 0, 6) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _view.Children.Add(note);
    }
}
