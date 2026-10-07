using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Cairn.Assets;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Ui;

/// <summary>
/// The Help windows' content, built as <see cref="FlowDocument"/>s.
///
/// The format reference is generated from <see cref="AtxSchema"/> rather than written out, which is
/// the point of having one schema: adding a key to the format adds it to this page, with its type,
/// default, explanation and runtime event, without anyone remembering to update the documentation.
/// The shortcut list is generated from the shortcut table for the same reason.
///
/// Colours are resource references, never literals, so both pages follow a theme change in place.
/// </summary>
public static class HelpDocuments
{
    private const double BodySize = 13;

    /// <summary>The ATX format reference, generated from the schema.</summary>
    public static FlowDocument FormatReference()
    {
        var document = NewDocument();

        Add(document, Heading("The ATX format"));
        Add(document, Body(
            "An .atx file is a small TOML document that turns a run of still images into one "
            + "animated texture. Put it next to the images it names and use its own name — without "
            + "the extension — wherever a texture name is expected in the level editor. That name "
            + "is the texture's handle: an .atx called mtl_hazard.atx is referred to as "
            + "mtl_hazard, and level events address it by that handle too."));

        Add(document, Code(string.Join(Environment.NewLine,
            "[header]",
            "frame_time = 80          # milliseconds per frame",
            "animation_mode = 2       # 0 static, 1 ping-pong, 2 loop, 3 play once",
            "",
            "[[frame]]",
            "file = \"hazard_00.tga\"",
            "",
            "[[frame]]",
            "file = \"hazard_01.tga\"",
            "frame_time = 240         # this frame only")));

        Add(document, Heading2("[header] keys"));
        Add(document, KeyTable(AtxSchema.HeaderKeys));

        Add(document, Heading2("[[frame]] keys"));
        Add(document, Body(
            "A file needs at least one [[frame]]. Every frame must be the same width, height, "
            + "pixel format and mip count as frame 0, or the game refuses to load the texture."));
        Add(document, KeyTable(AtxSchema.FrameKeys));

        Add(document, Heading2("format tokens"));
        Add(document, Body(
            "Converting costs quality but saves video memory. The conversion only works on "
            + "uncompressed images: a DXT-compressed .dds frame cannot be converted, and using "
            + "format (or an alpha mask) with one makes the texture fail to load."));
        Add(document, ThreeColumnTable(
            ["TOKEN", "ALSO ACCEPTED", "WHAT IT MEANS"],
            AtxSchema.FormatTokens.Select(f => (f.Token, f.Alias, f.Description))));

        Add(document, Heading2("material tokens"));
        Add(document, Body(
            "The material decides the footstep sound, the bullet impact effect and the decal a "
            + "surface uses. Without it the engine guesses from the texture's name."));
        Add(document, ThreeColumnTable(
            ["TOKEN", "INDEX", "TYPICAL USE"],
            AtxSchema.Materials.Select(m =>
                (m.Token, m.Index.ToString(CultureInfo.InvariantCulture), m.Description))));

        Add(document, Heading2("animation modes"));
        Add(document, ThreeColumnTable(
            ["VALUE", "NAME", "BEHAVIOUR"],
            AtxSchema.AnimationModes.Select(m =>
                (((int)m.Mode).ToString(CultureInfo.InvariantCulture), m.Label, m.Description))));

        Add(document, Heading2("How the game finds an image"));
        Add(document, Body(
            "RF's file system is flat: a frame stores a bare file name and never a path. For each "
            + "name the engine first looks for a superseding sibling — the same name with a "
            + "different extension — and only then for the name as written. The chain is:"));
        Add(document, Code(string.Join("  →  ", AtxSchema.SupersedeProbeExtensions) + "  →  the name as written"));
        Add(document, Body(
            "So a frame that says smoke.tga will quietly use smoke.dds if one exists next to it. "
            + "That is how a mod ships a higher-quality replacement without editing anything. The "
            + "full texture chain also includes .atx itself, which is why a frame may never name "
            + "another .atx file — the texture would try to contain itself."));
        Add(document, Body(
            ".atx comes first in that chain, ahead of .vbm — the stock game's own animated texture "
            + "format. An .atx named after a .vbm therefore replaces that animation everywhere the "
            + "game uses it, with no level edits at all. File › Import VBM… (Ctrl+Shift+I) does "
            + "exactly that: it writes each frame of a .vbm out as a TGA and generates the matching "
            + ".atx, which you can then edit like any other."));
        Add(document, Body(
            "Cairn searches, in order: the .atx file's own folder, the folders listed in "
            + "Settings (loose files first, then any .vpp inside them), and finally the Red Faction "
            + "install — its root .vpp archives and the user_maps "
            + JoinWithAnd(AssetResolver.GameSubFolders)
            + " folders. Tools › Settings shows the resolved order for the file you have open."));

        Add(document, Heading2("Level events"));
        Add(document, Body(
            "Alpine Faction adds four events that drive an animated texture at runtime. Each one "
            + "takes the texture's handle — the .atx file name without its extension."));
        Add(document, ThreeColumnTable(
            ["EVENT", "TAKES", "WHAT IT DOES"],
            [
                ("ATX_Play", "handle", "Starts the animation, whatever initially_on said."),
                ("ATX_Pause", "handle", "Stops the animation on the frame it is showing."),
                ("ATX_Set_Frame", "handle, frame",
                    "Shows one frame, counting from 0. The only way a Static texture ever changes."),
                ("ATX_Set_Frame_Time", "handle, milliseconds",
                    "Changes the texture-wide frame time. Per-frame overrides still win."),
            ]));

        Add(document, Heading2("Limits worth knowing"));
        Add(document, Bullets(
            $"A bitmap name — a frame, a mask, or the .atx itself — can be at most "
            + $"{AtxSchema.MaxBitmapNameLength} characters. The engine's name buffer is 32 bytes.",
            $"frame_time is clamped to at least {AtxSchema.MinFrameTimeMs} ms; the default is "
            + $"{AtxSchema.DefaultFrameTimeMs} ms.",
            "An animation_mode outside 0–3 falls back to Static.",
            "An alpha mask must be an 8-bit greyscale or paletted image the same size as frame 0. "
            + "With a no-alpha format the game promotes it: 565 becomes 4444, 888 becomes 8888. "
            + "With 1555 the mask is reduced to on or off.",
            "Image types Cairn can decode and preview: "
            + string.Join(", ", AtxSchema.ReadableExtensions) + ". The engine also loads "
            + string.Join(", ", AtxSchema.TextureExtensions.Except(AtxSchema.ReadableExtensions)
                .Where(e => e != ".atx")) + ", which this app can list but not show."));

        return document;
    }

