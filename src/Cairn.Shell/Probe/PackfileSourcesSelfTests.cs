#if CAIRN_MODULE_VPP && CAIRN_MODULE_RFA
using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using Cairn.Formats;
using Cairn.Rfa.Assets;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Ui;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui;
using Cairn.Vpp.Ui.Documents;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>
/// Open packfiles as library sources (L14), end to end through the packfile module's tabs: the source contract
/// (<see cref="IOpenArchiveSources"/>: a tab is a source, an edit is a new version, a closed tab is gone, a work copy
/// knows its source), the library's group, the pickers of tabs opened with Open in Cairn listing their own packfile
/// first, Ctrl+double-click opening an entry linked to its packfile. Scenario C: a Red Faction .vpp with a character
/// and clips (synthetic, under %TEMP%). Scenarios A and B: Red Faction II's All_Levels.toc_group (read in place, never
/// written): a clip tab plays on another character picked from the group, a character's tab plays a clip of the group.
/// </summary>
public static class PackfileSourcesSelfTests
{
    private static async Task<bool> WaitAsync(Func<bool> done, int seconds)
    {
        for (int i = 0; i < seconds * 10 && !done(); i++) await Task.Delay(100);
        return done();
    }

    [SelfTest("integration.packfile-sources")]
    public static async Task PackfileSources(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var vpp = shell.Modules.OfType<VppModule>().First();
        var ws = RfaModule.Workspace;
        string root = Path.Combine(Path.GetTempPath(), "cairn-packsrc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var opened = new List<IDocument>();
        int raised = 0;
        EventHandler count = (_, _) => raised++;
        ((IOpenArchiveSources)vpp).OpenSourcesChanged += count;
        try
        {
            // ── Scenario C: a Red Faction .vpp with a character and two clips ──
            byte[] swing = SyntheticRf2Clip.SampleSwing();
            byte[] walk = RfaWriter.Write(RfaReader.Read(swing, "walk.rfa") with { Version = 8 });
            byte[] stand = RfaWriter.Write(RfaReader.Read(swing, "stand.rfa") with { Version = 8, RampIn = 0 });
            byte[] pc = LegacyMeshSupport.Convert(SyntheticRf2Mesh.SampleCharacter(), "guard.rfc").Bytes;
            var package = Cairn.Vpp.Editing.VppEdit.AddSources(VppPackage.Empty,
                [("guard_pc.v3c", (VppSource)new MemorySource(pc)), ("walk.rfa", new MemorySource(walk)), ("stand.rfa", new MemorySource(stand))],
                Cairn.Vpp.Editing.VppClashPolicy.KeepBoth).Package;
            string vppPath = Path.Combine(root, "packsrc_mod.vpp");
            Cairn.Vpp.Writing.VppSaver.Save(package, vppPath, null, CancellationToken.None, Cairn.Vpp.Writing.VppSaveOptions.Default);
            if (!ctx.Check(shell.OpenFile(vppPath) && shell.ActiveDocument is VppDocument, "the .vpp opens")) return;
            var mod = (VppDocument)shell.ActiveDocument!;
            opened.Add(mod);
            await ctx.SettleAsync();

            // the contract
            var sources = AssetSiblings.OpenSources(shell);
            var source = sources.FirstOrDefault(s => s.Key == mod.Id);
            ctx.Check(source is { Label: "packsrc_mod.vpp", IsRedFactionII: false } && ReferenceEquals(source.Version, mod.Current)
                && string.Equals(source.ArchivePath, vppPath, StringComparison.OrdinalIgnoreCase) && source.Entries.Names.Count == 3 && source.OpenEntry is not null,
                $"the tab is a source: {source?.Label}, {source?.Entries.Names.Count} entries, version = its snapshot");
            ctx.Check(raised > 0, $"OpenSourcesChanged was raised when the tab opened ({raised})");
            ctx.Check(source?.Entries.ReadHead("walk.rfa", 8) is { Length: 8 } head && head.AsSpan().SequenceEqual(walk.AsSpan(0, 8)), "a source reads the head of an entry");
            ctx.Check(ReferenceEquals(AssetSiblings.OpenSources(shell).First(s => s.Key == mod.Id), source), "an unchanged tab keeps its source object (its version)");

            await WaitAsync(() => ws.Packfiles.Libraries.Any(l => l.Key == mod.Id), 30);
            await ctx.SettleAsync();
            var library = ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == mod.Id);
            ctx.Check(library is { Clips.Length: 2, Meshes.Length: 1 }, $"the library reads it: {library?.Summary}");
            ctx.Check(ws.Library.MeshRoots.Any(r => r.Header == "packsrc_mod.vpp") && ws.Library.ClipRows.OfType<LibraryClipGroupRow>().Any(h => h.Header == "packsrc_mod.vpp"),
                "its group shows in the library (both tabs)");

            // Open in Cairn: the clip's picker lists its own packfile first
            await mod.Commands.OpenInCairnAsync(mod.Current.Find("walk.rfa")!);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is ClipDocumentViewModel { IsRedFactionII: false }, "Open in Cairn gives the clip tab"))
            {
                var tab = (ClipDocumentViewModel)shell.ActiveDocument!;
                opened.Add(tab);
                ctx.Check(AssetSiblings.SourceKeyOf(shell, tab.FilePath) == mod.Id, "the work copy knows its source");
                await WaitAsync(() => tab.PreviewMeshOptions.Any(o => o.Source is not null), 20);
                var first = tab.PreviewMeshOptions.FirstOrDefault();
                ctx.Check(first is { Group: "This packfile · packsrc_mod.vpp", Fits: true } && first.Mesh.Name == "guard_pc.v3c",
                    $"Preview mesh lists its own packfile first ({first?.Mesh.Name} under {first?.Group})");
                tab.SelectedPreviewMesh = first;
                await WaitAsync(() => tab.Scene.MeshName == "guard_pc.v3c", 20);
                ctx.Check(tab.Scene.MeshName == "guard_pc.v3c" && tab.FittingSkeleton?.Count == 2, $"... and plays on it ({tab.Scene.MeshName}, {tab.Scene.Skeleton.Count} bones)");
            }

