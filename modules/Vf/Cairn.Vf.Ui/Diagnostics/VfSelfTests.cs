using Cairn.Formats.Vpp;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Ui.Documents;
using Cairn.Vf.Ui.Views;
using Cairn.Workspace;

namespace Cairn.Vf.Ui.Diagnostics;

/// <summary>Self-tests of the fonts module (run with <c>Cairn.exe --selftest</c>).</summary>
internal static class VfSelfTests
{
    private static VfModule? Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<VfModule>().FirstOrDefault();

    private static readonly (VfPixelFormat Format, int Version)[] Formats =
        [(VfPixelFormat.Mono, 0), (VfPixelFormat.Mono, 1), (VfPixelFormat.Rgba4444, 1), (VfPixelFormat.Indexed, 1)];

    [SelfTest("vf.formats")]
    public static async Task FormatsOpen(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        string temp = Path.Combine(Path.GetTempPath(), "cairn-vf-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var (format, version) in Formats)
            {
                string name = $"sample-{format}-v{version}.vf".ToLowerInvariant();
                var font = VfSamples.Create(format, version);
                byte[] bytes = VfWriter.Write(font);
                var doc = (VfDocument)module.Kind.OpenBytes(bytes, name, name + " (self-test)");
                ctx.Shell.AddDocument(doc);
                await ctx.SettleAsync();
                var view = (VfDocumentView)doc.View;
                ctx.Check(view.Grid.Items.Count == font.GlyphCount, $"{name}: grid shows {view.Grid.Items.Count} of {font.GlyphCount} glyphs");
                ctx.Check(!doc.Problems.Any(p => p.Severity != VfSeverity.Information), $"{name}: no errors or warnings ({string.Join("; ", doc.Problems)})");

                // The sample strip is the layout function's output, at the sample zoom.
                var (w, h) = VfLayout.Measure(font, VfLayout.Encode(doc.SampleText));
                var layout = VfLayout.Layout(font, VfLayout.Encode(doc.SampleText));
                ctx.Check(view.SampleInfo.StartsWith($"{w} × {h} px", StringComparison.Ordinal), $"{name}: sample info '{view.SampleInfo}' gives the measured {w} × {h}");
                ctx.Check(view.SampleImage is { } img && Math.Abs(img.Width - layout.Right * doc.SampleZoom) < 0.5 && Math.Abs(img.Height - h * doc.SampleZoom) < 0.5,
                    $"{name}: sample image {view.SampleImage?.Width}×{view.SampleImage?.Height} = layout {layout.Right}×{h} at {doc.SampleZoom}×");
                doc.ShowAllCharacters = true;
                await ctx.SettleAsync();
                ctx.Check(view.SampleInfo.Contains($" × {font.Height * 6} px", StringComparison.Ordinal), $"{name}: all characters take 6 lines ({view.SampleInfo})");
                doc.ShowAllCharacters = false;

                // Selecting a glyph updates the inspector; arrows move by one and by a row.
                doc.SelectCharacter('A');
                await ctx.SettleAsync();
                var a = font.Glyphs[font.IndexOf('A')];
                ctx.Check(view.Inspector.ValueOf("Character") == "A" && view.Inspector.ValueOf("Width") == $"{a.Width} px" && view.Inspector.ValueOf("Spacing") == $"{a.Spacing} px",
                    $"{name}: inspector shows 'A' (width {view.Inspector.ValueOf("Width")}, spacing {view.Inspector.ValueOf("Spacing")})");
                ctx.Check(view.Inspector.ValueOf("Kerning")?.Contains("'V' (86): -1", StringComparison.Ordinal) == true, $"{name}: inspector lists A's kerning pair ({view.Inspector.ValueOf("Kerning")})");
                ctx.Check(view.Grid.SelectedIndex == font.IndexOf('A'), $"{name}: the grid selection follows");
                view.MoveSelection(1, 0);
                ctx.Check(doc.SelectedGlyph == font.IndexOf('B') && view.Inspector.ValueOf("Character") == "B", $"{name}: Right selects 'B'");
                int columns = view.Columns;
                view.MoveSelection(0, 1);
                ctx.Check(columns > 1 && doc.SelectedGlyph == font.IndexOf('B') + columns, $"{name}: Down moves one row ({columns} columns)");
                ctx.Check(doc.StatusItems.Any(s => s.Text.Contains($"VF v{version}", StringComparison.Ordinal)), $"{name}: status bar names the version");

                // Save As goes through the writer: an unchanged font is saved byte for byte.
                string path = Path.Combine(temp, name);
                doc.SaveTo(path);
                ctx.Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), $"{name}: Save As writes the same {bytes.Length:N0} bytes");
                ctx.Shell.Close(doc);
            }
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [SelfTest("vf.problems")]
    public static async Task ProblemsShown(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        var font = VfSamples.Create(VfPixelFormat.Mono);
        byte a = (byte)font.IndexOf('A'), b = (byte)font.IndexOf('B'), c = (byte)font.IndexOf('C');
        // unsorted pairs: the game never finds A+B
        font = VfEdits.Normalize(font with { Kerning = [new(a, c, -3), new(a, b, -2)] });
        var doc = (VfDocument)module.Kind.OpenBytes(VfWriter.Write(font), "unsorted.vf", "unsorted.vf (self-test)");
        ctx.Shell.AddDocument(doc);
        await ctx.SettleAsync();
        var panel = VfProblemsPanel.For(doc);
        ctx.Check(doc.Problems.Any(p => p.Code == "VF041" && p.Glyph == a), "the pair the game never applies is reported (VF041)");
        ctx.Check(panel.Count == doc.Problems.Count && panel.Count > 0, $"the Problems tab lists {panel.Count} problems");
        ctx.Check(doc.StatusItems.Any(s => s.Text.Contains("warning", StringComparison.Ordinal) && s.Command is not null), "the status bar counts the warning and opens the tab");
        ctx.Check(ctx.Shell.ShowPanel(VfModule.ProblemsPanelId), "the Problems tab can be brought forward");
        await ctx.SettleAsync();
        var (headers, rows, cells) = Cairn.Ui.Diagnostics.GridListCheck.Count(panel.List);
        ctx.Log(Cairn.Ui.Diagnostics.GridListCheck.Describe(panel.List));
        ctx.Check(headers == 1, $"the Problems tab shows a column header row ({headers})");
        ctx.Check(rows == panel.Count && cells >= rows * 4, $"every problem is a row of cells, not its text ({rows} rows, {cells} cells)");
        ctx.Shell.Close(doc);
    }

