using System.Collections.Immutable;
using System.Numerics;
using Cairn.Formats.Obj;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Interchange;

/// <summary>Options for <see cref="ObjMeshImport"/>.</summary>
public sealed record ObjMeshImportOptions
{
    /// <summary>Character or static; null: what Cairn's metadata says, else static (OBJ has no skeleton).</summary>
    public V3dKind? Kind { get; init; }

    /// <summary>For a character: bones, collision spheres and prop points come from this mesh, and every vertex follows its nearest bone.</summary>
    public V3dFile? KeepSkeletonFrom { get; init; }

    /// <summary>Uniform scale applied to positions (and prop points from Cairn's metadata).</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>The file is Z-up (3ds Max without "flip YZ", some CAD tools): +Z becomes up.</summary>
    public bool ZUp { get; init; }

    /// <summary>Texture name extension policy (as the glTF import).</summary>
    public TextureNamePolicy TextureNames { get; init; } = TextureNamePolicy.ForceTga;

    /// <summary>LOD distances for LODs without Cairn's metadata, by LOD index.</summary>
    public IReadOnlyList<float> DefaultLodDistances { get; init; } = [0f, 10f, 50f];

    /// <summary>Read Cairn's <c># rf_…</c> lines (see <see cref="ObjMeshMetadata"/>) when the file has them.</summary>
    public bool UseMetadata { get; init; } = true;
}

/// <summary>What <see cref="ObjMeshImport"/> produced.</summary>
/// <param name="Mesh">The mesh, or null when it could not be built (see the Error issues).</param>
/// <param name="Description">The intermediate description (null when not even that could be made).</param>
/// <param name="Issues">The pre-flight list: every engine limit the result breaks, and every conversion made.</param>
/// <param name="HasMetadata">True when Cairn's metadata lines were found and used.</param>
public sealed record ObjMeshImportResult(V3dFile? Mesh, V3dMeshDescription? Description, ImmutableArray<MeshImportIssue> Issues, bool HasMetadata)
{
    /// <summary>True when any issue is an error.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == MeshImportSeverity.Error);
}

/// <summary>An .obj read with its material libraries.</summary>
/// <param name="Obj">The geometry.</param>
/// <param name="Materials">Every material library it names, merged (empty when none was found).</param>
/// <param name="MissingLibraries">The <c>mtllib</c> names that could not be found or read.</param>
public sealed record ObjSource(ObjDocument Obj, MtlDocument Materials, IReadOnlyList<string> MissingLibraries);

