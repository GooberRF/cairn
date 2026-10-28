using System.Diagnostics;
using System.Windows;
using Cairn.Formats.Imaging;
using Cairn.Formats.Vpp;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Ui.Dialogs;
using Cairn.Lvl.Ui.Documents;
using Cairn.Lvl.Ui.Preview;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Workspace;

namespace Cairn.Lvl.Ui.Diagnostics;

/// <summary>Self-tests of the levels and groups module (run with <c>Cairn.exe --selftest-only lvl.*</c>).</summary>
internal static class LvlSelfTests
{
    private static LvlModule? Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<LvlModule>().FirstOrDefault();

    private static string TempFolder(string what)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"cairn-lvl-{what}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Waits for a level tab to be read and its preview (and textures) built.</summary>
    private static async Task<LvlSceneView?> SceneOf(SelfTestContext ctx, LvlDocument doc)
    {
        await doc.Loading;
        await ctx.SettleAsync();
        if (LvlModule.ViewOf(doc) is not { } view) return null;
        await view.Scene.Building;
        await ctx.SettleAsync();
        return view.Scene;
    }

    /// <summary>A stock level from the game folder's packfiles (bytes and name), or null when there is no game folder.</summary>
    private static (byte[] Bytes, string Name)? StockLevel(string name = "L1S1.rfl")
    {
        if (LocalPaths.GameDirectory is not { } game || !Directory.Exists(game)) return null;
        foreach (var vpp in new[] { "levels1.vpp", "levelsm.vpp", "levels2.vpp" })
        {
            string path = Path.Combine(game, vpp);
            if (!File.Exists(path)) continue;
            var archive = VppArchive.Open(path);
            if (archive.TryGetEntry(name, out var entry)) return (archive.ReadEntry(entry), entry.Name);
        }
        return null;
    }

