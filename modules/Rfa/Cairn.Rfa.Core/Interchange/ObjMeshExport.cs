using System.Collections.Immutable;
using System.Numerics;
using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Formats.Obj;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Interchange;

/// <summary>Options for <see cref="ObjMeshExport"/>.</summary>
public sealed record ObjMeshExportOptions
{
    /// <summary>
    /// The LOD to export from every submesh (a submesh with fewer LODs gives its least detailed one); null exports
    /// every LOD, each as its own object named <c>{submesh}_LOD{n}</c>.
    /// </summary>
    public int? Lod { get; init; } = 0;

    /// <summary>Resolves texture names to image files, decoded and written as PNG beside the .obj. Null names the RF textures in the .mtl without images.</summary>
    public AssetResolver? TextureResolver { get; init; }

    /// <summary>Write Cairn's <c># rf_…</c> metadata lines (see <see cref="ObjMeshMetadata"/>) so importing back restores what OBJ cannot hold.</summary>
    public bool WriteMetadata { get; init; } = true;

    /// <summary>The source's file name for the header comment ("crate.v3m"), or null.</summary>
    public string? SourceName { get; init; }
}

/// <summary>A texture decoded for the .mtl: the RF name, the PNG's file name beside the .obj, and its bytes.</summary>
/// <param name="TextureName">The RF texture name ("crate01.tga").</param>
/// <param name="FileName">The PNG written beside the .obj ("crate01.png").</param>
/// <param name="Png">The PNG bytes.</param>
public sealed record ObjMeshImage(string TextureName, string FileName, byte[] Png);

/// <summary>What <see cref="ObjMeshExport"/> produced.</summary>
/// <param name="Obj">The .obj document (write it with <see cref="ObjMeshExport.WriteFiles"/>).</param>
/// <param name="Mtl">The material library.</param>
/// <param name="Images">The decoded textures (empty without a resolver).</param>
/// <param name="Warnings">Plain-language notes: what OBJ cannot carry, missing textures, sanitised values.</param>
/// <param name="MissingTextures">Texture names that could not be found or decoded (the .mtl names the RF texture instead).</param>
public sealed record ObjMeshExportResult(ObjDocument Obj, MtlDocument Mtl, ImmutableArray<ObjMeshImage> Images, ImmutableArray<string> Warnings,
    ImmutableArray<string> MissingTextures)
{
    /// <summary>The files written beside <paramref name="objPath"/>: the .mtl and one PNG per image.</summary>
    public IReadOnlyList<string> SideFiles(string objPath)
    {
        ArgumentNullException.ThrowIfNull(objPath);
        string folder = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? ".";
        var files = new List<string> { Path.ChangeExtension(Path.GetFullPath(objPath), ".mtl") };
        files.AddRange(Images.Select(i => Path.Combine(folder, i.FileName)));
        return files;
    }
}

/// <summary>
/// <see cref="V3dFile"/> → Wavefront OBJ + MTL, in the same space as the mesh glTF export (<see cref="GltfSpace"/>:
/// X mirrored, so the triangle winding is reversed and normals are mirrored with the geometry), metres, Y up, and V
/// flipped to OBJ's bottom-up convention. A character exports in its bind pose (OBJ has no skeleton). Each submesh
/// (each LOD, when all are exported) is one <c>o</c>; each (material, render flags, sidedness) one <c>usemtl</c> whose
/// .mtl entry names the texture's PNG copy (or the RF name when it cannot be decoded). Positions, UVs and normals
/// are shared within an object, so 3D tools see connected surfaces; <see cref="ObjMeshImport"/> splits them again.
/// </summary>
public static class ObjMeshExport
{
    /// <summary>Builds the documents.</summary>
    /// <exception cref="ArgumentException">A LOD's batch refers to a texture or material that does not exist.</exception>
    public static ObjMeshExportResult Export(V3dFile mesh, ObjMeshExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new ObjMeshExportOptions();
        if (options.Lod is < 0) throw new ArgumentException("The LOD index cannot be negative.", nameof(options));
        return new Exporter(mesh, options).Run();
    }

