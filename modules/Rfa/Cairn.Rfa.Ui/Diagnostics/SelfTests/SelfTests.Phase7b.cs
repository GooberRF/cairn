using System.Windows.Controls;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Views;
using Cairn.Assets;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of the phase 7b fixes: one selection across the viewport, the mesh structure tree and its
/// editor; a bone picked in a clip's viewport bringing up the Bone inspector (but not over the Key tab
/// in use) and revealing its timeline row; and the library's double-click (preview on the document in
/// front, a new tab when the bone count does not fit, Ctrl / the setting always a new tab). Uses the
/// active clip document and the research corpus's ult2_guard.v3c; closes what it opens.
/// </summary>
internal static class Phase7bSelfTests
{
    [SelfTest("selection-sync", Order = 660)]
    public static async Task SelectionSync(SelfTestContext ctx)
    {
        var model = ctx.Model;
        var start = model.ActiveDocument;
        var opened = new List<DocumentViewModel>();
        try
        {
            if (start is ClipDocumentViewModel clip) await ClipPickChecks(ctx, clip);

            string? corpus = MeshEditingSelfTests.Corpus();
            string? path = corpus is null ? null : Path.Combine(corpus, "ult2_guard.v3c");
            if (path is null || !File.Exists(path))
            {
                ctx.Log("selftest selection-sync: the research corpus (ult2_guard.v3c) is absent; mesh checks skipped");
                return;
            }
            bool wasOpen = model.DocumentAt(path) is not null;
            if (model.OpenFile(path) is not MeshDocumentViewModel mesh)
            {
                ctx.Check(false, "ult2_guard.v3c opens as a mesh document");
                return;
            }
            if (!wasOpen) opened.Add(mesh);
            model.ActiveDocument = mesh;
            model.IsInspectorVisible = true;
            await ctx.SettleAsync();

            // Something else selected first: a sphere node, then the viewport picks a bone deep in the hierarchy.
            mesh.Structure.Reveal(new MeshNodeRef(MeshNodeKind.CollisionSphere, -1, -1, 0));
            int bone = DeepestBone(mesh);
            mesh.Selection.Select(bone);
            mesh.Scene.NotifyBonePicked(bone);
            await ctx.SettleAsync();
            var selected = mesh.Structure.Selected;
            ctx.Check(selected?.Node is { Kind: MeshNodeKind.Bone } r && r.Index == bone && mesh.Structure.HasEditor,
                $"a bone picked in the viewport selects its structure node and shows its editor (bone {bone}, node '{selected?.Header}', editor {mesh.Structure.Editor?.GetType().Name})");
            ctx.Check(mesh.SelectedInspectorTab?.Id == "structure", "the Structure tab is in front after the pick");
            var view = TimelineSelfTests.FindVisible<MeshStructureView>(ctx.Window);
            var tree = view?.FindName("Tree") as TreeView;
            ctx.Check(tree is not null && ReferenceEquals(tree.SelectedItem, selected),
                $"the tree's own selection is that node, its ancestors expanded and the item realised ({(tree?.SelectedItem as MeshNodeViewModel)?.Header ?? "none"})");

            // Clicking empty space clears the bone selection: the bone node lets go too.
            mesh.Selection.Select(-1);
            await ctx.YieldAsync();
            ctx.Check(mesh.Structure.Selected is null && !mesh.Structure.HasEditor, "clearing the viewport's bone selection deselects the bone node");

            // A tree click on a sphere takes the bone selection away (one selection).
            mesh.Selection.Select(bone);
            mesh.Structure.Reveal(new MeshNodeRef(MeshNodeKind.CollisionSphere, -1, -1, 0));
            ctx.Check(mesh.Selection.Count == 0 && mesh.Structure.Selected?.Node?.Kind == MeshNodeKind.CollisionSphere,
                "selecting a sphere node in the tree clears the viewport's bone selection");

            // Selecting a bone node in the tree selects it in the viewport.
            mesh.Structure.Reveal(new MeshNodeRef(MeshNodeKind.Bone, -1, -1, 1));
            ctx.Check(mesh.Selection.Active == 1 && mesh.Selection.Count == 1, "selecting a bone node selects that bone in the viewport");
            mesh.Selection.Select(-1);
        }
        finally
        {
            foreach (var d in opened) model.CloseDiscarding(d);
            if (start is not null && model.Documents.Contains(start)) model.ActiveDocument = start;
            await ctx.SettleAsync();
        }
    }