    [SelfTest("lvl.synthetic")]
    public static async Task Synthetic(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        string folder = TempFolder("open");
        try
        {
            string path = Path.Combine(folder, "synthetic.rfl");
            File.WriteAllBytes(path, LvlSyntheticLevel.Bytes());
            if (!ctx.Check(ctx.Shell.OpenFile(path) && ctx.Shell.ActiveDocument is LvlDocument, "a synthetic .rfl opens in a level tab")) return;
            var doc = (LvlDocument)ctx.Shell.ActiveDocument!;
            var scene = await SceneOf(ctx, doc);
            if (!ctx.Check(scene?.Scene is not null && doc.Source?.Level is not null, $"the level is read and its preview built ({doc.LoadError ?? scene?.Message ?? "ok"})")) return;
            var level = doc.Source!.Level!;
            ctx.Check(level.Problems.Length == 0, "read without problems: " + string.Join("; ", level.Problems));
            ctx.Check(doc.IsReadOnly && !((IDocument)doc).CanSave && !doc.IsDirty && !ctx.Shell.Save(doc), "read only: Save and Save As are off");
            ctx.Check(doc.StatusItems.Count >= 2, "status bar: " + string.Join(" | ", doc.StatusItems.Select(s => s.Text)));
            ctx.Check(scene!.Scene!.Triangles == LvlSyntheticLevel.DefaultTriangles, $"compiled geometry without portal and invisible faces: {scene.Scene.Triangles} triangles (want {LvlSyntheticLevel.DefaultTriangles})");
            ctx.Check(scene.Scene.Batches.All(b => b.Mesh.IsFrozen), "every batch mesh is frozen (built off the UI thread)");
            ctx.Check(scene.Markers.Count == 4, $"markers: 2 lights, 1 item, the player start ({scene.Markers.Count})");
            ctx.Check(scene.MarkersDrawn == 4, $"the markers are drawn in the scene ({scene.MarkersDrawn})");
            scene.MarkerKinds = LvlMarkerKinds.Lights;
            await ctx.SettleAsync();
            ctx.Check(scene.MarkersDrawn == 2, $"showing only lights draws 2 markers ({scene.MarkersDrawn})");
            scene.MarkerKinds = LvlMarkerKinds.Default;

            // facts: the packfile module's level rows plus the full reader's
            var view = LvlModule.ViewOf(doc)!;
            await view.Facts.Pending;
            string Fact(string label) => view.Facts.Rows.FirstOrDefault(r => r.Label == label)?.Value ?? "";
            ctx.Check(Fact("Name").Length > 0 && Fact("Format version").Length > 0 && Fact("Compiled triangles") == "12", $"facts: name '{Fact("Name")}', version '{Fact("Format version")}', compiled triangles '{Fact("Compiled triangles")}'");
            ctx.Check(Fact("Brushes").StartsWith('1') && Fact("Lights").Length > 0, $"facts: brushes '{Fact("Brushes")}', lights '{Fact("Lights")}'");

            // face classes
            scene.SetSceneSettings(scene.SceneSettings.With(RfFaceClass.Sky, show: false));
            await scene.Building;
            ctx.Check(scene.Scene?.Triangles == LvlSyntheticLevel.DefaultTriangles - 2, $"hiding sky faces leaves {scene.Scene?.Triangles} triangles");
            scene.SetSceneSettings(scene.SceneSettings.With(RfFaceClass.Sky, show: true).With(RfFaceClass.Portal, show: true).With(RfFaceClass.Invisible, show: true));
            await scene.Building;
            ctx.Check(scene.Scene?.Triangles == 12, $"showing every class draws the whole room ({scene.Scene?.Triangles} triangles)");
            scene.SetSceneSettings(scene.SceneSettings.With(RfFaceClass.Normal, show: false));
            await scene.Building;
            ctx.Check(scene.Scene?.Triangles == 10, $"hiding ordinary faces leaves the classed ones ({scene.Scene?.Triangles} triangles)");
            ctx.Check(scene.Scene!.Batches.Any(b => b.Kind == LvlBatchKind.Alpha) && scene.Scene.Batches.Any(b => b.Kind == LvlBatchKind.FullBright),
                "alpha and full-bright faces get their own batches");
            scene.SetSceneSettings(LvlSceneSettings.Default);
            await scene.Building;

            // brushes
            scene.SetSceneSettings(scene.SceneSettings with { Compiled = false });
            await scene.Building;
            ctx.Check(scene.Scene?.Triangles == 12 && scene.Scene.Min.X > 10, $"brushes: the solid brush, placed by its transform ({scene.Scene?.Triangles} triangles, min x {scene.Scene?.Min.X:0.##})");
            scene.SetSceneSettings(LvlSceneSettings.Default);
            await scene.Building;

            // lighting and sides rebuild materials without a rebuild of the meshes
            var before = scene.Scene;
            foreach (var mode in new[] { LvlLighting.LevelAmbient, LvlLighting.Shaded, LvlLighting.FullBright }) scene.Lighting = mode;
            scene.BothSides = true;
            scene.BothSides = false;
            ctx.Check(ReferenceEquals(before, scene.Scene), "lighting and both-sides change the materials only");

            // picking from inside the room: the camera at the centre looking at the +X wall (an ordinary face)
            scene.FlyMode = true;
            scene.Fly.PlaceEye(Vector3.Zero, -90, 0);
            await ctx.SettleAsync();
            scene.UpdateLayout();
            scene.PickAt(new Point(scene.Surface.ActualWidth / 2, scene.Surface.ActualHeight / 2));
            string picked = scene.SelectionText ?? "";
            ctx.Check(picked.Contains("wall.tga", StringComparison.Ordinal) && picked.Contains("id 100", StringComparison.Ordinal), "a click on the wall in front shows its face: " + picked.Replace(Environment.NewLine, " | "));
            // fly: W moves along the view
            var eye = scene.Surface.Camera.Eye;
            scene.Fly.Move(1, 0, 0);
            var moved = scene.Surface.Camera.Eye - eye;
            ctx.Check(Vector3.Distance(moved, scene.Surface.Camera.Forward) < 1e-3f, $"the fly camera moves along the view ({moved})");
            scene.Fly.Look(90, 0);
            ctx.Check(Vector3.Distance(scene.Surface.Camera.Eye, eye + moved) < 1e-3f, "looking around keeps the eye where it is");
            scene.FlyMode = false;
            scene.ClearSelection();
            ctx.Check(scene.SelectionText is null, "the selection clears");

            // the size limit asks before building
            scene.TriangleLimit = 4;
            scene.SetSceneSettings(scene.SceneSettings.With(RfFaceClass.Portal, show: true));
            ctx.Check(scene.IsWaitingForConfirmation && scene.Scene is null && (scene.Message ?? "").Contains("triangles", StringComparison.Ordinal), "above the size limit the preview asks first: " + scene.Message);
            scene.BuildAnyway();
            await scene.Building;
            ctx.Check(scene.Scene?.Triangles == 10 && !scene.IsWaitingForConfirmation, "Build the preview builds it anyway");
            scene.TriangleLimit = LvlSettings.DefaultTriangleLimit;
            ctx.Shell.Close(doc);
            ctx.Check(!module.OpenDocuments.Contains(doc), "closing the tab forgets the document");

            // the settings page shows the values, and both kinds are offered for file associations
            var page = module.SettingsPages.OfType<LvlSettingsPage>().Single();
            page.Load();
            ctx.Check(page.Title == "Levels & groups" && int.TryParse(page.TriangleLimit.Text, System.Globalization.NumberStyles.Integer | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.CurrentCulture, out int limit) && limit == module.Settings.TriangleLimit,
                $"Settings > Levels & groups shows the triangle limit ({page.TriangleLimit.Text})");
            var kinds = ctx.Shell.Modules.SelectMany(m => m.DocumentKinds).ToList();
            ctx.Check(kinds.Any(k => k.Id == "lvl.level" && k.Extensions.Contains(".rfl") && k.AssociationDescription.Length > 0)
                && kinds.Any(k => k.Id == "lvl.group" && k.Extensions.Contains(".rfg") && k.AssociationDescription.Length > 0), "the .rfl and .rfg kinds are registered (file associations list them)");

            // a file that is not a level is refused at once
            string bad = Path.Combine(folder, "bad.rfl");
            File.WriteAllBytes(bad, [1, 2, 3, 4, 5, 6, 7, 8]);
            ctx.Check(!ctx.Shell.OpenFile(bad) && ctx.Shell.ActiveDocument is not LvlDocument { DisplayName: "bad.rfl" }, "a file without the level signature is refused");
        }
        finally { TryDelete(folder); }
    }

