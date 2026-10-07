using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Imaging;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Interchange;

/// <summary>A clip to export, with the name its glTF animation gets.</summary>
/// <param name="Name">Animation name (usually the clip's file name without extension).</param>
/// <param name="Clip">The clip; its bones are matched to the mesh by index, as in the engine.</param>
public sealed record GltfExportClip(string Name, RfaClip Clip);

/// <summary>Options for <see cref="GltfExport"/>.</summary>
public sealed record GltfExportOptions
{
    /// <summary>The LOD indices to export from every submesh (null: all).</summary>
    public IReadOnlyCollection<int>? Lods { get; init; }

    /// <summary>Export geometry; false exports the skeleton (and spheres, props, clips) only.</summary>
    public bool IncludeMesh { get; init; } = true;

    /// <summary>Export collision spheres as <c>rf_csphere::</c> nodes.</summary>
    public bool IncludeCollisionSpheres { get; init; } = true;

    /// <summary>Export prop points as <c>rf_prop::</c> nodes.</summary>
    public bool IncludePropPoints { get; init; } = true;

    /// <summary>Resolves texture names to image files (TGA/VBM/DDS/PNG/JPEG, decoded and written as PNG). Null exports named materials without images.</summary>
    public AssetResolver? TextureResolver { get; init; }

    /// <summary>Ticks between baked samples inside eased rotation segments (160 = 30 fps).</summary>
    public int BakeStepTicks { get; init; } = RfaClip.TicksPerFrame;

    /// <summary>Write the RFA Workbench <c>rf_keys</c> sampler extras that make an unedited round trip exact.</summary>
    public bool WriteKeyExtras { get; init; } = true;

    /// <summary>The asset generator string.</summary>
    public string Generator { get; init; } = "RFA Workbench";
}

/// <summary>What <see cref="GltfExport"/> produced and anything the user should know.</summary>
/// <param name="Document">The glTF document (images carry their PNG bytes; write it with <see cref="GltfWriter"/>).</param>
/// <param name="Warnings">Plain-language notes (missing textures, sanitised normals, bone-count mismatches, omitted morph data...).</param>
/// <param name="MissingTextures">Texture names that could not be found or decoded (their materials have no image).</param>
/// <param name="MorphOmittedClips">Clips whose morph (vertex) animation was left out: glTF morph targets cannot carry it faithfully (see DESIGN.md section 9).</param>
/// <param name="BakedRotationTracks">Rotation tracks baked at <see cref="GltfExportOptions.BakeStepTicks"/> inside eased segments.</param>
public sealed record GltfExportResult(
    GltfDocument Document,
    ImmutableArray<string> Warnings,
    ImmutableArray<string> MissingTextures,
    ImmutableArray<string> MorphOmittedClips,
    int BakedRotationTracks);

/// <summary>
/// <see cref="V3dFile"/> + any number of clips -> glTF 2.0 in REDUX's conventions (see
/// <see cref="GltfSpace"/>): bones as joint nodes with a skin, each chosen LOD of each submesh as a
/// mesh node in model space (submesh offset applied), one primitive per batch and triangle side
/// (double-sided triangles get a double-sided material, as REDUX does), materials named after their
/// texture with the RF texture decoded to PNG, collision spheres and prop points, and every clip as
/// an animation.
/// </summary>
/// <remarks>
/// Animation export: rotation tracks with no ease keep their keys (LINEAR, the engine's slerp); a
/// track with eases keeps its keys and gains samples every <see cref="GltfExportOptions.BakeStepTicks"/>
/// inside each eased segment, sampled with the engine's sampler. Position tracks become CUBICSPLINE
/// with tangents <c>3 (control - key) / dt</c>, which IS the RF Bezier (exact, and what REDUX writes).
/// A track with no keys gets one key holding what the engine plays (identity / origin). Every sampler
/// carries the RFA keys it came from in <c>rf_keys</c> extras, and every channel the bone weight in
/// <c>rf_weight</c>; the animation carries the header in REDUX's keys plus <c>rf_version</c>,
/// <c>rf_total_rotation</c>, <c>rf_total_translation</c>. Morph data is never exported.
/// </remarks>
public static class GltfExport
{
    /// <summary>Builds the glTF document.</summary>
    /// <exception cref="ArgumentException">A LOD's batch refers to a texture or material that does not exist.</exception>
    public static GltfExportResult Export(V3dFile mesh, IReadOnlyList<GltfExportClip>? clips = null, GltfExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new GltfExportOptions();
        if (options.BakeStepTicks <= 0) throw new ArgumentException("BakeStepTicks must be positive.", nameof(options));
        var ctx = new Context(mesh, options);
        ctx.Run(clips ?? []);
        return new GltfExportResult(ctx.Doc, [.. ctx.Warnings], [.. ctx.Missing], [.. ctx.MorphOmitted], ctx.Baked);
    }

