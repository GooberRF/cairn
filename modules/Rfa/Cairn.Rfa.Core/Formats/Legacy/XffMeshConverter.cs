using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Cairn.Formats.XForge;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>A game texture an N-Gage mesh names: its file name in the archive and the name the converted mesh stores.</summary>
/// <param name="SourceName">The file name the scene names (<c>lvl0_crate_01.pcx</c>).</param>
/// <param name="StoredName">The name the converted mesh's material holds: the same, or a shortened one when it does not fit the 32-byte field.</param>
public sealed record XffTextureUse(string SourceName, string StoredName)
{
    /// <summary>True when the name had to be shortened (the copy is written under <see cref="StoredName"/>).</summary>
    public bool IsRenamed => !string.Equals(SourceName, StoredName, StringComparison.Ordinal);
}

/// <summary>What an N-Gage scene holds, for facts lines and banners.</summary>
/// <param name="Kind">Model or character (a level never converts here).</param>
/// <param name="Nodes">Scene nodes (groups, objects, helpers, tracks, markers and the rest).</param>
/// <param name="Meshes">Mesh instances.</param>
/// <param name="Triangles">Triangles over every submesh.</param>
/// <param name="Textures">Distinct texture names.</param>
/// <param name="Tracks">Animation tracks.</param>
/// <param name="TrackLength">The longest track's range in milliseconds (end minus start), 0 without tracks.</param>
public sealed record XffSceneFacts(XffKind Kind, int Nodes, int Meshes, int Triangles, int Textures, int Tracks, int TrackLength)
{
    /// <summary>The facts of <paramref name="scene"/>.</summary>
    public static XffSceneFacts Of(XffScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return new XffSceneFacts(scene.Kind, scene.Nodes.Count, scene.Meshes.Count, scene.Meshes.Sum(m => m.Submeshes.Sum(s => s.TriangleCount)),
            scene.Meshes.SelectMany(m => m.Submeshes).Select(s => s.TextureName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            scene.Tracks.Count, scene.Tracks.Count == 0 ? 0 : (int)Math.Clamp(scene.Tracks.Max(t => (long)t.End - t.Start), int.MinValue, int.MaxValue));
    }

    /// <summary>"character: 37 nodes, 20 meshes, 146 triangles, 1 texture, 10 tracks (533 ms)".</summary>
    public string Summary => (Kind == XffKind.Character ? "character" : Kind == XffKind.Level ? "level" : "model") + ": " + Counts;

    /// <summary>"37 nodes, 20 meshes, 146 triangles, 1 texture, 10 tracks (533 ms)".</summary>
    public string Counts
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            string Count(int n, string one, string many) => n.ToString("N0", inv) + " " + (n == 1 ? one : many);
            string text = string.Join(", ", Count(Nodes, "node", "nodes"), Count(Meshes, "mesh", "meshes"), Count(Triangles, "triangle", "triangles"), Count(Textures, "texture", "textures"));
            if (Tracks > 0) text += ", " + Count(Tracks, "track", "tracks") + $" ({TrackLength.ToString("N0", inv)} ms)";
            return text;
        }
    }
}

/// <summary>What an N-Gage scene's conversion carries besides the mesh description (<see cref="LegacyMesh.Xff"/>).</summary>
/// <param name="Facts">What the scene holds.</param>
/// <param name="Scale">Red Faction metres per XFF unit the mesh was built with.</param>
/// <param name="Textures">The game textures the mesh names (copied beside the converted mesh when asked).</param>
/// <param name="GeneratedTextures">Files the conversion makes and the mesh needs (the solid colours of untextured parts), always written beside it.</param>
/// <param name="Clip">A character's animation as one looping version 8 clip, or null.</param>
/// <param name="ClipName">The clip's file name (<c>&lt;name&gt;_walk.rfa</c>), or null.</param>
public sealed record XffMeshParts(
    XffSceneFacts Facts,
    float Scale,
    ImmutableArray<XffTextureUse> Textures,
    ImmutableArray<(string Name, byte[] Bytes)> GeneratedTextures,
    RfaClip? Clip,
    string? ClipName);

/// <summary>
/// Red Faction for N-Gage scenes (<c>.xff</c>) of kind Model or Character to the <see cref="V3dMeshDescription"/> the PC
/// meshes are built from, so they open, preview and convert as the other read-only meshes do and the mesh linter checks
/// the result. The space is converted once, through <see cref="XffSpace"/>: positions times the scale (2.35 by default),
/// the axes kept, each triangle (A, B, C) written as (A, C, B) with its per-corner UVs, and the normals negated. Vertices
/// are split by position, normal and UV. A model is one submesh per mesh node (as Red Faction II's meshes are one per
/// object); a character is one submesh (a <c>.v3c</c> draws one) on a skeleton of one bone per animated part, each part's
/// vertices weighted fully to its bone, with the tracks sampled into one looping clip. Materials keep their texture
/// names; the blend pair becomes the nearest batch state; the baked vertex colours are dropped, except that untextured
/// parts are drawn with their average colour from a small generated texture. What is approximated is listed in the notes.
/// </summary>
public static class XffMeshConverter
{
    /// <summary>The render flags of an additive stock batch (the usual ones are <see cref="V3dBuilder.DefaultRenderFlags"/>).</summary>
    public const uint AdditiveRenderFlags = 0x110C21;

    /// <summary>Material flags of every converted material (bit 0, as every stock material).</summary>
    private const uint BaseFlags = ExporterMeshConverter.CompiledBaseFlags;

    /// <summary>What the clip's name adds to the source's stem: the one loop the game has is the character's walk.</summary>
    public const string ClipSuffix = "_walk";

    /// <summary>The bone the parts that no track moves are weighted to, when there are any.</summary>
    public const string RootBoneName = "root";

    /// <summary>
    /// The most keys a bone a character's clip gets (the samples of its longest track's range). The game's own tracks run
    /// at most 1,920 ms, 59 keys at its 33 ms interval; 2,000 keys is over a minute at that interval. A scene whose
    /// declared range needs more converts without its clip, and the report says why.
    /// </summary>
    public const int MaxClipKeys = 2_000;