    /// <summary>
    /// Writes <paramref name="objPath"/>, the .mtl beside it (same base name) and the PNG textures, each atomically.
    /// Returns the files written, the .obj first.
    /// </summary>
    public static IReadOnlyList<string> WriteFiles(string objPath, ObjMeshExportResult result)
    {
        ArgumentNullException.ThrowIfNull(objPath);
        ArgumentNullException.ThrowIfNull(result);
        string full = Path.GetFullPath(objPath);
        string folder = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(folder);
        var files = new List<string> { full };
        foreach (var image in result.Images)
        {
            string path = Path.Combine(folder, image.FileName);
            AtomicFile.WriteAllBytes(path, image.Png);
            files.Add(path);
        }
        ObjWriter.WriteFiles(full, result.Obj, result.Mtl);
        files.Insert(1, Path.ChangeExtension(full, ".mtl"));
        return files;
    }

    /// <summary>Exports and writes (see <see cref="WriteFiles"/>).</summary>
    public static ObjMeshExportResult ExportToFile(string objPath, V3dFile mesh, ObjMeshExportOptions? options = null)
    {
        var result = Export(mesh, options);
        WriteFiles(objPath, result);
        return result;
    }

    /// <summary>A texture name as the .mtl and the resolver use it: the file name only, ".tga" added when it has no extension (as the glTF export does).</summary>
    public static string NormalizeTextureName(string? texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return "default.tga";
        string name = Path.GetFileName(texture.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) return "default.tga";
        if (string.IsNullOrEmpty(Path.GetExtension(name))) name += ".tga";
        return name;
    }

    /// <summary>The OBJ material name a texture gives: its name without the extension, whitespace replaced.</summary>
    internal static string MaterialBaseName(string texture)
    {
        string stem = Path.GetFileNameWithoutExtension(texture.Replace('\\', '/'));
        var chars = stem.Select(c => char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c).ToArray();
        string name = new string(chars).Trim();
        return name.Length == 0 ? "material" : name;
    }

    private sealed class Pool<T> where T : notnull
    {
        private readonly Dictionary<T, int> _map = [];
        private readonly List<T> _target;

        public Pool(List<T> target) => _target = target;

        public int Get(T value)
        {
            if (_map.TryGetValue(value, out int index)) return index;
            index = _target.Count;
            _target.Add(value);
            _map[value] = index;
            return index;
        }
    }

    private sealed class Exporter(V3dFile mesh, ObjMeshExportOptions options)
    {
        private readonly ObjDocument _obj = new();
        private readonly MtlDocument _mtl = new();
        private readonly List<string> _warnings = [];
        private readonly List<string> _missing = [];
        private readonly List<ObjMeshImage> _images = [];
        private readonly Dictionary<string, (string? File, bool Alpha)> _textures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imageNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(int Sub, int Slot, bool Two, uint Flags), string> _materials = [];
        private readonly HashSet<string> _materialNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _objectNames = new(StringComparer.Ordinal);
        private int _badNormals, _badPositions, _badUvs;

