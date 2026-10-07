using System.Globalization;
using System.Windows;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.GltfTools;
using Cairn.Rfa.Ui.Views.Dialogs.Gltf;
using Cairn.Rfa.Animation;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;

namespace Cairn.Rfa.Ui.Diagnostics;

/// <summary>
/// <c>--dialog</c> factories for the glTF dialogs and two diagnostic steps:
/// <list type="bullet">
/// <item><c>--dialog gltf-export</c> (active mesh or clip document; <c>--gltf-clips a,b,c</c> ticks clips,
/// <c>--gltf-export-to &lt;file&gt;</c> also runs the export so the result shows).</item>
/// <item><c>--dialog gltf-import-animation --gltf &lt;file&gt;</c> (<c>--gltf-target &lt;mesh&gt;</c> picks the
/// target; <c>--gltf-target self</c> builds the file's own mesh and uses it).</item>
/// <item><c>--dialog gltf-import-mesh --gltf &lt;file&gt;</c>, and <c>gltf-import-mesh-errors</c> (an in-memory
/// export of the active mesh with an over-long bone name, so the pre-flight shows an error).</item>
/// <item><c>--gltf-roundtrip &lt;folder&gt;</c>: exports the active mesh document with <c>--gltf-clips</c> (default
/// ult2_walk, ult2_stand, ult2_run) to .glb and .gltf in the folder and imports both back through the
/// dialogs' view-models, logging the comparison.</item>
/// <item><c>--gltf-report &lt;file&gt;</c>: imports the file as a mesh and (onto that mesh, or the active
/// document's when it cannot be built) as animations, logging what came out.</item>
/// </list>
/// </summary>
internal static class GltfScreens
{
    private static readonly string[] DefaultClips = ["ult2_walk", "ult2_stand", "ult2_run"];

    [ScreenshotDialog("gltf-export")]
    public static Window? Export(ScreenshotContext ctx)
    {
        if (ctx.Model.ActiveDocument is not { } document)
        {
            ctx.Log("gltf-export needs a mesh or clip document");
            return null;
        }
        GltfExportViewModel vm;
        try
        {
            vm = new GltfExportViewModel(ctx.Model, document);
        }
        catch (ArgumentException ex)
        {
            ctx.Log("gltf-export refused: " + ex.Message);
            return null;
        }
        TickClips(vm, ctx.Extra("gltf-clips")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? DefaultClips);
        if (ctx.Extra("gltf-export-to") is { } target)
        {
            vm.IsGlb = target.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
            vm.OutputPath = target;
            _ = RunExport(ctx, vm);
        }
        return GltfExportWindow.CreateForCapture(vm);
    }

    private static async Task RunExport(ScreenshotContext ctx, GltfExportViewModel vm)
    {
        var result = await vm.ExportAsync();
        ctx.Log(result is null
            ? $"gltf-export: no result ({vm.Error ?? vm.ProgressText})"
            : $"gltf-export: wrote {string.Join(", ", vm.LastFiles.Select(Path.GetFileName))}; {result.Warnings.Length} warnings");
    }

    [ScreenshotDialog("gltf-import-animation")]
    public static Window? ImportAnimation(ScreenshotContext ctx)
    {
        if (ctx.Extra("gltf") is not { } path)
        {
            ctx.Log("gltf-import-animation needs --gltf <file>");
            return null;
        }
        var vm = new GltfAnimationImportViewModel(ctx.Model, path);
        if (ctx.Extra("gltf-target") is { } target) _ = PickTarget(ctx, vm, path, target);
        return GltfAnimationImportWindow.CreateForCapture(vm);
    }

    private static async Task PickTarget(ScreenshotContext ctx, GltfAnimationImportViewModel vm, string path, string target)
    {
        using var busy = BusyTracker.Begin("gltf target for capture");
        await vm.Ready;
        if (string.Equals(target, "self", StringComparison.OrdinalIgnoreCase))
        {
            var doc = await GltfText.ReadAsync(path);
            var built = await Task.Run(() => GltfMeshImport.Import(doc, null, Path.GetFileName(path)));
            if (built.Mesh is { } mesh) vm.UseTarget(mesh, Path.GetFileNameWithoutExtension(path) + ".v3c", Path.GetDirectoryName(Path.GetFullPath(path)));
            else ctx.Log("gltf-target self: the file's mesh could not be built");
        }
        else if (!vm.SelectTarget(target)) ctx.Log($"gltf-target: '{target}' is not offered");
    }

