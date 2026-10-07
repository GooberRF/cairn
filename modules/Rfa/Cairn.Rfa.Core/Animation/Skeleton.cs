using System.Collections.Immutable;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Animation;

/// <summary>
/// A mesh's skeleton in the active convention (math.md), built from the V3C BONE section. The file
/// stores each bone's INVERSE bind transform with its rotation conjugated (v3c_skeleton.md); the two
/// effects cancel for the rotation, so:
/// <code>
/// RestWorld.Rotation = normalize(stored rot)          (bone -> model)
/// RestWorld.Position = Rotate(RestWorld.Rotation, -stored pos)
/// InverseBind        = RestWorld.Inverse()            (= (conj(rot), stored pos))
/// RestLocal(i)       = RestWorld(parent)^-1 * RestWorld(i);  root: RestLocal = RestWorld
/// </code>
/// Bone order is the contract with every clip (clips address bones by index).
/// </summary>
public sealed class Skeleton
{
    private Skeleton(
        ImmutableArray<string> names, ImmutableArray<int> parents, ImmutableArray<int> effectiveParents,
        ImmutableArray<int> order, ImmutableArray<Rigid> inverseBind, ImmutableArray<Rigid> restWorld,
        ImmutableArray<Rigid> restLocal)
    {
        Names = names;
        Parents = parents;
        EffectiveParents = effectiveParents;
        EvaluationOrder = order;
        InverseBind = inverseBind;
        RestWorld = restWorld;
        RestLocal = restLocal;
    }

    /// <summary>A skeleton with no bones (static meshes).</summary>
    public static Skeleton Empty { get; } = FromBones([]);

    /// <summary>Builds the skeleton of a mesh file (empty when it has no BONE section).</summary>
    public static Skeleton FromFile(V3dFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return FromBones(file.Bones);
    }

    /// <summary>Builds a skeleton from raw stored bones.</summary>
    public static Skeleton FromBones(IReadOnlyList<V3dBone> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        int n = bones.Count;
        var names = new string[n];
        var parents = new int[n];
        var world = new Rigid[n];
        var inverse = new Rigid[n];
        for (int i = 0; i < n; i++)
        {
            var b = bones[i];
            names[i] = b.Name.Text;
            parents[i] = b.ParentIndex;
            var rot = Quat.Normalize(b.Rotation);
            world[i] = new Rigid(rot, Quat.Rotate(rot, -b.Position));
            inverse[i] = world[i].Inverse();
        }

        var effective = ForwardKinematics.EffectiveParents(parents);
        var order = ForwardKinematics.EvaluationOrder(effective);
        var local = new Rigid[n];
        for (int i = 0; i < n; i++)
        {
            int p = effective[i];
            local[i] = p < 0 ? world[i] : world[p].Inverse().Compose(world[i]);
        }
        return new Skeleton([.. names], [.. parents], [.. effective], [.. order], [.. inverse], [.. world], [.. local]);
    }

    /// <summary>Number of bones.</summary>
    public int Count => Names.Length;

    /// <summary>Bone names in index order.</summary>
    public ImmutableArray<string> Names { get; }

    /// <summary>Parent indices exactly as stored (-1 for the root; may be greater than the child's index).</summary>
    public ImmutableArray<int> Parents { get; }

    /// <summary>Parents with invalid entries (out of range, self, cycles) replaced by -1; what FK uses.</summary>
    public ImmutableArray<int> EffectiveParents { get; }

    /// <summary>Bone indices ordered so every parent precedes its children.</summary>
    public ImmutableArray<int> EvaluationOrder { get; }

    /// <summary>Model space to bone space in the bind pose (active convention).</summary>
    public ImmutableArray<Rigid> InverseBind { get; }

    /// <summary>Each bone's model-space transform in the bind (rest) pose.</summary>
    public ImmutableArray<Rigid> RestWorld { get; }

    /// <summary>Each bone's bind-pose transform relative to its parent (model space for roots).</summary>
    public ImmutableArray<Rigid> RestLocal { get; }

    /// <summary>The index of the named bone (case-insensitive), or -1.</summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>True when both skeletons have the same canonical bone names and parents, in the same order (see <see cref="SkeletonMatcher"/>).</summary>
    public bool HasSameBoneList(Skeleton other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return SkeletonMatcher.SameBoneList(Names, Parents, other.Names, other.Parents);
    }
}