    /// <summary>Exports and writes <c>.gltf</c> (+ <c>.bin</c> + PNG images beside it) or <c>.glb</c>, chosen by the extension.</summary>
    public static GltfExportResult ExportToFile(string path, V3dFile mesh, IReadOnlyList<GltfExportClip>? clips = null, GltfExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var result = Export(mesh, clips, options);
        if (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) GltfWriter.WriteGlb(result.Document, path);
        else GltfWriter.WriteGltf(result.Document, path);
        return result;
    }

    private sealed class Context(V3dFile mesh, GltfExportOptions options)
    {
        public readonly GltfDocument Doc = new();
        public readonly List<string> Warnings = [];
        public readonly List<string> Missing = [];
        public readonly List<string> MorphOmitted = [];
        public int Baked;

        private readonly Skeleton _skeleton = Skeleton.FromFile(mesh);
        private GltfBufferBuilder _buffer = null!;
        private int[] _boneNodes = [];
        private readonly List<int> _sceneRoots = [];
        private readonly Dictionary<string, int?> _textureByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _textureHasAlpha = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imageUris = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(int Sub, int Mat, bool Two, uint Flags), int> _materials = [];
        private int _sampler = -1;

        private bool HasBones => _skeleton.Count > 0;

        public void Run(IReadOnlyList<GltfExportClip> clips)
        {
            Doc.Asset.Generator = options.Generator;
            _buffer = new GltfBufferBuilder(Doc);
            AddBones();
            if (options.IncludeMesh) AddMeshes();
            if (options.IncludePropPoints) AddPropPoints();
            if (options.IncludeCollisionSpheres) AddSpheres();
            foreach (var clip in clips) AddAnimation(clip);
            if (Doc.Accessors.Count > 0 || Doc.BufferViews.Count > 0) _buffer.Finish();
            var scene = new GltfScene();
            scene.Nodes.AddRange(_sceneRoots);
            Doc.Scenes.Add(scene);
            Doc.Scene = 0;
        }

        // ── Skeleton ────────────────────────────────────────────────────────

        private void AddBones()
        {
            int n = _skeleton.Count;
            _boneNodes = new int[n];
            if (n == 0) return;
            for (int i = 0; i < n; i++)
            {
                var local = GltfSpace.ToGltf(_skeleton.RestLocal[i]);
                var node = new GltfNode
                {
                    Name = GltfSpace.BoneNodeName(_skeleton.Names[i], i),
                    Translation = local.Position,
                    Rotation = Quat.Normalize(local.Rotation),
                };
                var x = GltfExtras.Ensure(node);
                x[GltfExtras.Type] = "bone";
                x[GltfExtras.BoneIndex] = i;
                _boneNodes[i] = Doc.Nodes.Count;
                Doc.Nodes.Add(node);
            }
            for (int i = 0; i < n; i++)
            {
                int p = _skeleton.EffectiveParents[i];
                if (p >= 0) Doc.Nodes[_boneNodes[p]].Children.Add(_boneNodes[i]);
                else _sceneRoots.Add(_boneNodes[i]);
                if (_skeleton.Parents[i] != p)
                    Warnings.Add($"Bone '{_skeleton.Names[i]}' has an invalid parent ({_skeleton.Parents[i]}); it is exported as a root.");
            }

            var ibm = new Matrix4x4[n];
            for (int i = 0; i < n; i++) ibm[i] = GltfSpace.ToGltf(_skeleton.InverseBind[i]).ToMatrix();
            var skin = new GltfSkin { InverseBindMatrices = _buffer.AddMatrices(ibm), Skeleton = _boneNodes[_skeleton.EvaluationOrder[0]] };
            skin.Joints.AddRange(_boneNodes);
            Doc.Skins.Add(skin);
        }

