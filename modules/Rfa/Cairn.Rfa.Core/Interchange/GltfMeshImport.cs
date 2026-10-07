using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using Cairn.Rfa.Animation;
using Cairn.Formats;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Interchange;

/// <summary>How imported texture names get their extension.</summary>
public enum TextureNamePolicy
{
    /// <summary>Keep the name; add ".tga" only when it has no extension.</summary>
    Keep,
    /// <summary>Replace .png/.jpg/.jpeg/.dds/.bmp/.gif with ".tga" (the engine still finds a .png/.dds/.jpg sibling first); .tga/.vbm and other RF names stay.</summary>
    ForceTga,
}

/// <summary>Options for <see cref="GltfMeshImport"/>.</summary>
public sealed record GltfMeshImportOptions
{
    /// <summary>Character or static; null: character when the glTF has a skin (or <see cref="KeepSkeletonFrom"/> is a character).</summary>
    public V3dKind? Kind { get; init; }

    /// <summary>Replace geometry only: bones, collision spheres and prop points come from this mesh; joints map to its bones by name.</summary>
    public V3dFile? KeepSkeletonFrom { get; init; }

    /// <summary>Uniform scale applied to positions, bone positions, spheres and prop points.</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>Texture name extension policy.</summary>
    public TextureNamePolicy TextureNames { get; init; } = TextureNamePolicy.ForceTga;

    /// <summary>LOD distances for LODs whose node carries no <c>rf_lod_distance</c>, by LOD index.</summary>
    public IReadOnlyList<float> DefaultLodDistances { get; init; } = [0f, 10f, 50f];
}

/// <summary>Severity of a pre-flight finding.</summary>
public enum MeshImportSeverity
{
    /// <summary>Something was converted or assumed; check it.</summary>
    Info,
    /// <summary>The result loads but not entirely as authored.</summary>
    Warning,
    /// <summary>The result breaks an engine limit, or cannot be built at all.</summary>
    Error,
}

/// <summary>One pre-flight finding.</summary>
/// <param name="Severity">How bad.</param>
/// <param name="Code">Stable code (MI001 ...).</param>
/// <param name="Message">What and how to fix it.</param>
public sealed record MeshImportIssue(MeshImportSeverity Severity, string Code, string Message);

/// <summary>What <see cref="GltfMeshImport"/> produced.</summary>
/// <param name="Mesh">The mesh, or null when it could not be built (see the Error issues).</param>
/// <param name="Description">The intermediate description (null when not even that could be made).</param>
/// <param name="Issues">The pre-flight list: every engine limit the result breaks, and every conversion made.</param>
public sealed record GltfMeshImportResult(V3dFile? Mesh, V3dMeshDescription? Description, ImmutableArray<MeshImportIssue> Issues)
{
    /// <summary>True when any issue is an error.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == MeshImportSeverity.Error);
}

/// <summary>
/// glTF (REDUX-style or a plain Blender export) -> <see cref="V3dMeshDescription"/> -> <see cref="V3dFile"/>
/// through <see cref="V3dBuilder"/>, with a pre-flight report. REDUX extras restore submesh/LOD
/// structure, material slots and properties, LOD flags, distances and texture lists, render flags and
/// double-sided triangles; without them LODs come from <c>_LOD{n}</c> name suffixes and materials
/// from the glTF materials (texture name: <c>rf_texture</c>, else the image name/URI, else the material
/// name). Space conversion as <see cref="GltfSpace"/>. Weights: four largest influences normalised
/// to bytes summing to 255, except that normalised unsigned-byte weights (what <see cref="GltfExport"/>
/// writes) keep their bytes when they are already a valid stock-style set.
/// </summary>
public static class GltfMeshImport
{
    private static readonly string[] ReplacedExtensions = [".png", ".jpg", ".jpeg", ".dds", ".bmp", ".gif", ".webp"];

    /// <summary>Imports the document's meshes as one V3D mesh.</summary>
    /// <exception cref="AssetFormatException">An accessor the meshes use is damaged.</exception>
    public static GltfMeshImportResult Import(GltfDocument doc, GltfMeshImportOptions? options = null, string fileName = "glTF")
    {
        ArgumentNullException.ThrowIfNull(doc);
        options ??= new GltfMeshImportOptions();
        if (!(options.Scale > 0f) || !float.IsFinite(options.Scale)) throw new ArgumentException("Scale must be a positive number.", nameof(options));
        return new Importer(doc, options, fileName).Run();
    }

