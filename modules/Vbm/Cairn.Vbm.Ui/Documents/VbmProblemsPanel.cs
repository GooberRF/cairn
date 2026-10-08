using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cairn.Vbm.Ui.Documents;

/// <summary>The bottom "Problems" tab of a bitmap: what <see cref="VbmChecks"/> found, refreshed after every edit.</summary>
public sealed class VbmProblemsPanel : DockPanel
{
    /// <summary>The tab's id.</summary>
    public const string PanelId = "vbm.problems";

    private static readonly ConditionalWeakTable<VbmDocument, VbmProblemsPanel> Panels = new();
    private readonly VbmDocument _doc;
    private readonly Cairn.Ui.Controls.GridList _list = new();
    private readonly TextBlock _counts = new() { Margin = new Thickness(6, 3, 6, 3) };
    private readonly TextBlock _empty = new() { Text = "No problems found in this bitmap", Margin = new Thickness(12, 10, 12, 0), FontStyle = FontStyles.Italic, IsHitTestVisible = false };

    /// <summary>The panel of <paramref name="doc"/> (one per document).</summary>
    public static VbmProblemsPanel For(VbmDocument doc) => Panels.GetValue(doc, d => new VbmProblemsPanel(d));

    private VbmProblemsPanel(VbmDocument doc)
    {
        _doc = doc;
        AutomationProperties.SetName(_list, "Problems");
        _counts.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _list.Column("", 28, nameof(Row.Icon));
        _list.Column("Severity", 80, nameof(Row.Severity));
        _list.Column("Code", 70, nameof(Row.Code));
        _list.Column("Message", 760, nameof(Row.Message));
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _list.SelectedItem is Row row) { CopyRow(row); e.Handled = true; }
        };
        _list.ContextMenuOpening += (_, e) =>
        {
            if (_list.SelectedItem is not Row row) { e.Handled = true; return; }
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "_Copy message", Command = new Cairn.Ui.Mvvm.RelayCommand(() => CopyRow(row)) });
            _list.ContextMenu = menu;
        };
        DockPanel.SetDock(_counts, Dock.Top);
        Children.Add(_counts);
        Children.Add(new Grid { Children = { _list, _empty } });
        doc.PropertyChanged += OnDocumentChanged;
        Fill();
    }

    /// <summary>The rows shown (for tests).</summary>
    public IReadOnlyList<VbmProblem> Shown { get; private set; } = [];

    /// <summary>The list (for self-tests that check the columns render).</summary>
    internal ListView List => _list;

    private sealed record Row(VbmProblem Problem, string Icon, string Severity, string Code, string Message);

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VbmDocument.Problems)) Fill();
    }

    private void Fill()
    {
        var problems = _doc.Problems.OrderBy(p => p.Severity).ToList();
        Shown = problems;
        _list.ItemsSource = problems.Select(p => new Row(p, Icon(p.Severity), p.Severity.ToString(), p.Code, p.Message)).ToList();
        _empty.Visibility = problems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int errors = problems.Count(p => p.Severity == VbmSeverity.Error), warnings = problems.Count(p => p.Severity == VbmSeverity.Warning);
        _counts.Text = $"{Plural(errors, "error")}, {Plural(warnings, "warning")}, {Plural(problems.Count - errors - warnings, "note")}.";
    }

    private static string Icon(VbmSeverity severity) => severity switch
    {
        VbmSeverity.Error => "⛔",
        VbmSeverity.Warning => "⚠",
        _ => "ℹ",
    };

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static void CopyRow(Row row)
    {
        try { Clipboard.SetText($"{row.Code} {row.Message}"); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: nothing to do */ }
    }
}
