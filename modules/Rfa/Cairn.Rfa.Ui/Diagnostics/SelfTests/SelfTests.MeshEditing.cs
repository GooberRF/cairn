using System.Numerics;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.MeshEditing;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of mesh document editing (phase 6): every Structure-tab editor and Mesh menu command driven
/// through the view-models, each adding exactly one labelled undo step that undo reverts to the exact
/// snapshot; refusals that add no step and show a message; reorder + conform of an open clip with the
/// FK pose unchanged; a quick fix; and the read-only .v3m editors. Uses the active mesh document, or opens
/// the stock ult2_guard.v3c (and ult2_walk.rfa, a .v3m) from the research corpus and closes them after.
/// </summary>
internal static class MeshEditingSelfTests
{
    /// <summary>The stock corpus folder (see <see cref="LocalPaths.Corpus"/>), or null.</summary>
    internal static string? Corpus() => LocalPaths.Corpus;

    [SelfTest("meshedit", Order = 650)]
    public static async Task MeshEdit_(SelfTestContext ctx)
    {
        var model = ctx.Model;
        var startActive = model.ActiveDocument;
        var opened = new List<DocumentViewModel>();
        var tools = model.MeshTools;
        var savedReorder = tools.ReorderPrompt;
        var savedTexture = tools.TexturePrompt;
        string? corpus = Corpus();
        try
        {
            var doc = model.ActiveDocument as MeshDocumentViewModel;
            if (doc is { IsReadOnly: true })
            {
                ReadOnlyChecks(ctx, doc);
                doc = null;
            }
            if (doc is null)
            {
                string? path = corpus is null ? null : Path.Combine(corpus, "ult2_guard.v3c");
                if (path is null || !File.Exists(path))
                {
                    ctx.Log("selftest meshedit: no mesh document and the research corpus (ult2_guard.v3c) is absent; skipped");
                    return;
                }
                doc = model.OpenFile(path) as MeshDocumentViewModel;
                if (doc is null)
                {
                    ctx.Check(false, "ult2_guard.v3c opens as a mesh document");
                    return;
                }
                opened.Add(doc);
                await ctx.SettleAsync();
            }
            model.ActiveDocument = doc;
            await ctx.SettleAsync();
            var original = doc.Current;
            int undoStart = doc.History.UndoLabels.Count;
            tools.ReorderPrompt = vm => vm.Apply();

            ctx.Check(tools.AddSphereCommand.CanExecute(null) && tools.ReorderBonesCommand.CanExecute(null),
                "the Mesh menu commands are enabled for an editable mesh document");

            BoneChecks(ctx, doc, tools);
            await ReorderChecks(ctx, doc, corpus, opened);
            model.ActiveDocument = doc;
            await ctx.SettleAsync();
            SphereChecks(ctx, doc, tools);
            PropChecks(ctx, doc, tools);
            MaterialChecks(ctx, doc, tools);
            LodAndSubmeshChecks(ctx, doc);
            QuickFixChecks(ctx, doc);
            LocateChecks(ctx, doc, tools);

            // Rename Selected (F2) puts the keyboard in the name box: no edit by itself.
            doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.Bone, Index: 1));
            int asked = 0;
            void OnAsk(object? s, EventArgs e) => asked++;
            doc.Structure.RenameRequested += OnAsk;
            int before = doc.History.UndoLabels.Count;
            bool canRename = tools.RenameSelectedCommand.CanExecute(null);
            tools.RenameSelectedCommand.Execute(null);
            ctx.Check(canRename && asked == 1 && doc.History.UndoLabels.Count == before, "Rename Selected (F2) asks the editor's name box for the keyboard and edits nothing");
            // A LOD texture entry has no editor of its own: F2 selects the owning material and asks for its texture box.
            var lodTextures = doc.Current.Submeshes.SelectMany((s, si) => s.Lods.SelectMany((l, li) => l.Textures.Select((t, ti) => (si, li, ti, Material: (int)t.MaterialIndex))))
                .Where(x => MaterialEditor.Find(doc.Current, x.si, x.Material) is not null).ToList();
            if (lodTextures.Count > 0)
            {
                var lodTexture = lodTextures[0];
                doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.Texture, lodTexture.si, lodTexture.li, lodTexture.ti));
                bool canTexture = doc.Structure.Selected?.Node is { Kind: MeshNodeKind.Texture } && tools.RenameSelectedCommand.CanExecute(null);
                asked = 0;
                tools.RenameSelectedCommand.Execute(null);
                ctx.Check(canTexture && asked == 1 && doc.Structure.Editor is MaterialEditor
                    && doc.Structure.Selected?.Node is { Kind: MeshNodeKind.Material } m && m.Submesh == lodTexture.si && m.Index == lodTexture.Material
                    && doc.History.UndoLabels.Count == before,
                    $"Rename Selected on a LOD texture entry selects its material ({lodTexture.Material}) and asks for the texture name box");
            }
            doc.Structure.RenameRequested -= OnAsk;
            foreach (var (node, what) in new[] { (new MeshNodeRef(MeshNodeKind.Header), "the header"), (new MeshNodeRef(MeshNodeKind.Lod, 0, 0), "a LOD"), (new MeshNodeRef(MeshNodeKind.Batch, 0, 0, 0), "a batch") })
            {
                doc.Structure.Reveal(node);
                ctx.Check(doc.Structure.Selected?.Node is { } sel && sel.Kind == node.Kind && !tools.RenameSelectedCommand.CanExecute(null),
                    $"Rename Selected is disabled on {what} (it has no name to change)");
            }

            while (doc.History.UndoLabels.Count > undoStart && doc.CanUndo) doc.Undo();
            ctx.Check(ReferenceEquals(doc.Current, original), "the mesh editing test leaves the mesh document as it found it");

            // Read-only .v3m.
            if (corpus is not null && Directory.EnumerateFiles(corpus, "*.v3m").FirstOrDefault() is { } v3m)
            {
                if (model.OpenFile(v3m) is MeshDocumentViewModel staticDoc)
                {
                    opened.Add(staticDoc);
                    await ctx.SettleAsync();
                    ReadOnlyChecks(ctx, staticDoc);
                    ctx.Check(!tools.AddSphereCommand.CanExecute(null) && !tools.ReorderBonesCommand.CanExecute(null) && !tools.AddPropPointCommand.CanExecute(null),
                        "the Mesh menu's editing commands are disabled for a read-only .v3m");
                }
            }
        }
        finally
        {
            tools.ReorderPrompt = savedReorder;
            tools.TexturePrompt = savedTexture;
            foreach (var d in opened) model.CloseDiscarding(d);
            if (startActive is not null && model.Documents.Contains(startActive)) model.ActiveDocument = startActive;
            await ctx.SettleAsync();
        }
    }

    /// <summary>Runs <paramref name="act"/>, checks it added one step labelled <paramref name="label"/>, undoes it and checks the snapshot is back.</summary>
    private static bool Step(SelfTestContext ctx, MeshDocumentViewModel doc, string label, Action act, Func<V3dFile, bool>? verify = null, string? what = null)
    {
        var before = doc.Current;
        int count = doc.History.UndoLabels.Count;
        act();
        var after = doc.Current;
        bool one = doc.History.UndoLabels.Count == count + 1 && doc.UndoLabel == label;
        bool verified = verify?.Invoke(after) ?? true;
        ctx.Check(one && verified, $"{what ?? label}: one undo step '{doc.UndoLabel}' ({doc.History.UndoLabels.Count - count} added){(verified ? "" : ", but the result is wrong")}");
        if (doc.History.UndoLabels.Count > count) doc.Undo();
        ctx.Check(ReferenceEquals(doc.Current, before), $"undo of '{label}' restores the exact snapshot");
        return one && verified;
    }

    /// <summary>Runs <paramref name="act"/> and checks it was refused: no step, the snapshot unchanged, a message in the editor.</summary>
    private static void Refused(SelfTestContext ctx, MeshDocumentViewModel doc, string what, Action act, Func<bool> hasMessage)
    {
        var before = doc.Current;
        int count = doc.History.UndoLabels.Count;
        act();
        ctx.Check(ReferenceEquals(doc.Current, before) && doc.History.UndoLabels.Count == count && hasMessage(),
            $"{what} is refused with a message and adds no step ({doc.Structure.Editor?.Error ?? doc.StatusMessage ?? "no message"})");
    }

    private static T? Editor<T>(MeshDocumentViewModel doc, MeshNodeRef node) where T : MeshNodeEditor
    {
        doc.Structure.Reveal(node);
        return doc.Structure.Editor as T;
    }

    // ── Bones ────────────────────────────────────────────────────────────────

    private static void BoneChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshCommands tools)
    {
        var mesh = doc.Current;
        int n = mesh.Bones.Length;
        if (n < 4)
        {
            ctx.Log("selftest meshedit: the mesh has fewer than 4 bones; bone checks skipped");
            return;
        }
        var skeleton = Skeleton.FromFile(mesh);
        var bone3 = Editor<BoneEditor>(doc, new MeshNodeRef(MeshNodeKind.Bone, Index: 3));
        if (bone3 is null)
        {
            ctx.Check(false, "selecting bone 3 shows the bone editor");
            return;
        }
        string old = mesh.Bones[3].Name.Text;
        string renamed = (old.Length > 20 ? old[..20] : old) + "_x";
        Step(ctx, doc, $"Rename bone 3 to {renamed}", () => bone3.Name.Text = renamed, m => m.Bones[3].Name.Text == renamed);
        // The editor survives the rebuild after the edit, and the node stays selected.
        bone3.Name.Text = renamed;
        ctx.Check(ReferenceEquals(doc.Structure.Editor, bone3) && doc.Structure.Selected?.Node is { Kind: MeshNodeKind.Bone, Index: 3 },
            "the bone editor and the tree selection survive the rebuild after an edit");
        doc.Undo();
        Refused(ctx, doc, "a 24-character bone name", () => bone3.Name.Text = new string('n', 24), () => bone3.HasError);

        // Reparent to a bone outside its subtree, and a refused cycle.
        int parent = mesh.Bones[3].ParentIndex;
        int target = Enumerable.Range(0, n).FirstOrDefault(b => b != 3 && b != parent && !IsDescendant(skeleton, b, 3), -1);
        if (target >= 0)
        {
            Step(ctx, doc, $"Reparent bone '{old}' to '{mesh.Bones[target].Name.Text}'", () => bone3.Parent.Pick(target), m => m.Bones[3].ParentIndex == target);
        }
        int root = Enumerable.Range(0, n).First(b => skeleton.EffectiveParents[b] < 0 && skeleton.EffectiveParents.Contains(b));
        int child = Enumerable.Range(0, n).First(b => skeleton.EffectiveParents[b] == root);
        var rootEditor = Editor<BoneEditor>(doc, new MeshNodeRef(MeshNodeKind.Bone, Index: root))!;
        Refused(ctx, doc, "a parent that would close a loop", () => rootEditor.Parent.Pick(child), () => rootEditor.Error?.Contains("loop", StringComparison.OrdinalIgnoreCase) == true);

        // Bind pose: a bone with a parent and children.
        int k = Enumerable.Range(0, n).First(b => skeleton.EffectiveParents[b] >= 0 && skeleton.EffectiveParents.Contains(b));
        int kChild = Enumerable.Range(0, n).First(b => skeleton.EffectiveParents[b] == k);
        string kName = mesh.Bones[k].Name.Text;
        var bind = Editor<BoneEditor>(doc, new MeshNodeRef(MeshNodeKind.Bone, Index: k))!;
        bool wasWorld = bind.IsWorldSpace, wasFollow = bind.ChildrenFollow;
        try
        {
            bind.IsLocalSpace = true;
            bind.ChildrenFollow = false;
            var local0 = MeshEdit.GetBoneBind(mesh, k, BindSpace.Local);
            var childWorld0 = MeshEdit.GetBoneBind(mesh, kChild, BindSpace.World);
            Step(ctx, doc, $"Move bone '{kName}' (local)", () => bind.Position[0].Value = local0.Position.X + 0.05,
                m => Math.Abs(MeshEdit.GetBoneBind(m, k, BindSpace.Local).Position.X - (local0.Position.X + 0.05f)) < 1e-4f
                    && Vector3.Distance(MeshEdit.GetBoneBind(m, kChild, BindSpace.World).Position, childWorld0.Position) < 1e-4f,
                "a local bind move without children following (children stay in the model)");

            bind.IsWorldSpace = true;
            bind.ChildrenFollow = true;
            var world0 = MeshEdit.GetBoneBind(mesh, k, BindSpace.World);
            var childLocal0 = MeshEdit.GetBoneBind(mesh, kChild, BindSpace.Local);
            double yaw = Quat.ToEulerDegrees(world0.Rotation).Y;
            Step(ctx, doc, $"Rotate bone '{kName}' (world) with its children", () => bind.Rotation[1].Value = yaw + 10,
                m => Math.Abs(Quat.ToEulerDegrees(MeshEdit.GetBoneBind(m, k, BindSpace.World).Rotation).Y - (yaw + 10)) < 0.05
                    && Vector3.Distance(MeshEdit.GetBoneBind(m, kChild, BindSpace.Local).Position, childLocal0.Position) < 1e-4f
                    && Quat.AngleDegrees(MeshEdit.GetBoneBind(m, kChild, BindSpace.Local).Rotation, childLocal0.Rotation) < 0.01f,
                "a world bind rotation with children following (their locals stay)");

            bind.IsWorldSpace = false;
            bind.ChildrenFollow = false;
            var w1 = MeshEdit.GetBoneBind(mesh, k, BindSpace.World);
            Step(ctx, doc, $"Move bone '{kName}' (local)", () =>
            {
                bind.Position[1].BeginInteraction();
                bind.Position[1].Value += 0.01;
                bind.Position[1].Value += 0.01;
                bind.Position[1].Value += 0.01;
                bind.Position[1].EndInteraction();
            }, m => !MeshEdit.GetBoneBind(m, k, BindSpace.World).Equals(w1), "a spinner run on a bind coordinate");
        }
        finally
        {
            bind.IsWorldSpace = wasWorld;
            bind.ChildrenFollow = wasFollow;
        }

        // Move down / up (through the reorder warning, confirmed by the test's prompt).
        string b2 = mesh.Bones[2].Name.Text;
        doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.Bone, Index: 2));
        Step(ctx, doc, $"Move bone '{b2}' down", () => tools.MoveBoneDownCommand.Execute(null),
            m => m.Bones[3].Name.Text == b2 && m.Bones[2].Name.Text == mesh.Bones[3].Name.Text && doc.Structure.Selected?.Node is { Kind: MeshNodeKind.Bone, Index: 3 },
            "Move Bone Down (the moved bone stays selected at its new index)");
        doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.Bone, Index: 2));
        Step(ctx, doc, $"Move bone '{b2}' up", () => tools.MoveBoneUpCommand.Execute(null),
            m => m.Bones[1].Name.Text == b2, "Move Bone Up");
        var bone2 = Editor<BoneEditor>(doc, new MeshNodeRef(MeshNodeKind.Bone, Index: 2))!;
        Step(ctx, doc, $"Move bone '{b2}' down", () => bone2.MoveDownCommand.Execute(null), m => m.Bones[3].Name.Text == b2, "the bone editor's Move down button");
    }

    private static bool IsDescendant(Skeleton s, int bone, int ancestor)
    {
        for (int a = s.EffectiveParents[bone], guard = 0; a >= 0 && guard <= s.Count; a = s.EffectiveParents[a], guard++)
        {
            if (a == ancestor) return true;
        }
        return false;
    }

    // ── Reorder + conform ────────────────────────────────────────────────────

    private static async Task ReorderChecks(SelfTestContext ctx, MeshDocumentViewModel doc, string? corpus, List<DocumentViewModel> opened)
    {
        string? clipPath = corpus is null ? null : Path.Combine(corpus, "ult2_walk.rfa");
        if (clipPath is null || !File.Exists(clipPath))
        {
            ctx.Log("selftest meshedit: ult2_walk.rfa is absent; reorder + conform skipped");
            return;
        }
        var clipDoc = ctx.Model.Documents.OfType<ClipDocumentViewModel>().FirstOrDefault(d => string.Equals(d.FilePath, clipPath, StringComparison.OrdinalIgnoreCase));
        if (clipDoc is null)
        {
            clipDoc = ctx.Model.OpenFile(clipPath) as ClipDocumentViewModel;
            if (clipDoc is null)
            {
                ctx.Check(false, "ult2_walk.rfa opens as a clip document");
                return;
            }
            opened.Add(clipDoc);
            await ctx.SettleAsync();
        }
        var mesh = doc.Current;
        var clip = clipDoc.Current;
        if (clip.BoneCount != mesh.Bones.Length)
        {
            ctx.Log($"selftest meshedit: {clipDoc.DisplayName} has {clip.BoneCount} bones, the mesh {mesh.Bones.Length}; reorder + conform skipped");
            return;
        }
        var previewBefore = clipDoc.PreviewLibraryMesh;
        clipDoc.UsePreviewMesh(mesh, doc.DisplayName, doc.Folder);
        int n = mesh.Bones.Length;
        var order = Enumerable.Range(0, n).Reverse().ToArray();
        var vm = new ReorderBonesViewModel(doc, order, null);
        var item = vm.Clips.FirstOrDefault(c => ReferenceEquals(c.Document, clipDoc));
        ctx.Check(item is { Conform: true, CanConform: true } && vm.HasChanges && vm.Warning.Contains("by index", StringComparison.Ordinal),
            $"the reorder warning says clips address bones by index and lists {clipDoc.DisplayName} to conform ({vm.Clips.Count} clip(s))");

        int meshSteps = doc.History.UndoLabels.Count, clipSteps = clipDoc.History.UndoLabels.Count;
        bool applied = vm.Apply();
        var reordered = doc.Current;
        var conformed = clipDoc.Current;
        ctx.Check(applied && doc.History.UndoLabels.Count == meshSteps + 1 && doc.UndoLabel == "Reorder bones"
            && clipDoc.History.UndoLabels.Count == clipSteps + 1 && clipDoc.UndoLabel == $"Conform to the new bone order of {doc.DisplayName}"
            && ReferenceEquals(clipDoc.PreviewMesh, reordered),
            $"reorder + conform: one step in the mesh ('{doc.UndoLabel}') and one in the clip ('{clipDoc.UndoLabel}'), which previews the reordered mesh");

        // The pose is unchanged: every bone's FK world and skin matrix, at several times.
        var r = MeshEdit.ReorderBones(mesh, order);
        var oldSkeleton = Skeleton.FromFile(mesh);
        var newSkeleton = Skeleton.FromFile(reordered);
        var before = new Pose(oldSkeleton);
        var after = new Pose(newSkeleton);
        var skinBefore = new Matrix4x4[n];
        var skinAfter = new Matrix4x4[n];
        float worstPos = 0, worstRot = 0, worstSkin = 0;
        for (int step = 0; step <= 6; step++)
        {
            float t = clip.StartTime + (clip.EndTime - clip.StartTime) * step / 6f;
            before.Sample(clip, t);
            after.Sample(conformed, t);
            Skinning.ComputeSkinMatrices(oldSkeleton, before.World, skinBefore);
            Skinning.ComputeSkinMatrices(newSkeleton, after.World, skinAfter);
            for (int b = 0; b < n; b++)
            {
                int nb = r.OldToNew[b];
                worstPos = Math.Max(worstPos, Vector3.Distance(before.World[b].Position, after.World[nb].Position));
                worstRot = Math.Max(worstRot, Quat.AngleDegrees(before.World[b].Rotation, after.World[nb].Rotation));
                var probe = new Vector3(0.1f, 1.2f, -0.3f);
                worstSkin = Math.Max(worstSkin, Vector3.Distance(Vector3.Transform(probe, skinBefore[b]), Vector3.Transform(probe, skinAfter[nb])));
            }
        }
        // Positions compare exactly; the angle of two equal quaternions can come out a hair above 0 in float.
        ctx.Check(worstPos == 0 && worstSkin == 0 && worstRot < 1e-3f,
            $"after reorder + conform every bone's FK pose and skin transform is unchanged (worst {worstPos:G3} m, {worstRot:G3}°, skin {worstSkin:G3} m)");

        doc.Undo();
        ctx.Check(ReferenceEquals(doc.Current, mesh) && ReferenceEquals(clipDoc.PreviewMesh, mesh), "undo of the reorder restores the mesh, and the conformed clip's preview follows it");
        clipDoc.Undo();
        ctx.Check(ReferenceEquals(clipDoc.Current, clip), "undo in the clip tab restores the clip exactly");
        if (previewBefore is not null) clipDoc.UsePreviewMesh(previewBefore);
        await ctx.SettleAsync();
    }

    // ── Collision spheres ────────────────────────────────────────────────────

    private static void SphereChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshCommands tools)
    {
        var mesh = doc.Current;
        int count = mesh.CollisionSpheres.Count();
        if (count == 0)
        {
            Step(ctx, doc, "Add collision sphere 'sphere1'", () => tools.AddSphereCommand.Execute(null), m => m.CollisionSpheres.Count() == 1, "Add Collision Sphere on a mesh without spheres");
            return;
        }
        var s0 = mesh.CollisionSpheres.First();
        string name = s0.Name.Text;
        var editor = Editor<SphereEditor>(doc, new MeshNodeRef(MeshNodeKind.CollisionSphere, Index: 0));
        if (editor is null)
        {
            ctx.Check(false, "selecting a collision sphere shows the sphere editor");
            return;
        }
        Step(ctx, doc, $"Set radius of '{name}'", () => editor.Radius.Value = s0.Radius + 0.05, m => Math.Abs(m.CollisionSpheres.First().Radius - (s0.Radius + 0.05f)) < 1e-5f);
        Step(ctx, doc, $"Move sphere '{name}'", () => editor.Position[2].Value = s0.Position.Z + 0.1, m => Math.Abs(m.CollisionSpheres.First().Position.Z - (s0.Position.Z + 0.1f)) < 1e-5f);
        Step(ctx, doc, $"Rename sphere '{name}' to 'probe'", () => editor.Name.Text = "probe", m => m.CollisionSpheres.First().Name.Text == "probe");
        int otherBone = s0.BoneIndex == 0 ? 1 : 0;
        if (mesh.Bones.Length > 1)
            Step(ctx, doc, $"Attach sphere '{name}' to '{mesh.Bones[otherBone].Name.Text}'", () => editor.Bone.Pick(otherBone), m => m.CollisionSpheres.First().BoneIndex == otherBone);
        Refused(ctx, doc, "a 30-character sphere name", () => editor.Name.Text = new string('s', 30), () => editor.HasError);

        string added = MeshCommands.UniqueName(mesh.CollisionSpheres.Select(x => x.Name.Text), "sphere", 23);
        Step(ctx, doc, $"Add collision sphere '{added}'", () => tools.AddSphereCommand.Execute(null),
            m => m.CollisionSpheres.Count() == count + 1 && m.Header.CollisionSphereCount == count + 1
                && doc.Structure.Selected?.Node is { Kind: MeshNodeKind.CollisionSphere } sel && sel.Index == count,
            "Add Collision Sphere (the new sphere is selected)");
        doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.CollisionSphere, Index: 0));
        Step(ctx, doc, $"Duplicate sphere '{name}'", () => tools.DuplicateSphereCommand.Execute(null),
            m => m.CollisionSpheres.Count() == count + 1 && m.CollisionSpheres.Last().Radius == s0.Radius && m.CollisionSpheres.Last().BoneIndex == s0.BoneIndex,
            "Duplicate Collision Sphere");
        doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.CollisionSphere, Index: 0));
        Step(ctx, doc, $"Remove sphere '{name}'", () => tools.RemoveSelectedCommand.Execute(null),
            m => m.CollisionSpheres.Count() == count - 1 && (count == 1 || doc.Structure.Selected?.Node is { Kind: MeshNodeKind.CollisionSphere, Index: 0 }),
            "Remove Selected on a sphere (the next sphere is selected)");
    }

    // ── Prop points ──────────────────────────────────────────────────────────

    private static void PropChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshCommands tools)
    {
        var start = doc.Current;
        int startSteps = doc.History.UndoLabels.Count;
        int count = MeshEdit.PropPointCount(start);
        string name = MeshCommands.UniqueName(start.Submeshes.SelectMany(s => s.Lods).SelectMany(l => l.PropPoints).Select(p => p.Name.Text), "prop", 67);
        int steps = doc.History.UndoLabels.Count;
        tools.AddPropPointCommand.Execute(null);
        ctx.Check(doc.History.UndoLabels.Count == steps + 1 && doc.UndoLabel == $"Add prop point '{name}'" && MeshEdit.PropPointCount(doc.Current) == count + 1
            && doc.Current.Submeshes.SelectMany(s => s.Lods).All(l => l.PropPoints.Length == count + 1)
            && doc.Structure.Selected?.Node is { Kind: MeshNodeKind.PropPoint } sel && sel.Index == count,
            $"Add Prop Point adds it to every LOD in one step ('{doc.UndoLabel}') and selects it");
        var editor = doc.Structure.Editor as PropPointEditor;
        if (editor is null)
        {
            ctx.Check(false, "the new prop point shows the prop point editor");
        }
        else
        {
            Step(ctx, doc, $"Move prop point '{name}'", () => editor.Position[1].Value = 0.25, m => Math.Abs(PropPointEditor.Prop(m, count).Position.Y - 0.25f) < 1e-6f);
            Step(ctx, doc, $"Rotate prop point '{name}'", () => editor.Orientation[1].Value = 30,
                m => Math.Abs(Quat.ToEulerDegrees(Quat.Conj(Quat.Normalize(PropPointEditor.Prop(m, count).Rotation))).Y - 30) < 0.05);
            Step(ctx, doc, $"Rename prop point '{name}' to 'probe_prop'", () => editor.Name.Text = "probe_prop", m => PropPointEditor.Prop(m, count).Name.Text == "probe_prop");
            if (start.Bones.Length > 1)
                Step(ctx, doc, $"Attach prop point '{name}' to '{start.Bones[1].Name.Text}'", () => editor.Bone.Pick(1), m => PropPointEditor.Prop(m, count).ParentIndex == 1);
            Step(ctx, doc, $"Remove prop point '{name}'", () => tools.RemoveSelectedCommand.Execute(null), m => MeshEdit.PropPointCount(m) == count, "Remove Selected on a prop point");
        }
        while (doc.History.UndoLabels.Count > startSteps && doc.CanUndo) doc.Undo();
        ctx.Check(ReferenceEquals(doc.Current, start), "undoing the prop point steps restores the exact snapshot");
    }

    // ── Materials ────────────────────────────────────────────────────────────

    private static void MaterialChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshCommands tools)
    {
        var mesh = doc.Current;
        var sub = mesh.Submeshes.First();
        if (sub.Materials.Length == 0)
        {
            ctx.Log("selftest meshedit: submesh 0 has no material; material checks skipped");
            return;
        }
        var editor = Editor<MaterialEditor>(doc, new MeshNodeRef(MeshNodeKind.Material, 0, -1, 0));
        if (editor is null)
        {
            ctx.Check(false, "selecting a material shows the material editor");
            return;
        }
        string oldName = sub.Materials[0].DiffuseMap.Text;
        Step(ctx, doc, "Set texture of material 0 to 'probe_tex.tga'", () => editor.Texture.Text = "probe_tex.tga",
            m => m.Submeshes.First().Materials[0].DiffuseMap.Text == "probe_tex.tga"
                && !m.Submeshes.First().Lods.SelectMany(l => l.Textures).Any(t => t.MaterialIndex == 0 && string.Equals(t.FileName, oldName, StringComparison.OrdinalIgnoreCase)),
            "a texture rename (the LOD entries with the old name follow)");
        float emissive = sub.Materials[0].Emissive;
        double target = emissive > 0.4f && emissive < 0.6f ? 0.9 : 0.5;
        Step(ctx, doc, "Set emissive of material 0", () => editor.Emissive.Value = target, m => Math.Abs(m.Submeshes.First().Materials[0].Emissive - (float)target) < 1e-6f);
        Step(ctx, doc, "Set flags of material 0 to 0x1F", () => editor.Flags.Text = "0x1F", m => m.Submeshes.First().Materials[0].Flags == 0x1F);
        Refused(ctx, doc, "flags that are not a number", () => editor.Flags.Text = "lots", () => editor.HasError);
        Refused(ctx, doc, "a 40-character texture name", () => editor.Texture.Text = new string('t', 40), () => editor.HasError);
        tools.TexturePrompt = vm =>
        {
            vm.SetEntries([new TextureEntry("browsed.tga", "test"), new TextureEntry("other.tga", "test")]);
            vm.Filter = "brow";
            vm.Selected = vm.Items.FirstOrDefault();
            return vm.CanPick && vm.Items.Count == 1 ? vm.Selected!.Name : null;
        };
        Step(ctx, doc, "Set texture of material 0 to 'browsed.tga'", () => editor.BrowseCommand.Execute(null),
            m => m.Submeshes.First().Materials[0].DiffuseMap.Text == "browsed.tga", "Browse… (the texture browser's pick, filtered)");
    }

    // ── LOD and submesh ──────────────────────────────────────────────────────

    private static void LodAndSubmeshChecks(SelfTestContext ctx, MeshDocumentViewModel doc)
    {
        var sub = doc.Current.Submeshes.First();
        int l = sub.Lods.Length > 1 ? 1 : 0;
        var lod = Editor<LodEditor>(doc, new MeshNodeRef(MeshNodeKind.Lod, 0, l));
        if (lod is null) ctx.Check(false, "selecting a LOD shows the LOD editor");
        else
        {
            float d = l < sub.LodDistances.Length ? sub.LodDistances[l] : 0;
            Step(ctx, doc, $"Set LOD {l} distance of submesh '{sub.Name.Text}'", () => lod.Distance.Value = d + 5, m => Math.Abs(m.Submeshes.First().LodDistances[l] - (d + 5)) < 1e-4f);
        }
        var submesh = Editor<SubmeshEditor>(doc, new MeshNodeRef(MeshNodeKind.Submesh, 0));
        if (submesh is null) ctx.Check(false, "selecting a submesh shows the submesh editor");
        else
        {
            Step(ctx, doc, "Rename submesh 0 to 'probe_body'", () => submesh.Name.Text = "probe_body",
                m => m.Submeshes.First().Name.Text == "probe_body" && m.Submeshes.First().Trailers.All(t => t.Name.Text != sub.Name.Text || sub.Name.Text == "probe_body"));
        }
    }

    // ── Quick fix ────────────────────────────────────────────────────────────

    private static void QuickFixChecks(SelfTestContext ctx, MeshDocumentViewModel doc)
    {
        var start = doc.Current;
        if (start.Bones.Length < 2) return;
        int steps = doc.History.UndoLabels.Count;
        // Give bone 1 bone 0's name: V3C023 offers a unique suffix.
        string name0 = start.Bones[0].Name.Text;
        var editor = Editor<BoneEditor>(doc, new MeshNodeRef(MeshNodeKind.Bone, Index: 1))!;
        editor.Name.Text = name0;
        var d = doc.Diagnostics.FirstOrDefault(x => x.Code == MeshRules.DuplicateBoneName);
        var fix = d?.QuickFixes.FirstOrDefault(f => f.Kind == QuickFixKind.Edit);
        ctx.Check(d is not null && fix is not null && doc.CanApplyQuickFix(fix), $"a duplicated bone name is reported with a quick fix ('{fix?.Title}')");
        if (d is not null && fix is not null)
        {
            var broken = doc.Current;
            int count = doc.History.UndoLabels.Count;
            doc.ApplyQuickFix(fix, d);
            ctx.Check(doc.History.UndoLabels.Count == count + 1 && doc.UndoLabel == fix.Title && !doc.Diagnostics.Any(x => x.Code == MeshRules.DuplicateBoneName),
                $"the quick fix is one undo step ('{doc.UndoLabel}') and the problem is gone");
            doc.Undo();
            ctx.Check(ReferenceEquals(doc.Current, broken), "undo of the quick fix restores the exact snapshot");
        }
        while (doc.History.UndoLabels.Count > steps && doc.CanUndo) doc.Undo();
        ctx.Check(ReferenceEquals(doc.Current, start), "the quick fix check leaves the mesh as it found it");
    }

    /// <summary>
    /// The "Locate the file…" quick fix of V3C021: Core's diagnostic for a texture the resolver cannot find
    /// (here every name is reported missing) shows it enabled in its Problems row; it opens the material
    /// editor's texture browser on the owning material (the test's prompt answers) and the pick becomes that
    /// material's texture name as one undo step, labelled as the material editor labels it. Cancel adds no step.
    /// </summary>
    private static void LocateChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshCommands tools)
    {
        var start = doc.Current;
        var d = MeshLinter.Analyze(start, new MeshLintContext { TextureExists = _ => false }).FirstOrDefault(x => x.Code == MeshRules.MissingTexture);
        var fix = d?.QuickFixes.FirstOrDefault(f => f.Kind == QuickFixKind.LocateFile);
        if (d is null || fix is null || d.Location.MeshNode is not { Kind: MeshNodeKind.Texture } t)
        {
            ctx.Check(false, "a texture the resolver cannot find is reported (V3C021) with 'Locate the file…'");
            return;
        }
        var row = new DiagnosticViewModel(doc, d);
        ctx.Check(row.QuickFixes.Any(b => b.Title == fix.Title && b.ApplyCommand.CanExecute(null)),
            $"locate quick fix: V3C021's '{fix.Title}' is shown in its Problems row and enabled");
        var sub = start.Submeshes.ElementAt(t.Submesh);
        var entry = sub.Lods[t.Lod].Textures[t.Index];
        int mi = entry.MaterialIndex;
        string oldName = sub.Materials[mi].DiffuseMap.Text;
        bool follows = string.Equals(entry.FileName, oldName, StringComparison.OrdinalIgnoreCase);
        string? offered = null;
        tools.TexturePrompt = vm =>
        {
            offered = vm.Current;
            return "located.tga";
        };
        Step(ctx, doc, $"Set texture of material {mi} to 'located.tga'", () => doc.ApplyQuickFix(fix, d),
            m => m.Submeshes.ElementAt(t.Submesh).Materials[mi].DiffuseMap.Text == "located.tga"
                && (!follows || m.Submeshes.ElementAt(t.Submesh).Lods[t.Lod].Textures[t.Index].FileName == "located.tga")
                && offered == oldName,
            "locate quick fix: the texture browser opens on the material and the pick becomes its texture name");
        tools.TexturePrompt = _ => null;
        int steps = doc.History.UndoLabels.Count;
        bool changed = doc.LocateTexture(d);
        ctx.Check(!changed && doc.History.UndoLabels.Count == steps && ReferenceEquals(doc.Current, start), "locate quick fix: cancelling the browser changes nothing");
    }

    // ── Read-only ────────────────────────────────────────────────────────────

    private static void ReadOnlyChecks(SelfTestContext ctx, MeshDocumentViewModel doc)
    {
        var before = doc.Current;
        ctx.Check(doc.IsReadOnly && doc.Structure.IsReadOnly, $"{doc.DisplayName} opens read-only");
        var editor = Editor<SubmeshEditor>(doc, new MeshNodeRef(MeshNodeKind.Submesh, 0));
        ctx.Check(editor is { IsReadOnly: true, IsEditable: false } && editor.Name.IsReadOnly, "the read-only document's editors are disabled");
        if (editor is not null) editor.Name.Text = "changed";
        var lod = Editor<LodEditor>(doc, new MeshNodeRef(MeshNodeKind.Lod, 0, 0));
        if (lod is not null) lod.Distance.Value = 123;
        ctx.Check(ReferenceEquals(doc.Current, before) && !doc.CanUndo, "typing into a read-only editor changes nothing and adds no step");
        if (MeshLinter.Analyze(before, new MeshLintContext { TextureExists = _ => false }).FirstOrDefault(x => x.Code == MeshRules.MissingTexture) is { } missing
            && missing.QuickFixes.FirstOrDefault(f => f.Kind == QuickFixKind.LocateFile) is { } locate)
        {
            ctx.Check(!doc.CanApplyQuickFix(locate) && !doc.LocateTexture(missing) && ReferenceEquals(doc.Current, before),
                "'Locate the file…' is unavailable on a read-only document");
        }
    }
}
