using System.Collections.Immutable;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

public static partial class MeshEdit
{
    /// <summary>Renames a bone (24-byte field: at most 23 Latin-1 characters). The same name keeps the raw bytes.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="bone">Bone index.</param>
    /// <param name="name">The new name.</param>
    public static V3dFile RenameBone(V3dFile mesh, int bone, string name)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var bones = mesh.Bones;
        CheckBone(bones.Length, bone, nameof(bone));
        var old = bones[bone];
        var newName = ResolveName(old.Name, name, V3dBone.NameSize, "bone name", nameof(name));
        if (newName == old.Name) return mesh;
        return WithBones(mesh, bones.SetItem(bone, old with { Name = newName }));
    }

    /// <summary>
    /// Gives a bone another parent (-1 makes it a root). The stored bind is unchanged, so the bone's
    /// rest WORLD pose stays where it is and its rest local (relative to the new parent) changes.
    /// Refuses the bone itself, an index out of range, and any parent that would close a cycle.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="bone">Bone index.</param>
    /// <param name="newParent">The new parent's index, or -1.</param>
    public static V3dFile ReparentBone(V3dFile mesh, int bone, int newParent)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var bones = mesh.Bones;
        int n = bones.Length;
        CheckBone(n, bone, nameof(bone));
        if (bones[bone].ParentIndex == newParent) return mesh;
        CheckBoneOrNone(n, newParent, nameof(newParent));
        if (newParent == bone)
            throw new ArgumentException($"Bone {bone} ('{bones[bone].Name.Text}') cannot be its own parent.", nameof(newParent));
        if (newParent >= 0)
        {
            // Walk up from the new parent: reaching the bone means the new link would close a loop.
            var seen = new bool[n];
            int a = newParent;
            while (a >= 0 && a < n)
            {
                if (a == bone)
                    throw new ArgumentException(
                        $"Bone {newParent} ('{bones[newParent].Name.Text}') is a descendant of bone {bone} ('{bones[bone].Name.Text}'); "
                        + "making it the parent would create a loop. Re-parent the descendant first.", nameof(newParent));
                if (seen[a])
                    throw new ArgumentException(
                        $"Bone {newParent} ('{bones[newParent].Name.Text}') is part of an existing parent loop; fix that loop before attaching bones to it.",
                        nameof(newParent));
                seen[a] = true;
                a = bones[a].ParentIndex;
            }
        }
        return WithBones(mesh, bones.SetItem(bone, bones[bone] with { ParentIndex = newParent }));
    }

    /// <summary>
    /// Reorders the bones: <paramref name="newOrder"/>[newIndex] = oldIndex, a permutation of every bone.
    /// Parent indices, every batch's bone links (every LOD of every submesh; 0xFF stays 0xFF),
    /// collision sphere bones and prop point parents are remapped, so the mesh looks and skins the same.
    /// Clips address bones by INDEX, though: every clip made for the old order plays the wrong tracks on
    /// the new one, so callers should offer <see cref="ClipEdit.ConformToSkeleton"/> (with the old bone
    /// names) for the mesh's clips. The identity permutation returns the mesh itself.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="newOrder">For each new index, the old bone index that goes there.</param>
    public static BoneReorderResult ReorderBones(V3dFile mesh, IReadOnlyList<int> newOrder)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(newOrder);
        var bones = mesh.Bones;
        int n = bones.Length;
        if (newOrder.Count != n)
            throw new ArgumentException($"The new order lists {newOrder.Count} bones but the mesh has {n}; list every bone exactly once.", nameof(newOrder));
        var oldToNew = new int[n];
        Array.Fill(oldToNew, -1);
        bool identity = true;
        for (int i = 0; i < n; i++)
        {
            int old = newOrder[i];
            if ((uint)old >= (uint)n)
                throw new ArgumentOutOfRangeException(nameof(newOrder), $"Entry {i} of the new order is {old}, but the mesh has {n} bones (0 to {n - 1}).");
            if (oldToNew[old] >= 0)
                throw new ArgumentException($"Bone {old} appears twice in the new order (entries {oldToNew[old]} and {i}); list every bone exactly once.", nameof(newOrder));
            oldToNew[old] = i;
            identity &= old == i;
        }
        ImmutableArray<int> newToOldArray = [.. newOrder];
        ImmutableArray<int> oldToNewArray = [.. oldToNew];
        if (identity) return new BoneReorderResult(mesh, oldToNewArray, newToOldArray);

        int Map(int index) => index >= 0 && index < n ? oldToNew[index] : index;

        var reordered = new V3dBone[n];
        for (int i = 0; i < n; i++)
        {
            var b = bones[newOrder[i]];
            reordered[i] = b with { ParentIndex = Map(b.ParentIndex) };
        }
        var result = WithBones(mesh, [.. reordered]);

        result = MapSubmeshes(result, sub => MapLods(sub, lod =>
        {
            var batches = MapItems(lod.Batches, batch => RemapLinks(batch, oldToNew));
            var props = MapItems(lod.PropPoints, p =>
                Map(p.ParentIndex) == p.ParentIndex ? p : p with { ParentIndex = Map(p.ParentIndex) });
            return batches == lod.Batches && props == lod.PropPoints ? lod : lod with { Batches = batches, PropPoints = props };
        }));

        var sections = result.Sections.ToBuilder();
        bool spheresChanged = false;
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i] is V3dCollisionSphere s && Map(s.BoneIndex) != s.BoneIndex)
            {
                sections[i] = s with { BoneIndex = Map(s.BoneIndex) };
                spheresChanged = true;
            }
        }
        if (spheresChanged) result = result with { Sections = sections.MoveToImmutable() };
        return new BoneReorderResult(result, oldToNewArray, newToOldArray);
    }

    private static V3dBatch RemapLinks(V3dBatch batch, int[] oldToNew)
    {
        if (batch.BoneLinks.IsDefaultOrEmpty) return batch;
        int n = oldToNew.Length;
        byte Map(byte b) => b < n ? (byte)oldToNew[b] : b;
        V3dBoneLink[]? links = null;
        for (int v = 0; v < batch.BoneLinks.Length; v++)
        {
            var l = batch.BoneLinks[v];
            var m = l with { Bone0 = Map(l.Bone0), Bone1 = Map(l.Bone1), Bone2 = Map(l.Bone2), Bone3 = Map(l.Bone3) };
            if (m == l) continue;
            links ??= [.. batch.BoneLinks];
            links[v] = m;
        }
        return links is null ? batch : batch with { BoneLinks = [.. links] };
    }

    /// <summary>
    /// Sets a bone's bind (rest) pose. <paramref name="transform"/> is the rest pose in
    /// <paramref name="space"/> (active convention, <see cref="Rigid"/>); it is stored as the file's
    /// inverse bind: <c>rot = world rot</c> (normalised, kept in the old value's hemisphere),
    /// <c>pos = -Rotate(conj(rot), world pos)</c>. With <paramref name="childrenFollow"/> the bone's
    /// descendants move rigidly with it (their rest locals stay); without, their rest WORLD poses stay
    /// (their rest locals change). Passing exactly what <see cref="GetBoneBind"/> returns changes nothing.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="bone">Bone index.</param>
    /// <param name="transform">The bone's rest pose in <paramref name="space"/>.</param>
    /// <param name="space">World (model space) or Local (relative to the parent's rest world).</param>
    /// <param name="childrenFollow">Move the descendants with the bone.</param>
    public static V3dFile SetBoneBind(V3dFile mesh, int bone, Rigid transform, BindSpace space, bool childrenFollow = false)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var bones = mesh.Bones;
        CheckBone(bones.Length, bone, nameof(bone));
        CheckSpace(space);
        CheckRotation(transform.Rotation, "bind rotation", nameof(transform));
        CheckFinite(transform.Position, "bind position", nameof(transform));

        var skeleton = Skeleton.FromFile(mesh);
        var current = space == BindSpace.World ? skeleton.RestWorld[bone] : skeleton.RestLocal[bone];
        if (Same(transform, current)) return mesh;

        var t = new Rigid(Quat.Normalize(transform.Rotation), transform.Position);
        int parent = skeleton.EffectiveParents[bone];
        var world = space == BindSpace.World || parent < 0 ? t : skeleton.RestWorld[parent].Compose(t);

        var result = bones.ToBuilder();
        result[bone] = Stored(bones[bone], world);
        if (childrenFollow)
        {
            var delta = world.Compose(skeleton.RestWorld[bone].Inverse());
            for (int d = 0; d < bones.Length; d++)
            {
                if (d != bone && IsDescendant(skeleton.EffectiveParents, d, bone))
                    result[d] = Stored(bones[d], delta.Compose(skeleton.RestWorld[d]));
            }
        }

        bool changed = false;
        for (int i = 0; i < bones.Length && !changed; i++) changed = !Same(result[i], bones[i]);
        return changed ? WithBones(mesh, result.MoveToImmutable()) : mesh;
    }

    /// <summary>
    /// A bone's bind (rest) pose in <paramref name="space"/>: <see cref="Skeleton.RestWorld"/> or
    /// <see cref="Skeleton.RestLocal"/>. The inverse of <see cref="SetBoneBind"/>.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="bone">Bone index.</param>
    /// <param name="space">World (model space) or Local (relative to the parent's rest world).</param>
    public static Rigid GetBoneBind(V3dFile mesh, int bone, BindSpace space)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        CheckBone(mesh.Bones.Length, bone, nameof(bone));
        CheckSpace(space);
        var skeleton = Skeleton.FromFile(mesh);
        return space == BindSpace.World ? skeleton.RestWorld[bone] : skeleton.RestLocal[bone];
    }

    /// <summary>The stored (inverse bind) form of a rest world transform, in the old rotation's hemisphere.</summary>
    private static V3dBone Stored(V3dBone old, Rigid world)
    {
        var rot = Quat.Align(Quat.Normalize(world.Rotation), old.Rotation);
        var pos = -Quat.Rotate(Quat.Conj(rot), world.Position);
        return old with { Rotation = rot, Position = pos };
    }

    private static bool IsDescendant(ImmutableArray<int> effectiveParents, int bone, int ancestor)
    {
        int a = effectiveParents[bone];
        for (int guard = 0; a >= 0 && guard <= effectiveParents.Length; guard++)
        {
            if (a == ancestor) return true;
            a = effectiveParents[a];
        }
        return false;
    }

    private static void CheckSpace(BindSpace space)
    {
        if (space is not (BindSpace.World or BindSpace.Local))
            throw new ArgumentOutOfRangeException(nameof(space), $"{(int)space} is not a bind space; use World or Local.");
    }
}
