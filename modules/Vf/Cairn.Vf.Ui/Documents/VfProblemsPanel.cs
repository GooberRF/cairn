using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;

namespace Cairn.Vf.Ui.Documents;

/// <summary>
/// The bottom "Problems" tab of a font: everything the reader and <see cref="Validation.VfValidator"/> found, errors
/// first. Double-click or Enter selects the glyph a problem concerns.
/// </summary>
public sealed class VfProblemsPanel : DockPanel
{
    private static readonly ConditionalWeakTable<VfDocument, VfProblemsPanel> Panels = new();
    private readonly VfDocument _doc;
    private readonly Cairn.Ui.Controls.GridList _list = new();
    private readonly TextBlock _counts = new() { Margin = new Thickness(6, 3, 6, 3) };
    private readonly TextBlock _empty = new() { Text = "No problems found in this font", Margin = new Thickness(12, 10, 12, 0), FontStyle = FontStyles.Italic, IsHitTestVisible = false };

    /// <summary>The panel of <paramref name="doc"/> (one per document).</summary>
    public static VfProblemsPanel For(VfDocument doc) => Panels.GetValue(doc, d => new VfProblemsPanel(d));

    private VfProblemsPanel(VfDocument doc)
    {
        _doc = doc;
        AutomationProperties.SetName(_list, "Problems");
        _list.Column("", 28, nameof(Row.Icon));
        _list.Column("Code", 64, nameof(Row.Code));
        _list.Column("Message", 720, nameof(Row.Message));
        _list.Column("Character", 120, nameof(Row.Location));
        _list.MouseDoubleClick += (_, _) => Go();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Go(); e.Handled = true; } };
        _counts.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        SetDock(_counts, Dock.Top);
        Children.Add(_counts);
        Children.Add(new Grid { Children = { _list, _empty } });
        doc.FontChanged += (_, _) => Fill();
        Fill();
    }

    /// <summary>The rows shown (for self-tests).</summary>
    public int Count => _list.Items.Count;

    /// <summary>The list (for self-tests that check the columns render).</summary>
    internal ListView List => _list;

    private sealed record Row(VfProblem Problem, string Icon, string Code, string Message, string Location);

    private void Fill()
    {
        var problems = _doc.Problems.OrderBy(p => p.Severity).ToList();
        var f = _doc.Current;
        _list.ItemsSource = problems.Select(p => new Row(p,
            p.Severity == VfSeverity.Error ? "⛔" : p.Severity == VfSeverity.Warning ? "⚠" : "ℹ",
            p.Code, p.Message,
            p.Glyph is int g && g < f.GlyphCount ? VfReader.Describe(f.CharacterOf(g)) : "Font")).ToList();
        _empty.Visibility = problems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int errors = problems.Count(p => p.Severity == VfSeverity.Error), warnings = problems.Count(p => p.Severity == VfSeverity.Warning);
        _counts.Text = $"{VfDocument.Plural(errors, "error")}, {VfDocument.Plural(warnings, "warning")}, {VfDocument.Plural(problems.Count - errors - warnings, "note")}. Double-click a problem to select its character.";
    }

    private void Go()
    {
        if (_list.SelectedItem is Row { Problem.Glyph: int g }) _doc.SelectedGlyph = g;
    }

    /// <summary>Focuses the list.</summary>
    public void FocusList() => _list.Focus();
}