    [SelfTest("lvl.stock-level")]
    public static async Task StockLevelTest(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        if (StockLevel() is not { } stock) { ctx.Skip("no game folder with levels1.vpp (" + LocalPaths.HowToSet(LocalPaths.GameDirectoryVariable, "gameDirectory") + ")"); return; }
        var watch = Stopwatch.StartNew();
        var doc = (LvlDocument)module.LevelKind.OpenBytes(stock.Bytes, stock.Name, stock.Name + " in levels1.vpp (self-test)");
        ctx.Shell.AddDocument(doc);
        var scene = await SceneOf(ctx, doc);
        if (!ctx.Check(scene?.Scene is { IsEmpty: false }, $"{stock.Name}: preview built ({doc.LoadError ?? scene?.Message ?? "ok"}) in {watch.ElapsedMilliseconds} ms")) { ctx.Shell.Close(doc); return; }
        var built = scene!.Scene!;
        ctx.Check(built.Triangles > 1000 && built.Textures.Count > 10, $"{stock.Name}: {built.Triangles:N0} triangles, {built.Textures.Count} textures, {built.Batches.Count} batches");
        if (ctx.Shell.Assets.HasSources)
            ctx.Check(scene.TexturesLoaded >= built.Textures.Count * 3 / 4, $"{stock.Name}: textures found in the game data ({scene.TexturesLoaded} of {built.Textures.Count}; missing {string.Join(", ", scene.MissingTextures.Take(8))})");
        ctx.Check(scene.Markers.Any(m => m.Kind == LvlMarkerKinds.Lights) && scene.Markers.Any(m => m.Kind == LvlMarkerKinds.Spawns), $"{stock.Name}: {scene.Markers.Count} object markers with lights and the player start");

        // a click on a face seen from the camera (the middle of the view may be between tunnels) shows a face
        scene.MarkerKinds = LvlMarkerKinds.None;
        scene.UpdateLayout();
        var eye = scene.Surface.Camera.Eye;
        var aim = built.Targets.Select(t => (Target: t, Centre: t.Polygon.Corners.Aggregate(Vector3.Zero, (s, c) => s + c.Position) / t.Polygon.Corners.Length))
            .Where(t => Vector3.Dot(t.Target.Polygon.Normal, eye - t.Centre) > 0 && t.Target.Polygon.Normal.Y > 0.7f)
            .OrderBy(t => Vector3.Distance(t.Centre, (built.Min + built.Max) / 2)).Select(t => t.Centre).FirstOrDefault();
        if (scene.Surface.Project(aim, out var screen, out _))
        {
            scene.PickAt(screen);
            ctx.Check(scene.SelectionText is { } text && text.Contains("Texture:", StringComparison.Ordinal), $"a click on a floor at {screen.X:0},{screen.Y:0} shows a face: " + (scene.SelectionText ?? "nothing").Replace(Environment.NewLine, " | "));
        }
        else ctx.Check(false, "a floor face projects into the view");
        scene.MarkerKinds = LvlMarkerKinds.Default;

        int all = built.Triangles;
        scene.SetSceneSettings(scene.SceneSettings.With(RfFaceClass.Normal, show: false));
        await scene.Building;
        ctx.Check(scene.Scene is { } classed && classed.Triangles < all, $"hiding ordinary faces: {scene.Scene?.Triangles:N0} of {all:N0} triangles");
        scene.SetSceneSettings(LvlSceneSettings.Default with { Compiled = false });
        await scene.Building;
        ctx.Check(scene.Scene is { IsEmpty: false } brushes && brushes.Parts > 10, $"brushes: {scene.Scene?.Parts:N0} brushes, {scene.Scene?.Triangles:N0} triangles");
        scene.SetSceneSettings(LvlSceneSettings.Default);
        await scene.Building;
        ctx.Shell.Close(doc);
    }

    [SelfTest("lvl.rf2-level")]
    public static async Task Rf2Level(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        string? folder = LocalPaths.ReduxResearchData is { } data ? Path.Combine(data, "rf2_rfls") : null;
        string? path = folder is not null && File.Exists(Path.Combine(folder, "ctfc01.rfl")) ? Path.Combine(folder, "ctfc01.rfl") : null;
        if (path is null) { ctx.Skip("no Red Faction II levels (" + LocalPaths.HowToSet(LocalPaths.ReduxResearchDataVariable, "reduxResearchData") + ")"); return; }
        // read from disk only: the sample folder is never written
        var doc = (LvlDocument)module.LevelKind.OpenBytes(await File.ReadAllBytesAsync(path), "ctfc01.rfl", "ctfc01.rfl (self-test)");
        ctx.Shell.AddDocument(doc);
        await doc.Loading;
        if (doc.Source?.Level is not { IsRf2: true })
        {
            ctx.Check(doc.LoadError is not null, "a Red Faction II level this version cannot read says why: " + doc.LoadError);
            ctx.Shell.Close(doc);
            return;
        }
        // the textures are in the .peg beside the level: a preview of the folder's files finds them
        var siblings = AssetSiblings.FromMemory("rf2_rfls", Directory.EnumerateFiles(folder!, "ctfc01*.peg").Select(f => new KeyValuePair<string, byte[]>(Path.GetFileName(f), File.ReadAllBytes(f))));
        var preview = (LvlPreview)module.CreatePreview(doc.Bytes, "ctfc01.rfl", siblings)!;
        var host = new Window { Width = 640, Height = 420, Content = preview, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false, Left = -4000, Top = -4000 };
        host.Show();
        try
        {
            await preview.Loading;
            await ctx.SettleAsync();
            var scene = preview.View;
            ctx.Check(scene.Scene is { IsEmpty: false }, $"ctfc01.rfl: the Red Faction II level is drawn ({scene.Scene?.Triangles:N0} triangles)");
            ctx.Check(scene.TexturesLoaded >= (scene.Scene?.Textures.Count ?? 0) * 3 / 4, $"its textures come from its .peg ({scene.TexturesLoaded} of {scene.Scene?.Textures.Count}; missing {string.Join(", ", scene.MissingTextures.Take(6))})");
        }
        finally
        {
            host.Close();
            preview.Dispose();
        }
        var view = LvlModule.ViewOf(doc)!;
        await view.Facts.Pending;
        ctx.Check(view.Facts.Rows.Any(r => r.Label == "Loads in" && r.Value == "Red Faction II") && !view.Facts.Warnings.Any(w => w.Contains("only the header", StringComparison.Ordinal)),
            "facts: loads in Red Faction II, read in full");
        CheckRf2Facts(ctx, "ctfc01.rfl", view.Facts.Rows);
        ctx.Shell.Close(doc);
    }

