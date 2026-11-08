using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Turns a parsed <see cref="Rf2MeshFile"/> into a <see cref="V3dMeshDescription"/> for <see cref="V3dBuilder"/>:
/// one submesh per object (a character's objects joined into one, since a <c>.v3c</c> draws only one submesh; named after
/// it, placed at its offset), its levels of detail with their stored switch
/// distances, one material per texture, vertices split wherever a corner's UV differs (UVs are stored per corner),
/// the skeleton from the stored inverse bind transforms, and the collision spheres and prop points. What the PC
/// format cannot hold or Cairn does not understand is listed in the notes.
/// </summary>
internal static class Rf2MeshConverter
{
    /// <summary>A material flag the PC compiler also sets for see-through textures (0x8); RF2 sets it the same way.</summary>
    private const uint AlphaFlag = 0x8;

    /// <summary>A switch distance at or above this is "never" (a single LOD stores the float maximum).</summary>
    private const float Never = 1e30f;

    public static LegacyMesh Convert(Rf2MeshFile file, string name)
    {
        var notes = new List<string>();
        var stats = new Stats();
        bool character = file.IsSkinned;
        string ext = character ? "rfc" : "rfm";

        var submeshes = ImmutableArray.CreateBuilder<V3dSubmeshDescription>();
        foreach (var obj in file.Objects)
        {
            if (obj.Lods.IsEmpty) continue;
            var lods = obj.Lods;
            if (lods.Length > V3dBuilder.MaxLods)
            {
                stats.DroppedLods += lods.Length - V3dBuilder.MaxLods;
                lods = lods[..V3dBuilder.MaxLods];
            }
            // a .v3c draws only one submesh: a character is never cut, and its objects are joined below
            List<HashSet<string>?> parts = character ? [null] : Split(lods, stats);
            foreach (var part in parts) submeshes.Add(BuildSubmesh(file, obj, lods, part, stats));

            // facts of the object as stored, whatever it was cut into
            if (lods.Length > 1)
            {
                stats.LodObjects++;
                stats.LodDistances.Add(string.Join(", ", obj.LodDistances.Take(lods.Length - 1).Select(d => d.ToString("0.##", CultureInfo.InvariantCulture) + " m")));
            }
            foreach (var lod in lods)
            {
                stats.SourceVertices += character ? lod.SkinVertices.Length : lod.Geometries.Sum(g => g.Vertices.Length);
                if (!character && lod.Offset != lods[0].Offset) stats.ShiftedLods++;
                foreach (var g in lod.Geometries.Where(g => g.Unknown != 0))
                {
                    stats.GeometryValues++;
                    stats.GeometryValueMin = Math.Min(stats.GeometryValueMin, g.Unknown);
                    stats.GeometryValueMax = Math.Max(stats.GeometryValueMax, g.Unknown);
                }
            }
        }

        // names: the object names, made unique and fitted to the 24-byte field
        var subs = submeshes.ToList();
        int joinedObjects = 0;
        if (character && subs.Count > 1)
        {
            joinedObjects = subs.Count;
            // an object with fewer levels of detail keeps its last one at every distance beyond it
            int most = subs.Max(s => s.Lods.Length);
            subs = [V3dSubmeshMerge.Merge([.. subs.Select(s => s with { Lods = [.. s.Lods, .. Enumerable.Repeat(s.Lods[^1], most - s.Lods.Length)] })])];
        }
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < subs.Count; i++)
        {
            string wanted = subs[i].Name.Text;
            string unique = Unique(wanted, taken);
            if (unique != wanted) subs[i] = subs[i] with { Name = FixedString.FromText(unique, V3dSubmesh.NameSize) };
        }

        var bones = ImmutableArray.CreateBuilder<V3dBone>(file.Bones.Length);
        int rotated = 0;
        foreach (var bone in file.Bones)
        {
            if (!bone.IsIdentity) rotated++;
            bones.Add(new V3dBone(FixedString.FromText(Fit(bone.Name, V3dBone.NameSize, stats), V3dBone.NameSize), BoneRotation(bone.Rotation), bone.Translation, bone.Parent));
        }