    /// <summary>The texture names the game's own archive does not hold (named by its scenes all the same).</summary>
    public static ImmutableArray<string> MissingFromGame { get; } =
    [
        "txtr_enviro_guard_limbs_mip2.tga", "txtr_enviro_guard_chest_mip2.tga", "txtr_enviro_guard_face_mip2.tga",
        "txtr_enviro_guard_glass_mip2.tga", "txtr_fcanister1.tga",
    ];

    /// <summary>
    /// Parts of a character the N-Gage game draws only now and then (the bosses' nano shield, a sphere round the whole
    /// body). A .v3c cannot hide a part, so a character's conversion leaves them out.
    /// </summary>
    public static ImmutableArray<string> EffectParts { get; } = ["g_nanoshield"];

    /// <summary>The colour an untextured part without vertex colours is drawn with.</summary>
    private static readonly (byte R, byte G, byte B) NoColour = (192, 192, 192);

    /// <summary>Pixels per colour in the generated colour texture.</summary>
    private const int ColourCell = 8;

    private const int ListedNames = 8;

    /// <summary>The name of a character's clip for <paramref name="sourceName"/>: <c>enemy_ug.xff</c> → <c>enemy_ug_walk.rfa</c>.</summary>
    public static string ClipName(string sourceName) => Path.GetFileNameWithoutExtension(Path.GetFileName(sourceName)) + ClipSuffix + ".rfa";

