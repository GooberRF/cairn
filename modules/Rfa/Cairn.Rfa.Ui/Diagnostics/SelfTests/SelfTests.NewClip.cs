using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.ClipCreation;
using Cairn.Rfa.Ui.ViewModels.PoseEditing;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of File › New Clip… (1.0.1): the commands' enablement (File, Mesh menu, library context menu);
/// the dialog's defaults for a stock character (its stand clip found through the tables) and for the sample
/// figure (no table or rig names a stand clip for it, so it starts from its bind pose); the created tab's clip
/// byte for byte equal to Core's <see cref="NewClip.Create(Skeleton, NewClipOptions)"/> for the same options;
/// the tab dirty, never saved, previewed on the mesh, timeline in front and playhead at the start; a gizmo
/// rotate with auto-key writing a key in the new clip; and the name checks. Opens and closes its own tabs.
/// </summary>
internal static class NewClipSelfTests
{
    [SelfTest("newclip", Order = 670)]
    public static async Task NewClips(SelfTestContext ctx)
    {
        var model = ctx.Model;
        var startActive = model.ActiveDocument;
        var opened = new List<DocumentViewModel>();
        var saved = (model.Display.AutoKey, model.PoseSettings.Tool, model.IsBottomVisible, model.SelectedBottomTab);
        try
        {
            var commands = model.NewClips;
            ctx.Check(commands.NewClipCommand.CanExecute(null), "newclip: File › New Clip… is always available");
            ctx.Check(model.ActiveDocument is not MeshDocumentViewModel || commands.ForActiveMeshCommand.CanExecute(null) == model.IsSkeletalMeshDocument,
                "newclip: Mesh › New Clip for This Mesh… follows the tab in front");
            if (model.ActiveDocument is ClipDocumentViewModel)
                ctx.Check(!commands.ForActiveMeshCommand.CanExecute(null), "newclip: New Clip for This Mesh… is disabled with a clip tab in front");
            var snapshot = model.Assets.Snapshot;
            if (snapshot.Meshes.FirstOrDefault(m => m.HasSkeleton && m.IsReadable) is { } character)
            {
                ctx.Check(model.Library.NewClipCommand.CanExecute(character) && new LibraryNode(character.Name, "", "", "", character).IsCharacterMesh,
                    $"newclip: the library offers New Clip for This Mesh… on {character.Name}");
            }
            if (snapshot.Clips.FirstOrDefault() is { } anyClip)
                ctx.Check(!model.Library.NewClipCommand.CanExecute(anyClip) && !new LibraryNode(anyClip.Name, "", "", "", anyClip).IsCharacterMesh,
                    "newclip: the library does not offer it on a clip");
            if (snapshot.StaticMeshes.FirstOrDefault() is { } staticMesh)
                ctx.Check(!model.Library.NewClipCommand.CanExecute(staticMesh), $"newclip: the library does not offer it on the static mesh {staticMesh.Name}");

            if (ctx.Clip is { PreviewMesh: not null } clipDoc)
            {
                var vm = new NewClipViewModel(model);
                try
                {
                    await vm.SettleAsync();
                    ctx.Check(vm.SelectedTarget?.MeshName == clipDoc.Scene.MeshName && vm.SelectedTarget?.Note.StartsWith("preview of", StringComparison.Ordinal) == true,
                        $"newclip: with a clip tab in front the default mesh is its preview mesh ({vm.SelectedTarget?.MeshName})");
                }
                finally { vm.End(); }
            }

            await StockCharacter(ctx, opened);
            await SampleFigure(ctx, opened);
        }
        finally
        {
            foreach (var d in opened)
            {
                if (d is ClipDocumentViewModel c && PoseEditController.For(c) is { IsDragging: true } ctl) ctl.CancelDrag();
                model.CloseDiscarding(d);
            }
            model.Display.AutoKey = saved.AutoKey;
            model.PoseSettings.Tool = saved.Tool;
            model.IsBottomVisible = saved.IsBottomVisible;
            if (startActive is not null && model.Documents.Contains(startActive)) model.ActiveDocument = startActive;
            model.SelectedBottomTab = saved.SelectedBottomTab;
            await ctx.SettleAsync();
        }
    }