    /// <summary>The rows every Red Faction II level's facts carry, as an RF1 level's do.</summary>
    private static void CheckRf2Facts(SelfTestContext ctx, string name, IReadOnlyList<AssetDetailRow> rows)
    {
        string? Value(string section, string label) => rows.FirstOrDefault(r => r.Section == section && r.Label == label)?.Value;
        ctx.Check(Value("Level", "Author") is { Length: > 0 } && Value("Level", "Editor's date") is { Length: > 0 } && Value("Level", "Mode") is not null,
            $"{name} facts: author, editor's date and mode ({Value("Level", "Author")}; {Value("Level", "Editor's date")}; {Value("Level", "Mode")})");
        ctx.Check(Value("Properties", "Fog") is { } fog && (fog == "none" || fog.StartsWith("colour ", StringComparison.Ordinal))
            && Value("Properties", "Ambient light") is not null && Value("Properties", "Sun colour (light bake)") is not null && Value("Properties", "Lightmap multiplier") is not null,
            $"{name} facts: fog ({Value("Properties", "Fog")}), ambient light, the sun and the lightmap multiplier");
        ctx.Check(Value("Statistics", "Rooms") is not null && Value("Statistics", "Faces") is not null && Value("Statistics", "Player start") == "yes",
            $"{name} facts: statistics ({Value("Statistics", "Rooms")} rooms, {Value("Statistics", "Faces")} faces)");
        var sections = rows.Where(r => r.Section == "Sections").ToList();
        ctx.Check(sections.Count >= 20 && sections.Any(r => r.Label == "Vertex lighting") && sections.All(r => !r.Flagged),
            $"{name} facts: {sections.Count} sections, none flagged ({string.Join(", ", sections.Take(5).Select(r => r.Label))}, ...)");
        ctx.Check(!rows.Any(r => r.Section == "Notes") && !rows.Any(r => r.Section == "References" && r.Flagged), $"{name} facts: no notes, no file called missing");
    }

    [SelfTest("lvl.rf2-level-entry")]
    public static async Task Rf2LevelEntry(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        string? toc = LocalPaths.Rf2GameDirectory is { } rf2 ? Path.Combine(rf2, "pc_media", "All_Levels" + Cairn.Formats.Rf2.Rf2TocGroup.Extension) : null;
        if (toc is null || !File.Exists(toc)) { ctx.Skip("no Red Faction II install (" + LocalPaths.HowToSet(LocalPaths.Rf2GameDirectoryVariable, "rf2GameDirectory") + ")"); return; }
        // the level and its own pack, read in place from the packed group (never written)
        var group = Cairn.Formats.Rf2.Rf2TocGroup.Open(toc);
        string? packfile = group.IsPacked ? group.FindPackfile(toc) : null;
        var levelEntry = group.Entries.FirstOrDefault(e => e.Name.Equals("dmc13.rfl", StringComparison.OrdinalIgnoreCase));
        var pegEntry = group.Entries.FirstOrDefault(e => e.Name.Equals("dmc13.peg", StringComparison.OrdinalIgnoreCase));
        if (!ctx.Check(packfile is not null && levelEntry is not null && pegEntry is not null, "All_Levels holds dmc13.rfl and dmc13.peg")) return;
        byte[] Read(Cairn.Formats.Rf2.Rf2TocEntry e)
        {
            using var f = File.OpenRead(packfile!);
            f.Position = e.Offset;
            var data = new byte[e.Size];
            f.ReadExactly(data);
            return data;
        }
        byte[] bytes = await Task.Run(() => Read(levelEntry!));
        var doc = (LvlDocument)module.LevelKind.OpenBytes(bytes, "dmc13.rfl", "All_Levels.toc_group > dmc13.rfl (self-test)");
        ctx.Shell.AddDocument(doc);
        try
        {
            await doc.Loading;
            if (!ctx.Check(doc.Source?.Level is { IsRf2: true }, $"dmc13.rfl opens as a Red Faction II level ({doc.LoadError ?? "read"})")) return;
            var view = LvlModule.ViewOf(doc)!;
            await view.Facts.Pending;
            CheckRf2Facts(ctx, "dmc13.rfl", view.Facts.Rows);

            // as an entry of its packfile: its textures are found in its own pack and the install's shared packs
            byte[] peg = await Task.Run(() => Read(pegEntry!));
            var siblings = AssetSiblings.FromMemory("All_Levels.toc_group", [new KeyValuePair<string, byte[]>(pegEntry!.Name, peg)]);
            string? install = LvlTexturePacks.Rf2InstallOf(toc);
            var packs = LvlTexturePacks.PacksFor("dmc13.rfl", null, siblings, install);
            var presence = LvlFacts.Rf2Presence(packs, install is null ? [] : LvlTexturePacks.SharedPacks(install).Select(p => p.Name));
            var source = doc.Source!;
            var details = await Task.Run(() => LvlFacts.Build(source, presence, CancellationToken.None));
            var refs = details.Rows.Where(r => r.Section == "References").ToList();
            int own = refs.Count(r => r.Value.EndsWith("in this packfile", StringComparison.Ordinal)), shared = refs.Count(r => r.Value.EndsWith("in the game data", StringComparison.Ordinal));
            ctx.Check(own > 0 && refs.All(r => !r.Flagged), $"dmc13.rfl in its packfile: {refs.Count - 1} files named, {own} textures in its own pack, {shared} in the install's shared packs, none called missing");
            CheckRf2Facts(ctx, "dmc13.rfl (packfile entry)", details.Rows);
        }
        finally
        {
            ctx.Shell.Close(doc);
        }
    }

    [SelfTest("lvl.group")]
    public static async Task Group(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        string? groups = LocalPaths.GameDirectory is { } game ? Path.Combine(game, "red", "groups") : null;
        string? file = groups is not null && Directory.Exists(groups)
            ? Directory.EnumerateFiles(groups, "*.rfg").Where(f => new FileInfo(f).Length is > 2000 and < 2_000_000).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
        if (file is null) { ctx.Skip("no editor groups in the game folder"); return; }
        var bytes = await File.ReadAllBytesAsync(file);
        var doc = (LvlDocument)module.GroupKind.OpenBytes(bytes, Path.GetFileName(file), Path.GetFileName(file) + " (self-test)");
        ctx.Shell.AddDocument(doc);
        var scene = await SceneOf(ctx, doc);
        ctx.Check(doc.Source?.GroupFile is not null && scene?.Scene is { IsEmpty: false }, $"{Path.GetFileName(file)}: group file read and drawn ({doc.LoadError ?? scene?.Message ?? $"{scene?.Scene?.Triangles:N0} triangles"})");
        ctx.Check(scene?.SceneSettings.Compiled == false, "a group file shows its brushes");
        var view = LvlModule.ViewOf(doc)!;
        await view.Facts.Pending;
        ctx.Check(view.Facts.Rows.Any(r => r.Section == "Group file" && r.Label == "Groups"), "group facts list the groups");
        ctx.Check(AssetConversions.TargetsFor(ctx.Shell, doc).Any(c => c.Target.Extension == ".glb") && AssetConversions.TargetsFor(ctx.Shell, doc).All(c => c.Target.Extension != ".rfg"),
            "a group file converts to glTF, OBJ and static meshes (not to another group file)");
        ctx.Shell.Close(doc);
    }

    [SelfTest("lvl.convert")]
    public static async Task Convert(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        string folder = TempFolder("convert");
        try
        {
            var bytes = LvlSyntheticLevel.Bytes();
            var doc = (LvlDocument)module.LevelKind.OpenBytes(bytes, "synthetic.rfl", "synthetic.rfl (self-test)");
            ctx.Shell.AddDocument(doc);
            await SceneOf(ctx, doc);
            var choices = AssetConversions.TargetsFor(ctx.Shell, doc);
            var lvl = choices.Where(c => c.Module is LvlModule).Select(c => c.Target.Extension).ToList();
            ctx.Check(lvl.Contains(".gltf") && lvl.Contains(".glb") && lvl.Contains(".obj") && lvl.Contains(".v3m"), "File › Convert to lists glTF, glTF binary, OBJ and static mesh: " + string.Join(", ", lvl));
            ctx.Check(choices.First(c => c.Target.Extension == ".obj").Target.Lossy && !choices.First(c => c.Target.Extension == ".glb").Target.Lossy, "OBJ is marked lossy, glTF is not");

            foreach (var (ext, expected) in new[] { (".glb", new[] { "synthetic.glb" }), (".gltf", new[] { "synthetic.gltf", "synthetic.bin" }), (".obj", new[] { "synthetic.obj", "synthetic.mtl" }), (".v3m", new[] { "synthetic.v3m" }) })
            {
                string into = Path.Combine(folder, ext.TrimStart('.'));
                var request = AssetConversions.RequestFor(ctx.Shell, doc, ext, interactive: false) with { OutputFolder = into };
                var main = await module.ConvertAsync(request);
                ctx.Check(main is not null && expected.All(f => File.Exists(Path.Combine(into, f))), $"{ext}: tab converted without a dialog ({main ?? "nothing"}; {string.Join(", ", Directory.Exists(into) ? Directory.GetFiles(into).Select(Path.GetFileName) : [])})");
                ctx.Check(module.LastOutcomes is [{ Report: { HasErrors: false } }], $"{ext}: report without errors ({module.LastOutcomes?.FirstOrDefault()?.Report?.Issues.Length ?? -1} entries)");
            }
            // the same name again gets a free name (Replace is off)
            var again = await module.ConvertAsync(AssetConversions.RequestFor(ctx.Shell, doc, ".glb", interactive: false) with { OutputFolder = Path.Combine(folder, "glb") });
            ctx.Check(again is not null && Path.GetFileName(again) == "synthetic (2).glb", "a taken name gets a free one: " + again);
            ctx.Shell.Close(doc);

            // a packfile entry (no tab): into the packfile through the request's add step
            var added = new List<(string Name, byte[] Bytes)>();
            var entryRequest = new AssetConversionRequest("entry.rfl", bytes, ".glb")
            {
                Interactive = false,
                ArchivePath = Path.Combine(folder, "maps.vpp"),
                ArchiveEntryNames = ["entry.rfl", "entry.glb"],
                AddToArchive = (label, files, replace) => { added.AddRange(files); return [.. files.Select(f => f.Name)]; },
                ReadSibling = _ => null,
            };
            var entryMain = await module.ConvertAsync(entryRequest);
            ctx.Check(entryMain == "entry_2.glb" && added.Count == 1 && added[0].Bytes.Length > 100, $"an entry converts into its packfile with a free name ({entryMain}; {added.Count} files)");

            // a batch from a packfile: into a folder, then into the packfile as one step
            var entries = new[] { new ArchiveBatchEntry("one.rfl", bytes.Length, () => bytes), new ArchiveBatchEntry("two.rfl", bytes.Length, () => bytes), new ArchiveBatchEntry("readme.txt", 3, () => [1, 2, 3]) };
            string batchFolder = Path.Combine(folder, "batch");
            var options = new LvlConvertOptions { Target = LvlConvertTarget.Obj };
            var summary = await module.ConvertAsync(new ArchiveBatchRequest("maps.vpp", folder, entries) { Interactive = false, Options = new LvlConvertChoice(options, LvlOutputPlace.Folder, batchFolder, false), AllEntries = entries });
            ctx.Check(summary is not null && File.Exists(Path.Combine(batchFolder, "one.obj")) && File.Exists(Path.Combine(batchFolder, "two.obj")), "Convert levels… writes each level into the folder: " + summary);
            int steps = 0;
            var packed = new List<string>();
            summary = await module.ConvertAsync(new ArchiveBatchRequest("maps.vpp", folder, entries)
            {
                Interactive = false,
                Options = new LvlConvertChoice(options with { Target = LvlConvertTarget.V3m }, LvlOutputPlace.IntoPackfile, null, false),
                AllEntries = entries,
                AddFiles = (label, files, replace) => { steps++; packed.AddRange(files.Select(f => f.Name)); return [.. files.Select(f => f.Name)]; },
            });
            ctx.Check(steps == 1 && packed.Order().SequenceEqual(["one.v3m", "two.v3m"]), $"Convert levels… adds every output to the packfile in one step ({steps} steps: {string.Join(", ", packed)})");
            ctx.Check(module.CommandText.Contains("levels", StringComparison.OrdinalIgnoreCase) && module.CanConvert("x.RFG") && !module.CanConvert("x.txt"), "the batch command takes levels and group files only");

            // the convert window starts at the chosen target and binds the options
            var window = new LvlConvertWindow(ctx.Shell.Dialogs, ["synthetic.rfl"], "maps.vpp", folder, module.Settings, LvlConvertTarget.Obj, rfgAvailable: false);
            ctx.Check(window.Options.Target == LvlConvertTarget.Obj && window.Options.ResolvedGeometry == LvlGeometryChoice.Compiled && window.Options.Keeps(RfFaceClass.Normal) && !window.Options.Keeps(RfFaceClass.Portal),
                "the Convert window starts at OBJ with compiled geometry and without portal faces");
            window.Select(LvlConvertTarget.V3m);
            ctx.Check(window.Options.Target == LvlConvertTarget.V3m && window.Current() is { Place: LvlOutputPlace.Folder or LvlOutputPlace.IntoPackfile }, "picking a target updates the options");
            window.Close();

            // a stock level converts with its textures, in bounded time
            if (StockLevel() is { } stock && ctx.Shell.Assets.HasSources)
            {
                var watch = Stopwatch.StartNew();
                string into = Path.Combine(folder, "stock");
                var result = await module.ConvertAsync(new AssetConversionRequest(stock.Name, stock.Bytes, ".glb") { Interactive = false, OutputFolder = into });
                var report = module.LastOutcomes?.FirstOrDefault()?.Report;
                ctx.Check(result is not null && report is { HasErrors: false } && report.Stats.Textures > 10,
                    $"{stock.Name} to .glb: {report?.Stats.Triangles:N0} triangles, {report?.Stats.Textures} textures, {report?.MissingTextures.Length} missing, {watch.ElapsedMilliseconds} ms");
            }
        }
        finally { TryDelete(folder); }
    }

    [ScreenshotDialog("lvl.convert")]
    public static Window? ConvertDialog(ScreenshotContext ctx) => ConvertWindow(ctx, LvlConvertTarget.Glb, ["L1S1.rfl"]);

    [ScreenshotDialog("lvl.convert-obj")]
    public static Window? ConvertDialogObj(ScreenshotContext ctx) => ConvertWindow(ctx, LvlConvertTarget.Obj, ["L1S1.rfl"]);

    [ScreenshotDialog("lvl.convert-batch")]
    public static Window? ConvertDialogBatch(ScreenshotContext ctx) => ConvertWindow(ctx, LvlConvertTarget.V3m, ["L1S1.rfl", "L1S2.rfl", "L1S3.rfl", "elevator.rfg"]);

    [ScreenshotDialog("lvl.convert-rfg")]
    public static Window? ConvertDialogRfg(ScreenshotContext ctx) => ConvertWindow(ctx, LvlConvertTarget.Rfg, ["L1S1.rfl"]);

    [ScreenshotDialog("lvl.convert-rf2")]
    public static Window? ConvertDialogRf2(ScreenshotContext ctx) => ConvertWindow(ctx, LvlConvertTarget.Rfg, ["dmc13.rfl"], packfile: null, anyRf2: true);

    [ScreenshotDialog("lvl.convert-rf2-details")]
    public static Window? ConvertDialogRf2Details(ScreenshotContext ctx)
    {
        var window = (LvlConvertWindow?)ConvertWindow(ctx, LvlConvertTarget.V3m, ["dmc13.rfl"], packfile: null, anyRf2: true);
        window?.ShowRf2Details();
        return window;
    }

    [ScreenshotDialog("lvl.convert-mesh")]
    public static Window? ConvertDialogMesh(ScreenshotContext ctx) => ConvertWindow(ctx, LvlConvertTarget.Rfg, ["crate.v3m"], packfile: null);

    [ScreenshotDialog("lvl.convert-gltf")]
    public static Window? ConvertDialogGltf(ScreenshotContext ctx) => ConvertWindow(ctx, LvlConvertTarget.Rfg, ["L1S1.gltf"], packfile: null);

    private static Window? ConvertWindow(ScreenshotContext ctx, LvlConvertTarget target, string[] names, string? packfile = "levels1.vpp", bool anyRf2 = false)
    {
        if (ctx.Shell.Modules.OfType<LvlModule>().FirstOrDefault() is not { } module) return null;
        return new LvlConvertWindow(ctx.Shell.Dialogs, names, packfile, Path.Combine(Path.GetTempPath(), "levels"), module.Settings, target, LvlModule.RfgAvailable, anyRf2)
        {
            Owner = ctx.MainWindow,
        };
    }

    [SelfTest("lvl.convert-window-fits")]
    public static async Task ConvertWindowFits(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        // The tallest form of the window (Red Faction II, every option shown) on a short screen: the choices scroll and
        // Convert, Cancel and where the files go stay in view.
        var window = new LvlConvertWindow(ctx.Shell.Dialogs, ["dmc13.rfl"], null, Path.Combine(Path.GetTempPath(), "levels"), module.Settings, LvlConvertTarget.V3m, LvlModule.RfgAvailable, anyRf2: true)
        {
            ShowActivated = false, Left = -10000, Top = -10000,
        };
        try
        {
            window.ShowRf2Details();
            window.Show();
            window.MaxHeight = 560;
            await ctx.SettleAsync();
            window.UpdateLayout();
            var convert = window.ConvertButton;
            double bottom = convert.TranslatePoint(new Point(0, convert.ActualHeight), window).Y;
            ctx.Check(window.ActualHeight <= 561 && window.ChoicesScroll, $"the window keeps to the screen and its choices scroll ({window.ActualHeight:0} px high)");
            ctx.Check(convert.IsVisible && bottom > 0 && bottom <= window.ActualHeight, $"Convert stays in view ({bottom:0} of {window.ActualHeight:0} px)");
        }
        finally { window.Close(); }

        // glTF and OBJ sources offer the level of detail, as meshes do (a mesh's glTF holds each of its LODs)
        foreach (var (name, lod) in new[] { ("crate.v3m", true), ("L1S1.gltf", true), ("room.obj", true) })
        {
            var w = new LvlConvertWindow(ctx.Shell.Dialogs, [name], null, Path.Combine(Path.GetTempPath(), "levels"), module.Settings, LvlConvertTarget.Rfg, LvlModule.RfgAvailable);
            ctx.Check(w.OffersLod == lod, $"{name}: the level of detail is {(w.OffersLod ? "offered" : "not offered")}");
            w.Close();
        }
    }

    [SelfTest("lvl.convert-meshes")]
    public static async Task ConvertMeshes(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        if (!ctx.Check(LvlModule.RfgAvailable, "this version writes group files")) return;
        foreach (string name in new[] { "crate.v3m", "guard.V3C", "scene.gltf", "scene.glb", "room.obj" })
        {
            var targets = module.TargetsFor(name);
            ctx.Check(targets is [{ Extension: ".rfg", Lossy: true }] && module.CanConvert(name, ".rfg") && !module.CanConvert(name, ".glb"), $"{name}: lists a group file only ({string.Join(", ", targets.Select(t => t.Extension))})");
        }
        ctx.Check(module.TargetsFor("box.v3d").Count == 0 && module.TargetsFor("walk.rfa").Count == 0 && !module.CanConvert("crate.v3m"), "exporter meshes and clips are not offered; Convert levels… keeps to levels");
        ctx.Check(module.Menus.Any(m => m.Slot == MenuSlot.FileImport && m.Item is System.Windows.Controls.MenuItem { Header: string h } && h.Contains("Group file", StringComparison.Ordinal)),
            "File › Import › Group file from glTF, OBJ or mesh… is contributed");

        string folder = TempFolder("meshes");
        try
        {
            // a mesh from the meshes module's own sample, without a dialog
            var v3m = Cairn.Rfa.Formats.Legacy.LegacyMeshSupport.Convert(Cairn.Rfa.Formats.Legacy.SyntheticExporterMesh.SampleStatic(), "crate.v3d").Bytes;
            var main = await module.ConvertAsync(new AssetConversionRequest("crate.v3m", v3m, ".rfg") { Interactive = false, OutputFolder = folder });
            ctx.Check(main == Path.Combine(folder, "crate.rfg") && File.Exists(main) && module.LastOutcomes is [{ Report.HasErrors: false }], $"crate.v3m to .rfg without a dialog ({main})");
            if (main is not null && File.Exists(main))
            {
                var group = Reading.RfgReader.Read(File.ReadAllBytes(main), "crate.rfg");
                ctx.Check(group.Problems.Length == 0 && group.AllBrushes.Any(), $"... which reads back ({group.AllBrushes.Count()} brushes)");
            }

            // an OBJ entry of a packfile finds its .mtl among the other entries
            const string obj = "mtllib room.mtl\no wall\nv 0 0 0\nv 1 0 0\nv 1 1 0\nusemtl stone\nf 1 2 3\n";
            const string mtl = "newmtl stone\nmap_Kd rock01.tga\n";
            var read = new List<string>();
            var objRequest = new AssetConversionRequest("room.obj", System.Text.Encoding.ASCII.GetBytes(obj), ".rfg")
            {
                Interactive = false, OutputFolder = folder, ArchivePath = Path.Combine(folder, "maps.vpp"), ArchiveEntryNames = ["room.obj", "room.mtl"],
                ReadSibling = n => { read.Add(n); return n.Equals("room.mtl", StringComparison.OrdinalIgnoreCase) ? System.Text.Encoding.ASCII.GetBytes(mtl) : null; },
            };
            var fromObj = await module.ConvertAsync(objRequest);
            ctx.Check(fromObj is not null && read.Contains("room.mtl"), $"an OBJ entry converts with its .mtl read from the packfile ({fromObj})");
            if (fromObj is not null)
            {
                var group = Reading.RfgReader.Read(File.ReadAllBytes(fromObj), "room.rfg");
                ctx.Check(group.AllBrushes.Any(b => b.Geometry.Textures.Contains("rock01.tga", StringComparer.OrdinalIgnoreCase)) && group.AllBrushes.All(b => b.IsDetail),
                    "... its texture comes from the .mtl and the open mesh became a detail brush");
            }
            ctx.Check(await module.ConvertFileToRfgAsync(Path.Combine(folder, "missing.glb"), new LvlConvertChoice(new LvlConvertOptions { Target = LvlConvertTarget.Rfg }, LvlOutputPlace.Folder, folder, false)) is null,
                "a file that cannot be read converts to nothing (the error is reported)");
        }
        finally { TryDelete(folder); }

        // the Convert window for meshes: only the group file, the brush kind and the level of detail
        var window = new LvlConvertWindow(ctx.Shell.Dialogs, ["crate.v3m"], null, null, module.Settings, null, rfgAvailable: true);
        ctx.Check(window.Options.Target == LvlConvertTarget.Rfg && window.Title.Contains("group file", StringComparison.Ordinal), $"the mesh window converts to a group file ({window.Title})");
        window.Select(RfgBrushKind.Detail);
        ctx.Check(window.Import.BrushKind == RfgBrushKind.Detail && window.Current() is { Import.BrushKind: RfgBrushKind.Detail }, "picking a brush kind reaches the choice");
        window.Select(LvlConvertTarget.Glb);
        ctx.Check(window.Options.Target == LvlConvertTarget.Rfg, "no other target can be picked for a mesh");
        window.Close();

        // the Red Faction II options: REDUX's defaults, kept through the settings shape
        var rf2Window = new LvlConvertWindow(ctx.Shell.Dialogs, ["dmc13.rfl"], null, null, module.Settings, LvlConvertTarget.Rfg, rfgAvailable: true, anyRf2: true);
        ctx.Check(rf2Window.Options.Rf2.LightRangeScale > 0 && rf2Window.Options.Rf2.JumpPadScale > 0, "the window carries the Red Faction II options");
        rf2Window.Close();
        var changed = new LvlRf2Options { Textures = LvlRf2TextureMode.KeepOriginals, LightScale = 2, CoronaRange = 25, GeoableWithoutDetail = true, JumpPadScale = 0.5f };
        var back = LvlSettings.Rf2Dto.From(changed).ToOptions();
        ctx.Check(back == changed, "the Red Faction II options survive the settings round trip");
        ctx.Check(new LvlSettings.Rf2Dto { LightScale = float.NaN, JumpPadScale = -1 }.ToOptions() is { LightScale: 1f, JumpPadScale: LvlRf2Options.DefaultJumpPadScale }, "damaged settings fall back to the defaults");
    }

    [ScreenshotDialog("lvl.report")]
    public static Window? ReportDialog(ScreenshotContext ctx)
    {
        var source = LvlSource.Read(LvlSyntheticLevel.Bytes(), "synthetic.rfl");
        var conversion = LvlConverter.Convert(source.Input, new LvlConvertOptions { Target = LvlConvertTarget.V3m, TextureResolver = ctx.Shell.Assets.Resolver }, "synthetic.v3m");
        var outcome = new LvlConvertOutcome("synthetic.rfl", conversion.Report, [Path.Combine(Path.GetTempPath(), "levels", "synthetic.v3m")], null);
        return new LvlReportWindow("Converted synthetic.rfl to .v3m: 1 file written.", [outcome]) { Owner = ctx.MainWindow };
    }

    [SelfTest("lvl.packfile-preview")]
    public static async Task PackfilePreview(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        ctx.Check(module.CanPreview("a.rfl") && module.CanPreview("B.RFG") && !module.CanPreview("a.v3m"), "previews levels and group files");
        // the room's textures beside it, as a packfile's other entries
        var image = new BgraImage(8, 8);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) image.Set(x, y, 40, (byte)(x * 30), 200, 255);
        var siblings = AssetSiblings.FromMemory("maps.vpp", [new("wall.tga", TgaWriter.Write(image, includeAlpha: false)), new("floor.tga", TgaWriter.Write(image, includeAlpha: false))]);
        var bytes = LvlSyntheticLevel.Bytes();
        var element = module.CreatePreview(bytes, "synthetic.rfl", siblings);
        if (!ctx.Check(element is LvlPreview, "CreatePreview gives the level view")) return;
        var preview = (LvlPreview)element!;
        var host = new Window { Width = 640, Height = 420, Content = preview, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false, Left = -4000, Top = -4000 };
        host.Show();
        try
        {
            await preview.Loading;
            await ctx.SettleAsync();
            var scene = preview.View;
            ctx.Check(scene.Scene?.Triangles == LvlSyntheticLevel.DefaultTriangles, $"the preview draws the level ({scene.Scene?.Triangles} triangles; {scene.Message})");
            ctx.Check(siblings.ReadCount("wall.tga") > 0 && scene.TexturesLoaded >= 2, $"textures come from the packfile first ({scene.TexturesLoaded} loaded, wall.tga read {siblings.ReadCount("wall.tga")} times)");
        }
        finally
        {
            host.Close();
            preview.Dispose();
        }
        // disposing before the file is read is safe
        var early = (LvlPreview)module.CreatePreview(bytes, "synthetic.rfl", null)!;
        early.Dispose();
        await early.Loading;
        ctx.Check(early.View.Scene is null, "a preview disposed at once builds nothing");

        // the shared preview pane uses the module's view, with Open in Cairn
        var pane = new AssetPreviewPane(ctx.Shell);
        var paneHost = new Window { Width = 640, Height = 420, Content = pane, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false, Left = -4000, Top = -4000 };
        paneHost.Show();
        try
        {
            pane.ShowBytes("synthetic.rfl", _ => Task.FromResult(bytes), siblings, bytes.Length);
            await pane.Pending;
            await ctx.SettleAsync();
            ctx.Check(pane.Kind == AssetPreviewKind.Module && pane.View is ModulePreviewHost { Inner: LvlPreview }, $"the packfile preview pane shows a level with this module's view ({pane.Kind})");
            if (pane.View is ModulePreviewHost { Inner: LvlPreview inner })
            {
                await inner.Loading;
                ctx.Check(inner.View.Scene is { IsEmpty: false }, "and builds it");
            }
            pane.ShowBytes("group.rfg", _ => Task.FromResult<byte[]>([0x0D, 0xD0, 0x3D, 0xD4, 0, 0, 0, 0]), null, 8);
            await pane.Pending;
            await ctx.SettleAsync();
            ctx.Check(pane.Kind == AssetPreviewKind.Module, "a broken group file shows a message in the module's view, not a crash");
        }
        finally
        {
            paneHost.Close();
            pane.Dispose();
        }
    }
}