    [ScreenshotDialog("gltf-import-mesh")]
    public static Window? ImportMesh(ScreenshotContext ctx)
    {
        if (ctx.Extra("gltf") is not { } path)
        {
            ctx.Log("gltf-import-mesh needs --gltf <file>");
            return null;
        }
        return GltfMeshImportWindow.CreateForCapture(new GltfMeshImportViewModel(ctx.Model, path));
    }

    [ScreenshotDialog("gltf-import-mesh-errors")]
    public static Window? ImportMeshErrors(ScreenshotContext ctx)
    {
        var mesh = ActiveMesh(ctx.Model);
        if (mesh is null)
        {
            ctx.Log("gltf-import-mesh-errors needs a mesh document (or a clip with a preview mesh)");
            return null;
        }
        var doc = ErrorDocument(mesh);
        return GltfMeshImportWindow.CreateForCapture(new GltfMeshImportViewModel(ctx.Model, "pre-flight-errors.gltf", doc));
    }

    /// <summary>The active document's mesh (a mesh tab, or a clip tab's preview mesh), or null.</summary>
    internal static V3dFile? ActiveMesh(RfaWorkspace model) => model.ActiveDocument switch
    {
        MeshDocumentViewModel m => m.Current,
        ClipDocumentViewModel c => c.PreviewMesh,
        _ => null,
    };

    /// <summary>
    /// An in-memory export of <paramref name="mesh"/> whose first bone is renamed to 31 characters (8 more than
    /// a bone name holds), so the mesh pre-flight reports MI002 as an error and blocks Import.
    /// </summary>
    internal static GltfDocument ErrorDocument(V3dFile mesh)
    {
        var doc = GltfExport.Export(mesh, null, new GltfExportOptions { TextureResolver = null }).Document;
        var bone = doc.Nodes.FirstOrDefault(n => GltfExtras.TryGetString(n.Extras, GltfExtras.Type, out var t) && t == "bone") ?? doc.Nodes.FirstOrDefault();
        if (bone is not null) bone.Name = GltfSpace.BoneNodeName("a_bone_name_far_too_long_for_rf", 0);
        return doc;
    }

