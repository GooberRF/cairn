#if CAIRN_MODULE_VPP && CAIRN_MODULE_VFX
using System.Windows;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Cairn.Vfx.Rfe;
using Cairn.Vfx.Ui;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Rfe;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>
/// Red Faction II effects in the install's packfile groups: Botmatch_vfx (loose) and All_Levels (packed) list them as
/// RF2 effects with facts; the preview pane plays one with its meshes, particles and textures; Open in Cairn gives a
/// read-only tab whose textures resolve and whose playback advances; a loose file opens from disk; Convert to makes a
/// Red Faction .vfx from a Botmatch_vfx entry that reads back, lints clean and plays in the effect preview; the batch
/// converts several. The install is only read; every output goes to a temporary folder.
/// </summary>
public static class Rf2EffectSelfTests
{
    [SelfTest("vfx.rfe-install")]
    public static async Task Rf2Effects(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        string? media = LocalPaths.Rf2GameDirectory is { } rf2 ? Path.Combine(rf2, "pc_media") : null;
        if (media is null || !File.Exists(Path.Combine(media, "Botmatch_vfx.toc_group"))) { ctx.Skip("no Red Faction II install (" + LocalPaths.HowToSet(LocalPaths.Rf2GameDirectoryVariable, "rf2GameDirectory") + ")"); return; }
        var vpp = shell.Modules.OfType<VppModule>().First();
        var vfx = shell.Modules.OfType<VfxModule>().First();
        string folder = Path.Combine(Path.GetTempPath(), "cairn-rf2fx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<IDocument>();
        try
        {
            // ── Botmatch_vfx (loose): an effect with meshes and emitters ──
            if (!ctx.Check(shell.OpenFile(Path.Combine(media, "Botmatch_vfx.toc_group")) && shell.ActiveDocument is VppDocument { IsReadOnly: true }, "Botmatch_vfx.toc_group opens read only")) return;
            var loose = (VppDocument)shell.ActiveDocument!;
            opened.Add(loose);
            await ctx.SettleAsync(TimeSpan.FromSeconds(60));
            var effects = loose.Current.Items.Where(i => RfeReader.HasExtension(i.Name)).ToList();
            ctx.Check(effects.Count == 288, $"the group lists its effects ({effects.Count})");
            var item = effects.FirstOrDefault(i => i.Name.Equals("objex_barrel.rfe", StringComparison.OrdinalIgnoreCase)) ?? effects.First(i => Both(i.Source.ReadAll()));
            ctx.Check(loose.List.AllRows.First(r => r.Name == item.Name).TypeName == "RF2 effect", "listed as an RF2 effect");
            ctx.Check(VppFacts.Describe(item).Rows.Any(r => r.Label == "Kind" && r.Value.StartsWith("Red Faction II effect", StringComparison.Ordinal)), "the details say it is a Red Faction II effect");
            loose.SetSelection([item]);
            await ctx.SettleAsync();
            ctx.Check(loose.Commands.CanOpenInCairn, "Open in Cairn is offered");
            await Preview(ctx, item, loose);

            // Open in Cairn: the read-only tab, its textures, its playback
            await loose.Commands.OpenInCairnAsync(item);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is RfeDocument, "Open in Cairn opens a Red Faction II effect tab"))
            {
                var tab = (RfeDocument)shell.ActiveDocument!;
                opened.Add(tab);
                await Tab(ctx, tab);
            }

            // Convert to from the entry: a .vfx that reads back, lints clean and plays in the effect preview
            byte[] bytes = item.Source.ReadAll();
            var choice = AssetConversions.TargetsFor(shell.Modules, item.Name, bytes.AsSpan(0, 16)).FirstOrDefault(c => c.Module == vfx && c.Target.Extension == ".vfx");
            if (ctx.Check(choice is not null, "Convert to lists a Red Faction effect (.vfx)"))
            {
                string outFolder = Path.Combine(folder, "entry");
                var (written, stall) = await LongestStallAsync(() => vpp.ConvertEntryAsync(loose, item, choice!, interactive: false, outputFolder: outFolder));
                ctx.Log($"  converting {item.Name} held the UI thread for at most {stall:0} ms (opening the converted tab included)");
                if (shell.ActiveDocument is VfxDocument converted) opened.Add(converted);
                if (ctx.Check(written is not null && File.Exists(written), $"... written ({written})"))
                {
                    var file = VfxReader.Read(File.ReadAllBytes(written!), Path.GetFileName(written!));
                    var errors = VfxLinter.Lint(file, new VfxLintContext(Path.GetFileName(written))).Where(d => d.Severity == Cairn.Rfa.Linting.DiagnosticSeverity.Error).ToList();
                    ctx.Check(errors.Count == 0, $"... reads back with no lint errors ({file.Sections.OfType<VfxMesh>().Count()} meshes, {file.Sections.OfType<VfxParticleSystem>().Count()} particle systems; {string.Join("; ", errors)})");
                    ctx.Check(vfx.RfeTools.LastConversion?.Notes.Length > 0, $"... with a report ({vfx.RfeTools.LastConversion?.Approximations} lines approximate or drop something)");
                    using var preview = (Cairn.Vfx.Ui.Preview.VfxPreview)vfx.CreatePreview(File.ReadAllBytes(written!), Path.GetFileName(written!))!;
                    var window = Host();
                    try
                    {
                        window.Content = preview;
                        await preview.Loading;
                        await ctx.SettleAsync();
                        var renderer = preview.Renderer;
                        float before = preview.Playback?.Time ?? 0;
                        await Task.Delay(500);
                        ctx.Check(preview.Message is null && renderer is not null && preview.Playback?.IsPlaying == true && preview.Playback.Time != before,
                            $"... and plays in the effect preview ({preview.Message ?? $"{renderer?.ParticleQuadCount} particle quads, {renderer?.LoadedTextures} textures"})");
                    }
                    finally { window.Content = null; window.Close(); }
                }
            }

            // --rfe-out <folder>: keep converted copies of a few effects (side-by-side screenshots)
            if (choice is not null && ctx.Options.TryGetValue("rfe-out", out var keep) && !string.IsNullOrWhiteSpace(keep))
            {
                vfx.RfeTools.QuietTextures = Cairn.Rfa.Formats.Legacy.Rf2TextureMode.KeepOriginals;
                try
                {
                    foreach (string name in new[] { item.Name, "fire_giant.rfe", "holo_symbol.rfe" })
                    {
                        if (effects.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } e) continue;
                        await vpp.ConvertEntryAsync(loose, e, choice, interactive: false, outputFolder: keep);
                        if (shell.ActiveDocument is VfxDocument d && !opened.Contains(d)) opened.Add(d);
                    }
                }
                finally { vfx.RfeTools.QuietTextures = Cairn.Rfa.Formats.Legacy.Rf2TextureMode.Translate; }
            }