        // ── Geometry ────────────────────────────────────────────────────────

        private void AddMeshes()
        {
            int brushUid = 0;
            int sanitized = 0;
            var subs = mesh.Submeshes.ToList();
            for (int si = 0; si < subs.Count; si++)
            {
                var s = subs[si];
                string subName = s.Name.Text;
                for (int li = 0; li < s.Lods.Length; li++)
                {
                    if (options.Lods is not null && !options.Lods.Contains(li)) continue;
                    var lod = s.Lods[li];
                    string brushName = $"{subName}_LOD{li}";
                    var gm = new GltfMesh { Name = brushName };
                    foreach (var batch in lod.Batches)
                    {
                        if ((uint)batch.TextureIndex >= (uint)lod.Textures.Length)
                            throw new ArgumentException($"{brushName} has a batch with texture index {batch.TextureIndex}, outside its {lod.Textures.Length}-entry texture list.");
                        int slot = lod.Textures[batch.TextureIndex].MaterialIndex;
                        if (slot >= s.Materials.Length)
                            throw new ArgumentException($"{brushName} uses material {slot}, but the submesh has {s.Materials.Length}.");
                        sanitized += AddBatch(gm, s, si, slot, batch, brushUid, brushName);
                    }
                    if (gm.Primitives.Count == 0) continue;

                    int meshIndex = Doc.Meshes.Count;
                    Doc.Meshes.Add(gm);
                    var node = new GltfNode { Name = brushName, Mesh = meshIndex, Skin = HasBones ? 0 : null };
                    var x = GltfExtras.Ensure(node);
                    x[GltfExtras.Type] = "brush";
                    x[GltfExtras.BrushUid] = brushUid;
                    x[GltfExtras.BrushName] = brushName;
                    var slots = new JsonArray();
                    foreach (var m in s.Materials) slots.Add(NormalizeTextureName(m.DiffuseMap.Text));
                    x[GltfExtras.MaterialSlots] = slots;
                    x[GltfExtras.LodIndex] = li;
                    x[GltfExtras.LodDistance] = li < s.LodDistances.Length ? s.LodDistances[li] : 0f;
                    var props = new JsonArray();
                    foreach (var m in s.Materials)
                    {
                        props.Add(new JsonObject
                        {
                            ["emissive"] = m.Emissive,
                            ["specular"] = m.Unknown0,
                            ["glossiness"] = m.Unknown1,
                            ["reflection"] = m.ReflectionCoefficient,
                            ["refl_map"] = m.ReflectionMap.Text,
                            ["flags"] = m.Flags,
                        });
                    }
                    x[GltfExtras.MaterialProps] = props;
                    x[GltfExtras.SubmeshParent] = s.ParentName.Text;
                    x[GltfExtras.SubmeshIndex] = si;
                    x[GltfExtras.LodFlags] = lod.Flags;
                    x[GltfExtras.SubmeshOffset] = GltfExtras.Array(s.Offset.X, s.Offset.Y, s.Offset.Z);
                    if (lod.Textures.Length > 0)
                    {
                        var lt = new JsonArray();
                        foreach (var t in lod.Textures) lt.Add(new JsonObject { ["slot"] = (int)t.MaterialIndex, ["name"] = t.FileName });
                        x[GltfExtras.LodTextures] = lt;
                    }
                    _sceneRoots.Add(Doc.Nodes.Count);
                    Doc.Nodes.Add(node);
                    brushUid++;
                }
            }
            if (sanitized > 0)
                Warnings.Add($"{sanitized} vertex normals were not finite or zero length (some stock meshes store NaN normals); they are exported as (0, 1, 0).");
        }

