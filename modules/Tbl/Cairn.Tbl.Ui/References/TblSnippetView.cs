using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Cairn.Tbl.Model;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Ui.References;

/// <summary>
/// A read-only, selectable piece of table text coloured by token class with the theme's syntax colours (the definition
/// of an entry in another table, a removed stock entry). Re-colours itself when the theme changes.
/// </summary>
public sealed class TblSnippetView : RichTextBox
{
    private string _text = string.Empty;
    private ImmutableArray<TblClassifiedSpan> _classes = [];
    private int _offset;

    public TblSnippetView()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(6, 4, 6, 4);
        FontFamily = new FontFamily("Consolas, Courier New");
        FontSize = 12.5;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        SetResourceReference(BackgroundProperty, "Editor.Background");
        SetResourceReference(ForegroundProperty, "Editor.Foreground");
        AutomationProperties.SetName(this, "Table text");
        ToolTip = "Read-only table text";
        Document = new FlowDocument { PagePadding = new Thickness(0) };
    }

    /// <summary>The text shown.</summary>
    public string Text => _text;

    /// <summary>
    /// Shows <paramref name="text"/> (a slice of a table starting at <paramref name="offset"/>) coloured with
    /// <paramref name="classes"/> (the whole table's token classes; those outside the slice are ignored).
    /// </summary>
    public void Show(string text, ImmutableArray<TblClassifiedSpan> classes, int offset)
    {
        _text = text ?? string.Empty;
        _classes = classes.IsDefault ? [] : classes;
        _offset = offset;
        Render();
    }

    /// <summary>Re-applies the theme colours (call after a theme change).</summary>
    public void Render()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = FontSize * 1.3 };
        // No wrapping: the page is as wide as the longest line (monospace) so the horizontal bar appears only when needed.
        int longest = _text.Split('\n').Max(l => l.TrimEnd('\r').Replace("\t", "    ", StringComparison.Ordinal).Length);
        var doc = new FlowDocument(paragraph) { PagePadding = new Thickness(0), PageWidth = Math.Max(100, longest * FontSize * 0.56 + 24), FontFamily = FontFamily, FontSize = FontSize };
        int pos = 0;
        foreach (var c in _classes)
        {
            int start = c.Span.Start - _offset, end = c.Span.End - _offset;
            if (end <= 0) continue;
            if (start >= _text.Length) break;
            start = Math.Max(start, 0);
            end = Math.Min(end, _text.Length);
            if (start > pos) paragraph.Inlines.Add(new Run(_text[pos..start]));
            if (start < pos) start = pos;
            if (end > start) paragraph.Inlines.Add(Styled(new Run(_text[start..end]), c.Class));
            pos = Math.Max(pos, end);
        }
        if (pos < _text.Length) paragraph.Inlines.Add(new Run(_text[pos..]));
        Document = doc;
    }

    private Run Styled(Run run, TblTextClass cls)
    {
        string? key = cls switch
        {
            TblTextClass.Comment => "Syntax.CommentColor",
            TblTextClass.SectionHeader => "Syntax.TableColor",
            TblTextClass.FieldName => "Syntax.KeyColor",
            TblTextClass.String or TblTextClass.FileName or TblTextClass.RefName or TblTextClass.EntryName => "Syntax.StringColor",
            TblTextClass.Number => "Syntax.NumberColor",
            TblTextClass.Keyword => "Syntax.BooleanColor",
            TblTextClass.Brace => "Syntax.PunctuationColor",
            _ => null,
        };
        if (key is not null && TryFindResource(key) is Color color) run.Foreground = new SolidColorBrush(color);
        if (cls is TblTextClass.FileName or TblTextClass.RefName) run.TextDecorations = TextDecorations.Underline;
        if (cls is TblTextClass.EntryName or TblTextClass.SectionHeader) run.FontWeight = FontWeights.SemiBold;
        if (cls == TblTextClass.Comment) run.FontStyle = FontStyles.Italic;
        return run;
    }
}
