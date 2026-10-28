#if CAIRN_MODULE_LVL && CAIRN_MODULE_VPP && CAIRN_MODULE_RFA
using System.Diagnostics;
using Cairn.Formats.Gltf;
using Cairn.Formats.Obj;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Reading;
using Cairn.Lvl.Ui;
using Cairn.Lvl.Ui.Dialogs;
using Cairn.Lvl.Ui.Documents;
using Cairn.Lvl.Ui.Preview;
using Cairn.Previews;
using Cairn.Rfa.Formats.V3d;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>
/// Levels, packfiles and meshes together, end to end: a Red Faction II level in its packfile group previews and converts
/// to glTF and a group file; a stock level previews in its packfile and converts to glTF, OBJ, a static mesh and a group
/// file, each read back (the mesh and the group file open in tabs); a mesh goes to OBJ and back, and meshes, OBJ and
/// glTF files (a level's glTF too) convert to group files. Every output goes to a temporary folder.
/// </summary>
public static class LevelChainSelfTests
{
    /// <summary>A temporary folder and the tabs a chain opened, both gone when it ends.</summary>
    private sealed class Chain(ShellViewModel shell, string what) : IDisposable
    {
        public string Folder { get; } = CreateFolder(what);
        public List<IDocument> Opened { get; } = [];

