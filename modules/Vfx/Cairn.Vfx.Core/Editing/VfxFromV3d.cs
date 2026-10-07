using Cairn.Rfa.Formats.V3d;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>Which part of a V3D mesh <see cref="VfxFromV3d"/> converts, and how.</summary>
public sealed record VfxFromV3dOptions
{
    /// <summary>Convert only this submesh (file order). Null with no <see cref="SubmeshName"/> converts every submesh.</summary>
    public int? SubmeshIndex { get; init; }

    /// <summary>Convert only the submesh with this name (case-insensitive); ignored when <see cref="SubmeshIndex"/> is set.</summary>
    public string? SubmeshName { get; init; }

    /// <summary>Level of detail to read (0 = most detailed); a submesh with fewer LODs uses its last one.</summary>
    public int LodIndex { get; init; }

    /// <summary>Additive blending for newly created image materials.</summary>
    public bool Additive { get; init; }

    /// <summary>Parent name of the created meshes.</summary>
    public string Parent { get; init; } = "Scene Root";
}

/// <summary>What <see cref="VfxFromV3d.Add"/> produced: the new file and the section indices of the added meshes.</summary>
public sealed record VfxFromV3dResult(VfxFile File, ImmutableArray<int> MeshSections);

/// <summary>
/// Builds static effect meshes from the geometry of a .v3m, or a .v3c in bind pose: one mesh per submesh,
/// vertices welded by position across batches, per-corner UVs, one material slot per texture of the LOD
/// (image materials named after the texture, reusing an identical material already in the file), smoothing
/// group 1 where the stored vertex normals bend away from the face normal (0 for flat faces), the submesh
/// offset as the static transform, fullbright off. Both formats share RF's left-handed Y-up space and the
/// same triangle order convention, so positions and winding are copied as they are.
/// </summary>
public static class VfxFromV3d
{
    /// <summary>Corner normals within about 2.5 degrees of the face normal count as flat.</summary>
    private const float FlatCosine = 0.999f;

    /// <summary>A new 0x40006 effect holding the converted meshes and their materials.</summary>
    public static VfxFile CreateFile(V3dFile source, VfxFromV3dOptions? options = null) =>
        Add(VfxBuilder.NewFile(), source, options).File;

    /// <summary>Adds the converted meshes (and any materials they need) to an existing 0x40006 effect.</summary>
    public static VfxFile AddTo(VfxFile target, V3dFile source, VfxFromV3dOptions? options = null) =>
        Add(target, source, options).File;

    /// <summary>
    /// Adds the converted meshes to <paramref name="target"/>; mesh names are made unique against it. Throws
    /// <see cref="ArgumentException"/> when the selected submesh does not exist or has no LOD.
    /// </summary>
    public static VfxFromV3dResult Add(VfxFile target, V3dFile source, VfxFromV3dOptions? options = null)
    {
        VfxEdit.RequireCurrent(target);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new VfxFromV3dOptions();
        if (options.LodIndex < 0) throw new ArgumentOutOfRangeException(nameof(options), "The LOD index cannot be negative.");

        var file = target;
        var added = ImmutableArray.CreateBuilder<int>();
        foreach (var submesh in Select(source, options))
        {
            var builder = Describe(submesh, options, ref file);
            builder.Name = VfxEdit.UniqueName(file, builder.Name);
            file = VfxEdit.AddSection(file, builder.Build());
            added.Add(file.Sections.IndexOf(file.Sections.First(s => s is VfxMesh m && m.Name == builder.Name)));
        }
        // Single-frame meshes need no end frame change (frame 0 is always played).
        return new VfxFromV3dResult(file, added.ToImmutable());
    }

    /// <summary>The submeshes <paramref name="options"/> selects, in file order.</summary>
    public static IReadOnlyList<V3dSubmesh> Select(V3dFile source, VfxFromV3dOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var all = source.Submeshes.ToList();
        if (options?.SubmeshIndex is { } index)
            return (uint)index < (uint)all.Count ? [all[index]] : throw new ArgumentException($"The mesh has no submesh {index}.", nameof(options));
        if (options?.SubmeshName is { } name)
            return all.FirstOrDefault(s => string.Equals(s.Name.Text, name, StringComparison.OrdinalIgnoreCase)) is { } found
                ? [found] : throw new ArgumentException($"The mesh has no submesh named \"{name}\".", nameof(options));
        return all;
    }

