using System.Text.Json.Nodes;
using System.Windows;
using Cairn.Formats.Imaging;
using Cairn.Formats.Vpp;
using Cairn.Ui.Diagnostics;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Sheets;
using Cairn.Vf.Ui.Dialogs;
using Cairn.Vf.Ui.Documents;
using Cairn.Workspace;

namespace Cairn.Vf.Ui.Diagnostics;

/// <summary>Self-tests of font editing: commands with undo, image replace, kerning, sheets, saving with errors.</summary>
internal static class VfEditSelfTests
{
    private static VfModule? Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<VfModule>().FirstOrDefault();

    private static readonly (VfPixelFormat Format, int Version)[] Formats =
        [(VfPixelFormat.Mono, 0), (VfPixelFormat.Mono, 1), (VfPixelFormat.Rgba4444, 1), (VfPixelFormat.Indexed, 1)];

    private static async Task<VfDocument> OpenAsync(SelfTestContext ctx, VfModule module, VfFont font, string name)
    {
        var doc = (VfDocument)module.Kind.OpenBytes(VfWriter.Write(font), name, name + " (self-test)");
        ctx.Shell.AddDocument(doc);
        await ctx.SettleAsync();
        return doc;
    }

    /// <summary>A white bar (columns 2 and 3) on black, as high as the font, for image tests.</summary>
    internal static BgraImage Bar(int height)
    {
        var image = new BgraImage(6, height);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < 6; x++) { byte v = x is 2 or 3 ? (byte)255 : (byte)0; image.Set(x, y, v, v, v, 255); }
        return image;
    }

    [SelfTest("vf.edit-commands")]
    public static async Task EditCommands(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        var font = VfSamples.Create(VfPixelFormat.Mono);
        var doc = await OpenAsync(ctx, module, font, "edit.vf");
        var view = (VfDocumentView)doc.View;
        int a = font.IndexOf('A');
        var g = font.Glyphs[a];
        doc.SelectCharacter('A');
        await ctx.SettleAsync();

        ctx.Check(doc.SetWidth(a, g.Width + 2) && doc.IsDirty && doc.UndoLabel == "Change width" && doc.Current.Glyphs[a].Width == g.Width + 2, "width +2 is one undo step and marks the font changed");
        await ctx.SettleAsync();
        ctx.Check(view.Inspector.FieldOf("Width")?.Value == g.Width + 2, $"the inspector's Width follows ({view.Inspector.FieldOf("Width")?.Value})");
        doc.Undo();
        ctx.Check(doc.Current.Glyphs[a].Width == g.Width && !doc.IsDirty, "undo restores the width and the saved state");
        doc.Redo();
        ctx.Check(doc.Current.Glyphs[a].Width == g.Width + 2 && VfRender.RawPixel(doc.Current, doc.Current.Glyphs[a], 0, 0) == VfRender.RawPixel(font, g, 0, 0), "redo widens again, keeping the pixels on the left");
        doc.Undo();

        // Inspector fields edit the font (a typed value is one step).
        await ctx.SettleAsync();
        if (view.Inspector.FieldOf("Spacing") is { } spacing) spacing.Value = g.Spacing + 3;
        ctx.Check(doc.Current.Glyphs[a].Spacing == g.Spacing + 3 && doc.UndoLabel == "Change spacing", "the Spacing field changes the spacing as one step");
        doc.Undo();
        ctx.Check(doc.SetUserData(a, 9) && doc.Current.Glyphs[a].UserData == 9, "user data");
        doc.Undo();
        ctx.Check(doc.SetDefaultSpacing(7) && doc.Current.DefaultSpacing == 7, "default spacing");
        doc.Undo();
        ctx.Check(doc.SetHeight(10, keepTop: true) && doc.Current.Height == 10 && doc.Current.Glyphs[a].Pixels.Length == 10 * g.Width, "height 8 → 10 adds rows");
        doc.Undo();
        ctx.Check(doc.ConvertFormat(VfPixelFormat.Indexed) && doc.Current.Format == VfPixelFormat.Indexed && doc.Current.Palette.Length == 256, "convert to indexed");
        ctx.Check(VfRender.Glyph(doc.Current, a).Bgra.AsSpan().SequenceEqual(VfRender.Glyph(font, a).Bgra), "the converted glyph looks the same");
        doc.Undo();
        ctx.Check(doc.SetRange(32, 100, 5) && doc.Current.GlyphCount == 100 && doc.Current.Glyphs[99].Width == 5, "add characters up to 131 with 5-pixel blank glyphs");
        await ctx.SettleAsync();
        ctx.Check(view.Grid.Items.Count == 100, $"the grid shows the 100 glyphs ({view.Grid.Items.Count})");
        doc.Undo();
        await ctx.SettleAsync();
        ctx.Check(doc.Current.GlyphCount == font.GlyphCount && view.Grid.Items.Count == font.GlyphCount, "undo removes them");

        // A paint stroke is one step.
        int before = doc.History.UndoLabels.Count;
        doc.BeginStroke();
        for (int y = 0; y < 4; y++) doc.PaintPixel(a, 1, y, y == 0 ? 0 : 14);
        doc.EndStroke();
        ctx.Check(doc.History.UndoLabels.Count == before + 1 && doc.UndoLabel == "Paint pixels", $"a stroke is one undo step ({doc.History.UndoLabels.Count - before})");
        ctx.Check(VfRender.RawPixel(doc.Current, doc.Current.Glyphs[a], 1, 2) == 14, "the stroke painted coverage 14");
        doc.Undo();
        ctx.Check(doc.Current.Glyphs[a].Pixels.AsSpan().SequenceEqual(g.Pixels.AsSpan()), "undo restores the pixels");
        ctx.Check(!doc.IsDirty, "back at the saved state");
        ctx.Shell.Close(doc);
    }

    [SelfTest("vf.image-replace")]
    public static async Task ImageReplace(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        foreach (var (format, version) in Formats)
        {
            var font = VfSamples.Create(format, version);
            var doc = await OpenAsync(ctx, module, font, $"replace-{format}.vf".ToLowerInvariant());
            int b = font.IndexOf('B');
            doc.SelectedGlyph = b;
            var window = new VfReplaceGlyphWindow(doc.Current, b, Bar(font.Height), "bar.png", doc.Backdrop);
            var r = window.Current();
            window.Close();
            ctx.Check(r.Width == 6 && r.Pixels.Length == font.PixelBytes(6), $"{format}: the 6 × 8 picture keeps its size ({r.Width})");
            ctx.Check(doc.ReplaceGlyph(b, r.Width, r.Pixels, r.Spacing) && doc.UndoLabel == "Replace glyph", $"{format}: replaced as one step");
            var lit = VfRender.Expand(VfRender.ToArgb4444(doc.Current, VfRender.RawPixel(doc.Current, doc.Current.Glyphs[b], 2, 3)));
            var dark = VfRender.Expand(VfRender.ToArgb4444(doc.Current, VfRender.RawPixel(doc.Current, doc.Current.Glyphs[b], 0, 3)));
            ctx.Check(lit.A > 200 && lit.R > 200, $"{format}: the bar is drawn solid white ({lit})");
            ctx.Check(dark.A == 0 || dark.R < 60, $"{format}: the background is clear or dark ({dark})");
            ctx.Check(doc.Current.Glyphs[b].Spacing == font.Glyphs[b].Spacing + 6 - font.Glyphs[b].Width, "the spacing moved with the width");
            doc.Undo();
            ctx.Check(!doc.IsDirty && doc.Current.Glyphs[b].Width == font.Glyphs[b].Width, $"{format}: undo restores the glyph");
            ctx.Shell.Close(doc);
        }
    }

    [SelfTest("vf.kerning")]
    public static async Task Kerning(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        var font = VfSamples.Create(VfPixelFormat.Mono);
        var doc = await OpenAsync(ctx, module, font, "kerning.vf");
        var view = (VfDocumentView)doc.View;
        doc.SelectCharacter('A');
        await ctx.SettleAsync();
        ctx.Check(view.Inspector.KerningRows.Any(r => r.Contains("'V' (86): -1", StringComparison.Ordinal)), $"A's pair with V is listed ({string.Join(" | ", view.Inspector.KerningRows)})");
        ctx.Check(view.Inspector.AddKerningFromFields("B", -2, selectedFirst: true), "Add pair A + B");
        await ctx.SettleAsync();
        var k = doc.Current.Kerning;
        ctx.Check(k.Zip(k.Skip(1)).All(p => (p.First.Left, p.First.Right).CompareTo((p.Second.Left, p.Second.Right)) < 0), "the table stays sorted by first, then second glyph");
        ctx.Check(VfLayout.Advance(doc.Current, (byte)'A', (byte)'B', out _, out _) == font.Glyphs[font.IndexOf('A')].Spacing - 2, "the game's scan finds A + B");
        ctx.Check(!doc.Problems.Any(p => p.Code is "VF041" or "VF042"), "no kerning order problems");
        ctx.Check(view.Inspector.KerningRows.Count == 3, $"the list shows A + V, V + A and A + B ({view.Inspector.KerningRows.Count})");
        ctx.Check(!view.Inspector.AddKerningFromFields("ÿ", -1, true) && view.Inspector.Message.Contains("not a character", StringComparison.Ordinal), $"a character the font lacks is refused ({view.Inspector.Message})");
        ctx.Check(view.Inspector.AddKerningFromFields("V", -3, selectedFirst: false), "Add pair V + A changes the existing one");
        ctx.Check(doc.Current.Kerning.Single(p => p.Left == font.IndexOf('V') && p.Right == font.IndexOf('A')).Offset == -3, "V + A is now -3");
        ctx.Check(doc.SetKernPair(font.IndexOf('A'), font.IndexOf('B'), 0) && !doc.Current.Kerning.Any(p => p.Right == font.IndexOf('B')), "offset 0 removes A + B");

        // Glyph 128 or later: never applied, and said so.
        doc.SetRange(32, 160, 4);
        doc.SelectedGlyph = 150;
        await ctx.SettleAsync();
        view.Inspector.AddKerningFromFields("A", -1, true);
        await ctx.SettleAsync();
        ctx.Check(doc.Problems.Any(p => p.Code == "VF044"), "VF044 for a pair with glyph 150");
        ctx.Check(view.Inspector.Message.Contains("128", StringComparison.Ordinal), $"the inspector warns ({view.Inspector.Message})");
        while (doc.CanUndo) doc.Undo();
        ctx.Check(!doc.IsDirty, "all undone");
        ctx.Shell.Close(doc);
    }

    [SelfTest("vf.sheet")]
    public static async Task Sheet(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        string temp = Path.Combine(Path.GetTempPath(), "cairn-vf-sheet-" + Environment.ProcessId);
        Directory.CreateDirectory(temp);
        try
        {
            var fonts = Formats.Select(f => ($"sample-{f.Format}-v{f.Version}.vf".ToLowerInvariant(), VfWriter.Write(VfSamples.Create(f.Format, f.Version)))).ToList();
            string? ui = LocalPaths.GameDirectory is { } game && Directory.Exists(game) ? Directory.EnumerateFiles(game, "*.vpp").FirstOrDefault(p => Path.GetFileName(p).Equals("ui.vpp", StringComparison.OrdinalIgnoreCase)) : null;
            if (ui is not null)
            {
                var archive = VppArchive.Open(ui);
                fonts.AddRange(archive.Entries.Where(e => e.Name.EndsWith(".vf", StringComparison.OrdinalIgnoreCase)).Select(e => (e.Name, archive.ReadEntry(e))));
            }
            else ctx.Log("(no ui.vpp: stock fonts skipped)");
            foreach (var (name, bytes) in fonts)
            {
                var doc = (VfDocument)module.Kind.OpenBytes(bytes, name, name + " (self-test)");
                ctx.Shell.AddDocument(doc);
                await ctx.SettleAsync();
                string png = Path.Combine(temp, Path.ChangeExtension(name, ".png"));
                ctx.Check(module.ExportSheet(doc, png, new VfSheetOptions()) && File.Exists(png) && File.Exists(VfModule.SidecarOf(png)), $"{name}: sheet and sidecar written");
                ctx.Check(!module.ImportSheet(doc, png) && !doc.IsDirty && doc.Serialize().AsSpan().SequenceEqual(bytes), $"{name}: importing the unchanged sheet changes nothing (byte for byte)");
                ctx.Check(!module.ImportSheet(doc, VfModule.SidecarOf(png)) && !doc.IsDirty, $"{name}: the sidecar can be picked instead of the image");

                // Change a width in the sidecar: the import takes it.
                var node = JsonNode.Parse(File.ReadAllText(VfModule.SidecarOf(png)))!;
                var first = node["glyphs"]![doc.Current.GlyphCount / 2]!;
                int w = (int)first["width"]!, cell = (int)node["layout"]!["cellWidth"]!;
                first["width"] = Math.Min(cell, w + 1);
                File.WriteAllText(VfModule.SidecarOf(png), node.ToJsonString());
                ctx.Check(w + 1 > cell || (module.ImportSheet(doc, png) && doc.Current.Glyphs[doc.Current.GlyphCount / 2].Width == w + 1 && doc.UndoLabel == "Import image sheet"), $"{name}: a wider glyph in the sidecar is imported");
                while (doc.CanUndo) doc.Undo();
                ctx.Check(doc.Serialize().AsSpan().SequenceEqual(bytes), $"{name}: undo gives back the original bytes");
                ctx.Shell.Close(doc);
            }
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [SelfTest("vf.save-errors")]
    public static async Task SaveWithErrors(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        var font = VfSamples.Create(VfPixelFormat.Mono);
        var doc = await OpenAsync(ctx, module, font, "toobig.vf");
        ctx.Check(doc.ConfirmSave(), "a font without errors saves without a prompt");
        // 224 glyphs 60 wide and 40 high do not fit 256 × 256: "Font too big!"
        doc.SetRange(32, 224, 60);
        doc.SetHeight(40, true);
        for (int i = 0; i < doc.Current.GlyphCount; i++) if (doc.Current.Glyphs[i].Width < 60) doc.SetWidth(i, 60);
        await ctx.SettleAsync();
        ctx.Check(doc.Problems.Any(p => p.Code == "VF060"), "the texture check re-runs after the edits (VF060)");
        ctx.Check(doc.ErrorCount > 0 && doc.StatusItems.Any(s => s.Text.Contains("error", StringComparison.Ordinal)), "the status bar counts the error");
        ctx.Check(!doc.ConfirmSave(), "saving with errors asks first; a run without windows answers Cancel");
        while (doc.CanUndo) doc.Undo();
        ctx.Shell.Close(doc);
    }

    /// <summary><c>--dialog vf-replace-glyph</c>: the replace window over the active font's selected glyph, with a drawn picture.</summary>
    [ScreenshotDialog("vf-replace-glyph")]
    public static Window? ReplaceDialog(ScreenshotContext ctx)
    {
        if (ctx.Shell.ActiveDocument is not VfDocument doc || doc.SelectedGlyph < 0) return null;
        int h = Math.Max(8, doc.Current.Height);
        var image = new BgraImage(h * 2, h * 3);
        double cx = image.Width / 2.0, cy = image.Height / 2.0, r = Math.Min(cx, cy) - 2;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                double d = Math.Abs(Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy)) - r * 0.7);
                byte a = (byte)Math.Clamp((int)((r * 0.25 - d) * 255 / 2), 0, 255);
                image.Set(x, y, 255, 255, 255, a);
            }
        var window = new VfReplaceGlyphWindow(doc.Current, doc.SelectedGlyph, image, "ring.png", doc.Backdrop) { Owner = ctx.MainWindow };
        window.Show();
        return window;
    }
}
