using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Views.Dialogs.MeshEditing;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.ViewModels.MeshEditing;

/// <summary>
/// The Mesh menu's editing commands for the active mesh document (exposed on <see cref="RfaWorkspace.MeshTools"/>):
/// add / duplicate / remove collision spheres and prop points, rename the selected node (F2), move a
/// bone up or down and reorder the bones (with the warning that clips address bones by index and the
/// offer to conform the open clips previewing the mesh). Enabled only for an editable (.v3c) mesh
/// document. The Structure tab's editor buttons are these same commands.
/// </summary>
public sealed class MeshCommands
{
    private readonly RfaWorkspace _shell;
    private readonly List<RelayCommand> _all = [];
    private MeshDocumentViewModel? _watched;

    public MeshCommands(RfaWorkspace shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        ReorderPrompt = m => ReorderBonesDialog.ShowModal(_shell.Dialogs.Owner, m);
        TexturePrompt = m => TextureBrowserDialog.ShowModal(_shell.Dialogs.Owner, m);
        AddSphereCommand = Make(() => AddSphere(Editable!), () => Editable is not null);
        DuplicateSphereCommand = Make(() => DuplicateSphere(Editable!), () => SelectedOf(MeshNodeKind.CollisionSphere) is not null);
        AddPropPointCommand = Make(() => AddPropPoint(Editable!), () => Editable is { } d && d.Current.Submeshes.Any(s => s.Lods.Length > 0));
        RemoveSelectedCommand = Make(() => RemoveSelected(Editable!), () => SelectedOf(MeshNodeKind.CollisionSphere) is not null || SelectedOf(MeshNodeKind.PropPoint) is not null);
        RenameSelectedCommand = Make(() => RenameSelected(Editable!),
            () => Editable is { } d && ((d.Structure.Editor is { } e && HasName(e)) || OwningMaterial(d) is not null));
        ReorderBonesCommand = Make(() => ReorderBones(Editable!, null), () => Editable is { } d && d.Current.Bones.Length > 1);
        MoveBoneUpCommand = Make(() => MoveSelectedBone(-1), () => SelectedBone() is { } b && b > 0);
        MoveBoneDownCommand = Make(() => MoveSelectedBone(+1), () => SelectedBone() is { } b && Editable is { } d && b < d.Current.Bones.Length - 1);
        _shell.PropertyChanged += OnShellPropertyChanged;
        Watch(_shell.ActiveDocument as MeshDocumentViewModel);
    }

    /// <summary>Mesh › Add Collision Sphere (on the selected bone, or the selected sphere's).</summary>
    public RelayCommand AddSphereCommand { get; }

    /// <summary>Mesh › Duplicate Collision Sphere.</summary>
    public RelayCommand DuplicateSphereCommand { get; }

    /// <summary>Mesh › Add Prop Point.</summary>
    public RelayCommand AddPropPointCommand { get; }

    /// <summary>Mesh › Remove Selected (a sphere or prop point).</summary>
    public RelayCommand RemoveSelectedCommand { get; }

    /// <summary>
    /// Mesh › Rename Selected (F2): focuses the selected node's name box — a bone's, collision sphere's,
    /// prop point's or submesh's name, a material's texture name; a LOD texture entry selects its material
    /// first. Disabled on every other node (header, LOD, batch, groups), which has no name to change.
    /// </summary>
    public RelayCommand RenameSelectedCommand { get; }

    /// <summary>Mesh › Reorder Bones….</summary>
    public RelayCommand ReorderBonesCommand { get; }

    /// <summary>Mesh › Move Bone Up.</summary>
    public RelayCommand MoveBoneUpCommand { get; }

    /// <summary>Mesh › Move Bone Down.</summary>
    public RelayCommand MoveBoneDownCommand { get; }

    /// <summary>
    /// Shows the reorder warning and returns true when the user confirmed (the view-model applied it).
    /// Replaceable so a self-test can confirm without a modal; the default shows <see cref="ReorderBonesDialog"/>.
    /// </summary>
    internal Func<ReorderBonesViewModel, bool> ReorderPrompt { get; set; }

    /// <summary>Shows the texture browser and returns the picked name, or null. Replaceable for self-tests.</summary>
    internal Func<TextureBrowserViewModel, string?> TexturePrompt { get; set; }

