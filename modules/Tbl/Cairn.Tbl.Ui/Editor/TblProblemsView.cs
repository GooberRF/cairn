using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Ui.Documents;
using UiDocument = Cairn.Tbl.Ui.Documents.TblDocument;

namespace Cairn.Tbl.Ui.Editor;

/// <summary>
/// The bottom-pane Problems tab of one table: every diagnostic with its severity, code, line and message; a click
/// jumps to it, and diagnostics with quick fixes get a button per fix.
/// </summary>
public sealed class TblProblemsView : UserControl, IDisposable
{
    /// <summary>Rows beyond this are summarised (the editor still shows every squiggle).</summary>
    public const int MaxRows = 500;

    private readonly StackPanel _rows = new();
    private readonly TextBlock _summary = new() { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
    private UiDocument? _document;
    private int _shownVersion = -1;

    public TblProblemsView(UiDocument document)
    {
        _document = document;
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _rows };
        var root = new DockPanel();
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(_summary);
        root.Children.Add(scroll);
        Content = root;
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        System.Windows.Automation.AutomationProperties.SetName(this, "Problems");
        document.ModelChanged += OnModelChanged;
        Refresh();
    }

    /// <summary>The diagnostics listed.</summary>
    public IReadOnlyList<TblDiagnostic> Diagnostics { get; private set; } = [];

    private void OnModelChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Focuses the first row.</summary>
    public void FocusFirst()
    {
        if (_rows.Children.Count > 0 && _rows.Children[0] is FrameworkElement first) first.Focus();
    }

    private void Refresh()
    {
        if (_document is null) return;
        var model = _document.Model;
        if (model.Version == _shownVersion) return;
        _shownVersion = model.Version;
        Diagnostics = model.Diagnostics;
        _rows.Children.Clear();
        _summary.Text = model.Diagnostics.IsEmpty
            ? "No problems."
            : $"{Count(model.ErrorCount, "error")}, {Count(model.WarningCount, "warning")}, {model.InfoCount} information. Click a row to go to it.";
        var lines = new Cairn.Tbl.Text.LineMap(model.Text);
        foreach (var d in model.Diagnostics.Take(MaxRows)) _rows.Children.Add(Row(d, lines));
        if (model.Diagnostics.Length > MaxRows)
        {
            var more = new TextBlock { Text = $"{model.Diagnostics.Length - MaxRows} more not listed (F8 steps through all of them).", Margin = new Thickness(8, 4, 8, 4) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
            _rows.Children.Add(more);
        }
    }

    private static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private FrameworkElement Row(TblDiagnostic d, Cairn.Tbl.Text.LineMap lines)
    {
        var (line, column) = lines.PositionOf(d.Span.Start);
        string severityKey = d.Severity switch { TblSeverity.Error => "Severity.Error", TblSeverity.Warning => "Severity.Warning", _ => "Severity.Info" };
        string glyph = d.Severity switch { TblSeverity.Error => "", TblSeverity.Warning => "", _ => "" };

        var icon = new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextBlock.ForegroundProperty, severityKey);
        var code = new TextBlock { Text = d.Code, Width = 58, FontWeight = FontWeights.SemiBold };
        code.SetResourceReference(TextBlock.ForegroundProperty, severityKey);
        var where = new TextBlock { Text = $"Ln {line}, Col {column}", Width = 96, Opacity = 0.75 };
        where.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        var message = new TextBlock { Text = d.Message, TextWrapping = TextWrapping.Wrap, ToolTip = d.Help };
        message.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");

        var fixes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        foreach (var fix in UiDocument.FixesFor(d))
        {
            var button = new Button { Content = fix.Title, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Quick fix: " + fix.Title, MaxWidth = 280 };
            System.Windows.Automation.AutomationProperties.SetName(button, "Quick fix: " + fix.Title);
            var f = fix;
            button.Click += (_, e) => { e.Handled = true; _document?.ApplyQuickFix(d, f); };
            fixes.Children.Add(button);
        }

        var grid = new DockPanel { Margin = new Thickness(6, 2, 6, 2) };
        DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(code, Dock.Left); DockPanel.SetDock(where, Dock.Left); DockPanel.SetDock(fixes, Dock.Right);
        grid.Children.Add(icon); grid.Children.Add(code); grid.Children.Add(where); grid.Children.Add(fixes); grid.Children.Add(message);

        var row = new Border { Child = grid, Padding = new Thickness(2, 2, 2, 2), Focusable = true, Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent, ToolTip = d.Help ?? d.Message };
        System.Windows.Automation.AutomationProperties.SetName(row, $"{d.Severity} {d.Code} line {line}: {d.Message}");
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "App.Hover");
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => _document?.NavigateTo(d.Span.Start, d.Span.Length);
        row.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _document?.NavigateTo(d.Span.Start, d.Span.Length); e.Handled = true; } };
        return row;
    }

    public void Dispose()
    {
        if (_document is null) return;
        _document.ModelChanged -= OnModelChanged;
        _document = null;
        _rows.Children.Clear();
    }
}
