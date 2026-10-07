using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Linting;

/// <summary>
/// The quick fixes the mesh rules offer (phase 6). Each is a pure edit applied to whatever mesh the UI
/// passes in (normally the one the diagnostic was computed for); each recomputes what it needs from that
/// mesh and returns it unchanged when there is nothing (left) to fix, so a stale fix is harmless. Only
/// repairs that cannot change how a valid mesh looks or plays are offered: a positive radius for a
/// sphere nothing can hit, sorted LOD distances, a unique suffix for a duplicated bone name, a name
/// truncated to leave room for its terminator, an out-of-range bone index sent to the root (where the
/// engine's lookups would not land anyway), an invalid parent made a root (what FK already does), and
/// header counts recomputed from the sections.
/// </summary>
internal static class MeshFixes
{
    /// <summary>The radius given to a collision sphere whose radius is zero or negative.</summary>
    public const float DefaultSphereRadius = 0.1f;

    /// <summary>The distance added between two LODs whose sorted distances are equal.</summary>
    public const float LodDistanceGap = 1f;

    public static QuickFix HeaderCounts() =>
        new("Set the header counts to the sections'", QuickFixKind.Edit, MeshEdit: m =>
        {
            int submeshes = m.Submeshes.Count(), spheres = m.CollisionSpheres.Count();
            return m.Header.SubmeshCount == submeshes && m.Header.CollisionSphereCount == spheres
                ? m
                : m with { Header = m.Header with { SubmeshCount = submeshes, CollisionSphereCount = spheres } };
        });

    public static QuickFix MakeRoot(int bone) =>
        new($"Make bone {bone} a root", QuickFixKind.Edit, MeshEdit: m =>
        {
            if ((uint)bone >= (uint)m.Bones.Length) return m;
            int p = m.Bones[bone].ParentIndex;
            bool invalid = p != -1 && (p < 0 || p >= m.Bones.Length || p == bone);
            return invalid ? MeshEdit.ReparentBone(m, bone, -1) : m;
        });

    public static QuickFix UniqueBoneName(V3dFile mesh, int bone) =>
        new($"Rename bone {bone} to '{UniqueName(mesh, bone)}'", QuickFixKind.Edit, MeshEdit: m =>
        {
            if ((uint)bone >= (uint)m.Bones.Length) return m;
            string name = m.Bones[bone].Name.Text;
            bool duplicate = Enumerable.Range(0, bone).Any(i => string.Equals(m.Bones[i].Name.Text, name, StringComparison.OrdinalIgnoreCase));
            return duplicate ? MeshEdit.RenameBone(m, bone, UniqueName(m, bone)) : m;
        });