        public ObjMeshExportResult Run()
        {
            var subs = mesh.Submeshes.ToList();
            string lodText = options.Lod is { } l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : "all";
            _obj.Comments.Add(new ObjComment($"Cairn OBJ export{(options.SourceName is { Length: > 0 } source ? " of " + source : "")} ({(options.Lod is { } n ? $"LOD {n}" : "every LOD")})"));
            _obj.Comments.Add(new ObjComment("Metres, Y up; X is mirrored from Red Faction's left-handed space and V is flipped (OBJ convention)."));
            if (options.WriteMetadata)
            {
                _obj.Comments.Add(new ObjComment("The rf_ lines are Cairn's own metadata for importing back; other tools ignore them."));
                _obj.Comments.Add(new ObjComment(ObjMeshMetadata.Line(ObjMeshMetadata.Mesh, ("kind", mesh.Kind == V3dKind.Character ? "character" : "static"), ("lod", lodText))));
                for (int si = 0; si < subs.Count; si++)
                {
                    for (int m = 0; m < subs[si].Materials.Length; m++)
                    {
                        var mat = subs[si].Materials[m];
                        _obj.Comments.Add(new ObjComment(ObjMeshMetadata.Line(ObjMeshMetadata.Slot,
                            ("submesh", ObjMeshMetadata.Number(si)), ("slot", ObjMeshMetadata.Number(m)), ("texture", ObjMeshMetadata.Text(mat.DiffuseMap.Text)),
                            ("emissive", ObjMeshMetadata.Number(mat.Emissive)), ("specular", ObjMeshMetadata.Number(mat.Unknown0)),
                            ("glossiness", ObjMeshMetadata.Number(mat.Unknown1)), ("reflection", ObjMeshMetadata.Number(mat.ReflectionCoefficient)),
                            ("refl_map", ObjMeshMetadata.Text(mat.ReflectionMap.Text)), ("flags", ObjMeshMetadata.Hex(mat.Flags)))));
                    }
                }
                AddProps(subs);
            }

            var lodsShortened = new List<string>();
            for (int si = 0; si < subs.Count; si++)
            {
                var s = subs[si];
                if (s.Lods.Length == 0) continue;
                IEnumerable<int> lods;
                if (options.Lod is { } want)
                {
                    int use = Math.Min(want, s.Lods.Length - 1);
                    if (use != want) lodsShortened.Add($"{s.Name.Text} (LOD {use})");
                    lods = [use];
                }
                else lods = Enumerable.Range(0, s.Lods.Length);
                foreach (int li in lods) AddObject(s, si, li);
            }
            if (lodsShortened.Count > 0)
                _warnings.Add($"LOD {options.Lod} does not exist in every submesh; these give their least detailed LOD instead: {string.Join(", ", lodsShortened)}.");

            if (mesh.Kind == V3dKind.Character)
            {
                int bones = mesh.Bones.Length, spheres = mesh.CollisionSpheres.Count();
                _warnings.Add($"OBJ holds geometry only: the mesh is written in its bind pose without its skeleton ({bones} bones), bone weights or {spheres} collision spheres. Use glTF to keep them.");
            }
            int lodCount = subs.Count == 0 ? 0 : subs.Max(x => x.Lods.Length);
            if (options.Lod is not null && lodCount > 1)
                _warnings.Add($"Only LOD {options.Lod} is written; the mesh has {lodCount} LODs.");
            if (_badNormals > 0) _warnings.Add($"{_badNormals} vertex normals were not finite or zero length (some stock meshes store NaN normals); they are written as (0, 1, 0).");
            if (_badPositions > 0) _warnings.Add($"{_badPositions} vertex positions were not finite; they are written as the origin.");
            if (_badUvs > 0) _warnings.Add($"{_badUvs} texture coordinates were not finite; they are written as (0, 0).");
            if (!options.WriteMetadata)
                _warnings.Add("Without Cairn's metadata lines, render flags, double-sided triangles, material properties, LOD distances and prop points are not kept.");
            return new ObjMeshExportResult(_obj, _mtl, [.. _images], [.. _warnings], [.. _missing]);
        }

        private void AddProps(List<V3dSubmesh> subs)
        {
            // Prop points are model-level: every stock LOD stores the same list (see GltfExport).
            var props = subs.SelectMany(s => s.Lods).FirstOrDefault(l => l.PropPoints.Length > 0)?.PropPoints ?? [];
            foreach (var p in props)
            {
                _obj.Comments.Add(new ObjComment(ObjMeshMetadata.Line(ObjMeshMetadata.Prop,
                    ("name", ObjMeshMetadata.Text(p.Name.Text)), ("parent", ObjMeshMetadata.Number(p.ParentIndex)),
                    ("position", ObjMeshMetadata.Vector(p.Position)), ("rotation", ObjMeshMetadata.Quaternion(p.Rotation)))));
            }
        }

