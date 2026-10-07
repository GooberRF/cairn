using System.Numerics;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Animation;

/// <summary>
/// CPU linear-blend skinning with up to four influences per vertex. Every method writes into
/// caller-provided buffers and allocates nothing, so it can run every frame.
/// </summary>
/// <remarks>
/// A bone's skin transform is <c>World(pose) * InverseBind</c>, the identity in the bind pose, so
/// vertices are expected in model space as the mesh stores them for its bind. Weights are the stored
/// bytes divided by the sum of the weights actually used (stock characters sum to 255, so this is
/// the same as /255 there). A slot is ignored when its weight is 0, its bone is 0xFF or its bone index
/// is out of range; a vertex with no usable slot keeps its stored position.
/// </remarks>
public static class Skinning
{
    /// <summary>Influences per vertex.</summary>
    public const int MaxInfluences = 4;

    /// <summary>
    /// Fills <paramref name="skin"/> with each bone's skin matrix (<c>World * InverseBind</c>) in
    /// System.Numerics' row-vector layout, ready for <see cref="Skin"/>.
    /// </summary>
    public static void ComputeSkinMatrices(Skeleton skeleton, ReadOnlySpan<Rigid> world, Span<Matrix4x4> skin)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        int n = skeleton.Count;
        if (world.Length < n || skin.Length < n) throw new ArgumentException("Need one world and one matrix slot per bone.");
        for (int i = 0; i < n; i++) skin[i] = world[i].Compose(skeleton.InverseBind[i]).ToMatrix();
    }

    /// <summary>
    /// Skins one batch. <paramref name="normals"/> and <paramref name="outNormals"/> may both be
    /// empty to skip normals; <paramref name="links"/> may be empty (the vertices are copied).
    /// </summary>
    public static void Skin(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<V3dBoneLink> links,
        ReadOnlySpan<Matrix4x4> skin,
        Span<Vector3> outPositions,
        Span<Vector3> outNormals)
    {
        int n = positions.Length;
        if (outPositions.Length < n) throw new ArgumentException("The output position buffer is too small.", nameof(outPositions));
        bool doNormals = normals.Length > 0;
        if (doNormals && (normals.Length < n || outNormals.Length < n))
            throw new ArgumentException("Normals need one input and one output per vertex.", nameof(normals));

        for (int v = 0; v < n; v++)
        {
            var p = positions[v];
            var nrm = doNormals ? normals[v] : Vector3.Zero;
            if (v >= links.Length)
            {
                outPositions[v] = p;
                if (doNormals) outNormals[v] = nrm;
                continue;
            }

            var link = links[v];
            var accP = Vector3.Zero;
            var accN = Vector3.Zero;
            int total = 0;
            for (int s = 0; s < MaxInfluences; s++)
            {
                int w = link.GetWeight(s);
                int bone = link.GetBone(s);
                if (w == 0 || bone == V3dBoneLink.NoBone || bone >= skin.Length) continue;
                ref readonly var m = ref skin[bone];
                accP += w * Vector3.Transform(p, m);
                if (doNormals) accN += w * Vector3.TransformNormal(nrm, m);
                total += w;
            }

            if (total == 0)
            {
                outPositions[v] = p;
                if (doNormals) outNormals[v] = nrm;
                continue;
            }
            outPositions[v] = accP / total;
            if (doNormals)
            {
                float len = accN.Length();
                outNormals[v] = len > 0f ? accN / len : nrm;
            }
        }
    }
}
