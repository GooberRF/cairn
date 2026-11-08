using System.Collections.Immutable;
using System.Numerics;

namespace Cairn.Rfa.Formats.V3d;

/// <summary>
/// Joins submeshes into one. The game reads every submesh of a <c>.v3c</c> into the same slot, so only the last one
/// draws (a <c>.v3m</c> draws them all); a character must therefore be one submesh. Each LOD's groups are kept in order
/// (one batch each), materials equal in every field are shared, and the LOD's vertex count and morph map follow from
/// the joined originals when <see cref="V3dBuilder"/> builds the result. Texture limits are not checked here: see
/// <see cref="V3dTextureAtlas"/>.
/// </summary>
public static class V3dSubmeshMerge
{
    private readonly record struct MaterialKey(string Diffuse, (float, float, float, float, string, uint) Settings);

    /// <summary>Everything of a material but its texture (materials equal in this draw alike), name case ignored.</summary>
    internal static (float, float, float, float, string, uint) Settings(V3dMaterial m) =>
        (m.Emissive, m.Unknown0, m.Unknown1, m.ReflectionCoefficient, m.ReflectionMap.Text.ToLowerInvariant(), m.Flags);

    /// <summary>Why these submeshes cannot be joined without guessing, as a clause for a sentence; null when they can.</summary>
    public static string? Obstacle(IReadOnlyList<V3dSubmeshDescription> submeshes)
    {
        ArgumentNullException.ThrowIfNull(submeshes);
        if (submeshes.Count == 0) return "there are no submeshes";
        if (submeshes.Select(s => s.Lods.Length).Distinct().Count() > 1)
            return $"the submeshes have different numbers of levels of detail ({string.Join(", ", submeshes.Select(s => s.Lods.Length))})";
        if (submeshes.Any(s => s.Offset != submeshes[0].Offset)) return "the submeshes have different offsets";
        if (submeshes.Any(s => s.Lods.Any(l => l.Groups.Any(g => (uint)g.Material >= (uint)s.Materials.Length)))) return "a batch uses a material its submesh does not have";
        return null;
    }

    /// <summary>Why this mesh's submeshes cannot be joined, as a clause for a sentence; null when they can.</summary>
    public static string? Obstacle(V3dFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            return Obstacle(V3dBuilder.Decompose(file).Submeshes);
        }
        catch (ArgumentException)
        {
            return "a batch uses a texture slot its level of detail does not have";
        }
    }

    /// <summary>The mesh with its submeshes joined, for <see cref="V3dBuilder.Build"/> with <see cref="V3dBuildOptions.Preserve"/>.</summary>
    /// <exception cref="ArgumentException">See <see cref="Obstacle(V3dFile)"/>.</exception>
    public static V3dMeshDescription Merge(V3dFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var description = V3dBuilder.Decompose(file);
        return description with { Submeshes = [Merge(description.Submeshes)] };
    }

    /// <summary>One submesh of <paramref name="submeshes"/>, named after the first. Bones and prop points belong to the mesh and do not change.</summary>
    /// <exception cref="ArgumentException">See <see cref="Obstacle(IReadOnlyList{V3dSubmeshDescription})"/>.</exception>
    public static V3dSubmeshDescription Merge(IReadOnlyList<V3dSubmeshDescription> submeshes)
    {
        if (Obstacle(submeshes) is { } why) throw new ArgumentException($"The submeshes cannot be merged: {why}.", nameof(submeshes));

        var materials = new List<V3dMaterial>();
        var index = new Dictionary<MaterialKey, int>();
        var maps = new int[submeshes.Count][];
        for (int s = 0; s < maps.Length; s++)
        {
            maps[s] = new int[submeshes[s].Materials.Length];
            for (int m = 0; m < maps[s].Length; m++)
            {
                var material = submeshes[s].Materials[m];
                var key = new MaterialKey(material.DiffuseMap.Text.ToLowerInvariant(), Settings(material));
                if (!index.TryGetValue(key, out int at))
                {
                    at = materials.Count;
                    materials.Add(material);
                    index.Add(key, at);
                }
                maps[s][m] = at;
            }
        }

        var lods = ImmutableArray.CreateBuilder<V3dLodDescription>(submeshes[0].Lods.Length);
        for (int l = 0; l < submeshes[0].Lods.Length; l++)
        {
            var sources = submeshes.Select(s => s.Lods[l]).ToList();
            lods.Add(sources[0] with
            {
                Distance = sources.Max(x => x.Distance),
                Groups = [.. sources.SelectMany((x, s) => x.Groups.Select(g => g with { Material = maps[s][g.Material] }))],
                Textures = null,
                MorphVertices = Originals(sources),
                VertexCountOverride = sources.All(x => x.VertexCountOverride is not null) ? sources.Sum(x => x.VertexCountOverride) : null,
            });
        }
        return submeshes[0] with { Lods = lods.MoveToImmutable(), Materials = [.. materials] };
    }

    /// <summary>
    /// The joined LODs' original vertices: one after the other, so each batch keeps its own range of the morph map; or,
    /// when the lists are parts of one list (a character cut by texture: same length, equal wherever both know the
    /// vertex), that one list, which is what the clips' morph keys address.
    /// </summary>
    private static ImmutableArray<Vector3>? Originals(List<V3dLodDescription> lods)
    {
        if (lods.All(l => l.MorphVertices is null)) return null;
        var lists = lods.Select(l => l.MorphVertices ?? V3dBuilder.DistinctPositions(l.Groups)).ToList();
        if (lists.All(x => x.Length == lists[0].Length))
        {
            var joined = new Vector3[lists[0].Length];
            Array.Fill(joined, new Vector3(float.NaN));
            bool same = true;
            foreach (var list in lists)
            {
                for (int i = 0; i < joined.Length && same; i++)
                {
                    if (float.IsNaN(list[i].X)) continue;
                    if (float.IsNaN(joined[i].X)) joined[i] = list[i];
                    else same = new V3dBuilder.PositionKey(joined[i]) == new V3dBuilder.PositionKey(list[i]);
                }
            }
            if (same) return [.. joined];
        }
        return [.. lists.SelectMany(x => x)];
    }
}
