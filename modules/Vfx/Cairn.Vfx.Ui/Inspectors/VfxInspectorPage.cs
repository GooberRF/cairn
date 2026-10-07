using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Ui.Controls;
using Cairn.Vfx.Docs;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Inspectors;

/// <summary>
/// Base of the editable inspector tabs: label/value rows built in code. The page is rebuilt only when
/// <see cref="StructureKey"/> changes (selection, section types, list lengths); otherwise every row re-reads its
/// value in place, so a spinner drag (coalesced edit) keeps its control. Values read across the selection: when
/// they differ the field shows indeterminate and an edit writes the new value to every selected section.
/// </summary>
public abstract class VfxInspectorPage : ScrollViewer
{
    protected readonly VfxDocument Doc;
    protected readonly StackPanel Body = new() { Margin = new Thickness(8, 4, 8, 8) };
    private readonly List<Action> _refreshers = [];
    private readonly List<FrameworkElement> _editors = [];
    private string? _key;
    private bool _reading;

    protected VfxInspectorPage(VfxDocument doc)
    {
        Doc = doc;
        Content = Body;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        doc.SceneChanged += (_, _) => Refresh();
        doc.Selection.Changed += (_, _) => Refresh();
        doc.FrameChanged += (_, _) => { if (UsesFrame) RefreshValues(); };
        Loaded += (_, _) => Refresh();
    }

    /// <summary>True when values depend on the playhead (per-frame fields).</summary>
    protected virtual bool UsesFrame => false;

    /// <summary>A string that changes whenever the rows must be rebuilt.</summary>
    protected abstract string StructureKey();

    /// <summary>Adds the rows (called on structure change).</summary>
    protected abstract void Build();

    /// <summary>Self-test access: the editor named <paramref name="label"/> (automation name), after a refresh.</summary>
    internal FrameworkElement? EditorFor(string label)
    {
        Refresh();
        return _editors.FirstOrDefault(e => AutomationProperties.GetName(e) == label);
    }

    public void Refresh()
    {
        var key = StructureKey();
        if (key != _key)
        {
            _key = key;
            Body.Children.Clear(); _refreshers.Clear(); _editors.Clear();
            Build();
        }
        RefreshValues();
    }

    private void RefreshValues()
    {
        _reading = true;
        try { foreach (var r in _refreshers) r(); }
        finally { _reading = false; }
        var why = VfxEditing.DisabledReason(Doc);
        foreach (var e in _editors)
        {
            e.IsEnabled = why is null;
            ToolTipService.SetShowOnDisabled(e, true);
            if (why is not null) e.ToolTip = why; else if (e.Tag is string tip) e.ToolTip = tip;
        }
    }

    protected VfxFile File => Doc.Current;

    /// <summary>Runs <paramref name="refresh"/> whenever values are re-read (scene, selection and, for per-frame pages, playhead changes).</summary>
    protected void Watch(Action refresh) => _refreshers.Add(refresh);

    protected static string Tip(string? docsId, string fallback) =>
        docsId is not null && VfxFormatDocs.Find(docsId) is { } f ? fallback + "\n\n" + f.Tooltip : fallback;

    protected void Header(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        AutomationProperties.SetName(t, text);
        Body.Children.Add(t);
    }

    protected Grid Row(string label, UIElement value, string tip)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 64, MaxWidth = 130 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        // labels wrap rather than clip at narrow widths; short editor names (kept as automation names and undo
        // labels) are spelt out here
        var shown = label.StartsWith("Tex ", StringComparison.Ordinal) ? "Texture " + label[4..].Replace(" rate", " playback rate") : label == "Fps" ? "Track frame rate (fps)" : label;
        var l = new TextBlock { Text = shown, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
        l.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        Grid.SetColumn(value, 1);
        g.Children.Add(l); g.Children.Add(value);
        Body.Children.Add(g);
        return g;
    }

    private T Editor<T>(T e, string label, string tip) where T : FrameworkElement
    {
        e.Tag = tip; e.ToolTip = tip;
        AutomationProperties.SetName(e, label);
        _editors.Add(e);
        return e;
    }

    /// <summary>A read-only value.</summary>
    protected void Fact(string label, Func<string> read, string? docsId = null)
    {
        var v = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        v.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        AutomationProperties.SetName(v, label);
        var tip = Tip(docsId, label);
        v.ToolTip = tip;
        Row(label, v, tip);
        _refreshers.Add(() => v.Text = read());
    }