        int boneCount = file.Bones.Length;
        var spheres = ImmutableArray.CreateBuilder<V3dCollisionSphere>(file.Spheres.Length);
        foreach (var s in file.Spheres)
        {
            int parent = s.Parent;
            if (parent < -1 || parent >= boneCount) { stats.UnparentedItems++; parent = -1; }
            spheres.Add(new V3dCollisionSphere(FixedString.FromText(Fit(s.Name, V3dCollisionSphere.NameSize, stats), V3dCollisionSphere.NameSize), parent, s.Position, s.Radius, []));
        }
        var props = ImmutableArray.CreateBuilder<V3dPropPoint>(file.Props.Length);
        int turnedProps = 0;
        foreach (var p in file.Props)
        {
            int parent = p.Parent;
            if (parent < -1 || parent >= boneCount) { stats.UnparentedItems++; parent = -1; }
            if (MathF.Abs(p.Orientation.W) < 0.9999f) turnedProps++;
            props.Add(new V3dPropPoint(FixedString.FromText(Fit(p.Name, V3dPropPoint.NameSize, stats), V3dPropPoint.NameSize), p.Orientation, p.Position, parent));
        }

        // ── notes: what the conversion approximates, in plain words ──
        if (subs.Count == 0) notes.Add("The file holds no geometry.");
        if (joinedObjects > 0) notes.Add($"The {joinedObjects:N0} objects were joined into one submesh: the game draws only one submesh of a .v3c.");
        if (character && subs.Count == 1 && V3dTextureAtlas.MostTextures(subs[0]) is var textures and > V3dBuilder.MaxTexturesPerLod)
            notes.Add($"The mesh uses {textures:N0} textures in one level of detail; a .v3c holds {V3dBuilder.MaxTexturesPerLod} in its one submesh, so converting combines the fewest needed into an atlas written beside the mesh, from the .peg packs.");
        int objects = file.Objects.Count(o => !o.Lods.IsEmpty);
        if (stats.SplitObjects > 0)
            notes.Add($"{stats.SplitObjects:N0} object{(stats.SplitObjects == 1 ? " uses" : "s use")} more than {V3dBuilder.MaxTexturesPerLod} textures in one level of detail, more than a PC mesh allows; "
                + $"{(stats.SplitObjects == 1 ? "it was" : "they were")} split into submeshes of at most {V3dBuilder.MaxTexturesPerLod} textures each ({subs.Count:N0} submeshes from {objects:N0} objects).");
        if (stats.EmptyLodParts > 0)
            notes.Add($"{stats.EmptyLodParts:N0} level{(stats.EmptyLodParts == 1 ? "" : "s")} of detail of a split submesh use none of its textures; those levels are empty, so that part disappears at their distance.");
        if (stats.DroppedLods > 0)
            notes.Add($"{stats.DroppedLods:N0} levels of detail beyond the {V3dBuilder.MaxLods} a PC mesh holds were left out.");
        if (stats.LodObjects > 0)
            notes.Add($"Levels of detail switch at the distances the file stores ({string.Join("; ", stats.LodDistances.Distinct().Take(6))}).");
        if (stats.Corners > 0)
            notes.Add($"UVs are stored per triangle corner: {stats.Corners:N0} corners became {stats.Vertices:N0} vertices, one per distinct position, normal and UV{(character ? " (and bone weights)" : "")}"
                + (stats.SourceVertices > 0 ? $"; the file had {stats.SourceVertices:N0} {(character ? "skinned vertices" : "vertices")}." : "."));
        if (stats.MaterialRecords > 0)
            notes.Add($"The {stats.MaterialRecords:N0} material records hold settings Cairn does not understand; only each texture, the see-through flag and the environment map name were kept.");
        if (stats.EnvironmentMaps.Count > 0)
            notes.Add($"{stats.EnvironmentMapUses:N0} material{(stats.EnvironmentMapUses == 1 ? " uses" : "s use")} an environment map ({string.Join(", ", stats.EnvironmentMaps.Take(4))}{(stats.EnvironmentMaps.Count > 4 ? ", ..." : "")}); "
                + "the PC mesh keeps only its name, as the material's reflection map, which the game does not draw.");
        if (stats.UnknownMaterialBits > 0)
            notes.Add($"{stats.UnknownMaterialBits:N0} materials set flag 0x2, whose meaning is unknown; it was not carried over.");
        if (stats.GeometryValues > 0)
            notes.Add($"{stats.GeometryValues:N0} geometr{(stats.GeometryValues == 1 ? "y stores" : "ies store")} a value Cairn does not understand (from {stats.GeometryValueMin.ToString("0.###", CultureInfo.InvariantCulture)} to {stats.GeometryValueMax.ToString("0.###", CultureInfo.InvariantCulture)}); it was not carried over.");
        if (stats.ShiftedLods > 0)
            notes.Add($"{stats.ShiftedLods:N0} levels of detail store another offset than their object's first; their vertices were moved to the first one's.");
        if (character)
        {
            if (rotated > 0)
                notes.Add($"{rotated:N0} of the {boneCount:N0} bones are turned in the bind pose. Their orientation follows the storage convention measured on the game's meshes and checked by posing them with the game's animations; no Red Faction II documentation confirms it.");
            notes.Add("Bone weights are stored as floats; they were normalised to the bytes summing to 255 a PC character stores"
                + (stats.OddWeightSums > 0 ? $" ({stats.OddWeightSums:N0} vertices' weights did not add up to 1)." : "."));
            if (stats.Unweighted > 0) notes.Add($"{stats.Unweighted:N0} vertices have no bone weights.");
        }
        else if (file.UnstoredBoneCount > 0)
        {
            notes.Add($"The header counts {file.UnstoredBoneCount:N0} bones, but a static mesh stores none; the mesh was read without a skeleton.");
        }
        if (stats.UnparentedItems > 0)
            notes.Add($"{stats.UnparentedItems:N0} collision spheres or prop points name a bone the mesh does not have; they were attached to the mesh instead.");
        if (turnedProps > 0)
            notes.Add($"{turnedProps:N0} prop points are turned; their orientation was copied as stored, in a convention no Red Faction II documentation confirms.");
        if (props.Count > 0) notes.Add($"Kept {props.Count:N0} prop point{(props.Count == 1 ? "" : "s")}.");
        if (spheres.Count > 0) notes.Add($"Kept {spheres.Count:N0} collision sphere{(spheres.Count == 1 ? "" : "s")}.");
        if (stats.TruncatedNames > 0) notes.Add($"{stats.TruncatedNames:N0} names were cut to fit the PC format's name fields.");
        if (file.TrailingBytes > 0) notes.Add($"Ignored {file.TrailingBytes:N0} bytes after the end of the mesh.");

        var description = new V3dMeshDescription
        {
            Kind = character ? V3dKind.Character : V3dKind.StaticMesh,
            Submeshes = [.. subs],
            Bones = bones.MoveToImmutable(),
            CollisionSpheres = spheres.MoveToImmutable(),
            PropPoints = props.MoveToImmutable(),
        };
        return new LegacyMesh(ext, description, [.. notes]) { IsRedFactionII = true };
    }