    /// <summary>The active mesh document when it may be edited, else null.</summary>
    private MeshDocumentViewModel? Editable => _shell.ActiveDocument is MeshDocumentViewModel { IsReadOnly: false } d ? d : null;

    private MeshNodeRef? SelectedOf(MeshNodeKind kind) =>
        Editable?.Structure.Selected?.Node is { } n && n.Kind == kind ? n : null;

    /// <summary>The bone selected in the structure tree, else the active bone of the viewport selection.</summary>
    private int? SelectedBone()
    {
        if (Editable is not { } d) return null;
        if (d.Structure.Selected?.Node is { Kind: MeshNodeKind.Bone } n) return n.Index;
        return d.Selection.Active >= 0 && d.Selection.Active < d.Current.Bones.Length ? d.Selection.Active : null;
    }

    private static bool HasName(MeshNodeEditor editor) => editor is BoneEditor or SphereEditor or PropPointEditor or SubmeshEditor or MaterialEditor;

    /// <summary>
    /// The material owning the LOD texture entry selected in the tree, or null. A LOD texture entry has
    /// no editor of its own: its name is the material's texture name (the entries follow a rename there).
    /// </summary>
    private static MeshNodeRef? OwningMaterial(MeshDocumentViewModel document)
    {
        if (document.Structure.Selected?.Node is not { Kind: MeshNodeKind.Texture } t) return null;
        var mesh = document.Current;
        if (mesh.Submeshes.ElementAtOrDefault(t.Submesh) is not { } sub || (uint)t.Lod >= (uint)sub.Lods.Length
            || (uint)t.Index >= (uint)sub.Lods[t.Lod].Textures.Length) return null;
        int material = sub.Lods[t.Lod].Textures[t.Index].MaterialIndex;
        return MaterialEditor.Find(mesh, t.Submesh, material) is null ? null : new MeshNodeRef(MeshNodeKind.Material, t.Submesh, -1, material);
    }

    /// <summary>Re-queries every command (the active document, its selection or its state changed).</summary>
    public void Refresh()
    {
        foreach (var c in _all) c.RaiseCanExecuteChanged();
    }