        /// <summary>One batch: shared vertex accessors, one primitive per triangle side. Returns the number of sanitised normals.</summary>
        private int AddBatch(GltfMesh gm, V3dSubmesh s, int si, int slot, V3dBatch batch, int brushUid, string brushName)
        {
            int nv = batch.VertexCount;
            if (nv == 0 || batch.TriangleCount == 0) return 0;
            int bad = 0;
            var pos = new Vector3[nv];
            var nrm = new Vector3[nv];
            var uv = new Vector2[nv];
            for (int i = 0; i < nv; i++)
            {
                pos[i] = GltfSpace.ToGltf(batch.Positions[i] + s.Offset);
                var n = batch.Normals[i];
                float len = n.Length();
                if (!float.IsFinite(len) || len < 1e-12f)
                {
                    n = Vector3.UnitY;
                    bad++;
                }
                else
                {
                    n = GltfSpace.ToGltf(n / len);
                }
                nrm[i] = n;
                uv[i] = batch.TexCoords[i];
            }
            var attributes = new Dictionary<string, int>
            {
                ["POSITION"] = _buffer.AddVector3(pos, minMax: true),
                ["NORMAL"] = _buffer.AddVector3(nrm),
                ["TEXCOORD_0"] = _buffer.AddVector2(uv),
            };
            if (HasBones && mesh.Kind == V3dKind.Character && batch.BoneLinks.Length == nv)
            {
                var joints = new int[nv * 4];
                var weights = new int[nv * 4];
                for (int i = 0; i < nv; i++)
                {
                    var l = batch.BoneLinks[i];
                    for (int k = 0; k < 4; k++)
                    {
                        int bone = l.GetBone(k), w = l.GetWeight(k);
                        bool used = w > 0 && bone != V3dBoneLink.NoBone && bone < _skeleton.Count;
                        joints[i * 4 + k] = used ? bone : 0;
                        weights[i * 4 + k] = used ? w : 0;
                    }
                }
                // Weights as normalised unsigned bytes keep the stored bytes exactly (stock sums are 252..255).
                attributes["JOINTS_0"] = _buffer.AddIntegers(joints, GltfComponentType.UnsignedByte, GltfAccessorType.Vec4, target: 34962);
                attributes["WEIGHTS_0"] = _buffer.AddIntegers(weights, GltfComponentType.UnsignedByte, GltfAccessorType.Vec4, normalized: true, target: 34962);
            }

            foreach (bool twoSided in new[] { false, true })
            {
                var indices = new List<int>(batch.TriangleCount * 3);
                foreach (var t in batch.Triangles)
                {
                    if (((t.Flags & V3dTriangle.DoubleSided) != 0) != twoSided) continue;
                    // Reverse the winding for the mirrored (right-handed) space.
                    indices.Add(t.A);
                    indices.Add(t.C);
                    indices.Add(t.B);
                }
                if (indices.Count == 0) continue;
                var prim = new GltfPrimitive { Indices = _buffer.AddIndices([.. indices]), Material = Material(s, si, slot, twoSided, batch.RenderFlags, brushUid) };
                foreach (var kv in attributes) prim.Attributes[kv.Key] = kv.Value;
                var x = GltfExtras.Ensure(prim);
                x[GltfExtras.BrushUid] = brushUid;
                x[GltfExtras.BrushName] = brushName;
                x[GltfExtras.Texture] = NormalizeTextureName(s.Materials[slot].DiffuseMap.Text);
                x[GltfExtras.TextureSlot] = slot;
                x[GltfExtras.RenderFlags] = batch.RenderFlags;
                x[GltfExtras.DoubleSided] = twoSided;
                gm.Primitives.Add(prim);
            }
            return bad;
        }

        private int Material(V3dSubmesh s, int si, int slot, bool twoSided, uint renderFlags, int brushUid)
        {
            if (_materials.TryGetValue((si, slot, twoSided, renderFlags), out int existing)) return existing;
            string texture = NormalizeTextureName(s.Materials[slot].DiffuseMap.Text);
            int? textureIndex = Texture(texture);
            var pbr = new GltfPbrMetallicRoughness { BaseColorFactor = [1f, 1f, 1f, 1f], MetallicFactor = 0f, RoughnessFactor = 1f };
            if (textureIndex is { } ti) pbr.BaseColorTexture = new GltfTextureInfo { Index = ti };
            var mat = new GltfMaterial
            {
                // The material name is the texture name, so a tool that strips extras still finds the RF bitmap (REDUX does the same).
                Name = texture,
                PbrMetallicRoughness = pbr,
                DoubleSided = twoSided ? true : null,
            };
            if (textureIndex is not null && _textureHasAlpha.TryGetValue(texture, out bool alpha) && alpha)
            {
                mat.AlphaMode = "MASK";
                mat.AlphaCutoff = 0.5f;
            }
            var x = GltfExtras.Ensure(mat);
            x[GltfExtras.Texture] = texture;
            x[GltfExtras.BrushUid] = brushUid;
            x[GltfExtras.TextureSlot] = slot;
            x[GltfExtras.RenderFlags] = renderFlags;
            int index = Doc.Materials.Count;
            Doc.Materials.Add(mat);
            _materials[(si, slot, twoSided, renderFlags)] = index;
            return index;
        }