    /// <summary>A texture name after REDUX's normalisation and the extension policy.</summary>
    public static string NormalizeTextureName(string? texture, TextureNamePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(texture)) return "default.tga";
        string name = texture.Replace('\\', '/');
        int cut = name.IndexOfAny(['?', '#']);
        if (cut >= 0) name = name[..cut];
        name = Uri.UnescapeDataString(Path.GetFileName(name));
        if (string.IsNullOrWhiteSpace(name)) return "default.tga";
        string ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) return name + ".tga";
        if (policy == TextureNamePolicy.ForceTga && ReplacedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return name[..^ext.Length] + ".tga";
        return name;
    }

    private sealed class Block
    {
        public required V3dMeshVertex[] Vertices;
        public readonly HashSet<(int Sub, int Lod, int Slot)> Users = [];
    }

    private sealed class Brush
    {
        public required string SubmeshKey;
        public required string SubmeshName;
        public int Lod;
        public required int Node;
        public readonly List<(Block Block, int Slot, uint RenderFlags, List<V3dMeshTriangle> Triangles)> Parts = [];
    }

    private sealed class SubmeshInfo
    {
        public required string Name;
        public string? ParentName;
        public Vector3 Offset;
        public readonly List<string> Slots = [];
        public List<V3dMaterial>? Props;
        public readonly Dictionary<int, float> Distances = [];
        public readonly Dictionary<int, uint> Flags = [];
        public readonly Dictionary<int, List<V3dLodTexture>> LodTextures = [];
        public bool SlotsFromExtras;
    }

    private sealed class Importer(GltfDocument doc, GltfMeshImportOptions options, string fileName)
    {
        private readonly List<MeshImportIssue> _issues = [];
        private readonly int[] _parents = doc.ComputeParents();
        private Rigid[] _restWorld = [];
        private Matrix4x4[] _worldMatrices = [];
        private V3dKind _kind;
        private ImmutableArray<V3dBone> _bones = [];
        private int[] _jointToBone = [];          // skin joint index -> bone index (-1 none)
        private readonly Dictionary<int, int> _nodeToBone = [];
        private int _skin = -1;
        private int _offSum;
        private int _unweighted, _reduced, _outOfRange, _missingUv, _generatedNormals, _skippedPrims, _convertedPrims;

        private void Issue(MeshImportSeverity s, string code, string message) => _issues.Add(new MeshImportIssue(s, code, message));

        public GltfMeshImportResult Run()
        {
            _worldMatrices = doc.ComputeWorldMatrices();
            _restWorld = new Rigid[doc.Nodes.Count];
            for (int i = 0; i < doc.Nodes.Count; i++)
                _restWorld[i] = Matrix4x4.Decompose(_worldMatrices[i], out _, out var r, out var t) ? new Rigid(Quat.Normalize(r), t) : new Rigid(Quaternion.Identity, _worldMatrices[i].Translation);

            var meshNodes = Enumerable.Range(0, doc.Nodes.Count).Where(i => doc.Nodes[i].Mesh is { } m && m >= 0 && m < doc.Meshes.Count).ToList();
            _skin = meshNodes.Select(n => doc.Nodes[n].Skin ?? -1).FirstOrDefault(s => s >= 0 && s < doc.Skins.Count, -1);
            if (_skin < 0 && meshNodes.Count == 0 && doc.Skins.Count > 0) _skin = 0;
            _kind = options.Kind ?? (options.KeepSkeletonFrom?.Kind == V3dKind.Character || _skin >= 0 ? V3dKind.Character : V3dKind.StaticMesh);
            if (_kind == V3dKind.StaticMesh && _skin >= 0)
                Issue(MeshImportSeverity.Info, "MI016", "The glTF is skinned but a static mesh was asked for; the skin is ignored and vertices keep their bind pose.");

            if (!SetUpSkeleton()) return new GltfMeshImportResult(null, null, [.. _issues]);

            var submeshes = new Dictionary<string, SubmeshInfo>(StringComparer.Ordinal);
            var order = new List<string>();
            var brushes = new List<Brush>();
            var blocks = new Dictionary<(int Node, int Pos, int Nrm, int Uv, int J, int W), Block>();
            foreach (int n in meshNodes) ReadMeshNode(n, submeshes, order, brushes, blocks);
            if (brushes.Count == 0 || brushes.All(b => b.Parts.Count == 0))
            {
                Issue(MeshImportSeverity.Error, "MI013", "The glTF has no triangle geometry to import.");
                return new GltfMeshImportResult(null, null, [.. _issues]);
            }
            Summarise();

            bool fatal = false;
            var subs = ImmutableArray.CreateBuilder<V3dSubmeshDescription>();
            foreach (string key in order)
            {
                var info = submeshes[key];
                var mine = brushes.Where(b => b.SubmeshKey == key && b.Parts.Count > 0).ToList();
                if (mine.Count == 0) continue;
                var desc = BuildSubmesh(info, mine, ref fatal);
                if (desc is not null) subs.Add(desc);
            }

            var description = new V3dMeshDescription
            {
                Kind = _kind,
                Submeshes = subs.ToImmutable(),
                Bones = _kind == V3dKind.Character ? _bones : [],
                CollisionSpheres = _kind == V3dKind.Character ? Spheres(ref fatal) : [],
                PropPoints = Props(ref fatal),
            };
            Limits(description);
            if (fatal) return new GltfMeshImportResult(null, description, [.. _issues]);
            try
            {
                return new GltfMeshImportResult(V3dBuilder.Build(description, V3dBuildOptions.Preserve), description, [.. _issues]);
            }
            catch (ArgumentException ex)
            {
                Issue(MeshImportSeverity.Error, "MI099", $"The mesh could not be built: {ex.Message}");
                return new GltfMeshImportResult(null, description, [.. _issues]);
            }
        }

        // â”€â”€ Skeleton â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private bool SetUpSkeleton()
        {
            var keep = options.KeepSkeletonFrom;
            if (_kind != V3dKind.Character)
            {
                _bones = [];
                return true;
            }
            var src = GltfAnimationImport.SourceSkeleton(doc);
            var joints = _skin >= 0 ? doc.Skins[_skin].Joints : [];
            if (keep is not null)
            {
                _bones = keep.Bones;
                var sk = Skeleton.FromFile(keep);
                Issue(MeshImportSeverity.Info, "MI012", $"Geometry replaced; the {sk.Count} bones, collision spheres and prop points of the existing mesh are kept.");
                if (src.Nodes.Length > 0)
                {
                    // Each glTF joint finds its bone (target = joints, source = existing bones).
                    var map = BoneMapper.Map(sk.Names, sk.Parents, RigProfile.Generic(sk), src.Names, src.Parents, RigProfile.Generic(src.Names, src.Parents));
                    for (int j = 0; j < src.Nodes.Length; j++)
                    {
                        int b = map.SourceOf(j);
                        if (b >= 0) _nodeToBone[src.Nodes[j]] = b;
                        else Issue(MeshImportSeverity.Warning, "MI011", $"The glTF joint '{src.Names[j]}' matches no bone of the existing mesh; vertices weighted to it go to its nearest matched ancestor.");
                    }
                    // Unmatched joints fall back to their nearest matched ancestor.
                    foreach (int node in src.Nodes)
                    {
                        if (_nodeToBone.ContainsKey(node)) continue;
                        for (int k = _parents[node], guard = 0; k >= 0 && guard <= doc.Nodes.Count; k = _parents[k], guard++)
                        {
                            if (_nodeToBone.TryGetValue(k, out int b))
                            {
                                _nodeToBone[node] = b;
                                break;
                            }
                        }
                    }
                }
            }
            else
            {
                if (src.Nodes.Length == 0)
                {
                    if (options.Kind == V3dKind.Character)
                        Issue(MeshImportSeverity.Warning, "MI017", "A character was asked for but the glTF has no skeleton; the mesh gets no bones.");
                    _bones = [];
                    return true;
                }
                Matrix4x4[]? ibm = null;
                if (_skin >= 0 && doc.Skins[_skin].InverseBindMatrices is { } acc)
                {
                    ibm = GltfAccessorReader.ReadMatrices(doc, acc, fileName);
                    if (ibm.Length < joints.Count)
                    {
                        Issue(MeshImportSeverity.Warning, "MI018", $"The skin has {ibm.Length} inverse bind matrices for {joints.Count} joints; the bind pose comes from the node hierarchy instead.");
                        ibm = null;
                    }
                }
                var bones = ImmutableArray.CreateBuilder<V3dBone>(src.Nodes.Length);
                bool fatal = false;
                for (int i = 0; i < src.Nodes.Length; i++)
                {
                    int node = src.Nodes[i];
                    _nodeToBone[node] = i;
                    string name = src.Names[i];
                    if (!Fits(name, V3dBone.NameSize))
                    {
                        Issue(MeshImportSeverity.Error, "MI002", $"Bone name '{name}' is {name.Length} characters; at most {V3dBone.NameSize - 1} fit (or it has characters outside Latin-1). Rename it in the source.");
                        fatal = true;
                        name = "x";
                    }
                    int ji = joints.IndexOf(node);
                    Quaternion rot;
                    Vector3 pos;
                    if (ibm is not null && ji >= 0 && Matrix4x4.Decompose(ibm[ji], out _, out var q, out var t))
                    {
                        // IBM (glTF) = ToGltf(inverse bind) = ToGltf((conj(rot), storedPos)).
                        rot = Quat.Conj(GltfSpace.FromGltf(Quat.Normalize(q)));
                        pos = GltfSpace.FromGltf(t) * options.Scale;
                    }
                    else
                    {
                        var w = GltfSpace.FromGltf(_restWorld[node]);
                        var b = V3dBuilder.BoneFromRestWorld("x", -1, new Rigid(w.Rotation, w.Position * options.Scale));
                        rot = b.Rotation;
                        pos = b.Position;
                    }
                    bones.Add(new V3dBone(FixedString.FromText(name, V3dBone.NameSize), rot, pos, src.Parents[i]));
                }
                if (fatal) return false;
                _bones = bones.MoveToImmutable();
            }
            _jointToBone = [.. joints.Select(n => _nodeToBone.TryGetValue(n, out int b) ? b : -1)];
            return true;
        }

        // â”€â”€ Geometry â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void ReadMeshNode(int n, Dictionary<string, SubmeshInfo> submeshes, List<string> order, List<Brush> brushes,
            Dictionary<(int, int, int, int, int, int), Block> blocks)
        {
            var node = doc.Nodes[n];
            var gm = doc.Meshes[node.Mesh!.Value];
            var x = node.Extras;
            var firstPrimExtras = gm.Primitives.Count > 0 ? gm.Primitives[0].Extras : null;

            string rawName = GltfExtras.TryGetString(x, GltfExtras.BrushName, out var bn) && bn.Length > 0 ? bn : node.Name ?? gm.Name ?? $"mesh_{n}";
            int lodFromName = GltfSpace.LodIndexFromName(rawName, out string baseName);
            int lod = GltfExtras.TryGetInt(x, GltfExtras.LodIndex, out int li) && li >= 0 ? li
                : GltfExtras.TryGetInt(firstPrimExtras, GltfExtras.LodIndex, out li) && li >= 0 ? li
                : Math.Max(0, lodFromName);
            string key = GltfExtras.TryGetInt(x, GltfExtras.SubmeshIndex, out int si) && si >= 0 ? $"#{si}" : "name:" + baseName;
            if (!submeshes.TryGetValue(key, out var info))
            {
                info = new SubmeshInfo { Name = baseName };
                submeshes[key] = info;
                order.Add(key);
                if (GltfExtras.TryGetString(x, GltfExtras.SubmeshParent, out var parent)) info.ParentName = parent;
                if (GltfExtras.TryGetFloats(x, GltfExtras.SubmeshOffset, 3, out var off)) info.Offset = new Vector3(off[0], off[1], off[2]) * options.Scale;
                if (GltfExtras.Get(x, GltfExtras.MaterialSlots) is JsonArray slots)
                {
                    foreach (var s in slots) info.Slots.Add(NormalizeTextureName(s is JsonValue v && v.TryGetValue(out string? str) ? str : null, options.TextureNames));
                    info.SlotsFromExtras = info.Slots.Count > 0;
                }
                if (GltfExtras.Get(x, GltfExtras.MaterialProps) is JsonArray props) info.Props = [.. props.Select(MaterialFromProps)];
            }
            if (GltfExtras.TryGetNumber(x, GltfExtras.LodDistance, out double dist)) info.Distances[lod] = (float)dist;
            if (GltfExtras.TryGetNumber(x, GltfExtras.LodFlags, out double flags) && flags >= 0 && flags <= uint.MaxValue) info.Flags[lod] = (uint)flags;
            if (GltfExtras.Get(x, GltfExtras.LodTextures) is JsonArray lt)
            {
                var list = new List<V3dLodTexture>();
                foreach (var item in lt)
                {
                    if (GltfExtras.TryGetInt(item, "slot", out int slot) && slot is >= 0 and < 256 && GltfExtras.TryGetString(item, "name", out var tn) && tn.Length > 0)
                        list.Add(new V3dLodTexture((byte)slot, tn));
                }
                if (list.Count > 0) info.LodTextures[lod] = list;
            }

            var brush = new Brush { SubmeshKey = key, SubmeshName = baseName, Lod = lod, Node = n };
            brushes.Add(brush);
            bool skinned = node.Skin is not null && _kind == V3dKind.Character && _bones.Length > 0;
            // glTF ignores a skinned mesh node's own transform; a static mesh node's world is baked in.
            var world = skinned ? Matrix4x4.Identity : _worldMatrices[n];
            bool mirrored = !skinned && Determinant3(world) < 0;
            int fallbackBone = FallbackBone(n);

            for (int p = 0; p < gm.Primitives.Count; p++)
            {
                var prim = gm.Primitives[p];
                int mode = prim.Mode ?? 4;
                if (mode is < 4 or > 6)
                {
                    _skippedPrims++;
                    continue;
                }
                if (!prim.Attributes.TryGetValue("POSITION", out int posAcc)) continue;
                var material = prim.Material is { } mi && mi >= 0 && mi < doc.Materials.Count ? doc.Materials[mi] : null;
                int uvSet = material?.PbrMetallicRoughness?.BaseColorTexture?.TexCoord ?? 0;
                int uvAcc = prim.Attributes.TryGetValue($"TEXCOORD_{uvSet}", out int ua) ? ua : prim.Attributes.TryGetValue("TEXCOORD_0", out ua) ? ua : -1;
                int nrmAcc = prim.Attributes.TryGetValue("NORMAL", out int na) ? na : -1;
                int jAcc = prim.Attributes.TryGetValue("JOINTS_0", out int ja) ? ja : -1;
                int wAcc = prim.Attributes.TryGetValue("WEIGHTS_0", out int wa) ? wa : -1;

                if ((uint)posAcc >= (uint)doc.Accessors.Count)
                    throw GltfAccessorReader.Damaged(fileName, $"mesh {node.Mesh} uses POSITION accessor {posAcc}, which does not exist (the file has {doc.Accessors.Count}).");
                int count = doc.Accessors[posAcc].Count;
                var indices = prim.Indices is { } ia ? GltfAccessorReader.ReadInts(doc, ia, fileName) : [.. Enumerable.Range(0, count)];
                var tris = Triangulate(indices, mode, count);
                if (mode != 4) _convertedPrims++;
                if (tris.Count == 0) continue;

                var blockKey = (n, posAcc, nrmAcc, uvAcc, jAcc, wAcc);
                if (!blocks.TryGetValue(blockKey, out var block))
                {
                    block = new Block { Vertices = ReadVertices(prim, posAcc, nrmAcc, uvAcc, tris, world, skinned, fallbackBone) };
                    blocks[blockKey] = block;
                }

                string texture = TextureName(material, prim.Material);
                int slot = Slot(info, prim, material, texture);
                bool twoSided = GltfExtras.TryGetBool(prim.Extras, GltfExtras.DoubleSided, out bool ds) ? ds : material?.DoubleSided == true;
                uint renderFlags = GltfExtras.TryGetNumber(prim.Extras, GltfExtras.RenderFlags, out double rf) && rf > 0 ? (uint)rf
                    : GltfExtras.TryGetNumber(material?.Extras, GltfExtras.RenderFlags, out rf) && rf > 0 ? (uint)rf : V3dBuilder.DefaultRenderFlags;
                ushort triFlags = twoSided ? V3dTriangle.DoubleSided : (ushort)0;
                var list = new List<V3dMeshTriangle>(tris.Count / 3);
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    // Reverse the winding back to RF's left-handed space (a mirrored node world already did).
                    list.Add(mirrored ? new V3dMeshTriangle(tris[t], tris[t + 1], tris[t + 2], triFlags) : new V3dMeshTriangle(tris[t], tris[t + 2], tris[t + 1], triFlags));
                }
                block.Users.Add((order.IndexOf(key), lod, slot));
                brush.Parts.Add((block, slot, renderFlags, list));
            }
        }

        private List<int> Triangulate(int[] indices, int mode, int vertexCount)
        {
            var result = new List<int>(indices.Length);
            bool Valid(int a, int b, int c) => (uint)a < (uint)vertexCount && (uint)b < (uint)vertexCount && (uint)c < (uint)vertexCount && a != b && b != c && a != c;
            switch (mode)
            {
                case 4:
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                        if (Valid(indices[i], indices[i + 1], indices[i + 2])) result.AddRange([indices[i], indices[i + 1], indices[i + 2]]);
                    break;
                case 5:
                    for (int i = 0; i + 2 < indices.Length; i++)
                    {
                        int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                        if (i % 2 == 1) (a, b) = (b, a);
                        if (Valid(a, b, c)) result.AddRange([a, b, c]);
                    }
                    break;
                case 6:
                    for (int i = 1; i + 1 < indices.Length; i++)
                        if (Valid(indices[0], indices[i], indices[i + 1])) result.AddRange([indices[0], indices[i], indices[i + 1]]);
                    break;
            }
            return result;
        }

        private V3dMeshVertex[] ReadVertices(GltfPrimitive prim, int posAcc, int nrmAcc, int uvAcc, List<int> tris, Matrix4x4 world, bool skinned, int fallbackBone)
        {
            var pos = GltfAccessorReader.ReadVector3(doc, posAcc, fileName);
            int n = pos.Length;
            var nrm = nrmAcc >= 0 ? GltfAccessorReader.ReadVector3(doc, nrmAcc, fileName) : null;
            if (nrm is not null && nrm.Length < n) nrm = null;
            var uv = uvAcc >= 0 ? GltfAccessorReader.ReadVector2(doc, uvAcc, fileName) : null;
            if (uv is null || uv.Length < n)
            {
                uv = null;
                _missingUv += n;
            }
            bool applyWorld = !world.IsIdentity;
            Matrix4x4 normalMatrix = Matrix4x4.Identity;
            if (applyWorld)
            {
                var linear = world;
                linear.Translation = Vector3.Zero;
                normalMatrix = Matrix4x4.Invert(linear, out var inv) ? Matrix4x4.Transpose(inv) : linear;
            }
            var glPos = new Vector3[n];
            for (int i = 0; i < n; i++) glPos[i] = applyWorld ? Vector3.Transform(pos[i], world) : pos[i];
            Vector3[] glNrm;
            if (nrm is null)
            {
                _generatedNormals += n;
                glNrm = new Vector3[n];
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    var c = Vector3.Cross(glPos[tris[t + 1]] - glPos[tris[t]], glPos[tris[t + 2]] - glPos[tris[t]]);
                    glNrm[tris[t]] += c;
                    glNrm[tris[t + 1]] += c;
                    glNrm[tris[t + 2]] += c;
                }
                for (int i = 0; i < n; i++) glNrm[i] = glNrm[i].LengthSquared() > 0 ? Vector3.Normalize(glNrm[i]) : Vector3.UnitY;
            }
            else
            {
                glNrm = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    var v = applyWorld ? Vector3.TransformNormal(nrm[i], normalMatrix) : nrm[i];
                    float len = v.Length();
                    glNrm[i] = len > 1e-12f && float.IsFinite(len) ? v / len : Vector3.UnitY;
                }
            }

            var links = skinned ? ReadLinks(prim, n, fallbackBone) : null;
            var result = new V3dMeshVertex[n];
            for (int i = 0; i < n; i++)
            {
                result[i] = new V3dMeshVertex(
                    GltfSpace.FromGltf(glPos[i]) * options.Scale,
                    GltfSpace.FromGltf(glNrm[i]),
                    uv is null ? Vector2.Zero : uv[i],
                    links is null ? [] : V3dBuilder.Influences(links[i]));
            }
            return result;
        }

        private V3dBoneLink[] ReadLinks(GltfPrimitive prim, int n, int fallbackBone)
        {
            var sets = new List<(int[] J, float[] W, int[]? RawBytes)>();
            for (int set = 0; prim.Attributes.TryGetValue($"JOINTS_{set}", out int ja) && prim.Attributes.TryGetValue($"WEIGHTS_{set}", out int wa); set++)
            {
                GltfAccessorReader.Expect(doc, ja, fileName, GltfAccessorType.Vec4);
                GltfAccessorReader.Expect(doc, wa, fileName, GltfAccessorType.Vec4);
                var j = GltfAccessorReader.ReadInts(doc, ja, fileName);
                var w = GltfAccessorReader.ReadFloats(doc, wa, fileName);
                var acc = doc.Accessors[wa];
                int[]? raw = acc.ComponentType == GltfComponentType.UnsignedByte && acc.Normalized ? [.. w.Select(f => (int)MathF.Round(f * 255f))] : null;
                if (j.Length < n * 4 || w.Length < n * 4) continue;
                sets.Add((j, w, raw));
            }
            var links = new V3dBoneLink[n];
            var infl = new List<V3dBoneInfluence>(8);
            for (int v = 0; v < n; v++)
            {
                infl.Clear();
                foreach (var (J, W, _) in sets)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        float w = W[v * 4 + k];
                        if (!(w > 0f)) continue;
                        int joint = J[v * 4 + k];
                        int bone = joint >= 0 && joint < _jointToBone.Length ? _jointToBone[joint] : -1;
                        if (bone < 0)
                        {
                            _outOfRange++;
                            continue;
                        }
                        infl.Add(new V3dBoneInfluence(bone, w));
                    }
                }
                int distinct = infl.Select(i => i.Bone).Distinct().Count();
                if (distinct == 0)
                {
                    _unweighted++;
                    links[v] = new V3dBoneLink(255, 0, 0, 0, (byte)Math.Clamp(fallbackBone, 0, 254), V3dBoneLink.NoBone, V3dBoneLink.NoBone, V3dBoneLink.NoBone);
                    continue;
                }
                if (distinct > 4) _reduced++;
                // Unsigned-byte weights on at most 4 distinct bones ARE the stored bytes (what GltfExport
                // writes): keep them exactly, even when they do not sum to 255 (some stock vertices don't).
                if (sets.Count == 1 && sets[0].RawBytes is { } raw && distinct == infl.Count)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++) sum += raw[v * 4 + k];
                    if (sum > 0)
                    {
                        if (Math.Abs(sum - 255) > 3) _offSum++;
                        var kept = new List<V3dBoneInfluence>(4);
                        for (int k = 0; k < 4; k++)
                        {
                            int b = raw[v * 4 + k];
                            int joint = sets[0].J[v * 4 + k];
                            if (b > 0 && joint >= 0 && joint < _jointToBone.Length && _jointToBone[joint] >= 0) kept.Add(new V3dBoneInfluence(_jointToBone[joint], b / 255f));
                        }
                        links[v] = V3dBuilder.PackLink(kept, V3dWeightMode.Preserve);
                        continue;
                    }
                }
                links[v] = V3dBuilder.PackLink(infl, V3dWeightMode.Normalize);
            }
            return links;
        }

        private int FallbackBone(int meshNode)
        {
            for (int k = _parents[meshNode], guard = 0; k >= 0 && guard <= doc.Nodes.Count; k = _parents[k], guard++)
            {
                if (_nodeToBone.TryGetValue(k, out int b)) return b;
            }
            if (_bones.Length == 0) return 0;
            var sk = Skeleton.FromBones(_bones);
            return sk.EvaluationOrder.Length > 0 ? sk.EvaluationOrder[0] : 0;
        }

        private string TextureName(GltfMaterial? material, int? materialIndex)
        {
            if (material is null) return "default.tga";
            if (GltfExtras.TryGetString(material.Extras, GltfExtras.Texture, out var t) && t.Length > 0) return NormalizeTextureName(t, options.TextureNames);
            if (material.PbrMetallicRoughness?.BaseColorTexture is { } info && info.Index >= 0 && info.Index < doc.Textures.Count
                && doc.Textures[info.Index].Source is { } src && src >= 0 && src < doc.Images.Count)
            {
                var img = doc.Images[src];
                if (!string.IsNullOrWhiteSpace(img.Uri) && !img.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return NormalizeTextureName(img.Uri, options.TextureNames);
                if (!string.IsNullOrWhiteSpace(img.Name)) return NormalizeTextureName(img.Name, options.TextureNames);
            }
            if (!string.IsNullOrWhiteSpace(material.Name)) return NormalizeTextureName(material.Name, options.TextureNames);
            return $"material_{materialIndex}.tga";
        }

        private int Slot(SubmeshInfo info, GltfPrimitive prim, GltfMaterial? material, string texture)
        {
            if (info.SlotsFromExtras)
            {
                int hint = GltfExtras.TryGetInt(prim.Extras, GltfExtras.TextureSlot, out int ps) ? ps
                    : GltfExtras.TryGetInt(material?.Extras, GltfExtras.TextureSlot, out int ms) ? ms : -1;
                if (hint >= 0 && hint < 256)
                {
                    while (info.Slots.Count <= hint) info.Slots.Add("default.tga");
                    return hint;
                }
            }
            int existing = info.Slots.FindIndex(s => string.Equals(s, texture, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) return existing;
            info.Slots.Add(texture);
            return info.Slots.Count - 1;
        }

        private static V3dMaterial MaterialFromProps(JsonNode? p)
        {
            float F(string k) => GltfExtras.TryGetNumber(p, k, out double d) ? (float)d : 0f;
            string refl = GltfExtras.TryGetString(p, "refl_map", out var r) ? r : string.Empty;
            uint flags = GltfExtras.TryGetNumber(p, "flags", out double fl) && fl >= 0 && fl <= uint.MaxValue ? (uint)fl : 1u;
            return new V3dMaterial(FixedString.FromText("", V3dMaterial.NameSize), F("emissive"), F("specular"), F("glossiness"), F("reflection"),
                Fits(refl, V3dMaterial.NameSize) ? FixedString.FromText(refl, V3dMaterial.NameSize) : FixedString.FromText("", V3dMaterial.NameSize), flags);
        }

        private V3dSubmeshDescription? BuildSubmesh(SubmeshInfo info, List<Brush> brushes, ref bool fatal)
        {
            string name = info.Name;
            if (!Fits(name, V3dSubmesh.NameSize))
            {
                Issue(MeshImportSeverity.Error, "MI002", $"Submesh name '{name}' is {name.Length} characters; at most {V3dSubmesh.NameSize - 1} fit. Rename the object.");
                fatal = true;
                return null;
            }
            var materials = ImmutableArray.CreateBuilder<V3dMaterial>(info.Slots.Count);
            for (int i = 0; i < info.Slots.Count; i++)
            {
                string tex = info.Slots[i];
                var baseMat = info.Props is not null && i < info.Props.Count ? info.Props[i]
                    : new V3dMaterial(FixedString.FromText("", 32), 0f, 0f, 0f, 0f, FixedString.FromText("", 32), 1u);
                if (!Fits(tex, V3dMaterial.NameSize))
                {
                    Issue(MeshImportSeverity.Error, "MI002", $"Texture name '{tex}' is {tex.Length} characters; at most {V3dMaterial.NameSize - 1} fit. Rename the texture.");
                    fatal = true;
                    tex = "x";
                }
                materials.Add(baseMat with { DiffuseMap = FixedString.FromText(tex, V3dMaterial.NameSize) });
            }

            var lodIndices = brushes.Select(b => b.Lod).Distinct().Order().ToList();
            if (lodIndices.Count > 0 && (lodIndices[0] != 0 || lodIndices[^1] != lodIndices.Count - 1))
                Issue(MeshImportSeverity.Warning, "MI014", $"Submesh '{name}' has LODs {string.Join(", ", lodIndices)}; they are renumbered 0..{lodIndices.Count - 1}.");

            var lods = ImmutableArray.CreateBuilder<V3dLodDescription>();
            for (int l = 0; l < lodIndices.Count; l++)
            {
                int src = lodIndices[l];
                var parts = brushes.Where(b => b.Lod == src).SelectMany(b => b.Parts).ToList();
                var groups = new List<V3dMaterialGroup>();
                foreach (var bySlot in parts.GroupBy(p => (p.Slot, p.RenderFlags)).OrderBy(g => g.Key.Slot).ThenBy(g => g.Key.RenderFlags))
                {
                    var verts = new List<V3dMeshVertex>();
                    var tris = new List<V3dMeshTriangle>();
                    foreach (var byBlock in bySlot.GroupBy(p => p.Block))
                    {
                        var block = byBlock.Key;
                        var all = byBlock.SelectMany(p => p.Triangles).ToList();
                        // A block only this group uses keeps every vertex in order (exact round trip of an RFA
                        // Workbench export); a block shared between groups (REDUX style) keeps the referenced ones.
                        bool exclusive = block.Users.Count == 1;
                        var remap = new Dictionary<int, int>();
                        if (exclusive)
                        {
                            for (int i = 0; i < block.Vertices.Length; i++) remap[i] = verts.Count + i;
                            verts.AddRange(block.Vertices);
                        }
                        else
                        {
                            foreach (int i in all.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Order())
                            {
                                remap[i] = verts.Count;
                                verts.Add(block.Vertices[i]);
                            }
                        }
                        foreach (var t in all) tris.Add(new V3dMeshTriangle(remap[t.A], remap[t.B], remap[t.C], t.Flags));
                    }
                    if (tris.Count == 0) continue;
                    groups.Add(new V3dMaterialGroup { Material = bySlot.Key.Slot, RenderFlags = bySlot.Key.RenderFlags, Vertices = [.. verts], Triangles = [.. tris] });
                    if (verts.Count > V3dBuilder.MaxBatchVertices || tris.Count > V3dBuilder.MaxBatchTriangles)
                        Issue(MeshImportSeverity.Info, "MI005", $"Submesh '{name}' LOD {l}: material '{info.Slots[bySlot.Key.Slot]}' has {verts.Count} vertices / {tris.Count} triangles; it is split into several batches (at most {V3dBuilder.MaxBatchVertices} vertices and {V3dBuilder.MaxBatchTriangles} triangles each).");
                }

                ImmutableArray<V3dLodTexture>? textures = null;
                if (info.LodTextures.TryGetValue(src, out var lt) && groups.All(g => lt.Any(t => t.MaterialIndex == g.Material)) && lt.All(t => t.MaterialIndex < materials.Count))
                    textures = [.. lt];
                float distance = info.Distances.TryGetValue(src, out float d) ? d : l < options.DefaultLodDistances.Count ? options.DefaultLodDistances[l] : 10f * l;
                lods.Add(new V3dLodDescription
                {
                    Distance = distance,
                    Flags = info.Flags.TryGetValue(src, out uint f) ? f : null,
                    Groups = [.. groups],
                    Textures = textures,
                });
            }

            string parentName = info.ParentName is { } pn && Fits(pn, V3dSubmesh.NameSize) ? pn : "None";
            var desc = new V3dSubmeshDescription
            {
                Name = FixedString.FromText(name, V3dSubmesh.NameSize),
                ParentName = FixedString.FromText(parentName, V3dSubmesh.NameSize),
                Offset = info.Offset,
                Materials = materials.ToImmutable(),
                Lods = lods.ToImmutable(),
            };
            // Vertices are in model space in the glTF (REDUX adds the offset on export); take it off again.
            return info.Offset != Vector3.Zero ? SubtractOffset(desc, info.Offset) : desc;
        }

        private static V3dSubmeshDescription SubtractOffset(V3dSubmeshDescription d, Vector3 offset) => d with
        {
            Lods = [.. d.Lods.Select(l => l with
            {
                Groups = [.. l.Groups.Select(g => g with { Vertices = [.. g.Vertices.Select(v => v with { Position = v.Position - offset })] })],
            })],
        };

        // â”€â”€ Spheres and props â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private ImmutableArray<V3dCollisionSphere> Spheres(ref bool fatal)
        {
            if (options.KeepSkeletonFrom is { } keep) return [.. keep.CollisionSpheres];
            var result = ImmutableArray.CreateBuilder<V3dCollisionSphere>();
            for (int i = 0; i < doc.Nodes.Count; i++)
            {
                var node = doc.Nodes[i];
                if (!Is(node, GltfSpace.CollisionSpherePrefix, "collision_sphere")) continue;
                string name = GltfExtras.TryGetString(node.Extras, GltfExtras.Name, out var n) && n.Length > 0 ? n
                    : node.Name is { } nn && nn.StartsWith(GltfSpace.CollisionSpherePrefix, StringComparison.OrdinalIgnoreCase) ? nn[GltfSpace.CollisionSpherePrefix.Length..] : "csphere";
                if (!Fits(name, V3dCollisionSphere.NameSize))
                {
                    Issue(MeshImportSeverity.Error, "MI002", $"Collision sphere name '{name}' is {name.Length} characters; at most {V3dCollisionSphere.NameSize - 1} fit.");
                    fatal = true;
                    continue;
                }
                var (bone, local, scale) = RelativeToBone(i);
                float radius = GltfExtras.TryGetNumber(node.Extras, GltfExtras.Radius, out double r) && r > 0 ? (float)r * options.Scale
                    : MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z))) * options.Scale;
                if (!(radius > 0f)) radius = 0.01f;
                result.Add(new V3dCollisionSphere(FixedString.FromText(name, V3dCollisionSphere.NameSize), bone, GltfSpace.FromGltf(local.Position) * options.Scale, radius, []));
            }
            return result.ToImmutable();
        }

        private ImmutableArray<V3dPropPoint> Props(ref bool fatal)
        {
            if (options.KeepSkeletonFrom is { } keep)
                return keep.Submeshes.FirstOrDefault()?.Lods.FirstOrDefault()?.PropPoints ?? [];
            var result = ImmutableArray.CreateBuilder<V3dPropPoint>();
            for (int i = 0; i < doc.Nodes.Count; i++)
            {
                var node = doc.Nodes[i];
                if (!Is(node, GltfSpace.PropPointPrefix, "prop_point")) continue;
                string name = GltfExtras.TryGetString(node.Extras, GltfExtras.Name, out var n) && n.Length > 0 ? n
                    : node.Name is { } nn && nn.StartsWith(GltfSpace.PropPointPrefix, StringComparison.OrdinalIgnoreCase) ? nn[GltfSpace.PropPointPrefix.Length..] : "prop";
                if (!Fits(name, V3dPropPoint.NameSize))
                {
                    Issue(MeshImportSeverity.Error, "MI002", $"Prop point name '{name}' is {name.Length} characters; at most {V3dPropPoint.NameSize - 1} fit.");
                    fatal = true;
                    continue;
                }
                var (bone, local, _) = RelativeToBone(i);
                var gl = Quat.Normalize(local.Rotation);
                var rot = Quat.Normalize(new Quaternion(-gl.X, gl.Y, gl.Z, gl.W));
                if (GltfExtras.TryGetFloats(node.Extras, GltfExtras.Orientation, 4, out var o))
                {
                    var authored = new Quaternion(o[0], o[1], o[2], o[3]);
                    if (authored.LengthSquared() > 1e-12f && MathF.Abs(Quat.Dot(Quat.Normalize(authored), rot)) >= 0.9999f) rot = authored;
                }
                result.Add(new V3dPropPoint(FixedString.FromText(name, V3dPropPoint.NameSize), rot, GltfSpace.FromGltf(local.Position) * options.Scale, bone));
            }
            return result.ToImmutable();
        }

        private static bool Is(GltfNode node, string prefix, string type) =>
            (node.Name?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false)
            || (GltfExtras.TryGetString(node.Extras, GltfExtras.Type, out var t) && string.Equals(t, type, StringComparison.OrdinalIgnoreCase));

        /// <summary>A helper node's parent bone and its transform relative to that bone (glTF space), plus its scale.</summary>
        private (int Bone, Rigid Local, Vector3 Scale) RelativeToBone(int nodeIndex)
        {
            var node = doc.Nodes[nodeIndex];
            int bone = -1, boneNode = -1;
            for (int k = _parents[nodeIndex], guard = 0; k >= 0 && guard <= doc.Nodes.Count; k = _parents[k], guard++)
            {
                if (_nodeToBone.TryGetValue(k, out int b))
                {
                    bone = b;
                    boneNode = k;
                    break;
                }
            }
            if (bone < 0 && GltfExtras.TryGetInt(node.Extras, GltfExtras.ParentBone, out int pb) && pb >= 0 && pb < _bones.Length) bone = pb;
            Matrix4x4.Decompose(node.LocalMatrix(), out var scale, out _, out _);
            Rigid local;
            if (boneNode >= 0 && _parents[nodeIndex] == boneNode) local = GltfAnimationImport.RestLocal(node);
            else if (boneNode >= 0) local = _restWorld[boneNode].Inverse().Compose(_restWorld[nodeIndex]);
            else local = _restWorld[nodeIndex];
            return (bone, local, scale);
        }

        // â”€â”€ Report â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void Summarise()
        {
            if (_unweighted > 0) Issue(MeshImportSeverity.Warning, "MI006", $"{_unweighted} vertices have no bone weights; they are bound fully to the mesh node's bone (or the root).");
            if (_reduced > 0) Issue(MeshImportSeverity.Warning, "MI007", $"{_reduced} vertices are weighted to more than 4 bones; the 4 strongest are kept and renormalised.");
            if (_offSum > 0) Issue(MeshImportSeverity.Info, "MI019", $"{_offSum} vertices carry unsigned-byte weights that do not sum to 255 (within 3); they are kept as stored (a stock exporter quirk; lint rule V3C020 flags them).");
            if (_outOfRange > 0) Issue(MeshImportSeverity.Warning, "MI015", $"{_outOfRange} weights refer to joints that are not bones; they are dropped.");
            if (_skippedPrims > 0) Issue(MeshImportSeverity.Warning, "MI008", $"{_skippedPrims} primitives are points or lines; they are skipped (only triangles are imported).");
            if (_convertedPrims > 0) Issue(MeshImportSeverity.Info, "MI008", $"{_convertedPrims} triangle strips/fans were converted to triangle lists.");
            if (_generatedNormals > 0) Issue(MeshImportSeverity.Info, "MI009", $"{_generatedNormals} vertices had no normals; smooth normals were generated.");
            if (_missingUv > 0) Issue(MeshImportSeverity.Warning, "MI010", $"{_missingUv} vertices have no texture coordinates; they get (0, 0).");
        }

        private void Limits(V3dMeshDescription d)
        {
            if (d.Bones.Length > V3dBoneSection.MaxBones)
                Issue(MeshImportSeverity.Error, "MI001", $"The skeleton has {d.Bones.Length} bones; the engine poses at most {V3dBoneSection.MaxBones}.");
            if (d.Bones.Length > 255)
                Issue(MeshImportSeverity.Error, "MI001", "Bone links store bone indices in one byte; more than 255 bones cannot be weighted.");
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
                if (s.Materials.Length > 255)
                    Issue(MeshImportSeverity.Error, "MI004", $"Submesh '{s.Name.Text}' has {s.Materials.Length} materials; LOD texture entries index them with one byte.");
            }
        }

        private static float Determinant3(Matrix4x4 m) =>
            m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

        private static bool Fits(string text, int field) => text.Length <= field - 1 && text.All(c => c is > '\0' and <= '\u00FF');
    }
}