    /// <summary>
    /// <c>name_2</c>, <c>name_3</c>… (the base shortened so the whole fits the 23 characters a bone name
    /// holds), the first that no bone of the mesh uses (case-insensitive).
    /// </summary>
    internal static string UniqueName(V3dFile mesh, int bone)
    {
        var taken = new HashSet<string>(mesh.Bones.Select(b => b.Name.Text), StringComparer.OrdinalIgnoreCase);
        string name = mesh.Bones[bone].Name.Text;
        int max = V3dBone.NameSize - 1;
        for (int k = 2; ; k++)
        {
            string suffix = "_" + k.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string stem = name.Length + suffix.Length > max ? name[..Math.Max(0, max - suffix.Length)] : name;
            string candidate = stem + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    public static QuickFix TruncateBoneName(int bone) =>
        Truncate(V3dBone.NameSize, m => (uint)bone < (uint)m.Bones.Length ? m.Bones[bone].Name : null,
            (m, text) => MeshEdit.RenameBone(m, bone, text));

    public static QuickFix TruncateSphereName(int sphere) =>
        Truncate(V3dCollisionSphere.NameSize, m => Sphere(m, sphere)?.Name,
            (m, text) => Sphere(m, sphere) is { } s ? MeshEdit.SetCollisionSphere(m, sphere, text, s.BoneIndex, s.Position, s.Radius) : m);

    public static QuickFix TruncateSubmeshName(int submesh) =>
        Truncate(V3dSubmesh.NameSize, m => m.Submeshes.ElementAtOrDefault(submesh)?.Name,
            (m, text) => MeshEdit.RenameSubmesh(m, submesh, text));

    public static QuickFix TruncateTextureName(int submesh, int material) =>
        Truncate(V3dMaterial.NameSize,
            m => m.Submeshes.ElementAtOrDefault(submesh) is { } s && (uint)material < (uint)s.Materials.Length ? s.Materials[material].DiffuseMap : null,
            (m, text) => MeshEdit.SetTextureName(m, submesh, material, text));

    public static QuickFix TruncatePropName(int submesh, int lod, int prop) =>
        Truncate(V3dPropPoint.NameSize, m => Prop(m, submesh, lod, prop)?.Name,
            (m, text) => MapProp(m, submesh, lod, prop, p => p with { Name = FixedString.FromText(text, V3dPropPoint.NameSize) }));

    /// <summary>A name that fills its field with no terminator, shortened to fit (the field size kept).</summary>
    private static QuickFix Truncate(int size, Func<V3dFile, FixedString?> read, Func<V3dFile, string, V3dFile> write) =>
        new($"Shorten the name to {size - 1} characters", QuickFixKind.Edit, MeshEdit: m =>
        {
            if (read(m) is not { } name || name.Length == 0 || name.Bytes.AsSpan().IndexOf((byte)0) >= 0) return m;
            string text = name.Text;
            return write(m, text.Length > size - 1 ? text[..(size - 1)] : text);
        });

    public static QuickFix SphereRadius(int sphere) =>
        new($"Set the radius to {DefaultSphereRadius:0.0##} m", QuickFixKind.Edit, MeshEdit: m =>
            Sphere(m, sphere) is { } s && float.IsFinite(s.Radius) && s.Radius <= 0f
                ? MeshEdit.SetCollisionSphere(m, sphere, s.Name.Text, s.BoneIndex, s.Position, DefaultSphereRadius)
                : m);

    public static QuickFix SphereToRoot(V3dFile mesh, int sphere) =>
        new(RootTitle(mesh), QuickFixKind.Edit, MeshEdit: m =>
        {
            if (Sphere(m, sphere) is not { } s || !BoneOutOfRange(s.BoneIndex, m.Bones.Length)) return m;
            return MeshEdit.SetCollisionSphere(m, sphere, s.Name.Text, RootBone(m), s.Position, s.Radius);
        });

    public static QuickFix PropToRoot(V3dFile mesh, int submesh, int lod, int prop) =>
        new(RootTitle(mesh), QuickFixKind.Edit, MeshEdit: m =>
            Prop(m, submesh, lod, prop) is { } p && BoneOutOfRange(p.ParentIndex, m.Bones.Length)
                ? MapProp(m, submesh, lod, prop, x => x with { ParentIndex = RootBone(m) })
                : m);

    public static QuickFix SortLodDistances(int submesh) =>
        new("Sort the LOD distances (each beyond the previous)", QuickFixKind.Edit, MeshEdit: m =>
        {
            if (m.Submeshes.ElementAtOrDefault(submesh) is not { } sub || sub.LodDistances.Length != sub.Lods.Length) return m;
            var sorted = Increasing(sub.LodDistances);
            return sorted is null ? m : MeshEdit.SetLodDistances(m, submesh, sorted);
        });

    /// <summary>The distances sorted, each equal one moved <see cref="LodDistanceGap"/> beyond the previous; null when already increasing or not finite.</summary>
    internal static float[]? Increasing(IReadOnlyList<float> distances)
    {
        if (distances.Any(d => !float.IsFinite(d) || d < 0f)) return null;
        bool increasing = true;
        for (int i = 1; i < distances.Count && increasing; i++) increasing = distances[i] > distances[i - 1];
        if (increasing) return null;
        var sorted = distances.Order().ToArray();
        for (int i = 1; i < sorted.Length; i++)
        {
            if (!(sorted[i] > sorted[i - 1])) sorted[i] = sorted[i - 1] + LodDistanceGap;
        }
        return sorted;
    }

    /// <summary>True when the fix can offer sorting (the distances are finite, non-negative and one per LOD).</summary>
    internal static bool CanSort(V3dSubmesh sub) =>
        sub.LodDistances.Length == sub.Lods.Length && sub.LodDistances.All(d => float.IsFinite(d) && d >= 0f);

    private static string RootTitle(V3dFile mesh) => mesh.Bones.Length == 0
        ? "Detach it from bones (-1)"
        : $"Attach it to the root bone ({RootBone(mesh)}: {mesh.Bones[RootBone(mesh)].Name.Text})";

    /// <summary>The first bone FK treats as a root (bone 0 when there is none), or -1 for a mesh without bones.</summary>
    internal static int RootBone(V3dFile mesh)
    {
        if (mesh.Bones.Length == 0) return -1;
        var effective = ForwardKinematics.EffectiveParents([.. mesh.Bones.Select(b => b.ParentIndex)]);
        int root = Array.IndexOf(effective, -1);
        return root < 0 ? 0 : root;
    }

    private static bool BoneOutOfRange(int bone, int count) => bone < -1 || (bone >= count && bone != -1);

    private static V3dCollisionSphere? Sphere(V3dFile mesh, int index) => mesh.CollisionSpheres.ElementAtOrDefault(index);

    private static V3dPropPoint? Prop(V3dFile mesh, int submesh, int lod, int prop) =>
        mesh.Submeshes.ElementAtOrDefault(submesh) is { } s && (uint)lod < (uint)s.Lods.Length && (uint)prop < (uint)s.Lods[lod].PropPoints.Length
            ? s.Lods[lod].PropPoints[prop]
            : null;

    /// <summary>The mesh with one LOD's prop point replaced by <paramref name="map"/> of it.</summary>
    private static V3dFile MapProp(V3dFile mesh, int submesh, int lod, int prop, Func<V3dPropPoint, V3dPropPoint> map)
    {
        int count = -1;
        for (int i = 0; i < mesh.Sections.Length; i++)
        {
            if (mesh.Sections[i] is not V3dSubmesh s || ++count != submesh) continue;
            if ((uint)lod >= (uint)s.Lods.Length || (uint)prop >= (uint)s.Lods[lod].PropPoints.Length) return mesh;
            var l = s.Lods[lod];
            var changed = map(l.PropPoints[prop]);
            if (changed == l.PropPoints[prop]) return mesh;
            var lods = s.Lods.SetItem(lod, l with { PropPoints = l.PropPoints.SetItem(prop, changed) });
            return mesh with { Sections = mesh.Sections.SetItem(i, s with { Lods = lods }) };
        }
        return mesh;
    }
}