    /// <summary>A number across the selection. Spinner drags coalesce into one undo step; typed values are one step each.</summary>
    protected NumericBox Number(string label, string tip, Func<IEnumerable<double>> read, Func<VfxFile, double, VfxFile> write,
        int decimals = 2, double step = 1, double min = double.MinValue, double max = double.MaxValue, string suffix = "", bool addRow = true)
    {
        var box = Editor(new NumericBox { Decimals = decimals, Step = step, Minimum = min, Maximum = max, Suffix = suffix }, label, tip);
        bool dragging = false;
        box.InteractionStarted += (_, _) => dragging = VfxEditing.Begin(Doc, "Edit " + label.ToLowerInvariant());
        box.InteractionEnded += (_, _) => { if (dragging) VfxEditing.Commit(Doc); dragging = false; };
        box.ValueChanged += (_, _) =>
        {
            if (_reading) return;
            double v = box.Value;
            if (dragging) VfxEditing.Update(Doc, f => write(f, v));
            else VfxEditing.Apply(Doc, "Edit " + label.ToLowerInvariant(), f => write(f, v));
        };
        _refreshers.Add(() =>
        {
            var vals = read().Distinct().ToList();
            box.IsIndeterminate = vals.Count > 1;
            if (vals.Count > 0) box.Value = vals[0];
        });
        if (addRow) Row(label, box, tip);
        return box;
    }

    /// <summary>A text field (committed on Enter or focus loss).</summary>
    protected TextBox Text(string label, string tip, Func<IEnumerable<string>> read, Func<VfxFile, string, VfxFile> write)
    {
        var box = Editor(new TextBox { VerticalContentAlignment = VerticalAlignment.Center }, label, tip);
        void Commit() { if (!_reading && box.Text != (string?)box.DataContext) VfxEditing.Apply(Doc, "Edit " + label.ToLowerInvariant(), f => write(f, box.Text.Trim())); }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); e.Handled = true; } };
        _refreshers.Add(() =>
        {
            var vals = read().Distinct().ToList();
            var text = vals.Count == 1 ? vals[0] : "";
            box.DataContext = text; box.Text = text;
        });
        Row(label, box, tip);
        return box;
    }

    /// <summary>A check box across the selection (three-state display when values differ).</summary>
    protected CheckBox Check(string label, string tip, Func<IEnumerable<bool>> read, Func<VfxFile, bool, VfxFile> write)
    {
        var box = Editor(new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 1, 0, 1), MinHeight = 24 }, label, tip);
        box.SetResourceReference(ForegroundProperty, "App.Text");
        box.Click += (_, _) =>
        {
            bool on = box.IsChecked != false;
            VfxEditing.Apply(Doc, (on ? "Set " : "Clear ") + label.ToLowerInvariant(), f => write(f, on));
            RefreshValues();
        };
        _refreshers.Add(() =>
        {
            var vals = read().Distinct().ToList();
            box.IsChecked = vals.Count == 1 ? vals[0] : null;
        });
        Body.Children.Add(box);
        return box;
    }

    /// <summary>A choice; <paramref name="editable"/> allows free text (committed on Enter or focus loss).</summary>
    protected ComboBox Choice(string label, string tip, Func<IReadOnlyList<string>> items, Func<IEnumerable<string>> read,
        Func<VfxFile, string, VfxFile> write, bool editable = false)
    {
        var box = Editor(new ComboBox { IsEditable = editable, IsTextSearchEnabled = true }, label, tip);
        void Commit(string? value)
        {
            if (_reading || value is null || value == (string?)box.DataContext) return;
            VfxEditing.Apply(Doc, "Edit " + label.ToLowerInvariant(), f => write(f, value));
        }
        box.SelectionChanged += (_, _) => { if (!box.IsEditable || box.IsDropDownOpen) Commit(box.SelectedItem as string); };
        box.DropDownClosed += (_, _) => Commit(box.SelectedItem as string ?? box.Text);
        box.LostKeyboardFocus += (_, e) => { if (box.IsEditable && !box.IsKeyboardFocusWithin) Commit(box.Text); };
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(box.Text); e.Handled = true; } };
        _refreshers.Add(() =>
        {
            var list = items();
            if (!box.Items.Cast<string>().SequenceEqual(list)) { box.Items.Clear(); foreach (var i in list) box.Items.Add(i); }
            var vals = read().Distinct().ToList();
            var v = vals.Count == 1 ? vals[0] : null;
            box.DataContext = v;
            box.SelectedItem = v is not null && list.Contains(v) ? v : null;
            if (editable) box.Text = v ?? "";
        });
        Row(label, box, tip);
        return box;
    }

    /// <summary>A row of command buttons.</summary>
    protected void Buttons(params (string Text, string Tip, Action Run)[] buttons)
    {
        var p = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var (text, tip, run) in buttons)
        {
            var b = Editor(new Button { Content = text, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 2, 8, 2) }, text, tip);
            b.Click += (_, _) => run();
            p.Children.Add(b);
        }
        Body.Children.Add(p);
    }

    protected static float F(double v) => (float)v;
}
