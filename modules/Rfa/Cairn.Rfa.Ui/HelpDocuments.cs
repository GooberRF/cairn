using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Cairn.Rfa.Docs;

namespace Cairn.Rfa.Ui;

/// <summary>
/// The Help windows' content, built as <see cref="FlowDocument"/>s. The format reference is generated
/// from <see cref="FormatDocs"/> — the same text the inspector's tooltips show — so the two can never
/// disagree, and the shortcut list from <see cref="Shortcuts.All"/>, the table that installs the key
/// bindings. Colours are resource references, so both pages follow a theme change in place.
/// </summary>
public static class HelpDocuments
{
    private const double BodySize = 13;

    /// <summary>Help › RFA &amp; V3C Format Reference.</summary>
    public static FlowDocument FormatReference()
    {
        var document = NewDocument();
        Add(document, Heading("RFA & V3C format reference"));
        Add(document, Body(
            "Every stored field of Red Faction's animation clips (.rfa) and meshes (.v3c, .v3m), what the game does with it "
            + "and its limits. The same text is what the inspector's tooltips show. Facts come from the format research, "
            + "RF.exe's disassembly and Alpine Faction's source."));
        AddReference(document, FormatDocs.Rfa);
        AddReference(document, FormatDocs.V3c);

        Add(document, Heading("How the engine plays clips"));
        foreach (var topic in FormatDocs.EngineTopics)
        {
            Add(document, Heading2(topic.Title));
            foreach (string paragraph in topic.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(document, Body(paragraph));
            }
        }
        return document;
    }

    private static void AddReference(FlowDocument document, FormatReference reference)
    {
        Add(document, Heading(reference.Title));
        Add(document, Body(reference.Summary));
        foreach (var section in reference.Sections)
        {
            Add(document, Heading2(section.Title));
            Add(document, Body(section.Summary));
            if (section.Fields.Length == 0) continue;

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12) };
            table.Columns.Add(new TableColumn { Width = new GridLength(0.22, GridUnitType.Star) });
            table.Columns.Add(new TableColumn { Width = new GridLength(0.20, GridUnitType.Star) });
            table.Columns.Add(new TableColumn { Width = new GridLength(0.58, GridUnitType.Star) });
            var group = new TableRowGroup();
            var header = new TableRow();
            header.Cells.Add(Cell(new Run("FIELD"), header: true));
            header.Cells.Add(Cell(new Run("TYPE"), header: true));
            header.Cells.Add(Cell(new Run("WHAT IT DOES"), header: true));
            group.Rows.Add(header);

            foreach (var field in section.Fields)
            {
                var row = new TableRow();
                var name = new Paragraph { Margin = new Thickness(0) };
                name.Inlines.Add(new Run(field.Name) { FontWeight = FontWeights.SemiBold });
                if (field.Offset is { } offset)
                {
                    name.Inlines.Add(new LineBreak());
                    var at = Mono("at " + offset);
                    at.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");
                    name.Inlines.Add(at);
                }
                row.Cells.Add(CellOf(name));

                var type = new Paragraph { Margin = new Thickness(0) };
                type.Inlines.Add(Mono(field.Type));
                if (field.Units is { } units)
                {
                    type.Inlines.Add(new LineBreak());
                    var u = new Run(units);
                    u.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");
                    type.Inlines.Add(u);
                }
                row.Cells.Add(CellOf(type));

                var what = new Paragraph { Margin = new Thickness(0) };
                what.Inlines.Add(new Run(field.Summary));
                what.Inlines.Add(new LineBreak());
                var engine = new Run(field.EngineUse);
                engine.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");
                what.Inlines.Add(engine);
                if (field.Limits is { } limits)
                {
                    what.Inlines.Add(new LineBreak());
                    what.Inlines.Add(new Run("Limits: " + limits) { FontStyle = FontStyles.Italic });
                }
                if (field.ReadByGame == false)
                {
                    what.Inlines.Add(new LineBreak());
                    var unread = new Run("Not read by the game.");
                    unread.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");
                    what.Inlines.Add(unread);
                }
                row.Cells.Add(CellOf(what));
                group.Rows.Add(row);
            }
            table.RowGroups.Add(group);
            Add(document, table);
        }
    }

    /// <summary>Help › Keyboard Shortcuts, generated from the same table that binds the keys.</summary>
    public static FlowDocument KeyboardShortcuts()
    {
        var document = NewDocument();
        Add(document, Heading("Keyboard shortcuts"));
        Add(document, Body("Undo and redo work wherever the focus is: every change, whichever panel made it, is one step in the document's history."));
        foreach (string category in Shortcuts.Categories)
        {
            Add(document, Heading2(category));
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 14) };
            table.Columns.Add(new TableColumn { Width = new GridLength(0.32, GridUnitType.Star) });
            table.Columns.Add(new TableColumn { Width = new GridLength(0.68, GridUnitType.Star) });
            var group = new TableRowGroup();
            foreach (var shortcut in Shortcuts.InCategory(category))
            {
                var row = new TableRow();
                row.Cells.Add(Cell(Mono(shortcut.Gesture)));
                row.Cells.Add(Cell(new Run(shortcut.Action)));
                group.Rows.Add(row);
            }
            table.RowGroups.Add(group);
            Add(document, table);
        }
        return document;
    }

    // ── Building blocks ──────────────────────────────────────────────────────

    private static FlowDocument NewDocument()
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = BodySize,
            PagePadding = new Thickness(28, 22, 28, 28),
            ColumnWidth = double.PositiveInfinity,
            ColumnGap = 0,
            TextAlignment = TextAlignment.Left,
            IsOptimalParagraphEnabled = false,
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "App.Text");
        document.SetResourceReference(FlowDocument.BackgroundProperty, "App.WindowBackground");
        return document;
    }

    private static void Add(FlowDocument document, Block block) => document.Blocks.Add(block);

    private static Paragraph Heading(string text) => new(new Run(text))
    {
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 10),
    };

    private static Paragraph Heading2(string text) => new(new Run(text))
    {
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 8),
    };

    private static Paragraph Body(string text) => new(new Run(text))
    {
        Margin = new Thickness(0, 0, 0, 10),
        LineHeight = 19,
    };

    private static Run Mono(string text) => new(text)
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
        FontSize = 12.5,
    };

    private static TableCell Cell(Inline content, bool header = false)
    {
        var paragraph = new Paragraph(content) { Margin = new Thickness(0) };
        if (header)
        {
            paragraph.FontSize = 11;
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");
        }
        return CellOf(paragraph);
    }

    private static TableCell CellOf(Paragraph paragraph)
    {
        var cell = new TableCell(paragraph)
        {
            Padding = new Thickness(0, 5, 14, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        cell.SetResourceReference(TableCell.BorderBrushProperty, "Help.RuleBrush");
        return cell;
    }
}
