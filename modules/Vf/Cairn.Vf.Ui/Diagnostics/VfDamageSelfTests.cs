using System.Text.Json.Nodes;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Sheets;
using Cairn.Vf.Ui.Documents;

namespace Cairn.Vf.Ui.Diagnostics;

/// <summary>
/// Self-tests of damaged fonts and hand-edited sheets in the running app: they open, show what is wrong and never
/// crash an edit; sheet export never replaces files without asking; kerning the game applies without a pair is shown.
/// </summary>
internal static class VfDamageSelfTests
{
    private static VfModule? Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<VfModule>().FirstOrDefault();

    private static List<string>? Collected(SelfTestContext ctx) => (ctx.Shell.Dialogs as DialogService)?.CollectErrors;

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-vf-damage-" + Environment.ProcessId, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A version 1 monochrome font: the header values given and one glyph table entry per (spacing, width).</summary>
    private static byte[] FontFile(int height, IReadOnlyList<(int Spacing, int Width)> glyphs, int pixelBytes)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(VfFont.Signature); w.Write(1); w.Write((uint)VfPixelFormat.Mono);
        w.Write(glyphs.Count); w.Write(32); w.Write(5); w.Write(height); w.Write(0); w.Write(pixelBytes);
        uint offset = 0;
        foreach (var (spacing, width) in glyphs)
        {
            w.Write(spacing); w.Write(width); w.Write(offset); w.Write((short)-1); w.Write((ushort)0);
            offset += (uint)Math.Max(0, width * height);
        }
        w.Write(new byte[pixelBytes]);
        return ms.ToArray();
    }

    [SelfTest("vf.damaged-fonts")]
    public static async Task DamagedFonts(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        string folder = NewFolder();

        // A 36-byte font claiming a height of 1,000,000,000: reported as damaged, nothing opened.
        string tall = Path.Combine(folder, "tall.vf");
        File.WriteAllBytes(tall, FontFile(1_000_000_000, [], 0));
        int errors = Collected(ctx)?.Count ?? 0, open = ctx.Shell.Documents.Count;
        ctx.Check(!ctx.Shell.OpenFile(tall) && ctx.Shell.Documents.Count == open, "a font with an absurd height is not opened");
        ctx.Check(Collected(ctx) is not { } list || list.Skip(errors).Any(e => e.Contains("height", StringComparison.Ordinal)), "the reason (its height) is reported");

        // A readable font whose spacing makes the sample far too wide to draw: the tab opens with a note instead.
        var glyphs = Enumerable.Range(0, 95).Select(i => (Spacing: i == 'e' - 32 ? 400_000_000 : 6, Width: 4)).ToList();
        string far = Path.Combine(folder, "far.vf");
        File.WriteAllBytes(far, FontFile(8, glyphs, 95 * 4 * 8));
        if (ctx.Check(ctx.Shell.OpenFile(far), "a font with a huge spacing opens") && ctx.Shell.ActiveDocument is VfDocument doc)
        {
            await ctx.SettleAsync(); // the default sample has three e's: 1.2 billion pixels wide
            var view = doc.View as VfDocumentView;
            ctx.Check(view is not null && ReferenceEquals(view, doc.View), "the view is built once and kept");
            ctx.Check(view?.SampleImage is null && view?.SampleInfo.StartsWith("Not drawn", StringComparison.Ordinal) == true, $"the sample says why it is not drawn ({view?.SampleInfo})");
            ctx.Check(doc.Problems.Any(p => p.Code == "VF055"), "the Problems tab names the spacing (VF055)");
            ctx.Shell.Close(doc);
        }

        // A glyph with a negative width: read as an empty glyph and reported; changing the height works.
        string neg = Path.Combine(folder, "neg.vf");
        File.WriteAllBytes(neg, FontFile(8, [(4, 3), (4, -3)], 24));
        if (ctx.Check(ctx.Shell.OpenFile(neg), "a font with a negative glyph width opens") && ctx.Shell.ActiveDocument is VfDocument negDoc)
        {
            await ctx.SettleAsync();
            ctx.Check(negDoc.Problems.Any(p => p.Code == "VF050" && p.Glyph == 1) && negDoc.Current.Glyphs[1].Width == 0, "the negative width is reported (VF050) and read as 0");
            ctx.Check(negDoc.SetHeight(9, keepTop: true) && negDoc.Current.Height == 9, "Change Height works on it");
            ctx.Check(negDoc.SetHeight(7, keepTop: false) && negDoc.Current.Height == 7, "Change Height (rows at the top) works on it");
            // An edit failing on a damaged font is reported, not thrown (whatever the operation).
            var before = negDoc.Current;
            int shown = Collected(ctx)?.Count ?? 0;
            bool done = true;
            try { done = negDoc.EditValue("Change height", _ => throw new OverflowException("Arithmetic operation resulted in an overflow.")); }
            catch (Exception ex) { ctx.Check(false, "an edit's failure escaped: " + ex.GetType().Name); }
            ctx.Check(!done && ReferenceEquals(before, negDoc.Current), "a failing edit leaves the font unchanged");
            ctx.Check(Collected(ctx) is not { } shownList || shownList.Count > shown, "and is reported in an error dialog");
            while (negDoc.CanUndo) negDoc.Undo();
            ctx.Shell.Close(negDoc);
        }
        await ctx.SettleAsync();
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [SelfTest("vf.sheet-damaged")]
    public static async Task SheetDamaged(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        string folder = NewFolder();
        var font = VfSamples.Create(VfPixelFormat.Indexed);
        var doc = (VfDocument)module.Kind.OpenBytes(VfWriter.Write(font), "sheet.vf", "sheet.vf (self-test)");
        ctx.Shell.AddDocument(doc);
        await ctx.SettleAsync();
        try
        {
            string png = Path.Combine(folder, "sheet.png");
            ctx.Check(module.ExportSheet(doc, png, new VfSheetOptions()), "the sheet is exported");
            string good = File.ReadAllText(VfModule.SidecarOf(png));
            var cases = new (string Name, Action<JsonNode> Damage)[]
            {
                ("a null glyph", n => n["glyphs"]![0] = null),
                ("a null kerning pair", n => n["kerning"] = new JsonArray((JsonNode?)null)),
                ("a null palette entry", n => n["palette"]![3] = null),
                ("4,194,304 columns", n => { n["layout"]!["columns"] = 4_194_304; n["layout"]!["cellWidth"] = 1000; }),
                ("a text width", n => n["glyphs"]![1]!["width"] = "wide"),
                ("a huge user data", n => n["glyphs"]![1]!["userData"] = 1_000_000),
            };
            foreach (var (name, damage) in cases)
            {
                var node = JsonNode.Parse(good)!;
                damage(node);
                File.WriteAllText(VfModule.SidecarOf(png), node.ToJsonString());
                int errors = Collected(ctx)?.Count ?? 0;
                bool imported = true;
                try { imported = module.ImportSheet(doc, png); }
                catch (Exception ex) { ctx.Check(false, $"{name}: {ex.GetType().Name} escaped the import"); continue; }
                ctx.Check(!imported && !doc.IsDirty, $"{name}: nothing imported");
                ctx.Check(Collected(ctx) is not { } list || list.Skip(errors).Any(e => e.StartsWith("The sheet could not be imported", StringComparison.Ordinal)), $"{name}: reported ({Collected(ctx)?.LastOrDefault()})");
            }
        }
        finally
        {
            ctx.Shell.Close(doc);
            await ctx.SettleAsync();
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [SelfTest("vf.sheet-overwrite")]
    public static async Task SheetOverwrite(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        string folder = NewFolder();
        var doc = (VfDocument)module.Kind.OpenBytes(VfWriter.Write(VfSamples.Create(VfPixelFormat.Mono)), "overwrite.vf", "overwrite.vf (self-test)");
        ctx.Shell.AddDocument(doc);
        await ctx.SettleAsync();
        try
        {
            string png = Path.Combine(folder, "sheet.png"), json = VfModule.SidecarOf(png);
            const string mine = "{ \"note\": \"hand-edited metrics - must survive\" }";
            File.WriteAllText(json, mine);
            int errors = Collected(ctx)?.Count ?? 0;
            ctx.Check(!module.ExportSheet(doc, png, new VfSheetOptions()), "exporting next to an existing JSON file asks first (a run without windows declines)");
            ctx.Check(File.ReadAllText(json) == mine && !File.Exists(png), "the existing JSON file is left as it was and nothing is written");
            ctx.Check(Collected(ctx) is not { } list || list.Skip(errors).Any(e => e.Contains("Replace sheet.json?", StringComparison.Ordinal)), "the question is logged");
            File.WriteAllText(png, "not really a png");
            ctx.Check(!module.ExportSheet(doc, png, new VfSheetOptions(), imageConfirmed: true) && File.ReadAllText(json) == mine, "the JSON file is asked about even when the image was confirmed");
            File.Delete(json);
            File.Delete(png);
            ctx.Check(module.ExportSheet(doc, png, new VfSheetOptions()) && File.Exists(png) && File.Exists(json), "with no files there it writes both without asking");
        }
        finally
        {
            ctx.Shell.Close(doc);
            await ctx.SettleAsync();
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [SelfTest("vf.kerning-phantom")]
    public static async Task KerningPhantom(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        // pairs A + T and B + V: the game's scan for "AV" runs past A's pairs and applies B + V
        var font = VfSamples.Create(VfPixelFormat.Mono) with { Kerning = [] };
        font = VfEdits.WithKernPair(font, font.IndexOf('A'), font.IndexOf('T'), -2);
        font = VfEdits.WithKernPair(font, font.IndexOf('B'), font.IndexOf('V'), -3);
        var doc = (VfDocument)module.Kind.OpenBytes(VfWriter.Write(font), "phantom.vf", "phantom.vf (self-test)");
        ctx.Shell.AddDocument(doc);
        try
        {
            doc.SelectCharacter('A');
            await ctx.SettleAsync();
            var view = (VfDocumentView)doc.View;
            ctx.Check(doc.Problems.Any(p => p.Code == "VF046" && p.Glyph == font.IndexOf('A') && p.Message.Contains("'B' (66) + 'V' (86)", StringComparison.Ordinal)), "the Problems tab warns that A + V gets B + V's offset (VF046)");
            ctx.Check(view.Inspector.PhantomRows.Any(r => r.StartsWith("'A' (65) + 'V' (86): -3", StringComparison.Ordinal)), $"the kerning list of A shows it ({string.Join(" | ", view.Inspector.PhantomRows)})");
            doc.SelectCharacter('V');
            await ctx.SettleAsync();
            ctx.Check(view.Inspector.PhantomRows.Count == 1, "and so does the kerning list of V");
            ctx.Check(view.Inspector.AddKerningFromFields("A", -1, selectedFirst: false) && !doc.Problems.Any(p => p.Code == "VF046"), "giving A + V a pair of its own clears it");
            ctx.Check(view.Inspector.PhantomRows.Count == 0, "and the list no longer shows it");

            // A paint stroke checks the font once, when it ends (not for every pixel).
            int a = font.IndexOf('A');
            var problems = doc.Problems;
            doc.BeginStroke();
            for (int y = 0; y < 4; y++) doc.PaintPixel(a, 0, y, y % 2 == 0 ? 0 : 14);
            ctx.Check(ReferenceEquals(problems, doc.Problems), "the checks wait for the end of a paint stroke");
            doc.EndStroke();
            ctx.Check(!ReferenceEquals(problems, doc.Problems), "and run once it ends");
            while (doc.CanUndo) doc.Undo();
        }
        finally
        {
            ctx.Shell.Close(doc);
            await ctx.SettleAsync();
        }
    }
}