    /// <summary>ult2_guard.v3c from the library: the tables' stand clip is the default; an action made from it equals Core's.</summary>
    private static async Task StockCharacter(SelfTestContext ctx, List<DocumentViewModel> opened)
    {
        var model = ctx.Model;
        if (model.Assets.Snapshot.FindMesh("ult2_guard.v3c") is not { HasSkeleton: true } guard || model.Assets.Usage.ClipListsForMesh(guard.Name).Count == 0)
        {
            ctx.Log("selftest newclip: ult2_guard.v3c or the tables are not in the library (set the game directory); the stock checks are skipped");
            return;
        }
        var vm = new NewClipViewModel(model, guard);
        try
        {
            await vm.SettleAsync();
            var reference = vm.References.Selected;
            ctx.Check(vm.TargetMeshName == guard.Name, $"newclip stock: the library's mesh is the target ({vm.TargetMeshName})");
            ctx.Check(vm.UseReference && reference is { IsSuggested: true } && string.Equals(reference.Clip.Name, "ult2_stand.rfa", StringComparison.OrdinalIgnoreCase)
                && reference.Note.StartsWith("tables: stand", StringComparison.Ordinal),
                $"newclip stock: the default starting pose is the tables' stand clip ({reference?.Clip.Name}, '{reference?.Note}'; {vm.SuggestionText})");
            ctx.Check(vm.ReferenceClip is { } r && vm.PoseTimeTicks == r.StartTime, $"newclip stock: the pose is taken at the stand clip's start (tick {vm.PoseTimeTicks})");
            ctx.Check(vm.IsState && vm.RampIn == 0 && vm.RampOut == 0 && vm.LengthTicks == NewClipOptions.DefaultLength && vm.IsVersion8 && vm.Weight == 10,
                "newclip stock: defaults are a 1 s version 8 state, weight 10, no ramps");
            ctx.Check(vm.Name.StartsWith("ult2_guard_new", StringComparison.OrdinalIgnoreCase) && vm.NameProblem is null,
                $"newclip stock: the default name is '{vm.Name}' and passes the name checks");

            // Name checks (the rules Retarget applies).
            (string Name, string Expect)[] names =
            [
                ("", "Give the clip a file name"),
                ("bad:name", "cannot have"),
                ("café_wave", "ASCII"),
                (new string('a', 56) + ".rfa", "59"),
                ("ult2_stand.rfa", "already exists"),
            ];
            foreach (var (name, expect) in names)
            {
                vm.Name = name;
                ctx.Check(vm.NameProblem?.Contains(expect, StringComparison.Ordinal) == true && !vm.CanCreate,
                    $"newclip name '{name}': refused ({vm.NameProblem})");
            }
            vm.Name = "rfawb_selftest_new_action";
            ctx.Check(vm.NameProblem is null && vm.FileName == "rfawb_selftest_new_action.rfa", "newclip name: a new name passes and gets .rfa");

            // An action, two seconds, posed at frame 10 of the stand clip.
            vm.IsAction = true;
            vm.LengthTicks = 2 * RfaClip.TicksPerSecond;
            int poseAt = (vm.ReferenceClip?.StartTime ?? RfaClip.TicksPerFrame) + 9 * RfaClip.TicksPerFrame;
            vm.PoseTimeTicks = poseAt;
            ctx.Check(vm.RampIn == TimeFormat.ToUnit(NewClipOptions.DefaultActionRamp, vm.Unit) && vm.RampOut == vm.RampIn,
                $"newclip stock: an action gets {NewClipOptions.DefaultActionRamp}-tick ramps");
            var stand = await model.Assets.LoadClipAsync(model.Assets.Snapshot.FindClip("ult2_stand.rfa")!);
            var mesh = await model.Assets.LoadMeshAsync(guard);
            var expected = NewClip.Create(Skeleton.FromFile(mesh), NewClipOptions.ForKind(NewClipKind.Action) with
            {
                Length = 2 * RfaClip.TicksPerSecond,
                PoseClip = stand,
                PoseTime = poseAt,
            });
            var document = vm.Create();
            if (document is null)
            {
                ctx.Check(false, $"newclip stock: Create opened a tab ({vm.Problem ?? vm.Error})");
                return;
            }
            opened.Add(document);
            await ctx.SettleAsync();
            ctx.Check(RfaWriter.Write(document.Current).AsSpan().SequenceEqual(RfaWriter.Write(expected)),
                "newclip stock: the tab's clip is byte for byte Core's NewClip.Create for the same options (action, 2 s, stand clip at frame 10)");
            ctx.Check(document.IsDirty && document.FilePath is null && document.ArchiveOrigin is null && document.DisplayName == "rfawb_selftest_new_action.rfa",
                $"newclip stock: the tab '{document.TabHeader}' is new, unsaved and dirty");
            ctx.Check(document.ErrorCount == 0 && document.WarningCount == 0,
                $"newclip stock: the new clip has no problems on its mesh ({document.ErrorCount} errors, {document.WarningCount} warnings)");
        }
        finally { vm.End(); }
    }

