using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Formats.Imaging;
using Cairn.Ui.Services;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.List;

namespace Cairn.Vpp.Ui.Conversion;

/// <summary>
/// "Convert images to DDS": the selected images with their size and alpha, the settings, a live
/// before/after preview of the selected image and the size summary. Returns the chosen settings;
/// the conversion itself runs afterwards as the document's operation.
/// </summary>
public sealed class VppDdsConvertWindow : Window
{
    /// <summary>One image in the list (facts filled in on a worker).</summary>
    public sealed class Row(VppItem item) : Cairn.Ui.Mvvm.ObservableObject
    {
        private string _facts = "reading...";
        public VppItem Item { get; } = item;
        public string Name => Item.Name;
        public BgraImage? Image { get; set; }
        public AlphaKind Alpha { get; set; }
        public string? Error { get; set; }
        public string Facts { get => _facts; set => Set(ref _facts, value); }
    }

    private readonly VppDocument _doc;
    private readonly List<Row> _rows;
    private readonly IReadOnlyList<VppItem> _skipped;
    private readonly ListBox _list = new();
    private readonly ComboBox _format = new(), _mips = new(), _filter = new(), _quality = new(), _resize = new(), _rounding = new(), _existing = new(), _zoom = new(), _mip = new();
    private readonly TextBox _mipCount = new() { Width = 40 }, _folder = new() { MinWidth = 160 };
    private readonly CheckBox _premultiply = new() { Content = "Premultiply colour by alpha" }, _alphaOnly = new() { Content = "Alpha only" };
    private readonly RadioButton _replace = new() { Content = "Replace the originals in the packfile", GroupName = "out" },
        _keep = new() { Content = "Keep the originals too (add the .dds files)", GroupName = "out" },
        _toFolder = new() { Content = "Write to a folder:", GroupName = "out" };
    private readonly Image _before = new(), _after = new();
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap }, _summary = new() { TextWrapping = TextWrapping.Wrap },
        _clash = new() { TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _previewCts;
    private byte[]? _previewDds;
    private bool _loading = true;

    private VppDdsConvertWindow(VppDocument doc, IReadOnlyList<VppItem> images, IReadOnlyList<VppItem> skipped, DdsConvertSettings settings)
    {
        _doc = doc;
        _rows = images.Select(i => new Row(i)).ToList();
        _skipped = skipped;
        Title = "Convert images to DDS";
        Width = 1040; Height = 780; MinWidth = 760; MinHeight = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");
        Content = Build();
        Load(settings);
        _mipCount.IsEnabled = settings.Encode.Mips == DdsMipMode.Count;
        _loading = false;
        Loaded += async (_, _) => await ReadFactsAsync();
        Closed += (_, _) => _previewCts?.Cancel();
    }

    /// <summary>The settings chosen with Convert (null when cancelled).</summary>
    public DdsConvertSettings? Chosen { get; private set; }

    /// <summary>The rows (for self-tests).</summary>
    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>Creates the window (not shown) for <paramref name="images"/>.</summary>
    public static VppDdsConvertWindow Create(VppDocument doc, IReadOnlyList<VppItem> images, IReadOnlyList<VppItem> skipped) =>
        new(doc, images, skipped, DdsConvertSettings.Load(doc.Module.ConvertSettingsStore)) { Owner = doc.Shell.MainWindow };

    // ---- layout ----------------------------------------------------------------------------------------------

    private static T Themed<T>(T e) where T : Control
    {
        e.SetResourceReference(ForegroundProperty, "App.Text");
        return e;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };

    private static void Fill<T>(ComboBox box, string name, string tip, params (T Value, string Text)[] items)
    {
        foreach (var (value, text) in items) box.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        box.ToolTip = tip;
        box.Margin = new Thickness(0, 2, 0, 2);
        AutomationProperties.SetName(box, name);
    }

    private static T Value<T>(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: T v } ? v : default!;

    private static void Select<T>(ComboBox box, T value) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value)) ?? box.Items[0];

    private UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "The game loads name.dds in place of a requested name.tga, so tables and meshes need no changes. "
                + "Only formats the game loads are offered.",
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var convert = new Button { Content = "_Convert", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Convert the listed images with these settings" };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, ToolTip = "Close without converting" };
        foreach (var b in new[] { convert, cancel }) { b.SetResourceReference(StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        convert.Click += (_, _) => { Chosen = Current(); Chosen.Save(_doc.Module.ConvertSettingsStore); DialogResult = true; };
        var bottom = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_summary);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Images
        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        var skippedText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = _skipped.Count == 0 ? string.Empty : $"Skipped (not TGA/PNG/JPG): {string.Join(", ", _skipped.Take(8).Select(s => s.Name))}{(_skipped.Count > 8 ? $" and {_skipped.Count - 8} more" : "")}",
            Visibility = _skipped.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
        };
        skippedText.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        DockPanel.SetDock(skippedText, Dock.Bottom);
        left.Children.Add(skippedText);
        var imagesLabel = Label($"Images ({_rows.Count})");
        DockPanel.SetDock(imagesLabel, Dock.Top);
        left.Children.Add(imagesLabel);
        _list.ItemsSource = _rows;
        _list.ItemTemplate = RowTemplate();
        _list.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        _list.SetResourceReference(ForegroundProperty, "App.Text");
        _list.SetResourceReference(BorderBrushProperty, "App.Border");
        AutomationProperties.SetName(_list, "Images to convert");
        _list.SelectionChanged += (_, _) => { FillMipChoices(); _ = PreviewAsync(); };
        left.Children.Add(_list);
        grid.Children.Add(left);

        // Settings
        var settings = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        Fill(_format, "Format", "The DDS pixel format; Auto picks DXT1, DXT1 with 1-bit alpha or DXT5 from each image's alpha",
            Enum.GetValues<DdsTargetFormat>().Select(f => (f, DdsEncoder.Label(f))).ToArray());
        Fill(_mips, "Mipmaps", "Mip levels to write (the game uses them for distant surfaces and lower detail settings)",
            (DdsMipMode.Full, "Full chain"), (DdsMipMode.None, "None"), (DdsMipMode.Count, "At most N levels"));
        Fill(_filter, "Mip filter", "The filter used to make each smaller level (and to resize)",
            (ResampleFilter.Box, "Box"), (ResampleFilter.Triangle, "Triangle"), (ResampleFilter.Lanczos3, "Lanczos (sharper)"));
        Fill(_quality, "Quality", "Block-compression effort (DXT formats)",
            (DdsQuality.Fast, "Fast"), (DdsQuality.Balanced, "Balanced"), (DdsQuality.Best, "Best (slow)"));
        Fill(_resize, "Resize to a power of two", "DXT needs both sides a multiple of 4; power-of-two sizes are safe on every renderer",
            (DdsResize.WhenNeeded, "When the format needs it"), (DdsResize.Always, "Whenever a side is not a power of two"), (DdsResize.Never, "Never"));
        Fill(_rounding, "Rounding", "Which power of two a side is resized to",
            (PowerOfTwoRounding.Nearest, "Nearest"), (PowerOfTwoRounding.Larger, "Next larger"), (PowerOfTwoRounding.Smaller, "Next smaller"));
        Fill(_existing, "When a .dds exists", "What to do when a .dds of the new name already exists",
            (DdsExistingPolicy.Replace, "Replace it"), (DdsExistingPolicy.Skip, "Skip that image"));
        _mipCount.ToolTip = "The number of levels for \"At most N levels\"";
        AutomationProperties.SetName(_mipCount, "Mip level count");
        _premultiply.ToolTip = "Off: the game expects straight alpha. Only for textures drawn additively.";
        _folder.ToolTip = "The folder the .dds files are written to";
        AutomationProperties.SetName(_folder, "Output folder");
        var browse = new Button { Content = "...", MinWidth = 28, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Choose the output folder" };
        browse.SetResourceReference(StyleProperty, "DialogButton");
        browse.Click += (_, _) => { if (_doc.Shell.Dialogs.PickFolder(_folder.Text, "Write the .dds files to") is { } f) { _folder.Text = f; _toFolder.IsChecked = true; } };

        void Row(string label, UIElement control) { settings.Children.Add(Label(label)); settings.Children.Add(control); }
        Row("Format", _format);
        var mipRow = new DockPanel();
        DockPanel.SetDock(_mipCount, Dock.Right);
        _mipCount.Margin = new Thickness(6, 2, 0, 2);
        mipRow.Children.Add(_mipCount);
        mipRow.Children.Add(_mips);
        Row("Mipmaps", mipRow);
        Row("Mip filter", _filter);
        Row("Quality", _quality);
        Row("Resize to a power of two", _resize);
        Row("Rounding", _rounding);
        settings.Children.Add(Themed(_premultiply));
        _premultiply.Margin = new Thickness(0, 6, 0, 6);
        settings.Children.Add(Label("Output"));
        foreach (var r in new[] { _replace, _keep }) settings.Children.Add(Themed(r));
        var folderRow = new DockPanel();
        DockPanel.SetDock(_toFolder, Dock.Left);
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(Themed(_toFolder));
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);
        settings.Children.Add(folderRow);
        Row("When a .dds of that name exists", _existing);
        _clash.Margin = new Thickness(0, 4, 0, 0);
        _clash.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Warning");
        settings.Children.Add(_clash);
        var settingsScroll = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(settingsScroll, 1);
        grid.Children.Add(settingsScroll);

        // Preview
        var preview = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        Fill(_zoom, "Zoom", "Preview zoom", (0, "Fit"), (1, "100 %"), (2, "200 %"), (4, "400 %"));
        Fill<int>(_mip, "Mip level", "Which mip level of the result to show");
        _zoom.MinWidth = 70; _mip.MinWidth = 110;
        _alphaOnly.ToolTip = "Show the alpha channel as grey";
        _alphaOnly.Margin = new Thickness(10, 0, 0, 0);
        _alphaOnly.VerticalAlignment = VerticalAlignment.Center;
        tools.Children.Add(Label("Zoom"));
        tools.Children.Add(_zoom);
        tools.Children.Add(new TextBlock { Width = 10 });
        tools.Children.Add(Label("Level"));
        tools.Children.Add(_mip);
        tools.Children.Add(Themed(_alphaOnly));
        DockPanel.SetDock(tools, Dock.Top);
        preview.Children.Add(tools);
        DockPanel.SetDock(_result, Dock.Bottom);
        _result.Margin = new Thickness(0, 6, 0, 0);
        preview.Children.Add(_result);
        var pair = new UniformGrid { Columns = 2 };
        pair.Children.Add(Pane("Before", _before));
        pair.Children.Add(Pane("After", _after));
        preview.Children.Add(pair);
        Grid.SetColumn(preview, 2);
        grid.Children.Add(preview);
        root.Children.Add(grid);

        foreach (var box in new[] { _format, _mips, _filter, _quality, _resize, _rounding, _existing })
            box.SelectionChanged += (_, _) => SettingsChanged();
        _mipCount.TextChanged += (_, _) => SettingsChanged();
        _premultiply.Click += (_, _) => SettingsChanged();
        foreach (var r in new[] { _replace, _keep, _toFolder }) r.Checked += (_, _) => SettingsChanged();
        _zoom.SelectionChanged += (_, _) => ApplyZoom();
        _mip.SelectionChanged += (_, _) => { if (!_loading) _ = ShowResultLevelAsync(); };
        _alphaOnly.Click += (_, _) => _ = PreviewAsync();
        return root;
    }

    private Border Pane(string title, Image image)
    {
        image.Stretch = Stretch.None;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(image, title + " preview");
        var canvas = new Border { Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.SetResourceReference(Border.BackgroundProperty, "Preview.CheckerBrush");
        var scroll = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _scrolls.Add(scroll);
        var heading = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(heading, Dock.Top);
        var panel = new DockPanel { Margin = new Thickness(0, 0, 6, 0) };
        panel.Children.Add(heading);
        panel.Children.Add(scroll);
        var border = new Border { Child = panel, BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 0, 6, 0) };
        border.SetResourceReference(Border.BorderBrushProperty, "App.Border");
        return border;
    }

    private static DataTemplate RowTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(2));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Name)));
        var facts = new FrameworkElementFactory(typeof(TextBlock));
        facts.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Facts)));
        facts.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        panel.AppendChild(name);
        panel.AppendChild(facts);
        return new DataTemplate { VisualTree = panel };
    }

    // ---- settings --------------------------------------------------------------------------------------------

    private void Load(DdsConvertSettings s)
    {
        var e = s.Encode;
        Select(_format, e.Format); Select(_mips, e.Mips); Select(_filter, e.MipFilter); Select(_quality, e.Quality);
        Select(_resize, e.Resize); Select(_rounding, e.Rounding); Select(_existing, s.Existing); Select(_zoom, 0);
        _mipCount.Text = e.MipCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _premultiply.IsChecked = e.PremultiplyAlpha;
        _folder.Text = s.Folder ?? string.Empty;
        (s.Output switch { DdsOutputMode.KeepOriginals => _keep, DdsOutputMode.Folder => _toFolder, _ => _replace }).IsChecked = true;
    }

    /// <summary>The settings as shown.</summary>
    public DdsConvertSettings Current() => new()
    {
        Encode = new DdsEncodeOptions
        {
            Format = Value<DdsTargetFormat>(_format),
            Mips = Value<DdsMipMode>(_mips),
            MipCount = int.TryParse(_mipCount.Text, out var n) ? Math.Clamp(n, 1, 15) : 4,
            MipFilter = Value<ResampleFilter>(_filter),
            Quality = Value<DdsQuality>(_quality),
            Resize = Value<DdsResize>(_resize),
            Rounding = Value<PowerOfTwoRounding>(_rounding),
            PremultiplyAlpha = _premultiply.IsChecked == true,
        },
        Output = _toFolder.IsChecked == true ? DdsOutputMode.Folder : _keep.IsChecked == true ? DdsOutputMode.KeepOriginals : DdsOutputMode.ReplaceOriginals,
        Existing = Value<DdsExistingPolicy>(_existing),
        Folder = _folder.Text.Trim() is { Length: > 0 } f ? f : null,
    };

    /// <summary>Selects the image at <paramref name="index"/> (self-tests and screenshots).</summary>
    public void SelectRow(int index) => _list.SelectedIndex = Math.Clamp(index, 0, _rows.Count - 1);

    /// <summary>Applies settings as if chosen in the controls (self-tests and screenshots).</summary>
    public void Apply(DdsConvertSettings s) { _loading = true; Load(s); _loading = false; SettingsChanged(); }

    private void SettingsChanged()
    {
        if (_loading) return;
        _mipCount.IsEnabled = Value<DdsMipMode>(_mips) == DdsMipMode.Count;
        UpdateSummary();
        FillMipChoices();
        _ = PreviewAsync();
    }

    private void UpdateSummary()
    {
        var s = Current();
        long before = 0, after = 0;
        int failed = 0;
        foreach (var r in _rows)
        {
            before += r.Item.Size;
            if (r.Image is null) { if (r.Error is not null) failed++; continue; }
            after += EstimatedSize(r, s.Encode);
            r.Facts = $"{r.Image.Width} x {r.Image.Height} · {AlphaText(r.Alpha)} -> {DdsEncoder.Label(DdsEncoder.ResolveFormat(s.Encode.Format, r.Alpha))}";
        }
        _summary.Text = $"{_rows.Count} image(s): {VppEntryRow.FormatSize(before)} before, about {VppEntryRow.FormatSize(after)} after"
            + (failed > 0 ? $"; {failed} cannot be read" : "");
        var existing = s.Output == DdsOutputMode.Folder
            ? _rows.Where(r => s.Folder is { } f && File.Exists(Path.Combine(f, DdsConversion.DdsNameFor(r.Name)))).ToList()
            : _rows.Where(r => _doc.Current.IndexOf(DdsConversion.DdsNameFor(r.Name)) >= 0).ToList();
        _clash.Text = existing.Count == 0 ? string.Empty
            : $"{existing.Count} .dds file(s) of these names already exist ({string.Join(", ", existing.Take(4).Select(r => DdsConversion.DdsNameFor(r.Name)))}{(existing.Count > 4 ? ", ..." : "")}): "
              + (s.Existing == DdsExistingPolicy.Replace ? "they will be replaced." : "those images will be skipped.");
    }

    private static string AlphaText(AlphaKind a) => a switch
    {
        AlphaKind.Opaque => "opaque",
        AlphaKind.Binary => "1-bit alpha",
        _ => "smooth alpha",
    };

    private static long EstimatedSize(Row r, DdsEncodeOptions o)
    {
        var target = DdsEncoder.ResolveFormat(o.Format, r.Alpha);
        var (w, h) = DdsEncoder.TargetSize(r.Image!.Width, r.Image.Height, DdsEncoder.IsBlockCompressed(target), o);
        var format = DdsEncoder.EngineFormatOf(target);
        long total = 128;
        for (int i = 0, n = DdsEncoder.LevelCount(w, h, o); i < n; i++, w = Math.Max(w / 2, 1), h = Math.Max(h / 2, 1))
            total += DdsEncoder.LevelSize(w, h, format);
        return total;
    }

    // ---- facts and preview -----------------------------------------------------------------------------------

    private async Task ReadFactsAsync()
    {
        using var busy = BusyTracker.Begin("dds convert facts");
        await Task.Run(() => Parallel.ForEach(_rows, r =>
        {
            try
            {
                r.Image = ImageDecoder.Decode(r.Item.Source.ReadAll(), r.Name);
                r.Alpha = DdsEncoder.AnalyzeAlpha(r.Image);
            }
            catch (Exception ex) when (ex is ImageDecodeException or IOException or InvalidDataException)
            {
                r.Error = ex.Message;
                r.Facts = "cannot be read: " + ex.Message;
            }
        }));
        UpdateSummary();
        if (_rows.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
        else await PreviewAsync();
    }

    private void FillMipChoices()
    {
        if (_list.SelectedItem is not Row { Image: { } img } row) return;
        var o = Current().Encode;
        var target = DdsEncoder.ResolveFormat(o.Format, row.Alpha);
        var (w, h) = DdsEncoder.TargetSize(img.Width, img.Height, DdsEncoder.IsBlockCompressed(target), o);
        int keep = Math.Max(_mip.SelectedIndex, 0);
        bool was = _loading;
        _loading = true;
        _mip.Items.Clear();
        for (int i = 0, n = DdsEncoder.LevelCount(w, h, o); i < n; i++)
            _mip.Items.Add(new ComboBoxItem { Content = $"{i}: {Math.Max(w >> i, 1)} x {Math.Max(h >> i, 1)}", Tag = i });
        _mip.SelectedIndex = Math.Min(keep, _mip.Items.Count - 1);
        _loading = was;
    }

    /// <summary>Encodes the selected image with the current settings and shows before/after (last call wins).</summary>
    public async Task PreviewAsync()
    {
        _previewCts?.Cancel();
        if (_list.SelectedItem is not Row row) return;
        if (row.Image is not { } img)
        {
            _result.Text = row.Error is null ? "Reading..." : "Cannot be read: " + row.Error;
            _before.Source = _after.Source = null;
            return;
        }
        var cts = _previewCts = new CancellationTokenSource();
        var options = Current().Encode;
        bool alphaOnly = _alphaOnly.IsChecked == true;
        _before.Source = ToBitmap(img, alphaOnly);
        using var busy = BusyTracker.Begin("dds convert preview");
        try
        {
            var result = await Task.Run(() => DdsEncoder.EncodeDetailed(img, options, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _previewDds = result.Bytes;
            _result.Text = $"{DdsEncoder.Label(result.Target)} · {result.Width} x {result.Height}{(result.Resized ? " (resized)" : "")} · "
                + $"{result.MipCount} mip level{(result.MipCount == 1 ? "" : "s")} · {VppEntryRow.FormatSize(result.Bytes.Length)} (was {VppEntryRow.FormatSize(row.Item.Size)})";
            await ShowResultLevelAsync();
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException ex) { _result.Text = ex.Message; _after.Source = null; _previewDds = null; }
    }

    private async Task ShowResultLevelAsync()
    {
        if (_previewDds is not { } dds) return;
        int level = Math.Max(_mip.SelectedIndex, 0);
        bool alphaOnly = _alphaOnly.IsChecked == true;
        var decoded = await Task.Run(() => DdsEncoder.ExtractLevel(dds, level) is { } one ? DdsCodec.Decode(one, "preview.dds") : null);
        _after.Source = decoded is null ? null : ToBitmap(decoded, alphaOnly);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        int zoom = Value<int>(_zoom);
        // Fit: no scrolling, the image scales uniformly into the pane. Zoom: actual pixels times the factor, scrollable.
        var bars = zoom == 0 ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        foreach (var s in _scrolls) { s.HorizontalScrollBarVisibility = bars; s.VerticalScrollBarVisibility = bars; }
        foreach (var image in new[] { _before, _after })
        {
            if (image.Source is not BitmapSource source) continue;
            image.Stretch = zoom == 0 ? Stretch.Uniform : Stretch.Fill;
            image.Width = zoom == 0 ? double.NaN : source.PixelWidth * zoom;
            image.Height = zoom == 0 ? double.NaN : source.PixelHeight * zoom;
        }
    }

    private readonly List<ScrollViewer> _scrolls = [];

    private static BitmapSource ToBitmap(BgraImage img, bool alphaOnly)
    {
        var pixels = img.Pixels;
        if (alphaOnly)
        {
            pixels = new byte[img.Pixels.Length];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = img.Pixels[i + 3];
                pixels[i] = pixels[i + 1] = pixels[i + 2] = a;
                pixels[i + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, pixels, img.Stride);
        bmp.Freeze();
        return bmp;
    }
}