        private static string CreateFolder(string what)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"cairn-chain-{what}-{Guid.NewGuid().ToString("N")[..8]}");
            Directory.CreateDirectory(folder);
            return folder;
        }

        public string Out(string name)
        {
            string folder = Path.Combine(Folder, name);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public void Dispose()
        {
            foreach (var d in Enumerable.Reverse(Opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            try
            {
                // CAIRN_CHAIN_OUT=<folder> keeps the outputs for checking by hand (in the level editor, for example)
                if (Environment.GetEnvironmentVariable("CAIRN_CHAIN_OUT") is { Length: > 0 } keep)
                {
                    foreach (string file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
                    {
                        string target = Path.Combine(keep, what, Path.GetRelativePath(Folder, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
                    }
                }
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ConversionChoice? Choice(ShellViewModel shell, string name, string module, string extension) =>
        AssetConversions.TargetsFor(shell.Modules, name).FirstOrDefault(c => c.Module.Id == module && c.Target.Extension == extension);

    /// <summary>Selects <paramref name="item"/> in the packfile tab and waits for its preview: the levels module's view, built.</summary>
    private static async Task<LvlPreview?> PreviewAsync(SelfTestContext ctx, VppDocument doc, VppItem item)
    {
        var view = (VppDocumentView)doc.View;
        doc.SetSelection([item]);
        await Task.Delay(400);
        var pane = view.PreviewHost.Content as VppPreviewPane;
        if (pane is not null) await pane.Pending;
        await ctx.SettleAsync(TimeSpan.FromSeconds(60));
        if (pane?.View is not ModulePreviewHost { Inner: LvlPreview preview }) return null;
        await preview.Loading;
        await preview.View.Building;
        await ctx.SettleAsync(TimeSpan.FromSeconds(30));
        return preview;
    }

    /// <summary>The report of the levels module's last conversion, as one line for the log.</summary>
    private static string Report(LvlModule lvl)
    {
        var outcome = lvl.LastOutcomes?.FirstOrDefault();
        if (outcome?.Report is not { } report) return outcome?.Error ?? "no report";
        int errors = report.Issues.Count(i => i.Severity == LvlIssueSeverity.Error), warnings = report.Issues.Count(i => i.Severity == LvlIssueSeverity.Warning);
        return $"{errors} errors, {warnings} warnings, {report.Stats.Triangles:N0} triangles, {report.Stats.Textures} textures ({report.MissingTextures.Length} missing)"
            + (errors > 0 ? ": " + string.Join(" | ", report.Issues.Where(i => i.Severity == LvlIssueSeverity.Error).Take(3).Select(i => i.Message)) : "");
    }

    private static bool Clean(LvlModule lvl) => lvl.LastOutcomes is [{ Report: { HasErrors: false } }];

    [SelfTest("integration.level-chain-rf2")]
    public static async Task Rf2Chain(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        string? toc = LocalPaths.Rf2GameDirectory is { } rf2 ? Path.Combine(rf2, "pc_media", "All_Levels.toc_group") : null;
        if (toc is null || !File.Exists(toc)) { ctx.Skip("no Red Faction II install (" + LocalPaths.HowToSet(LocalPaths.Rf2GameDirectoryVariable, "rf2GameDirectory") + ")"); return; }
        var lvl = shell.Modules.OfType<LvlModule>().First();
        var vpp = shell.Modules.OfType<VppModule>().First();
        using var chain = new Chain(shell, "rf2");
        var watch = Stopwatch.StartNew();
        if (!ctx.Check(shell.OpenFile(toc) && shell.ActiveDocument is VppDocument { IsReadOnly: true }, "All_Levels.toc_group opens read only in the packfile view")) return;
        var doc = (VppDocument)shell.ActiveDocument!;
        chain.Opened.Add(doc);
        await ctx.SettleAsync(TimeSpan.FromSeconds(60));
        var levels = doc.Current.Items.Where(i => Path.GetExtension(i.Name).Equals(".rfl", StringComparison.OrdinalIgnoreCase)).ToList();
        ctx.Check(levels.Count >= 100, $"the group lists its levels ({levels.Count})");
        // a small multiplayer level keeps the run short (dmc13 when it is there, as checked by eye before)
        var item = levels.FirstOrDefault(i => i.Name.Equals("dmc13.rfl", StringComparison.OrdinalIgnoreCase))
            ?? levels.Where(i => i.Source.Size > 200_000).OrderBy(i => i.Source.Size).FirstOrDefault();
        if (!ctx.Check(item is not null, "a level to convert")) return;

        // the details pane: the whole summary, its textures found in the group's .peg packs, nothing called missing
        var facts = await Task.Run(() => Cairn.Vpp.Facts.VppFacts.Describe(item!, new Cairn.Vpp.Facts.VppFactsContext(doc.Current, shell.Assets.Resolver)));
        int sectionRows = facts.Rows.Count(r => r.Label.StartsWith("Sections: ", StringComparison.Ordinal));
        int inPack = facts.Rows.Count(r => r.Label.StartsWith("References: ", StringComparison.Ordinal) && r.Value.EndsWith("in this packfile", StringComparison.Ordinal));
        ctx.Check(facts["Author"] is not null && facts["Loads in"] == "Red Faction II" && facts["Properties: Fog"] is not null && facts["Properties: Lightmap multiplier"] is not null
            && facts["Statistics: Rooms"] is not null && sectionRows >= 20 && inPack > 0 && facts.Warnings.Length == 0,
            $"{item!.Name} details: author {facts["Author"]}, fog {facts["Properties: Fog"]}, {facts["Statistics: Rooms"]} rooms, {sectionRows} sections, {inPack} textures in the group's packs, {facts.Warnings.Length} warnings");

        var preview = await PreviewAsync(ctx, doc, item!);
        if (ctx.Check(preview is not null, $"{item!.Name}: the packfile previews it with the levels module's view"))
        {
            var scene = preview!.View;
            ctx.Check(scene.TrianglesDrawn > 1000, $"{item.Name}: drawn ({scene.TrianglesDrawn:N0} triangles)");
            ctx.Check(scene.TexturesLoaded >= scene.TexturesUsed * 3 / 4 && scene.TexturesLoaded > 0,
                $"{item.Name}: textures from its .peg packs and the install's shared packs ({scene.TexturesLoaded} of {scene.TexturesUsed}; missing {string.Join(", ", scene.MissingTextures.Take(6))})");
        }

        var targets = AssetConversions.TargetsFor(shell.Modules, item.Name).Where(c => c.Module == lvl).Select(c => c.Target.Extension).ToList();
        ctx.Check(targets.Contains(".glb") && targets.Contains(".rfg"), "Convert to lists glTF and a group file: " + string.Join(", ", targets));

        string glbOut = chain.Out("glb");
        string? glb = await vpp.ConvertEntryAsync(doc, item, Choice(shell, item.Name, "lvl", ".glb")!, interactive: false, outputFolder: glbOut);
        if (ctx.Check(glb is not null && File.Exists(glb) && Clean(lvl), $"{item.Name} to .glb: {Report(lvl)}"))
        {
            var gltf = GltfReader.ReadFile(glb!);
            ctx.Check(gltf.Images.Count > 0 && gltf.Images.All(i => i.Data is { Length: > 0 }), $"the .glb carries the RF2 textures as images ({gltf.Images.Count})");
            ctx.Check(lvl.LastOutcomes![0].Report!.Issues.All(i => !i.Message.Contains("No Red Faction II texture packs", StringComparison.Ordinal)), "the level's texture packs were passed to the conversion");
        }

        string rfgOut = chain.Out("rfg");
        string? rfg = await vpp.ConvertEntryAsync(doc, item, Choice(shell, item.Name, "lvl", ".rfg")!, interactive: false, outputFolder: rfgOut);
        if (ctx.Check(rfg is not null && File.Exists(rfg) && Clean(lvl), $"{item.Name} to .rfg: {Report(lvl)}"))
        {
            var group = RfgReader.Read(File.ReadAllBytes(rfg!), Path.GetFileName(rfg!));
            ctx.Check(group.Problems.Length == 0 && group.AllBrushes.Count() > 10, $"the group file reads back: {group.Groups.Length} groups, {group.AllBrushes.Count()} brushes, {group.Problems.Length} problems");
            ctx.Check(lvl.LastOutcomes![0].Report!.Issues.All(i => !i.Message.Contains("clutter table was not found", StringComparison.Ordinal)), "the install's clutter table was found (clutter becomes Alpine meshes)");
            if (ctx.Check(shell.OpenFile(rfg!) && shell.ActiveDocument is LvlDocument, "the group file opens in a tab"))
            {
                var tab = (LvlDocument)shell.ActiveDocument!;
                chain.Opened.Add(tab);
                await tab.Loading;
                ctx.Check(tab.Source?.GroupFile is { } opened && opened.AllBrushes.Count() == group.AllBrushes.Count(), $"... with its {tab.Source?.GroupFile?.AllBrushes.Count()} brushes ({tab.LoadError ?? "read"})");
            }
        }
        // "Convert levels…" on two levels of the read-only group: into a folder, each with its own texture packs
        shell.Activate(doc);
        await ctx.SettleAsync();
        var second = levels.Where(i => i != item && i.Source.Size > 200_000).OrderBy(i => i.Source.Size).First();
        doc.SetSelection([item, second]);
        await ctx.SettleAsync();
        string batchOut = chain.Out("batch");
        string? summary = await vpp.ConvertWithAsync(doc, lvl, interactive: false,
            options: new LvlConvertChoice(new LvlConvertOptions { Target = LvlConvertTarget.Glb }, LvlOutputPlace.Folder, batchOut, false));
        var batch = lvl.LastOutcomes ?? [];
        ctx.Check(summary is not null && batch.Count == 2 && batch.All(o => o.Succeeded && o.Report is { HasErrors: false }), $"Convert levels… on {item.Name} and {second.Name}: {summary}");
        ctx.Check(batch.All(o => o.Written.Count == 1 && GltfReader.ReadFile(o.Written[0]).Images.Count > 0), "... each .glb with its RF2 textures");
        ctx.Log($"Red Faction II chain in {watch.ElapsedMilliseconds:N0} ms");
    }

    [SelfTest("integration.level-chain-rf1")]
    public static async Task StockChain(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        string? vppPath = LocalPaths.GameDirectory is { } game ? Path.Combine(game, "levels1.vpp") : null;
        if (vppPath is null || !File.Exists(vppPath)) { ctx.Skip("no game folder with levels1.vpp (" + LocalPaths.HowToSet(LocalPaths.GameDirectoryVariable, "gameDirectory") + ")"); return; }
        var lvl = shell.Modules.OfType<LvlModule>().First();
        var vpp = shell.Modules.OfType<VppModule>().First();
        using var chain = new Chain(shell, "rf1");
        if (!ctx.Check(shell.OpenFile(vppPath) && shell.ActiveDocument is VppDocument, "levels1.vpp opens in the packfile view")) return;
        var doc = (VppDocument)shell.ActiveDocument!;
        chain.Opened.Add(doc);
        await ctx.SettleAsync(TimeSpan.FromSeconds(60));
        var item = doc.Current.Find("L1S1.rfl");
        if (!ctx.Check(item is not null, "levels1.vpp holds L1S1.rfl")) return;

        var preview = await PreviewAsync(ctx, doc, item!);
        if (ctx.Check(preview is not null, "L1S1.rfl previews with the levels module's view"))
            ctx.Check(preview!.View.TrianglesDrawn > 10_000 && preview.View.TexturesLoaded > 10, $"drawn: {preview.View.TrianglesDrawn:N0} triangles, {preview.View.TexturesLoaded} of {preview.View.TexturesUsed} textures");

        var outputs = new Dictionary<string, string>();
        foreach (string ext in new[] { ".glb", ".gltf", ".obj", ".v3m", ".rfg" })
        {
            var choice = Choice(shell, item!.Name, "lvl", ext);
            if (!ctx.Check(choice is not null, $"Convert to lists {ext}")) continue;
            var watch = Stopwatch.StartNew();
            string? main = await vpp.ConvertEntryAsync(doc, item, choice!, interactive: false, outputFolder: chain.Out(ext.TrimStart('.')));
            if (ctx.Check(main is not null && File.Exists(main) && Clean(lvl), $"L1S1.rfl to {ext} in {watch.ElapsedMilliseconds:N0} ms: {Report(lvl)}")) outputs[ext] = main!;
        }

        if (outputs.TryGetValue(".glb", out var glbPath))
        {
            var glb = GltfReader.ReadFile(glbPath);
            ctx.Check(glb.Nodes.Count > 100 && glb.Images.Count > 10, $".glb reads back: {glb.Nodes.Count} nodes, {glb.Images.Count} images");
        }
        if (outputs.TryGetValue(".obj", out var objPath))
        {
            var obj = ObjReader.ReadFile(objPath);
            var (materials, missing) = ObjReader.ReadMaterialLibraries(obj, Path.GetDirectoryName(objPath)!);
            int faces = obj.Objects.Sum(o => o.Faces.Count);
            ctx.Check(faces > 1000 && materials.Materials.Count > 10 && missing.Count == 0, $".obj reads back: {faces:N0} faces, {materials.Materials.Count} materials");
        }
        if (outputs.TryGetValue(".v3m", out var v3mPath))
        {
            var mesh = V3dReader.ReadFile(v3mPath);
            ctx.Check(mesh.Submeshes.Any(), $".v3m reads back: {mesh.Submeshes.Count()} submeshes");
            if (ctx.Check(shell.OpenFile(v3mPath) && shell.ActiveDocument is { } meshTab && shell.Modules.First(m => m.DocumentKinds.Any(k => k.Id == meshTab.Kind.Id)).Id == "rfa", "the .v3m opens in the meshes module"))
                chain.Opened.Add(shell.ActiveDocument!);
        }
        int directBrushes = -1;
        if (outputs.TryGetValue(".rfg", out var rfgPath))
        {
            var group = RfgReader.Read(File.ReadAllBytes(rfgPath), Path.GetFileName(rfgPath));
            directBrushes = group.AllBrushes.Count();
            ctx.Check(group.Problems.Length == 0 && directBrushes > 100, $".rfg reads back: {group.Groups.Length} groups, {directBrushes} brushes");
            if (ctx.Check(shell.OpenFile(rfgPath) && shell.ActiveDocument is LvlDocument, "the .rfg opens in a group tab"))
            {
                var tab = (LvlDocument)shell.ActiveDocument!;
                chain.Opened.Add(tab);
                await tab.Loading;
                ctx.Check(tab.Source?.GroupFile is not null && AssetConversions.TargetsFor(shell, tab).Any(c => c.Target.Extension == ".glb"), "... and converts on (glTF listed)");
            }
        }

        // the level's glTF back into a group file: from the compiled geometry one brush per room; from the brushes the
        // same brushes as the direct conversion (Cairn's extras restore them)
        var toRfg = new LvlConvertChoice(new LvlConvertOptions { Target = LvlConvertTarget.Rfg }, LvlOutputPlace.Folder, chain.Out("gltf-rfg"), false);
        if (outputs.TryGetValue(".gltf", out var gltfPath))
        {
            string? back = await lvl.ConvertFileToRfgAsync(gltfPath, toRfg);
            if (ctx.Check(back is not null && Clean(lvl), $"L1S1.gltf (compiled geometry) to .rfg: {Report(lvl)}"))
                ctx.Check(RfgReader.Read(File.ReadAllBytes(back!), Path.GetFileName(back!)).AllBrushes.Any(), "... one brush per room");
        }
        var bytes = await Task.Run(item!.Source.ReadAll);
        string brushesGltf = Path.Combine(chain.Out("gltf-brushes"), "L1S1.gltf");
        var written = await Task.Run(() => LvlConverter.ConvertToFile(LvlSource.Read(bytes, "L1S1.rfl").Input, brushesGltf,
            // every face class (portal brushes and invisible faces are left out by default), as a group file keeps them
            new LvlConvertOptions { Target = LvlConvertTarget.Gltf, Geometry = LvlGeometryChoice.Brushes, ExcludedFaceClasses = default, TextureResolver = shell.Assets.Resolver }));
        if (ctx.Check(written.Succeeded, "L1S1.rfl to a glTF of its brushes"))
        {
            string? back = await lvl.ConvertFileToRfgAsync(brushesGltf, toRfg);
            if (ctx.Check(back is not null && Clean(lvl), $"L1S1.gltf (brushes) to .rfg: {Report(lvl)}"))
            {
                var group = RfgReader.Read(File.ReadAllBytes(back!), Path.GetFileName(back!));
                ctx.Check(group.AllBrushes.Count() == directBrushes, $"... with the same brushes as the direct conversion ({group.AllBrushes.Count()} and {directBrushes})");
            }
        }
    }

    [SelfTest("integration.mesh-chain")]
    public static async Task MeshChain(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var lvl = shell.Modules.OfType<LvlModule>().First();
        using var chain = new Chain(shell, "mesh");
        var v3m = Cairn.Rfa.Formats.Legacy.LegacyMeshSupport.Convert(Cairn.Rfa.Formats.Legacy.SyntheticExporterMesh.SampleStatic(), "crate.v3d").Bytes;
        string v3mPath = Path.Combine(chain.Folder, "crate.v3m");
        File.WriteAllBytes(v3mPath, v3m);
        int triangles = V3dReader.Read(v3m, "crate.v3m").Submeshes.Sum(s => s.Lods.Length == 0 ? 0 : s.Lods[0].Batches.Sum(b => b.Triangles.Length));
        if (!ctx.Check(shell.OpenFile(v3mPath) && shell.ActiveDocument is { } tab, "crate.v3m opens in the meshes module")) return;
        chain.Opened.Add(shell.ActiveDocument!);
        await ctx.SettleAsync();
        var targets = shell.ConversionsForActive.Select(c => $"{c.Module.Id}:{c.Target.Extension}").ToList();
        ctx.Check(targets.Contains("rfa:.obj") && targets.Contains("lvl:.rfg"), "a mesh tab lists OBJ and a group file: " + string.Join(", ", targets));

        // .v3m -> .obj -> .v3m
        string? obj = await shell.ConvertActiveAsync(shell.ConversionsForActive.First(c => c.Module.Id == "rfa" && c.Target.Extension == ".obj"), interactive: false, outputFolder: chain.Out("obj"));
        if (!ctx.Check(obj is not null && File.Exists(obj) && File.Exists(Path.ChangeExtension(obj, ".mtl")), $"crate.v3m to .obj: {obj}")) return;
        var toV3m = Choice(shell, "crate.obj", "rfa", ".v3m");
        string? again = toV3m is null ? null : await AssetConversions.RunAsync(shell, toV3m,
            new AssetConversionRequest("crate.obj", File.ReadAllBytes(obj!), ".v3m") { FilePath = obj, Interactive = false, OutputFolder = chain.Out("obj-v3m") });
        if (ctx.Check(again is not null && File.Exists(again), $"crate.obj back to .v3m: {again}"))
        {
            int back = V3dReader.ReadFile(again!).Submeshes.Sum(s => s.Lods.Length == 0 ? 0 : s.Lods[0].Batches.Sum(b => b.Triangles.Length));
            ctx.Check(back == triangles, $"... with the same triangles ({back} and {triangles})");
        }
        if (shell.ActiveDocument is { } now && !chain.Opened.Contains(now)) chain.Opened.Add(now);

        // .v3m -> .rfg from the tab
        shell.Activate(chain.Opened[0]);
        await ctx.SettleAsync();
        string? rfg = await shell.ConvertActiveAsync(shell.ConversionsForActive.First(c => c.Module.Id == "lvl" && c.Target.Extension == ".rfg"), interactive: false, outputFolder: chain.Out("v3m-rfg"));
        if (ctx.Check(rfg is not null && File.Exists(rfg) && Clean(lvl), $"crate.v3m to .rfg: {Report(lvl)}"))
            ctx.Check(RfgReader.Read(File.ReadAllBytes(rfg!), "crate.rfg").AllBrushes.Any(), "... which reads back with its brush");

        // .obj -> .rfg (the .mtl beside it) and a mesh glTF -> .rfg, from files on disk
        var choice = new LvlConvertChoice(new LvlConvertOptions { Target = LvlConvertTarget.Rfg }, LvlOutputPlace.Folder, chain.Out("file-rfg"), false) { Import = new RfgImportOptions { BrushKind = RfgBrushKind.Detail } };
        string? fromObj = await lvl.ConvertFileToRfgAsync(obj!, choice);
        if (ctx.Check(fromObj is not null && Clean(lvl), $"crate.obj to .rfg: {Report(lvl)}"))
            ctx.Check(RfgReader.Read(File.ReadAllBytes(fromObj!), "crate.rfg").AllBrushes.All(b => (b.SolidFlags & Cairn.Lvl.Model.RfSolidFlags.Detail) != 0), "... as detail brushes, as chosen");
        shell.Activate(chain.Opened[0]);
        await ctx.SettleAsync();
        string? gltf = await shell.ConvertActiveAsync(shell.ConversionsForActive.First(c => c.Module.Id == "rfa" && c.Target.Extension == ".gltf"), interactive: false, outputFolder: chain.Out("gltf"));
        if (ctx.Check(gltf is not null && File.Exists(gltf), $"crate.v3m to .gltf: {gltf}"))
        {
            string? fromGltf = await lvl.ConvertFileToRfgAsync(gltf!, choice with { Import = new RfgImportOptions() });
            ctx.Check(fromGltf is not null && Clean(lvl) && RfgReader.Read(File.ReadAllBytes(fromGltf!), "crate.rfg").AllBrushes.Any(), $"crate.gltf to .rfg: {Report(lvl)}");
        }
        ctx.Check(Choice(shell, "scene.glb", "lvl", ".rfg") is not null && Choice(shell, "guard.v3c", "lvl", ".rfg") is not null && Choice(shell, "box.v3d", "lvl", ".rfg") is null,
            "glTF binaries and character meshes list a group file; exporter meshes do not");
    }
}
#endif