    /// <summary>The sample figure open in a tab: the bind pose is the default; the created tab is posed with the gizmo.</summary>
    private static async Task SampleFigure(SelfTestContext ctx, List<DocumentViewModel> opened)
    {
        var model = ctx.Model;
        string? root = LocalPaths.RepositoryRoot;
        string? path = root is null ? null : Path.Combine(root, "samples", "rfa", "sample_figure.v3c");
        if (path is null || !File.Exists(path))
        {
            ctx.Log("selftest newclip: samples/sample_figure.v3c not found; the sample checks are skipped");
            return;
        }
        bool wasOpen = model.Documents.Any(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (model.OpenFile(path) is not MeshDocumentViewModel figure)
        {
            ctx.Check(false, "newclip sample: sample_figure.v3c opens as a mesh tab");
            return;
        }
        if (!wasOpen) opened.Add(figure);
        model.ActiveDocument = figure;
        await ctx.SettleAsync();
        ctx.Check(model.NewClips.ForActiveMeshCommand.CanExecute(null), "newclip sample: Mesh › New Clip for This Mesh… is enabled for a mesh tab with bones");

        var vm = new NewClipViewModel(model);
        ClipDocumentViewModel? document;
        try
        {
            await vm.SettleAsync();
            ctx.Check(vm.SelectedTarget is { } st && st.Document == figure && st.Note == "active tab", $"newclip sample: the active mesh tab is the target ({vm.SelectedTarget?.Note})");
            ctx.Check(vm.UseBindPose && !vm.UseReference && vm.References.Selected is null,
                $"newclip sample: no table or rig names a stand clip for the sample figure, so it starts from its bind pose ({vm.SuggestionText})");
            ctx.Check(vm.Name == "sample_figure_new.rfa" || vm.Name.StartsWith("sample_figure_new", StringComparison.Ordinal), $"newclip sample: default name '{vm.Name}'");
            ctx.Check(vm.Clip is not null && vm.CanCreate, $"newclip sample: the clip can be created ({vm.Problem})");
            ctx.Check(vm.Preview.Clip is { } shown && ReferenceEquals(shown, vm.Clip), "newclip sample: the preview shows the starting pose");
            vm.Name = "rfawb_selftest_new_state";
            document = vm.Create();
        }
        finally { vm.End(); }
        if (document is null)
        {
            ctx.Check(false, "newclip sample: Create opened a tab");
            return;
        }
        opened.Add(document);
        await ctx.SettleAsync();

        var skeleton = Skeleton.FromFile(figure.Current);
        var expected = NewClip.Create(skeleton, NewClipOptions.ForKind(NewClipKind.State));
        ctx.Check(RfaWriter.Write(document.Current).AsSpan().SequenceEqual(RfaWriter.Write(expected)),
            "newclip sample: the tab's clip is byte for byte Core's NewClip.Create (state, bind pose, defaults)");
        ctx.Check(ReferenceEquals(model.ActiveDocument, document) && document.IsDirty && document.FilePath is null,
            "newclip sample: the new tab is in front, dirty and never saved");
        ctx.Check(model.IsBottomVisible && model.SelectedBottomTab?.Id == "timeline", "newclip sample: the Timeline tab is in front");
        ctx.Check(document.Playback.Time == expected.StartTime, $"newclip sample: the playhead is at the start (tick {document.Playback.Time})");
        ctx.Check(document.FittingSkeleton is { Count: > 0 } && document.Scene.MeshName == figure.DisplayName,
            $"newclip sample: previewed on {document.Scene.MeshName}");
        ctx.Check(document.StatusMessage?.StartsWith("New clip ready", StringComparison.Ordinal) == true, $"newclip sample: the status bar says what next ('{document.StatusMessage}')");
        ctx.Check(document.ErrorCount == 0 && document.WarningCount == 0, $"newclip sample: no problems ({document.ErrorCount} errors, {document.WarningCount} warnings)");

        // The pose-editing path works on it: an auto-key rotate writes a key at the playhead.
        var ctl = PoseEditController.For(document);
        int bone = skeleton.IndexOf("upper_arm_r");
        if (bone < 0) bone = skeleton.Count - 1;
        int t = expected.StartTime + 15 * RfaClip.TicksPerFrame;
        document.Playback.Seek(t);
        document.Selection.Select(bone);
        ctl.AutoKey = true;
        ctl.Tool = PoseTool.Rotate;
        var before = document.Current;
        bool began = ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.X);
        ctl.UpdateAxisRotation(GizmoHandle.X, 0.5);
        ctl.CommitDrag();
        var keys = document.Current.Bones[bone].RotationKeys;
        ctx.Check(began && !ReferenceEquals(before, document.Current) && keys.Length == 3 && keys.Any(k => k.Time == t),
            $"newclip sample: an auto-key rotate keys {document.BoneDisplayName(bone)} at tick {t} ({keys.Length} rotation keys)");
        ctx.Check(document.UndoLabel == "Rotate " + document.BoneDisplayName(bone), $"newclip sample: one undo step '{document.UndoLabel}'");
        document.Undo();
        ctx.Check(document.IsDirty && ReferenceEquals(document.Current, before), "newclip sample: undoing back to the start pose leaves the tab dirty (never saved)");
    }
}
