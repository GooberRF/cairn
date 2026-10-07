using System.Globalization;
using System.Numerics;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.ViewModels.PoseEditing;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Ui.ViewModels.MeshEditing;

/// <summary>Builds the editor for a structure node, or null for a node that only has facts.</summary>
public static class MeshNodeEditors
{
    public static MeshNodeEditor? Create(MeshDocumentViewModel document, MeshNodeRef node)
    {
        var mesh = document.Current;
        try
        {
            return node.Kind switch
            {
                MeshNodeKind.Bone when node.Index >= 0 && node.Index < mesh.Bones.Length => new BoneEditor(document, node),
                MeshNodeKind.CollisionSphere when node.Index >= 0 && node.Index < mesh.CollisionSpheres.Count() => new SphereEditor(document, node),
                MeshNodeKind.PropPoint when PropPointEditor.Find(mesh, node.Index) is not null => new PropPointEditor(document, node),
                MeshNodeKind.Material when MaterialEditor.Find(mesh, node.Submesh, node.Index) is not null => new MaterialEditor(document, node),
                MeshNodeKind.Lod when mesh.Submeshes.ElementAtOrDefault(node.Submesh) is { } s && node.Lod >= 0 && node.Lod < s.Lods.Length => new LodEditor(document, node),
                MeshNodeKind.Submesh when mesh.Submeshes.ElementAtOrDefault(node.Submesh) is not null => new SubmeshEditor(document, node),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}

// ── Bone ─────────────────────────────────────────────────────────────────────

/// <summary>
/// A bone: name, parent (cycles refused by the Core), bind pose in local (parent) or world (model) terms
/// with "children follow", and its place in the index order (move up / down: a reorder with a warning).
/// </summary>
public sealed class BoneEditor : MeshNodeEditor
{
    // The space is a working preference: kept for the session, for every bone. "Children follow" is too, and the
    // mesh gizmos share it (RfaWorkspace.BindOptions).
    private static bool s_world;

    public BoneEditor(MeshDocumentViewModel document, MeshNodeRef node) : base(document, node)
    {
        int i = node.Index;
        System.ComponentModel.PropertyChangedEventManager.AddHandler(document.Shell.BindOptions, OnBindOptionsChanged, nameof(BindEditOptions.ChildrenFollow));
        Name = Add(new MeshTextField(this, "Name", "v3c.bone.name", "The bone's name (at most 23 Latin-1 characters). Clips address bones by index, so renaming does not change any clip; tools that map by name (retarget, conform, glTF) see the new name.",
            V3dBone.NameSize - 1, m => m.Bones[i].Name.Text, (m, t) => MeshEdit.RenameBone(m, i, t), t => $"Rename bone {i} to {t}"));
        Parent = Add(new MeshChoiceField(this, "Parent", "v3c.bone.parent",
            "The bone this one hangs from (none makes it a root). Its rest pose in the model stays where it is; a parent that would close a loop is refused.",
            m => BoneChoices(m, withNone: true, except: i), m => m.Bones[i].ParentIndex, (m, p) => MeshEdit.ReparentBone(m, i, p),
            c => c.Value < 0 ? $"Make bone '{BoneName(Document.Current, i)}' a root" : $"Reparent bone '{BoneName(Document.Current, i)}' to '{BoneName(Document.Current, c.Value)}'"));
        for (int a = 0; a < 3; a++)
        {
            int axis = a;
            Position.Add(Add(new MeshNumberField(this, "Bind position " + Axes[a], "v3c.bone.position", PositionTip,
                m => Component(MeshEdit.GetBoneBind(m, i, Space).Position, axis),
                (m, v) =>
                {
                    var b = MeshEdit.GetBoneBind(m, i, Space);
                    return MeshEdit.SetBoneBind(m, i, new Rigid(b.Rotation, WithComponent(b.Position, axis, (float)v)), Space, ChildrenFollow);
                },
                () => $"Move bone '{BoneName(Document.Current, i)}'" + Suffix(), decimals: 4, step: 0.01, suffix: "m") { Caption = Axes[a] }));
            Rotation.Add(Add(new MeshNumberField(this, "Bind rotation " + Angles[a], "v3c.bone.rotation", EulerConvention,
                m => Component(Quat.ToEulerDegrees(MeshEdit.GetBoneBind(m, i, Space).Rotation), axis),
                (m, v) =>
                {
                    var b = MeshEdit.GetBoneBind(m, i, Space);
                    return MeshEdit.SetBoneBind(m, i, new Rigid(WithEuler(b.Rotation, axis, v), b.Position), Space, ChildrenFollow);
                },
                () => $"Rotate bone '{BoneName(Document.Current, i)}'" + Suffix(), decimals: 2, step: 1, minimum: -360, maximum: 360, suffix: "°") { Caption = Angles[a] }));
        }
        MoveUpCommand = new RelayCommand(() => Tools.MoveBone(Document, i, -1), () => IsEditable && i > 0);
        MoveDownCommand = new RelayCommand(() => Tools.MoveBone(Document, i, +1), () => IsEditable && i < Document.Current.Bones.Length - 1);
        ReorderCommand = new RelayCommand(() => Tools.ReorderBones(Document, null), () => IsEditable && Document.Current.Bones.Length > 1);
    }

    private const string PositionTip =
        "The bone's rest (bind) position, in metres: relative to the parent's rest frame in Local, in model space in World. "
        + "The file stores the inverse bind; this is the pose the skin was bound to, so moving it moves the skinned mesh.";

    private string Suffix() => (s_world ? " (world)" : " (local)") + (ChildrenFollow ? " with its children" : "");

    private void OnBindOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Raise(nameof(ChildrenFollow));

    public MeshTextField Name { get; }

    public MeshChoiceField Parent { get; }

    /// <summary>Bind position X, Y, Z in the chosen space.</summary>
    public List<MeshNumberField> Position { get; } = [];

    /// <summary>Bind rotation as pitch, yaw, roll in the chosen space.</summary>
    public List<MeshNumberField> Rotation { get; } = [];

    /// <summary>The space the bind pose is shown and edited in.</summary>
    public BindSpace Space => s_world ? BindSpace.World : BindSpace.Local;

    public bool IsWorldSpace
    {
        get => s_world;
        set
        {
            if (s_world == value) return;
            s_world = value;
            RaiseAll(nameof(IsWorldSpace), nameof(IsLocalSpace), nameof(SpaceNote));
            Refresh();
        }
    }

    public bool IsLocalSpace
    {
        get => !s_world;
        set => IsWorldSpace = !value;
    }

    /// <summary>
    /// Descendants move rigidly with the bone (otherwise they stay where they are in the model); the same
    /// setting as the viewport tool bar's "Children" toggle (<see cref="RfaWorkspace.BindOptions"/>).
    /// </summary>
    public bool ChildrenFollow
    {
        get => Document.Shell.BindOptions.ChildrenFollow;
        set => Document.Shell.BindOptions.ChildrenFollow = value;
    }

    public string SpaceNote => s_world
        ? "Model space: where the bone sits in the mesh at rest."
        : Document.Current.Bones.ElementAtOrDefault(Node.Index).ParentIndex is >= 0 and var p && p < Document.Current.Bones.Length
            ? $"Relative to the parent's rest frame ({BoneName(Document.Current, p)})."
            : "This bone is a root: local is model space.";

    public string IndexText => Node.Index.ToString(CultureInfo.CurrentCulture) + " of " + Document.Current.Bones.Length.ToString(CultureInfo.CurrentCulture);

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand ReorderCommand { get; }

    public override bool Exists(V3dFile mesh) => Node.Index < mesh.Bones.Length;

    public override void Refresh()
    {
        base.Refresh();
        RaiseAll(nameof(SpaceNote), nameof(IndexText));
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        ReorderCommand.RaiseCanExecuteChanged();
    }
}

// ── Collision sphere ─────────────────────────────────────────────────────────

/// <summary>A collision sphere: name, bone, position (relative to the bone), radius; add, duplicate, remove.</summary>
public sealed class SphereEditor : MeshNodeEditor
{
    public SphereEditor(MeshDocumentViewModel document, MeshNodeRef node) : base(document, node)
    {
        int i = node.Index;
        Name = Add(new MeshTextField(this, "Name", "v3c.csphere.name", "The sphere's name (at most 23 Latin-1 characters).", V3dCollisionSphere.NameSize - 1,
            m => Sphere(m, i).Name.Text, (m, t) => Set(m, i, name: t), t => $"Rename sphere '{Sphere(Document.Current, i).Name.Text}' to '{t}'"));
        Bone = Add(new MeshChoiceField(this, "Bone", "v3c.csphere.bone", "The bone the sphere follows (none: the model itself).",
            m => BoneChoices(m, withNone: true), m => Sphere(m, i).BoneIndex, (m, b) => Set(m, i, bone: b),
            c => $"Attach sphere '{Sphere(Document.Current, i).Name.Text}' to '{BoneName(Document.Current, c.Value)}'"));
        for (int a = 0; a < 3; a++)
        {
            int axis = a;
            Position.Add(Add(new MeshNumberField(this, "Centre " + Axes[a], "v3c.csphere.position", "The centre, in metres, in the bone's frame.",
                m => Component(Sphere(m, i).Position, axis), (m, v) => Set(m, i, position: WithComponent(Sphere(m, i).Position, axis, (float)v)),
                () => $"Move sphere '{Sphere(Document.Current, i).Name.Text}'", decimals: 4, step: 0.01, suffix: "m") { Caption = Axes[a] }));
        }
        Radius = Add(new MeshNumberField(this, "Radius", "v3c.csphere.radius", "The radius in metres (greater than 0).",
            m => Sphere(m, i).Radius, (m, v) => Set(m, i, radius: (float)v),
            () => $"Set radius of '{Sphere(Document.Current, i).Name.Text}'", decimals: 4, step: 0.01, minimum: 0.0001, maximum: 1000, suffix: "m"));
    }

    internal static V3dCollisionSphere Sphere(V3dFile mesh, int index) => mesh.CollisionSpheres.ElementAt(index);

    private static V3dFile Set(V3dFile m, int i, string? name = null, int? bone = null, Vector3? position = null, float? radius = null)
    {
        var s = Sphere(m, i);
        return MeshEdit.SetCollisionSphere(m, i, name ?? s.Name.Text, bone ?? s.BoneIndex, position ?? s.Position, radius ?? s.Radius);
    }

    public MeshTextField Name { get; }

    public MeshChoiceField Bone { get; }

    public List<MeshNumberField> Position { get; } = [];

    public MeshNumberField Radius { get; }

    /// <summary>"Sphere 2 of 9".</summary>
    public string IndexText => $"{Node.Index} of {Document.Current.CollisionSpheres.Count()}";

    public override bool Exists(V3dFile mesh) => Node.Index < mesh.CollisionSpheres.Count();

    public override void Refresh()
    {
        base.Refresh();
        Raise(nameof(IndexText));
    }
}

// ── Prop point ───────────────────────────────────────────────────────────────

/// <summary>A prop point (in every LOD that has it): name, bone, position, orientation; add, remove.</summary>
public sealed class PropPointEditor : MeshNodeEditor
{
    public PropPointEditor(MeshDocumentViewModel document, MeshNodeRef node) : base(document, node)
    {
        int p = node.Index;
        Name = Add(new MeshTextField(this, "Name", "v3c.prop.name", "The prop point's name (at most 67 Latin-1 characters); the game finds props and effects by it.",
            V3dPropPoint.NameSize - 1, m => Prop(m, p).Name.Text, (m, t) => Set(m, p, name: t), t => $"Rename prop point '{Prop(Document.Current, p).Name.Text}' to '{t}'"));
        Bone = Add(new MeshChoiceField(this, "Bone", "v3c.prop.parent", "The bone the prop point follows (none: the model itself).",
            m => BoneChoices(m, withNone: true), m => Prop(m, p).ParentIndex, (m, b) => Set(m, p, bone: b),
            c => $"Attach prop point '{Prop(Document.Current, p).Name.Text}' to '{BoneName(Document.Current, c.Value)}'"));
        for (int a = 0; a < 3; a++)
        {
            int axis = a;
            Position.Add(Add(new MeshNumberField(this, "Position " + Axes[a], "v3c.prop.position", "The position in metres, in the bone's frame (model space without a bone).",
                m => Component(Prop(m, p).Position, axis), (m, v) => Set(m, p, position: WithComponent(Prop(m, p).Position, axis, (float)v)),
                () => $"Move prop point '{Prop(Document.Current, p).Name.Text}'", decimals: 4, step: 0.01, suffix: "m") { Caption = Axes[a] }));
            Orientation.Add(Add(new MeshNumberField(this, "Orientation " + Angles[a], "v3c.prop.rotation",
                "The orientation, read as the conjugate of the stored quaternion (as the viewport and REDUX show it). " + EulerConvention,
                m => Component(Quat.ToEulerDegrees(Active(Prop(m, p).Rotation)), axis),
                (m, v) =>
                {
                    var old = Prop(m, p).Rotation;
                    var active = WithEuler(Active(old), axis, v);
                    return Set(m, p, rotation: Quat.Align(Quat.Conj(active), old));
                },
                () => $"Rotate prop point '{Prop(Document.Current, p).Name.Text}'", decimals: 2, step: 1, minimum: -360, maximum: 360, suffix: "°") { Caption = Angles[a] }));
        }
    }

    private static Quaternion Active(Quaternion stored) => Quat.Conj(Quat.Normalize(stored));

    /// <summary>Prop point <paramref name="index"/> of the first LOD that has it (what <c>MeshEdit</c> compares with), or null.</summary>
    internal static V3dPropPoint? Find(V3dFile mesh, int index)
    {
        if (index < 0) return null;
        // FirstOrDefault of a struct sequence gives a default (null) array when no LOD has the prop point any
        // more (just removed, or undone): "is { }" would still match it, so test IsDefault.
        var list = mesh.Submeshes.SelectMany(s => s.Lods).Select(l => l.PropPoints).FirstOrDefault(pp => !pp.IsDefault && pp.Length > index);
        return list.IsDefault ? null : list[index];
    }

    internal static V3dPropPoint Prop(V3dFile mesh, int index) =>
        Find(mesh, index) ?? throw new ArgumentOutOfRangeException(nameof(index), $"The mesh has no prop point {index}.");

    private static V3dFile Set(V3dFile m, int p, string? name = null, int? bone = null, Quaternion? rotation = null, Vector3? position = null)
    {
        var old = Prop(m, p);
        return MeshEdit.SetPropPoint(m, p, name ?? old.Name.Text, bone ?? old.ParentIndex, rotation ?? old.Rotation, position ?? old.Position);
    }

    public MeshTextField Name { get; }

    public MeshChoiceField Bone { get; }

    public List<MeshNumberField> Position { get; } = [];

    public List<MeshNumberField> Orientation { get; } = [];

    public override bool Exists(V3dFile mesh) => Find(mesh, Node.Index) is not null;
}

// ── Material ─────────────────────────────────────────────────────────────────

/// <summary>A submesh material: texture name (with a browser), emissive, reflection fields, flags, the unknown floats.</summary>
public sealed class MaterialEditor : MeshNodeEditor
{
    private bool _renameAllLodEntries;

    public MaterialEditor(MeshDocumentViewModel document, MeshNodeRef node) : base(document, node)
    {
        int si = node.Submesh, mi = node.Index;
        Texture = Add(new MeshTextField(this, "Texture", "v3c.material.diffuse_map",
            "The texture file (at most 31 Latin-1 characters). The LOD texture entries that carried the old name follow (or every entry of this material, with the box below ticked).",
            V3dMaterial.NameSize - 1, m => Mat(m, si, mi).DiffuseMap.Text,
            (m, t) => MeshEdit.SetTextureName(m, si, mi, t, _renameAllLodEntries ? LodTextureUpdate.All : LodTextureUpdate.MatchingName),
            t => $"Set texture of material {mi} to '{t}'"));
        Emissive = Add(new MeshNumberField(this, "Emissive", "v3c.material.emissive", null,
            m => Mat(m, si, mi).Emissive, (m, v) => SetMat(m, si, mi, x => x with { Emissive = (float)v }),
            () => $"Set emissive of material {mi}", decimals: 3, step: 0.05, minimum: 0, maximum: 1));
        ReflectionCoefficient = Add(new MeshNumberField(this, "Reflection", "v3c.material.ref_coefficient", null,
            m => Mat(m, si, mi).ReflectionCoefficient, (m, v) => SetMat(m, si, mi, x => x with { ReflectionCoefficient = (float)v }),
            () => $"Set reflection coefficient of material {mi}", decimals: 3, step: 0.05, minimum: -1e6, maximum: 1e6));
        ReflectionMap = Add(new MeshTextField(this, "Reflection map", "v3c.material.ref_map", "At most 31 Latin-1 characters; empty for none.", V3dMaterial.NameSize - 1,
            m => Mat(m, si, mi).ReflectionMap.Text,
            (m, t) => SetMat(m, si, mi, x => x with { ReflectionMap = Field(x.ReflectionMap, t) }),
            t => t.Length == 0 ? $"Clear reflection map of material {mi}" : $"Set reflection map of material {mi} to '{t}'"));
        Flags = Add(new MeshTextField(this, "Flags", "v3c.material.flags", "A whole number, hexadecimal with 0x (0x11) or decimal (17).", 12,
            m => "0x" + Mat(m, si, mi).Flags.ToString("X", CultureInfo.InvariantCulture),
            (m, t) => SetMat(m, si, mi, x => x with { Flags = ParseFlags(t) }),
            t => $"Set flags of material {mi} to {t.Trim()}"));
        Unknown0 = Add(new MeshNumberField(this, "Unknown 0", "v3c.material.unknown", null,
            m => Mat(m, si, mi).Unknown0, (m, v) => SetMat(m, si, mi, x => x with { Unknown0 = (float)v }),
            () => $"Set unknown 0 of material {mi}", decimals: 3, step: 0.1));
        Unknown1 = Add(new MeshNumberField(this, "Unknown 1", "v3c.material.unknown", null,
            m => Mat(m, si, mi).Unknown1, (m, v) => SetMat(m, si, mi, x => x with { Unknown1 = (float)v }),
            () => $"Set unknown 1 of material {mi}", decimals: 3, step: 0.1));
        BrowseCommand = new RelayCommand(Browse, () => IsEditable);
    }

    internal static V3dMaterial? Find(V3dFile mesh, int submesh, int material) =>
        mesh.Submeshes.ElementAtOrDefault(submesh) is { } s && material >= 0 && material < s.Materials.Length ? s.Materials[material] : null;

    private static V3dMaterial Mat(V3dFile m, int si, int mi) =>
        Find(m, si, mi) ?? throw new ArgumentOutOfRangeException(nameof(mi), $"Submesh {si} has no material {mi}.");

    private static V3dFile SetMat(V3dFile m, int si, int mi, Func<V3dMaterial, V3dMaterial> change) =>
        MeshEdit.SetMaterial(m, si, mi, change(Mat(m, si, mi)));

    /// <summary>The old field when its text is unchanged (raw bytes kept), otherwise a clean 32-byte field.</summary>
    private static FixedString Field(FixedString old, string text) =>
        string.Equals(old.Text, text, StringComparison.Ordinal) ? old : FixedString.FromText(text, V3dMaterial.NameSize);

    internal static uint ParseFlags(string text)
    {
        string t = text.Trim();
        bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(t[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)
            : uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (!ok) throw new ArgumentException($"'{t}' is not a whole number; write the flags as 0x11 or 17.", nameof(text));
        return value;
    }

    public MeshTextField Texture { get; }

    public MeshNumberField Emissive { get; }

    public MeshNumberField ReflectionCoefficient { get; }

    public MeshTextField ReflectionMap { get; }

    public MeshTextField Flags { get; }

    public MeshNumberField Unknown0 { get; }

    public MeshNumberField Unknown1 { get; }

    /// <summary>A texture rename also renames LOD-specific entries (<c>foo-mip1.tga</c>) of this material.</summary>
    public bool RenameAllLodEntries
    {
        get => _renameAllLodEntries;
        set => Set(ref _renameAllLodEntries, value);
    }

    /// <summary>Opens the texture browser (the library's and the archives' textures).</summary>
    public RelayCommand BrowseCommand { get; }

    private void Browse()
    {
        string? picked = Tools.PickTexture(Document, Texture.Text);
        if (!string.IsNullOrEmpty(picked)) Texture.Text = picked;
    }

    public override bool Exists(V3dFile mesh) => Find(mesh, Node.Submesh, Node.Index) is not null;

    public override void Refresh()
    {
        base.Refresh();
        BrowseCommand.RaiseCanExecuteChanged();
    }
}

// ── LOD ──────────────────────────────────────────────────────────────────────

/// <summary>A level of detail: the camera distance it starts at.</summary>
public sealed class LodEditor : MeshNodeEditor
{
    public LodEditor(MeshDocumentViewModel document, MeshNodeRef node) : base(document, node)
    {
        int si = node.Submesh, l = node.Lod;
        Distance = Add(new MeshNumberField(this, "Distance", "v3c.submesh.lod_distances",
            "The camera distance (metres) at which this LOD starts; each LOD's should be larger than the previous one's (LOD 0 is 0 in stock meshes).",
            m => Distances(m, si) is { } d && l < d.Length ? d[l] : 0,
            (m, v) =>
            {
                var sub = m.Submeshes.ElementAt(si);
                var d = Distances(m, si) ?? [];
                var list = Enumerable.Range(0, sub.Lods.Length).Select(i => i < d.Length ? d[i] : 0f).ToArray();
                list[l] = (float)v;
                return MeshEdit.SetLodDistances(m, si, list);
            },
            () => $"Set LOD {l} distance of submesh '{Document.Current.Submeshes.ElementAt(si).Name.Text}'", decimals: 2, step: 1, minimum: 0, maximum: 1e6, suffix: "m"));
    }

    private static float[]? Distances(V3dFile m, int si) =>
        m.Submeshes.ElementAtOrDefault(si) is { } s && !s.LodDistances.IsDefault ? [.. s.LodDistances] : null;

    public MeshNumberField Distance { get; }

    public override bool Exists(V3dFile mesh) =>
        mesh.Submeshes.ElementAtOrDefault(Node.Submesh) is { } s && Node.Lod < s.Lods.Length;
}

// ── Submesh ──────────────────────────────────────────────────────────────────

/// <summary>A submesh: its name (the trailer and a parent name equal to the old name follow).</summary>
public sealed class SubmeshEditor : MeshNodeEditor
{
    public SubmeshEditor(MeshDocumentViewModel document, MeshNodeRef node) : base(document, node)
    {
        int si = node.Submesh;
        Name = Add(new MeshTextField(this, "Name", "v3c.submesh.name", "The submesh's name (at most 23 Latin-1 characters); its trailer entry (and a parent name equal to the old name) follow.",
            V3dSubmesh.NameSize - 1, m => m.Submeshes.ElementAt(si).Name.Text, (m, t) => MeshEdit.RenameSubmesh(m, si, t), t => $"Rename submesh {si} to '{t}'"));
    }

    public MeshTextField Name { get; }

    public override bool Exists(V3dFile mesh) => mesh.Submeshes.ElementAtOrDefault(Node.Submesh) is not null;
}

/// <summary>Bind-pose helpers for the self-tests and screens.</summary>
internal static class MeshEditorFacts
{
    /// <summary>A collision sphere's centre in model space at rest.</summary>
    public static Vector3 RestCentre(V3dFile mesh, V3dCollisionSphere sphere)
    {
        var skeleton = Skeleton.FromFile(mesh);
        return sphere.BoneIndex >= 0 && sphere.BoneIndex < skeleton.Count ? skeleton.RestWorld[sphere.BoneIndex].TransformPoint(sphere.Position) : sphere.Position;
    }
}