/// <summary>
/// Wavefront OBJ (+ MTL) → <see cref="V3dMeshDescription"/> → <see cref="V3dFile"/> through <see cref="V3dBuilder"/>,
/// with the same kind of pre-flight report as <see cref="GltfMeshImport"/> (codes MI0xx). Space as
/// <see cref="GltfSpace"/> (X mirrored: winding reversed, normals mirrored), V flipped from OBJ's bottom-up
/// convention. One submesh per <c>o</c>/<c>g</c> part (<c>_LOD{n}</c> name suffixes give LODs), one material per
/// texture (<c>map_Kd</c>, else the material name), n-gons triangulated, vertices split by (position, UV, normal)
/// corner, normals from the file or computed from the smoothing groups. Cairn's own <c># rf_…</c> lines restore
/// submeshes, LODs, materials, render flags, double-sided triangles and prop points of a mesh Cairn exported.
/// </summary>
public static class ObjMeshImport
{
    /// <summary>Reads an .obj file and the .mtl files it names (beside it, or by the stored path).</summary>
    /// <exception cref="AssetFormatException">The .obj is damaged.</exception>
    /// <exception cref="IOException">The .obj cannot be read.</exception>
    public static ObjSource ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var obj = ObjReader.ReadFile(path);
        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var (mtl, missing) = ObjReader.ReadMaterialLibraries(obj, folder);
        return new ObjSource(obj, mtl, missing);
    }

    /// <summary>
    /// Reads an .obj from bytes; its material libraries come from <paramref name="readSibling"/> (a packfile's other
    /// entries, or files beside it) by file name. A library that is missing or damaged is listed, not thrown.
    /// </summary>
    /// <exception cref="AssetFormatException">The .obj is damaged.</exception>
    public static ObjSource Read(byte[] bytes, string fileName, Func<string, byte[]?>? readSibling)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var obj = ObjReader.Read(bytes, fileName);
        var merged = new MtlDocument();
        var missing = new List<string>();
        foreach (string library in obj.MaterialLibraries)
        {
            string name = new MtlTextureMap(library).FileName;
            byte[]? data = null;
            try { data = readSibling?.Invoke(name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException) { }
            if (data is null)
            {
                missing.Add(library);
                continue;
            }
            try
            {
                var mtl = ObjReader.ReadMtl(data, name);
                merged.Materials.AddRange(mtl.Materials);
            }
            catch (AssetFormatException)
            {
                missing.Add(library);
            }
        }
        return new ObjSource(obj, merged, missing);
    }

    /// <summary>True when the document carries Cairn's metadata lines (an export of a Cairn mesh).</summary>
    public static bool HasCairnMetadata(ObjDocument obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return obj.Comments.Any(c => c.ObjectIndex < 0 && ObjMeshMetadata.Parse(c.Text) is { Keyword: ObjMeshMetadata.Mesh });
    }

    /// <summary>The kind Cairn's metadata names, or null.</summary>
    public static V3dKind? MetadataKind(ObjDocument obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        foreach (var c in obj.Comments)
        {
            if (c.ObjectIndex >= 0 || ObjMeshMetadata.Parse(c.Text) is not { Keyword: ObjMeshMetadata.Mesh } line) continue;
            return line.Values.TryGetValue("kind", out var k) && k == "character" ? V3dKind.Character : V3dKind.StaticMesh;
        }
        return null;
    }

    /// <summary>Imports a read source as one V3D mesh.</summary>
    public static ObjMeshImportResult Import(ObjSource source, ObjMeshImportOptions? options = null, string fileName = "OBJ")
    {
        ArgumentNullException.ThrowIfNull(source);
        return Import(source.Obj, source.Materials, options, fileName, source.MissingLibraries);
    }

    /// <summary>Imports the document as one V3D mesh.</summary>
    /// <param name="obj">The geometry.</param>
    /// <param name="materials">Its material libraries (null or empty: texture names come from the material names).</param>
    /// <param name="options">The options.</param>
    /// <param name="fileName">For messages.</param>
    /// <param name="missingLibraries">Material libraries the file names that could not be read (reported).</param>
    public static ObjMeshImportResult Import(ObjDocument obj, MtlDocument? materials, ObjMeshImportOptions? options = null, string fileName = "OBJ",
        IReadOnlyList<string>? missingLibraries = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        options ??= new ObjMeshImportOptions();
        if (!(options.Scale > 0f) || !float.IsFinite(options.Scale)) throw new ArgumentException("Scale must be a positive number.", nameof(options));
        return new Importer(obj, materials ?? new MtlDocument(), options, fileName, missingLibraries ?? []).Run();
    }

    private readonly record struct VertexKey(int Position, int TexCoord, int NormalKind, int NormalId);

    private sealed class Group
    {
        public readonly Dictionary<VertexKey, int> Map = [];
        public readonly List<V3dMeshVertex> Vertices = [];
        public readonly List<V3dMeshTriangle> Triangles = [];
    }

    private sealed class SubmeshInfo
    {
        public required string Name;
        public string ParentName = "None";
        public Vector3 Offset;
        public readonly List<V3dMaterial> Slots = [];
        /// <summary>How many of <see cref="Slots"/> came from Cairn's <c>rf_slot</c> lines (the first ones).</summary>
        public int RestoredSlots;
        public readonly Dictionary<int, float> Distances = [];
        public readonly Dictionary<int, uint> Flags = [];
        public readonly SortedDictionary<int, SortedDictionary<(int Slot, uint Flags), Group>> Lods = [];
    }

    private sealed record MaterialMeaning(int Submesh, int Slot, uint RenderFlags, bool DoubleSided);

    private sealed class Importer(ObjDocument obj, MtlDocument mtl, ObjMeshImportOptions options, string fileName, IReadOnlyList<string> missingLibraries)
    {
        private readonly List<MeshImportIssue> _issues = [];
        private readonly Dictionary<(int Sub, int Slot), V3dMaterial> _metaSlots = [];
        private readonly Dictionary<string, MaterialMeaning> _metaMaterials = new(StringComparer.Ordinal);
        private readonly Dictionary<int, IReadOnlyDictionary<string, string>> _metaObjects = [];
        private readonly List<V3dPropPoint> _metaProps = [];
        private readonly HashSet<string> _noMap = new(StringComparer.Ordinal);
        private bool _metadata, _fatal, _oneLodExport;
        private int _unusedSlots;
        private V3dKind _kind;
        private int _ngons, _degenerate, _noUvFaces, _computedNormalFaces, _noMaterialFaces, _badMetadata;

        private void Issue(MeshImportSeverity s, string code, string message) => _issues.Add(new MeshImportIssue(s, code, message));

        public ObjMeshImportResult Run()
        {
            if (options.UseMetadata) ReadMetadata();
            _kind = options.Kind ?? (options.KeepSkeletonFrom?.Kind == V3dKind.Character ? V3dKind.Character : _metadata ? MetadataKind(obj) ?? V3dKind.StaticMesh : V3dKind.StaticMesh);
            foreach (string library in missingLibraries)
                Issue(MeshImportSeverity.Warning, "MI021", $"The material library '{library}' was not found or could not be read; texture names come from the material names.");

            var submeshes = new Dictionary<string, SubmeshInfo>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var part in Parts()) AddPart(part.Key, part.Name, part.Lod, part.Meta, part.Faces, submeshes, order);
            if (submeshes.Values.All(s => s.Lods.Values.All(l => l.Values.All(g => g.Triangles.Count == 0))))
            {
                Issue(MeshImportSeverity.Error, "MI013", $"{fileName} has no faces to import.");
                return new ObjMeshImportResult(null, null, [.. _issues], _metadata);
            }
            Summarise();

            bool fatal = false;
            var subs = ImmutableArray.CreateBuilder<V3dSubmeshDescription>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in order)
            {
                var desc = BuildSubmesh(submeshes[key], names, ref fatal);
                if (desc is not null) subs.Add(desc);
            }
            if (_unusedSlots > 0)
                Issue(MeshImportSeverity.Info, "MI026", $"{_unusedSlots} of the exported mesh's materials belong to levels of detail the file does not hold (no face uses them); they were left out.");

            ImmutableArray<V3dBone> bones = [];
            ImmutableArray<V3dCollisionSphere> spheres = [];
            ImmutableArray<V3dPropPoint> props = [.. _metaProps];
            if (_kind == V3dKind.Character)
            {
                if (options.KeepSkeletonFrom is { } keep)
                {
                    bones = keep.Bones;
                    spheres = [.. keep.CollisionSpheres];
                    props = keep.Submeshes.FirstOrDefault()?.Lods.FirstOrDefault()?.PropPoints ?? [];
                    var skeleton = Skeleton.FromFile(keep);
                    Issue(MeshImportSeverity.Info, "MI012", $"The {skeleton.Count} bones, collision spheres and prop points of the kept mesh are used.");
                    if (skeleton.Count > 0)
                    {
                        Issue(MeshImportSeverity.Warning, "MI025", "OBJ has no bone weights: every vertex follows the nearest bone of the kept skeleton. Weight-paint the mesh and import it as glTF for real skinning.");
                        for (int i = 0; i < subs.Count; i++) subs[i] = BindToNearestBone(subs[i], skeleton);
                    }
                }
                else Issue(MeshImportSeverity.Warning, "MI017", "A character was asked for but OBJ has no skeleton; the mesh gets no bones. Keep the skeleton of an open character mesh, or build a static mesh (.v3m).");
            }

            var description = new V3dMeshDescription
            {
                Kind = _kind,
                Submeshes = subs.ToImmutable(),
                Bones = bones,
                CollisionSpheres = spheres,
                PropPoints = props,
            };
            Limits(description);
            if (fatal || _fatal) return new ObjMeshImportResult(null, description, [.. _issues], _metadata);
            try
            {
                return new ObjMeshImportResult(V3dBuilder.Build(description, V3dBuildOptions.Default), description, [.. _issues], _metadata);
            }
            catch (ArgumentException ex)
            {
                Issue(MeshImportSeverity.Error, "MI099", $"The mesh could not be built: {ex.Message}");
                return new ObjMeshImportResult(null, description, [.. _issues], _metadata);
            }
        }

        // ── Cairn metadata ──────────────────────────────────────────────────

        private void ReadMetadata()
        {
            foreach (var c in obj.Comments)
            {
                if (ObjMeshMetadata.Parse(c.Text) is not { } line) continue;
                var v = line.Values;
                if (c.ObjectIndex >= 0)
                {
                    if (line.Keyword == ObjMeshMetadata.Submesh && c.FaceIndex == 0 && ObjMeshMetadata.TryGetInt(v, "index", out int si) && si >= 0) _metaObjects[c.ObjectIndex] = v;
                    continue;
                }
                switch (line.Keyword)
                {
                    case ObjMeshMetadata.Mesh:
                        _metadata = true;
                        _oneLodExport = !(v.TryGetValue("lod", out var exported) && exported == "all");
                        break;
                    case ObjMeshMetadata.Slot:
                        if (ObjMeshMetadata.TryGetInt(v, "submesh", out int sub) && ObjMeshMetadata.TryGetInt(v, "slot", out int slot) && sub >= 0 && slot is >= 0 and < 256
                            && ObjMeshMetadata.GetText(v, "texture") is { } texture)
                        {
                            float F(string key) => ObjMeshMetadata.TryGetFloat(v, key, out float f) ? f : 0f;
                            uint flags = ObjMeshMetadata.TryGetUInt(v, "flags", out uint fl) ? fl : 1u;
                            string refl = ObjMeshMetadata.GetText(v, "refl_map") ?? string.Empty;
                            _metaSlots[(sub, slot)] = new V3dMaterial(FixedString.FromText(Fits(texture, V3dMaterial.NameSize) ? texture : "x", V3dMaterial.NameSize),
                                F("emissive"), F("specular"), F("glossiness"), F("reflection"),
                                FixedString.FromText(Fits(refl, V3dMaterial.NameSize) ? refl : string.Empty, V3dMaterial.NameSize), flags);
                        }
                        else _badMetadata++;
                        break;
                    case ObjMeshMetadata.Material:
                        if (ObjMeshMetadata.GetText(v, "name") is { } name && ObjMeshMetadata.TryGetInt(v, "submesh", out int ms) && ObjMeshMetadata.TryGetInt(v, "slot", out int mslot)
                            && ms >= 0 && mslot is >= 0 and < 256)
                        {
                            uint rf = ObjMeshMetadata.TryGetUInt(v, "render_flags", out uint r) && r != 0 ? r : V3dBuilder.DefaultRenderFlags;
                            bool two = ObjMeshMetadata.TryGetBool(v, "double_sided", out bool d) && d;
                            _metaMaterials[name] = new MaterialMeaning(ms, mslot, rf, two);
                        }
                        else _badMetadata++;
                        break;
                    case ObjMeshMetadata.Prop:
                        if (ObjMeshMetadata.GetText(v, "name") is { } pname && Fits(pname, V3dPropPoint.NameSize)
                            && ObjMeshMetadata.TryGetFloats(v, "position", 3, out var pos) && ObjMeshMetadata.TryGetFloats(v, "rotation", 4, out var rot))
                        {
                            int parent = ObjMeshMetadata.TryGetInt(v, "parent", out int pp) ? pp : -1;
                            _metaProps.Add(new V3dPropPoint(FixedString.FromText(pname, V3dPropPoint.NameSize), new Quaternion(rot[0], rot[1], rot[2], rot[3]),
                                new Vector3(pos[0], pos[1], pos[2]) * options.Scale, parent));
                        }
                        else _badMetadata++;
                        break;
                }
            }
            if (!_metadata)
            {
                // rf_ lines without the marker are someone else's: ignore them all.
                _metaSlots.Clear();
                _metaMaterials.Clear();
                _metaObjects.Clear();
                _metaProps.Clear();
                _badMetadata = 0;
            }
        }

        private IEnumerable<(string Key, string Name, int Lod, IReadOnlyDictionary<string, string>? Meta, IReadOnlyList<ObjFace> Faces)> Parts()
        {
            foreach (var part in obj.SplitIntoParts())
            {
                if (_metadata && _metaObjects.TryGetValue(part.ObjectIndex, out var meta))
                {
                    ObjMeshMetadata.TryGetInt(meta, "index", out int index);
                    int lod = ObjMeshMetadata.TryGetInt(meta, "lod", out int l) && l >= 0 ? l : 0;
                    string name = ObjMeshMetadata.GetText(meta, "name") ?? part.Name;
                    yield return ($"#{index}", name, lod, meta, part.Faces);
                    continue;
                }
                int fromName = GltfSpace.LodIndexFromName(part.Name, out string baseName);
                yield return ("name:" + baseName, baseName, Math.Max(0, fromName), null, part.Faces);
            }
        }

        // ── Geometry ────────────────────────────────────────────────────────

        private Vector3 ToRfPosition(Vector3 p) => GltfSpace.FromGltf(Up(p)) * options.Scale;

        private Vector3 Up(Vector3 v) => options.ZUp ? new Vector3(v.X, v.Z, -v.Y) : v;

        private void AddPart(string key, string name, int lod, IReadOnlyDictionary<string, string>? meta, IReadOnlyList<ObjFace> faces,
            Dictionary<string, SubmeshInfo> submeshes, List<string> order)
        {
            if (faces.Count == 0) return;
            if (!submeshes.TryGetValue(key, out var info))
            {
                info = new SubmeshInfo { Name = name };
                submeshes[key] = info;
                order.Add(key);
                if (meta is not null)
                {
                    if (ObjMeshMetadata.GetText(meta, "parent") is { Length: > 0 } parent) info.ParentName = parent;
                    if (ObjMeshMetadata.TryGetFloats(meta, "offset", 3, out var off)) info.Offset = new Vector3(off[0], off[1], off[2]) * options.Scale;
                    if (ObjMeshMetadata.TryGetInt(meta, "index", out int si))
                    {
                        for (int slot = 0; _metaSlots.TryGetValue((si, slot), out var material); slot++) info.Slots.Add(material);
                        info.RestoredSlots = info.Slots.Count;
                    }
                }
            }
            if (meta is not null)
            {
                if (ObjMeshMetadata.TryGetFloat(meta, "distance", out float distance) && distance >= 0) info.Distances[lod] = distance * options.Scale;
                if (ObjMeshMetadata.TryGetUInt(meta, "lod_flags", out uint flags)) info.Flags[lod] = flags;
            }
            int submeshIndex = meta is not null && ObjMeshMetadata.TryGetInt(meta, "index", out int index) ? index : -1;
            if (!info.Lods.TryGetValue(lod, out var groups)) info.Lods[lod] = groups = [];

            // Normals the faces need computed: smooth ones accumulate per (position, smoothing group).
            var positions = obj.Positions;
            var smooth = new Dictionary<(int Position, int Group), Vector3>();
            var needsNormals = new bool[faces.Count];
            var faceNormals = new Vector3[faces.Count];
            var triangles = new IReadOnlyList<(int A, int B, int C)>[faces.Count];
            for (int f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                triangles[f] = face.Corners.Count == 3 ? [(0, 1, 2)] : ObjTriangulator.Triangulate(positions, face);
                if (face.Corners.Count > 3) _ngons++;
                needsNormals[f] = !face.Corners.All(c => c.HasNormal && (uint)c.Normal < (uint)obj.Normals.Count && UsableNormal(obj.Normals[c.Normal]));
                if (!needsNormals[f]) continue;
                _computedNormalFaces++;
                var n = Vector3.Zero;
                foreach (var (a, b, c) in triangles[f])
                {
                    var pa = Up(positions[face.Corners[a].Position]);
                    n += Vector3.Cross(Up(positions[face.Corners[b].Position]) - pa, Up(positions[face.Corners[c].Position]) - pa);
                }
                faceNormals[f] = n;
                if (face.SmoothingGroup != 0)
                {
                    foreach (var corner in face.Corners)
                    {
                        var k = (corner.Position, face.SmoothingGroup);
                        smooth[k] = smooth.TryGetValue(k, out var sum) ? sum + n : n;
                    }
                }
            }

            for (int f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                var (slot, renderFlags, twoSided) = SlotOf(info, submeshIndex, face.Material);
                var gkey = (slot, renderFlags);
                if (!groups.TryGetValue(gkey, out var group)) groups[gkey] = group = new Group();
                if (face.Corners.Any(c => !c.HasTexCoord || (uint)c.TexCoord >= (uint)obj.TexCoords.Count)) _noUvFaces++;
                var corners = new int[face.Corners.Count];
                for (int i = 0; i < corners.Length; i++) corners[i] = Vertex(group, face, f, i, needsNormals[f], faceNormals[f], smooth);
                ushort triFlags = twoSided ? V3dTriangle.DoubleSided : (ushort)0;
                foreach (var (a, b, c) in triangles[f])
                {
                    int pa = face.Corners[a].Position, pb = face.Corners[b].Position, pc = face.Corners[c].Position;
                    if (pa == pb || pb == pc || pa == pc || Vector3.Cross(positions[pb] - positions[pa], positions[pc] - positions[pa]).LengthSquared() == 0f) _degenerate++;
                    // Reverse the winding back to RF's left-handed space.
                    group.Triangles.Add(new V3dMeshTriangle(corners[a], corners[c], corners[b], triFlags));
                }
            }
        }

        private static bool UsableNormal(Vector3 n) => float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z) && n.LengthSquared() > 1e-24f;

        private int Vertex(Group group, ObjFace face, int faceIndex, int cornerIndex, bool computed, Vector3 faceNormal, Dictionary<(int, int), Vector3> smooth)
        {
            var corner = face.Corners[cornerIndex];
            int uv = corner.HasTexCoord && (uint)corner.TexCoord < (uint)obj.TexCoords.Count ? corner.TexCoord : -1;
            var key = !computed ? new VertexKey(corner.Position, uv, 0, corner.Normal)
                : face.SmoothingGroup != 0 ? new VertexKey(corner.Position, uv, 1, face.SmoothingGroup)
                : new VertexKey(corner.Position, uv, 2, faceIndex);
            if (group.Map.TryGetValue(key, out int index)) return index;
            Vector3 normal;
            if (!computed) normal = Up(obj.Normals[corner.Normal]);
            else if (face.SmoothingGroup != 0 && smooth.TryGetValue((corner.Position, face.SmoothingGroup), out var sum)) normal = sum;
            else normal = faceNormal;
            normal = normal.LengthSquared() > 0 && float.IsFinite(normal.LengthSquared()) ? Vector3.Normalize(GltfSpace.FromGltf(normal)) : Vector3.UnitY;
            var t = uv >= 0 ? obj.TexCoords[uv] : Vector2.Zero;
            index = group.Vertices.Count;
            group.Vertices.Add(new V3dMeshVertex(ToRfPosition(obj.Positions[corner.Position]), normal, new Vector2(t.X, 1f - t.Y)));
            group.Map[key] = index;
            return index;
        }

        /// <summary>The slot, render flags and sidedness of a face's material.</summary>
        private (int Slot, uint RenderFlags, bool DoubleSided) SlotOf(SubmeshInfo info, int submeshIndex, string? material)
        {
            if (material is not null && _metaMaterials.TryGetValue(material, out var meaning) && meaning.Submesh == submeshIndex && meaning.Slot < info.Slots.Count)
                return (meaning.Slot, meaning.RenderFlags, meaning.DoubleSided);
            if (material is null) _noMaterialFaces++;
            string texture = TextureName(material);
            int existing = info.Slots.FindIndex(s => string.Equals(s.DiffuseMap.Text, texture, StringComparison.OrdinalIgnoreCase));
            if (existing < 0)
            {
                if (!Fits(texture, V3dMaterial.NameSize))
                {
                    Issue(MeshImportSeverity.Error, "MI002", $"Texture name '{texture}' is {texture.Length} characters; at most {V3dMaterial.NameSize - 1} fit (or it has characters outside Latin-1). Rename the texture or material.");
                    texture = "x";
                    _fatal = true;
                }
                info.Slots.Add(new V3dMaterial(FixedString.FromText(texture, V3dMaterial.NameSize), 0f, 0f, 0f, 0f, FixedString.FromText(string.Empty, V3dMaterial.NameSize), 1u));
                existing = info.Slots.Count - 1;
            }
            return (existing, V3dBuilder.DefaultRenderFlags, false);
        }

        private string TextureName(string? material)
        {
            if (material is null) return "default.tga";
            if (mtl.Find(material)?.DiffuseMap is { } map && !string.IsNullOrWhiteSpace(map.FileName))
                return GltfMeshImport.NormalizeTextureName(map.FileName, options.TextureNames);
            _noMap.Add(material);
            // A material name is a name, not a file: "Floor.001" (Blender's duplicate suffix) becomes "Floor.001.tga".
            string ext = Path.GetExtension(material);
            return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                ? GltfMeshImport.NormalizeTextureName(material, options.TextureNames)
                : GltfMeshImport.NormalizeTextureName(material + ".tga", options.TextureNames);
        }

        private static readonly string[] ImageExtensions = [".tga", ".vbm", ".png", ".jpg", ".jpeg", ".dds", ".bmp", ".gif", ".webp"];

        private V3dSubmeshDescription? BuildSubmesh(SubmeshInfo info, HashSet<string> names, ref bool fatal)
        {
            string name = info.Name;
            if (!Fits(name, V3dSubmesh.NameSize))
            {
                string shortened = Shorten(name, V3dSubmesh.NameSize - 1);
                for (int n = 2; names.Contains(shortened); n++)
                {
                    string suffix = "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    shortened = Shorten(name, V3dSubmesh.NameSize - 1 - suffix.Length) + suffix;
                }
                Issue(MeshImportSeverity.Warning, "MI002", $"Object name '{name}' does not fit a submesh name ({V3dSubmesh.NameSize - 1} Latin-1 characters); it becomes '{shortened}'.");
                name = shortened;
            }
            names.Add(name);
            if (info.Slots.Count > 255)
            {
                Issue(MeshImportSeverity.Error, "MI004", $"Submesh '{name}' has {info.Slots.Count} materials; LOD texture entries index them with one byte.");
                fatal = true;
                return null;
            }

            var lodIndices = info.Lods.Where(l => l.Value.Values.Any(g => g.Triangles.Count > 0)).Select(l => l.Key).ToList();
            if (lodIndices.Count == 0) return null;

            // A one-LOD export lists every material of the submesh, the other LODs' too: restored ones no face uses are
            // left out (the game would load their textures for nothing), and the slots renumbered.
            var materials = info.Slots;
            Dictionary<int, int>? remap = null;
            if (_oneLodExport && info.RestoredSlots > 0)
            {
                var used = info.Lods.Values.SelectMany(l => l).Where(g => g.Value.Triangles.Count > 0).Select(g => g.Key.Slot).ToHashSet();
                var kept = Enumerable.Range(0, info.Slots.Count).Where(s => s >= info.RestoredSlots || used.Contains(s)).ToList();
                if (kept.Count < info.Slots.Count)
                {
                    _unusedSlots += info.Slots.Count - kept.Count;
                    remap = kept.Select((old, i) => (old, i)).ToDictionary(p => p.old, p => p.i);
                    materials = [.. kept.Select(s => info.Slots[s])];
                }
            }
            if (lodIndices[0] != 0 || lodIndices[^1] != lodIndices.Count - 1)
                Issue(MeshImportSeverity.Warning, "MI014", $"Submesh '{name}' has LODs {string.Join(", ", lodIndices)}; they are renumbered 0..{lodIndices.Count - 1}.");
            var lods = ImmutableArray.CreateBuilder<V3dLodDescription>();
            for (int l = 0; l < lodIndices.Count; l++)
            {
                int src = lodIndices[l];
                var groups = new List<V3dMaterialGroup>();
                foreach (var ((slot, flags), group) in info.Lods[src])
                {
                    if (group.Triangles.Count == 0) continue;
                    groups.Add(new V3dMaterialGroup { Material = remap?[slot] ?? slot, RenderFlags = flags, Vertices = [.. group.Vertices], Triangles = [.. group.Triangles] });
                    if (group.Vertices.Count > V3dBuilder.MaxBatchVertices || group.Triangles.Count > V3dBuilder.MaxBatchTriangles)
                        Issue(MeshImportSeverity.Info, "MI005", $"Submesh '{name}' LOD {l}: material '{info.Slots[slot].DiffuseMap.Text}' has {group.Vertices.Count} vertices / {group.Triangles.Count} triangles; it is split into several batches (at most {V3dBuilder.MaxBatchVertices} vertices and {V3dBuilder.MaxBatchTriangles} triangles each).");
                }
                float distance = info.Distances.TryGetValue(src, out float d) ? d : l < options.DefaultLodDistances.Count ? options.DefaultLodDistances[l] : 10f * l;
                lods.Add(new V3dLodDescription
                {
                    Distance = distance,
                    Flags = info.Flags.TryGetValue(src, out uint f) ? f : null,
                    Groups = [.. groups],
                });
            }

            var desc = new V3dSubmeshDescription
            {
                Name = FixedString.FromText(name, V3dSubmesh.NameSize),
                ParentName = FixedString.FromText(Fits(info.ParentName, V3dSubmesh.NameSize) ? info.ParentName : "None", V3dSubmesh.NameSize),
                Offset = info.Offset,
                Materials = [.. materials],
                Lods = lods.ToImmutable(),
            };
            // Vertices are in model space in the file (the export adds the offset); take it off again.
            return info.Offset != Vector3.Zero ? SubtractOffset(desc, info.Offset) : desc;
        }

        private static V3dSubmeshDescription SubtractOffset(V3dSubmeshDescription d, Vector3 offset) => d with
        {
            Lods = [.. d.Lods.Select(l => l with
            {
                Groups = [.. l.Groups.Select(g => g with { Vertices = [.. g.Vertices.Select(v => v with { Position = v.Position - offset })] })],
            })],
        };

        /// <summary>Every vertex fully bound to the bone whose rest position is nearest (model space).</summary>
        private static V3dSubmeshDescription BindToNearestBone(V3dSubmeshDescription d, Skeleton skeleton)
        {
            var joints = skeleton.RestWorld.Select(r => r.Position).ToArray();
            int Nearest(Vector3 p)
            {
                int best = 0;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < joints.Length; i++)
                {
                    float distance = Vector3.DistanceSquared(p, joints[i]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }
                return best;
            }
            return d with
            {
                Lods = [.. d.Lods.Select(l => l with
                {
                    Groups = [.. l.Groups.Select(g => g with { Vertices = [.. g.Vertices.Select(v => v with { Influences = [new V3dBoneInfluence(Nearest(v.Position + d.Offset), 1f)] })] })],
                })],
            };
        }

        // ── Report ──────────────────────────────────────────────────────────

        private void Summarise()
        {
            if (_metadata)
                Issue(MeshImportSeverity.Info, "MI026", "Cairn's metadata lines restored the submeshes, LODs, materials, render flags, double-sided triangles and prop points of the mesh it exported.");
            if (_badMetadata > 0)
                Issue(MeshImportSeverity.Warning, "MI026", $"{_badMetadata} of Cairn's metadata lines are damaged and were ignored; what they described gets the defaults.");
            if (obj.IgnoredStatements.Count > 0)
                Issue(MeshImportSeverity.Warning, "MI020", $"Only faces are imported; ignored: {string.Join(", ", obj.IgnoredStatements.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Value} '{k.Key}'"))}.");
            if (obj.Colors is not null)
                Issue(MeshImportSeverity.Info, "MI020", "The file has vertex colours; RF meshes have none, so they are dropped.");
            if (_noMap.Count > 0)
                Issue(MeshImportSeverity.Info, "MI022", $"{Plural(_noMap.Count, "material has", "materials have")} no texture map in the material library ({string.Join(", ", _noMap.Take(6))}{(_noMap.Count > 6 ? ", …" : "")}); the material name is used as the texture name.");
            if (_noMaterialFaces > 0)
                Issue(MeshImportSeverity.Info, "MI022", $"{Plural(_noMaterialFaces, "face has", "faces have")} no material; they get the texture default.tga.");
            if (_ngons > 0)
                Issue(MeshImportSeverity.Info, "MI023", $"{Plural(_ngons, "polygon", "polygons")} with more than three corners were triangulated.");
            if (_computedNormalFaces > 0)
                Issue(MeshImportSeverity.Info, "MI009", $"{Plural(_computedNormalFaces, "face has", "faces have")} no usable normals (missing or not finite); normals were computed from the smoothing groups (flat where smoothing is off).");
            if (_noUvFaces > 0)
                Issue(MeshImportSeverity.Warning, "MI010", $"{Plural(_noUvFaces, "face has", "faces have")} corners without texture coordinates; they get (0, 0).");
            if (_degenerate > 0)
                Issue(MeshImportSeverity.Info, "MI024", $"{Plural(_degenerate, "triangle has", "triangles have")} no area (corners at one point or on a line); they are kept but draw nothing.");
            if (options.ZUp) Issue(MeshImportSeverity.Info, "MI027", "The file was read as Z-up: its +Z became up.");
            if (Math.Abs(options.Scale - 1f) > 1e-6f) Issue(MeshImportSeverity.Info, "MI027", $"Positions were scaled by {options.Scale.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)}.");
        }

        private static string Plural(int n, string singular, string plural) =>
            $"{n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} {(n == 1 ? singular : plural)}";

        private void Limits(V3dMeshDescription d)
        {
            if (d.Bones.Length > V3dBoneSection.MaxBones)
                Issue(MeshImportSeverity.Error, "MI001", $"The skeleton has {d.Bones.Length} bones; the engine poses at most {V3dBoneSection.MaxBones}.");
            if (d.Kind == V3dKind.Character && d.Submeshes.Length > 1)
                Issue(MeshImportSeverity.Warning, "MI028", $"The mesh has {d.Submeshes.Length} submeshes, but the game keeps only the last submesh of a .v3c, so the others will not appear in game.");
            foreach (var s in d.Submeshes)
            {
                if (s.Lods.Length > V3dBuilder.MaxLods)
                    Issue(MeshImportSeverity.Error, "MI003", $"Submesh '{s.Name.Text}' has {s.Lods.Length} LODs; the engine (and Alpine Faction) holds at most {V3dBuilder.MaxLods}.");
                for (int l = 0; l < s.Lods.Length; l++)
                {
                    int textures = s.Lods[l].Textures?.Length ?? s.Lods[l].Groups.Select(g => g.Material).Distinct().Count();
                    if (textures > V3dBuilder.MaxTexturesPerLod)
                        Issue(MeshImportSeverity.Error, "MI004", $"Submesh '{s.Name.Text}' LOD {l} uses {textures} textures; the engine allows {V3dBuilder.MaxTexturesPerLod} per LOD.");
                }
            }
        }

        private static string Shorten(string text, int length)
        {
            var chars = text.Select(c => c is > '\0' and <= 'ÿ' ? c : '_').Take(Math.Max(1, length)).ToArray();
            return new string(chars);
        }

        private static bool Fits(string text, int field) => text.Length <= field - 1 && text.All(c => c is > '\0' and <= 'ÿ');
    }
}
