using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cairn.Rfa.Linting;
using Cairn.Ui.Modules;
using Cairn.Vfx.Linting;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Problems;

/// <summary>
/// Bottom "Problems" tab: <see cref="VfxLinter"/> re-run (off the UI thread, 300 ms debounce) on every
/// snapshot change; double-click / Enter selects the location; quick fixes in the context menu and the
/// Fix button are applied as undoable edits.
/// </summary>
public sealed class VfxProblemsPanel : DockPanel
{
    private static readonly ConditionalWeakTable<VfxDocument, VfxProblemsPanel> Panels = new();
    private readonly IShellContext _shell;
    private readonly VfxDocument _doc;
    private readonly Cairn.Ui.Controls.GridList _list = new();
    private readonly TextBlock _empty = new() { Text = "No problems found in this effect", Margin = new Thickness(12, 10, 12, 0), FontStyle = FontStyles.Italic, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _counts = new() { Margin = new Thickness(6, 3, 6, 3) };
    private readonly Button _fix = new() { Content = "_Fix", Margin = new Thickness(4, 2, 4, 2), MinWidth = 60, IsEnabled = false, ToolTip = "Apply the first quick fix of the selected problem (undoable)" };
    private readonly DispatcherTimer _timer;
    private int _generation;

    public static VfxProblemsPanel For(IShellContext shell, VfxDocument doc) => Panels.GetValue(doc, d => new VfxProblemsPanel(shell, d));

    /// <summary>Current diagnostics (for self-tests).</summary>
    public IReadOnlyList<VfxDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>The list (for self-tests that check the columns render).</summary>
    internal ListView List => _list;

    /// <summary>The Fix button (for self-tests).</summary>
    internal Button FixButton => _fix;
    public event EventHandler? Updated;

    private VfxProblemsPanel(IShellContext shell, VfxDocument doc)
    {
        _shell = shell; _doc = doc;
        _counts.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        AutomationProperties.SetName(_list, "Problems");
        AutomationProperties.SetName(_fix, "Fix");
        // the themed push button shows "_Fix" as Fix with an access key (as the other code-built buttons do)
        _fix.SetResourceReference(StyleProperty, "PushButton");
        _fix.Padding = new Thickness(10, 2, 10, 2);
        _list.Column("", 28, "Icon");
        _list.Column("Code", 70, "Code");
        _list.Column("Message", 520, "Message");
        _list.Column("Location", 220, "Location");
        _list.MouseDoubleClick += (_, _) => Go();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Go(); e.Handled = true; } };
        _list.SelectionChanged += (_, _) => _fix.IsEnabled = Selected?.Diagnostic.QuickFixes.Count > 0;
        _list.ContextMenuOpening += (_, e) => { if (BuildMenu() is { } m) _list.ContextMenu = m; else e.Handled = true; };
        _fix.Click += (_, _) => { if (Selected?.Diagnostic.QuickFixes is { Count: > 0 } fixes) ApplyFix(fixes[0]); };
        var bar = new DockPanel { Children = { _fix, _counts } };
        DockPanel.SetDock(_fix, Dock.Right);
        DockPanel.SetDock(bar, Dock.Top);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        Children.Add(bar); Children.Add(new Grid { Children = { _list, _empty } });
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => { _timer!.Stop(); Run(); }, Dispatcher);
        doc.SceneChanged += (_, _) => { _timer.Stop(); _timer.Start(); };
        Run();
    }

    private sealed record Row(VfxDiagnostic Diagnostic, string Icon, string Code, string Message, string Location);
    private Row? Selected => _list.SelectedItem as Row;

    /// <summary>Lints the current snapshot now (off the UI thread).</summary>
    public async void Run()
    {
        // pending work for captures and for checks that a closed effect is released (the lint keeps this panel and its document)
        using var busy = Cairn.Ui.Services.BusyTracker.Begin("effect lint");
        int generation = ++_generation;
        var file = _doc.Current;
        var context = new VfxLintContext(_doc.DisplayName, _shell.Assets.ResolverFor(_doc.FilePath is null ? null : Path.GetDirectoryName(_doc.FilePath)));
        IReadOnlyList<VfxDiagnostic> result;
        try { result = await Task.Run(() => VfxLinter.Lint(file, context)); }
        catch (Exception ex) { _counts.Text = "Lint failed: " + ex.Message; return; }
        if (generation != _generation) return;
        Diagnostics = result;
        _list.ItemsSource = result.OrderBy(d => d.Severity == DiagnosticSeverity.Error ? 0 : d.Severity == DiagnosticSeverity.Warning ? 1 : 2)
            .Select(d => new Row(d, d.Severity == DiagnosticSeverity.Error ? "⛔" : d.Severity == DiagnosticSeverity.Warning ? "⚠" : "ℹ",
                d.Code, d.Message, d.Location.Display)).ToList();
        _empty.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int errors = result.Count(d => d.Severity == DiagnosticSeverity.Error), warnings = result.Count(d => d.Severity == DiagnosticSeverity.Warning);
        _counts.Text = $"{Plural(errors, "error")}, {Plural(warnings, "warning")}, {Plural(result.Count - errors - warnings, "note")}. Double-click to select the object; right-click for fixes.";
        _doc.ProblemStatus = errors + warnings == 0 ? [] :
            [new($"{(errors > 0 ? Plural(errors, "error") : "")}{(errors > 0 && warnings > 0 ? ", " : "")}{(warnings > 0 ? Plural(warnings, "warning") : "")}",
                "Problems found in this effect: click to open the Problems tab", new Cairn.Ui.Mvvm.RelayCommand(ShowTab))];
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>Brings this panel's bottom tab to the front (the tab that hosts it).</summary>
    public void ShowTab()
    {
        // The shell selects the tab and shows the bottom pane if it was hidden; the list is focused once laid out.
        if (_shell.ShowPanel(PanelId)) _shell.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _list.Focus());
        else _list.Focus();
    }

    /// <summary>The bottom Problems tab's id.</summary>
    public const string PanelId = "vfx.problems";

    private void Go()
    {
        if (Selected?.Diagnostic.Location is not { } loc) return;
        if (loc.Section is int s) _doc.Selection.Select(s);
        if (loc.Material is int m) _doc.Selection.Material = m;
    }

    private ContextMenu? BuildMenu()
    {
        if (Selected is not { } row) return null;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "_Go to location", Command = new Cairn.Ui.Mvvm.RelayCommand(Go) });
        foreach (var fix in row.Diagnostic.QuickFixes)
            menu.Items.Add(new MenuItem { Header = fix.Label, ToolTip = "Quick fix (undoable)", Command = new Cairn.Ui.Mvvm.RelayCommand(() => ApplyFix(fix)) });
        menu.Items.Add(new MenuItem { Header = "_Copy message", Command = new Cairn.Ui.Mvvm.RelayCommand(() => Clipboard.SetText($"{row.Code} {row.Message} ({row.Location})")) });
        return menu;
    }

    /// <summary>Applies a quick fix as one undo step.</summary>
    public bool ApplyFix(VfxQuickFix fix) => _doc.Apply(fix.Label, fix.Apply);
}
