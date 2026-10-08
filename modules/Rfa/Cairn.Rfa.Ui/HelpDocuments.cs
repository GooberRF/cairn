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

    /// <summary>Help › Exporter and PS2 meshes: the four mesh formats Cairn reads and converts but never writes.</summary>
    public static FlowDocument LegacyMeshes()
    {
        var document = NewDocument();
        Add(document, Heading("Exporter and PS2 meshes (.v3d, .vcm, .rfm, .rfc)"));
        Add(document, Body(
            "Cairn reads four mesh formats the PC game does not load, shows them in 3D and converts them to .v3m or .v3c. "
            + "It never writes them. .v3d is the 3ds Max exporter's static mesh (the source a .v3m was compiled from) and "
            + ".vcm its character mesh, with skeleton, collision spheres, prop points and weights; .rfm and .rfc are the "
            + "PlayStation 2 version's static and character meshes. A file is recognised by its contents, so an exporter "
            + "mesh named .v3m still opens as one."));
        Add(document, Heading2("The tab"));
        Add(document, Body(
            "The tab shows the mesh converting the file makes, read-only, with a banner and a Convert… button. A character "
            + "mesh shows its skeleton and plays preview clips like a .v3c. Problems lists, as information (LEG100), what "
            + "converting approximates; a file that cannot be read (a Red Faction II mesh, a damaged file) opens with the "
            + "reason (LEG001), and Save and Save As are off."));
        Add(document, Heading2("Converting"));
        Add(document, Body(
            "Convert… on the banner, or File › Export › Convert to .v3m/.v3c…, writes the converted mesh next to the source, "
            + "into a folder, or into the packfile the mesh came from as a new entry (one undo step in the packfile's tab). "
            + "Next to the source is off when that is the game directory. A taken name gets a free one unless Replace is "
            + "ticked. Save As on the tab writes the converted mesh too, never over the file the tab was read from. In a "
            + "packfile, select meshes and use Convert meshes… (right-click, or the Packfile menu) to convert them all as "
            + "one undo step, with a report."));
        Add(document, Heading2("What the conversion does"));
        Add(document, Body(
            "Exporter meshes convert the way the game's mesh compiler did: one smooth normal per position, each triangle's "
            + "UVs moved by whole tiles into 0..1, corners whose UVs differ by less than 0.03 joined, materials naming the "
            + "same texture drawn as one batch, LOD submeshes folded into the submesh that names them, bone names in lower "
            + "case, a character's weights kept byte for byte. PS2 meshes lost their submesh names and welded vertices, and "
            + "their weights are 1/16 steps; when the same-named .v3d or .vcm is beside a .rfm or .rfc (the same packfile or "
            + "folder), Cairn converts that instead and says so. Material flag 0x8 (a texture with alpha) is not set; the "
            + "engine does not read material flags."));
        return document;
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
