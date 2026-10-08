using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Cairn.Ui.Controls;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Ui.Views;

namespace Cairn.Vf.Ui.Documents;

/// <summary>
/// The right-hand pane of a font tab: the selected glyph as a small pixel editor with its paint value, its metrics
/// (width, spacing and user data editable), its kerning pairs (add, change, remove) and its problems; then the font
/// (height and default spacing editable, the rest as facts) and an indexed font's palette. Every change is one undo
/// step; a spin of a number box is one step too.
/// </summary>
public sealed class VfInspector : ScrollViewer
{
    private readonly VfDocument _doc;
    private readonly StackPanel _stack = new() { Margin = new Thickness(10, 8, 10, 10) };
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _fields = new(StringComparer.Ordinal);
    private readonly VfGlyphEditor _editor;
    private readonly ToggleButton _pencil = new() { Content = "Pencil", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0), IsChecked = true };
    private readonly ToggleButton _eraser = new() { Content = "Eraser", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 8, 0) };
    private readonly NumericBox _paintValue = new() { Width = 84, Minimum = 0, Maximum = 14, Step = 1, Decimals = 0, Value = 14 };
    private readonly TextBox _paintHex = new() { Width = 64, Text = "FFFF", VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Border _paintSwatch = new() { Width = 18, Height = 18, Margin = new Thickness(6, 0, 0, 0), BorderThickness = new Thickness(1) };
    private readonly TextBox _kernChar = new() { Width = 54, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly NumericBox _kernOffset = new() { Width = 84, Minimum = -128, Maximum = 127, Step = 1, Decimals = 0, Value = -1 };
    private readonly ComboBox _kernSide = new() { MinWidth = 140, Margin = new Thickness(0, 0, 4, 0) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private bool _syncing;
    private int _builtGlyph = -1, _builtCount = -1;
    private VfPixelFormat _builtFormat;

    public VfInspector(VfDocument doc)
    {
        _doc = doc;
        _editor = new VfGlyphEditor(doc) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6) };
        AutomationProperties.SetName(_editor, "Glyph pixels");
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        AutomationProperties.SetName(this, "Inspector");
        Content = _stack;
        foreach (var t in new[] { _pencil, _eraser }) t.SetResourceReference(StyleProperty, "ToolToggle");
        _pencil.Click += (_, _) => SetEraser(false);
        _eraser.Click += (_, _) => SetEraser(true);
        _paintValue.ValueChanged += (_, _) => UpdatePaintValue();
        _paintHex.TextChanged += (_, _) => UpdatePaintValue();
        AutomationProperties.SetName(_paintValue, "Paint value");
        AutomationProperties.SetName(_paintHex, "Paint colour");
        _paintHex.ToolTip = "The colour the pencil paints, as 4 hex digits ARGB (F000 is solid black, FFFF solid white)";
        _kernSide.Items.Add("followed by");
        _kernSide.Items.Add("after");
        _kernSide.SelectedIndex = 0;
        _kernSide.ToolTip = "\"followed by\": the pair is this character then the other one; \"after\": the other one then this character";
        _kernChar.ToolTip = "The other character: type it, or # and its code (#65)";
        _kernOffset.ToolTip = "Pixels added to the first character's spacing (negative moves the second one closer)";
        AutomationProperties.SetName(_kernChar, "Kerning character");
        AutomationProperties.SetName(_kernOffset, "Kerning offset");
        AutomationProperties.SetName(_kernSide, "Kerning order");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Warning");
        _paintSwatch.SetResourceReference(Border.BorderBrushProperty, "App.Border");
        doc.SelectionChanged += (_, _) => Rebuild();
        doc.FontChanged += (_, _) => Rebuild();
        doc.ViewOptionsChanged += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>The value shown for a label ("Width", "Format"...), for self-tests; null when not shown.</summary>
    public string? ValueOf(string label) => _values.TryGetValue(label, out var v) ? v : null;

    /// <summary>The editable field of a label ("Width", "Spacing", "User data", "Height", "Default spacing"), for self-tests.</summary>
    public NumericBox? FieldOf(string label) => _fields.TryGetValue(label, out var f) ? f as NumericBox : null;

    /// <summary>The pixel editor.</summary>
    public VfGlyphEditor Editor => _editor;

    /// <summary>The kerning pair rows shown for the selected glyph ("'A' (65) + 'V' (86): -1"), for self-tests.</summary>
    public IReadOnlyList<string> KerningRows { get; private set; } = [];

    /// <summary>Kerning the game applies to the selected glyph without a pair of its own ("'A' (65) + 'V' (86): -3 (from …)"), for self-tests.</summary>
    public IReadOnlyList<string> PhantomRows { get; private set; } = [];

    /// <summary>The last message under the kerning list (a character not found...), or empty.</summary>
    public string Message => _message.Visibility == Visibility.Visible ? _message.Text : "";

    /// <summary>Adds the pair typed in the kerning row (as the Add button does).</summary>
    public bool AddKerningFromFields(string other, int offset, bool selectedFirst)
    {
        _kernChar.Text = other;
        _kernOffset.Value = offset;
        _kernSide.SelectedIndex = selectedFirst ? 0 : 1;
        return AddPair();
    }

    private void SetEraser(bool on)
    {
        _editor.Eraser = on;
        _pencil.IsChecked = !on;
        _eraser.IsChecked = on;
    }

    private void UpdatePaintValue()
    {
        var f = _doc.Current;
        int value;
        if (f.Format == VfPixelFormat.Rgba4444)
            value = int.TryParse(_paintHex.Text.Trim().TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? Math.Clamp(v, 0, 0xFFFF) : _editor.PaintValue;
        else value = (int)Math.Round(_paintValue.Value);
        _editor.PaintValue = value;
        var (b, g, r, a) = VfRender.Expand(VfRender.ToArgb4444(f, value));
        _paintSwatch.Background = new SolidColorBrush(Color.FromArgb(Math.Max(a, (byte)24), r, g, b));
    }

    private void Rebuild()
    {
        _syncing = true;
        try { RebuildCore(); }
        finally { _syncing = false; }
    }

    private void RebuildCore()
    {
        // During a paint stroke or a spin, and while a number box has the keyboard, only the values are refreshed (a
        // rebuild would take the mouse capture or the focus away).
        if ((_doc.History.IsCoalescing || _fields.Values.Any(f => f.IsKeyboardFocusWithin)) && RefreshValues()) return;
        _stack.Children.Clear();
        _values.Clear();
        _fields.Clear();
        var f = _doc.Current;
        int i = _doc.SelectedGlyph;
        (_builtGlyph, _builtCount, _builtFormat) = (i, f.GlyphCount, f.Format);
        if (i >= 0 && i < f.GlyphCount)
        {
            var g = f.Glyphs[i];
            int code = f.CharacterOf(i);
            Heading("Glyph");
            int zoom = Math.Clamp(Math.Min(160 / Math.Max(1, f.Height), 240 / Math.Max(1, f.MaxGlyphWidth)), 2, 16);
            _editor.Show(i, zoom);
            _stack.Children.Add(_editor);
            _stack.Children.Add(PaintRow(f));
            var rows = Rows();
            Row(rows, "Character", VfFont.CharacterText(code), "The character in the game's code page (Windows-1252)");
            Row(rows, "Code", $"{code} (0x{code:X2})", "Character code; glyph number " + i.ToString(CultureInfo.InvariantCulture));
            Number(rows, "Width", g.Width, 0, 255, " px", "Width of the glyph's pixels: narrower cuts columns off the right, wider adds clear columns",
                "Change width", (font, v) => VfEdits.WithWidth(font, i, v));
            Number(rows, "Spacing", g.Spacing, -255, 255, " px", "How far the pen moves after this character",
                "Change spacing", (font, v) => VfEdits.WithSpacing(font, i, v));
            Number(rows, "User data", g.UserData, 0, ushort.MaxValue, "", "A 16-bit field the game ignores (0 in every stock font)",
                "Change user data", (font, v) => VfEdits.WithUserData(font, i, (ushort)v));
            Row(rows, "Pixel offset", g.PixelOffset.ToString("N0", CultureInfo.CurrentCulture), "Where the glyph's pixels start in the pixel data, in bytes (set when saving)");
            Row(rows, "Kerning index", g.FirstKernIndex.ToString(CultureInfo.InvariantCulture), "This glyph's first kerning pair (-1 = none; set from the kerning table)");
            Kerning(f, i);
            foreach (var problem in _doc.Problems.Where(p => p.Glyph == i && p.Code != "VF046")) Note(problem); // VF046 is in the kerning list
        }

        Heading("Font");
        var fr = Rows();
        Row(fr, "Version", f.Version.ToString(CultureInfo.InvariantCulture), "Format version: 0 (older header, monochrome only) or 1");
        Row(fr, "Format", VfFont.FormatName(f.Format), "Pixel format (Font › Pixel Format converts it)");
        Number(fr, "Height", f.Height, 1, 255, " px", "The height of every glyph and of a line: taller adds clear rows at the bottom, shorter cuts rows off the bottom (Font › Change Height… can work at the top)",
            "Change height", (font, v) => VfEdits.WithHeight(font, v));
        Row(fr, "Glyphs", f.GlyphCount.ToString(CultureInfo.InvariantCulture), "Number of characters (Font › Add or Remove Characters… changes the range)");
        Row(fr, "Characters", f.GlyphCount == 0 ? "None" : $"{VfReader.Describe(f.FirstCharacter)} to {VfReader.Describe(f.LastCharacter)}", "Character range (glyph i is the first character + i)");
        Number(fr, "Default spacing", f.DefaultSpacing, -255, 255, " px", "The advance for characters the font has no glyph for",
            "Change default spacing", VfEdits.WithDefaultSpacing);
        Row(fr, "Widest glyph", $"{f.MaxGlyphWidth} px", "Width of the widest glyph");
        Row(fr, "Kerning pairs", f.Kerning.Length.ToString(CultureInfo.InvariantCulture), "Number of kerning pairs");
        long pixels = VfAtlas.PixelDataSize(f);
        Row(fr, "Pixel data", pixels.ToString("N0", CultureInfo.CurrentCulture) + " bytes", "Size of the pixel data block");
        var atlas = VfAtlas.Plan(f);
        Row(fr, "Texture", atlas.Fits ? $"{atlas.Size} × {atlas.Size} ({atlas.UsedHeight} px tall used)" : $"Too big for {atlas.Size} × {atlas.Size}", "The texture the game builds for the font when it loads it");
        foreach (var problem in _doc.Problems.Where(p => p.Code is "VF060" or "VF061")) Note(problem);
        if (f.Format == VfPixelFormat.Indexed && f.Palette.Length == VfFont.PaletteSize)
        {
            Heading("Palette");
            _stack.Children.Add(Palette(f));
        }
    }

    /// <summary>Updates the number boxes in place when the layout would not change (same glyph, same pairs); false otherwise.</summary>
    private bool RefreshValues()
    {
        var f = _doc.Current;
        int i = _doc.SelectedGlyph;
        if (i < 0 || i >= f.GlyphCount || i != _builtGlyph || f.GlyphCount != _builtCount || f.Format != _builtFormat) return false;
        var g = f.Glyphs[i];
        var want = new Dictionary<string, int> { ["Width"] = g.Width, ["Spacing"] = g.Spacing, ["User data"] = g.UserData, ["Height"] = f.Height, ["Default spacing"] = f.DefaultSpacing };
        foreach (var (label, value) in want)
        {
            if (_fields.TryGetValue(label, out var e) && e is NumericBox box)
            {
                if (!box.IsKeyboardFocusWithin && Math.Abs(box.Value - value) > 0.1) box.Value = value;
                _values[label] = value.ToString(CultureInfo.InvariantCulture) + box.Suffix;
            }
        }
        _editor.Show(i, _editor.Zoom);
        return true;
    }

    private StackPanel PaintRow(VfFont f)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(_pencil.Parent is Panel p1 ? Detach(p1, _pencil) : _pencil);
        row.Children.Add(_eraser.Parent is Panel p2 ? Detach(p2, _eraser) : _eraser);
        FrameworkElement value;
        if (f.Format == VfPixelFormat.Rgba4444) value = _paintHex;
        else
        {
            _paintValue.Maximum = VfPixelConvert.MaxValue(f.Format);
            if (_paintValue.Value > _paintValue.Maximum) _paintValue.Value = _paintValue.Maximum;
            _paintValue.ToolTip = f.Format == VfPixelFormat.Mono
                ? "Coverage the pencil paints: 0 (clear) to 14 (solid)"
                : "Palette entry the pencil paints (0 to 255)";
            value = _paintValue;
        }
        if (value.Parent is Panel p3) p3.Children.Remove(value);
        if (_paintSwatch.Parent is Panel p4) p4.Children.Remove(_paintSwatch);
        row.Children.Add(value);
        row.Children.Add(_paintSwatch);
        UpdatePaintValue();
        return row;

        static UIElement Detach(Panel panel, UIElement e) { panel.Children.Remove(e); return e; }
    }

    private void Kerning(VfFont f, int i)
    {
        Heading("Kerning");
        var pairs = f.Kerning.Where(k => k.Left == i || k.Right == i).ToList();
        KerningRows = [.. pairs.Select(k => Pair(f, k))];
        _values["Kerning"] = pairs.Count == 0 ? "None" : string.Join("\n", KerningRows);
        if (pairs.Count == 0)
        {
            var none = new TextBlock { Text = "No pairs with this character.", Margin = new Thickness(0, 0, 0, 4) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            _stack.Children.Add(none);
        }
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var k in pairs)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string text = $"{GlyphName(f, k.Left)} + {GlyphName(f, k.Right)}";
            string Short(int g) => g < f.GlyphCount ? VfFont.CharacterText(f.CharacterOf(g)) : $"#{g}";
            var label = new TextBlock { Text = $"{Short(k.Left)} + {Short(k.Right)}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text, FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
            var box = new NumericBox { Width = 84, Minimum = -128, Maximum = 127, Step = 1, Decimals = 0, Value = k.Offset, Margin = new Thickness(4, 1, 4, 1), ToolTip = "Offset in pixels (0 removes the pair)" };
            AutomationProperties.SetName(box, "Offset of " + text);
            int left = k.Left, right = k.Right;
            Wire(box, "Change kerning pair", (font, v) => VfEdits.WithKernPair(font, left, right, v));
            var remove = new Button { Content = "Remove", Padding = new Thickness(6, 1, 6, 1), ToolTip = "Remove this pair" };
            remove.SetResourceReference(StyleProperty, "ToolButton");
            remove.Click += (_, _) => _doc.SetKernPair(left, right, 0);
            AutomationProperties.SetName(remove, "Remove " + text);
            Grid.SetRow(label, r); Grid.SetRow(box, r); Grid.SetColumn(box, 1); Grid.SetRow(remove, r); Grid.SetColumn(remove, 2);
            grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(remove);
            if (k.Left >= 128 || k.Right >= 128)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var warn = new TextBlock { Text = "The game never applies this pair: it involves glyph number 128 or later.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
                warn.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Warning");
                Grid.SetRow(warn, r + 1); Grid.SetColumnSpan(warn, 3);
                grid.Children.Add(warn);
            }
        }
        _stack.Children.Add(grid);

        // Pairs the game applies to this character although nobody added them (its lookup runs into another pair).
        var phantoms = VfLayout.PhantomPairs(f).Where(ph => ph.Left == i || ph.Right == i).ToList();
        PhantomRows = [.. phantoms.Select(ph => $"{GlyphName(f, ph.Left)} + {GlyphName(f, ph.Right)}: {f.Kerning[ph.Pair].Offset:+0;-0;0} (from {Pair(f, f.Kerning[ph.Pair])})")];
        foreach (var (left, right, k) in phantoms)
        {
            var applied = f.Kerning[k];
            string Short(int g) => VfFont.CharacterText(f.CharacterOf(g));
            var warn = new TextBlock
            {
                Text = $"{Short(left)} + {Short(right)}: {applied.Offset:+0;-0;0} in the game, from the pair {Short(applied.Left)} + {Short(applied.Right)} (no pair of its own)",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3),
                ToolTip = Validation.VfValidator.PhantomText(f, left, right, applied),
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Warning");
            AutomationProperties.SetName(warn, "Kerning the game applies without a pair");
            _stack.Children.Add(warn);
        }

        foreach (var e in new FrameworkElement[] { _kernSide, _kernChar, _kernOffset }) if (e.Parent is Panel p) p.Children.Remove(e);
        var addButton = new Button { Content = "Add pair", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Add the pair, or change it when it exists" };
        addButton.SetResourceReference(StyleProperty, "ToolButton");
        addButton.Click += (_, _) => AddPair();
        AutomationProperties.SetName(addButton, "Add kerning pair");
        _kernOffset.Margin = new Thickness(0);
        _kernChar.Margin = new Thickness(0);
        var caption = new TextBlock { Text = "New pair: this character, the other one, the offset", Margin = new Thickness(0, 4, 0, 2) };
        caption.SetResourceReference(StyleProperty, "FactLabel");
        _stack.Children.Add(caption);
        _stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _kernSide, _kernChar } });
        _stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { _kernOffset, addButton } });
        if (_message.Parent is Panel mp) mp.Children.Remove(_message);
        _stack.Children.Add(_message);
        if (i >= 128)
        {
            var note = new TextBlock { Text = "This is glyph number 128 or later: the game never applies kerning pairs with it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Warning");
            _stack.Children.Add(note);
        }
    }

    private bool AddPair()
    {
        var f = _doc.Current;
        int i = _doc.SelectedGlyph;
        string text = _kernChar.Text.Trim();
        int code = -1;
        if (text.Length > 1 && text[0] == '#' && int.TryParse(text[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) code = n;
        else if (text.Length == 1 && VfFont.TextEncoding.GetBytes(text) is [var b]) code = b;
        else if (text.Equals("space", StringComparison.OrdinalIgnoreCase)) code = 32;
        int other = code is >= 0 and <= 255 ? f.IndexOf(code) : -1;
        string? problem = text.Length == 0 ? "Type the other character first."
            : other < 0 ? $"\"{text}\" is not a character of this font."
            : (int)Math.Round(_kernOffset.Value) == 0 ? "An offset of 0 does nothing; type the pixels to add (negative moves closer)."
            : null;
        ShowMessage(problem);
        if (problem is not null) return false;
        bool first = _kernSide.SelectedIndex != 1;
        int left = first ? i : other, right = first ? other : i;
        bool done = _doc.SetKernPair(left, right, (int)Math.Round(_kernOffset.Value));
        if (done)
        {
            _kernChar.Text = "";
            ShowMessage(left >= 128 || right >= 128 ? "Added, but the game never applies it: it involves glyph number 128 or later." : null);
        }
        return done;
    }

    private void ShowMessage(string? text)
    {
        _message.Text = text ?? "";
        _message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string GlyphName(VfFont f, int g) => g < f.GlyphCount ? VfReader.Describe(f.CharacterOf(g)) : $"glyph {g}";

    private static string Pair(VfFont f, VfKernPair k) => $"{GlyphName(f, k.Left)} + {GlyphName(f, k.Right)}: {k.Offset:+0;-0;0}";

    private UniformGrid Palette(VfFont f)
    {
        var grid = new UniformGrid { Columns = 16, Rows = 16, Width = 16 * 14, Height = 16 * 14, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6) };
        var used = new bool[256];
        foreach (var g in f.Glyphs) foreach (byte b in g.Pixels) used[b] = true;
        for (int n = 0; n < 256; n++)
        {
            var (b, gr, r, a) = VfRender.PaletteColour(f, n);
            var cell = new Border { Margin = new Thickness(0.5), BorderThickness = new Thickness(used[n] ? 0 : 0.5) };
            VfImages.SetBackdrop(cell, VfBackdrop.Checker);
            cell.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
            cell.Child = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(a, r, gr, b)) };
            cell.ToolTip = $"{n}: 0x{f.Palette[n]:X8}{(used[n] ? "" : " (not used)")}. Click to paint with it.";
            int index = n;
            cell.MouseLeftButtonDown += (_, _) => { _paintValue.Value = index; SetEraser(false); };
            grid.Children.Add(cell);
        }
        AutomationProperties.SetName(grid, "Palette");
        return grid;
    }

    private void Heading(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, _stack.Children.Count == 0 ? 0 : 12, 0, 4) };
        t.SetResourceReference(StyleProperty, "PaneHeaderText");
        _stack.Children.Add(t);
    }

    private Grid Rows()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _stack.Children.Add(g);
        return g;
    }

    private void Row(Grid grid, string label, string value, string tip)
    {
        _values[label] = value;
        var v = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(0) };
        v.SetResourceReference(ForegroundProperty, "App.Text");
        Place(grid, label, tip, v);
    }

    private void Number(Grid grid, string label, int value, int min, int max, string suffix, string tip, string undoLabel, Func<VfFont, int, VfFont> edit)
    {
        _values[label] = value.ToString(CultureInfo.InvariantCulture) + suffix;
        var box = new NumericBox { Minimum = min, Maximum = max, Step = 1, Decimals = 0, Suffix = suffix, Value = value, Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1), ToolTip = tip };
        _fields[label] = box;
        Wire(box, undoLabel, edit);
        Place(grid, label, tip, box);
    }

    /// <summary>Typed values are one step each; a spin (wheel, arrows) is one step for the whole gesture.</summary>
    private void Wire(NumericBox box, string undoLabel, Func<VfFont, int, VfFont> edit)
    {
        box.InteractionStarted += (_, _) => { if (!_syncing) _doc.BeginEdit(undoLabel); };
        box.InteractionEnded += (_, _) => _doc.CommitEdit();
        box.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            int v = (int)Math.Round(box.Value);
            string? refused = null;
            // Never throws: a refused value is shown here, a failure on a damaged font in an error dialog.
            _doc.EditValue(undoLabel, f => edit(f, v), m => refused = m);
            ShowMessage(refused);
        };
    }

    private static void Place(Grid grid, string label, string tip, FrameworkElement value)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, ToolTip = tip, Margin = new Thickness(0, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center };
        l.SetResourceReference(StyleProperty, "FactLabel");
        AutomationProperties.SetName(value, label);
        Grid.SetRow(l, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
        grid.Children.Add(l); grid.Children.Add(value);
    }

    private void Note(VfProblem problem)
    {
        var t = new TextBlock { Text = $"{problem.Code}: {problem.Message}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, problem.Severity switch
        {
            VfSeverity.Error => "Severity.Error",
            VfSeverity.Warning => "Severity.Warning",
            _ => "Severity.Info",
        });
        _stack.Children.Add(t);
    }
}