    private RelayCommand Make(Action execute, Func<bool> canExecute)
    {
        var command = new RelayCommand(() =>
        {
            _shell.CommitPendingEdits();
            execute();
            Refresh();
        }, canExecute);
        _all.Add(command);
        return command;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RfaWorkspace.ActiveDocument) or nameof(RfaWorkspace.HasDocument))) return;
        Watch(_shell.ActiveDocument as MeshDocumentViewModel);
        Refresh();
    }

    private void Watch(MeshDocumentViewModel? document)
    {
        if (ReferenceEquals(document, _watched)) return;
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnDocumentChanged;
            _watched.Structure.PropertyChanged -= OnDocumentChanged;
        }
        _watched = document;
        if (document is not null)
        {
            document.PropertyChanged += OnDocumentChanged;
            document.Structure.PropertyChanged += OnDocumentChanged;
        }
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MeshStructureViewModel.Selected) or nameof(MeshStructureViewModel.Editor) or nameof(DocumentViewModel.CanUndo))
            Refresh();
    }

    /// <summary>
    /// Called by a mesh document after its snapshot changed: open clip documents previewing the snapshot it
    /// replaced (a clip conformed to a reorder previews the edited mesh in memory) follow it, so undoing
    /// or editing the mesh keeps them consistent.
    /// </summary>
    public void OnMeshSnapshotChanged(MeshDocumentViewModel document, V3dFile previous, V3dFile current)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (ReferenceEquals(previous, current)) return;
        foreach (var clip in _shell.Documents.OfType<ClipDocumentViewModel>())
        {
            if (ReferenceEquals(clip.PreviewMesh, previous)) clip.UsePreviewMesh(current, document.DisplayName, document.Folder);
        }
    }

    // ── Collision spheres ────────────────────────────────────────────────────

    /// <summary>Adds a sphere on the selected bone (or the selected sphere's bone, or the root) and selects it.</summary>
    internal bool AddSphere(MeshDocumentViewModel document)
    {
        var mesh = document.Current;
        var node = document.Structure.Selected?.Node;
        int bone = node is { Kind: MeshNodeKind.Bone } b ? b.Index
            : node is { Kind: MeshNodeKind.CollisionSphere } s && mesh.CollisionSpheres.ElementAtOrDefault(s.Index) is { } sphere ? sphere.BoneIndex
            : RootBone(mesh);
        string name = UniqueName(mesh.CollisionSpheres.Select(x => x.Name.Text), "sphere", V3dCollisionSphere.NameSize - 1);
        int index = mesh.CollisionSpheres.Count();
        return TryApply(document, $"Add collision sphere '{name}'",
            m => MeshEdit.AddCollisionSphere(m, name, bone, Vector3.Zero, 0.2f),
            new MeshNodeRef(MeshNodeKind.CollisionSphere, Index: index));
    }

    /// <summary>Duplicates the selected sphere (same bone, position and radius) and selects the copy.</summary>
    internal bool DuplicateSphere(MeshDocumentViewModel document)
    {
        if (document.Structure.Selected?.Node is not { Kind: MeshNodeKind.CollisionSphere } node) return false;
        var mesh = document.Current;
        if (mesh.CollisionSpheres.ElementAtOrDefault(node.Index) is not { } sphere) return false;
        string name = UniqueName(mesh.CollisionSpheres.Select(x => x.Name.Text), sphere.Name.Text, V3dCollisionSphere.NameSize - 1);
        int index = mesh.CollisionSpheres.Count();
        return TryApply(document, $"Duplicate sphere '{sphere.Name.Text}'",
            m => MeshEdit.AddCollisionSphere(m, name, sphere.BoneIndex, sphere.Position, sphere.Radius > 0 ? sphere.Radius : 0.2f),
            new MeshNodeRef(MeshNodeKind.CollisionSphere, Index: index));
    }

    // ── Prop points ──────────────────────────────────────────────────────────

    /// <summary>Adds a prop point on the selected bone (or the root) in every LOD, and selects it.</summary>
    internal bool AddPropPoint(MeshDocumentViewModel document)
    {
        var mesh = document.Current;
        var node = document.Structure.Selected?.Node;
        int bone = node is { Kind: MeshNodeKind.Bone } b ? b.Index
            : node is { Kind: MeshNodeKind.PropPoint } p && PropPointEditor.Find(mesh, p.Index) is { } prop ? prop.ParentIndex
            : RootBone(mesh);
        var existing = mesh.Submeshes.SelectMany(s => s.Lods).SelectMany(l => l.PropPoints).Select(x => x.Name.Text);
        string name = UniqueName(existing, "prop", V3dPropPoint.NameSize - 1);
        int index = MeshEdit.PropPointCount(mesh);
        return TryApply(document, $"Add prop point '{name}'",
            m => MeshEdit.AddPropPoint(m, name, bone, Quaternion.Identity, Vector3.Zero),
            new MeshNodeRef(MeshNodeKind.PropPoint, 0, 0, index));
    }

    // ── Selection-based ──────────────────────────────────────────────────────

    /// <summary>Removes the selected sphere or prop point; the next one (or the previous) is selected.</summary>
    internal bool RemoveSelected(MeshDocumentViewModel document)
    {
        var mesh = document.Current;
        switch (document.Structure.Selected?.Node)
        {
            case { Kind: MeshNodeKind.CollisionSphere } s when mesh.CollisionSpheres.ElementAtOrDefault(s.Index) is { } sphere:
            {
                int remaining = mesh.CollisionSpheres.Count() - 1;
                return TryApply(document, $"Remove sphere '{sphere.Name.Text}'", m => MeshEdit.RemoveCollisionSphere(m, s.Index),
                    remaining > 0 ? new MeshNodeRef(MeshNodeKind.CollisionSphere, Index: Math.Min(s.Index, remaining - 1)) : NoNode);
            }
            case { Kind: MeshNodeKind.PropPoint } p when PropPointEditor.Find(mesh, p.Index) is { } prop:
            {
                int remaining = Math.Max(MeshEdit.PropPointCount(mesh), p.Index + 1) - 1;
                return TryApply(document, $"Remove prop point '{prop.Name.Text}'", m => MeshEdit.RemovePropPoint(m, p.Index),
                    remaining > 0 ? new MeshNodeRef(MeshNodeKind.PropPoint, 0, 0, Math.Min(p.Index, remaining - 1)) : NoNode);
            }
            default:
                return false;
        }
    }

    /// <summary>A node address that selects nothing after the rebuild (the list became empty).</summary>
    internal static readonly MeshNodeRef NoNode = new(MeshNodeKind.Header, -2, -2, -2);

    /// <summary>
    /// Shows the inspector's Structure tab and puts the keyboard in the selected node's name box (for a
    /// LOD texture entry, the owning material's texture name box).
    /// </summary>
    internal void RenameSelected(MeshDocumentViewModel document)
    {
        _shell.IsInspectorVisible = true;
        document.SelectedInspectorTab = document.InspectorTabs.FirstOrDefault(t => t.Id == "structure") ?? document.SelectedInspectorTab;
        if (OwningMaterial(document) is { } material) document.Structure.Reveal(material);
        document.Structure.RequestRename();
    }

    private void MoveSelectedBone(int delta)
    {
        if (Editable is { } d && SelectedBone() is { } bone) MoveBone(d, bone, delta);
    }

    // ── Bone order ───────────────────────────────────────────────────────────

    /// <summary>Moves a bone one place up (-1) or down (+1) in the index order, through the reorder warning.</summary>
    internal bool MoveBone(MeshDocumentViewModel document, int bone, int delta)
    {
        int n = document.Current.Bones.Length;
        int other = bone + delta;
        if (document.IsReadOnly || bone < 0 || bone >= n || other < 0 || other >= n) return false;
        var order = Enumerable.Range(0, n).ToArray();
        (order[bone], order[other]) = (order[other], order[bone]);
        string name = document.Current.Bones[bone].Name.Text;
        var model = new ReorderBonesViewModel(document, order, $"Move bone '{name}' {(delta < 0 ? "up" : "down")}", bone);
        return ReorderPrompt(model);
    }

    /// <summary>Opens the reorder warning with <paramref name="order"/> (identity when null) to edit and confirm.</summary>
    internal bool ReorderBones(MeshDocumentViewModel document, IReadOnlyList<int>? order)
    {
        if (document.IsReadOnly || document.Current.Bones.Length < 2) return false;
        int focus = document.Structure.Selected?.Node is { Kind: MeshNodeKind.Bone } n ? n.Index : -1;
        return ReorderPrompt(new ReorderBonesViewModel(document, order, null, focus));
    }

    // ── Textures ─────────────────────────────────────────────────────────────

    /// <summary>Opens the texture browser for <paramref name="document"/>; returns the picked name or null.</summary>
    internal string? PickTexture(MeshDocumentViewModel document, string current) =>
        TexturePrompt(new TextureBrowserViewModel(_shell, document.Resolver, current));

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool TryApply(MeshDocumentViewModel document, string label, Func<V3dFile, V3dFile> edit, MeshNodeRef select)
    {
        V3dFile next;
        try
        {
            next = edit(document.Current);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            string message = UserMessage(ex);
            document.ShowStatus($"{label}: {message}");
            if (document.Structure.Editor is { } editor) editor.ClearError();
            return false;
        }
        if (ReferenceEquals(next, document.Current)) return false;
        document.Structure.SelectAfterRebuild(select);
        return document.Apply(label, _ => next);
    }

    /// <summary>The first bone FK treats as a root, or -1 for a mesh without bones.</summary>
    internal static int RootBone(V3dFile mesh)
    {
        var bones = mesh.Bones;
        if (bones.Length == 0) return -1;
        var effective = Cairn.Rfa.Animation.ForwardKinematics.EffectiveParents([.. bones.Select(b => b.ParentIndex)]);
        int root = Array.IndexOf(effective, -1);
        return root < 0 ? 0 : root;
    }

    /// <summary><paramref name="stem"/>1, 2… (or <c>stem_2</c> for a copy), the first name not taken (case-insensitive), within <paramref name="maxLength"/>.</summary>
    internal static string UniqueName(IEnumerable<string> existing, string stem, int maxLength)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        bool copy = taken.Contains(stem);
        for (int k = copy ? 2 : 1; ; k++)
        {
            string suffix = (copy ? "_" : "") + k.ToString(CultureInfo.InvariantCulture);
            string baseText = stem.Length + suffix.Length > maxLength ? stem[..Math.Max(0, maxLength - suffix.Length)] : stem;
            string candidate = baseText + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>An exception's message without the " (Parameter 'x')" suffix ArgumentException appends.</summary>
    internal static string UserMessage(Exception ex)
    {
        string message = ex.Message;
        int at = ex is ArgumentException { ParamName: { } p } ? message.LastIndexOf($" (Parameter '{p}')", StringComparison.Ordinal) : -1;
        return at > 0 ? message[..at] : message;
    }

    internal RfaWorkspace Shell => _shell;
}
