using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Ui.ViewModels;
using ShellSelfTestContext = Cairn.Ui.Diagnostics.SelfTestContext;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Open packfiles as library sources (L14), over synthetic packfiles held in memory (<see cref="AssetSiblings.FromMemory"/>,
/// given to <see cref="Services.PackfileSources"/> in place of the shell's): each packfile gets a group in the library's
/// Meshes tree and Clips list, filtered like the rest; the clip tab's Preview mesh picker lists a Red Faction II clip's
/// Red Faction II characters (ranked, never a Red Faction mesh unless asked) and a Red Faction clip's Red Faction meshes
/// (never a Red Faction II character); a pick plays and is remembered over the automatic choice; double-click across games
/// refuses with the reason; a Red Faction II character's tab offers Red Faction II clips, a Red Faction mesh's tab Red
/// Faction clips; a packfile that goes away takes its group and its picker entries with it.
/// </summary>
internal static class PackfileSourcesSelfTests
{
    [SelfTest("rfa.packfile-sources")]
    public static async Task PackfileSources(ShellSelfTestContext ctx)
    {
        var ws = RfaModule.Workspace;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-packsrc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<DocumentViewModel>();
        try
        {
            byte[] character = SyntheticRf2Mesh.SampleCharacter();
            byte[] swing = SyntheticRf2Clip.SampleSwing();
            byte[] walk = RfaWriter.Write(RfaReader.Read(swing, "walk.rfa") with { Version = 8 });
            byte[] pcCharacter = LegacyMeshSupport.Convert(character, "guard.rfc").Bytes; // a Red Faction mesh with the same 2 bones
            var rf2Files = AssetSiblings.FromMemory("rf2.packfile", [new("guard.rfc", character), new("zz_other.rfc", character),
                new("guard_vcm.peg", SyntheticRf2Mesh.SamplePeg(("guard_body.tga", 8, 8, 0xFF40C040))), new("guard_swing.rfa", swing), new("crate.rfm", SyntheticRf2Mesh.SampleStatic())]);
            string pcTexture = Formats.V3d.V3dReader.Read(pcCharacter, "guard_pc.v3c").Submeshes.SelectMany(s => s.Lods).SelectMany(l => l.Textures)
                .Select(t => t.FileName).FirstOrDefault(t => t.Length > 0) ?? "guard_body.tga";
            var rf1Files = AssetSiblings.FromMemory("mod.vpp", [new("guard_pc.v3c", pcCharacter), new("walk.rfa", walk), new(pcTexture, new byte[64])]);
            var rf2Source = new OpenArchiveSource("test-rf2", "rf2.packfile", null, rf2Files, new object()) { IsRedFactionII = true };
            var rf1Source = new OpenArchiveSource("test-rf1", "mod.vpp", null, rf1Files, new object());

            ws.Packfiles.UseTestSources([rf2Source, rf1Source]);
            await ws.Packfiles.Idle;
            await ctx.SettleAsync();
            var libraries = ws.Packfiles.Libraries;
            ctx.Check(libraries.Count == 2 && libraries[0].Label == "rf2.packfile" && libraries[1].Label == "mod.vpp", "both packfiles become sources, in tab order: " + string.Join(", ", libraries.Select(l => $"{l.Label} ({l.Summary})")));
            ctx.Check(rf2Files.ReadCount("guard_swing.rfa") == 1 && rf2Files.ReadCount("crate.rfm") == 0, $"probing reads a clip once (its head) and a static mesh not at all ({rf2Files.ReadCount("guard_swing.rfa")}, {rf2Files.ReadCount("crate.rfm")})");

            // the library panel: one group per packfile above the library, in both tabs
            var roots = ws.Library.MeshRoots.ToList();
            ctx.Check(roots.Count >= 2 && roots[0].Header == "rf2.packfile" && roots[1].Header == "mod.vpp", "the Meshes tree starts with the packfiles' groups: " + string.Join(", ", roots.Take(3).Select(r => r.Header)));
            var rf2Group = roots.FirstOrDefault(r => r.Header == "rf2.packfile");
            var guardNode = rf2Group?.Children.SelectMany(f => f.Children).FirstOrDefault(n => n.Header == "guard.rfc");
            ctx.Check(rf2Group?.Detail == "3 meshes · 1 clip" && rf2Group.Children.Any(c => c.Header == "guard family (2)") && rf2Group.Children.Any(c => c.Header == "Static meshes"),
                $"the group holds a skeleton family of its characters and its static meshes ({rf2Group?.Detail}: {string.Join(", ", rf2Group?.Children.Select(c => c.Header) ?? [])})");
            ctx.Check(guardNode is { IsPackfileItem: true, IsCharacterMesh: false, Item: LibraryMesh { BoneCount: 2 } }, "a character node of the group (no New Clip for it)");
            var headers = ws.Library.ClipRows.OfType<LibraryClipGroupRow>().Select(h => h.Header).ToList();
            ctx.Check(headers.Take(2).SequenceEqual(["rf2.packfile", "mod.vpp"]), "the Clips list has a header per packfile: " + string.Join(", ", headers));
            var swingRow = ws.Library.ClipRows.OfType<LibraryClipRow>().FirstOrDefault(r => r.Name == "guard_swing.rfa");
            ctx.Check(swingRow is { VersionText: "v9", BonesText: "2" }, $"an RF2 clip lists with its facts ({swingRow?.VersionText}, {swingRow?.BonesText} bones)");

            // the filter applies to the groups
            ws.Library.Filter = "guard_sw";
            await Task.Delay(400);
            await ctx.SettleAsync();
            var filtered = ws.Library.ClipRows.ToList();
            ctx.Check(filtered.OfType<LibraryClipRow>().All(r => r.Name.Contains("guard_sw", StringComparison.OrdinalIgnoreCase))
                && filtered.OfType<LibraryClipGroupRow>().Any(h => h.Header == "rf2.packfile") && filtered.OfType<LibraryClipGroupRow>().All(h => h.Header != "mod.vpp"),
                "the filter applies to the packfile groups (a group with no match is hidden): " + string.Join(", ", filtered.Select(r => r is LibraryClipRow c ? c.Name : ((LibraryClipGroupRow)r).Header)));
            ws.Library.Filter = "";
            await Task.Delay(400);
            await ctx.SettleAsync();

            // ── a Red Faction II clip (alone in a folder, so no character beside it) ──
            string clipPath = Path.Combine(folder, "guard_swing.rfa");
            File.WriteAllBytes(clipPath, swing);
            ctx.Check(ctx.Shell.OpenFile(clipPath), "the Red Faction II clip opens");
            await ctx.SettleAsync();
            if (ws.ActiveDocument is not ClipDocumentViewModel { IsRedFactionII: true } clip) { ctx.Check(false, "... as a Red Faction II clip tab"); return; }
            opened.Add(clip);
            await WaitForMesh(clip);
            await ctx.SettleAsync();
            var options = clip.PreviewMeshOptions.ToList();
            ctx.Check(options.Count > 0 && options.All(o => o.Source is not null && o.Mesh.Name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase)),
                "the Preview mesh picker lists the Red Faction II characters of the open packfiles, no Red Faction mesh: " + Describe(options));
            ctx.Check(options.Select(o => o.Mesh.Name).SequenceEqual(["guard.rfc", "zz_other.rfc"]) && options[0].Group == "Open packfile · rf2.packfile",
                "ranked (name, then fit) and grouped under their packfile");
            ctx.Check(clip.Rf2PreviewMeshName == "guard.rfc" && clip.PreviewSkeletonCount == 2 && clip.SelectedPreviewMesh?.Mesh.Name == "guard.rfc",
                $"the automatic choice is the best character of an open packfile? ({clip.Scene.MeshName ?? "none"}; {clip.Scene.Notice ?? "no notice"}); picker shows {clip.SelectedPreviewMesh?.Mesh.Name ?? "nothing"}");

            // pick another character: it plays (converted), with its textures, and is remembered
            clip.SelectedPreviewMesh = options.First(o => o.Mesh.Name == "zz_other.rfc");
            await WaitForMesh(clip, "zz_other.rfc");
            ctx.Check(clip.Scene.MeshName == "zz_other.rfc" && clip.FittingSkeleton?.Count == 2 && clip.Rf2PreviewMeshName == "zz_other.rfc",
                $"the picked character plays the clip ({clip.Scene.MeshName}, {clip.Scene.Skeleton.Count} bones)");
            ctx.Check(clip.Scene.TextureResolver?.Resolve("guard_body.tga") is not null, "its texture resolves from the packfile's .peg");
            ctx.Check(ws.RememberedPreviewMesh(clip.LintDocumentPath) == "zz_other.rfc", "the pick is remembered for the clip");

            // the other game's meshes only when asked
            clip.ShowOtherGameMeshes = true;
            ctx.Check(clip.PreviewMeshOptions.Take(2).All(o => o.Source is not null), "Other game keeps the Red Faction II characters on top");
            int libraryOptions = clip.PreviewMeshOptions.Count(o => o.Source is null);
            ctx.Log($"  Other game lists {libraryOptions} library meshes");
            ctx.Check(clip.PreviewMeshOptions.All(o => o.Source is null || !o.Mesh.Name.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase)), "a Red Faction II clip's picker never takes a packfile's Red Faction mesh");
            // a Red Faction mesh of the library played, then Other game turned off: the picker never goes blank while a
            // mesh plays (the clip goes back to a Red Faction II character)
            if (clip.PreviewMeshOptions.FirstOrDefault(o => o.Source is null && o.Mesh.HasSkeleton) is { } rf1Option)
            {
                clip.SelectedPreviewMesh = rf1Option;
                await WaitForMesh(clip, rf1Option.Mesh.Name);
                clip.ShowOtherGameMeshes = false;
                await WaitForMesh(clip, "guard.rfc");
                await ctx.SettleAsync();
                ctx.Check(clip.SelectedPreviewMesh is { Source: not null } shownOption && string.Equals(shownOption.Mesh.Name, clip.Scene.MeshName, StringComparison.OrdinalIgnoreCase),
                    $"Other game off while {rf1Option.Mesh.Name} played: plays {clip.Scene.MeshName ?? "nothing"}, the picker shows {clip.SelectedPreviewMesh?.Mesh.Name ?? "nothing"}");
            }
            else
            {
                clip.ShowOtherGameMeshes = false;
                ctx.Log("  (the library has no character: Other game off while one plays is not checked)");
            }

            // the library: double-click across games refuses with the reason; a character of the clip's game previews
            var pcMesh = libraries[1].FindMesh("guard_pc.v3c")!;
            string? why = ws.Library.GameMismatch(pcMesh, clip);
            ctx.Check(why?.Contains("mangled", StringComparison.Ordinal) == true, $"a Red Faction mesh under a Red Faction II clip: refused ({why})");
            ctx.Check(!ws.Library.PreviewCommand.CanExecute(pcMesh), "Preview is off for the other game's mesh");
            var guardNodeTip = LibraryViewModel.ActivationHint?.Invoke(pcMesh) ?? "";
            ctx.Check(guardNodeTip.Contains("open in a new tab", StringComparison.Ordinal) && guardNodeTip.Contains("mangled", StringComparison.Ordinal), "its tooltip says why: " + guardNodeTip.Replace("\n", " / ", StringComparison.Ordinal));
            ws.Library.Activate(libraries[0].FindMesh("guard.rfc"), newTab: false);
            await WaitForMesh(clip, "guard.rfc");
            ctx.Check(clip.Scene.MeshName == "guard.rfc" && clip.PreviewLibraryMesh?.Location.DisplayLocation == "rf2.packfile" && clip.SelectedPreviewMesh?.Mesh.Name == "guard.rfc",
                $"double-click on the group's character previews the clip on it ({clip.Scene.MeshName}; picker {clip.SelectedPreviewMesh?.Mesh.Name})");

            // close and reopen: the remembered pick wins over the automatic choice
            clip.SelectedPreviewMesh = clip.PreviewMeshOptions.First(o => o.Mesh.Name == "zz_other.rfc");
            await WaitForMesh(clip, "zz_other.rfc");
            ws.CloseDiscarding(clip);
            opened.Remove(clip);
            ctx.Check(ctx.Shell.OpenFile(clipPath), "the clip opens again");
            await ctx.SettleAsync();
            if (ws.ActiveDocument is ClipDocumentViewModel again)
            {
                opened.Add(again);
                await WaitForMesh(again, "zz_other.rfc");
                ctx.Check(again.Scene.MeshName == "zz_other.rfc" && again.SelectedPreviewMesh?.Mesh.Name == "zz_other.rfc",
                    $"reopened, it plays on the remembered pick, not the automatic one ({again.Scene.MeshName})");
            }

            // ── a Red Faction clip: Red Faction meshes only ──
            string walkPath = Path.Combine(folder, "walk.rfa");
            File.WriteAllBytes(walkPath, walk);
            ctx.Check(ctx.Shell.OpenFile(walkPath), "a Red Faction clip opens");
            await ctx.SettleAsync();
            if (ws.ActiveDocument is ClipDocumentViewModel { IsRedFactionII: false } rf1Clip)
            {
                opened.Add(rf1Clip);
                await WaitForMesh(rf1Clip);
                var rf1Options = rf1Clip.PreviewMeshOptions.ToList();
                ctx.Check(rf1Options.Any(o => o.Mesh.Name == "guard_pc.v3c" && o.Group == "Open packfile · mod.vpp" && o.Fits) && rf1Options.All(o => !o.Mesh.Name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase)),
                    "its picker lists the packfile's Red Faction mesh and no Red Faction II character: " + Describe(rf1Options));
                var pick = rf1Options.First(o => o.Mesh.Name == "guard_pc.v3c");
                rf1Clip.SelectedPreviewMesh = pick;
                await WaitForMesh(rf1Clip, "guard_pc.v3c");
                ctx.Check(rf1Clip.Scene.MeshName == "guard_pc.v3c" && rf1Clip.FittingSkeleton?.Count == 2, $"... and plays on it ({rf1Clip.Scene.MeshName})");
                ctx.Check(ws.Library.GameMismatch(libraries[0].FindMesh("guard.rfc"), rf1Clip) is not null, "a Red Faction II character under a Red Faction clip is refused");

                // double-click on another clip with this one in front: its new tab plays on the same packfile mesh, credited
                // to the packfile, with the packfile's textures
                string walk2Path = Path.Combine(folder, "walk2.rfa");
                File.WriteAllBytes(walk2Path, walk);
                var walk2Item = new LibraryClip("walk2.rfa", new AssetLocation("walk2.rfa", "walk2.rfa", AssetSourceKind.DocumentFolder, walk2Path, null, null), null, null, null);
                ws.Library.OpenOnSameMesh(walk2Item, rf1Clip);
                for (int i = 0; i < 100 && ws.ActiveDocument is not ClipDocumentViewModel { DisplayName: "walk2.rfa" }; i++) await Task.Delay(50);
                if (ws.ActiveDocument is ClipDocumentViewModel { DisplayName: "walk2.rfa" } walk2)
                {
                    opened.Add(walk2);
                    await WaitForMesh(walk2, "guard_pc.v3c");
                    await ctx.SettleAsync();
                    var texture = walk2.Scene.TextureResolver?.Resolve(pcTexture);
                    ctx.Check(walk2.Scene.MeshName == "guard_pc.v3c" && walk2.PreviewLibraryMesh?.Location.DisplayLocation == "mod.vpp" && texture?.DisplayLocation == "mod.vpp",
                        $"a clip opened on the same mesh plays on the packfile's mesh with its textures (mesh {walk2.Scene.MeshName ?? "none"} from {walk2.PreviewLibraryMesh?.Location.DisplayLocation ?? "nowhere"}; {pcTexture} from {texture?.DisplayLocation ?? "nowhere"})");
                }
                else ctx.Check(false, "a clip opened on the same mesh gets its tab");
            }
            else ctx.Check(false, "... as a Red Faction clip tab");

            // ── at startup (the library still loading): a Red Faction clip's remembered pick the library has is the
            // library's, not an open packfile's mesh of the same name ──
            if (ws.Assets.Snapshot.Meshes.FirstOrDefault(m => m.HasSkeleton) is { } libraryMesh)
            {
                var startFiles = AssetSiblings.FromMemory("startup.vpp", [new(libraryMesh.Name, pcCharacter)]);
                var startSource = new OpenArchiveSource("test-startup", "startup.vpp", null, startFiles, new object());
                ws.Packfiles.UseTestSources([rf2Source, rf1Source, startSource]);
                await ws.Packfiles.Idle;
                await ctx.SettleAsync();
                string startPath = Path.Combine(folder, "walk_start.rfa");
                File.WriteAllBytes(startPath, walk);
                ws.RememberPreviewMesh(startPath, 2, libraryMesh.Name);
                ClipDocumentViewModel? startClip;
                using (ws.Assets.SimulateStartupForTest())
                {
                    ctx.Check(ctx.Shell.OpenFile(startPath), "a Red Faction clip opens while the library is loading");
                    await Task.Delay(300);
                    await ctx.SettleAsync();
                    startClip = ws.ActiveDocument as ClipDocumentViewModel;
                    if (startClip is not null) opened.Add(startClip);
                }
                if (startClip is not null)
                {
                    await WaitForMesh(startClip, libraryMesh.Name);
                    await ctx.SettleAsync();
                    ctx.Check(startClip.Scene.MeshName == libraryMesh.Name && startClip.PreviewLibraryMesh is { } shownMesh && shownMesh.Location.Origin is null,
                        $"once the library is there, it plays on the library's {libraryMesh.Name} it remembers (plays {startClip.Scene.MeshName ?? "nothing"} from {startClip.PreviewLibraryMesh?.Location.DisplayLocation ?? "nowhere"})");
                }
                ws.Packfiles.UseTestSources([rf2Source, rf1Source]);
                await ws.Packfiles.Idle;
            }
            else ctx.Log("  (the library has no character: a remembered pick at startup is not checked)");

            // ── a Red Faction II character's tab: Red Faction II clips, ranked by fit ──
            string rfcPath = Path.Combine(folder, "guard.rfc");
            File.WriteAllBytes(rfcPath, character);
            ctx.Check(ctx.Shell.OpenFile(rfcPath), "a Red Faction II character opens");
            await ctx.SettleAsync();
            if (ws.ActiveDocument is MeshDocumentViewModel { IsRedFactionIIMesh: true } rf2Mesh)
            {
                opened.Add(rf2Mesh);
                for (int i = 0; i < 100 && !rf2Mesh.PreviewClipOptions.Any(o => o.Note.Contains("fit", StringComparison.Ordinal)); i++) await Task.Delay(50);
                await ctx.SettleAsync();
                var clipOptions = rf2Mesh.PreviewClipOptions.Where(o => o.Clip is not null).ToList();
                ctx.Check(clipOptions.Any(o => o.Clip!.Name == "guard_swing.rfa" && o.Note.Contains("fit", StringComparison.Ordinal)) && clipOptions.All(o => PackfileLibrary.IsRedFactionII(o.Clip!)),
                    "its picker offers the packfiles' Red Faction II clips, ranked by fit, and no Red Faction clip: " + string.Join(", ", clipOptions.Select(o => $"{o.Clip!.Name} [{o.Note}]")));
                rf2Mesh.SelectedPreviewClip = clipOptions.FirstOrDefault(o => o.Clip!.Name == "guard_swing.rfa");
                for (int i = 0; i < 100 && rf2Mesh.PreviewClip is null; i++) await Task.Delay(50);
                ctx.Check(rf2Mesh.PreviewClip is { BoneCount: 2, IsRedFactionII: true } && rf2Mesh.Scene.Skeleton.Count == 2, "the picked Red Faction II clip plays on it");
                ctx.Check(ws.Library.GameMismatch(libraries[1].FindClip("walk.rfa"), rf2Mesh) is not null, "a Red Faction clip on it is refused");
            }
            else ctx.Check(false, "... as a Red Faction II mesh tab");

            // ── a Red Faction mesh's tab: Red Faction clips ──
            string v3cPath = Path.Combine(folder, "guard_pc.v3c");
            File.WriteAllBytes(v3cPath, pcCharacter);
            ctx.Check(ctx.Shell.OpenFile(v3cPath), "a Red Faction mesh opens");
            await ctx.SettleAsync();
            if (ws.ActiveDocument is MeshDocumentViewModel { IsRedFactionIIMesh: false } rf1Mesh)
            {
                opened.Add(rf1Mesh);
                var clipOptions = rf1Mesh.PreviewClipOptions.Where(o => o.Clip is not null).ToList();
                ctx.Check(clipOptions.Any(o => o.Clip!.Name == "walk.rfa" && o.Source is not null) && clipOptions.All(o => !PackfileLibrary.IsRedFactionII(o.Clip!)),
                    "its picker offers the packfile's Red Faction clip and no Red Faction II clip: " + string.Join(", ", clipOptions.Select(o => o.Clip!.Name)));
            }
            else ctx.Check(false, "... as a Red Faction mesh tab");

            // ── a packfile closes: its group and its picker entries go ──
            ws.Packfiles.UseTestSources([rf1Source]);
            await ws.Packfiles.Idle;
            await ctx.SettleAsync();
            ctx.Check(ws.Packfiles.Libraries.Count == 1 && ws.Library.MeshRoots.All(r => r.Header != "rf2.packfile") && ws.Library.ClipRows.OfType<LibraryClipGroupRow>().All(h => h.Header != "rf2.packfile"),
                "a closed packfile's group disappears from both tabs");
            if (opened.OfType<ClipDocumentViewModel>().FirstOrDefault(d => d.IsRedFactionII) is { } rf2Clip)
                ctx.Check(rf2Clip.PreviewMeshOptions.All(o => o.Source?.Label != "rf2.packfile"), "... and from the pickers");
            ctx.Check(!ws.Packfiles.IsCached("test-rf2") && ws.Packfiles.IsCached("test-rf1"), "... and from the cache (an open packfile's stays)");
            ws.Packfiles.UseTestSources([rf2Source, rf1Source]);
            await ws.Packfiles.Idle;
            await ctx.SettleAsync();
            ctx.Check(ws.Library.MeshRoots.FirstOrDefault()?.Header == "rf2.packfile", "opened again, it is back (read again)");
        }
        finally
        {
            ws.Packfiles.UseTestSources(null);
            foreach (var d in Enumerable.Reverse(opened)) if (ws.Documents.Contains(d)) ws.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Describe(IEnumerable<PreviewMeshOption> options) =>
        string.Join(", ", options.Take(12).Select(o => $"{o.Mesh.Name} [{o.Group ?? "-"}; {o.Note}]"));

    /// <summary>Waits (up to 30 s) until the clip's preview mesh is chosen, or is the named one.</summary>
    private static async Task WaitForMesh(ClipDocumentViewModel doc, string? name = null)
    {
        for (int i = 0; i < 300 && (name is null ? doc.PreviewMesh is null && !doc.Scene.NoticeIsWarning
            : !string.Equals(doc.Scene.MeshName, name, StringComparison.OrdinalIgnoreCase)); i++)
            await Task.Delay(100);
        await Task.Delay(100);
    }
}
