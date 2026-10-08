using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Ui.Views;

namespace Cairn.Vf.Ui.Documents;

/// <summary>
/// The font tab: a toolbar, the glyph grid (every glyph on its backdrop with its character; arrow keys move the
/// selection), the sample strip (text drawn with the font's own spacing and kerning, zoomed with sharp pixels) and the
/// inspector on the right (selected glyph and font facts).
/// </summary>
public sealed class VfDocumentView : Grid
{
    private readonly VfDocument _doc;
    private readonly ListBox _grid = new() { BorderThickness = new Thickness(0), Padding = new Thickness(6) };
    private readonly WrapPanelHolder _panel = new();
    private readonly TextBox _sampleBox = new() { MinWidth = 260, Margin = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _allToggle = new() { Content = "All characters", Margin = new Thickness(4, 2, 4, 2), Padding = new Thickness(7, 0, 7, 0), Height = 24 };
    // Wide enough for "8×": the theme's combo box keeps about 45 px for its arrow and padding, and a narrower box
    // clipped the "×" to half (it read as "3›").
    private readonly ComboBox _sampleZoom = new() { MinWidth = 96, Margin = new Thickness(4, 2, 4, 2) };
    private readonly ComboBox _gridZoom = new() { MinWidth = 96, Margin = new Thickness(4, 2, 4, 2) };
    private readonly ComboBox _backdrop = new() { Width = 90, Margin = new Thickness(4, 2, 4, 2) };
    private readonly Border _sampleHost = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
    private readonly TextBlock _sampleInfo = new() { Margin = new Thickness(8, 0, 8, 4) };
    private bool _syncing;

    public VfDocumentView(VfDocument doc)
    {
        _doc = doc;
        Inspector = new VfInspector(doc);
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 200 });

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 80 });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 90 });

        // Toolbar
        foreach (int z in Enumerable.Range(1, 6)) _gridZoom.Items.Add($"{z}×");
        foreach (int z in Enumerable.Range(1, 8)) _sampleZoom.Items.Add($"{z}×");
        foreach (var b in Enum.GetValues<VfBackdrop>()) _backdrop.Items.Add(b.ToString());
        Tip(_gridZoom, "Glyph grid zoom", "Zoom of the glyph grid (pixels stay sharp)");
        Tip(_sampleZoom, "Sample zoom", "Zoom of the sample text (1× to 8×, pixels stay sharp)");
        Tip(_backdrop, "Backdrop", "What glyphs are drawn on: stock fonts are white, so Dark shows them best");
        Tip(_sampleBox, "Sample text", "Text drawn with this font's spacing and kerning, as the game draws it");
        Tip(_allToggle, "All characters", "Show every character of the font instead of the sample text");
        _allToggle.SetResourceReference(StyleProperty, "ToolToggle");
        var bar = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };
        bar.Children.Add(Label("Glyphs")); bar.Children.Add(_gridZoom);
        bar.Children.Add(Label("Backdrop")); bar.Children.Add(_backdrop);
        var barBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder.SetResourceReference(Border.BackgroundProperty, "App.ChromeBackground");
        barBorder.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        left.Children.Add(barBorder);

        // Glyph grid
        AutomationProperties.SetName(_grid, "Glyphs");
        _grid.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, ScrollBarVisibility.Disabled);
        _grid.ItemsPanel = _panel.Template;
        _grid.SelectionChanged += (_, _) => { if (!_syncing && _grid.SelectedIndex >= 0) _doc.SelectedGlyph = _grid.SelectedIndex; };
        SetRow(_grid, 1);
        left.Children.Add(_grid);

        var split = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        split.SetResourceReference(StyleProperty, "HorizontalSplitter");
        SetRow(split, 2);
        left.Children.Add(split);

        // Sample strip
        var sampleBar = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };
        sampleBar.Children.Add(Label("Sample")); sampleBar.Children.Add(_sampleBox); sampleBar.Children.Add(_allToggle);
        sampleBar.Children.Add(Label("Zoom")); sampleBar.Children.Add(_sampleZoom);
        _sampleInfo.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        var sampleScroll = new ScrollViewer { Content = _sampleHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(sampleScroll, "Sample text preview");
        var sample = new DockPanel();
        var sampleTop = new Border { Child = sampleBar, BorderThickness = new Thickness(0, 1, 0, 1) };
        sampleTop.SetResourceReference(Border.BackgroundProperty, "App.ChromeBackground");
        sampleTop.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        DockPanel.SetDock(sampleTop, Dock.Top);
        DockPanel.SetDock(_sampleInfo, Dock.Bottom);
        sample.Children.Add(sampleTop); sample.Children.Add(_sampleInfo); sample.Children.Add(sampleScroll);
        SetRow(sample, 3);
        left.Children.Add(sample);
        Children.Add(left);

        var vsplit = new GridSplitter { Width = 5, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        vsplit.SetResourceReference(StyleProperty, "VerticalSplitter");
        SetColumn(vsplit, 1);
        Children.Add(vsplit);
        SetColumn(Inspector, 2);
        Children.Add(Inspector);

        // Wiring
        _sampleBox.Text = doc.SampleText;
        _sampleBox.TextChanged += (_, _) => { if (!_syncing) _doc.SampleText = _sampleBox.Text; };
        _allToggle.Click += (_, _) => _doc.ShowAllCharacters = _allToggle.IsChecked == true;
        _sampleZoom.SelectionChanged += (_, _) => { if (!_syncing && _sampleZoom.SelectedIndex >= 0) _doc.SampleZoom = _sampleZoom.SelectedIndex + 1; };
        _gridZoom.SelectionChanged += (_, _) => { if (!_syncing && _gridZoom.SelectedIndex >= 0) _doc.GridZoom = _gridZoom.SelectedIndex + 1; };
        _backdrop.SelectionChanged += (_, _) => { if (!_syncing && _backdrop.SelectedIndex >= 0) _doc.Backdrop = (VfBackdrop)_backdrop.SelectedIndex; };
        doc.FontChanged += (_, _) => { UpdateGrid(); RebuildSample(); };
        doc.SelectionChanged += (_, _) => SyncSelection();
        doc.ViewOptionsChanged += (_, _) => { SyncOptions(); UpdateGrid(); RebuildSample(); };
        // Arrow keys move the glyph selection anywhere in the tab except in the text box and the drop-downs.
        PreviewKeyDown += (_, e) => { if (!e.Handled && !InInput(e.OriginalSource as DependencyObject)) OnGridKey(_grid, e); };
        SyncOptions();
        RebuildGrid();
        RebuildSample();
        Loaded += (_, _) => { if (_grid.SelectedItem is ListBoxItem item) item.Focus(); };
    }

    /// <summary>The inspector on the right.</summary>
    public VfInspector Inspector { get; }

    /// <summary>The glyph grid (one item per glyph).</summary>
    public ListBox Grid => _grid;

    /// <summary>The image of the sample text (null when nothing is drawn).</summary>
    public Image? SampleImage => _sampleHost.Child as Image;

    /// <summary>The line under the sample ("312 × 12 px as the game measures it").</summary>
    public string SampleInfo => _sampleInfo.Text;

    /// <summary>Glyphs per row in the grid as laid out now (at least 1).</summary>
    public int Columns
    {
        get
        {
            double cell = _panel.ItemWidth;
            double width = _panel.Panel?.ActualWidth ?? 0;
            return cell <= 0 || width <= 0 ? 1 : Math.Max(1, (int)Math.Floor((width + 0.5) / cell));
        }
    }

    /// <summary>Moves the selection: <paramref name="dx"/> glyphs sideways, <paramref name="dy"/> rows.</summary>
    public void MoveSelection(int dx, int dy)
    {
        if (_doc.Current.GlyphCount == 0) return;
        int next = _doc.SelectedGlyph + dx + dy * Columns;
        if (next < 0 || next >= _doc.Current.GlyphCount) next = Math.Clamp(next, 0, _doc.Current.GlyphCount - 1);
        _doc.SelectedGlyph = next;
        if (_grid.ItemContainerGenerator.ContainerFromIndex(next) is ListBoxItem item) { item.BringIntoView(); if (_grid.IsKeyboardFocusWithin) item.Focus(); }
    }

    private void OnGridKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        (int dx, int dy)? move = e.Key switch
        {
            Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), Key.Down => (0, 1),
            Key.Home => (-_doc.SelectedGlyph, 0), Key.End => (_doc.Current.GlyphCount, 0),
            _ => null,
        };
        if (move is not { } m) return;
        MoveSelection(m.dx, m.dy);
        e.Handled = true;
    }

    private static bool InInput(DependencyObject? d)
    {
        for (; d is not null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBoxBase or ComboBox) return true;
        return false;
    }

    private void SyncOptions()
    {
        _syncing = true;
        _sampleZoom.SelectedIndex = _doc.SampleZoom - 1;
        _gridZoom.SelectedIndex = _doc.GridZoom - 1;
        _backdrop.SelectedIndex = (int)_doc.Backdrop;
        _allToggle.IsChecked = _doc.ShowAllCharacters;
        _sampleBox.IsEnabled = !_doc.ShowAllCharacters;
        if (_sampleBox.Text != _doc.SampleText) _sampleBox.Text = _doc.SampleText;
        _syncing = false;
    }

    private void SyncSelection()
    {
        _syncing = true;
        _grid.SelectedIndex = _doc.SelectedGlyph;
        _syncing = false;
        Highlight();
        if (_grid.SelectedItem is { } item) _grid.ScrollIntoView(item);
    }

    private VfFont? _shown;
    private VfBackdrop _shownBackdrop;
    private int _shownZoom, _highlighted = -1;

    /// <summary>After an edit: only the glyphs that changed are redrawn when the grid's layout stays the same (a paint stroke).</summary>
    private void UpdateGrid()
    {
        var font = _doc.Current;
        if (_shown is not { } old || old.GlyphCount != font.GlyphCount || old.MaxGlyphWidth != font.MaxGlyphWidth || old.Height != font.Height
            || old.FirstCharacter != font.FirstCharacter || old.Format != font.Format || old.Palette != font.Palette || _shownZoom != _doc.GridZoom || _shownBackdrop != _doc.Backdrop)
        {
            RebuildGrid();
            return;
        }
        _syncing = true;
        for (int i = 0; i < font.GlyphCount; i++)
        {
            var (a, b) = (old.Glyphs[i], font.Glyphs[i]);
            if (a.Pixels == b.Pixels && a.Width == b.Width && a.Spacing == b.Spacing) continue;
            _grid.Items[i] = BuildItem(font, i, _doc.GridZoom);
            if (i == _highlighted) _highlighted = -1;
        }
        _grid.SelectedIndex = _doc.SelectedGlyph;
        _syncing = false;
        _shown = font;
        Highlight();
    }

    private void RebuildGrid()
    {
        var font = _doc.Current;
        int zoom = _doc.GridZoom;
        double cellW = Math.Max(44, font.MaxGlyphWidth * zoom + 16), cellH = Math.Max(1, font.Height) * zoom + 34;
        _panel.ItemWidth = cellW;
        _panel.ItemHeight = cellH;
        _syncing = true;
        _grid.Items.Clear();
        for (int i = 0; i < font.GlyphCount; i++) _grid.Items.Add(BuildItem(font, i, zoom));
        _grid.SelectedIndex = _doc.SelectedGlyph;
        _syncing = false;
        (_shown, _shownZoom, _shownBackdrop, _highlighted) = (font, zoom, _doc.Backdrop, -1);
        Highlight();
    }

    private ListBoxItem BuildItem(VfFont font, int i, int zoom)
    {
        int code = font.CharacterOf(i);
        var swatch = new Border { Width = Math.Max(1, font.MaxGlyphWidth) * zoom, Height = Math.Max(1, font.Height) * zoom };
        VfImages.SetBackdrop(swatch, _doc.Backdrop);
        try
        {
            var glyph = VfRender.Glyph(font, i);
            if (glyph.Width > 0 && glyph.Height > 0) swatch.Child = VfImages.Image(glyph, zoom);
        }
        catch (VfImageTooLargeException) { /* far too large to show: the swatch stays empty, the Problems tab says why */ }
        // A frame in the accent colour marks the selected glyph on any backdrop (Highlight sets it).
        var frame = new Border { Child = swatch, BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center };
        string text = VfFont.CharacterText(code);
        var label = new TextBlock
        {
            Text = (text.Length == 1 ? text + "  " : "") + code.ToString(CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 11,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        var g = font.Glyphs[i];
        var item = new ListBoxItem
        {
            Content = new StackPanel { Children = { frame, label } },
            ToolTip = $"{Cairn.Vf.Formats.VfReader.Describe(code)}: width {g.Width}, spacing {g.Spacing}",
            Padding = new Thickness(2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(item, "Glyph " + Cairn.Vf.Formats.VfReader.Describe(code));
        return item;
    }

    private void Highlight()
    {
        static Border? Frame(object? item) => item is ListBoxItem { Content: StackPanel { Children.Count: > 0 } s } ? s.Children[0] as Border : null;
        if (_highlighted >= 0 && _highlighted < _grid.Items.Count && Frame(_grid.Items[_highlighted]) is { } old) old.BorderBrush = Brushes.Transparent;
        _highlighted = _doc.SelectedGlyph;
        if (_highlighted >= 0 && _highlighted < _grid.Items.Count && Frame(_grid.Items[_highlighted]) is { } now) now.SetResourceReference(Border.BorderBrushProperty, "App.Accent");
    }

    private void RebuildSample()
    {
        var font = _doc.Current;
        var layout = _doc.SampleLayout();
        VfImages.SetBackdrop(_sampleHost, _doc.Backdrop);
        // A damaged font can measure far beyond anything drawable: say so instead of drawing (the size check comes
        // before any allocation, so this costs nothing however often the sample is rebuilt).
        try { _sampleHost.Child = VfImages.Image(VfRender.Text(font, layout), _doc.SampleZoom); }
        catch (VfImageTooLargeException ex)
        {
            _sampleHost.Child = VfImages.Placeholder(ex.Message);
            _sampleInfo.Text = "Not drawn: " + ex.Message;
            return;
        }
        var missing = _doc.SampleBytes.Where(b => b >= 0x20 && font.IndexOf(b) < 0).Select(b => (int)b).Distinct().ToList();
        _sampleInfo.Text = string.Format(CultureInfo.CurrentCulture, "{0} × {1} px as the game measures it{2}{3}",
            layout.Width, layout.Height, layout.Right > layout.Width ? $" (glyphs reach {layout.Right} px)" : "",
            missing.Count == 0 ? "" : $". Not in the font, drawn as a {font.DefaultSpacing} px gap: "
                + string.Join(" ", missing.Take(16).Select(VfFont.CharacterText)) + (missing.Count > 16 ? $" and {missing.Count - 16} more" : ""));
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        return t;
    }

    private static void Tip(FrameworkElement e, string name, string tip)
    {
        AutomationProperties.SetName(e, name);
        e.ToolTip = tip;
    }

    /// <summary>The grid's items panel: a wrap panel with uniform cells, kept so its cell size and width can be read.</summary>
    private sealed class WrapPanelHolder
    {
        private double _w = 48, _h = 48;
        public WrapPanel? Panel { get; private set; }
        public ItemsPanelTemplate Template { get; }

        public WrapPanelHolder()
        {
            var factory = new FrameworkElementFactory(typeof(WrapPanel));
            factory.AddHandler(LoadedEvent, new RoutedEventHandler((s, _) => { Panel = (WrapPanel)s; Apply(); }));
            Template = new ItemsPanelTemplate(factory);
        }

        public double ItemWidth { get => _w; set { _w = value; Apply(); } }
        public double ItemHeight { get => _h; set { _h = value; Apply(); } }

        private void Apply()
        {
            if (Panel is null) return;
            Panel.ItemWidth = _w;
            Panel.ItemHeight = _h;
        }
    }
}