        private void AddObject(V3dSubmesh s, int si, int li)
        {
            var lod = s.Lods[li];
            string baseName = string.IsNullOrWhiteSpace(s.Name.Text) ? $"submesh_{si}" : s.Name.Text.Replace('\n', '_').Replace('\r', '_');
            string name = options.Lod is null ? $"{baseName}_LOD{li}" : baseName;
            for (int n = 2; !_objectNames.Add(name); n++) name = options.Lod is null ? $"{baseName}_{n}_LOD{li}" : $"{baseName}_{n}";
            var obj = new ObjObject(name);
            int objectIndex = _obj.Objects.Count;
            _obj.Objects.Add(obj);
            if (options.WriteMetadata)
            {
                _obj.Comments.Add(new ObjComment(ObjMeshMetadata.Line(ObjMeshMetadata.Submesh,
                    ("index", ObjMeshMetadata.Number(si)), ("name", ObjMeshMetadata.Text(s.Name.Text)), ("parent", ObjMeshMetadata.Text(s.ParentName.Text)),
                    ("lod", ObjMeshMetadata.Number(li)), ("distance", ObjMeshMetadata.Number(li < s.LodDistances.Length ? s.LodDistances[li] : 0f)),
                    ("lod_flags", ObjMeshMetadata.Hex(lod.Flags)), ("offset", ObjMeshMetadata.Vector(s.Offset))), objectIndex, 0));
            }

            // Shared pools per object: equal values become one index (3D tools then see connected surfaces).
            var positions = new Pool<Vector3>(_obj.Positions);
            var uvs = new Pool<Vector2>(_obj.TexCoords);
            var normals = new Pool<Vector3>(_obj.Normals);
            foreach (var batch in lod.Batches)
            {
                if (batch.TriangleCount == 0 || batch.VertexCount == 0) continue;
                if ((uint)batch.TextureIndex >= (uint)lod.Textures.Length)
                    throw new ArgumentException($"{s.Name.Text} LOD {li} has a batch with texture index {batch.TextureIndex}, outside its {lod.Textures.Length}-entry texture list.");
                int slot = lod.Textures[batch.TextureIndex].MaterialIndex;
                if (slot >= s.Materials.Length)
                    throw new ArgumentException($"{s.Name.Text} LOD {li} uses material {slot}, but the submesh has {s.Materials.Length}.");
                int nv = batch.VertexCount;
                var pi = new int[nv];
                var ti = new int[nv];
                var ni = new int[nv];
                for (int i = 0; i < nv; i++)
                {
                    var p = i < batch.Positions.Length ? batch.Positions[i] + s.Offset : Vector3.Zero;
                    if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                    {
                        _badPositions++;
                        p = Vector3.Zero;
                    }
                    var uv = i < batch.TexCoords.Length ? batch.TexCoords[i] : Vector2.Zero;
                    if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                    {
                        _badUvs++;
                        uv = Vector2.Zero;
                    }
                    var nrm = i < batch.Normals.Length ? batch.Normals[i] : Vector3.Zero;
                    float len = nrm.Length();
                    if (!float.IsFinite(len) || len < 1e-12f)
                    {
                        _badNormals++;
                        nrm = Vector3.UnitY;
                    }
                    else nrm = GltfSpace.ToGltf(nrm / len);
                    pi[i] = positions.Get(GltfSpace.ToGltf(p));
                    ti[i] = uvs.Get(new Vector2(uv.X, 1f - uv.Y));
                    ni[i] = normals.Get(nrm);
                }
                foreach (bool twoSided in new[] { false, true })
                {
                    string? material = null;
                    foreach (var t in batch.Triangles)
                    {
                        if (((t.Flags & V3dTriangle.DoubleSided) != 0) != twoSided) continue;
                        if (t.A >= nv || t.B >= nv || t.C >= nv)
                            throw new ArgumentException($"{s.Name.Text} LOD {li} has a triangle that uses vertex {Math.Max(t.A, Math.Max(t.B, t.C))} of a {nv}-vertex batch.");
                        material ??= Material(s, si, slot, twoSided, batch.RenderFlags);
                        var face = new ObjFace { Material = material, SmoothingGroup = 1 };
                        // Reverse the winding for the mirrored (right-handed) space.
                        foreach (int v in new int[] { t.A, t.C, t.B }) face.Corners.Add(new ObjCorner(pi[v], ti[v], ni[v]));
                        obj.Faces.Add(face);
                    }
                }
            }
        }