    /// <summary>Ticks the first row of each named clip (library or open tab), unticks the rest (the fixed row stays).</summary>
    internal static int TickClips(GltfExportViewModel vm, IReadOnlyCollection<string> names)
    {
        int ticked = 0;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in vm.Clips.All)
        {
            if (row.IsFixed) continue;
            bool want = names.Contains(row.Name, StringComparer.OrdinalIgnoreCase) && done.Add(row.Name);
            row.IsChecked = want;
            if (want) ticked++;
        }
        return ticked;
    }

    // ── Steps ───────────────────────────────────────────────────────────────

    /// <summary><c>--gltf-roundtrip &lt;folder&gt;</c>: export the active mesh with clips, import both formats back, log.</summary>
    [ScreenshotStep(700)]
    public static async Task RoundTripStep(ScreenshotContext ctx)
    {
        if (ctx.Extra("gltf-roundtrip") is not { } folder) return;
        if (ctx.Model.ActiveDocument is not MeshDocumentViewModel meshDoc)
        {
            ctx.Log("--gltf-roundtrip needs an active mesh document");
            return;
        }
        var names = ctx.Extra("gltf-clips")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? DefaultClips;
        Directory.CreateDirectory(folder);
        await RoundTripAsync(ctx.Model, meshDoc, folder, names, ctx.Log, (ok, what) => ctx.Log((ok ? "roundtrip ok: " : "roundtrip FAILED: ") + what), closeImports: true);
    }

    /// <summary>
    /// Exports <paramref name="meshDoc"/> with the named clips to .glb and .gltf in <paramref name="folder"/>
    /// through <see cref="GltfExportViewModel"/>, then imports each back through the import view-models:
    /// every animation must come back byte-identical to the clip exported (key extras), the mesh with the
    /// same bones, spheres and prop points and no pre-flight errors; Import must open new unsaved tabs.
    /// Leaves the active document as it found it.
    /// </summary>
    internal static async Task RoundTripAsync(RfaWorkspace model, MeshDocumentViewModel meshDoc, string folder, IReadOnlyCollection<string> clipNames,
        Action<string> log, Action<bool, string> check, bool closeImports)
    {
        var export = new GltfExportViewModel(model, meshDoc);
        int ticked = TickClips(export, clipNames);
        if (ticked < 3)
        {
            // Fall back to the first compatible library clips (tables first).
            foreach (var row in export.Clips.All.Where(r => !r.IsChecked).Take(3 - ticked)) row.IsChecked = true;
        }
        var rows = export.Clips.All.Where(r => r.IsChecked).ToList();
        check(rows.Count >= 1, $"export: {rows.Count} clips ticked ({string.Join(", ", rows.Select(r => r.Name))})");
        var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) expected[row.Name] = RfaWriter.Write(await row.Load(CancellationToken.None));

        var source = meshDoc.Current;
        var sourceSkeleton = Skeleton.FromFile(source);
        int spheres = source.CollisionSpheres.Count();
        int props = source.Submeshes.FirstOrDefault()?.Lods.FirstOrDefault()?.PropPoints.Length ?? 0;
        string stem = Path.GetFileNameWithoutExtension(meshDoc.DisplayName) + "_roundtrip";

        foreach (string extension in new[] { ".glb", ".gltf" })
        {
            string path = Path.Combine(folder, stem + extension);
            export.IsGlb = extension == ".glb";
            export.OutputPath = path;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await export.ExportAsync();
            check(result is not null && export.LastFiles.All(File.Exists),
                $"export {extension}: {(result is null ? export.Error ?? export.ProgressText : $"{export.LastFiles.Count} files ({string.Join(", ", export.LastFiles.Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length:N0} B").Take(4))}{(export.LastFiles.Count > 4 ? ", …" : "")}), {result.Warnings.Length} warnings, {result.MissingTextures.Length} missing textures, {result.BakedRotationTracks} baked tracks, {clock.ElapsedMilliseconds} ms")}");
            if (result is null) continue;
            foreach (string w in result.Warnings) log($"export {extension} warning: {w}");

            // Animations back.
            model.ActiveDocument = meshDoc;
            var anim = new GltfAnimationImportViewModel(model, path);
            bool settled = await anim.SettleAsync();
            check(settled && anim.LoadError is null && anim.Animations.Count == rows.Count,
                $"import animation {extension}: {anim.Animations.Count} animations read ({anim.SourceSummary})");
            check(anim.TargetSkeleton?.Count == sourceSkeleton.Count && anim.TargetText.StartsWith(meshDoc.DisplayName, StringComparison.OrdinalIgnoreCase),
                $"import animation {extension}: the default target is the active mesh ({anim.TargetText})");
            check(anim.BoneMap.Map is not null && !anim.BoneMap.HasErrors, $"import animation {extension}: bone map {anim.BoneMap.Summary}");
            var modes = anim.ReportRows.GroupBy(r => r.ModeText).Select(g => $"{g.Count()} {g.Key}");
            check(anim.ReportRows.Count == sourceSkeleton.Count && anim.ReportRows.All(r => r.Mode == GltfBoneImportMode.Restored),
                $"import animation {extension}: preview report {string.Join(", ", modes)}");
            var docs = await anim.ImportAsync();
            check(docs.Count == rows.Count, $"import animation {extension}: Import opened {docs.Count} new clip tabs");
            int identical = 0;
            foreach (var doc in docs)
            {
                string name = Path.GetFileNameWithoutExtension(doc.DisplayName);
                byte[] bytes = RfaWriter.Write(doc.Current);
                bool same = expected.TryGetValue(name, out var want) && bytes.AsSpan().SequenceEqual(want);
                if (same) identical++;
                check(same, $"import animation {extension}: {name} comes back byte-identical ({bytes.Length:N0} bytes, {doc.Current.Duration} ticks, {doc.Current.Bones.Sum(b => b.RotationKeys.Length):N0} rotation keys)");
                check(doc.IsDirty && doc.FilePath is null && doc.PreviewMesh is not null && doc.Scene.MeshName == meshDoc.DisplayName,
                    $"import animation {extension}: {doc.DisplayName} is a new unsaved tab previewed on {doc.Scene.MeshName}");
                if (closeImports) model.CloseDiscarding(doc);
            }
            log($"roundtrip {extension}: {identical} of {expected.Count} clips byte-identical");
            model.ActiveDocument = meshDoc;

            // Mesh back.
            var meshVm = new GltfMeshImportViewModel(model, path);
            await meshVm.SettleAsync();
            var built = meshVm.Result?.Mesh;
            int builtBones = built is null ? -1 : Skeleton.FromFile(built).Count;
            int builtSpheres = built?.CollisionSpheres.Count() ?? -1;
            int builtProps = built?.Submeshes.FirstOrDefault()?.Lods.FirstOrDefault()?.PropPoints.Length ?? -1;
            check(built is not null && !meshVm.HasErrors && builtBones == sourceSkeleton.Count && builtSpheres == spheres && builtProps == props,
                $"import mesh {extension}: {builtBones} bones (want {sourceSkeleton.Count}), {builtSpheres} spheres (want {spheres}), {builtProps} prop points (want {props}); {meshVm.IssuesSummary}");
            foreach (var issue in meshVm.Issues) log($"import mesh {extension}: {issue.SeverityText} {issue.Code}: {issue.Message}");
            foreach (string line in meshVm.SummaryLines) log($"import mesh {extension}: {line}");
            var newMesh = meshVm.Import();
            check(newMesh is not null && newMesh.IsDirty && newMesh.FilePath is null && newMesh.Extension == ".v3c",
                $"import mesh {extension}: Import opened {newMesh?.DisplayName ?? "nothing"} as a new unsaved tab");
            if (newMesh is not null && closeImports) model.CloseDiscarding(newMesh);
            model.ActiveDocument = meshDoc;
        }
    }

    /// <summary><c>--gltf-report &lt;file&gt;</c>: import a file as a mesh and as animations, logging what came out.</summary>
    [ScreenshotStep(710)]
    public static async Task ReportStep(ScreenshotContext ctx)
    {
        if (ctx.Extra("gltf-report") is not { } path) return;
        var model = ctx.Model;
        var meshVm = new GltfMeshImportViewModel(model, path);
        await meshVm.SettleAsync();
        ctx.Log($"report mesh: {meshVm.SourceSummary}");
        ctx.Log($"report mesh: {meshVm.IssuesSummary}; can import: {meshVm.CanImport}");
        foreach (string line in meshVm.SummaryLines) ctx.Log("report mesh: " + line);
        foreach (var issue in meshVm.Issues) ctx.Log($"report mesh: {issue.SeverityText} {issue.Code}: {issue.Message}");
        var built = meshVm.Result?.Mesh;
        meshVm.End();

        var anim = new GltfAnimationImportViewModel(model, path);
        await anim.Ready;
        if (built is not null) anim.UseTarget(built, Path.GetFileNameWithoutExtension(path) + ".v3c");
        await anim.SettleAsync();
        ctx.Log($"report animation: {anim.SourceSummary}; target {anim.TargetText}; bone map {anim.BoneMap.Summary}");
        foreach (var row in anim.Animations)
        {
            anim.SelectedAnimation = row;
            await Task.Delay(50);
            await anim.SettleAsync();
            ctx.Log($"report animation {row.Name} ({row.Note}): {anim.ReportSummary}{(anim.PreviewMessage is { } m ? " — " + m : "")}");
            if (anim.Imported is { } imported)
            {
                var c = imported.Clip;
                ctx.Log(string.Format(CultureInfo.InvariantCulture, "report animation {0}: start {1}, end {2}, duration {3} ticks, version {4}, ramps {5}/{6}, {7} rotation keys, {8} position keys",
                    row.Name, c.StartTime, c.EndTime, c.Duration, c.Version, c.RampIn, c.RampOut, c.Bones.Sum(b => b.RotationKeys.Length), c.Bones.Sum(b => b.PositionKeys.Length)));
            }
            foreach (string w in anim.Warnings) ctx.Log($"report animation {row.Name} note: {w}");
        }
        anim.End();
    }
}