            // the batch: three effects into a folder
            var batch = effects.Where(i => RfeReader.Identify(i.Source.ReadAll()) == RfeLayout.Pc).Take(3).ToList();
            shell.ActiveDocument = loose;
            loose.SetSelection(batch);
            await ctx.SettleAsync();
            string batchOut = Path.Combine(folder, "batch");
            string? summary = await vpp.ConvertWithAsync(loose, vfx, interactive: false, options: new RfeConvertChoice(batchOut));
            ctx.Check(summary?.StartsWith("Converted 3 of 3", StringComparison.Ordinal) == true && Directory.GetFiles(batchOut, "*.vfx").Length == 3, $"Convert effects... converts three ({summary})");

            // the old layout: refused with its reason
            var fan = effects.FirstOrDefault(i => i.Name.Equals("fan.rfe", StringComparison.OrdinalIgnoreCase));
            if (fan is not null)
                ctx.Check(VppFacts.Describe(fan).Warnings.Any(w => w.Contains("PC game does not load either", StringComparison.Ordinal)), "fan.rfe (the old layout) says why it cannot be shown");

            // ── a loose file opened from disk ──
            string disk = Path.Combine(media, "Botmatch_vfx", item.Name);
            if (File.Exists(disk) && ctx.Check(shell.OpenFile(disk) && shell.ActiveDocument is RfeDocument, $"{item.Name} opens from disk"))
            {
                var tab = (RfeDocument)shell.ActiveDocument!;
                opened.Add(tab);
                await Tab(ctx, tab);
            }

