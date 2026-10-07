using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.ClipTools;
using Cairn.Rfa.Ui.Views.Dialogs.ClipTools;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of the Clip menu tools (phase 5): each tool's view-model is driven without a window — its
/// preview must equal the direct Core call, the document must stay untouched while previewing, OK must
/// add exactly one undo step with the expected label, closing must clear the preview, and undo must
/// restore the clip. Also: cancel, a refusal shown inline, the real window closing, and Compare With's
/// ghost. Leaves the document (clip, selection, comparison) as it found it.
/// </summary>
internal static class ClipToolSelfTests
{
    [SelfTest("tools", Order = 500)]
    public static async Task Tools(SelfTestContext ctx)
    {
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("selftest tools: needs a clip document");
            return;
        }
        await ctx.SettleAsync();
        var original = doc.Current;
        var savedSelection = doc.Selection.Bones.ToList();
        var savedCompare = doc.Compare.Capture();
        var unit = ctx.Model.TimeUnit;
        try
        {
            await RunAll(ctx, doc, unit);
        }
        finally
        {
            doc.SetPreviewClip(null);
            doc.Selection.Set(savedSelection);
            doc.Compare.Restore(savedCompare);
        }
        ctx.Check(ReferenceEquals(doc.Current, original) && doc.Scene.PreviewClip is null, "tools: the document is left as it was found");
    }

    private static async Task RunAll(SelfTestContext ctx, ClipDocumentViewModel doc, TimeUnit unit)
    {
        var c = doc.Current;
        var skeleton = doc.FittingSkeleton;
        ctx.Log($"selftest tools: {doc.DisplayName}, {c.BoneCount} bones, {c.StartTime}–{c.EndTime}, skeleton {(skeleton is null ? "none" : doc.Scene.MeshName)}");
        ctx.Check(ctx.Model.ClipTools.TrimCommand.CanExecute(null) && ctx.Model.ClipTools.CompareCommand.CanExecute(null), "tools: Clip menu commands are enabled for a clip document");
        int frame = RfaClip.TicksPerFrame;
        int quarter = Math.Max(frame, c.Duration / 4 / frame * frame);
        int from = c.StartTime + quarter, to = c.EndTime - quarter;
        string N(int ticks) => TimeFormat.Number(ticks, unit);
        string S = TimeFormat.Suffix(unit);
        double U(int ticks) => TimeFormat.ToUnit(ticks, unit);

        // ── Time ─────────────────────────────────────────────────────────────
        await Run(ctx, doc, () => new TrimToolViewModel(doc), vm => { vm.From = U(from); vm.To = U(to); },
            (x, _) => ClipEdit.Trim(x, from, to), $"Trim to {N(from)}–{N(to)}");
        await Run(ctx, doc, () => new ShiftToolViewModel(doc), vm => vm.Delta = U(2 * frame),
            (x, _) => ClipEdit.Shift(x, 2 * frame), $"Shift by +{N(2 * frame)} {S}");
        await Run(ctx, doc, () => new RetimeToolViewModel(doc), vm => vm.Percent = 150,
            (x, _) => ClipEdit.Retime(x, 1.5, x.StartTime), "Retime ×1.5");
        await Run(ctx, doc, () => new RetimeToolViewModel(doc), vm => { vm.IsToLength = true; vm.Length = U(c.Duration / 2 / frame * frame); vm.IsPivotEnd = true; },
            (x, _) => ClipEdit.Retime(x, (c.Duration / 2 / frame * frame) / (double)x.Duration, x.EndTime), $"Retime to {N(c.Duration / 2 / frame * frame)} {S}");
        await Run(ctx, doc, () => new ReverseToolViewModel(doc), _ => { }, (x, _) => ClipEdit.Reverse(x), "Reverse clip");

        // Recompute start/end needs a range that does not fit the keys: make one (its own undo step), then undo it.
        if (doc.Apply("Self-test: widen the range", x => ClipEdit.SetHeader(x, new ClipHeaderChange { EndTime = x.EndTime + 4 * frame })))
        {
            await Run(ctx, doc, () => new RecomputeRangeToolViewModel(doc), _ => { }, (x, _) => ClipEdit.RecomputeRange(x), "Recompute start/end");
            doc.Undo();
        }
        await NoChange(ctx, doc, new RecomputeRangeToolViewModel(doc), "range on a clip whose keys already span it");

        // ── Motion ───────────────────────────────────────────────────────────
        // Precondition: the 4-frame blend changes this clip. A clip that already loops with no key inside the
        // window (the generated sample walk) is left as it is by Core (Cairn.Rfa.Tests.EditingSampleLoopTests).
        if (SameBytes(ClipEdit.MakeLoopable(doc.Current, 4 * frame, LoopMode.BlendEndToStart), doc.Current))
            ctx.Log("loop: skipped, the clip already loops and no key lies inside the 4-frame blend window, so Make loopable has nothing to change");
        else await Run(ctx, doc, () => new LoopToolViewModel(doc), vm => vm.BlendWindow = U(4 * frame),
            (x, vm) => ClipEdit.MakeLoopable(x, 4 * frame, LoopMode.BlendEndToStart, vm.RootBone >= 0 ? new LoopOptions(vm.RootBone, vm.KeepAxes) : null),
            $"Make loopable (blend {N(4 * frame)} {S})");
        await Run(ctx, doc, () => new ResampleToolViewModel(doc), vm => vm.Fps = 15,
            (x, _) => ClipEdit.Resample(x, 320), "Resample at 15 fps");
        await Run(ctx, doc, () => new ReduceToolViewModel(doc), vm => vm.RotationTolerance = 0.5,
            (x, vm) => ClipEdit.ReduceKeys(x, vm.Options).Clip, "Reduce keys (0.5°)");
        await Run(ctx, doc, () =>
        {
            var vm = new MirrorToolViewModel(doc);
            if (vm.HasNames) ctx.Check(vm.Rows.Count(r => r.IsPaired) >= 2, $"mirror: pairs detected from the bone names ({vm.Rows.Count(r => r.IsPaired)} paired bones)");
            // Editing a row re-pairs both bones.
            if (vm.Rows.FirstOrDefault(r => r.IsPaired) is { } row)
            {
                int partner = row.Partner.Bone;
                row.Partner = row.Options[0];
                ctx.Check(!vm.Pairs.IsPaired(row.Bone) && !vm.Pairs.IsPaired(partner), "mirror: setting a partner to centre unpairs both bones");
                row.Partner = row.Options[partner + 1];
                ctx.Check(vm.Pairs.PartnerOf(row.Bone) == partner && vm.Pairs.PartnerOf(partner) == row.Bone, "mirror: picking a partner pairs both ways");
            }
            return vm;
        }, _ => { }, (x, vm) => ClipEdit.MirrorClip(x, vm.Pairs, vm.Options), "Mirror left/right");

        // ── Bones ────────────────────────────────────────────────────────────
        int bone = PickBone(doc, c);
        doc.Selection.Set([bone]);
        await Run(ctx, doc, () => new OffsetToolViewModel(doc), vm => { vm.Yaw = 20; vm.MoveY = 0.05; },
            (x, vm) => ClipEdit.OffsetBones(x, [bone], vm.Offset), $"Offset {doc.BoneDisplayName(bone)}");
        await Run(ctx, doc, () => new OffsetToolViewModel(doc), vm => { vm.Pitch = -15; vm.UseRange = true; vm.From = U(from); vm.To = U(to); vm.Falloff = U(2 * frame); },
            (x, vm) => ClipEdit.OffsetBones(x, [bone], vm.Offset), $"Offset {doc.BoneDisplayName(bone)}");
        await Run(ctx, doc, () => new RootMotionToolViewModel(doc), _ => { },
            (x, vm) => ClipEdit.RemoveRootMotion(x, vm.SelectedRoot.Value, RootMotionAxes.X | RootMotionAxes.Z), "Remove root motion (X, Z)", allowNoChange: true);
        await Run(ctx, doc, () => new RootMotionToolViewModel(doc), vm => { vm.IsScale = true; vm.ScaleZ = 0.5; },
            (x, vm) => ClipEdit.ScaleRootMotion(x, vm.SelectedRoot.Value, new System.Numerics.Vector3(1, 1, 0.5f)), "Scale root motion (1, 1, 0.5)", allowNoChange: true);
        if (skeleton is not null)
        {
            await Run(ctx, doc, () => new BoneLengthsToolViewModel(doc), vm => vm.IsFromBind = true,
                (x, _) => ClipEdit.SetBoneLengthsFromBind(x, skeleton), "Set bone lengths from the bind pose", allowNoChange: true);
            var lengths = new BoneLengthsToolViewModel(doc);
            if (lengths.References.Selected is { } reference)
            {
                var referenceClip = await ctx.Model.Assets.LoadClipAsync(reference.Clip);
                lengths.End();
                await Run(ctx, doc, () => new BoneLengthsToolViewModel(doc), vm => vm.References.Selected = reference,
                    (x, _) => ClipEdit.SetBoneLengthsFromClip(x, referenceClip, skeleton.Parents), $"Set bone lengths from {reference.Clip.Name}", allowNoChange: true);
            }
            else
            {
                lengths.End();
                ctx.Log("selftest tools: no compatible reference clip in the library; bone lengths from a clip skipped");
            }
        }
        else
        {
            var lengths = new BoneLengthsToolViewModel(doc);
            await lengths.SettleAsync();
            ctx.Check(lengths.Error is { Length: > 0 } && !lengths.CanApply, $"lengths: without a fitting preview mesh the dialog explains why ('{lengths.Error}')");
            lengths.End();
        }

        var conform = new ConformToolViewModel(doc);
        var target = conform.Meshes.All.FirstOrDefault(m => m.Mesh.BoneCount != c.BoneCount)
            ?? conform.Meshes.All.FirstOrDefault(m => !string.Equals(m.Mesh.Name, doc.Scene.MeshName, StringComparison.OrdinalIgnoreCase));
        conform.End();
        if (doc.ClipboardBoneNames is { } names && target is not null)
        {
            var targetSkeleton = Skeleton.FromFile(await ctx.Model.Assets.LoadMeshAsync(target.Mesh));
            await Run(ctx, doc, () => new ConformToolViewModel(doc), vm => vm.Meshes.Selected = target,
                (x, _) => ClipEdit.ConformToSkeleton(x, names, targetSkeleton).Clip, $"Conform to {target.Mesh.Name}");
        }
        else
        {
            var vm = new ConformToolViewModel(doc);
            if (target is not null) vm.Meshes.Selected = target;
            vm.RecomputeNow();
            await vm.SettleAsync();
            ctx.Check(doc.ClipboardBoneNames is null ? vm.Error is { Length: > 0 } && !vm.CanApply : !vm.CanApply,
                $"conform: without bone names (or a target) the dialog explains why ('{vm.Error ?? vm.SummaryLines.FirstOrDefault()}')");
            vm.End();
        }
        await ConformQuickFixChecks(ctx, doc, target);

        await Run(ctx, doc, () => new WeightsToolViewModel(doc), vm => { vm.Target = skeleton is not null ? WeightTarget.UpperBody : WeightTarget.All; vm.Weight = 5; },
            (x, vm) => ClipEdit.SetBoneWeights(x, vm.TargetBones(), 5f), skeleton is not null ? "Set weights (upper body → 5)" : "Set weights (all bones → 5)");
        await Run(ctx, doc, () => new NormalizeToolViewModel(doc), _ => { },
            (x, vm) => ClipEdit.Normalize(x, vm.Options), "Normalise", allowNoChange: true);
        // Keys outside [start, end] give Normalise something to repair: make that (its own step), then undo it.
        if (doc.Apply("Self-test: shrink the range", x => ClipEdit.SetHeader(x, new ClipHeaderChange { EndTime = x.EndTime - 4 * frame })))
        {
            await Run(ctx, doc, () =>
            {
                var vm = new NormalizeToolViewModel(doc);
                vm.Fixes.First(f => f.Id == "signs").IsEnabled = false;
                return vm;
            }, _ => { }, (x, vm) => ClipEdit.Normalize(x, vm.Options), "Normalise");
            doc.Undo();
        }

        // ── Cancel, refusal, the real window ─────────────────────────────────
        {
            var before = doc.Current;
            int undo = doc.History.UndoLabels.Count;
            var vm = new ShiftToolViewModel(doc) { Delta = U(-frame) };
            vm.RecomputeNow();
            await vm.SettleAsync();
            bool previewing = doc.Scene.PreviewClip is not null && doc.Playback.StartTime == before.StartTime - frame;
            vm.End();
            ctx.Check(previewing && doc.Scene.PreviewClip is null && ReferenceEquals(doc.Current, before) && doc.History.UndoLabels.Count == undo
                && doc.Playback.StartTime == before.StartTime && doc.Playback.EndTime == before.EndTime,
                "cancel: closing without OK clears the preview, restores the transport's range and adds no step");
        }
        {
            var vm = new TrimToolViewModel(doc) { From = U(to), To = U(from) };
            vm.RecomputeNow();
            await vm.SettleAsync();
            ctx.Check(vm.Error is { Length: > 0 } && !vm.CanApply && doc.Scene.PreviewClip is null && !vm.Commit(),
                $"refusal: a Core refusal shows inline and disables OK ('{vm.Error}')");
            vm.End();
        }
        {
            var before = doc.Current;
            var vm = new ReverseToolViewModel(doc);
            var window = ClipToolWindow.CreateForCapture(vm);
            window.ShowActivated = false;
            window.Show();
            await vm.SettleAsync();
            await ctx.YieldAsync();
            bool shown = doc.Scene.PreviewClip is not null;
            window.Close();
            ctx.Check(shown && doc.Scene.PreviewClip is null && vm.IsEnded && ReferenceEquals(doc.Current, before),
                "window: the dialog previews while open and closing it (X / Esc / Cancel) clears the preview");
        }

        // ── Compare with ─────────────────────────────────────────────────────
        await CompareChecks(ctx, doc);
    }

    private static async Task CompareChecks(SelfTestContext ctx, ClipDocumentViewModel doc)
    {
        var before = doc.Current;
        int undo = doc.History.UndoLabels.Count;
        doc.Compare.Clear();
        var dialog = new CompareDialogViewModel(doc);
        var choices = dialog.Clips.All.Where(x => x.Clip.BoneCount == before.BoneCount).Take(2).ToList();
        if (choices.Count == 0)
        {
            dialog.End();
            ctx.Log("selftest tools: no other clip with the same bone count in the library; compare skipped");
            return;
        }
        dialog.Clips.Selected = choices[0];
        await dialog.SettleAsync();
        ctx.Check(doc.Scene.HasGhost(ClipCompareViewModel.GhostId) && dialog.CanApply, $"compare: picking {choices[0].Clip.Name} shows its ghost live");
        ctx.Check(dialog.Commit() && doc.Compare.IsActive && doc.Compare.ClipName == choices[0].Clip.Name && doc.Scene.HasGhost(ClipCompareViewModel.GhostId),
            "compare: OK keeps the ghost and the chip names the clip");
        ctx.Check(ReferenceEquals(doc.Current, before) && doc.History.UndoLabels.Count == undo, "compare: no undo step, the clip is untouched");
        var ghost = doc.Scene.Ghosts.First(g => g.Id == ClipCompareViewModel.GhostId);
        var compared = ghost.Clip;
        float t0 = ghost.TimeMap?.Invoke(before.StartTime) ?? float.NaN;
        float t1 = ghost.TimeMap?.Invoke(before.StartTime + compared.Duration + 160) ?? float.NaN;
        ctx.Check(t0 == compared.StartTime && Math.Abs(t1 - (compared.StartTime + (compared.Duration > 0 ? 160 % compared.Duration : 0))) < 0.01f,
            $"compare: the ghost starts with the clip and loops over its own length ({t0}, {t1})");
        doc.Compare.IsVisible = false;
        bool hidden = !doc.Scene.HasGhost(ClipCompareViewModel.GhostId) && doc.Compare.IsActive;
        doc.Compare.IsVisible = true;
        ctx.Check(hidden && doc.Scene.HasGhost(ClipCompareViewModel.GhostId), "compare: the chip's toggle hides and shows the ghost");
        if (choices.Count > 1)
        {
            var second = new CompareDialogViewModel(doc);
            second.Clips.Selected = second.Clips.All.First(x => x.Clip.Name == choices[1].Clip.Name);
            await second.SettleAsync();
            bool live = doc.Compare.ClipName == choices[1].Clip.Name;
            second.End();
            ctx.Check(live && doc.Compare.ClipName == choices[0].Clip.Name && doc.Scene.HasGhost(ClipCompareViewModel.GhostId),
                "compare: Cancel puts back the previous comparison");
        }
        doc.Compare.ClearCommand.Execute(null);
        ctx.Check(!doc.Compare.IsActive && !doc.Scene.HasGhost(ClipCompareViewModel.GhostId), "compare: the chip's × clears the ghost");
    }

    /// <summary>
    /// The Problems panel's "Conform to skeleton…" quick fix: Core's RFA013 (a table playing the clip on
    /// another mesh) and RFA002 (a preview mesh with another bone count) rows show it enabled; it opens the
    /// Conform tool with the named mesh (or the preview mesh) picked — the same view-model the button shows,
    /// driven here without the modal window — and OK commits the conformed clip through Apply as one step.
    /// </summary>
    private static async Task ConformQuickFixChecks(SelfTestContext ctx, ClipDocumentViewModel doc, LibraryMeshChoice? target)
    {
        if (target is null)
        {
            ctx.Log("selftest tools: no other mesh with a skeleton in the library; the conform quick fix checks are skipped");
            return;
        }
        var before = doc.Current;
        int tableBones = target.Mesh.BoneCount != before.BoneCount ? target.Mesh.BoneCount : before.BoneCount + 1;
        var use = new ClipTableUse("selftest", "entity.tbl", "stand", true, target.Mesh.Name, tableBones);
        var rfa013 = ClipLinter.Analyze(before, new ClipLintContext { TableUses = [use] }).FirstOrDefault(d => d.Code == ClipRules.TableMeshBoneCount);
        var fix = rfa013?.QuickFixes.FirstOrDefault(f => f.Kind == QuickFixKind.ConformToSkeleton);
        var row = rfa013 is null ? null : new DiagnosticViewModel(doc, rfa013);
        ctx.Check(fix is not null && row is not null && row.QuickFixes.Any(b => b.Title == fix.Title && b.ApplyCommand.CanExecute(null)),
            $"conform quick fix: RFA013's '{fix?.Title}' is shown in its Problems row and enabled");
        if (fix is null) return;
        string? picked = doc.ConformTarget(fix);
        ctx.Check(string.Equals(picked, target.Mesh.Name, StringComparison.OrdinalIgnoreCase),
            $"conform quick fix: RFA013's fix picks the mesh the table names ({picked})");

        // RFA002 names no mesh: the preview mesh is picked.
        var targetSkeleton = Skeleton.FromFile(await ctx.Model.Assets.LoadMeshAsync(target.Mesh));
        if (targetSkeleton.Count != before.BoneCount && doc.Scene.MeshName is { } previewName)
        {
            var rfa002 = ClipLinter.Analyze(before, new ClipLintContext { Skeleton = targetSkeleton, PreviewMeshName = target.Mesh.Name })
                .FirstOrDefault(d => d.Code == ClipRules.BoneCountMismatch);
            var fix002 = rfa002?.QuickFixes.FirstOrDefault(f => f.Kind == QuickFixKind.ConformToSkeleton);
            var row002 = rfa002 is null ? null : new DiagnosticViewModel(doc, rfa002);
            var tool002 = fix002 is null ? null : new ConformToolViewModel(doc, doc.ConformTarget(fix002));
            ctx.Check(row002 is not null && fix002 is not null && row002.QuickFixes.Any(b => b.Title == fix002.Title && b.ApplyCommand.CanExecute(null))
                && string.Equals(tool002?.Meshes.Selected?.Mesh.Name, previewName, StringComparison.OrdinalIgnoreCase),
                $"conform quick fix: RFA002's fix is shown, enabled, and opens the tool on the preview mesh ({tool002?.Meshes.Selected?.Mesh.Name ?? "none"})");
            tool002?.End();
        }

        if (doc.ClipboardBoneNames is not { } names)
        {
            ctx.Log("selftest tools: the clip has no bone names (no fitting preview mesh); the conform quick fix's OK is not checked");
            return;
        }
        var want = ClipEdit.ConformToSkeleton(before, names, targetSkeleton).Clip;
        int undo = doc.History.UndoLabels.Count;
        var vm = new ConformToolViewModel(doc, picked);
        try
        {
            bool preselected = string.Equals(vm.Meshes.Selected?.Mesh.Name, target.Mesh.Name, StringComparison.OrdinalIgnoreCase);
            vm.RecomputeNow();
            await vm.SettleAsync();
            bool ok = vm.Commit();
            ctx.Check(preselected && ok && doc.History.UndoLabels.Count == undo + 1 && doc.UndoLabel == $"Conform to {target.Mesh.Name}" && SameBytes(doc.Current, want),
                $"conform quick fix: the tool opens with {target.Mesh.Name} picked and OK commits the conformed clip as one step '{doc.UndoLabel}'");
            if (ok) doc.Undo();
            ctx.Check(ReferenceEquals(doc.Current, before), "conform quick fix: undo restores the clip");
        }
        finally
        {
            vm.End();
            if (!ReferenceEquals(doc.Current, before) && doc.CanUndo) doc.Undo();
        }
    }

    /// <summary>A bone with rotation keys and a parent (an arm or leg on the stock rigs), else bone 0.</summary>
    private static int PickBone(ClipDocumentViewModel doc, RfaClip clip)
    {
        if (doc.FittingSkeleton is { } s)
        {
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Names[i].Contains("arm", StringComparison.OrdinalIgnoreCase) && clip.Bones[i].RotationKeys.Length > 1) return i;
            }
            for (int i = 0; i < s.Count; i++)
            {
                if (s.Parents[i] >= 0 && clip.Bones[i].RotationKeys.Length > 1) return i;
            }
        }
        for (int i = 0; i < clip.BoneCount; i++)
        {
            if (clip.Bones[i].RotationKeys.Length > 1) return i;
        }
        return 0;
    }

    /// <summary>
    /// Drives one tool: configure, wait for the preview, compare it with <paramref name="expected"/>
    /// (the direct Core call on the snapshot the tool opened on), check the document is untouched, press
    /// OK, check one undo step with <paramref name="label"/>, the preview cleared, and undo restoring the clip.
    /// </summary>
    private static async Task Run<T>(SelfTestContext ctx, ClipDocumentViewModel doc, Func<T> create, Action<T> configure,
        Func<RfaClip, T, RfaClip> expected, string label, bool allowNoChange = false) where T : ClipToolViewModel
    {
        var before = doc.Current;
        bool dirty = doc.IsDirty;
        int undo = doc.History.UndoLabels.Count;
        var vm = create();
        string name = vm.ToolId;
        try
        {
            configure(vm);
            vm.RecomputeNow();
            bool settled = await vm.SettleAsync();
            ctx.Check(settled && vm.Error is null && vm.Result is not null, $"{name}: preview computed{(vm.Error is null ? "" : $" — {vm.Error}")}");
            if (vm.Result is not { } result) return;
            var want = expected(before, vm);
            bool same = SameBytes(want, before);
            if (same && allowNoChange)
            {
                ctx.Check(ReferenceEquals(result.Clip, before) && !vm.CanApply && doc.Scene.PreviewClip is null,
                    $"{name}: nothing changes on this clip, so OK is disabled ('{result.Summary.LastOrDefault()}')");
                return;
            }
            ctx.Check(!same, $"{name}: the test parameters change the clip");
            ctx.Check(SameBytes(result.Clip, want), $"{name}: preview equals the direct Core call");
            ctx.Check(ReferenceEquals(doc.Scene.PreviewClip, result.Clip), $"{name}: the viewport shows the preview");
            ctx.Check(ReferenceEquals(doc.Current, before) && doc.IsDirty == dirty && doc.History.UndoLabels.Count == undo,
                $"{name}: the document is untouched while previewing");
            bool ok = vm.Commit();
            ctx.Check(ok && doc.History.UndoLabels.Count == undo + 1 && doc.UndoLabel == label,
                $"{name}: OK adds exactly one undo step '{doc.UndoLabel}' (expected '{label}')");
            ctx.Check(ok && SameBytes(doc.Current, want), $"{name}: the committed clip is the previewed one");
            ctx.Check(doc.Scene.PreviewClip is null && vm.IsEnded, $"{name}: the preview is cleared after OK");
            if (ok) doc.Undo();
            ctx.Check(ReferenceEquals(doc.Current, before) && doc.IsDirty == dirty, $"{name}: undo restores the clip");
        }
        finally
        {
            vm.End();
            if (!ReferenceEquals(doc.Current, before) && doc.CanUndo) doc.Undo();
        }
    }

    private static async Task NoChange(SelfTestContext ctx, ClipDocumentViewModel doc, ClipToolViewModel vm, string what)
    {
        vm.RecomputeNow();
        await vm.SettleAsync();
        ctx.Check(vm.Error is null && vm.Result is { } r && ReferenceEquals(r.Clip, doc.Current) && !vm.CanApply && doc.Scene.PreviewClip is null,
            $"{vm.ToolId}: {what} changes nothing and OK is disabled ('{vm.SummaryLines.FirstOrDefault()}')");
        vm.End();
    }

    private static bool SameBytes(RfaClip a, RfaClip b)
    {
        if (ReferenceEquals(a, b)) return true;
        try
        {
            return RfaWriter.Write(a).AsSpan().SequenceEqual(RfaWriter.Write(b));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
