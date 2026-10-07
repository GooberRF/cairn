using System.Numerics;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Editing;

public static partial class MeshEdit
{
    /// <summary>
    /// The model-level prop point count: that of LOD 0 of submesh 0 (0 without one). Prop points are
    /// stored per LOD, but every LOD of every stock submesh carries the same list.
    /// </summary>
    public static int PropPointCount(V3dFile mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var first = mesh.Submeshes.FirstOrDefault();
        return first is { Lods.IsDefaultOrEmpty: false } && !first.Lods[0].PropPoints.IsDefault ? first.Lods[0].PropPoints.Length : 0;
    }

    /// <summary>
    /// Adds a prop point at the end of every LOD of every submesh (the list is model-level).
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="name">Prop point name (68-byte field: at most 67 Latin-1 characters).</param>
    /// <param name="parentBone">The bone it follows, or -1.</param>
    /// <param name="rotation">Rotation, raw as stored.</param>
    /// <param name="position">Position, raw as stored (relative to the bone; model space without one).</param>
    public static V3dFile AddPropPoint(V3dFile mesh, string name, int parentBone, Quaternion rotation, Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var fixedName = NewName(name, V3dPropPoint.NameSize, "prop point name", nameof(name));
        CheckBoneOrNone(mesh.Bones.Length, parentBone, nameof(parentBone));
        CheckRotation(rotation, "prop point rotation", nameof(rotation));
        CheckFinite(position, "prop point position", nameof(position));
        if (!mesh.Submeshes.Any(s => !s.Lods.IsDefaultOrEmpty))
            throw new ArgumentException("The mesh has no submesh with a level of detail to hold prop points.", nameof(mesh));
        var point = new V3dPropPoint(fixedName, rotation, position, parentBone);
        return MapSubmeshes(mesh, sub => MapLods(sub, lod => lod with { PropPoints = lod.PropPoints.IsDefault ? [point] : lod.PropPoints.Add(point) }));
    }

    /// <summary>Removes prop point <paramref name="prop"/> from every LOD that has it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="prop">Prop point index.</param>
    public static V3dFile RemovePropPoint(V3dFile mesh, int prop)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        CheckPropPoint(mesh, prop);
        return MapSubmeshes(mesh, sub => MapLods(sub, lod =>
            lod.PropPoints.IsDefault || lod.PropPoints.Length <= prop ? lod : lod with { PropPoints = lod.PropPoints.RemoveAt(prop) }));
    }

    /// <summary>
    /// Changes prop point <paramref name="prop"/> in every LOD that has it. An unchanged name keeps its
    /// raw bytes (per LOD); values equal to the stored ones (of the first LOD that has the point) are
    /// always accepted.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="prop">Prop point index.</param>
    /// <param name="name">Prop point name (68-byte field: at most 67 Latin-1 characters).</param>
    /// <param name="parentBone">The bone it follows, or -1.</param>
    /// <param name="rotation">Rotation, raw as stored.</param>
    /// <param name="position">Position, raw as stored.</param>
    public static V3dFile SetPropPoint(V3dFile mesh, int prop, string name, int parentBone, Quaternion rotation, Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(name);
        var reference = CheckPropPoint(mesh, prop);
        ResolveName(reference.Name, name, V3dPropPoint.NameSize, "prop point name", nameof(name));
        if (parentBone != reference.ParentIndex) CheckBoneOrNone(mesh.Bones.Length, parentBone, nameof(parentBone));
        if (!Same(rotation, reference.Rotation)) CheckRotation(rotation, "prop point rotation", nameof(rotation));
        if (!Same(position, reference.Position)) CheckFinite(position, "prop point position", nameof(position));

        return MapSubmeshes(mesh, sub => MapLods(sub, lod =>
        {
            if (lod.PropPoints.IsDefault || lod.PropPoints.Length <= prop) return lod;
            var old = lod.PropPoints[prop];
            var fixedName = ResolveName(old.Name, name, V3dPropPoint.NameSize, "prop point name", nameof(name));
            if (fixedName == old.Name && parentBone == old.ParentIndex && Same(rotation, old.Rotation) && Same(position, old.Position)) return lod;
            var changed = old with { Name = fixedName, ParentIndex = parentBone, Rotation = rotation, Position = position };
            return lod with { PropPoints = lod.PropPoints.SetItem(prop, changed) };
        }));
    }

    /// <summary>Throws unless some LOD has prop point <paramref name="prop"/>; returns the first LOD's record.</summary>
    private static V3dPropPoint CheckPropPoint(V3dFile mesh, int prop)
    {
        int max = 0;
        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Lods.IsDefault) continue;
            foreach (var lod in sub.Lods)
            {
                if (lod.PropPoints.IsDefault) continue;
                if (prop >= 0 && prop < lod.PropPoints.Length) return lod.PropPoints[prop];
                max = Math.Max(max, lod.PropPoints.Length);
            }
        }
        throw new ArgumentOutOfRangeException(nameof(prop),
            max == 0 ? $"The mesh has no prop points; there is no prop point {prop}." : $"The mesh has {max} prop points (0 to {max - 1}); there is no prop point {prop}.");
    }
}
