using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Cairn.Formats.Imaging;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ps2;
using Cairn.Vpp.Ui.Documents;

namespace Cairn.Vpp.Ui.Dialogs;

/// <summary>One .peg entry offered for conversion: its directory, or why it cannot be read.</summary>
/// <param name="Item">The entry.</param>
/// <param name="Index">Its position in the packfile.</param>
/// <param name="Pack">Its directory, or null when it cannot be read.</param>
/// <param name="Error">Why it cannot be read, or null.</param>
public sealed record PegPick(VppItem Item, int Index, PegFile? Pack, string? Error);

/// <summary>What the convert dialog chose: the textures of each .peg entry, black as transparent, keep the .peg entries.</summary>
/// <param name="Entries">The .peg entries and their ticked textures.</param>
/// <param name="BlackKey">Black as transparent for the MPEG-2 backgrounds, or null.</param>
/// <param name="KeepPegs">Keep the .peg entries in the packfile (the files are added after them).</param>
public sealed record PegConvertChoice(IReadOnlyList<PegEntryChoice> Entries, PegBlackKey? BlackKey, bool KeepPegs = false);

/// <summary>
/// "Convert to .tga": the textures of the selected .peg entries with a tick each (all ticked), what each becomes (an
/// animation's .atx, an MPEG-2 frame sequence's .atx), what happens when the packfile already has that name, and the
/// "treat black as transparent" choice for MPEG-2 backgrounds (from the settings). The conversion runs afterwards.
/// </summary>
public sealed class VppPegConvertWindow : Window
{
    /// <summary>One texture in the list.</summary>
    public sealed class Row(PegPick pick, PegTexture texture, bool canConvert) : Cairn.Ui.Mvvm.ObservableObject
    {
        private bool _isChecked = canConvert;
        /// <summary>The .peg entry.</summary>
        public PegPick Pick { get; } = pick;
        /// <summary>The texture.</summary>
        public PegTexture Texture { get; } = texture;
        /// <summary>False for a texture that cannot be converted (undecodable, or MPEG-2 with decoding off).</summary>
        public bool CanConvert { get; } = canConvert;
        /// <summary>Ticked: converted.</summary>
        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value && CanConvert); }
        public string Peg => Pick.Item.Name;
        public string Name => Texture.Name.Length > 0 ? Texture.Name : $"#{Texture.Index}";
        public string Size => string.Create(CultureInfo.InvariantCulture, $"{Texture.Width}×{Texture.Height}");
        public string Kind { get; init; } = string.Empty;
        public string Becomes { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
    }

    private readonly VppDocument _doc;
    private readonly IReadOnlyList<PegPick> _picks;
    private readonly List<Row> _rows = [];
    private readonly Cairn.Ui.Controls.GridList _list = new();
    private readonly CheckBox _black = new() { Content = "Treat _black as transparent (MPEG-2 backgrounds)" };
    private readonly CheckBox _soft = new() { Content = "_Soft edge" };
    private readonly CheckBox _keep = new() { Content = "_Keep the .peg entries in the packfile" };
    private readonly Slider _threshold = new() { Minimum = 0, Maximum = PegBlackKey.MaxThreshold, Width = 180, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _thresholdText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 150 };
    private readonly TextBlock _thresholdLabel = new() { Text = "Threshold", Margin = new Thickness(18, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _convert = new() { Content = "_Convert", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Convert the ticked textures (one undoable change)" };

    private VppPegConvertWindow(VppDocument doc, IReadOnlyList<PegPick> picks, bool decodeMpeg2, bool blackOn, int threshold, bool softEdge)
    {
        _doc = doc;
        _picks = picks;
        DecodeMpeg2 = decodeMpeg2;
        var readable = picks.Where(p => p.Pack is not null).ToList();
        Title = readable.Count == 1 ? $"Convert {readable[0].Item.Name} to .tga" : string.Create(CultureInfo.CurrentCulture, $"Convert {readable.Count:N0} PEG texture packs to .tga");
        Width = 1240; Height = 660; MinWidth = 720; MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");
        BuildRows(doc.Current);
        Content = Build();
        _black.IsChecked = blackOn;
        _threshold.Value = Math.Clamp(threshold, 0, PegBlackKey.MaxThreshold);
        _thresholdText.Text = VppSettingsPage.ThresholdText((int)_threshold.Value);
        _soft.IsChecked = softEdge;
        UpdateOptions();
        UpdateSummary();
    }

    /// <summary>Creates the window (not shown) for <paramref name="picks"/>, the black option from <paramref name="settings"/>.</summary>
    public static VppPegConvertWindow Create(VppDocument doc, IReadOnlyList<PegPick> picks, VppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(settings);
        return new VppPegConvertWindow(doc, picks, settings.DecodeMpeg2, settings.Mpeg2BlackTransparent, settings.Mpeg2BlackThreshold, settings.Mpeg2SoftEdge)
        {
            Owner = doc.Shell.MainWindow,
        };
    }

    /// <summary>The choice made with Convert (null when cancelled).</summary>
    public PegConvertChoice? Chosen { get; private set; }

    /// <summary>The rows (for self-tests).</summary>
    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>MPEG-2 backgrounds are decoded (the setting); else they are listed and left out.</summary>
    public bool DecodeMpeg2 { get; }

    /// <summary>"Treat black as transparent" (for self-tests and captures).</summary>
    public bool BlackTransparent { get => _black.IsChecked == true; set { _black.IsChecked = value; UpdateOptions(); } }

    /// <summary>The threshold shown (for self-tests).</summary>
    public int Threshold { get => (int)_threshold.Value; set => _threshold.Value = value; }

    /// <summary>The summary line (for self-tests).</summary>
    public string Summary => _summary.Text;

    /// <summary>"Keep the .peg entries" (for self-tests).</summary>
    public bool KeepPegs { get => _keep.IsChecked == true; set => _keep.IsChecked = value; }

    /// <summary>The choice as the dialog stands now (what Convert returns).</summary>
    public PegConvertChoice Current()
    {
        var entries = _picks.Where(p => p.Pack is not null)
            .Select(p => new PegEntryChoice(p.Index, _rows.Where(r => ReferenceEquals(r.Pick, p) && r.IsChecked).Select(r => r.Texture.Index).ToHashSet()))
            .ToList();
        var key = DecodeMpeg2 && _black.IsChecked == true ? new PegBlackKey((int)_threshold.Value, _soft.IsChecked == true) : null;
        return new PegConvertChoice(entries, key, _keep.IsChecked == true);
    }

    // ---- rows ------------------------------------------------------------------------------------------------

    private void BuildRows(VppPackage package)
    {
        var existing = Ps2Packfiles.ExistingNames(package, _picks.Select(p => p.Index));
        // which selected PEG files give each name (a texture several of them hold: the largest is kept)
        var givers = new Dictionary<string, List<string>>(VppNames.Comparer);
        foreach (var p in _picks)
            if (p.Pack is { } pack)
                foreach (var t in pack.Textures)
                {
                    string key = PegConverter.OutputName(t);
                    if (!givers.TryGetValue(key, out var list)) givers[key] = list = [];
                    if (!list.Contains(p.Item.Name, VppNames.Comparer)) list.Add(p.Item.Name);
                }
        foreach (var p in _picks)
        {
            if (p.Pack is not { } pack) continue;
            var atx = PegConverter.AtxNames(pack, p.Item.Name);
            foreach (var t in pack.Textures)
            {
                bool off = t.IsMpeg2 && !DecodeMpeg2;
                bool can = t.Problem is null && !off;
                string key = PegConverter.OutputName(t);
                string kind = t.IsMpeg2 ? "MPEG-2 background" : t.IsAnimated ? string.Create(CultureInfo.InvariantCulture, $"animated, {t.FrameCount} frames") : t.FormatLabel;
                string becomes = t.IsAnimated && !t.IsMpeg2
                    ? string.Create(CultureInfo.InvariantCulture, $"{key} + {t.FrameCount} .tga frames")
                    : atx.TryGetValue(t.Index, out var sequence) ? $"{key} (a frame of {sequence})" : key;
                var notes = new List<string>();
                if (t.Problem is { } problem) notes.Add("cannot be decoded: " + problem);
                else if (off) notes.Add("left out: MPEG-2 decoding is switched off in Settings > Packfiles");
                if (can && existing.Contains(key) && package.Find(key) is { } old)
                {
                    var size = Ps2Packfiles.EntrySize(package, old);
                    notes.Add(size is not { } s ? "the packfile already has this name (size unknown): left out"
                        : (long)t.Width * t.Height > (long)s.Width * s.Height
                            ? string.Create(CultureInfo.InvariantCulture, $"replaces the {s.Width}×{s.Height} already in the packfile")
                            : string.Create(CultureInfo.InvariantCulture, $"already in the packfile at {s.Width}×{s.Height}: left out"));
                }
                if (can && givers.TryGetValue(key, out var from) && from.Count > 1)
                    notes.Add("also in " + string.Join(", ", from.Where(n => !VppNames.Comparer.Equals(n, p.Item.Name)).Take(3)) + ": the largest is kept");
                var row = new Row(p, t, can) { Kind = kind, Becomes = becomes, Note = string.Join("; ", notes) };
                row.PropertyChanged += (_, _) => UpdateSummary();
                _rows.Add(row);
            }
        }
    }

    // ---- layout ----------------------------------------------------------------------------------------------

    private UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "Each ticked texture becomes a .tga the PC game loads; an animation becomes numbered .tga frames plus an .atx (Alpine Faction "
                + PegConverter.AtxAlpineSince + " or later). The .peg entries are replaced by the files unless you keep them. Undo (Ctrl+Z) brings everything back.",
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, ToolTip = "Close without converting" };
        foreach (var b in new[] { _convert, cancel }) { b.SetResourceReference(StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        AutomationProperties.SetName(_convert, "Convert");
        _convert.Click += (_, _) => { Chosen = Current(); DialogResult = true; };
        var bottom = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_summary);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // options
        var options = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        bool anyMpeg2 = _rows.Any(r => r.Texture.IsMpeg2);
        foreach (var c in new[] { _black, _soft, _keep }) c.SetResourceReference(ForegroundProperty, "App.Text");
        _black.ToolTip = "Pixels whose red, green and blue are all below the threshold become transparent; the backgrounds become 32-bit .tga with alpha (else 24-bit, no alpha)";
        _soft.ToolTip = "Pixels just above the threshold (up to twice it) become half transparent, as the PS2's second threshold would";
        _keep.ToolTip = "Off: each .peg entry is replaced by its files (the PC game does not load .peg files). On: the files are added after it.";
        _threshold.ToolTip = $"0 to {PegBlackKey.MaxThreshold}; {PegBlackKey.GameThreshold} is the value the PS2 game uses";
        AutomationProperties.SetName(_threshold, "Black threshold");
        _black.Click += (_, _) => UpdateOptions();
        _threshold.ValueChanged += (_, _) => _thresholdText.Text = VppSettingsPage.ThresholdText((int)_threshold.Value);
        _thresholdText.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        if (anyMpeg2 && DecodeMpeg2)
        {
            var blackRow = new StackPanel { Orientation = Orientation.Horizontal };
            _black.VerticalAlignment = VerticalAlignment.Center;
            blackRow.Children.Add(_black);
            _thresholdLabel.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
            blackRow.Children.Add(_thresholdLabel);
            blackRow.Children.Add(_threshold);
            blackRow.Children.Add(_thresholdText);
            _soft.Margin = new Thickness(12, 0, 0, 0);
            _soft.VerticalAlignment = VerticalAlignment.Center;
            blackRow.Children.Add(_soft);
            options.Children.Add(blackRow);
        }
        else if (anyMpeg2)
        {
            var off = new TextBlock { Text = "MPEG-2 backgrounds are left out: decoding them is switched off in Settings > Packfiles.", TextWrapping = TextWrapping.Wrap };
            off.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            options.Children.Add(off);
        }
        _keep.Margin = new Thickness(0, 6, 0, 0);
        options.Children.Add(_keep);
        var unreadable = _picks.Where(p => p.Pack is null).ToList();
        if (unreadable.Count > 0)
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                Text = "Cannot be read (they stay as they are): " + string.Join("; ", unreadable.Take(4).Select(p => $"{p.Item.Name}: {p.Error}")) + (unreadable.Count > 4 ? "; ..." : string.Empty),
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            options.Children.Add(text);
        }
        DockPanel.SetDock(options, Dock.Bottom);
        root.Children.Add(options);

        // tick all / none
        var ticks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        Button Link(string text, string tip, Action action)
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 14, 0) };
            b.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetName(b, text);
            b.Click += (_, _) => action();
            ticks.Children.Add(b);
            return b;
        }
        Link("Tick all", "Convert every texture that can be converted", () => { foreach (var r in _rows) r.IsChecked = true; });
        Link("Tick none", "Untick every texture", () => { foreach (var r in _rows) r.IsChecked = false; });
        if (_rows.Any(r => r.Texture.IsMpeg2) && _rows.Any(r => !r.Texture.IsMpeg2))
            Link("Only MPEG-2 backgrounds", "Tick the MPEG-2 backgrounds only", () => { foreach (var r in _rows) r.IsChecked = r.Texture.IsMpeg2; });
        DockPanel.SetDock(ticks, Dock.Bottom);
        root.Children.Add(ticks);

        // the textures
        _list.HideWhenEmpty = false;
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(Row.IsChecked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetBinding(IsEnabledProperty, new Binding(nameof(Row.CanConvert)));
        check.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(Row.Name)));
        _list.Columns.Add(new GridViewColumn { Header = "", Width = 34, CellTemplate = new DataTemplate { VisualTree = check } });
        if (_picks.Count(p => p.Pack is not null) > 1) _list.Column("PEG", 150, nameof(Row.Peg));
        _list.Column("Texture", 130, nameof(Row.Name));
        _list.Column("Size", 72, nameof(Row.Size));
        _list.Column("Kind", 205, nameof(Row.Kind));
        _list.Column("Becomes", 300, nameof(Row.Becomes));
        _list.Column("Note", 320, nameof(Row.Note));
        _list.ItemsSource = _rows;
        _list.SetResourceReference(BorderBrushProperty, "App.Border");
        _list.BorderThickness = new Thickness(1);
        AutomationProperties.SetName(_list, "Textures to convert");
        root.Children.Add(_list);
        return root;
    }

    private void UpdateOptions()
    {
        bool on = _black.IsChecked == true;
        _threshold.IsEnabled = _soft.IsEnabled = on;
        // the themed text keeps its colour when disabled: dim the controls that depend on the box
        foreach (var e in new FrameworkElement[] { _thresholdLabel, _threshold, _thresholdText, _soft }) e.Opacity = on ? 1 : 0.45;
    }

    private void UpdateSummary()
    {
        int ticked = _rows.Count(r => r.IsChecked), pegs = _picks.Count(p => p.Pack is not null);
        int mpeg2 = _rows.Count(r => r.IsChecked && r.Texture.IsMpeg2), animated = _rows.Count(r => r.IsChecked && r.Texture.IsAnimated && !r.Texture.IsMpeg2);
        var parts = new List<string>();
        if (animated > 0) parts.Add(string.Create(CultureInfo.CurrentCulture, $"{animated:N0} animated"));
        if (mpeg2 > 0) parts.Add(string.Create(CultureInfo.CurrentCulture, $"{mpeg2:N0} MPEG-2"));
        _summary.Text = string.Create(CultureInfo.CurrentCulture, $"{ticked:N0} of {_rows.Count:N0} textures ticked")
            + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : string.Empty)
            + string.Create(CultureInfo.CurrentCulture, $" in {pegs:N0} PEG texture pack{(pegs == 1 ? "" : "s")}.");
        _convert.IsEnabled = ticked > 0;
    }
}