    private static int DeepestBone(DocumentViewModel doc)
    {
        var parents = doc.Scene.Skeleton.EffectiveParents;
        int best = 0, bestDepth = -1;
        for (int i = 0; i < parents.Length; i++)
        {
            int depth = 0;
            for (int p = parents[i]; p >= 0 && depth <= parents.Length; p = parents[p]) depth++;
            if (depth > bestDepth)
            {
                best = i;
                bestDepth = depth;
            }
        }
        return best;
    }

    private static async Task ClipPickChecks(SelfTestContext ctx, ClipDocumentViewModel doc)
    {
        var savedTab = doc.SelectedInspectorTab;
        var savedKeys = doc.KeySelection;
        var savedBones = doc.Selection.Bones.ToList();
        try
        {
            if (doc.FittingSkeleton is null || doc.Current.BoneCount < 2)
            {
                ctx.Log("selftest selection-sync: the clip has no fitting preview mesh; clip pick checks skipped");
                return;
            }
            int bone = DeepestBone(doc);
            doc.SelectedInspectorTab = doc.InspectorTabs.First(t => t.Id == "clip");
            doc.SetKeySelection(KeySelection.Empty);
            // Collapse every ancestor row so the pick has to reveal it.
            var parents = doc.Scene.Skeleton.EffectiveParents;
            for (int p = parents[bone]; p >= 0; p = parents[p])
            {
                if (!doc.Timeline.IsCollapsed(p)) doc.Timeline.ToggleExpanded(p);
            }
            bool hiddenBefore = doc.Timeline.VisibleRows.All(r => r.Bone != bone);
            doc.Selection.Select(bone);
            doc.Scene.NotifyBonePicked(bone);
            await ctx.YieldAsync();
            ctx.Check(doc.SelectedInspectorTab?.Id == "bone", $"a bone picked in a clip's viewport brings the Bone tab forward (tab '{doc.SelectedInspectorTab?.Header}')");
            ctx.Check(hiddenBefore && doc.Timeline.VisibleRows.Any(r => r.Bone == bone), "the picked bone's timeline row is revealed (collapsed ancestors expand)");

            // Working in the Key tab with keys selected: a pick must not take the tab away.
            var track = doc.Current.Bones[bone];
            if (track.RotationKeys.Length > 0)
            {
                doc.SetKeySelection(KeySelection.Of(new KeyRef(bone, KeyKind.Rotation, 0)));
                doc.SelectedInspectorTab = doc.InspectorTabs.First(t => t.Id == "key");
                doc.Selection.Select(parents[bone] >= 0 ? parents[bone] : bone);
                doc.Scene.NotifyBonePicked(doc.Selection.Active);
                ctx.Check(doc.SelectedInspectorTab?.Id == "key", "with keys selected in the Key tab, a pick leaves the Key tab in front");
            }
        }
        finally
        {
            doc.Timeline.ExpandAllCommand.Execute(null);
            doc.SetKeySelection(savedKeys);
            doc.Selection.Set(savedBones);
            doc.SelectedInspectorTab = savedTab;
        }
    }