    /// <summary>
    /// The stored bone rotation maps model space to bone space (row-major, <c>x_bone = M x + t</c>); a PC bone stores
    /// the inverse bind with the rotation conjugated, so its quaternion is the conjugate of M's (which is the rotation
    /// from bone space to model space) and its translation is <c>t</c> as stored.
    /// </summary>
    internal static Quaternion BoneRotation(Matrix4x4 stored)
    {
        // Numerics applies matrices to row vectors, so M x is x times the transpose.
        var modelToBone = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(stored)));
        return Quaternion.Conjugate(modelToBone);
    }

    internal sealed class Stats
    {
        public int SplitObjects, EmptyLodParts, DroppedLods, LodObjects, ShiftedLods;
        public int Corners, Vertices, SourceVertices;
        public int MaterialRecords, EnvironmentMapUses, UnknownMaterialBits, GeometryValues;
        public float GeometryValueMin = float.MaxValue, GeometryValueMax = float.MinValue;
        public int OddWeightSums, Unweighted, UnparentedItems, TruncatedNames;
        public readonly List<string> EnvironmentMaps = [];
        public readonly List<string> LodDistances = [];
        public readonly HashSet<Rf2Material> SeenMaterials = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// The texture groups a static object is cut into: one group (every texture) when no LOD uses more than a PC LOD holds,
    /// else consecutive groups of at most <see cref="V3dBuilder.MaxTexturesPerLod"/> distinct texture names in order of
    /// first use.
    /// </summary>
    private static List<HashSet<string>?> Split(ImmutableArray<Rf2Lod> lods, Stats stats)
    {
        bool tooMany = lods.Any(l => l.Geometries.Select(g => l.Textures[g.Texture]).Distinct(StringComparer.OrdinalIgnoreCase).Count() > V3dBuilder.MaxTexturesPerLod);
        if (!tooMany) return [null];
        stats.SplitObjects++;
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lod in lods)
        {
            foreach (var g in lod.Geometries)
            {
                if (seen.Add(lod.Textures[g.Texture])) order.Add(lod.Textures[g.Texture]);
            }
        }
        var parts = new List<HashSet<string>?>();
        for (int i = 0; i < order.Count; i += V3dBuilder.MaxTexturesPerLod)
            parts.Add(new HashSet<string>(order.Skip(i).Take(V3dBuilder.MaxTexturesPerLod), StringComparer.OrdinalIgnoreCase));
        return parts;
    }

    private readonly record struct MaterialKey(string Texture, uint Flags, string Environment);

    private readonly record struct VertexKey(int Px, int Py, int Pz, int Nx, int Ny, int Nz, int U, int V, int Skin)
    {
        public static VertexKey From(Vector3 p, Vector3 n, Vector2 uv, int skin) => new(
            Bits(p.X), Bits(p.Y), Bits(p.Z), Bits(n.X), Bits(n.Y), Bits(n.Z), Bits(uv.X), Bits(uv.Y), skin);

        // -0 and +0 are the same value.
        private static int Bits(float f) => BitConverter.SingleToInt32Bits(f + 0f);
    }

    private sealed class Group(int material)
    {
        public int Material { get; } = material;
        public List<V3dMeshVertex> Vertices { get; } = [];
        public Dictionary<VertexKey, int> Index { get; } = [];
        public List<V3dMeshTriangle> Triangles { get; } = [];
    }

    private static V3dSubmeshDescription BuildSubmesh(Rf2MeshFile file, Rf2Object obj, ImmutableArray<Rf2Lod> lods, HashSet<string>? part, Stats stats)
    {
        var materials = new List<V3dMaterial>();
        var materialIndex = new Dictionary<MaterialKey, int>();
        var first = lods[0];
        var offset = file.IsSkinned ? Vector3.Zero : first.Offset;
        var lodList = ImmutableArray.CreateBuilder<V3dLodDescription>(lods.Length);
        var influenceCache = new Dictionary<Rf2Influences, ImmutableArray<V3dBoneInfluence>>();
        for (int l = 0; l < lods.Length; l++)
        {
            var lod = lods[l];
            var shift = file.IsSkinned ? Vector3.Zero : lod.Offset - offset;
            var groups = new Dictionary<int, Group>();
            bool any = false;
            foreach (var geometry in lod.Geometries)
            {
                string texture = lod.Textures[geometry.Texture];
                if (part is not null && !part.Contains(texture)) continue;
                any = true;
                var record = lod.Materials[geometry.Material];
                int material = Material(texture, record, materials, materialIndex, stats);
                if (!groups.TryGetValue(material, out var g)) groups.Add(material, g = new Group(material));
                foreach (var tri in geometry.Triangles)
                {
                    // the stored winding already faces the way a PC mesh expects (it agrees with the vertex normals)
                    int a = Corner(g, geometry, lod, tri.A, shift);
                    int b = Corner(g, geometry, lod, tri.B, shift);
                    int c = Corner(g, geometry, lod, tri.C, shift);
                    g.Triangles.Add(new V3dMeshTriangle(a, b, c));
                    stats.Corners += 3;
                }
            }
            if (!any && part is not null) stats.EmptyLodParts++;
            foreach (var g in groups.Values) stats.Vertices += g.Vertices.Count;

            ImmutableArray<Vector3>? morph = null;
            if (file.IsSkinned && lod.SourceVertexCount > 0)
            {
                // the welded source order (what RFA morph keys address), from each vertex's source index
                var originals = new Vector3[lod.SourceVertexCount];
                var known = new bool[originals.Length];
                foreach (var v in lod.SkinVertices)
                {
                    if (v.Source < 0 || v.Source >= originals.Length || known[v.Source]) continue;
                    originals[v.Source] = v.Position;
                    known[v.Source] = true;
                }
                for (int o = 0; o < originals.Length; o++)
                {
                    if (!known[o]) originals[o] = new Vector3(float.NaN);
                }
                morph = [.. originals];
            }

            lodList.Add(new V3dLodDescription
            {
                Distance = l == 0 ? 0f : SwitchDistance(obj.LodDistances, l),
                Groups = [.. groups.Values.OrderBy(g => g.Material).Select(g => new V3dMaterialGroup
                {
                    Material = g.Material,
                    Vertices = [.. g.Vertices],
                    Triangles = [.. g.Triangles],
                })],
                MorphVertices = morph,
            });
        }

        string objectName = first.Name.Length > 0 ? first.Name : "mesh";
        string parent = first.Parent is { Length: > 0 } p && !string.Equals(p, first.Name, StringComparison.OrdinalIgnoreCase) ? p : "None";
        return new V3dSubmeshDescription
        {
            Name = FixedString.FromText(Fit(objectName, V3dSubmesh.NameSize, stats), V3dSubmesh.NameSize),
            ParentName = FixedString.FromText(Fit(parent, V3dSubmesh.NameSize, stats), V3dSubmesh.NameSize),
            Offset = offset,
            Lods = lodList.MoveToImmutable(),
            Materials = [.. materials],
        };

        int Corner(Group g, Rf2Geometry geometry, Rf2Lod lod, Rf2Corner corner, Vector3 shift)
        {
            Vector3 position, normal;
            int skin = -1;
            if (file.IsSkinned)
            {
                var v = lod.SkinVertices[corner.Vertex];
                (position, normal, skin) = (v.Position, v.Normal, corner.Vertex);
            }
            else
            {
                (position, normal) = geometry.Vertices[corner.Vertex];
                position += shift;
            }
            var key = VertexKey.From(position, normal, corner.Uv, skin);
            if (g.Index.TryGetValue(key, out int index)) return index;
            index = g.Vertices.Count;
            g.Index.Add(key, index);
            var influences = file.IsSkinned ? Influences(lod.SkinVertices[corner.Vertex].Influences) : [];
            g.Vertices.Add(new V3dMeshVertex(position, normal, corner.Uv, influences));
            return index;
        }

        ImmutableArray<V3dBoneInfluence> Influences(Rf2Influences packed)
        {
            if (influenceCache.TryGetValue(packed, out var cached)) return cached;
            var list = new List<V3dBoneInfluence>(4);
            float sum = 0;
            for (int s = 0; s < 4; s++)
            {
                var (bone, weight) = packed[s];
                if (bone == Rf2MeshFile.NoBone || !(weight > 0f) || !float.IsFinite(weight)) continue;
                list.Add(new V3dBoneInfluence(bone, weight));
                sum += weight;
            }
            if (list.Count == 0) stats.Unweighted++;
            else if (MathF.Abs(sum - 1f) > 0.02f) stats.OddWeightSums++;
            ImmutableArray<V3dBoneInfluence> result = [.. list];
            influenceCache[packed] = result;
            return result;
        }
    }

    /// <summary>LOD <paramref name="level"/> starts where the one before it stops (the stored distance of that one).</summary>
    private static float SwitchDistance(ImmutableArray<float> distances, int level)
    {
        float d = level - 1 < distances.Length ? distances[level - 1] : 0f;
        return float.IsFinite(d) && d < Never && d > 0f ? d : level * 10f;
    }

    /// <summary>The submesh material for a texture and a material record (one per distinct texture, flags and environment map).</summary>
    private static int Material(string texture, Rf2Material record, List<V3dMaterial> materials, Dictionary<MaterialKey, int> index, Stats stats)
    {
        if (stats.SeenMaterials.Add(record))
        {
            stats.MaterialRecords++;
            if ((record.Field1 & 0x2) != 0) stats.UnknownMaterialBits++;
            if (record.EnvironmentMap.Length > 0)
            {
                stats.EnvironmentMapUses++;
                if (!stats.EnvironmentMaps.Contains(record.EnvironmentMap, StringComparer.OrdinalIgnoreCase)) stats.EnvironmentMaps.Add(record.EnvironmentMap);
            }
        }
        uint flags = 0x1u | (record.Field1 & AlphaFlag);
        string environment = record.EnvironmentMap.Length < V3dMaterial.NameSize ? record.EnvironmentMap : "";
        var key = new MaterialKey(texture.ToLowerInvariant(), flags, environment.ToLowerInvariant());
        if (index.TryGetValue(key, out int existing)) return existing;
        string diffuse = Fit(texture, V3dMaterial.NameSize, stats);
        materials.Add(new V3dMaterial(FixedString.FromText(diffuse, V3dMaterial.NameSize), 0f, 0f, 0f, 0f, FixedString.FromText(environment, V3dMaterial.NameSize), flags));
        index.Add(key, materials.Count - 1);
        return materials.Count - 1;
    }

    /// <summary>The text cut to fit a name field of <paramref name="size"/> bytes (with its terminator), Latin-1 only.</summary>
    private static string Fit(string text, int size, Stats stats)
    {
        string clean = new([.. text.Select(c => c == 0 || c > 255 ? '_' : c)]);
        if (clean.Length <= size - 1) return clean;
        stats.TruncatedNames++;
        return clean[..(size - 1)];
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        if (taken.Add(name)) return name;
        for (int n = 2; ; n++)
        {
            string suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
            string stem = name.Length > V3dSubmesh.NameSize - 1 - suffix.Length ? name[..(V3dSubmesh.NameSize - 1 - suffix.Length)] : name;
            if (taken.Add(stem + suffix)) return stem + suffix;
        }
    }
}