            // the character: its picker lists the packfile's clips
            shell.ActiveDocument = mod;
            await mod.Commands.OpenInCairnAsync(mod.Current.Find("guard_pc.v3c")!);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is MeshDocumentViewModel { HasSkeleton: true }, "Open in Cairn gives the mesh tab"))
            {
                var meshTab = (MeshDocumentViewModel)shell.ActiveDocument!;
                opened.Add(meshTab);
                var clips = meshTab.PreviewClipOptions.Where(o => o.Source is not null).Select(o => o.Clip!.Name).ToList();
                ctx.Check(clips.Contains("walk.rfa") && clips.Contains("stand.rfa") && meshTab.PreviewClipOptions[1].Group == "This packfile · packsrc_mod.vpp",
                    "Preview clip lists its packfile's clips first: " + string.Join(", ", clips));
                await WaitAsync(() => meshTab.PreviewClip is not null, 10);
                ctx.Check(meshTab.PreviewClip is { BoneCount: 2 }, $"a clip of its packfile plays on it ({meshTab.SelectedPreviewClip?.Label})");

                // Ctrl+double-click on a group's clip: a tab linked to the packfile (a work copy)
                var standItem = ws.Packfiles.Libraries.First(l => l.Key == mod.Id).FindClip("stand.rfa")!;
                int before = shell.Documents.Count;
                ws.Library.Activate(standItem, newTab: true);
                await WaitAsync(() => shell.Documents.Count > before, 10);
                await ctx.SettleAsync();
                var standTab = shell.Documents.OfType<ClipDocumentViewModel>().FirstOrDefault(d => d.DisplayName == "stand.rfa");
                if (standTab is not null) opened.Add(standTab);
                ctx.Check(standTab?.FilePath is { } p && vpp.IsWorkCopy(p) && vpp.ArchiveEntryOf(p) is { EntryName: "stand.rfa" },
                    $"Ctrl+double-click opens the entry through the packfile's Open in Cairn ({standTab?.FilePath ?? "no tab"})");
            }

            // an edit: a new version, read again; undo goes back to the version already read
            raised = 0;
            var oldLibrary = ws.Packfiles.Libraries.First(l => l.Key == mod.Id);
            ctx.Check(mod.ApplyEdit("add a clip", p => Cairn.Vpp.Editing.VppEdit.AddSources(p, [("extra.rfa", (VppSource)new MemorySource(walk))], Cairn.Vpp.Editing.VppClashPolicy.KeepBoth).Package),
                "an entry is added to the packfile");
            await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == mod.Id)?.Clips.Length == 3, 20);
            await ctx.SettleAsync();
            ctx.Check(raised > 0 && ws.Packfiles.Libraries.First(l => l.Key == mod.Id).FindClip("extra.rfa") is not null, $"the source changed and its group follows ({raised} events)");
            mod.Undo();
            await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == mod.Id)?.Clips.Length == 2, 20);
            ctx.Check(ReferenceEquals(ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == mod.Id), oldLibrary), "undo brings back the snapshot already read (cached, not probed again)");

            // closing the packfile removes its group; opening it again brings it back
            foreach (var d in opened.Where(d => d is not VppDocument).ToList()) { if (shell.Documents.Contains(d)) shell.CloseDiscarding(d); opened.Remove(d); }
            shell.CloseDiscarding(mod);
            opened.Remove(mod);
            await WaitAsync(() => ws.Packfiles.Libraries.All(l => l.Label != "packsrc_mod.vpp"), 10);
            await ctx.SettleAsync();
            ctx.Check(AssetSiblings.OpenSources(shell).All(s => s.Label != "packsrc_mod.vpp") && ws.Library.MeshRoots.All(r => r.Header != "packsrc_mod.vpp")
                && ws.Library.ClipRows.OfType<LibraryClipGroupRow>().All(h => h.Header != "packsrc_mod.vpp"), "closing the packfile removes its source and its group");
            if (ctx.Check(shell.OpenFile(vppPath) && shell.ActiveDocument is VppDocument, "the .vpp opens again"))
            {
                opened.Add(shell.ActiveDocument!);
                await WaitAsync(() => ws.Packfiles.Libraries.Any(l => l.Label == "packsrc_mod.vpp"), 30);
                await ctx.SettleAsync();
                ctx.Check(ws.Library.MeshRoots.Any(r => r.Header == "packsrc_mod.vpp"), "... and its group is back");
            }
        }
        finally
        {
            ((IOpenArchiveSources)vpp).OpenSourcesChanged -= count;
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Scenarios A and B on Red Faction II's All_Levels.toc_group (read in place, read only): the group is probed off the
    /// UI thread (the time is logged); a version 9 clip opened with Open in Cairn lists the group's characters in its
    /// picker and plays on another one picked there; a character's tab lists the group's Red Faction II clips and plays one.
    /// </summary>
    [SelfTest("integration.packfile-sources-rf2")]
    public static async Task PackfileSourcesRf2(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var ws = RfaModule.Workspace;
        string? toc = LocalPaths.Rf2GameDirectory is { } rf2 ? Path.Combine(rf2, "pc_media", "All_Levels.toc_group") : null;
        if (toc is null || !File.Exists(toc)) { ctx.Skip("no Red Faction II install (" + LocalPaths.HowToSet(LocalPaths.Rf2GameDirectoryVariable, "rf2GameDirectory") + ")"); return; }
        var opened = new List<IDocument>();
        try
        {
            var clock = Stopwatch.StartNew();
            if (!ctx.Check(shell.OpenFile(toc) && shell.ActiveDocument is VppDocument { IsReadOnly: true }, "All_Levels.toc_group opens read only")) return;
            var group = (VppDocument)shell.ActiveDocument!;
            opened.Add(group);
            var openTime = clock.Elapsed;
            // the UI stays responsive while the group is probed: the dispatcher answers within a frame or two
            double worst = 0;
            while (!ws.Packfiles.Libraries.Any(l => l.Key == group.Id) && clock.Elapsed < TimeSpan.FromSeconds(120))
            {
                var tick = Stopwatch.StartNew();
                await ctx.Shell.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
                worst = Math.Max(worst, tick.Elapsed.TotalMilliseconds);
                await Task.Delay(20);
            }
            var library = ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == group.Id);
            if (!ctx.Check(library is not null, "the group becomes a source")) return;
            ctx.Log($"  All_Levels: tab open {openTime.TotalSeconds:0.00} s; probe {library!.ProbeTime.TotalSeconds:0.000} s ({library.Summary}, {library.Families.Length} families, {library.Errors} unreadable); "
                + $"ready {clock.Elapsed.TotalSeconds:0.00} s after open; slowest UI response while probing {worst:0} ms");
            ctx.Check(library.Clips.Length >= 60 && library.Meshes.Count(m => m.HasSkeleton) >= 100, $"its clips and characters: {library.Summary}");
            ctx.Check(worst < 500, $"the UI kept answering while the group was read (slowest {worst:0} ms)");
            await ctx.SettleAsync();
            ctx.Check(ws.Library.MeshRoots.FirstOrDefault()?.Header == "All_Levels.toc_group" && ws.Library.ClipRows.OfType<LibraryClipGroupRow>().FirstOrDefault()?.Header == "All_Levels.toc_group",
                "the library shows its group first (Meshes and Clips)");

            // ── Scenario A: a version 9 clip, another character picked ──
            var item = group.Current.Items.FirstOrDefault(i => i.Name.Equals("medm_talk.rfa", StringComparison.OrdinalIgnoreCase))
                ?? group.Current.Items.First(i => i.Name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase));
            await group.Commands.OpenInCairnAsync(item);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is ClipDocumentViewModel { IsRedFactionII: true }, $"Open in Cairn gives the clip tab ({item.Name})"))
            {
                var tab = (ClipDocumentViewModel)shell.ActiveDocument!;
                opened.Add(tab);
                await WaitAsync(() => tab.PreviewMesh is not null, 120);
                await ctx.SettleAsync();
                string? auto = tab.Scene.MeshName;
                var own = tab.PreviewMeshOptions.Where(o => o.Group == "This packfile · All_Levels.toc_group").ToList();
                ctx.Check(own.Count >= 2 && own.All(o => o.Mesh.Name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase) && o.Mesh.BoneCount == tab.Current.BoneCount)
                    && tab.PreviewMeshOptions.All(o => o.Source is not null),
                    $"Preview mesh lists the group's characters with the clip's {tab.Current.BoneCount} bones, nothing else ({own.Count}: {string.Join(", ", own.Take(6).Select(o => o.Mesh.Name + " " + o.Note))})");
                ctx.Check(auto is not null && tab.SelectedPreviewMesh?.Mesh.Name == auto, $"the automatic choice ({auto}) shows in the picker ({tab.SelectedPreviewMesh?.Mesh.Name})");
                var other = own.FirstOrDefault(o => !string.Equals(o.Mesh.Name, auto, StringComparison.OrdinalIgnoreCase));
                if (other is not null)
                {
                    var pickClock = Stopwatch.StartNew();
                    tab.SelectedPreviewMesh = other;
                    await WaitAsync(() => string.Equals(tab.Scene.MeshName, other.Mesh.Name, StringComparison.OrdinalIgnoreCase), 60);
                    await ctx.SettleAsync();
                    ctx.Check(string.Equals(tab.Scene.MeshName, other.Mesh.Name, StringComparison.OrdinalIgnoreCase) && tab.FittingSkeleton?.Count == tab.Current.BoneCount
                        && tab.Rf2PreviewMeshName is not null && tab.Playback.HasClip,
                        $"another character picked plays it: {tab.Scene.MeshName}, {tab.Scene.Skeleton.Count} bones = the clip's {tab.Current.BoneCount} (loaded in {pickClock.Elapsed.TotalSeconds:0.00} s)");
                    ctx.Check(ws.RememberedPreviewMesh(tab.LintDocumentPath) == other.Mesh.Name, "the pick is remembered");
                }
            }

            // ── Scenario B: a character's tab, a Red Faction II clip of the group ──
            shell.ActiveDocument = group;
            var burke = group.Current.Items.FirstOrDefault(i => i.Name.Equals("burke.rfc", StringComparison.OrdinalIgnoreCase))
                ?? group.Current.Items.First(i => i.Name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase));
            await group.Commands.OpenInCairnAsync(burke);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is MeshDocumentViewModel { IsRedFactionIIMesh: true }, $"Open in Cairn gives the character's tab ({burke.Name})"))
            {
                var meshTab = (MeshDocumentViewModel)shell.ActiveDocument!;
                opened.Add(meshTab);
                await WaitAsync(() => meshTab.PreviewClipOptions.Any(o => o.Note.Contains("fit", StringComparison.Ordinal)), 60);
                await ctx.SettleAsync();
                var clips = meshTab.PreviewClipOptions.Where(o => o.Clip is not null).ToList();
                ctx.Check(clips.Count > 0 && clips.All(o => o.Clip!.Facts?.Version == RfaClip.Rf2Version && o.Clip.BoneCount == meshTab.Scene.Skeleton.Count),
                    $"Preview clip lists Red Faction II clips with its {meshTab.Scene.Skeleton.Count} bones only ({clips.Count}: {string.Join(", ", clips.Take(5).Select(o => $"{o.Clip!.Name} [{o.Note}]"))})");
                ctx.Check(clips.FirstOrDefault()?.Group == "This packfile · All_Levels.toc_group", "its own packfile first, ranked by fit");
                if (clips.FirstOrDefault() is { } best)
                {
                    meshTab.SelectedPreviewClip = best;
                    await WaitAsync(() => meshTab.PreviewClip is not null, 30);
                    ctx.Check(meshTab.PreviewClip is { IsRedFactionII: true } c && c.BoneCount == meshTab.Scene.Skeleton.Count && meshTab.Playback.HasClip,
                        $"the clip picked plays on it ({best.Clip!.Name}, {meshTab.PreviewClip?.BoneCount} bones)");
                }
            }

            // closing the group removes it
            foreach (var d in opened.Where(d => d is not VppDocument).ToList()) { if (shell.Documents.Contains(d)) shell.CloseDiscarding(d); opened.Remove(d); }
            shell.CloseDiscarding(group);
            opened.Remove(group);
            await WaitAsync(() => ws.Packfiles.Libraries.All(l => l.Label != "All_Levels.toc_group"), 10);
            await ctx.SettleAsync();
            ctx.Check(ws.Library.MeshRoots.All(r => r.Header != "All_Levels.toc_group"), "closing the group removes it from the library");
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
        }
    }

    // ── L14 review: the cache, snapshots gone stale, same-name packfiles, an item's game ─────────────────────

    /// <summary>
    /// The cache of probed snapshots (defects 1 and 4 of the L14 review): an open packfile that does not change keeps its
    /// library while another one is edited more often than the cache holds snapshots (never evicted, never read again); a
    /// closed tab's snapshots leave the cache, so neither its library nor its packfile snapshot stays alive once the tab
    /// and a clip that played on its mesh are closed.
    /// </summary>
    [SelfTest("integration.packfile-sources-cache")]
    public static async Task PackfileSourcesCache(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var ws = RfaModule.Workspace;
        string root = Path.Combine(Path.GetTempPath(), "cairn-packsrc-" + Guid.NewGuid().ToString("N")[..8]);
        var before = shell.Documents.ToList();
        try
        {
            var (walk, _, pc) = Samples();
            string a = MakeVpp(root, "keep_a.vpp", ("guard_pc.v3c", pc), ("walk.rfa", walk));
            string b = MakeVpp(root, "edit_b.vpp", ("walk.rfa", walk));
            if (!ctx.Check(shell.OpenFile(a) && shell.ActiveDocument is VppDocument, "keep_a.vpp opens")) return;
            var modA = (VppDocument)shell.ActiveDocument!;
            if (!ctx.Check(shell.OpenFile(b) && shell.ActiveDocument is VppDocument, "edit_b.vpp opens")) return;
            var modB = (VppDocument)shell.ActiveDocument!;
            await WaitAsync(() => ws.Packfiles.Libraries.Count(l => l.Key == modA.Id || l.Key == modB.Id) == 2, 30);
            var libA = ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == modA.Id);
            if (!ctx.Check(libA is not null, "both packfiles are read")) return;

            // twenty edits of B: more snapshots than the cache keeps
            var missing = new List<int>();
            int replaced = -1;
            for (int i = 1; i <= 20; i++)
            {
                var previous = ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == modB.Id);
                string extra = $"extra{i}.rfa";
                modB.ApplyEdit("add " + extra, p => Cairn.Vpp.Editing.VppEdit.AddSources(p, [(extra, (VppSource)new MemorySource(walk))], Cairn.Vpp.Editing.VppClashPolicy.KeepBoth).Package);
                await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == modB.Id) is { } l && !ReferenceEquals(l, previous) && l.FindClip(extra) is not null, 20);
                await ws.Packfiles.Idle;
                await ctx.SettleAsync();
                var nowA = ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == modA.Id);
                if (nowA is null) missing.Add(i);
                else if (!ReferenceEquals(nowA, libA) && replaced < 0) replaced = i;
            }
            ctx.Check(missing.Count == 0, $"an open, unchanged packfile never leaves the sources while another one is edited 20 times (gone after edits [{string.Join(", ", missing)}])");
            ctx.Check(replaced < 0, $"... and is never read again (read again first after edit {replaced})");
            int cached = CachedKeys(ws.Packfiles).Count;
            ctx.Check(cached <= 16 + 2, $"the cache stays bounded: 16 earlier snapshots at most besides the open ones ({cached} snapshots)");

            // B closes: its snapshots leave the cache, A's stays
            string keyB = modB.Id;
            shell.CloseDiscarding(modB);
            await WaitAsync(() => ws.Packfiles.Libraries.All(l => l.Key != keyB), 10);
            await ctx.SettleAsync();
            var keys = CachedKeys(ws.Packfiles);
            ctx.Check(!keys.Contains(keyB) && keys.Contains(modA.Id), $"a closed packfile's snapshots leave the cache, an open one's stays ({keys.Count(k => k == keyB)} of the closed tab's kept, {keys.Count})");
            shell.CloseDiscarding(modA);
            await ctx.SettleAsync();

            // a packfile tab and a clip that played on its mesh, both closed: nothing of them stays alive
            string leakPath = MakeVpp(Path.Combine(root, "leak"), "leak_mod.vpp", ("guard_pc.v3c", pc), ("walk.rfa", walk), ("guard_body.tga", new byte[64]));
            var (refs, closedKey) = await OpenPickClose(ctx, shell, ws, leakPath);
            await ctx.SettleAsync();
            // work still finishing (a load on a worker thread, a queued callback) is a transient root, not a leak: collect
            // again for a while before checking
            for (int i = 0; i < 20 && refs.Any(x => x.Item2.IsAlive); i++)
            {
                await Task.Delay(500);
                await ctx.SettleAsync();
                await CollectAsync();
            }
            foreach (var (label, r) in refs)
            {
                bool alive = r.IsAlive;
                ctx.Check(!alive, $"{label} collected after its tab closed");
                if (alive) ctx.Log("  GC root of " + label + ":" + Environment.NewLine + RootOf(ctx, r));
            }
            ctx.Check(closedKey is not null && !CachedKeys(ws.Packfiles).Contains(closedKey), "... and the closed tab's snapshot left the cache");

            // the same file opened again: a new tab (a new key), read again; the closed tab's snapshot is not kept
            if (ctx.Check(shell.OpenFile(leakPath) && shell.ActiveDocument is VppDocument, "leak_mod.vpp opens again"))
            {
                var again = (VppDocument)shell.ActiveDocument!;
                await WaitAsync(() => ws.Packfiles.Libraries.Any(l => l.Key == again.Id), 30);
                await ctx.SettleAsync();
                var now = CachedKeys(ws.Packfiles);
                ctx.Check(now.Contains(again.Id) && (closedKey is null || !now.Contains(closedKey)), $"reopened: its new tab is read, the closed tab's snapshot is not kept ({now.Count} snapshots)");
            }
        }
        finally
        {
            CloseNew(shell, before);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<(List<(string, WeakReference)> Refs, string? Key)> OpenPickClose(SelfTestContext ctx, ShellViewModel shell, RfaWorkspace ws, string vppPath)
    {
        var refs = new List<(string, WeakReference)>();
        if (!ctx.Check(shell.OpenFile(vppPath) && shell.ActiveDocument is VppDocument, "leak_mod.vpp opens")) return (refs, null);
        var mod = (VppDocument)shell.ActiveDocument!;
        await WaitAsync(() => ws.Packfiles.Libraries.Any(l => l.Key == mod.Id), 30);
        var clip = await OpenClipFrom(shell, mod, "walk.rfa");
        if (!ctx.Check(clip is not null, "its clip opens")) return (refs, mod.Id);
        await WaitAsync(() => clip!.PreviewMeshOptions.Any(o => o.Source is not null), 20);
        if (clip!.PreviewMeshOptions.FirstOrDefault(o => o.Mesh.Name == "guard_pc.v3c") is { } option)
        {
            clip.SelectedPreviewMesh = option;
            await WaitAsync(() => clip.Scene.MeshName == "guard_pc.v3c", 20);
        }
        ctx.Check(clip.Scene.MeshName == "guard_pc.v3c", "the clip plays on its packfile's mesh");
        refs.Add(("VppDocument", new WeakReference(mod)));
        refs.Add(("VppPackage (its snapshot)", new WeakReference(mod.Current)));
        refs.Add(("PackfileLibrary", new WeakReference(ws.Packfiles.Libraries.First(l => l.Key == mod.Id))));
        refs.Add(("ClipDocumentViewModel", new WeakReference(clip)));
        string key = mod.Id;
        shell.CloseDiscarding(clip);
        shell.CloseDiscarding(mod);
        return (refs, key);
    }

    /// <summary>
    /// Snapshots gone stale (defects 2 and 3 of the L14 review): after the packfile is saved (every entry moves in the
    /// file) a snapshot from before refuses to read rather than read the wrong bytes, and a clip playing on its mesh and a
    /// work copy of the mesh read the texture from the saved file; a picked preview mesh replaced in its packfile plays as
    /// it is now (picking it again too), one removed stops playing with a notice; a mesh tab's preview clip follows its
    /// entry the same way.
    /// </summary>
    [SelfTest("integration.packfile-sources-stale")]
    public static async Task PackfileSourcesStale(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var ws = RfaModule.Workspace;
        string root = Path.Combine(Path.GetTempPath(), "cairn-packsrc-" + Guid.NewGuid().ToString("N")[..8]);
        var before = shell.Documents.ToList();
        try
        {
            var (walk, stand, pc) = Samples();

            // ── a save moves every entry ──
            string texture = TextureOf(pc, "guard_pc.v3c") ?? "guard_body.tga";
            byte[] texBytes = Enumerable.Repeat((byte)'T', 4096).ToArray();
            string vppPath = MakeVpp(Path.Combine(root, "save"), "save_mod.vpp", ("aaa_pad.bin", Enumerable.Repeat((byte)'P', 8192).ToArray()),
                ("guard_pc.v3c", pc), (texture, texBytes), ("walk.rfa", walk));
            if (!ctx.Check(shell.OpenFile(vppPath) && shell.ActiveDocument is VppDocument, "save_mod.vpp opens")) return;
            var mod = (VppDocument)shell.ActiveDocument!;
            await WaitAsync(() => ws.Packfiles.Libraries.Any(l => l.Key == mod.Id), 30);
            // a work copy of the mesh (Open in Cairn): its textures come from the rest of the packfile
            await mod.Commands.OpenInCairnAsync(mod.Current.Find("guard_pc.v3c")!);
            await WaitAsync(() => shell.ActiveDocument is MeshDocumentViewModel { DisplayName: "guard_pc.v3c" }, 20);
            if (!ctx.Check(shell.ActiveDocument is MeshDocumentViewModel, "Open in Cairn gives the mesh's tab")) return;
            var meshTab = (MeshDocumentViewModel)shell.ActiveDocument!;
            // a loose clip picks the packfile's mesh
            string clipPath = Path.Combine(root, "walk.rfa");
            File.WriteAllBytes(clipPath, walk);
            if (!ctx.Check(shell.OpenFile(clipPath) && shell.ActiveDocument is ClipDocumentViewModel, "a loose clip opens")) return;
            var clip = (ClipDocumentViewModel)shell.ActiveDocument!;
            await WaitAsync(() => clip.PreviewMeshOptions.Any(o => o.Source?.Key == mod.Id), 20);
            clip.SelectedPreviewMesh = clip.PreviewMeshOptions.FirstOrDefault(o => o.Mesh.Name == "guard_pc.v3c" && o.Source?.Key == mod.Id);
            await WaitAsync(() => clip.Scene.MeshName == "guard_pc.v3c", 20);
            await ctx.SettleAsync();
            var clip0 = ReadThrough(clip.Scene.TextureResolver, texture);
            var tab0 = ReadThrough(meshTab.Scene.TextureResolver, texture);
            ctx.Check(clip0 is not null && clip0.AsSpan().SequenceEqual(texBytes) && tab0 is not null && tab0.AsSpan().SequenceEqual(texBytes),
                $"before the save the clip and the work copy read {texture} from the packfile ({Describe(clip0)}; {Describe(tab0)})");
            var oldSource = AssetSiblings.OpenSources(shell).First(s => s.Key == mod.Id);
            var oldLibrary = ws.Packfiles.Libraries.First(l => l.Key == mod.Id);

            mod.ApplyEdit("grow pad", p => Cairn.Vpp.Editing.VppEdit.Replace(p, "aaa_pad.bin", new MemorySource(Enumerable.Repeat((byte)'Q', 32768).ToArray())));
            mod.SaveTo(vppPath);
            await WaitAsync(() => !mod.IsDirty, 20);
            await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == mod.Id) is { } l && !ReferenceEquals(l, oldLibrary), 20);
            await Task.Delay(500);
            await ctx.SettleAsync();
            ctx.Check(oldSource.Entries.Read(texture) is null && oldSource.Entries.ReadHead(texture, 16) is null,
                "a snapshot from before the save refuses to read (its offsets point into the replaced file)");
            var clip1 = ReadThrough(clip.Scene.TextureResolver, texture);
            ctx.Check(clip1 is not null && clip1.AsSpan().SequenceEqual(texBytes),
                $"after the save the clip on the packfile's mesh reads its texture from the saved file ({Describe(clip1)}; glTF/OBJ export with textures uses this resolver)");
            var tab1 = ReadThrough(meshTab.Scene.TextureResolver, texture);
            ctx.Check(tab1 is not null && tab1.AsSpan().SequenceEqual(texBytes), $"... and so does the work copy's tab ({Describe(tab1)})");
            // the next parts on their own (no other packfile with the same names open)
            CloseNew(shell, before);
            await ctx.SettleAsync();

            // ── a picked mesh replaced in its packfile, then removed ──
            string stalePath = MakeVpp(Path.Combine(root, "stale"), "stale_mod.vpp", ("guard_pc.v3c", pc), ("walk.rfa", walk));
            if (!ctx.Check(shell.OpenFile(stalePath) && shell.ActiveDocument is VppDocument, "stale_mod.vpp opens")) return;
            var staleMod = (VppDocument)shell.ActiveDocument!;
            await WaitAsync(() => ws.Packfiles.Libraries.Any(l => l.Key == staleMod.Id), 30);
            var staleClip = await OpenClipFrom(shell, staleMod, "walk.rfa");
            if (!ctx.Check(staleClip is not null, "its clip opens")) return;
            await WaitAsync(() => staleClip!.PreviewMeshOptions.Any(o => o.Source?.Key == staleMod.Id), 20);
            staleClip!.SelectedPreviewMesh = staleClip.PreviewMeshOptions.FirstOrDefault(o => o.Mesh.Name == "guard_pc.v3c" && o.Source?.Key == staleMod.Id);
            await WaitAsync(() => staleClip.Scene.MeshName == "guard_pc.v3c", 20);
            ctx.Check(staleClip.Scene.MeshName == "guard_pc.v3c" && staleClip.Scene.Skeleton.Count == 2, "the clip plays on its packfile's mesh (2 bones)");
            staleMod.ApplyEdit("replace mesh", p => Cairn.Vpp.Editing.VppEdit.Replace(p, "guard_pc.v3c", new MemorySource(ThreeBones(pc))));
            await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == staleMod.Id)?.FindMesh("guard_pc.v3c")?.BoneCount == 3, 20);
            await WaitAsync(() => staleClip.Scene.Skeleton.Count == 3, 10);
            await ctx.SettleAsync();
            var newMesh = ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == staleMod.Id)?.FindMesh("guard_pc.v3c");
            ctx.Check(staleClip.Scene.Skeleton.Count == 3 && newMesh is not null && ReferenceEquals(staleClip.SelectedPreviewMesh?.Mesh, newMesh),
                $"replaced in its packfile, the picked mesh plays as it is now ({staleClip.Scene.Skeleton.Count} bones; the picker shows the new entry: {ReferenceEquals(staleClip.SelectedPreviewMesh?.Mesh, newMesh)})");
            staleClip.SelectedPreviewMesh = staleClip.SelectedPreviewMesh;
            await Task.Delay(1000);
            await ctx.SettleAsync();
            ctx.Check(staleClip.Scene.Skeleton.Count == 3, $"picking the entry shown again plays the new content ({staleClip.Scene.Skeleton.Count} bones)");
            staleMod.ApplyEdit("remove mesh", p => Cairn.Vpp.Editing.VppEdit.Remove(p, ["guard_pc.v3c"]));
            await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == staleMod.Id) is { } l && l.FindMesh("guard_pc.v3c") is null, 20);
            await WaitAsync(() => staleClip.Scene.MeshName != "guard_pc.v3c", 10);
            await ctx.SettleAsync();
            ctx.Check(staleClip.PreviewLibraryMesh?.Location.DisplayLocation != "stale_mod.vpp" && staleClip.Scene.MeshName != "guard_pc.v3c"
                && staleClip.Scene.Notice?.Contains("no longer", StringComparison.Ordinal) == true,
                $"removed from its packfile, it stops playing and a notice says so (plays {staleClip.Scene.MeshName ?? "nothing"} from {staleClip.PreviewLibraryMesh?.Location.DisplayLocation ?? "nowhere"}; notice: {staleClip.Scene.Notice ?? "none"})");
            CloseNew(shell, before);
            await ctx.SettleAsync();

            // ── a mesh tab's preview clip replaced in its packfile, then removed ──
            string clipsPath = MakeVpp(Path.Combine(root, "clips"), "clips_mod.vpp", ("guard_pc.v3c", pc), ("walk.rfa", walk));
            if (!ctx.Check(shell.OpenFile(clipsPath) && shell.ActiveDocument is VppDocument, "clips_mod.vpp opens")) return;
            var clipsMod = (VppDocument)shell.ActiveDocument!;
            await WaitAsync(() => ws.Packfiles.Libraries.Any(l => l.Key == clipsMod.Id), 30);
            await clipsMod.Commands.OpenInCairnAsync(clipsMod.Current.Find("guard_pc.v3c")!);
            await WaitAsync(() => shell.ActiveDocument is MeshDocumentViewModel { DisplayName: "guard_pc.v3c" }, 20);
            if (!ctx.Check(shell.ActiveDocument is MeshDocumentViewModel, "Open in Cairn gives the mesh's tab")) return;
            var clipsTab = (MeshDocumentViewModel)shell.ActiveDocument!;
            await WaitAsync(() => clipsTab.PreviewClip is not null, 20);
            await ctx.SettleAsync();
            var walkRamp = clipsTab.PreviewClip?.RampIn;
            ctx.Check(clipsTab.SelectedPreviewClip?.Clip?.Name == "walk.rfa" && walkRamp is not null && walkRamp != 0,
                $"the mesh tab plays its packfile's walk.rfa (ramp-in {walkRamp})");
            var clipsLibrary = ws.Packfiles.Libraries.First(l => l.Key == clipsMod.Id);
            clipsMod.ApplyEdit("replace clip", p => Cairn.Vpp.Editing.VppEdit.Replace(p, "walk.rfa", new MemorySource(stand)));
            await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == clipsMod.Id) is { } l && !ReferenceEquals(l, clipsLibrary), 20);
            await WaitAsync(() => clipsTab.PreviewClip?.RampIn == 0, 10);
            await ctx.SettleAsync();
            var newClip = ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == clipsMod.Id)?.FindClip("walk.rfa");
            ctx.Check(clipsTab.PreviewClip?.RampIn == 0 && newClip is not null && ReferenceEquals(clipsTab.SelectedPreviewClip?.Clip, newClip),
                $"replaced in its packfile, the preview clip plays as it is now (ramp-in {clipsTab.PreviewClip?.RampIn}; the picker shows the new entry: {ReferenceEquals(clipsTab.SelectedPreviewClip?.Clip, newClip)})");
            clipsMod.ApplyEdit("remove clip", p => Cairn.Vpp.Editing.VppEdit.Remove(p, ["walk.rfa"]));
            await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == clipsMod.Id) is { } l && l.FindClip("walk.rfa") is null, 20);
            await WaitAsync(() => clipsTab.Scene.Notice?.Contains("no longer", StringComparison.Ordinal) == true, 10);
            await ctx.SettleAsync();
            ctx.Check(clipsTab.SelectedPreviewClip?.Clip?.Location.DisplayLocation != "clips_mod.vpp" && (clipsTab.PreviewClip is null) == (clipsTab.SelectedPreviewClip?.Clip is null)
                && clipsTab.Scene.Notice?.Contains("no longer", StringComparison.Ordinal) == true,
                $"removed from its packfile, it stops playing and a notice says so (picker {clipsTab.SelectedPreviewClip?.Label ?? "nothing"}; plays {(clipsTab.PreviewClip is null ? "nothing" : "a clip")}; notice: {clipsTab.Scene.Notice ?? "none"})");
        }
        finally
        {
            CloseNew(shell, before);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Same-name packfiles and an item's game (defects 5 and 6 of the L14 review): a mesh picked from one of two open
    /// packfiles of the same name stays shown as that one's after it changes; a Red Faction II character of a packfile
    /// stays refused under a Red Faction clip after the packfile changed (the item from before the change, as a library
    /// row, a drag or a context menu holds it).
    /// </summary>
    [SelfTest("integration.packfile-sources-names")]
    public static async Task PackfileSourcesNames(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var ws = RfaModule.Workspace;
        string root = Path.Combine(Path.GetTempPath(), "cairn-packsrc-" + Guid.NewGuid().ToString("N")[..8]);
        var before = shell.Documents.ToList();
        try
        {
            var (walk, _, pc) = Samples();

            // ── two packfiles named mod.vpp ──
            string a = MakeVpp(Path.Combine(root, "a"), "mod.vpp", ("guard_pc.v3c", pc), ("walk.rfa", walk));
            string b = MakeVpp(Path.Combine(root, "b"), "mod.vpp", ("guard_pc.v3c", pc), ("walk.rfa", walk));
            if (!ctx.Check(shell.OpenFile(a) && shell.ActiveDocument is VppDocument, "a\\mod.vpp opens")) return;
            var modA = (VppDocument)shell.ActiveDocument!;
            if (!ctx.Check(shell.OpenFile(b) && shell.ActiveDocument is VppDocument, "b\\mod.vpp opens")) return;
            var modB = (VppDocument)shell.ActiveDocument!;
            await WaitAsync(() => ws.Packfiles.Libraries.Count(l => l.Key == modA.Id || l.Key == modB.Id) == 2, 30);
            var clip = await OpenClipFrom(shell, modA, "walk.rfa");
            if (!ctx.Check(clip is not null, "a's clip opens")) return;
            await WaitAsync(() => clip!.PreviewMeshOptions.Count(o => o.Source is not null) >= 2, 20);
            var bOption = clip!.PreviewMeshOptions.FirstOrDefault(o => o.Source?.Key == modB.Id);
            if (!ctx.Check(bOption is not null, "the picker lists b's mesh")) return;
            clip.SelectedPreviewMesh = bOption;
            await WaitAsync(() => clip.PreviewLibraryMesh is { } m && bOption!.Source!.Contains(m), 20);
            await ctx.SettleAsync();
            ctx.Check(clip.SelectedPreviewMesh?.Source?.Key == modB.Id, "b's mesh picked: the picker shows b's entry");
            var oldB = ws.Packfiles.Libraries.First(l => l.Key == modB.Id);
            modB.ApplyEdit("add", p => Cairn.Vpp.Editing.VppEdit.AddSources(p, [("extra.rfa", (VppSource)new MemorySource(walk))], Cairn.Vpp.Editing.VppClashPolicy.KeepBoth).Package);
            await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == modB.Id) is { } l && !ReferenceEquals(l, oldB), 20);
            await Task.Delay(500);
            await ctx.SettleAsync();
            string shown = clip.SelectedPreviewMesh?.Source?.Key == modA.Id ? "a's" : clip.SelectedPreviewMesh?.Source?.Key == modB.Id ? "b's" : "none";
            ctx.Check(clip.SelectedPreviewMesh?.Source?.Key == modB.Id, $"after b changes, the picker still shows b's entry (shows {shown}, under '{clip.SelectedPreviewMesh?.Group}')");

            // ── a Red Faction II character under a Red Faction clip, held from before its packfile changed ──
            string mixedPath = MakeVpp(Path.Combine(root, "mixed"), "mixed.vpp", ("guard.rfc", SyntheticRf2Mesh.SampleCharacter()), ("walk.rfa", walk));
            if (!ctx.Check(shell.OpenFile(mixedPath) && shell.ActiveDocument is VppDocument, "mixed.vpp opens")) return;
            var mixed = (VppDocument)shell.ActiveDocument!;
            await WaitAsync(() => ws.Packfiles.Libraries.Any(l => l.Key == mixed.Id), 30);
            var mixedLibrary = ws.Packfiles.Libraries.First(l => l.Key == mixed.Id);
            var rfc = mixedLibrary.FindMesh("guard.rfc");
            if (!ctx.Check(rfc is not null && mixedLibrary.IsRedFactionII(rfc), "its guard.rfc is a Red Faction II character")) return;
            var rf1Clip = await OpenClipFrom(shell, mixed, "walk.rfa");
            if (!ctx.Check(rf1Clip is { IsRedFactionII: false }, "its Red Faction clip opens")) return;
            await ctx.SettleAsync();
            ctx.Check(ws.Library.GameMismatch(rfc, rf1Clip) is not null, "the Red Faction II character under the Red Faction clip is refused");
            mixed.ApplyEdit("add", p => Cairn.Vpp.Editing.VppEdit.AddSources(p, [("extra.rfa", (VppSource)new MemorySource(walk))], Cairn.Vpp.Editing.VppClashPolicy.KeepBoth).Package);
            await WaitAsync(() => ws.Packfiles.Libraries.FirstOrDefault(l => l.Key == mixed.Id) is { } l && !ReferenceEquals(l, mixedLibrary), 20);
            await ctx.SettleAsync();
            shell.ActiveDocument = rf1Clip;
            await ctx.SettleAsync();
            string? why = ws.Library.GameMismatch(rfc, rf1Clip);
            ctx.Check(why is not null && !ws.Library.PreviewCommand.CanExecute(rfc),
                $"the same character, held from before the packfile changed, is still refused ({why ?? "not refused"}; Preview enabled: {ws.Library.PreviewCommand.CanExecute(rfc)})");
            ws.Library.Activate(rfc, newTab: false);
            await WaitAsync(() => rf1Clip!.Scene.MeshName == "guard.rfc", 5);
            await ctx.SettleAsync();
            ctx.Check(rf1Clip!.Scene.MeshName != "guard.rfc", $"double-click on it does not put it under the Red Faction clip (the clip plays on {rf1Clip.Scene.MeshName ?? "nothing"})");
        }
        finally
        {
            CloseNew(shell, before);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static (byte[] Walk, byte[] Stand, byte[] Pc) Samples()
    {
        byte[] swing = SyntheticRf2Clip.SampleSwing();
        byte[] walk = RfaWriter.Write(RfaReader.Read(swing, "walk.rfa") with { Version = 8 });
        byte[] stand = RfaWriter.Write(RfaReader.Read(swing, "stand.rfa") with { Version = 8, RampIn = 0 });
        byte[] pc = LegacyMeshSupport.Convert(SyntheticRf2Mesh.SampleCharacter(), "guard.rfc").Bytes;
        return (walk, stand, pc);
    }

    private static string MakeVpp(string folder, string name, params (string Name, byte[] Bytes)[] entries)
    {
        Directory.CreateDirectory(folder);
        var package = Cairn.Vpp.Editing.VppEdit.AddSources(VppPackage.Empty,
            entries.Select(e => (e.Name, (VppSource)new MemorySource(e.Bytes))), Cairn.Vpp.Editing.VppClashPolicy.KeepBoth).Package;
        string path = Path.Combine(folder, name);
        Cairn.Vpp.Writing.VppSaver.Save(package, path, null, CancellationToken.None, Cairn.Vpp.Writing.VppSaveOptions.Default);
        return path;
    }

    /// <summary>The Red Faction mesh with a third bone: another character under the same name.</summary>
    private static byte[] ThreeBones(byte[] pc)
    {
        var file = V3dReader.Read(pc, "guard_pc.v3c");
        var section = file.BoneSection!;
        var first = section.Bones[0];
        var extra = new V3dBone(FixedString.FromText("extra-bone", first.Name.Length), first.Rotation, first.Position + new System.Numerics.Vector3(0, 0.5f, 0), 0);
        var sections = file.Sections.Select(s => ReferenceEquals(s, section) ? section with { Bones = section.Bones.Add(extra) } : s).ToImmutableArray();
        return V3dWriter.Write(file with { Sections = sections });
    }

    private static string? TextureOf(byte[] mesh, string name) =>
        V3dReader.Read(mesh, name).Submeshes.SelectMany(s => s.Lods).SelectMany(l => l.Textures).Select(t => t.FileName).FirstOrDefault(t => t.Length > 0);

    /// <summary>The bytes a resolver gives for <paramref name="name"/>, or null when it finds none or cannot read it.</summary>
    private static byte[]? ReadThrough(Cairn.Assets.AssetResolver? resolver, string name)
    {
        var location = resolver?.Resolve(name);
        if (location is null) return null;
        try { return location.ReadAllBytes(); }
        catch (IOException) { return null; }
    }

    /// <summary>Why the target of <paramref name="r"/> is alive (not async, so no local of a test's state machine holds it).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string RootOf(SelfTestContext ctx, WeakReference r) => r.Target is { } o ? ctx.DescribeRoot(o) : "  (collected meanwhile)";

    private static string Describe(byte[]? b) =>
        b is null ? "not found" : $"{b.Length} bytes, starting '{System.Text.Encoding.ASCII.GetString(b, 0, Math.Min(8, b.Length)).Replace('\0', '.')}'";

    /// <summary>The source keys of the snapshots the packfile sources keep (their private cache, read for the checks).</summary>
    private static List<string> CachedKeys(Cairn.Rfa.Ui.Services.PackfileSources sources)
    {
        var cache = (IDictionary)typeof(Cairn.Rfa.Ui.Services.PackfileSources).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sources)!;
        return [.. cache.Keys.Cast<ValueTuple<string, object>>().Select(k => k.Item1)];
    }

    private static async Task<ClipDocumentViewModel?> OpenClipFrom(ShellViewModel shell, VppDocument pack, string entry)
    {
        shell.ActiveDocument = pack;
        await pack.Commands.OpenInCairnAsync(pack.Current.Find(entry)!);
        await WaitAsync(() => shell.ActiveDocument is ClipDocumentViewModel c && c.DisplayName == entry, 20);
        return shell.ActiveDocument as ClipDocumentViewModel;
    }

    private static async Task CollectAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    /// <summary>Closes the tabs opened since <paramref name="before"/> (work copies and other tabs first, then packfiles).</summary>
    private static void CloseNew(ShellViewModel shell, IReadOnlyCollection<IDocument> before)
    {
        var added = shell.Documents.Where(d => !before.Contains(d)).ToList();
        foreach (var d in Enumerable.Reverse(added.Where(d => d is not VppDocument).ToList())) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
        foreach (var d in Enumerable.Reverse(added.OfType<VppDocument>().ToList())) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
    }
}
#endif