    /// <summary>
    /// Screenshot switches: <c>--pick-bone &lt;name&gt;</c> does what a click on that bone in the viewport does
    /// (selection, editor, tree / timeline reveal); <c>--activate &lt;library entry&gt;</c> does what a double-click
    /// on that clip or mesh in the library does (with <c>--activate-new-tab on</c>: Ctrl+double-click).
    /// </summary>
    [ScreenshotStep(40)]
    public static async Task Screenshot(ScreenshotContext ctx)
    {
        string? pick = ctx.Extra("pick-bone"), activate = ctx.Extra("activate"), newTab = ctx.Extra("activate-new-tab");
        if (activate is not null)
        {
            var snapshot = ctx.Model.Assets.Snapshot;
            object? item = (object?)snapshot.FindClip(activate) ?? snapshot.FindMesh(activate);
            if (item is null) ctx.Log($"--activate: '{activate}' is not in the library");
            else
            {
                ctx.Model.Library.Activate(item, newTab: newTab is "on" or "true");
                await ctx.SettleAsync();
                ctx.Log($"--activate {activate}: active document {ctx.Model.ActiveDocument?.DisplayName}; status '{ctx.Model.StatusMessage}'");
            }
        }
        if (pick is not null && ctx.Model.ActiveDocument is { } doc)
        {
            int bone = doc.Scene.Skeleton.IndexOf(pick);
            if (bone < 0)
            {
                ctx.Log($"--pick-bone: '{pick}' is not a bone of {doc.Scene.MeshName}");
                return;
            }
            ctx.Model.IsInspectorVisible = true;
            doc.Selection.Select(bone);
            doc.Scene.NotifyBonePicked(bone);
            await ctx.SettleAsync();
            ctx.Log($"--pick-bone {pick}: inspector tab {doc.SelectedInspectorTab?.Header}"
                + (doc is MeshDocumentViewModel m ? $"; structure node '{m.Structure.Selected?.Header}', editor {m.Structure.Editor?.GetType().Name}" : string.Empty));
        }
    }

