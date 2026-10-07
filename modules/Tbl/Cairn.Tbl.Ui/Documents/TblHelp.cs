using System.Windows;
using System.Windows.Documents;

namespace Cairn.Tbl.Ui.Documents;

/// <summary>The "Table syntax" help topic: how the game reads .tbl files, in plain words.</summary>
public static class TblHelp
{
    /// <summary>Builds the topic.</summary>
    public static FlowDocument Build()
    {
        var doc = new FlowDocument { PagePadding = new Thickness(16), FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13 };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "App.Text");
        doc.SetResourceReference(FlowDocument.BackgroundProperty, "App.PaneBackground");

        void Heading(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        void Para(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 6) });
        void Bullets(params string[] items)
        {
            var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 6) };
            foreach (string item in items) list.ListItems.Add(new ListItem(new Paragraph(new Run(item)) { Margin = new Thickness(0, 0, 0, 2) }));
            doc.Blocks.Add(list);
        }

        doc.Blocks.Add(new Paragraph(new Run("Red Faction tables (.tbl)")) { FontSize = 20, FontWeight = FontWeights.SemiBold });
        Para("Tables are plain text files the game reads at start-up: weapons, entities, items, effects, sounds and more. " +
             "The game has no forgiving parser: it reads each table with a fixed sequence of \"expect this marker next\" calls, " +
             "and the first thing it does not expect stops the game with a \"Parsing file ...\" error box. Cairn's editor checks " +
             "the same rules as you type.");

        Heading("Sections, entries and fields");
        Bullets(
            "A section starts with a header such as #Primary Weapons and ends with #End. Most tables need the #End.",
            "An entry starts with its name field (for example $Name: \"12mm\") and runs to the next entry.",
            "Fields are written $Field Name: value. Sub-fields that belong to the field before them start with +.",
            "Field names are matched without regard to case, but every space and the colon must be there.",
            "Text after the last #End is never read.");

        Heading("Order matters");
        Para("The game looks for each optional field once, at its fixed place. A field written out of order (or an unknown " +
             "field) is not skipped: the next required read then fails and the game stops. Cairn's completion offers the " +
             "fields valid at the caret in the order the game reads them. entity.tbl and clutter.tbl are the exceptions: " +
             "there the game searches for the next entry, so stray text between entries is ignored.");

        Heading("Values");
        Bullets(
            "Strings must be in double quotes, on one line, with no escapes. A string cannot run over a line break.",
            "Numbers: whole numbers may be negative or hexadecimal (0x10). Decimals are written 1.5, .5 or 5.; no exponent (1e3) and no leading +.",
            "Booleans: true, false, yes or no. 0 and 1 are not accepted.",
            "Vectors <x, y, z>, colours {r, g, b} or {r, g, b, a}, lists (\"a\" \"b\") separated by spaces only (no commas).");

        Heading("Comments");
        Bullets(
            "// starts a comment that ends at the end of the line: the game ends it at a carriage return (CR) only. " +
            "In a file with LF-only line endings a // comment swallows everything up to the next CR, so keep tables in CRLF.",
            "/* ... */ comments may sit between any two values and span lines; they do not nest.",
            "Inside a quoted string, // and /* are ordinary text.");

        Heading("Encoding and byte-order mark");
        Para("The game reads tables as single bytes (Windows-1252). A UTF-8 byte-order mark (BOM) at the start of the file sits " +
             "in front of the first marker and breaks it. Cairn keeps the encoding and line endings a file had when you save; " +
             "the byte-order-mark problem has a quick fix that saves the table without it.");

        Heading("Files and names");
        Para("File names in a table (textures, meshes, sounds, effects) are found like every other asset: the game's packfiles " +
             "and folders, your search folders, and the table's own folder. Names that point at entries of other tables " +
             "(an ammo type, a vclip, a material) are looked up in those tables; an unknown name is silently ignored by the " +
             "game, so Cairn reports it as a warning. Ctrl+click a file or name to follow it.");

        Heading("Alpine Faction line tables");
        Para("Alpine Faction's own tables (af_*.tbl and <level>_info.tbl) are read line by line instead: lines before #Start " +
             "are ignored, reading stops at #End, lines starting with // are skipped, each other line is $Name: value with " +
             "the name matched case-sensitively, in any order (later lines win), and quotes around strings are optional. " +
             "Cairn treats Alpine Faction as the game: its tables and fields are checked like any other. Hover a field, or " +
             "read its completion entry, to see the first Alpine Faction version that reads it (for example \"Alpine Faction 1.1+\").");

        Heading("Editor keys");
        Bullets(
            "Ctrl+Space: complete fields, values, names and file names. Typing $ or + at a line start, or a space after a field's colon, opens it too (Settings).",
            "Ctrl+.: quick fixes for the problem at the caret. F8 / Shift+F8: next / previous problem.",
            "Ctrl+/: toggle // comments. Ctrl+G: go to line. Ctrl+F / Ctrl+H: find / replace; F3 / Shift+F3: next / previous match.");
        return doc;
    }
}