            // ── All_Levels (packed) ──
            if (ctx.Check(shell.OpenFile(Path.Combine(media, "All_Levels.toc_group")) && shell.ActiveDocument is VppDocument, "All_Levels.toc_group opens"))
            {
                var packed = (VppDocument)shell.ActiveDocument!;
                opened.Add(packed);
                await ctx.SettleAsync(TimeSpan.FromSeconds(60));
                var levelEffects = packed.Current.Items.Where(i => RfeReader.HasExtension(i.Name)).ToList();
                ctx.Check(levelEffects.Count == 162, $"All_Levels lists its effects ({levelEffects.Count})");
                var packedItem = levelEffects.FirstOrDefault(i => i.Name.Equals("barge_sink.rfe", StringComparison.OrdinalIgnoreCase)) ?? levelEffects.First(i => Both(i.Source.ReadAll()));
                packed.SetSelection([packedItem]);
                await ctx.SettleAsync();
                await Preview(ctx, packedItem, packed);
                await packed.Commands.OpenInCairnAsync(packedItem);
                await ctx.SettleAsync();
                if (ctx.Check(shell.ActiveDocument is RfeDocument, $"{packedItem.Name} opens in a tab from the packed group"))
                {
                    var tab = (RfeDocument)shell.ActiveDocument!;
                    opened.Add(tab);
                    await Tab(ctx, tab);
                }
                // how long converting holds the UI thread (logged; the review measured 268 and 368 ms before the packs
                // were read and the conversion made off the UI thread)
                var target = AssetConversions.TargetsFor(shell.Modules, packedItem.Name, packedItem.Source.ReadAll().AsSpan(0, 16)).FirstOrDefault(c => c.Module == vfx && c.Target.Extension == ".vfx");
                foreach (var (name, mode) in new[] { ("objex_barel2.rfe", Cairn.Rfa.Formats.Legacy.Rf2TextureMode.KeepOriginals), ("objex_jeepdie.rfe", Cairn.Rfa.Formats.Legacy.Rf2TextureMode.Translate) })
                {
                    if (target is null || levelEffects.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } e) continue;
                    vfx.RfeTools.QuietTextures = mode;
                    try
                    {
                        var (_, held) = await LongestStallAsync(() => vpp.ConvertEntryAsync(packed, e, target, interactive: false, outputFolder: Path.Combine(folder, "stall")));
                        ctx.Log($"  converting {name} ({mode}) held the UI thread for at most {held:0} ms (opening the converted tab included)");
                    }
                    finally { vfx.RfeTools.QuietTextures = Cairn.Rfa.Formats.Legacy.Rf2TextureMode.Translate; }
                    if (shell.ActiveDocument is VfxDocument d && !opened.Contains(d)) opened.Add(d);
                    shell.ActiveDocument = packed;
                }
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Converting into a Red Faction packfile with the textures kept: a texture the packfile already has is not added
    /// again under a new name (unless replacing), and a texture several effects of one batch share is added once. The
    /// converted effect itself still takes a free name. Runs on a scratch .vpp in a temporary folder.
    /// </summary>
    [SelfTest("vfx.rfe-packfile")]
    public static async Task IntoPackfile(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var vpp = shell.Modules.OfType<VppModule>().First();
        var vfx = shell.Modules.OfType<VfxModule>().First();
        string folder = Path.Combine(Path.GetTempPath(), "cairn-rfe-vpp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        VppDocument? doc = null;
        var keep = vfx.RfeTools.QuietTextures;
        try
        {
            byte[] effect = SyntheticRfe.Write(SyntheticRfe.Sample());
            byte[] glow = [1, 2, 3, 4, 5, 6, 7, 8];
            (string, byte[])[] files =
            [
                ("sample.rfe", effect), ("sample2.rfe", effect), ("glow.tga", glow),
                ("shared.peg", Cairn.Rfa.Formats.Legacy.SyntheticRf2Mesh.SamplePeg(("crate_side.tga", 8, 8, 0xFF3080C0), ("glow.tga", 8, 8, 0x80FFC040), ("spark.tga", 4, 4, 0xFFFFFFFF))),
            ];
            string path = Path.Combine(folder, "effects.vpp");
            Cairn.Vpp.Writing.VppSaver.Save(Cairn.Vpp.Editing.VppEdit.AddSources(VppPackage.Empty, [.. files.Select(f => (f.Item1, (VppSource)new MemorySource(f.Item2)))], Cairn.Vpp.Editing.VppClashPolicy.KeepBoth).Package,
                path, null, CancellationToken.None, Cairn.Vpp.Writing.VppSaveOptions.Default);
            if (!ctx.Check(shell.OpenFile(path) && shell.ActiveDocument is VppDocument { IsReadOnly: false }, "a scratch Red Faction packfile opens for editing")) return;
            doc = (VppDocument)shell.ActiveDocument!;
            await ctx.SettleAsync();
            vfx.RfeTools.QuietTextures = Cairn.Rfa.Formats.Legacy.Rf2TextureMode.KeepOriginals;
            List<string> Names() => [.. doc.Current.Items.Select(i => i.Name).Order(StringComparer.OrdinalIgnoreCase)];

            // one entry, into the packfile (no output folder): the effect and the two textures it lacks
            var item = doc.Current.Find("sample.rfe")!;
            var choice = AssetConversions.TargetsFor(shell.Modules, item.Name, effect.AsSpan(0, 16)).First(c => c.Module == vfx && c.Target.Extension == ".vfx");
            string? written = await vpp.ConvertEntryAsync(doc, item, choice, interactive: false);
            var names = Names();
            ctx.Check(written == "sample.vfx" && names.Contains("crate_side.tga") && names.Contains("spark.tga"), $"one effect goes into the packfile with its textures ({written}: {string.Join(", ", names)})");
            ctx.Check(!names.Any(n => n.Contains(" (", StringComparison.Ordinal)) && doc.Current.Find("glow.tga")!.Source.ReadAll().SequenceEqual(glow),
                $"... the glow.tga already there is kept and not added again under another name ({string.Join(", ", names)})");

            // the batch: two effects sharing their textures
            doc.SetSelection([doc.Current.Find("sample.rfe")!, doc.Current.Find("sample2.rfe")!]);
            await ctx.SettleAsync();
            string? summary = await vpp.ConvertWithAsync(doc, vfx, interactive: false, options: new RfeConvertChoice("", Cairn.Rfa.Formats.Legacy.Rf2TextureMode.KeepOriginals, IntoPackfile: true));
            names = Names();
            ctx.Check(names.Contains("sample (2).vfx") && names.Contains("sample2.vfx"), $"the batch adds both effects, the first under a free name ({summary}: {string.Join(", ", names)})");
            ctx.Check(names.Count(n => n.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) == 3 && !names.Any(n => n.Contains(" (", StringComparison.Ordinal) && n.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)),
                $"... and adds no texture twice ({string.Join(", ", names)})");
        }
        finally
        {
            vfx.RfeTools.QuietTextures = keep;
            if (doc is not null && shell.Documents.Contains(doc)) shell.CloseDiscarding(doc);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Runs <paramref name="work"/> and measures the longest the UI thread did not answer meanwhile.</summary>
    private static async Task<(T Result, double MaxStallMs)> LongestStallAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        double max = 0;
        using var stop = new CancellationTokenSource();
        var probe = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Send).Task.ConfigureAwait(false);
                max = Math.Max(max, watch.Elapsed.TotalMilliseconds);
                await Task.Delay(10).ConfigureAwait(false);
            }
        });
        var result = await work();
        await stop.CancelAsync();
        await probe;
        return (result, max);
    }

    private static bool Both(byte[] bytes)
    {
        try { return RfeReader.Read(bytes, "x.rfe") is { Meshes.Length: > 0, Emitters.Length: > 0 }; }
        catch (Cairn.Formats.AssetFormatException) { return false; }
    }

    private static Window Host()
    {
        var window = new Window { Width = 700, Height = 500, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow, Left = -2000, Top = -2000 };
        window.Show();
        return window;
    }

    /// <summary>The preview pane plays the entry with meshes, particles and textures.</summary>
    private static async Task Preview(SelfTestContext ctx, VppItem item, VppDocument doc)
    {
        var window = Host();
        var area = new VppPreviewArea(ctx.Shell);
        window.Content = area;
        try
        {
            area.Show(item, [item], doc.Current, immediate: true);
            await area.SettleAsync();
            var preview = (area.Preview.View as Cairn.Previews.ModulePreviewHost)?.Inner as RfePreview;
            if (!ctx.Check(preview is not null, $"{item.Name} previews in the effects module's view ({area.Preview.Kind})")) return;
            await preview!.Loading;
            await ctx.SettleAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(800);
            var r = preview.Renderer;
            ctx.Check(preview.Message is null && r is not null, $"... it shows ({preview.Message ?? "no message"})");
            if (r is null) return;
            ctx.Check(preview.Playback?.IsPlaying == true, "... it plays");
            ctx.Check(r.LoadedTextures > 0 && r.PendingTextures == 0, $"... with its textures ({r.LoadedTextures} loaded)");
            ctx.Check(r.VisibleMeshes > 0 || r.ParticleQuadCount > 0, $"... meshes and particles appear ({r.VisibleMeshes} meshes, {r.ParticleQuadCount} particles)");
        }
        finally
        {
            area.Dispose();
            window.Close();
        }
    }

    /// <summary>A tab: read-only, textures found, meshes and particles drawn, playback advancing.</summary>
    private static async Task Tab(SelfTestContext ctx, RfeDocument tab)
    {
        ctx.Check(tab.IsReadOnly && !((IDocument)tab).CanSave, $"{tab.DisplayName}: read-only, cannot be saved");
        _ = tab.View;
        await tab.TexturesFound;
        await ctx.SettleAsync(TimeSpan.FromSeconds(60));
        var textures = tab.Facts.Where(f => f.Section == "Textures").ToList();
        int found = textures.Count(f => f.Value is not ("not found" or "no name" or "looking…"));
        ctx.Check(found > 0 && found * 4 >= textures.Count * 3, $"... {found} of {textures.Count} textures found: {string.Join("; ", textures.Select(f => f.Label + " → " + f.Value).Take(6))}");
        ctx.Check(AssetConversions.TargetsFor(ctx.Shell, tab).Any(c => c.Target.Extension == ".vfx"), "... File > Convert to lists a Red Faction effect");
        var view = (RfeDocumentView)tab.View;
        tab.SeekSeconds(tab.Effect.Duration * 0.4);
        view.Viewport.Renderer.Update(view.Viewport.Camera);
        var r = view.Viewport.Renderer;
        ctx.Check((tab.Effect.Meshes.Length == 0 || r.VisibleMeshes > 0) && (tab.Effect.Emitters.Length == 0 || r.ParticleQuadCount > 0),
            $"... at 40 % it draws {r.VisibleMeshes} of {tab.Effect.Meshes.Length} meshes and {r.ParticleQuadCount} particles");
        float before = tab.Playback.Time;
        tab.Playback.Play();
        await Task.Delay(400);
        tab.Playback.Pause();
        ctx.Check(tab.Playback.Time != before, $"... playback advances ({before:0} -> {tab.Playback.Time:0} ticks)");
    }
}
#endif