    // ── Building blocks ───────────────────────────────────────────────────────

    /// <summary>Joins names as prose — "a, b and c" — so a generated sentence reads naturally.</summary>
    private static string JoinWithAnd(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static FlowDocument NewDocument()
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = BodySize,
            PagePadding = new Thickness(28, 22, 28, 28),
            // One column, however wide the window gets: a reference page that reflows into
            // newspaper columns is unreadable.
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
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static Paragraph Heading2(string text)
    {
        var paragraph = new Paragraph(new Run(text))
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 8),
        };
        return paragraph;
    }

    private static Paragraph Body(string text) => new(new Run(text))
    {
        Margin = new Thickness(0, 0, 0, 10),
        LineHeight = 19,
    };

    private static Paragraph Code(string text)
    {
        var paragraph = new Paragraph(Mono(text))
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 12),
        };
        paragraph.SetResourceReference(Paragraph.BackgroundProperty, "Help.CodeBackground");
        return paragraph;
    }

    private static Run Mono(string text) => new(text)
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
        FontSize = 12.5,
    };

    private static Paragraph Bullets(params string[] items)
    {
        // A List block would be tidier, but a paragraph of dashes keeps the page copyable as text.
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 19 };
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run("•  " + items[i]));
        }
        return paragraph;
    }

    private static TableCell Cell(Inline content, bool header = false)
    {
        var paragraph = new Paragraph(content) { Margin = new Thickness(0) };
        if (header)
        {
            paragraph.FontSize = 11;
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");
        }
        var cell = new TableCell(paragraph)
        {
            Padding = new Thickness(0, 5, 14, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        cell.SetResourceReference(TableCell.BorderBrushProperty, "Help.RuleBrush");
        return cell;
    }

    private static Table ThreeColumnTable(
        string[] headers, IEnumerable<(string A, string B, string C)> rows)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12) };
        table.Columns.Add(new TableColumn { Width = new GridLength(0.24, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.18, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.58, GridUnitType.Star) });

        var group = new TableRowGroup();
        var headerRow = new TableRow();
        foreach (string header in headers) headerRow.Cells.Add(Cell(new Run(header), header: true));
        group.Rows.Add(headerRow);

        foreach (var (a, b, c) in rows)
        {
            var row = new TableRow();
            row.Cells.Add(Cell(Mono(a)));
            row.Cells.Add(Cell(Mono(b)));
            row.Cells.Add(Cell(new Run(c)));
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static Table KeyTable(IReadOnlyList<AtxKeyInfo> keys)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12) };
        table.Columns.Add(new TableColumn { Width = new GridLength(0.24, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.16, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.60, GridUnitType.Star) });

        var group = new TableRowGroup();
        var header = new TableRow();
        header.Cells.Add(Cell(new Run("KEY"), header: true));
        header.Cells.Add(Cell(new Run("TYPE"), header: true));
        header.Cells.Add(Cell(new Run("WHAT IT DOES"), header: true));
        group.Rows.Add(header);

        foreach (var key in keys)
        {
            var row = new TableRow();
            row.Cells.Add(Cell(Mono(key.Name)));
            row.Cells.Add(Cell(new Run(TypeName(key.Kind))));

            var description = new Paragraph { Margin = new Thickness(0) };
            description.Inlines.Add(new Run(key.Summary));
            description.Inlines.Add(new LineBreak());
            var details = new Run(key.Details);
            description.Inlines.Add(details);
            if (key.DefaultText is { } fallback)
            {
                description.Inlines.Add(new LineBreak());
                var defaultRun = new Run("Default: " + fallback);
                description.Inlines.Add(defaultRun);
                defaultRun.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");
            }
            if (key.RuntimeEvent is { } runtimeEvent)
            {
                description.Inlines.Add(new LineBreak());
                var eventRun = Mono("Level event: " + runtimeEvent);
                description.Inlines.Add(eventRun);
                eventRun.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");
            }
            details.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");

            var cell = new TableCell(description)
            {
                Padding = new Thickness(0, 5, 0, 7),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "Help.RuleBrush");
            row.Cells.Add(cell);
            group.Rows.Add(row);
        }

        table.RowGroups.Add(group);
        return table;
    }

    private static string TypeName(AtxValueKind kind) => kind switch
    {
        AtxValueKind.Integer => "number",
        AtxValueKind.Boolean => "true / false",
        _ => "text",
    };
}