    /// <summary>Converts a model or character scene (no files are read: the textures are only named).</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="name">Its file name (the submesh, palette and clip names come from it).</param>
    /// <param name="scale">Red Faction metres per XFF unit (<see cref="XffSpace.RfScale"/> by default).</param>
    /// <exception cref="AssetFormatException">The scene is a level, or the scale is out of range.</exception>
    public static LegacyMesh Convert(XffScene scene, string name, float scale = XffSpace.RfScale)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(name);
        var kind = scene.Kind;
        if (kind == XffKind.Level)
            throw new AssetFormatException($"'{name}' is a Red Faction for N-Gage level, not a model or character; the Levels module opens it.");
        if (!XffSpace.IsValidScale(scale))
            throw new AssetFormatException($"The scale must be from {XffSpace.ScaleRangeText} metres per unit.");
        return new Builder(scene, Path.GetFileName(name), scale, kind == XffKind.Character).Build();
    }

    /// <summary>Reads and converts a scene file.</summary>
    /// <exception cref="AssetFormatException">It cannot be read, is a level, or the scale is out of range.</exception>
    public static LegacyMesh Convert(byte[] data, string name, float scale = XffSpace.RfScale)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Convert(XffScene.Read(data, name), name, scale);
    }

    /// <summary>
    /// The conversion of a read scene: the compiled mesh, the generated textures, the game textures copied unchanged from
    /// <paramref name="sibling"/> (the same archive or folder) when <see cref="LegacyConvertOptions.XffWriteTextures"/> is on,
    /// a character's clip, and the report.
    /// </summary>
    /// <exception cref="AssetFormatException">The mesh or the clip cannot be written; the message says why.</exception>
    internal static LegacyMeshConversion Finish(LegacyMesh mesh, string sourceName, Func<string, byte[]?>? sibling, LegacyConvertOptions options)
    {
        var parts = mesh.Xff ?? throw new ArgumentException("The mesh is not an N-Gage scene's.", nameof(mesh));
        var compiled = LegacyMeshSupport.Compile(mesh);
        byte[] bytes = V3dWriter.Write(compiled);
        string output = Path.ChangeExtension(Path.GetFileName(sourceName), LegacyMeshSupport.TargetExtension(mesh));
        var report = ImmutableArray.CreateBuilder<string>();
        report.AddRange(mesh.Notes);

        var files = ImmutableArray.CreateBuilder<(string Name, byte[] Bytes)>();
        files.AddRange(parts.GeneratedTextures);
        if (parts.Textures.Length > 0)
        {
            if (!options.XffWriteTextures)
                report.Add($"The textures were not written beside the mesh (that option was off); it names {List(parts.Textures.Select(t => t.StoredName))}.");
            else if (sibling is null)
                report.Add($"There is no archive or folder to copy the textures from, so none were written; the mesh names {List(parts.Textures.Select(t => t.StoredName))}.");
            else
            {
                var missing = new List<string>();
                var unreadable = new List<string>();
                var opaque = new List<string>();
                var written = new List<string>();
                int converted = 0;
                foreach (var use in parts.Textures)
                {
                    byte[]? data = null;
                    try { data = sibling(use.SourceName); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException or InvalidDataException) { data = null; }
                    if (data is null) { missing.Add(use.SourceName); continue; }
                    byte[] fixedData;
                    try { fixedData = XffTextureFix.ForPc(data, use.SourceName); }
                    catch (ImageDecodeException) { unreadable.Add(use.SourceName); continue; }
                    if (XffTextureFix.IsPcx(use.SourceName)) converted++;
                    else if (!ReferenceEquals(fixedData, data)) opaque.Add(use.StoredName);
                    files.Add((use.StoredName, fixedData));
                    written.Add(use.StoredName);
                }
                if (written.Count > 0)
                    report.Add($"{Plural(written.Count, "texture was", "textures were")} written beside the mesh ({List(written)})"
                        + (converted == 0 ? "." : $"; {Plural(converted, ".pcx texture was", ".pcx textures were")} written as .tga under the name the mesh stores (Red Faction's tools and levels take .tga, not .pcx)."));
                if (unreadable.Count > 0)
                    report.Add($"{Plural(unreadable.Count, "texture", "textures")} could not be read as an image ({List(unreadable)}), so {(unreadable.Count == 1 ? "it was" : "they were")} not written.");
                if (opaque.Count > 0)
                    report.Add($"{Plural(opaque.Count, "16-bit texture declares", "16-bit textures declare")} an alpha bit that no pixel sets ({List(opaque)}); the N-Gage game draws {(opaque.Count == 1 ? "it" : "them")} opaque, "
                        + $"so the copy's header says it has no alpha (the pixels are the same), or Red Faction might draw {(opaque.Count == 1 ? "it" : "them")} see-through.");
                if (missing.Count > 0)
                {
                    var known = missing.Where(m => MissingFromGame.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
                    report.Add($"{Plural(missing.Count, "texture is", "textures are")} not in the archive or folder ({List(missing)}), so {(missing.Count == 1 ? "it was" : "they were")} not written"
                        + (known.Count == missing.Count ? "; the game's own archive does not hold " + (known.Count == 1 ? "it" : "them") + " either." : "."));
                }
            }
        }

        foreach (var d in MeshLinter.Analyze(compiled, new MeshLintContext { FileName = output }))
        {
            if (d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning) report.Add($"{output}: {d.Message}");
        }

        Rf2ClipConversion? clip = null;
        if (parts.Clip is { } rfa && parts.ClipName is { } clipName)
        {
            byte[] clipBytes;
            try { clipBytes = RfaWriter.Write(rfa); }
            catch (ArgumentException ex) { throw new AssetFormatException($"'{sourceName}''s animation cannot be written: {ex.Message.Split(" (Parameter")[0]}"); }
            var clipReport = ImmutableArray.CreateBuilder<string>();
            foreach (var d in ClipLinter.AnalyzeStructure(rfa))
            {
                if (d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning) clipReport.Add($"{clipName}: {d.Message}");
            }
            report.AddRange(clipReport);
            clip = new Rf2ClipConversion(Path.GetFileName(sourceName), clipName, rfa, clipBytes, clipReport.ToImmutable());
        }
        return new LegacyMeshConversion(Path.GetFileName(sourceName), output, compiled, bytes, report.ToImmutable()) { Textures = files.ToImmutable(), Clip = clip };
    }

    // ── the blend pair ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The nearest batch state of a material's blend pair (source, destination factor; D3D numbering).</summary>
    internal enum Blend
    {
        /// <summary>(2, 1): one, zero.</summary>
        Opaque,
        /// <summary>(5, 6): source alpha, inverse source alpha.</summary>
        Alpha,
        /// <summary>(2, 2): one, one.</summary>
        Additive,
        /// <summary>(1, 4): zero, inverse source colour (darkening overlays).</summary>
        Darken,
        /// <summary>Any other pair.</summary>
        Other,
    }

    internal static Blend BlendOf(XffMaterial? material) => material is null ? Blend.Opaque : (material.UnknownByte0, material.UnknownByte1) switch
    {
        (2, 1) => Blend.Opaque,
        (5, 6) => Blend.Alpha,
        (2, 2) => Blend.Additive,
        (1, 4) => Blend.Darken,
        _ => Blend.Other,
    };

    private static uint MaterialFlags(Blend blend) => blend is Blend.Alpha or Blend.Darken ? BaseFlags | ExporterMeshConverter.CompiledAlpha : BaseFlags;

    private static uint RenderFlags(Blend blend) => blend == Blend.Additive ? AdditiveRenderFlags : V3dBuilder.DefaultRenderFlags;

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static string Plural(int n, string one, string many) => n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    private static string List(IEnumerable<string> items)
    {
        var all = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return string.Join(", ", all.Take(ListedNames)) + (all.Count > ListedNames ? $" and {(all.Count - ListedNames).ToString("N0", CultureInfo.InvariantCulture)} more" : "");
    }

    /// <summary>Latin-1 text cut to fit a field of <paramref name="size"/> bytes with its terminator.</summary>
    private static string Fit(string text, int size, ref int truncated)
    {
        string clean = new([.. text.Select(c => c == 0 || c > 255 ? '_' : c)]);
        if (clean.Length == 0) clean = "_";
        if (clean.Length <= size - 1) return clean;
        truncated++;
        return clean[..(size - 1)];
    }

    /// <summary><paramref name="name"/>, or "name_2", "name_3"… cut to <paramref name="size"/> − 1 characters, the first not in <paramref name="taken"/>.</summary>
    private static string Unique(string name, HashSet<string> taken, int size)
    {
        if (taken.Add(name)) return name;
        for (int n = 2; ; n++)
        {
            string suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
            string stem = name.Length > size - 1 - suffix.Length ? name[..(size - 1 - suffix.Length)] : name;
            if (taken.Add(stem + suffix)) return stem + suffix;
        }
    }

    /// <summary>A rigid transform from a world matrix (rotation and translation; the scenes' scales are all 1), its translation in Red Faction metres.</summary>
    internal static Rigid RigidOf(Matrix4x4 world, float scale)
    {
        if (!Matrix4x4.Decompose(world, out _, out var rotation, out var translation))
        {
            rotation = Quaternion.Identity;
            translation = world.Translation;
        }
        return new Rigid(Quat.Normalize(XffSpace.ToRfRotation(rotation)), ToRf(translation, scale));
    }

    /// <summary>A position in XFF space in Red Faction metres at <paramref name="scale"/> (<see cref="XffSpace.ToRfPosition"/> at the default).</summary>
    internal static Vector3 ToRf(Vector3 xff, float scale) => scale == XffSpace.RfScale ? XffSpace.ToRfPosition(xff) : XffSpace.ToRfPosition(xff) * (scale / XffSpace.RfScale);

    private readonly record struct MaterialKey(string Texture, Blend Blend);

    private readonly record struct VertexKey(int Px, int Py, int Pz, int Nx, int Ny, int Nz, int U, int V, int Bone)
    {
        public static VertexKey From(Vector3 p, Vector3 n, Vector2 uv, int bone) => new(
            Bits(p.X), Bits(p.Y), Bits(p.Z), Bits(n.X), Bits(n.Y), Bits(n.Z), Bits(uv.X), Bits(uv.Y), bone);

        // -0 and +0 are the same value.
        private static int Bits(float f) => BitConverter.SingleToInt32Bits(f + 0f);
    }

    private sealed class Group(int material, uint renderFlags)
    {
        public int Material { get; } = material;
        public uint RenderFlags { get; } = renderFlags;
        public List<V3dMeshVertex> Vertices { get; } = [];
        public Dictionary<VertexKey, int> Index { get; } = [];
        public List<V3dMeshTriangle> Triangles { get; } = [];
    }

    /// <summary>One submesh being built: its materials (at most <see cref="V3dBuilder.MaxTexturesPerLod"/> textures for a model) and geometry.</summary>
    private sealed class Part(string name)
    {
        public string Name { get; set; } = name;
        public List<V3dMaterial> Materials { get; } = [];
        public Dictionary<MaterialKey, int> MaterialIndex { get; } = [];
        public HashSet<string> TextureNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, Group> Groups { get; } = [];
    }

    /// <summary>A bone of a character: the track node it follows (null for the added root) and its parent bone.</summary>
    private sealed record BoneSource(XffSceneNode? Track, string Name, int Parent);

    private sealed class Builder(XffScene scene, string name, float scale, bool character)
    {
        private readonly List<string> _notes = [];
        private int _truncated, _corners, _vertices, _skipped, _noNormals, _texturedWithoutUvs;
        private readonly Dictionary<Blend, int> _blends = [];
        private readonly HashSet<(byte, byte)> _otherPairs = [];
        private int _blendedAlpha;
        private bool _colours;
        private readonly Dictionary<string, XffTextureUse> _textures = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _storedTextures = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(byte R, byte G, byte B)> _palette = [];
        private string _paletteName = "";

        public LegacyMesh Build()
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            var bind = scene.WorldMatrices(t => t.Sample(t.Start));

            // a character's bones: one per track that moves geometry, parents from the hierarchy
            var bones = new List<BoneSource>();
            var boneOfMesh = new Dictionary<XffSceneNode, int>(ReferenceEqualityComparer.Instance);
            var droppedHelpers = new List<string>();
            int unmoved = 0, overLimit = 0;
            if (character) (unmoved, overLimit) = BuildBones(bones, boneOfMesh, droppedHelpers);

            // the colours of untextured parts, first, so the palette's layout is known
            foreach (var mesh in scene.Meshes.Where(Drawn))
                foreach (var sub in mesh.Submeshes)
                    if (sub.TriangleCount > 0 && !IsTextured(sub) && !_palette.Contains(AverageColour(sub))) _palette.Add(AverageColour(sub));
            if (_palette.Count > 0) _paletteName = PaletteName(stem);

            // geometry: a model is one part per mesh node (more when a node has too many textures), a character one part
            var parts = new List<Part>();
            var partsOfNode = new Dictionary<XffSceneNode, List<Part>>(ReferenceEqualityComparer.Instance);
            var characterPart = new Part(stem);
            var influences = new Dictionary<int, ImmutableArray<V3dBoneInfluence>>();
            foreach (var mesh in scene.Meshes.Where(Drawn))
            {
                var world = bind[mesh.Node.Index];
                int bone = character ? boneOfMesh.GetValueOrDefault(mesh.Node, 0) : -1;
                foreach (var sub in mesh.Submeshes)
                {
                    if (sub.TriangleCount == 0) continue;
                    bool textured = IsTextured(sub);
                    if (sub.Texture is not null && !textured) _texturedWithoutUvs++;
                    string texture = textured ? StoredTexture(sub.TextureName!) : _paletteName;
                    var blend = BlendOf(sub.Material);
                    _blends[blend] = _blends.GetValueOrDefault(blend) + 1;
                    if (blend == Blend.Other && sub.Material is { } m) _otherPairs.Add((m.UnknownByte0, m.UnknownByte1));
                    if (blend is Blend.Alpha or Blend.Darken && sub.CornerColours.Count > 0) _blendedAlpha++;
                    if (sub.CornerColours.Count > 0) _colours = true;

                    Part part;
                    if (character) part = characterPart;
                    else
                    {
                        if (!partsOfNode.TryGetValue(mesh.Node, out var list)) partsOfNode.Add(mesh.Node, list = []);
                        part = list.FirstOrDefault(p => p.TextureNames.Contains(texture) || p.TextureNames.Count < V3dBuilder.MaxTexturesPerLod)
                            ?? AddPart(list, mesh.Node.Name);
                        if (!parts.Contains(part)) parts.Add(part);
                    }
                    int material = Material(part, texture, blend);
                    if (!part.Groups.TryGetValue(material, out var group)) part.Groups.Add(material, group = new Group(material, RenderFlags(blend)));
                    var uvFor = textured ? null : (Vector2?)PaletteUv(AverageColour(sub));
                    ImmutableArray<V3dBoneInfluence> weights = [];
                    if (bone >= 0 && !influences.TryGetValue(bone, out weights)) influences[bone] = weights = [new V3dBoneInfluence(bone, 1f)];
                    AddTriangles(group, sub, world, uvFor, bone, weights);
                }
            }
            if (character && characterPart.Groups.Count > 0) parts.Add(characterPart);

            // names: unique and fitted to the 24-byte field
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var submeshes = ImmutableArray.CreateBuilder<V3dSubmeshDescription>(parts.Count);
            foreach (var part in parts)
            {
                string unique = Unique(Fit(part.Name, V3dSubmesh.NameSize, ref _truncated), taken, V3dSubmesh.NameSize);
                submeshes.Add(new V3dSubmeshDescription
                {
                    Name = FixedString.FromText(unique, V3dSubmesh.NameSize),
                    Lods = [new V3dLodDescription
                    {
                        Groups = [.. part.Groups.Values.OrderBy(g => g.Material).Select(g => new V3dMaterialGroup
                        {
                            Material = g.Material,
                            RenderFlags = g.RenderFlags,
                            Vertices = [.. g.Vertices],
                            Triangles = [.. g.Triangles],
                        })],
                    }],
                    Materials = [.. part.Materials],
                });
            }

            var boneList = ImmutableArray.CreateBuilder<V3dBone>(bones.Count);
            if (character)
            {
                foreach (var b in bones)
                {
                    var rest = b.Track is null ? Rigid.Identity : RigidOf(bind[b.Track.Index], scale);
                    boneList.Add(V3dBuilder.BoneFromRestWorld(b.Name, b.Parent, rest));
                }
            }

            RfaClip? clip = null;
            string? clipName = null;
            var clipNotes = new List<string>();
            if (character && bones.Count > 0)
            {
                clip = BuildClip(bones, clipNotes, ClipName(name));
                clipName = clip is null ? null : ClipName(name);
            }

            byte[]? palette = _palette.Count > 0 ? PaletteFile() : null;
            Notes(submeshes.Count, bones, droppedHelpers, unmoved, overLimit, clipNotes);
            var description = new V3dMeshDescription
            {
                Kind = character ? V3dKind.Character : V3dKind.StaticMesh,
                Submeshes = submeshes.MoveToImmutable(),
                Bones = boneList.MoveToImmutable(),
            };
            var extras = new XffMeshParts(XffSceneFacts.Of(scene), scale, [.. _textures.Values],
                palette is null ? [] : [(_paletteName, palette)], clip, clipName);
            return new LegacyMesh("xff", description, [.. _notes]) { Xff = extras };
        }

        private Part AddPart(List<Part> list, string nodeName)
        {
            var part = new Part(nodeName);
            list.Add(part);
            return part;
        }

        private static bool IsTextured(XffSceneSubmesh sub) => sub.Texture is not null && sub.CornerUvs.Count == sub.Indices.Count && sub.Indices.Count > 0;

        private static (byte R, byte G, byte B) AverageColour(XffSceneSubmesh sub)
        {
            if (sub.CornerColours.Count == 0) return NoColour;
            long r = 0, g = 0, b = 0;
            foreach (var c in sub.CornerColours) { r += c.R; g += c.G; b += c.B; }
            int n = sub.CornerColours.Count;
            return ((byte)((r + n / 2) / n), (byte)((g + n / 2) / n), (byte)((b + n / 2) / n));
        }

        /// <summary>The palette texture's name: "&lt;stem&gt;_colours.tga", the stem cut to fit the 32-byte material field.</summary>
        private string PaletteName(string stem)
        {
            const string suffix = "_colours.tga";
            string clean = new([.. stem.Select(c => c == 0 || c > 255 ? '_' : c)]);
            int room = V3dMaterial.NameSize - 1 - suffix.Length;
            string result = (clean.Length > room ? clean[..room] : clean) + suffix;
            _storedTextures.Add(result);
            return result;
        }

        private int PaletteColumns()
        {
            int columns = 1;
            while (columns * columns < _palette.Count) columns *= 2;
            return columns;
        }

        private Vector2 PaletteUv((byte R, byte G, byte B) colour)
        {
            int index = _palette.IndexOf(colour), columns = PaletteColumns();
            float side = columns * ColourCell;
            return new Vector2(((index % columns) * ColourCell + ColourCell / 2f) / side, ((index / columns) * ColourCell + ColourCell / 2f) / side);
        }

        private byte[] PaletteFile()
        {
            int columns = PaletteColumns(), side = columns * ColourCell;
            var image = new BgraImage(side, side);
            for (int i = 0; i < columns * columns; i++)
            {
                var (r, g, b) = i < _palette.Count ? _palette[i] : NoColour;
                int x0 = (i % columns) * ColourCell, y0 = (i / columns) * ColourCell;
                for (int y = y0; y < y0 + ColourCell; y++)
                    for (int x = x0; x < x0 + ColourCell; x++) image.Set(x, y, b, g, r, 255);
            }
            return TgaWriter.Write(image, includeAlpha: false);
        }

        /// <summary>The name a game texture is stored under: its own (a .pcx as .tga), or a shortened unique one when it does not fit.</summary>
        private string StoredTexture(string source)
        {
            if (_textures.TryGetValue(source, out var use)) return use.StoredName;
            string clean = XffTextureFix.PcName(new([.. source.Select(c => c == 0 || c > 255 ? '_' : c)]));
            string stored = clean;
            if (clean.Length > V3dMaterial.NameSize - 1 || !_storedTextures.Add(clean))
            {
                string ext = Path.GetExtension(clean), stem = Path.GetFileNameWithoutExtension(clean);
                for (int n = 1; ; n++)
                {
                    string suffix = "_" + n.ToString(CultureInfo.InvariantCulture) + ext;
                    int room = V3dMaterial.NameSize - 1 - suffix.Length;
                    string candidate = (stem.Length > room ? stem[..room] : stem) + suffix;
                    if (_storedTextures.Add(candidate)) { stored = candidate; break; }
                }
            }
            _textures.Add(source, new XffTextureUse(source, stored));
            return stored;
        }

        private static int Material(Part part, string texture, Blend blend)
        {
            var key = new MaterialKey(texture.ToLowerInvariant(), blend);
            if (part.MaterialIndex.TryGetValue(key, out int existing)) return existing;
            part.Materials.Add(new V3dMaterial(FixedString.FromText(texture, V3dMaterial.NameSize), 0f, 0f, 0f, 0f,
                FixedString.FromText("", V3dMaterial.NameSize), MaterialFlags(blend)));
            part.MaterialIndex.Add(key, part.Materials.Count - 1);
            part.TextureNames.Add(texture);
            return part.Materials.Count - 1;
        }

        /// <summary>
        /// Adds a submesh's triangles in Red Faction's space: positions placed by <paramref name="world"/> and scaled, normals
        /// rotated and negated, corners (A, B, C) as (A, C, B) with their UVs, one vertex per distinct position, normal and UV.
        /// </summary>
        private void AddTriangles(Group group, XffSceneSubmesh sub, Matrix4x4 world, Vector2? flatUv, int bone, ImmutableArray<V3dBoneInfluence> weights)
        {
            bool normals = sub.Normals.Count == sub.Positions.Count;
            if (!normals) _noNormals++;
            for (int t = 0; t + 2 < sub.Indices.Count; t += 3)
            {
                var (a, b, c) = XffSpace.ToRfTriangle(t, t + 1, t + 2);
                var pa = Position(sub, a, world); var pb = Position(sub, b, world); var pc = Position(sub, c, world);
                if (!IsFinite(pa) || !IsFinite(pb) || !IsFinite(pc)) { _skipped++; continue; }
                // RF's front face: cross(B − A, C − A) points to the visible side
                var face = Vector3.Cross(pb - pa, pc - pa);
                face = face.LengthSquared() > 0 ? Vector3.Normalize(face) : Vector3.UnitY;
                int ia = Corner(group, sub, a, pa, world, face, normals, flatUv, bone, weights);
                int ib = Corner(group, sub, b, pb, world, face, normals, flatUv, bone, weights);
                int ic = Corner(group, sub, c, pc, world, face, normals, flatUv, bone, weights);
                group.Triangles.Add(new V3dMeshTriangle(ia, ib, ic));
                _corners += 3;
            }
        }

        private Vector3 Position(XffSceneSubmesh sub, int corner, Matrix4x4 world) => ToRf(Vector3.Transform(sub.Positions[sub.Indices[corner]], world), scale);

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private int Corner(Group group, XffSceneSubmesh sub, int corner, Vector3 position, Matrix4x4 world, Vector3 face, bool normals, Vector2? flatUv, int bone,
            ImmutableArray<V3dBoneInfluence> weights)
        {
            var normal = face;
            if (normals)
            {
                var n = Vector3.TransformNormal(sub.Normals[sub.Indices[corner]], world);
                normal = IsFinite(n) && n.LengthSquared() > 1e-12f ? XffSpace.ToRfNormal(Vector3.Normalize(n)) : face;
            }
            var uv = flatUv ?? sub.CornerUvs[corner];
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) uv = Vector2.Zero;
            var key = VertexKey.From(position, normal, uv, bone);
            if (group.Index.TryGetValue(key, out int index)) return index;
            index = group.Vertices.Count;
            group.Index.Add(key, index);
            group.Vertices.Add(new V3dMeshVertex(position, normal, uv, weights));
            _vertices++;
            return index;
        }

        // ── the skeleton ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One bone per track node whose subtree holds geometry, in tree order (parents first), each parented to the nearest
        /// such track above it; tracks without geometry below (IK helpers, cameras) are left out. Parts no track moves go to
        /// an added root bone, which every other root bone hangs from. At most <see cref="V3dBoneSection.MaxBones"/>.
        /// </summary>
        private (int Unmoved, int OverLimit) BuildBones(List<BoneSource> bones, Dictionary<XffSceneNode, int> boneOfMesh, List<string> droppedHelpers)
        {
            // which subtrees hold geometry: children before parents (reversed tree order), no recursion, as chains may be deep
            var order = new List<XffSceneNode>(scene.Nodes.Count);
            var walk = new Stack<XffSceneNode>(scene.Roots);
            while (walk.Count > 0)
            {
                var node = walk.Pop();
                order.Add(node);
                foreach (var child in node.Children) walk.Push(child);
            }
            var geometry = new bool[scene.Nodes.Count];
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var node = order[i];
                geometry[node.Index] = node.Meshes.Any(m => Drawn(m) && m.Submeshes.Any(s => s.TriangleCount > 0)) || node.Children.Any(c => geometry[c.Index]);
            }

            // track bones in tree order, before any root is added
            var found = new List<(XffSceneNode Node, XffSceneNode? ParentTrack)>();
            var meshTrack = new Dictionary<XffSceneNode, XffSceneNode?>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(XffSceneNode Node, XffSceneNode? Track)>();
            foreach (var top in scene.Roots.Reverse()) stack.Push((top, null));
            while (stack.Count > 0)
            {
                var (node, above) = stack.Pop();
                var track = above;
                if (node.AsTrack is { } t)
                {
                    if (geometry[node.Index])
                    {
                        found.Add((node, above));
                        track = node;
                    }
                    else droppedHelpers.Add(t.Target?.Name ?? t.Name);
                }
                if (node.Meshes.Any(Drawn)) meshTrack[node] = track;
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push((node.Children[i], track));
            }

            int unmoved = meshTrack.Count(kv => kv.Value is null && kv.Key.Meshes.Any(m => Drawn(m) && m.Submeshes.Any(s => s.TriangleCount > 0)));
            bool root = unmoved > 0;
            int limit = V3dBoneSection.MaxBones - (root ? 1 : 0);
            int overLimit = Math.Max(0, found.Count - limit);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root) bones.Add(new BoneSource(null, Unique(RootBoneName, names, V3dBone.NameSize), -1));
            // each found track's bone: its own when kept (the first ones in tree order, so the tracks above a kept one are
            // kept too), else the nearest kept one's above it (-1 for none); tree order puts every track after the tracks
            // above it, so one pass settles them all
            var nearest = new Dictionary<XffSceneNode, int>(ReferenceEqualityComparer.Instance);
            for (int f = 0; f < found.Count; f++)
            {
                var (node, parentTrack) = found[f];
                int above = parentTrack is null ? -1 : nearest[parentTrack];
                if (f >= limit)
                {
                    nearest[node] = above;
                    continue;
                }
                string boneName = node.AsTrack!.Target?.Name ?? (node.AsTrack.Name.StartsWith("anim_", StringComparison.OrdinalIgnoreCase) ? node.AsTrack.Name[5..] : node.AsTrack.Name);
                nearest[node] = bones.Count;
                bones.Add(new BoneSource(node, Unique(Fit(boneName, V3dBone.NameSize, ref _truncated), names, V3dBone.NameSize), above >= 0 ? above : root ? 0 : -1));
            }
            foreach (var (mesh, track) in meshTrack)
                boneOfMesh[mesh] = track is not null && nearest[track] >= 0 ? nearest[track] : 0;
            return (unmoved, overLimit);
        }

        // ── the clip ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The tracks sampled into one looping clip: every track from its own start on one grid of the shortest key interval,
        /// for the longest track's range, by the game's rule (<see cref="XffSceneTrack.Sample"/>: key k at k · interval ms,
        /// held before and after, wrapped when looping), so tracks with different ranges stay in step. Each bone's local
        /// transform is its world over its parent bone's world, in Red Faction metres. Null, with a note saying why, when the
        /// declared range needs more than <see cref="MaxClipKeys"/> keys a bone.
        /// </summary>
        private RfaClip? BuildClip(List<BoneSource> bones, List<string> notes, string clipName)
        {
            var tracks = bones.Select(b => b.Track?.AsTrack).OfType<XffSceneTrack>().ToList();
            int interval = tracks.Select(t => t.Interval).Where(i => i > 0).DefaultIfEmpty(33).Min();
            long declared = tracks.Count == 0 ? 0 : tracks.Max(t => (long)t.End - t.Start);
            // the sampling follows the declared range, not the keys: a range no real track comes near is refused
            long samples = declared <= 0 ? 2 : (declared + interval - 1) / interval + 1;
            if (samples > MaxClipKeys)
            {
                var inv = CultureInfo.InvariantCulture;
                notes.Add($"The animation was not converted: its tracks run {declared.ToString("N0", inv)} ms with a key every {interval.ToString("N0", inv)} ms, "
                    + $"{samples.ToString("N0", inv)} keys a bone, more than the {MaxClipKeys.ToString("N0", inv)} Cairn makes (the game's own tracks run about 2 seconds at most).");
                return null;
            }
            int span = declared <= 0 ? interval : (int)declared;
            var times = Times(span, interval);
            var rotations = bones.Select(_ => new List<RfaRotKey>(times.Count)).ToList();
            var positions = bones.Select(_ => new RfaPosKey[times.Count]).ToList();
            var worlds = new Rigid[bones.Count];
            for (int k = 0; k < times.Count; k++)
            {
                int elapsed = times[k];
                var matrices = scene.WorldMatrices(t => t.Sample(t.Start + (double)elapsed));
                int tick = Tick(elapsed);
                for (int b = 0; b < bones.Count; b++)
                {
                    worlds[b] = bones[b].Track is { } node ? RigidOf(matrices[node.Index], scale) : Rigid.Identity;
                    var local = bones[b].Parent < 0 ? worlds[b] : worlds[bones[b].Parent].Inverse().Compose(worlds[b]);
                    rotations[b].Add(ClipEdit.QuantizeRotation(tick, local.Rotation, rotations[b].Count > 0 ? rotations[b][^1] : null));
                    positions[b][k] = RfaPosKey.Constant(tick, local.Position);
                }
            }
            var tracksOut = ImmutableArray.CreateBuilder<RfaBoneTrack>(bones.Count);
            for (int b = 0; b < bones.Count; b++)
            {
                var keys = positions[b];
                // straight lines between keys, as the game moves positions: control points a third of the way along
                for (int k = 0; k + 1 < keys.Length; k++)
                {
                    var (p0, p1) = (keys[k].Position, keys[k + 1].Position);
                    keys[k] = keys[k] with { OutControl = p0 + (p1 - p0) / 3f };
                    keys[k + 1] = keys[k + 1] with { InControl = p1 + (p0 - p1) / 3f };
                }
                tracksOut.Add(new RfaBoneTrack(NewClipOptions.ForKind(NewClipKind.State).Weight, [.. rotations[b]], [.. keys]));
            }

            var ranges = tracks.Select(t => (t.Start, t.End)).Distinct().OrderBy(r => r.Start).ToList();
            notes.Add($"The animation became one looping clip, {clipName}: {span.ToString("N0", CultureInfo.InvariantCulture)} ms, {times.Count:N0} keys a bone, one every {interval:N0} ms "
                + $"(the game's key interval at {(tracks.FirstOrDefault(t => t.Interval == interval)?.Rate ?? 30f).ToString("0.#", CultureInfo.InvariantCulture)} keys a second), each bone's motion relative to its parent bone.");
            if (ranges.Count > 1)
                notes.Add($"The tracks run over different ranges ({string.Join("; ", ranges.Take(4).Select(r => $"{r.Start} to {r.End} ms"))}{(ranges.Count > 4 ? "; ..." : "")}): "
                    + "each was sampled from its own start on the same grid and held or wrapped as the game does, so they stay in step.");
            notes.Add("The N-Gage game has no other animations for its characters: it plays this one loop faster or slower with the character's speed. "
                + "Between keys the game moves positions in straight lines and turns rotations by an interpolation Cairn takes as spherical (the difference is under a degree).");
            return new RfaClip
            {
                Version = 8,
                StartTime = RfaClip.TicksPerFrame,
                EndTime = Tick(span),
                Bones = tracksOut.MoveToImmutable(),
                Morph = RfaMorph.Empty,
            };
        }

        /// <summary>The sample times in milliseconds from the start: 0, interval, 2 · interval… below <paramref name="span"/>, then <paramref name="span"/>.</summary>
        internal static List<int> Times(int span, int interval)
        {
            var times = new List<int>();
            for (long e = 0; e < span; e += interval) times.Add((int)e);
            times.Add(span);
            return times;
        }

        /// <summary>A time in milliseconds from the clip's start as Red Faction ticks (4,800 a second), the clip starting at tick 160 as stock clips do.</summary>
        internal static int Tick(int elapsed) => RfaClip.TicksPerFrame + (int)Math.Round(elapsed * (RfaClip.TicksPerSecond / 1000.0), MidpointRounding.AwayFromZero);

        // ── the notes ────────────────────────────────────────────────────────────────────────────────────

        private void Notes(int submeshes, List<BoneSource> bones, List<string> droppedHelpers, int unmoved, int overLimit, List<string> clipNotes)
        {
            var inv = CultureInfo.InvariantCulture;
            int triangles = _corners / 3;
            if (triangles == 0) _notes.Add("The scene holds no geometry.");
            _notes.Add($"Converted from a Red Faction for N-Gage scene: positions × {scale.ToString("0.###", inv)} to metres"
                + (scale == XffSpace.RfScale ? " (the size that makes the N-Gage player Red Faction's; measured, not documented)" : "")
                + ", the axes kept, and the front faces turned to Red Faction's side (each triangle's corners reordered and the normals reversed).");
            if (!character && submeshes > 0)
                _notes.Add($"{Plural(submeshes, "submesh", "submeshes")}, one for each mesh node of the scene.");
            if (_corners > 0)
                _notes.Add($"UVs are stored per triangle corner: {_corners.ToString("N0", inv)} corners became {_vertices.ToString("N0", inv)} vertices, one per distinct position, normal and UV{(character ? " (and bone)" : "")}.");
            if (_colours)
                _notes.Add("The scene's baked vertex lighting (a colour on each triangle corner) has no .v3m/.v3c equivalent and was dropped; Red Faction lights the mesh with the level's lights.");
            if (_palette.Count > 0)
            {
                int untextured = CountUntextured();
                _notes.Add(untextured == 1
                    ? $"1 untextured part is drawn in its vertex colours in the N-Gage game; it uses a solid colour instead (their average) from {_paletteName}, written beside the mesh."
                    : $"{untextured.ToString("N0", inv)} untextured parts are drawn in their vertex colours in the N-Gage game; they use solid colours instead (each part's average, {Plural(_palette.Count, "colour", "colours")}) from {_paletteName}, written beside the mesh.");
            }
            if (_texturedWithoutUvs > 0)
                _notes.Add($"{Plural(_texturedWithoutUvs, "submesh names", "submeshes name")} a texture but {(_texturedWithoutUvs == 1 ? "has" : "have")} no UVs; {(_texturedWithoutUvs == 1 ? "it was" : "they were")} drawn in a solid colour.");
            if (_blends.GetValueOrDefault(Blend.Alpha) is int alpha and > 0)
                _notes.Add($"{Plural(alpha, "submesh is", "submeshes are")} alpha-blended (source alpha, inverse source alpha): {(alpha == 1 ? "it is" : "they are")} see-through materials whose transparency comes from the texture's alpha"
                    + (_blendedAlpha > 0 ? "; the N-Gage blend took its alpha from the vertex colours, which were dropped, so a texture without alpha draws opaque." : "."));
            if (_blends.GetValueOrDefault(Blend.Additive) is int additive and > 0)
                _notes.Add($"{Plural(additive, "submesh is", "submeshes are")} additive (one, one): {(additive == 1 ? "its batches use" : "their batches use")} the additive render state.");
            if (_blends.GetValueOrDefault(Blend.Darken) is int darken and > 0)
                _notes.Add($"{Plural(darken, "submesh uses", "submeshes use")} a darkening blend (zero, inverse source colour), which a .v3m cannot hold; {(darken == 1 ? "it was" : "they were")} made alpha-blended.");
            if (_blends.GetValueOrDefault(Blend.Other) is int other and > 0)
                _notes.Add($"{Plural(other, "submesh uses", "submeshes use")} blend factors Cairn does not know ({string.Join(", ", _otherPairs.Select(p => $"{p.Item1}, {p.Item2}"))}); {(other == 1 ? "it was" : "they were")} made opaque.");
            if (scene.Materials.Count > 0)
                _notes.Add("The material records hold colours and settings whose meaning is unknown; only each texture and its blend were kept. Every face is single-sided, as in the N-Gage files (they hold reversed copies where both sides show).");
            var renamed = _textures.Values.Where(t => t.IsRenamed).ToList();
            if (renamed.Count > 0)
                _notes.Add($"{Plural(renamed.Count, "texture name is", "texture names are")} longer than a material's 31 characters and {(renamed.Count == 1 ? "was" : "were")} shortened (the copy is written under the new name): {List(renamed.Select(t => $"{t.SourceName} → {t.StoredName}"))}.");
            if (_skipped > 0) _notes.Add($"{Plural(_skipped, "triangle has", "triangles have")} a corner that is not a number and {(_skipped == 1 ? "was" : "were")} left out.");
            if (_noNormals > 0) _notes.Add($"{Plural(_noNormals, "submesh has", "submeshes have")} no normals; each face's own normal was used.");
            if (!character && scene.Tracks.Count > 0 && scene.Meshes.Any(m => m.Node.IsAnimated))
                _notes.Add($"The scene has {Plural(scene.Tracks.Count, "animation track", "animation tracks")}; a .v3m does not move, so the mesh is in the pose at the tracks' start.");
            if (character)
            {
                int trackBones = bones.Count(b => b.Track is not null);
                _notes.Add($"The character's parts became {Plural(trackBones, "bone", "bones")}, one for each animated part{(unmoved > 0 ? ", and a bone '" + RootBoneName + "' they hang from" : "")}; "
                    + "each part's vertices are weighted fully to its bone (the N-Gage meshes are rigid parts, not skinned). The bind pose is the pose at the start of the animation.");
                var effects = scene.Meshes.Where(m => !Drawn(m) && m.Submeshes.Any(s => s.TriangleCount > 0)).Select(m => m.Node.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (effects.Count > 0)
                    _notes.Add($"{List(effects)}, which the N-Gage game draws only while the character's nano shield is up, {(effects.Count == 1 ? "was" : "were")} left out: a .v3c cannot hide a part.");
                if (unmoved > 0) _notes.Add($"{Plural(unmoved, "part does", "parts do")} not move with any track; {(unmoved == 1 ? "it is" : "they are")} weighted to the bone '{RootBoneName}'.");
                if (droppedHelpers.Count > 0)
                    _notes.Add($"{Plural(droppedHelpers.Count, "animated helper", "animated helpers")} without geometry {(droppedHelpers.Count == 1 ? "was" : "were")} left out ({List(droppedHelpers)}).");
                if (overLimit > 0)
                    _notes.Add($"{Plural(overLimit, "animated part", "animated parts")} beyond the {V3dBoneSection.MaxBones} bones a character holds {(overLimit == 1 ? "moves" : "move")} with the bone above {(overLimit == 1 ? "it" : "them")} instead.");
                _notes.AddRange(clipNotes);
                _notes.Add("This is a new character: its skeleton does not fit any of Red Faction's stock skeletons, so Red Faction's animations do not play on it, and its own clip plays only on it.");
            }
            if (_truncated > 0) _notes.Add($"{Plural(_truncated, "name was", "names were")} cut to fit the PC format's name fields.");
            if (scene.Problems.Count > 0)
                _notes.Add($"Reading the scene found {Plural(scene.Problems.Count, "problem", "problems")}; what did not resolve was left out: {List(scene.Problems.Take(3))}{(scene.Problems.Count > 3 ? " ..." : "")}");
        }

        private int CountUntextured() => scene.Meshes.Where(Drawn).Sum(m => m.Submeshes.Count(s => s.TriangleCount > 0 && !IsTextured(s)));

        /// <summary>False for a character's effect part (<see cref="EffectParts"/>), which the conversion leaves out.</summary>
        private bool Drawn(XffSceneMesh mesh) => !character || !EffectParts.Contains(mesh.Node.Name, StringComparer.OrdinalIgnoreCase);
    }
}
