using System.Windows;
using Cairn.Rfa.Linting;
using Cairn.Ui.Diagnostics;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Interchange;
using Cairn.Vfx.Linting;
using Cairn.Vfx.Ui.Dialogs;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Library;
using Cairn.Vfx.Ui.Problems;
using Cairn.Workspace;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Self-tests and dialog captures for creation, import/export, library and problems.</summary>
public static class VfxCreationSelfTests
{
    private static string[] Corpus =>
        LocalPaths.Corpus is { } c && Directory.Exists(c) ? [.. Directory.GetFiles(c, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)] : [];

    private static bool Clean(VfxFile file, out string why)
    {
        var back = VfxReader.Read(VfxWriter.Write(file), "test.vfx");
        var errors = VfxLinter.Lint(back).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        why = string.Join("; ", errors.Select(e => $"{e.Code} {e.Message}"));
        return errors.Count == 0 && back.Sections.Length == file.Sections.Length;
    }

    private static VfxDocument NewDoc(SelfTestContext ctx, VfxFile file) =>
        new(ctx.Shell, new VfxKind { Shell = ctx.Shell }, file, "Test.vfx", null);

    [SelfTest("VFX: every Add item and template writes, re-reads and lints clean, as one undo step")]
    public static void AddItems(SelfTestContext ctx)
    {
        foreach (var kind in Enum.GetValues<VfxPrimitiveKind>())
        {
            var doc = NewDoc(ctx, VfxBuilder.NewFile());
            bool changed = VfxModule.Edit(doc, "Add primitive", f => VfxCreation.AddPrimitive(f, new(kind, "")));
            ctx.Check(changed && doc.Selection.Primary >= 0 && doc.Current.Sections[doc.Selection.Primary] is VfxMesh, $"{kind}: added and selected");
            ctx.Check(Clean(doc.Current, out var why), $"{kind}: round trip + lint ({why})");
            doc.Undo();
            ctx.Check(doc.Current.Sections.Length == 0, $"{kind}: one undo step removes mesh and material");
        }
        foreach (var kind in new[] { "Particle system", "Dummy", "Light", "Spacewarp" })
        {
            var (f, i) = VfxCreation.AddObject(VfxCreation.Template("Additive flash"), kind);
            ctx.Check(i >= 0 && Clean(f, out var why), $"{kind}: round trip + lint {(i < 0 ? "(not found)" : "")}");
        }
        foreach (var name in VfxCreation.TemplateNames)
            ctx.Check(Clean(VfxCreation.Template(name), out var why), $"template {name}: round trip + lint ({why})");
    }