    [SelfTest("library-activate", Order = 670)]
    public static async Task LibraryActivate(SelfTestContext ctx)
    {
        var model = ctx.Model;
        var library = model.Library;
        var snapshot = model.Assets.Snapshot;
        var start = model.ActiveDocument;
        var before = model.Documents.ToList();
        string? savedSetting = model.Settings.Get<string>(LibraryViewModel.DoubleClickSettingKey);
        try
        {
            if (start is not ClipDocumentViewModel clipDoc || clipDoc.PreviewLibraryMesh is not { } ownMesh)
            {
                ctx.Log("selftest library-activate: needs a clip document with a library preview mesh; skipped");
                return;
            }
            int bones = clipDoc.Current.BoneCount;
            model.Settings.Set(LibraryViewModel.DoubleClickSettingKey, "preview");
            ctx.Check(library.DoubleClickPreviews && library.DoubleClickHint.Contains(clipDoc.DisplayName, StringComparison.Ordinal),
                $"the library hint says what double-click does now ('{library.DoubleClickHint}')");

            // A clip document in front: a fitting mesh becomes its preview mesh, no new tab.
            var otherFitting = snapshot.Meshes.FirstOrDefault(m => m.HasSkeleton && m.BoneCount == bones
                && !string.Equals(m.Name, ownMesh.Name, StringComparison.OrdinalIgnoreCase));
            if (otherFitting is not null)
            {
                library.Activate(otherFitting, newTab: false);
                await ctx.SettleAsync();
                ctx.Check(model.Documents.Count == before.Count && ReferenceEquals(model.ActiveDocument, clipDoc)
                    && string.Equals(clipDoc.PreviewLibraryMesh?.Name, otherFitting.Name, StringComparison.OrdinalIgnoreCase),
                    $"double-clicking a fitting mesh ({otherFitting.Name}) previews the clip on it without a new tab");
                library.Activate(ownMesh, newTab: false);
                await ctx.SettleAsync();
            }

            // A mesh that does not fit opens in a new tab and says why.
            var misfit = snapshot.Meshes.FirstOrDefault(m => m.HasSkeleton && m.IsReadable && m.BoneCount != bones);
            if (misfit is not null)
            {
                library.Activate(misfit, newTab: false);
                await ctx.SettleAsync();
                var tab = model.Documents.Except(before).OfType<MeshDocumentViewModel>().FirstOrDefault();
                ctx.Check(tab is not null && model.StatusMessage.Contains("bones", StringComparison.Ordinal),
                    $"a mesh with another bone count ({misfit.Name}, {misfit.BoneCount}) opens in a new tab and the status bar says why ('{model.StatusMessage}')");

                // With that mesh in front: a fitting clip plays on it, no new tab.
                if (tab is not null && snapshot.Clips.FirstOrDefault(c => c.IsReadable && c.BoneCount == misfit.BoneCount) is { } fittingClip)
                {
                    int count = model.Documents.Count;
                    model.ActiveDocument = tab;
                    library.Activate(fittingClip, newTab: false);
                    await ctx.SettleAsync();
                    ctx.Check(model.Documents.Count == count && ReferenceEquals(tab.SelectedPreviewClip?.Clip, fittingClip) && tab.Playback.IsPlaying,
                        $"with a mesh in front, double-clicking a fitting clip ({fittingClip.Name}) plays it there without a new tab");
                    tab.Playback.Pause();
                    // A clip that does not fit that mesh opens in a new tab.
                    if (snapshot.Clips.FirstOrDefault(c => c.IsReadable && c.BoneCount != misfit.BoneCount) is { } misfitClip)
                    {
                        library.Activate(misfitClip, newTab: false);
                        await ctx.SettleAsync();
                        ctx.Check(model.Documents.Count == count + 1 && model.ActiveDocument is ClipDocumentViewModel,
                            $"a clip with another bone count ({misfitClip.Name}) opens in a new tab instead");
                    }
                }
                model.ActiveDocument = clipDoc;
                await ctx.SettleAsync();
            }

            // A clip document in front: another clip opens in a new tab, previewed on the same mesh when it fits.
            var sibling = snapshot.Clips.FirstOrDefault(c => c.IsReadable && c.BoneCount == bones
                && c.Location.FilePath is null && model.Documents.All(d => d.DisplayName != c.Name) && c.Name != clipDoc.DisplayName);
            if (sibling is not null)
            {
                int count = model.Documents.Count;
                library.Activate(sibling, newTab: false);
                await ctx.SettleAsync();
                var opened = model.ActiveDocument as ClipDocumentViewModel;
                ctx.Check(model.Documents.Count == count + 1 && opened is not null && opened.DisplayName == sibling.Name
                    && string.Equals(opened.Scene.MeshName, clipDoc.Scene.MeshName, StringComparison.OrdinalIgnoreCase),
                    $"with a clip in front, another clip ({sibling.Name}) opens in a new tab previewed on the same mesh ({opened?.Scene.MeshName} vs {clipDoc.Scene.MeshName})");
                model.ActiveDocument = clipDoc;
            }

            // Ctrl (newTab) and the "always open in a new tab" setting.
            int n = model.Documents.Count;
            if (otherFitting is not null)
            {
                library.Activate(otherFitting, newTab: true);
                await ctx.SettleAsync();
                ctx.Check(model.Documents.Count == n + 1 && model.ActiveDocument is MeshDocumentViewModel,
                    "Ctrl+double-click opens a fitting mesh in a new tab instead of previewing");
                model.ActiveDocument = clipDoc;
                model.Settings.Set(LibraryViewModel.DoubleClickSettingKey, "newTab");
                library.OnDoubleClickSettingChanged();
                ctx.Check(!library.DoubleClickPreviews && library.DoubleClickHint.Contains("Settings", StringComparison.Ordinal),
                    "the setting switches double-click to always open (and the hint says so)");
                var preview = clipDoc.PreviewLibraryMesh;
                int m = model.Documents.Count;
                library.Activate(ownMesh, newTab: false);
                await ctx.SettleAsync();
                ctx.Check(ReferenceEquals(clipDoc.PreviewLibraryMesh, preview) && (model.Documents.Count == m + 1 || model.DocumentAt(ownMesh.Location.FilePath) is not null || ownMesh.Location.FilePath is null),
                    "with the setting on, double-clicking a fitting mesh opens it instead of previewing");
            }
        }
        finally
        {
            model.Settings.Set(LibraryViewModel.DoubleClickSettingKey, savedSetting);
            library.OnDoubleClickSettingChanged();
            foreach (var d in model.Documents.Except(before).ToList()) model.CloseDiscarding(d);
            if (start is not null && model.Documents.Contains(start)) model.ActiveDocument = start;
            await ctx.SettleAsync();
        }
    }
}