    [SelfTest("vf.preview-provider")]
    public static void PreviewProvider(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        // The packfile preview pane takes the first module that previews a name.
        var provider = ctx.Shell.Modules.OfType<IAssetPreviewProvider>().FirstOrDefault(p => p.CanPreview("bigfont.vf"));
        ctx.Check(ReferenceEquals(provider, module), $"the fonts module previews .vf entries ({provider?.GetType().Name ?? "none"})");
        ctx.Check(ctx.Shell.Modules.SelectMany(m => m.DocumentKinds).Any(k => k.Extensions.Contains(".vf")), "a document kind opens .vf (Open in Cairn)");
        var font = VfSamples.Create(VfPixelFormat.Indexed);
        var element = module.CreatePreview(VfWriter.Write(font), "sample.vf");
        ctx.Check(element is VfPreview { Font: not null, SampleLayout: not null } p && p.SampleLayout.Width == VfLayout.Measure(font, VfLayout.Encode(VfDocument.PickSample(font))).Width,
            "the preview draws the sample with the layout function");
        var broken = module.CreatePreview([(byte)'V', (byte)'F', (byte)'N', (byte)'T', 1, 0, 0, 0], "broken.vf");
        ctx.Check(broken is VfPreview { Font: null }, "a damaged font previews as a message, not an exception");
    }

    [SelfTest("vf.stock-fonts")]
    public static async Task StockFonts(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no fonts module in this build"); return; }
        string? ui = LocalPaths.GameDirectory is { } game ? Directory.EnumerateFiles(game, "*.vpp").FirstOrDefault(p => Path.GetFileName(p).Equals("ui.vpp", StringComparison.OrdinalIgnoreCase)) : null;
        if (ui is null) { ctx.Skip("no game directory with ui.vpp (" + LocalPaths.HowToSet(LocalPaths.GameDirectoryVariable, "gameDirectory") + ")"); return; }
        var archive = VppArchive.Open(ui);
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".vf", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes = archive.ReadEntry(entry);
            var doc = (VfDocument)module.Kind.OpenBytes(bytes, entry.Name, "ui.vpp › " + entry.Name);
            ctx.Shell.AddDocument(doc);
            await ctx.SettleAsync();
            var view = (VfDocumentView)doc.View;
            ctx.Check(view.Grid.Items.Count == doc.Current.GlyphCount, $"{entry.Name}: {view.Grid.Items.Count} glyphs in the grid");
            ctx.Check(!doc.Problems.Any(p => p.Severity != VfSeverity.Information), $"{entry.Name}: no errors or warnings");
            ctx.Check(doc.Serialize().AsSpan().SequenceEqual(bytes), $"{entry.Name}: writes back byte for byte");
            ctx.Shell.Close(doc);
        }
    }
}