    [SelfTest("VFX: glTF export -> import round trip and transplant into an open effect")]
    public static void GltfAndTransplant(SelfTestContext ctx)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cairn-vfx-gltf-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        var source = VfxCreation.Template("Scrolling beam");
        var path = Path.Combine(dir, "beam.gltf");
        VfxGltfExport.Save(source, path);
        ctx.Check(File.Exists(Path.ChangeExtension(path, ".bin")), "export writes .gltf + .bin");
        ctx.Check(VfxGltfImport.Probe(path) >= 0.99f, "probe recognises a VFX glTF");
        var back = VfxGltfImport.Import(path).File;
        ctx.Check(back.Sections.Length == source.Sections.Length && Clean(back, out _), "imported effect matches and lints clean");
        var doc = NewDoc(ctx, VfxCreation.Template("Ring shockwave"));
        int before = doc.Current.Sections.Length;
        VfxModule.Edit(doc, "Import glTF", f => { var r = VfxTransplant.CopyAll(back, f); return (r.File, r.Sections[0].TargetIndex); });
        ctx.Check(doc.Current.Sections.Length > before && Clean(doc.Current, out _), "transplant adds objects and lints clean");
        doc.Undo();
        ctx.Check(doc.Current.Sections.Length == before, "transplant is one undo step");
        foreach (var file in Corpus.Take(5))
        {
            var r = VfxTransplant.CopyAll(VfxReader.Read(File.ReadAllBytes(file), Path.GetFileName(file)), VfxBuilder.NewFile());
            ctx.Check(Clean(r.File, out var why), $"objects from {Path.GetFileName(file)} ({why})");
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    [SelfTest("VFX: problems list reports an injected error and its quick fix clears it")]
    public static void Problems(SelfTestContext ctx)
    {
        var file = VfxCreation.Template("Additive flash");
        file = VfxEdit.AddSection(file, VfxBuilder.Dummy("Twin"));
        file = VfxEdit.AddSection(file, VfxBuilder.Dummy("Twin2"));
        file = VfxEdit.Update<VfxDummy>(file, VfxEdit.FindByName(file, "Twin2"), d => d with { Name = "Twin" });
        var doc = NewDoc(ctx, file);
        var panel = VfxProblemsPanel.For(ctx.Shell, doc);
        var diag = VfxLinter.Lint(doc.Current).FirstOrDefault(d => d.Code == VfxRules.SectionName && d.QuickFixes.Count > 0);
        ctx.Check(diag is not null, "duplicate name reported with a quick fix");
        if (diag is null) return;
        ctx.Check(panel.ApplyFix(diag.QuickFixes[0]), "quick fix applied");
        ctx.Check(!VfxLinter.Lint(doc.Current).Any(d => d.Code == VfxRules.SectionName), "quick fix clears it");
        doc.Undo();
        ctx.Check(VfxLinter.Lint(doc.Current).Any(d => d.Code == VfxRules.SectionName), "quick fix is undoable");
    }

    [SelfTest("VFX: effects library lists the stock effects")]
    public static void Library(SelfTestContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.Shell.Settings.GameDirectory)) { ctx.Log("no game directory: skipped"); return; }
        var entries = VfxLibraryPanel.Scan(ctx.Shell.Assets.Resolver);
        ctx.Log($"library: {entries.Count} entries, {entries.Count(e => e.Shadowed)} shadowed, {entries.Count(e => e.UsedBy.Length > 0)} referenced by tables");
        ctx.Check(entries.Count >= 40, "library lists the stock effects");
    }

    private static string[] CorpusMeshes(string pattern) =>
        LocalPaths.Corpus is { } c && Directory.Exists(c) ? [.. Directory.GetFiles(c, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(3)] : [];

    [SelfTest("VFX: geometry from stock V3M and V3C meshes through the dialog adds meshes as one undo step")]
    public static void GeometryFromMesh(SelfTestContext ctx)
    {
        var files = CorpusMeshes("*.v3m").Concat(CorpusMeshes("*.v3c")).ToList();
        if (files.Count == 0) { ctx.Log("no mesh corpus: skipped"); return; }
        foreach (var path in files)
        {
            var mesh = Rfa.Formats.V3d.V3dReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            var form = VfxCreationDialogs.FromV3d(mesh, true, null, out var read);
            form.Refresh();
            var (options, add) = read();
            ctx.Check(form.CanAccept && add, $"{Path.GetFileName(path)}: dialog accepts and defaults to the open effect ({form.Summary})");
            string why = "";
            var doc = NewDoc(ctx, VfxCreation.Template("Additive flash"));
            int before = doc.Current.Sections.Length;
            VfxModule.Edit(doc, "Geometry from mesh", f => { var r = VfxFromV3d.Add(f, mesh, options); return (r.File, r.MeshSections.IsEmpty ? -1 : r.MeshSections[0]); });
            ctx.Check(doc.Current.Sections.Length > before && doc.Selection.Primary >= 0 && doc.Current.Sections[doc.Selection.Primary] is VfxMesh && Clean(doc.Current, out why), $"{Path.GetFileName(path)}: meshes added, selected, lint clean ({why})");
            doc.Undo();
            ctx.Check(doc.Current.Sections.Length == before, $"{Path.GetFileName(path)}: one undo step");
            form.Close();
        }
    }

    [SelfTest("VFX: objects from another effect with a time offset start later")]
    public static void ObjectsWithOffset(SelfTestContext ctx)
    {
        var source = Corpus.Select(f => VfxReader.Read(File.ReadAllBytes(f), Path.GetFileName(f))).FirstOrDefault(f => f.Sections.Any(s => s is VfxMesh))
            ?? VfxCreation.Template("Additive flash");
        var form = VfxCreationDialogs.Transplant(source, "source.vfx", null, out var read);
        var (sections, _) = read();
        ctx.Check(sections.Length > 0, "dialog checks every object by default");
        int mesh = Array.Find(sections, i => source.Sections[i] is VfxMesh);
        var r = VfxTransplant.Copy(source, VfxBuilder.NewFile(), [mesh], 10);
        var copied = r.Sections.First(s => s.SourceIndex == mesh).TargetIndex;
        var a = Timeline.VfxTimelineEdits.Range(source.Sections[mesh]); var b = Timeline.VfxTimelineEdits.Range(r.File.Sections[copied]);
        ctx.Check(a is not null && b is not null && Math.Abs(b.Value.Start - a.Value.Start - 10) < 0.01f, $"mesh starts 10 frames later ({a?.Start} -> {b?.Start})");
        ctx.Check(Clean(r.File, out var why), $"result lints clean ({why})");
        form.Close();
    }

    /// <summary>A one-triangle glTF without Cairn/REDUX extras whose node moves 2 units over one second.</summary>
    internal static string WritePlainGltf(string dir)
    {
        var bin = new byte[76];
        float[] pos = [0, 0, 0, 1, 0, 0, 0, 1, 0], times = [0, 1], moves = [0, 0, 0, 2, 0, 0];
        Buffer.BlockCopy(pos, 0, bin, 0, 36);
        Buffer.BlockCopy(new ushort[] { 0, 1, 2 }, 0, bin, 36, 6);
        Buffer.BlockCopy(times, 0, bin, 44, 8);
        Buffer.BlockCopy(moves, 0, bin, 52, 24);
        File.WriteAllBytes(Path.Combine(dir, "plain.bin"), bin);
        var path = Path.Combine(dir, "plain.gltf");
        File.WriteAllText(path, """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"Tri","mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],
             "buffers":[{"uri":"plain.bin","byteLength":76}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":6},{"buffer":0,"byteOffset":44,"byteLength":8},{"buffer":0,"byteOffset":52,"byteLength":24}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},{"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"},
               {"bufferView":2,"componentType":5126,"count":2,"type":"SCALAR","min":[0],"max":[1]},{"bufferView":3,"componentType":5126,"count":2,"type":"VEC3"}],
             "animations":[{"channels":[{"sampler":0,"target":{"node":0,"path":"translation"}}],"samplers":[{"input":2,"output":3,"interpolation":"LINEAR"}]}]}
            """);
        return path;
    }

    [SelfTest("VFX: a plain glTF with a node animation imports as an animated mesh; Cairn glTF adds to the open effect")]
    public static void GltfPlainAndAdd(SelfTestContext ctx)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairn-vfx-plain-" + Environment.ProcessId)).FullName;
        try
        {
            var result = VfxGltfImport.Import(WritePlainGltf(dir));
            var mesh = result.File.Sections.Select((s, i) => (s, i)).FirstOrDefault(p => p.s is VfxMesh);
            ctx.Check(mesh.s is VfxMesh, $"plain glTF gives a mesh ({string.Join("; ", result.Messages)})");
            var kind = mesh.s is null ? Timeline.VfxAnimKind.None : Timeline.VfxTimelineEdits.KindOf(mesh.s);
            ctx.Check(kind is Timeline.VfxAnimKind.Keyframed or Timeline.VfxAnimKind.PerFrame, $"node animation kept ({kind})");
            ctx.Check(result.File.Sections.OfType<VfxMaterial>().Count() == 1, "a mesh without a material gets one grey colour material");
            ctx.Check(Clean(result.File, out var why), $"plain import lints clean ({why})");

            var path = Path.Combine(dir, "flash.gltf");
            VfxGltfExport.Save(VfxCreation.Template("Additive flash"), path);
            var back = VfxGltfImport.Import(path);
            var form = VfxCreationDialogs.GltfImport(path, back.File, back.Messages, true, null, out var add);
            ctx.Check(add(), "import dialog defaults to adding into the open effect");
            form.Close();
            var doc = NewDoc(ctx, VfxCreation.Template("Ring shockwave"));
            int before = doc.Current.Sections.Length;
            VfxModule.Edit(doc, "Import glTF", f => { var r = VfxTransplant.CopyAll(back.File, f); return (r.File, r.Sections[0].TargetIndex); });
            ctx.Check(doc.Current.Sections.Length == before + back.File.Sections.Length && Clean(doc.Current, out why), $"added into the open effect ({why})");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [SelfTest("VFX: library facts for a stock effect used by vclip.tbl; opening an archive entry is read-only origin")]
    public static void LibraryFactsAndOpen(SelfTestContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.Shell.Settings.GameDirectory)) { ctx.Log("no game directory: skipped"); return; }
        var entries = VfxLibraryPanel.Scan(ctx.Shell.Assets.Resolver);
        var used = entries.FirstOrDefault(e => !e.Shadowed && e.Location.IsArchived && e.UsedBy.Contains("vclip.tbl", StringComparison.OrdinalIgnoreCase));
        ctx.Check(used is not null, "an archived effect is referenced by vclip.tbl");
        if (used is null) return;
        ctx.Log($"{used.Name}: {used.Line2}");
        ctx.Check(used.Version.StartsWith("0x") && used.Frames > 0 && used.Objects > 0, "version, frames and object count are read");
        ctx.Check(ctx.Shell.OpenLocation(used.Location), "opens");
        var doc = ctx.Shell.ActiveDocument as VfxDocument;
        ctx.Check(doc is { IsFromArchive: true, IsDirty: false, FilePath: null }, $"document has an archive origin and no path ({doc?.OriginText})");
        if (doc is not null) ctx.Shell.Close(doc);
    }

    private static Window Show(ScreenshotContext ctx, Window w) { w.Owner = ctx.MainWindow; w.Show(); return w; }

    private static string ShotDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairn-vfx-shots")).FullName;

    [ScreenshotDialog("vfx-import-gltf")]
    public static Window ImportGltfShot(ScreenshotContext ctx)
    {
        var path = Path.Combine(ShotDir(), "Ring_shockwave.gltf");
        VfxGltfExport.Save(VfxCreation.Template("Ring shockwave"), path);
        var r = VfxGltfImport.Import(path);
        return Show(ctx, VfxCreationDialogs.GltfImport(path, r.File, r.Messages, true, null, out _));
    }

    [ScreenshotDialog("vfx-import-plain-gltf")]
    public static Window ImportPlainGltfShot(ScreenshotContext ctx)
    {
        var path = WritePlainGltf(ShotDir());
        var r = VfxGltfImport.Import(path);
        return Show(ctx, VfxCreationDialogs.GltfImport(path, r.File, r.Messages, false, null, out _));
    }

    [ScreenshotDialog("vfx-from-mesh")]
    public static Window FromMeshShot(ScreenshotContext ctx)
    {
        var path = CorpusMeshes("*.v3c").FirstOrDefault() ?? throw new InvalidOperationException("No mesh corpus (LocalPaths.Corpus).");
        var mesh = Rfa.Formats.V3d.V3dReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
        return Show(ctx, VfxCreationDialogs.FromV3d(mesh, true, null, out _));
    }

    [ScreenshotDialog("vfx-pick-effect")]
    public static Window PickEffectShot(ScreenshotContext ctx) =>
        Show(ctx, VfxAssetPicker.Build(ctx.Shell, "Objects from another effect", "Effects (*.vfx)|*.vfx", [".vfx"], out _));

    [ScreenshotDialog("vfx-add-primitive")]
    public static Window AddPrimitive(ScreenshotContext ctx) => Show(ctx, VfxCreationDialogs.Primitive(VfxCreation.Template("Additive flash"), null, out _));

    [ScreenshotDialog("vfx-import-objects")]
    public static Window ImportObjects(ScreenshotContext ctx) => Show(ctx, VfxCreationDialogs.Transplant(VfxCreation.Template("Particle fountain"), "fountain.vfx", null, out _));

    [ScreenshotDialog("vfx-export-gltf")]
    public static Window ExportGltf(ScreenshotContext ctx) => Show(ctx, VfxCreationDialogs.GltfExport("Explosion", null, out _));
}
