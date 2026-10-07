using Cairn.Rfa.Ui.ViewModels;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Phase 5 Table usage panel self-tests (with the RFA024 identical-copy check), and the asset-loading
/// figures every diagnostic run logs.
/// </summary>
internal static class TableUsageSelfTests
{
    [SelfTest("tables", Order = 300)]
    public static async Task Tables(SelfTestContext ctx)
    {
        var model = ctx.Model;
        if (ctx.Clip is not { } clip)
        {
            ctx.Log("selftest tables: needs a clip document (a stock clip such as ult2_walk.rfa)");
            return;
        }
        var usage = model.Assets.Usage;
        if (usage.TableSources.Count == 0)
        {
            ctx.Log("selftest tables: no game tables are loaded (set the game directory); skipped");
            return;
        }
        var selectedTab = model.SelectedBottomTab;
        bool bottomVisible = model.IsBottomVisible;

        // The tab and its per-document view-model.
        var tab = model.BottomTabs.FirstOrDefault(t => t.Id == "tables");
        ctx.Check(tab is not null && tab.Header == "Table usage", "the bottom panel has a Table usage tab");
        var panel = TableUsageViewModel.For(clip)!;
        ctx.Check(ReferenceEquals(tab?.Content, panel) && ReferenceEquals(TableUsageViewModel.For(clip), panel),
            "the tab shows the active clip's own (lazily made, reused) view-model");
        ctx.Check(model.BottomTabs.All(t => t.EmptyText.Length > 0) && model.BottomTabs.Select(t => t.EmptyText).Distinct().Count() == model.BottomTabs.Count,
            "every bottom tab has its own empty text");

        // Clip document: every table line, the meshes that play it, copy.
        var uses = usage.UsagesOf(clip.DisplayName);
        var usageRows = panel.Roots.SelectMany(r => r.Children).Where(n => n.Kind == TableUsageNodeKind.Usage).ToList();
        ctx.Check(uses.Count > 0 && usageRows.Count == uses.Count, $"{clip.DisplayName}: {usageRows.Count} usage rows for {uses.Count} table lines");
        ctx.Check(usageRows.Any(n => n.Usage!.Table == ClipUsageIndex.EntityTable && n.Subtitle.Contains("entity.tbl", StringComparison.Ordinal)),
            $"the rows name entity.tbl lines ('{usageRows.FirstOrDefault()?.Title} {usageRows.FirstOrDefault()?.Subtitle}')");
        var meshRows = panel.Roots.SelectMany(r => r.Children).Where(n => n.Kind == TableUsageNodeKind.Mesh).ToList();
        ctx.Check(meshRows.Count == usage.MeshesForClip(clip.DisplayName).Count && meshRows.Count > 0
            && meshRows.All(m => m.Mesh is null || m.Badge == $"{m.Mesh.BoneCount} bones"),
            $"the meshes that play it are listed with their library bone counts ({meshRows.Count})");
        ctx.Check(meshRows.All(m => m.IsWarning == (m.Mesh is null || m.Mesh.BoneCount != clip.Current.BoneCount)),
            "a mesh whose bone count differs from the clip's (or that is missing) is flagged");
        ctx.Check(panel.Summary.Contains(clip.DisplayName, StringComparison.Ordinal) && panel.EmptyText.Length == 0 && !panel.IsEmpty,
            $"summary: '{panel.Summary}'");

        var first = usageRows[0].Usage!;
        string line = panel.BuildCopyText(usageRows[0]) ?? "";
        ctx.Check(line == TblSnippet.Line(first.Kind, first.SlotName, clip.DisplayName, first.Sound, panel.Style),
            $"copy line is TblSnippet.Line ('{line.Replace("\t", "\\t", StringComparison.Ordinal)}')");
        var previous = panel.Style;
        panel.Style = TblSnippetStyle.Weapon;
        string block = panel.BuildCopyText(panel.Roots[0]) ?? "";
        ctx.Check(block == TblSnippet.Block(uses.Select(u => new TblSnippetEntry(u.Kind, u.SlotName, clip.DisplayName, u.Sound, u.WeaponBlock)), TblSnippetStyle.Weapon)
            && !block.StartsWith('\t'), "copying the group gives the whole block, in the chosen (weapons.tbl) layout");
        panel.Style = previous;

        // The empty state for a clip no table names.
        const string unused = "ult2_foo_never_used.rfa";
        string text = TableUsageViewModel.UnusedClipText(unused);
        ctx.Check(!usage.IsUsed(unused) && text.StartsWith($"No table plays {unused}", StringComparison.Ordinal)
            && text.Contains("only plays clips that a table names", StringComparison.Ordinal), $"unused clip: '{text[..60]}…'");

        // RFA024: the loose stock copy is byte-identical to the stock clip the library sees, so it is not a collision.
        // The context rules run off the UI thread after the library/index loads: until that lint (a BusyTracker token)
        // lands, the diagnostics come from the previous context. Wait for the indexes and for all pending work.
        await model.Assets.ArchivesIndexed;
        for (var start = Environment.TickCount64; Environment.TickCount64 - start < 20_000
            && (Cairn.Ui.Services.BusyTracker.Count > 0 || Cairn.Ui.Services.BusyTracker.QuietMilliseconds < 300);)
            await Task.Delay(50);
        var copies = model.Assets.Snapshot.CopiesOfClip(clip.DisplayName);
        ctx.Check(!clip.Diagnostics.Any(d => d.Code == ClipRules.NameCollision),
            $"RFA024 is not reported for {clip.DisplayName} ({copies.Count} other copy/copies visible, identical ones are the same clip)");

        // Mesh document: its classes and their clips; then close it again.
        string? folder = clip.FilePath is { } path ? Path.GetDirectoryName(path) : null;
        string? meshPath = folder is null ? null : Path.Combine(folder, "ult2_guard.v3c");
        if (meshPath is not null && File.Exists(meshPath) && model.DocumentAt(meshPath) is null)
        {
            var mesh = model.OpenFile(meshPath) as MeshDocumentViewModel;
            await ctx.SettleAsync();
            if (mesh is not null)
            {
                var meshPanel = TableUsageViewModel.For(mesh)!;
                var clipRows = meshPanel.Roots.SelectMany(r => r.Children).Where(n => n.Kind == TableUsageNodeKind.Clip).ToList();
                ctx.Check(meshPanel.Roots.Count > 0 && clipRows.Count > 0 && clipRows.All(n => n.Usage is not null),
                    $"{mesh.DisplayName}: {meshPanel.Roots.Count} class list(s), {clipRows.Count} clips ('{meshPanel.Summary}')");
                ctx.Check(clipRows.Any(n => n.Clip is not null && n.Badge.EndsWith("bones", StringComparison.Ordinal)),
                    "the clips carry the library's copy and bone count (double-click opens, the menu previews)");
                var row = clipRows.First(n => n.Usage is not null);
                ctx.Check(meshPanel.BuildCopyText(row) == TblSnippet.Line(row.Usage!.Kind, row.Usage.SlotName, row.Usage.Clip.Original, row.Usage.Sound, meshPanel.Style),
                    "a mesh's clip row copies its own table line");
                model.CloseDocument(mesh);
                await ctx.SettleAsync();
            }
            ctx.Check(mesh is not null && !model.Documents.Contains(mesh), "the mesh document opened and closed again");
        }
        else
        {
            ctx.Log("selftest tables: ult2_guard.v3c is not beside the clip (or already open); mesh checks skipped");
        }

        model.ActiveDocument = clip;
        model.SelectedBottomTab = selectedTab;
        model.IsBottomVisible = bottomVisible;
    }

    /// <summary>Logs how the library build went: its phases and the archive directories read from disk.</summary>
    [ScreenshotStep(5)]
    public static Task LogAssetLoading(ScreenshotContext context)
    {
        var assets = context.Model.Assets;
        var (reads, repeats) = assets.Resolver.ArchiveDirectoryReads;
        context.Log($"assets: {reads} archive directory reads ({repeats} of an archive read before)");
        foreach (string line in assets.BuildLog) context.Log("assets: " + line);
        foreach (var viewport in Viewport.ViewportControl.Visible())
            context.Log($"layout: 3D view {viewport.ActualWidth:0}x{viewport.ActualHeight:0} in a {context.Window.ActualWidth:0}x{context.Window.ActualHeight:0} window");
        return Task.CompletedTask;
    }
}