        private string Material(V3dSubmesh s, int si, int slot, bool twoSided, uint renderFlags)
        {
            if (_materials.TryGetValue((si, slot, twoSided, renderFlags), out var existing)) return existing;
            string texture = s.Materials[slot].DiffuseMap.Text;
            string baseName = MaterialBaseName(texture);
            string name = baseName;
            for (int n = 2; !_materialNames.Add(name); n++) name = $"{baseName}_{n}";
            _materials[(si, slot, twoSided, renderFlags)] = name;

            var (file, alpha) = Texture(texture);
            var material = new MtlMaterial(name)
            {
                Ambient = Vector3.One,
                Diffuse = Vector3.One,
                Specular = Vector3.Zero,
                Dissolve = 1f,
                Illumination = 1,
                DiffuseMap = new MtlTextureMap(file ?? NormalizeTextureName(texture)),
            };
            if (alpha && file is not null) material.OtherStatements.Add(new KeyValuePair<string, string>("map_d", file));
            _mtl.Materials.Add(material);
            if (options.WriteMetadata)
            {
                _obj.Comments.Add(new ObjComment(ObjMeshMetadata.Line(ObjMeshMetadata.Material,
                    ("name", ObjMeshMetadata.Text(name)), ("submesh", ObjMeshMetadata.Number(si)), ("slot", ObjMeshMetadata.Number(slot)),
                    ("render_flags", ObjMeshMetadata.Hex(renderFlags)), ("double_sided", twoSided ? "1" : "0"))));
            }
            return name;
        }

        /// <summary>The PNG file name of a texture (decoded once), or null when it cannot be found or decoded.</summary>
        private (string? File, bool Alpha) Texture(string texture)
        {
            string name = NormalizeTextureName(texture);
            if (_textures.TryGetValue(name, out var cached)) return cached;
            (string? File, bool Alpha) result = (null, false);
            if (options.TextureResolver is { } resolver)
            {
                try
                {
                    if (resolver.Resolve(name) is { } location)
                    {
                        var image = ImageDecoder.Decode(location.ReadAllBytes(), location.ResolvedName);
                        bool alpha = false;
                        for (int i = 3; i < image.Pixels.Length; i += 4)
                        {
                            if (image.Pixels[i] != 255)
                            {
                                alpha = true;
                                break;
                            }
                        }
                        string file = UniqueImageName(Path.GetFileNameWithoutExtension(name));
                        _images.Add(new ObjMeshImage(name, file, PngEncoder.Encode(image)));
                        result = (file, alpha);
                    }
                    else
                    {
                        _missing.Add(name);
                        _warnings.Add($"Texture '{name}' was not found; the .mtl names it without an image.");
                    }
                }
                catch (Exception ex) when (ex is ImageDecodeException or IOException or UnauthorizedAccessException or AssetFormatException)
                {
                    _missing.Add(name);
                    _warnings.Add($"Texture '{name}' could not be read ({ex.Message}); the .mtl names it without an image.");
                }
            }
            _textures[name] = result;
            return result;
        }

        private string UniqueImageName(string stem)
        {
            var clean = new string(stem.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
            if (clean.Length == 0) clean = "texture";
            string file = clean + ".png";
            for (int i = 2; !_imageNames.Add(file); i++) file = $"{clean}_{i}.png";
            return file;
        }
    }
}
