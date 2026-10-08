using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Formats.Audio;

namespace Cairn.Snd.Ui.Documents;

/// <summary>The bottom "Problems" tab of a sound: what decoding found (damaged data, unusual header values), errors first.</summary>
public sealed class SndProblemsPanel : DockPanel
{
    private static readonly ConditionalWeakTable<SndDocument, SndProblemsPanel> Panels = new();
    private readonly SndDocument _doc;
    private readonly Cairn.Ui.Controls.GridList _list = new();
    private readonly TextBlock _counts = new() { Margin = new Thickness(6, 3, 6, 3) };
    private readonly TextBlock _empty = new() { Text = "No problems found in this sound", Margin = new Thickness(12, 10, 12, 0), FontStyle = FontStyles.Italic, IsHitTestVisible = false };

    /// <summary>The panel of <paramref name="doc"/> (one per document).</summary>
    public static SndProblemsPanel For(SndDocument doc) => Panels.GetValue(doc, d => new SndProblemsPanel(d));

    private SndProblemsPanel(SndDocument doc)
    {
        _doc = doc;
        AutomationProperties.SetName(_list, "Problems");
        _list.Column("", 28, nameof(Row.Icon));
        _list.Column("Code", 64, nameof(Row.Code));
        _list.Column("Message", 820, nameof(Row.Message));
        _counts.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        SetDock(_counts, Dock.Top);
        Children.Add(_counts);
        Children.Add(new Grid { Children = { _list, _empty } });
        doc.SoundChanged += (_, _) => Fill();
        Fill();
    }

    /// <summary>The rows shown (for self-tests).</summary>
    public int Count => _list.Items.Count;

    /// <summary>The list (for self-tests that check the columns render).</summary>
    internal ListView List => _list;

    private sealed record Row(string Icon, string Code, string Message);

    private void Fill()
    {
        var problems = _doc.Sound.Problems.OrderByDescending(p => p.Severity).ToList();
        _list.ItemsSource = problems.Select(p => new Row(p.Severity == SoundSeverity.Error ? "⛔" : p.Severity == SoundSeverity.Warning ? "⚠" : "ℹ", p.Code, p.Message)).ToList();
        _empty.Visibility = problems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int errors = problems.Count(p => p.Severity == SoundSeverity.Error), warnings = problems.Count(p => p.Severity == SoundSeverity.Warning);
        _counts.Text = $"{SndDocument.Plural(errors, "error")}, {SndDocument.Plural(warnings, "warning")}, {SndDocument.Plural(problems.Count - errors - warnings, "note")}. The sound is decoded as well as the data allows.";
    }

    /// <summary>Focuses the list.</summary>
    public void FocusList() => _list.Focus();
}
