using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Formats.Imaging;
using Cairn.Formats.XForge;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Cairn.Lvl.Ui.Dialogs;
using Cairn.Lvl.Ui.Documents;
using Cairn.Lvl.Ui.Preview;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Lvl.Ui.Diagnostics;

/// <summary>
/// Self-tests of Red Faction for N-Gage levels (<c>.xff</c>): a synthetic level always; the game's own
/// <c>redfaction.cfl</c> when <c>ngageDirectory</c> is set (level_01a opened from the archive and converted to a group
/// file, both captured dark and light into <c>--lvl-shots &lt;folder&gt;</c>; <c>--lvl-xff-out &lt;folder&gt;</c> keeps the
/// converted files there for checking in the level editor).
/// </summary>
internal static class LvlXffSelfTests
{
    private static LvlModule? Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<LvlModule>().FirstOrDefault();

    private static XffTransform At(float x, float y, float z) => new(new Vector3(x, y, z), Quaternion.Identity, Vector3.One);

    /// <summary>A one-area level: a textured floor, the player, a guard, a health pack, a placed door, two spawn points with their volume, a script trigger.</summary>
    private static byte[] SyntheticLevel()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint area = x.Marker(XffMarkers.Area, root, "room01");
        x.MeshObject(area, "floor", [Vector3.Zero, new Vector3(4, 0, 0), new Vector3(4, 0, 4), new Vector3(0, 0, 4)], [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY],
            [0, 1, 2, 0, 2, 3], null, [Vector2.Zero, new Vector2(2, 0), new Vector2(2, 2), Vector2.Zero, new Vector2(2, 2), new Vector2(0, 2)], "txtr_floor.pcx");
        x.Property(x.CollisionBox(area, "col_room01", Vector3.Zero, new Vector3(4, 1.5f, 4)), "room");
        x.Prefab(area, "LD_player", At(0.5f, 0, 0.5f), "player.xff");
        x.Prefab(area, "LD_GUARD01", At(3, 0, 3), "enemy_ug.xff");
        x.Prefab(area, "LD_hp", At(2, 0, 2), "item_healthpack.xff");
        x.Prefab(area, "LD_door", At(4, 0, 2), "lvl1a_door01.xff");
        x.Marker(4202, area, "enemy01", At(3.5f, 0, 1));
        x.Marker(4202, area, "enemy01", At(3.5f, 0, 1.5f));
        uint group = x.Marker(XffMarkers.EnemyGroup, area, "gr_enemy01", At(1, 0, 3));
        x.CollisionBox(group, "col_gr_enemy01", new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 1, 0.5f));
        uint script = x.Marker(XffMarkers.ScriptTrigger, area, "gr_entrig", At(1, 0, 1));
        x.CollisionBox(script, "col_gr_entrig", new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 1, 0.5f));
        return x.ToArray();
    }

    /// <summary>A door scene (a model): one quad with the door marker.</summary>
    private static byte[] SyntheticDoor()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        x.Marker(XffMarkers.Door, root, "gr_door01");
        x.MeshObject(root, "door", [Vector3.Zero, Vector3.UnitZ, new Vector3(0, 1.5f, 1), new Vector3(0, 1.5f, 0)], [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
            [0, 1, 2, 0, 2, 3], null, [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.Zero, Vector2.One, Vector2.UnitY], "txtr_floor.pcx");
        return x.ToArray();
    }

    private static byte[] Texture() => SyntheticPcx.Write(32, 32, 8, 1, (px, py) => ((px / 8) + (py / 8)) % 2 == 0 ? 1 : 2, [0, 0, 0, 200, 120, 40, 60, 90, 160]);

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

    private static async Task<LvlSceneView?> SceneOf(SelfTestContext ctx, LvlDocument doc)
    {
        await doc.Loading;
        await ctx.SettleAsync();
        if (LvlModule.ViewOf(doc) is not { } view) return null;
        await view.Scene.Building;
        await ctx.SettleAsync();
        return view.Scene;
    }

    [SelfTest("lvl.xff-synthetic")]
    public static async Task Synthetic(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        string folder = TempFolder("xff");
        LvlDocument? doc = null;
        try
        {
            byte[] level = SyntheticLevel(), door = SyntheticDoor(), pcx = Texture();
            string path = Path.Combine(folder, "level_01a.xff");
            File.WriteAllBytes(path, level);
            File.WriteAllBytes(Path.Combine(folder, "lvl1a_door01.xff"), door);
            File.WriteAllBytes(Path.Combine(folder, "txtr_floor.pcx"), pcx);
            File.WriteAllBytes(Path.Combine(folder, "C01_entrig.txt"), "END"u8.ToArray());

            // the content decides: a level is this module's, a door model is not
            ctx.Check(module.XffKind.Claim("level_01a.xff", level) == ContentClaims.Recognised, "the .xff kind claims the level");
            ctx.Check(module.XffKind.Claim("lvl1a_door01.xff", door) == ContentClaims.NotMine, "the .xff kind declines the door model");
            ctx.Check(((IAssetPreviewProvider)module).Claim("lvl1a_door01.xff", door) == ContentClaims.NotMine && ((IArchiveBatchConverter)module).Claim("level_01a.xff", level) == ContentClaims.Recognised,
                "the preview provider and the batch converter claim the same way");
            var kinds = ctx.Shell.Modules.SelectMany(m => m.DocumentKinds).ToList();
            ctx.Check(ContentClaims.KindFor(kinds, "level_01a.xff", level) == module.XffKind, "the shell picks the levels module's kind for the level");
            var converter = (IAssetConverter)module;
            ctx.Check(converter.HeadLengthFor("level_01a.xff") == ContentClaims.WholeFile, "Convert to asks for the whole .xff");
            var targets = converter.TargetsFor("level_01a.xff", level).Select(t => t.Extension).ToList();
            ctx.Check(targets.Contains(".glb") && targets.Contains(".obj") && targets.Contains(".v3m") && targets.Contains(".rfg"), "level targets: " + string.Join(", ", targets));
            ctx.Check(converter.TargetsFor("lvl1a_door01.xff", door).Count == 0, "no level targets for the door model");

            if (!ctx.Check(ctx.Shell.OpenFile(path) && ctx.Shell.ActiveDocument is LvlDocument, "the synthetic level opens in a level tab")) return;
            doc = (LvlDocument)ctx.Shell.ActiveDocument!;
            var scene = await SceneOf(ctx, doc);
            if (!ctx.Check(doc.Source?.Level is { IsXff: true } && scene?.Scene is not null, $"read as an N-Gage level and its preview built ({doc.LoadError ?? scene?.Message ?? "ok"})")) return;
            var read = doc.Source!.Level!;
            ctx.Check(read.Xff!.PlacedGeometry.Length == 1 && read.Xff.MissingFiles.IsEmpty, "the placed door is read from beside the level: " + string.Join(", ", read.Xff.PlacedGeometry));
            ctx.Check(scene!.TrianglesDrawn == 4, $"floor and door drawn: {scene.TrianglesDrawn} triangles");
            ctx.Check(scene.TexturesLoaded == 1 && scene.MissingTextures.Count == 0, $"the .pcx texture loads ({scene.TexturesLoaded}; missing {string.Join(", ", scene.MissingTextures)})");
            // the player start, the guard, the two hidden spawns, the health pack, two triggers and the UnHide event
            ctx.Check(scene.Markers.Count == 8, $"markers: {scene.Markers.Count} ({string.Join(", ", scene.Markers.Select(m => m.Title))})");
            ctx.Check(doc.StatusItems.Any(s => s.Text == "Red Faction for N-Gage level"), "status bar: " + string.Join(" | ", doc.StatusItems.Select(s => s.Text)));
            ctx.Check(!((Cairn.Ui.Documents.IDocument)doc).CanSave && ((IDocumentContent)doc).ContentHead.Length == level.Length, "read only; the tab gives its whole content to Convert to");
            var view = LvlModule.ViewOf(doc)!;
            await view.Facts.Pending;
            var rows = view.Facts.Rows;
            ctx.Check(rows.Any(r => r.Label == "Rooms") && rows.Any(r => r.Label == "Entity guard1" && r.Value.Contains("1 placed, 2 spawned")) && rows.Any(r => r.Label == "Item Medical Kit")
                && rows.Any(r => r.Section == "Script triggers" && r.Value.Contains("C01_entrig.txt")), "facts: rooms, objects by class, script triggers");
            ctx.Check(rows.Any(r => r.Section == "Textures" && r.Label == "txtr_floor.pcx" && !r.Flagged), "facts: the texture is found");

            // Convert to a group file from the tab, with the textures copied
            var output = Path.Combine(folder, "out");
            var choice = new LvlConvertChoice(module.Settings.Options with { Target = LvlConvertTarget.Rfg, Geometry = LvlGeometryChoice.Default, Xff = new LvlXffOptions() }, LvlOutputPlace.Folder, output, Replace: false);
            var files = await module.ConvertDocumentAsync(doc, choice);
            if (ctx.Check(files is { Count: > 0 } && File.Exists(files[0]), "converted to a group file: " + string.Join(", ", files?.Select(Path.GetFileName) ?? [])))
            {
                var back = RfgReader.Read(File.ReadAllBytes(files![0]), Path.GetFileName(files[0]));
                ctx.Check(back.Problems.IsEmpty && back.AllBrushes.Count() == read.Brushes.Length && back.Groups.Sum(g => g.Objects.Count) == read.Objects.Count,
                    $"the group file reads back with {back.AllBrushes.Count()} brushes and {back.Groups.Sum(g => g.Objects.Count)} objects");
                ctx.Check(File.Exists(Path.Combine(output, "txtr_floor.tga")) && !File.Exists(Path.Combine(output, "txtr_floor.pcx"))
                    && back.AllBrushes.SelectMany(b => b.Geometry.Textures).Contains("txtr_floor.tga") && !back.AllBrushes.SelectMany(b => b.Geometry.Textures).Any(XffTextureFix.IsPcx),
                    "the group file names the .pcx texture as .tga, and the copy beside it is that .tga");
                var report = module.LastOutcomes?.FirstOrDefault()?.Report;
                ctx.Check(report?.Issues.Any(i => i.Code == LvlConvertCodes.XffObjects) == true && report.Issues.Any(i => i.Code == LvlConvertCodes.XffTextures), "the report has the N-Gage entries");
            }

            // the packfile preview: the door comes from the siblings
            var siblings = AssetSiblings.FromMemory("test.cfl", [new("lvl1a_door01.xff", door), new("txtr_floor.pcx", pcx)]);
            if (module.CreatePreview(level, "level_01a.xff", siblings) is LvlPreview preview)
            {
                try
                {
                    await preview.Loading;
                    ctx.Check(preview.View.Source?.Level?.Xff?.PlacedGeometry.Length == 1, "the packfile preview reads the door among the packfile's entries");
                }
                finally { preview.Dispose(); }
            }
            else ctx.Check(false, "the packfile preview is made");

            // tabs and previews draw a 16-bit texture whose alpha bit is never set as opaque, as the N-Gage game does;
            // only the textures the level names are touched
            {
                var x = new SyntheticXff();
                uint area = x.Marker(XffMarkers.Area, x.Root(), "room01");
                x.MeshObject(area, "grate", [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY], [0, 1, 2], null,
                    [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], "txtr_grate.tga");
                byte[] grate = [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 16, 0x21, 0x00, 0x7C]; // 1 x 1, ARGB 1555, alpha bit clear
                var source = LvlSource.Read(x.ToArray(), "level_grate.xff");
                var beside = AssetSiblings.FromMemory("test.cfl", [new("txtr_grate.tga", grate), new("txtr_other.tga", grate)]);
                var textures = LvlXffTextures.ResolverFor(source, beside, null);
                byte[]? Bytes(string name) => textures?.Resolve(name)?.ReadAllBytes();
                ctx.Check(Bytes("txtr_grate.tga") is { } shown && shown[17] == 0x20 && ImageDecoder.Decode(shown, "txtr_grate.tga").Pixels[3] == 255,
                    "a 16-bit texture the level names, its alpha bit never set, is shown opaque");
                ctx.Check(Bytes("txtr_other.tga") is { } other && other[17] == 0x21, "a texture the level does not name is shown as it is");
                var plainSource = LvlSource.Read(Cairn.Lvl.Writing.RfgWriter.Write(new RfGroupFile { Name = "plain.rfg" }).Bytes, "plain.rfg");
                ctx.Check(LvlXffTextures.ResolverFor(plainSource, beside, null)?.Resolve("txtr_grate.tga")?.ReadAllBytes() is { } plain && plain[17] == 0x21,
                    "a Red Faction group file's textures are shown as they are");
            }

            // "Convert levels…": the level converts; a model entry is reported, not converted
            var entries = new List<ArchiveBatchEntry> { new("level_01a.xff", level.Length, () => level), new("lvl1a_door01.xff", door.Length, () => door), new("txtr_floor.pcx", pcx.Length, () => pcx) };
            var batch = new ArchiveBatchRequest("test.cfl", folder, [entries[0], entries[1]])
            {
                Interactive = false,
                AllEntries = entries,
                Options = new LvlConvertChoice(module.Settings.Options with { Target = LvlConvertTarget.Obj, Geometry = LvlGeometryChoice.Default }, LvlOutputPlace.Folder, Path.Combine(folder, "batch"), Replace: false),
            };
            await ((IArchiveBatchConverter)module).ConvertAsync(batch);
            var outcomes = module.LastOutcomes ?? [];
            ctx.Check(outcomes.Count == 2 && outcomes[0].Succeeded && !outcomes[1].Succeeded && outcomes[1].Error?.Contains("not a level") == true,
                "batch: " + string.Join("; ", outcomes.Select(o => $"{o.SourceName} {(o.Succeeded ? "ok" : o.Error)}")));
        }
        finally
        {
            if (doc is not null) ctx.Shell.Close(doc);
            TryDelete(folder);
        }
    }

    [SelfTest("lvl.xff-ngage")]
    public static async Task Ngage(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no levels module in this build"); return; }
        string? cfl = LocalPaths.NgageDirectory is { } dir ? Path.Combine(dir, "redfaction.cfl") : null;
        if (cfl is null || !File.Exists(cfl)) { ctx.Skip("no Red Faction for N-Gage archive (" + LocalPaths.HowToSet(LocalPaths.NgageDirectoryVariable, "ngageDirectory") + ")"); return; }
        var provider = ctx.Shell.Modules.OfType<IWorkCopyProvider>().FirstOrDefault(p => p.CanOpenArchive(cfl));
        if (provider is null) { ctx.Skip("no module opens entries of a .cfl"); return; }
        string? keep = ctx.Options.TryGetValue("lvl-xff-out", out var k) && !string.IsNullOrWhiteSpace(k) ? Path.GetFullPath(k) : null;
        string folder = keep ?? TempFolder("xff-ngage");
        var opened = new List<Cairn.Ui.Documents.IDocument>();
        try
        {
            var before = ctx.Shell.Documents.ToList();
            if (!ctx.Check(provider.OpenArchiveEntry(cfl, "level_01a.xff"), "Open in Cairn opens level_01a.xff from redfaction.cfl")) return;
            LvlDocument? doc = null;
            var watch = Stopwatch.StartNew();
            while (doc is null && watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(100);
                doc = ctx.Shell.Documents.OfType<LvlDocument>().FirstOrDefault(d => d.DisplayName.Equals("level_01a.xff", StringComparison.OrdinalIgnoreCase));
            }
            opened.AddRange(ctx.Shell.Documents.Except(before));
            if (!ctx.Check(doc is not null, "the level opens in a level tab")) return;
            ctx.Shell.Activate(doc!);
            var scene = await SceneOf(ctx, doc!);
            if (!ctx.Check(doc!.Source?.Level is { IsXff: true } && scene?.Scene is not null, $"level_01a is read and previewed ({doc.LoadError ?? scene?.Message ?? "ok"})")) return;
            var level = doc.Source!.Level!;
            ctx.Check(level.Xff!.MissingFiles.IsEmpty && level.Xff.PlacedGeometry.Length == 5, $"its 5 placed doors and walls come from the archive (missing: {string.Join(", ", level.Xff.MissingFiles)})");
            ctx.Check(scene!.TexturesLoaded == level.Xff.Textures.Length && scene.MissingTextures.Count == 0,
                $"every texture loads from the archive ({scene.TexturesLoaded} of {level.Xff.Textures.Length}; missing {string.Join(", ", scene.MissingTextures)})");
            ctx.Check(level.Objects.Entities.Length == 14 && level.Objects.Items.Length == 14 && level.Objects.Triggers.Length == 14 && level.PlayerStart is not null,
                $"objects: {level.Objects.Entities.Length} entities, {level.Objects.Items.Length} items, {level.Objects.Triggers.Length} triggers, player start");
            scene.MarkerKinds = LvlMarkerKinds.All;
            await LvlModule.ApplyViewOptionsAsync(scene, new Dictionary<string, string> { ["lvl-camera"] = "35,55,1.4" });
            await Capture(ctx, LvlModule.ViewOf(doc)!, "xff-level_01a");
            ctx.Check(scene.MarkersDrawn > 0, $"markers drawn: {scene.MarkersDrawn}");

            // to a group file, its textures beside it; then the group file opened in Cairn
            var output = Path.Combine(folder, "out");
            var choice = new LvlConvertChoice(module.Settings.Options with { Target = LvlConvertTarget.Rfg, Geometry = LvlGeometryChoice.Default, Xff = new LvlXffOptions() }, LvlOutputPlace.Folder, output, Replace: false);
            var files = await module.ConvertDocumentAsync(doc, choice);
            if (ctx.Check(files is { Count: > 0 }, "level_01a converts to a group file: " + string.Join(", ", files?.Select(Path.GetFileName) ?? [])))
            {
                int copied = level.Xff.Textures.Count(t => File.Exists(Path.Combine(output, XffTextureFix.PcName(t))));
                ctx.Check(copied == level.Xff.Textures.Length && !Directory.EnumerateFiles(output, "*.pcx").Any(),
                    $"its {copied} of {level.Xff.Textures.Length} textures are copied beside it, each .pcx as .tga");
                var back = RfgReader.Read(File.ReadAllBytes(files![0]), Path.GetFileName(files[0]));
                ctx.Check(back.Problems.IsEmpty && back.AllBrushes.Count() == level.Brushes.Length && back.Groups.Sum(g => g.Objects.Count) == level.Objects.Count,
                    $"the group file reads back: {back.AllBrushes.Count()} brushes, {back.Groups.Sum(g => g.Objects.Count)} objects");
                if (module.LastOutcomes?.FirstOrDefault()?.Report is { } report) ctx.Log("  report:\n    " + report.ToText().Replace(Environment.NewLine, Environment.NewLine + "    "));
                var docsBefore = ctx.Shell.Documents.ToList();
                if (ctx.Check(ctx.Shell.OpenFile(files[0]) && ctx.Shell.ActiveDocument is LvlDocument { IsGroupFile: true }, "the group file opens in Cairn"))
                {
                    var rfg = (LvlDocument)ctx.Shell.ActiveDocument!;
                    opened.AddRange(ctx.Shell.Documents.Except(docsBefore));
                    var rfgScene = await SceneOf(ctx, rfg);
                    ctx.Check(rfgScene?.Scene is not null && rfgScene.TexturesLoaded > 0 && rfgScene.MissingTextures.Count == 0,
                        $"its preview shows the brushes with the copied textures ({rfgScene?.TexturesLoaded} loaded, missing {string.Join(", ", rfgScene?.MissingTextures ?? [])})");
                    if (rfgScene is not null)
                    {
                        rfgScene.MarkerKinds = LvlMarkerKinds.All;
                        await LvlModule.ApplyViewOptionsAsync(rfgScene, new Dictionary<string, string> { ["lvl-camera"] = "35,55,1.4" });
                        await Capture(ctx, LvlModule.ViewOf(rfg)!, "xff-level_01a-rfg");
                    }
                }
            }

            // a packfile entry through Convert to (no tab): another level, read with its archive's other entries
            var archive = CflArchive.Open(cfl);
            byte[] Read(string name)
            {
                using var stream = File.OpenRead(cfl);
                return archive.ReadEntry(stream, archive.Find(name)!);
            }
            byte[] level02 = await Task.Run(() => Read("level_02a.xff"));
            var names = archive.Entries.Select(e => e.Name).ToList();
            var request = new AssetConversionRequest("level_02a.xff", level02, ".v3m")
            {
                Interactive = false,
                OutputFolder = Path.Combine(folder, "entry"),
                ArchivePath = cfl,
                ArchiveEntryNames = names,
                ReadSibling = n => archive.Find(n) is null ? null : Read(n),
            };
            string? written = await ((IAssetConverter)module).ConvertAsync(request);
            ctx.Check(written is not null && File.Exists(written) && module.LastOutcomes?.FirstOrDefault()?.Report?.Issues.Any(i => i.Message.Contains("not found beside the level")) != true,
                $"level_02a converts from its entry to a static mesh with its placed files found ({Path.GetFileName(written)})");

            // "Convert levels…" to group files over three levels (deathmatch; barrels and the ATV; Masako's) and a character
            var batchEntries = new[] { "level_50.xff", "level_08a.xff", "level_12c.xff", "enemy_ug.xff" }.Select(n => new ArchiveBatchEntry(n, archive.Find(n)!.UnpackedSize, () => Read(n))).ToList();
            var all = archive.Entries.Select(e => new ArchiveBatchEntry(e.Name, e.UnpackedSize, () => Read(e.Name))).ToList();
            await ((IArchiveBatchConverter)module).ConvertAsync(new ArchiveBatchRequest("redfaction.cfl", Path.GetDirectoryName(cfl), batchEntries)
            {
                Interactive = false,
                AllEntries = all,
                Options = new LvlConvertChoice(module.Settings.Options with { Target = LvlConvertTarget.Rfg, Geometry = LvlGeometryChoice.Default, Xff = new LvlXffOptions() }, LvlOutputPlace.Folder, Path.Combine(folder, "batch"), Replace: false),
            });
            var outcomes = module.LastOutcomes ?? [];
            ctx.Check(outcomes.Count == 4 && outcomes.Take(3).All(o => o.Succeeded) && !outcomes[3].Succeeded && outcomes[3].Error?.Contains("not a level") == true,
                "batch: " + string.Join("; ", outcomes.Select(o => $"{o.SourceName} {(o.Succeeded ? "ok" : o.Error)}")));
        }
        finally
        {
            foreach (var d in opened.AsEnumerable().Reverse()) ctx.Shell.Close(d);
            if (keep is null) TryDelete(folder);
        }
    }

    /// <summary>The Convert window for an N-Gage level (<c>--dialog lvl.convert-xff</c>): its N-Gage section shows.</summary>
    [ScreenshotDialog("lvl.convert-xff")]
    public static Window? ConvertDialogXff(ScreenshotContext ctx)
    {
        if (ctx.Shell.Modules.OfType<LvlModule>().FirstOrDefault() is not { } module) return null;
        // a .cfl is read only, so "into the packfile" is never offered for its entries
        return new LvlConvertWindow(ctx.Shell.Dialogs, ["level_01a.xff"], null, Path.Combine(Path.GetTempPath(), "levels"), module.Settings, LvlConvertTarget.Rfg, LvlModule.RfgAvailable)
        {
            Owner = ctx.MainWindow,
        };
    }

    /// <summary>Captures <paramref name="element"/> in the dark and the light theme into the shots folder and checks each is not blank.</summary>
    private static async Task Capture(SelfTestContext ctx, FrameworkElement element, string name)
    {
        string shots = ctx.Options.TryGetValue("lvl-shots", out var d) && !string.IsNullOrWhiteSpace(d) ? d : Path.Combine(Path.GetTempPath(), "cairn-lvl-shots");
        Directory.CreateDirectory(shots);
        var theme = ctx.Shell.Theme;
        var before = theme.Requested;
        try
        {
            foreach (bool light in new[] { false, true })
            {
                theme.Apply(light ? Cairn.Workspace.AppTheme.Light : Cairn.Workspace.AppTheme.Dark);
                await Task.Delay(200);
                for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await ctx.SettleAsync();
                string path = Path.Combine(shots, $"{name}-{(light ? "light" : "dark")}.png");
                int colours = SavePng(element, path);
                ctx.Check(colours > 16, $"{Path.GetFileName(path)} is not blank ({colours} colours)");
            }
        }
        finally
        {
            theme.Apply(before);
        }
    }

    private static int SavePng(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        int w = (int)Math.Max(1, element.ActualWidth), h = (int)Math.Max(1, element.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var pixels = new int[w * h];
        rtb.CopyPixels(pixels, w * 4, 0);
        return pixels.Distinct().Count();
    }
}