        private int? Texture(string name)
        {
            if (_textureByName.TryGetValue(name, out var cached)) return cached;
            int? result = null;
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
                        _textureHasAlpha[name] = alpha;
                        string uri = UniqueImageUri(Path.GetFileNameWithoutExtension(name));
                        var img = new GltfImage { Name = name, Uri = uri, MimeType = "image/png", Data = PngEncoder.Encode(image) };
                        Doc.Images.Add(img);
                        if (_sampler < 0)
                        {
                            _sampler = Doc.Samplers.Count;
                            Doc.Samplers.Add(new GltfSampler { MagFilter = 9729, MinFilter = 9729, WrapS = 10497, WrapT = 10497 });
                        }
                        result = Doc.Textures.Count;
                        Doc.Textures.Add(new GltfTexture { Sampler = _sampler, Source = Doc.Images.Count - 1 });
                    }
                    else
                    {
                        Missing.Add(name);
                        Warnings.Add($"Texture '{name}' was not found; its material is exported without an image.");
                    }
                }
                catch (Exception ex) when (ex is ImageDecodeException or IOException or UnauthorizedAccessException or Cairn.Formats.AssetFormatException)
                {
                    Missing.Add(name);
                    Warnings.Add($"Texture '{name}' could not be read ({ex.Message}); its material is exported without an image.");
                }
            }
            _textureByName[name] = result;
            return result;
        }

        private string UniqueImageUri(string stem)
        {
            var clean = new string(stem.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
            if (clean.Length == 0) clean = "texture";
            string uri = clean + ".png";
            for (int i = 2; !_imageUris.Add(uri); i++) uri = $"{clean}_{i}.png";
            return uri;
        }

        /// <summary>REDUX's texture-name normalisation: the file name only, ".tga" added when it has no extension.</summary>
        public static string NormalizeTextureName(string? texture)
        {
            if (string.IsNullOrWhiteSpace(texture)) return "default.tga";
            string name = Path.GetFileName(texture.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) return "default.tga";
            if (string.IsNullOrEmpty(Path.GetExtension(name))) name += ".tga";
            return name;
        }

        // ── Props and spheres ───────────────────────────────────────────────

        private void AddPropPoints()
        {
            // Prop points are model-level: every stock LOD of every submesh stores the same list. The
            // first non-empty list is taken verbatim and props other LODs add are appended (REDUX).
            var props = new List<V3dPropPoint>();
            bool haveBase = false;
            foreach (var lod in mesh.Submeshes.SelectMany(s => s.Lods))
            {
                if (lod.PropPoints.Length == 0) continue;
                foreach (var p in lod.PropPoints)
                {
                    if (haveBase && props.Any(q => q.Name.Text == p.Name.Text && q.ParentIndex == p.ParentIndex && q.Position == p.Position)) continue;
                    props.Add(p);
                }
                haveBase = true;
            }
            foreach (var p in props)
            {
                var q = p.Rotation.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(p.Rotation);
                var node = new GltfNode
                {
                    Name = GltfSpace.PropPointPrefix + (string.IsNullOrWhiteSpace(p.Name.Text) ? "unnamed" : p.Name.Text),
                    Translation = GltfSpace.ToGltf(p.Position),
                    // REDUX treats the stored quaternion like a file (conjugated) one: (-x, y, z, w).
                    Rotation = new Quaternion(-q.X, q.Y, q.Z, q.W),
                };
                var x = GltfExtras.Ensure(node);
                x[GltfExtras.Type] = "prop_point";
                x[GltfExtras.Name] = p.Name.Text;
                x[GltfExtras.ParentBone] = p.ParentIndex;
                x[GltfExtras.Orientation] = GltfExtras.Array(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W);
                Attach(node, p.ParentIndex);
            }
        }

        private void AddSpheres()
        {
            foreach (var c in mesh.CollisionSpheres)
            {
                float r = c.Radius <= 0f ? 0.01f : c.Radius;
                var node = new GltfNode
                {
                    Name = GltfSpace.CollisionSpherePrefix + (string.IsNullOrWhiteSpace(c.Name.Text) ? "unnamed" : c.Name.Text),
                    Translation = GltfSpace.ToGltf(c.Position),
                    Scale = new Vector3(r),
                };
                var x = GltfExtras.Ensure(node);
                x[GltfExtras.Type] = "collision_sphere";
                x[GltfExtras.Name] = c.Name.Text;
                x[GltfExtras.Radius] = c.Radius;
                x[GltfExtras.ParentBone] = c.BoneIndex;
                Attach(node, c.BoneIndex);
            }
        }

        private void Attach(GltfNode node, int bone)
        {
            int index = Doc.Nodes.Count;
            Doc.Nodes.Add(node);
            if (bone >= 0 && bone < _boneNodes.Length) Doc.Nodes[_boneNodes[bone]].Children.Add(index);
            else _sceneRoots.Add(index);
        }

        // ── Animation ───────────────────────────────────────────────────────

        private void AddAnimation(GltfExportClip item)
        {
            var clip = item.Clip;
            if (!HasBones)
            {
                Warnings.Add($"Clip '{item.Name}' was not exported: the mesh has no bones.");
                return;
            }
            if (clip.BoneCount != _skeleton.Count)
                Warnings.Add($"Clip '{item.Name}' has {clip.BoneCount} bones but the mesh has {_skeleton.Count}; the first {Math.Min(clip.BoneCount, _skeleton.Count)} are exported (the engine matches bones by index).");
            if (!clip.Morph.IsEmpty)
            {
                MorphOmitted.Add(item.Name);
                Warnings.Add($"Clip '{item.Name}' has morph (vertex) animation for {clip.Morph.VertexCount} vertices; it is not exported (glTF morph targets cannot carry it faithfully).");
            }

            var anim = new GltfAnimation { Name = item.Name };
            var x = GltfExtras.Ensure(anim);
            x[GltfExtras.StartTime] = clip.StartTime;
            x[GltfExtras.EndTime] = clip.EndTime;
            x[GltfExtras.RampIn] = clip.RampIn;
            x[GltfExtras.RampOut] = clip.RampOut;
            x[GltfExtras.PosReduction] = clip.PosReduction;
            x[GltfExtras.RotReduction] = clip.RotReduction;
            x[GltfExtras.Version] = clip.Version;
            x[GltfExtras.TotalRotation] = GltfExtras.Array(clip.TotalRotation.X, clip.TotalRotation.Y, clip.TotalRotation.Z, clip.TotalRotation.W);
            x[GltfExtras.TotalTranslation] = GltfExtras.Array(clip.TotalTranslation.X, clip.TotalTranslation.Y, clip.TotalTranslation.Z);
            if (!clip.Morph.IsEmpty) x[GltfExtras.MorphVertexCount] = clip.Morph.VertexCount;

            int n = Math.Min(clip.BoneCount, _skeleton.Count);
            for (int i = 0; i < n; i++)
            {
                var track = clip.Bones[i];
                AddChannel(anim, i, "rotation", RotationSampler(clip, track), track.Weight);
                AddChannel(anim, i, "translation", TranslationSampler(clip, track), track.Weight);
            }
            Doc.Animations.Add(anim);
        }

        private void AddChannel(GltfAnimation anim, int bone, string path, GltfAnimationSampler sampler, float weight)
        {
            anim.Samplers.Add(sampler);
            var channel = new GltfAnimationChannel { Sampler = anim.Samplers.Count - 1, Target = new GltfAnimationTarget { Node = _boneNodes[bone], Path = path } };
            GltfExtras.Ensure(channel)[GltfExtras.Weight] = weight;
            anim.Channels.Add(channel);
        }

        private GltfAnimationSampler RotationSampler(RfaClip clip, RfaBoneTrack track)
        {
            var keys = track.RotationKeys;
            var times = new List<int>();
            var values = new List<Quaternion>();
            bool increasing = Increasing(keys.Select(k => k.Time));
            bool eased = keys.Any(k => k.EaseIn != 0 || k.EaseOut != 0);
            if (keys.Length == 0)
            {
                times.Add(clip.StartTime);
                values.Add(Quaternion.Identity);
            }
            else if (increasing && !eased)
            {
                foreach (var k in keys)
                {
                    times.Add(k.Time);
                    values.Add(ClipSampler.KeyRotation(k));
                }
            }
            else
            {
                // Keys plus samples every step inside eased segments (or everywhere when the key
                // times do not increase), all sampled with the engine's sampler.
                Baked++;
                var set = new SortedSet<int>();
                if (increasing)
                {
                    foreach (var k in keys) set.Add(k.Time);
                    for (int i = 0; i + 1 < keys.Length; i++)
                    {
                        if (keys[i].EaseOut == 0 && keys[i + 1].EaseIn == 0) continue;
                        foreach (int t in GridInside(clip.StartTime, keys[i].Time, keys[i + 1].Time)) set.Add(t);
                    }
                }
                else
                {
                    foreach (int t in ClipEditGrid(clip.StartTime, clip.EndTime)) set.Add(t);
                }
                foreach (int t in set)
                {
                    times.Add(t);
                    values.Add(ClipSampler.SampleRotation(keys.AsSpan(), t));
                }
            }

            // glTF space, unit length, sign-continuous.
            var gl = new Quaternion[values.Count];
            for (int i = 0; i < gl.Length; i++)
            {
                var q = Quat.Normalize(GltfSpace.ToGltf(values[i]));
                gl[i] = i > 0 ? Quat.Align(q, gl[i - 1]) : q;
            }
            var sampler = new GltfAnimationSampler
            {
                Input = _buffer.AddScalars([.. times.Select(GltfSpace.Seconds)]),
                Output = _buffer.AddQuaternions(gl),
                Interpolation = GltfInterpolation.Linear,
            };
            if (options.WriteKeyExtras) GltfExtras.Ensure(sampler)[GltfExtras.Keys] = RotationKeyExtras(keys);
            return sampler;
        }

        private GltfAnimationSampler TranslationSampler(RfaClip clip, RfaBoneTrack track)
        {
            var keys = track.PositionKeys;
            GltfAnimationSampler sampler;
            if (keys.Length == 0)
            {
                sampler = new GltfAnimationSampler
                {
                    Input = _buffer.AddScalars([GltfSpace.Seconds(clip.StartTime)]),
                    Output = _buffer.AddVector3([Vector3.Zero], target: null),
                    Interpolation = GltfInterpolation.Linear,
                };
            }
            else if (Increasing(keys.Select(k => k.Time)))
            {
                // CUBICSPLINE: [in tangent, value, out tangent] per key; the RF Bezier exactly.
                var values = new Vector3[keys.Length * 3];
                for (int i = 0; i < keys.Length; i++)
                {
                    var k = keys[i];
                    float prevDt = i > 0 ? (k.Time - keys[i - 1].Time) / (float)RfaClip.TicksPerSecond : 0f;
                    float nextDt = i + 1 < keys.Length ? (keys[i + 1].Time - k.Time) / (float)RfaClip.TicksPerSecond : 0f;
                    var inDeriv = Vector3.Zero;
                    var outDeriv = Vector3.Zero;
                    if (prevDt > 0f) inDeriv = (k.Position - k.InControl) * (3f / prevDt);
                    else if (nextDt > 0f) inDeriv = (k.OutControl - k.Position) * (3f / nextDt);
                    if (nextDt > 0f) outDeriv = (k.OutControl - k.Position) * (3f / nextDt);
                    else if (prevDt > 0f) outDeriv = (k.Position - k.InControl) * (3f / prevDt);
                    values[i * 3] = GltfSpace.ToGltf(inDeriv);
                    values[i * 3 + 1] = GltfSpace.ToGltf(k.Position);
                    values[i * 3 + 2] = GltfSpace.ToGltf(outDeriv);
                }
                sampler = new GltfAnimationSampler
                {
                    Input = _buffer.AddScalars([.. keys.Select(k => GltfSpace.Seconds(k.Time))]),
                    Output = _buffer.AddVector3(values, target: null),
                    Interpolation = GltfInterpolation.CubicSpline,
                };
            }
            else
            {
                var grid = ClipEditGrid(clip.StartTime, clip.EndTime);
                sampler = new GltfAnimationSampler
                {
                    Input = _buffer.AddScalars([.. grid.Select(GltfSpace.Seconds)]),
                    Output = _buffer.AddVector3([.. grid.Select(t => GltfSpace.ToGltf(ClipSampler.SamplePosition(keys.AsSpan(), t)))], target: null),
                    Interpolation = GltfInterpolation.Linear,
                };
            }
            if (options.WriteKeyExtras) GltfExtras.Ensure(sampler)[GltfExtras.Keys] = PositionKeyExtras(keys);
            return sampler;
        }

        private JsonObject RotationKeyExtras(ImmutableArray<RfaRotKey> keys)
        {
            var o = new JsonObject { ["count"] = keys.Length };
            if (keys.Length == 0) return o;
            int origin = keys.Min(k => k.Time);
            o["time_origin"] = origin;
            o["times"] = _buffer.AddIntegers([.. keys.Select(k => k.Time - origin)], GltfComponentType.UnsignedInt, GltfAccessorType.Scalar);
            var raw = new int[keys.Length * 4];
            for (int i = 0; i < keys.Length; i++)
            {
                raw[i * 4] = keys[i].X;
                raw[i * 4 + 1] = keys[i].Y;
                raw[i * 4 + 2] = keys[i].Z;
                raw[i * 4 + 3] = keys[i].W;
            }
            o["values"] = _buffer.AddIntegers(raw, GltfComponentType.Short, GltfAccessorType.Vec4);
            if (keys.Any(k => k.EaseIn != 0 || k.EaseOut != 0))
                o["eases"] = _buffer.AddIntegers([.. keys.SelectMany(k => new int[] { k.EaseIn, k.EaseOut })], GltfComponentType.Byte, GltfAccessorType.Vec2);
            if (keys.Any(k => k.Pad != 0))
                o["pads"] = _buffer.AddIntegers([.. keys.Select(k => (int)k.Pad)], GltfComponentType.Short, GltfAccessorType.Scalar);
            return o;
        }

        private JsonObject PositionKeyExtras(ImmutableArray<RfaPosKey> keys)
        {
            var o = new JsonObject { ["count"] = keys.Length };
            if (keys.Length == 0) return o;
            int origin = keys.Min(k => k.Time);
            o["time_origin"] = origin;
            o["times"] = _buffer.AddIntegers([.. keys.Select(k => k.Time - origin)], GltfComponentType.UnsignedInt, GltfAccessorType.Scalar);
            var points = new Vector3[keys.Length * 3];
            for (int i = 0; i < keys.Length; i++)
            {
                points[i * 3] = keys[i].Position;
                points[i * 3 + 1] = keys[i].InControl;
                points[i * 3 + 2] = keys[i].OutControl;
            }
            o["points"] = _buffer.AddVector3(points, target: null);
            return o;
        }

        private static bool Increasing(IEnumerable<int> times)
        {
            int? prev = null;
            foreach (int t in times)
            {
                if (prev is { } p && t <= p) return false;
                prev = t;
            }
            return true;
        }

        /// <summary>Grid times <c>origin + j * step</c> strictly inside (a, b).</summary>
        private IEnumerable<int> GridInside(int origin, int a, int b)
        {
            int step = options.BakeStepTicks;
            long first = origin + (long)Math.Floor((a - origin) / (double)step + 1) * step;
            for (long t = first; t < b; t += step) yield return (int)t;
        }

        private int[] ClipEditGrid(int start, int end)
        {
            var list = new List<int>();
            int step = options.BakeStepTicks;
            for (long t = start; t < end; t += step) list.Add((int)t);
            list.Add(Math.Max(start, end));
            return [.. list.Distinct()];
        }
    }
}