    /// <summary>The mesh description of one submesh; materials are found in or appended to <paramref name="file"/>.</summary>
    private static VfxMeshBuilder Describe(V3dSubmesh submesh, VfxFromV3dOptions options, ref VfxFile file)
    {
        if (submesh.Lods.Length == 0) throw new ArgumentException($"Submesh \"{submesh.Name.Text}\" has no LOD.", nameof(options));
        var lod = submesh.Lods[Math.Min(options.LodIndex, submesh.Lods.Length - 1)];

        // One slot per distinct texture name of the LOD, in texture-list order.
        var slotOfTexture = new int[lod.Textures.Length];
        var slotNames = new List<string>();
        for (int t = 0; t < lod.Textures.Length; t++)
        {
            string tex = TextureName(submesh, lod.Textures[t]);
            int slot = slotNames.FindIndex(n => string.Equals(n, tex, StringComparison.OrdinalIgnoreCase));
            if (slot < 0) { slot = slotNames.Count; slotNames.Add(tex); }
            slotOfTexture[t] = slot;
        }
        var materialIndices = new List<int>(slotNames.Count);
        foreach (var tex in slotNames) materialIndices.Add(FindOrAddMaterial(ref file, VfxBuilder.ImageMaterial(tex, options.Additive)));

        var positions = new List<Vector3>();
        var weld = new Dictionary<Vector3, int>();
        var triangles = new List<(int A, int B, int C)>();
        var uvs = new List<Vector2>();
        var cornerNormals = new List<Vector3>();
        var faceMaterials = new List<int>();
        foreach (var batch in lod.Batches)
        {
            var map = new int[batch.VertexCount];
            for (int v = 0; v < map.Length; v++)
            {
                var p = batch.Positions[v];
                if (!weld.TryGetValue(p, out map[v])) { map[v] = positions.Count; weld.Add(p, positions.Count); positions.Add(p); }
            }
            int material = (uint)batch.TextureIndex < (uint)slotOfTexture.Length ? slotOfTexture[batch.TextureIndex] : -1;
            foreach (var tri in batch.Triangles)
            {
                if (tri.A >= map.Length || tri.B >= map.Length || tri.C >= map.Length)
                    throw new System.IO.InvalidDataException($"Submesh \"{submesh.Name.Text}\" has a triangle with a vertex index past its batch.");
                triangles.Add((map[tri.A], map[tri.B], map[tri.C]));
                foreach (int v in (ReadOnlySpan<int>)[tri.A, tri.B, tri.C])
                {
                    uvs.Add(v < batch.TexCoords.Length ? batch.TexCoords[v] : Vector2.Zero);
                    cornerNormals.Add(v < batch.Normals.Length ? batch.Normals[v] : Vector3.Zero);
                }
                faceMaterials.Add(material);
            }
        }

        var smoothing = new int[triangles.Count];
        for (int f = 0; f < smoothing.Length; f++)
        {
            var (a, b, c) = triangles[f];
            var n = VfxGeometry.FaceShape(positions[a], positions[b], positions[c]).Normal;
            for (int k = 0; k < 3; k++)
            {
                var cn = cornerNormals[3 * f + k];
                float len = cn.Length();
                if (len > 1e-6f && float.IsFinite(len) && Vector3.Dot(cn / len, n) < FlatCosine) { smoothing[f] = 1; break; }
            }
        }

        string name = submesh.Name.Text;
        return new VfxMeshBuilder
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Mesh" : name,
            Parent = options.Parent,
            Frames = [positions.ToArray()],
            Triangles = triangles,
            Uvs = [.. uvs],
            FaceMaterials = materialIndices.Count > 0 ? [.. faceMaterials] : null,
            SmoothingGroups = smoothing,
            MaterialIndices = materialIndices,
            Flags = 0,
            Transform = VfxBuilder.Identity with { Translation = submesh.Offset },
        };
    }

    /// <summary>The LOD texture's file name, falling back to its material's diffuse map.</summary>
    private static string TextureName(V3dSubmesh submesh, V3dLodTexture texture)
    {
        if (!string.IsNullOrWhiteSpace(texture.FileName)) return texture.FileName;
        return texture.MaterialIndex < submesh.Materials.Length ? submesh.Materials[texture.MaterialIndex].DiffuseMap.Text : "";
    }

    /// <summary>Material number of an identical material already in the file, or of <paramref name="material"/> appended.</summary>
    private static int FindOrAddMaterial(ref VfxFile file, VfxMaterial material)
    {
        var existing = file.Sections.OfType<VfxMaterial>().ToList();
        int found = existing.FindIndex(m => SameMaterial(m, material));
        if (found >= 0) return found;
        file = VfxEdit.AddSection(file, material);
        return existing.Count;
    }

    private static bool SameMaterial(VfxMaterial a, VfxMaterial b) =>
        a.Type == b.Type && a.Fps == b.Fps && a.Additive == b.Additive && a.LegacyFps == b.LegacyFps
        && SameTexture(a.Texture0, b.Texture0) && SameTexture(a.Texture1, b.Texture1)
        && a.SpecularGlossReflection == b.SpecularGlossReflection && a.ReflectionTexture == b.ReflectionTexture
        && a.SolidColor == b.SolidColor && Same(a.Mix, b.Mix)
        && a.SelfIllumination.SequenceEqual(b.SelfIllumination) && Same(a.Opacity, b.Opacity);

    private static bool SameTexture(VfxTexture? a, VfxTexture? b) =>
        a is null || b is null ? a is null && b is null
            : string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) && a with { Name = b.Name } == b;

    private static bool Same(ImmutableArray<float>? a, ImmutableArray<float>? b) =>
        a is null || b is null ? a is null && b is null : a.Value.SequenceEqual(b.Value);
}
