using System.Buffers.Binary;
using System.Collections.Immutable;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>What converting a legacy mesh made: the compiled mesh, its bytes and the report.</summary>
/// <param name="SourceName">The legacy file's name.</param>
/// <param name="OutputName">The compiled file's name (same stem, <c>.v3m</c> or <c>.v3c</c>).</param>
/// <param name="Mesh">The compiled mesh.</param>
/// <param name="Bytes">The compiled file.</param>
/// <param name="Report">
/// What was approximated or left out (the reader's notes, then the compiled mesh's problems), in plain
/// sentences; empty for a lossless conversion.
/// </param>
/// <param name="TwinName">The same-named exporter file converted in place of a PS2 mesh, or null.</param>
public sealed record LegacyMeshConversion(
    string SourceName, string OutputName, V3dFile Mesh, byte[] Bytes, ImmutableArray<string> Report, string? TwinName = null)
{
    /// <summary>True when nothing was approximated.</summary>
    public bool IsLossless => Report.IsEmpty;

    /// <summary>
    /// Files written beside the mesh (a Red Faction II mesh converted with its original texture names: its textures
    /// from the <c>.peg</c> packs, as <c>.tga</c>; a Red Faction II character over the texture limit: its atlases); empty
    /// otherwise.
    /// </summary>
    public ImmutableArray<(string Name, byte[] Bytes)> Textures { get; init; } = [];

    /// <summary>An N-Gage character's animation, converted with it to one Red Faction clip (<c>&lt;name&gt;_walk.rfa</c>); null otherwise.</summary>
    public Rf2ClipConversion? Clip { get; init; }

    /// <summary>The mesh, its <see cref="Clip"/> and its <see cref="Textures"/>, in writing order.</summary>
    public IReadOnlyList<(string Name, byte[] Bytes)> AllFiles =>
        Clip is { } clip ? [(OutputName, Bytes), (clip.OutputName, clip.Bytes), .. Textures] : [(OutputName, Bytes), .. Textures];
}

/// <summary>What a Red Faction II mesh's conversion does with its texture names.</summary>
public enum Rf2TextureMode
{
    /// <summary>
    /// Translate each RF2 texture name to its Red Faction name (REDUX's tables, <see cref="Cairn.Formats.Rf2.Rf2TextureNames"/>);
    /// names without a translation stay as they are. The default.
    /// </summary>
    Translate,

    /// <summary>Keep the RF2 names and write the textures from the <c>.peg</c> packs beside the converted mesh (<c>.tga</c>).</summary>
    KeepOriginals,
}

/// <summary>Options of a legacy mesh conversion (they only change Red Faction II meshes).</summary>
public sealed record LegacyConvertOptions
{
    /// <summary>Translate RF2 names, no textures written.</summary>
    public static LegacyConvertOptions Default { get; } = new();

    /// <summary>What happens to RF2 texture names.</summary>
    public Rf2TextureMode Rf2Textures { get; init; } = Rf2TextureMode.Translate;

    /// <summary>
    /// The textures beside the source (its <c>.peg</c> packs, see <see cref="LegacyMeshSupport.TexturePacksFor"/>), for
    /// <see cref="Rf2TextureMode.KeepOriginals"/> and for the atlas of a character over the texture limit (in either
    /// mode; read only then); null when there are none.
    /// </summary>
    public PegTextureSet? TexturePacks { get; init; }

    /// <summary>Red Faction for N-Gage scenes: Red Faction metres per XFF unit (<see cref="Cairn.Formats.XForge.XffSpace.RfScale"/>, 2.35, by default).</summary>
    public float XffScale { get; init; } = Cairn.Formats.XForge.XffSpace.RfScale;

    /// <summary>Red Faction for N-Gage scenes: copy the textures the mesh names, unchanged, from its archive or folder beside the converted mesh (on by default).</summary>
    public bool XffWriteTextures { get; init; } = true;
}

/// <summary>
/// The one place the legacy mesh formats are registered, recognised by content and converted to the PC
/// formats. <c>.v3d</c> and <c>.v3m</c> share a header, so a file is identified by its structure, not
/// only its name: an exporter mesh misnamed <c>.v3m</c> is still read as one. The PlayStation 2 and Red
/// Faction II meshes share their magic and extensions; the version field tells them apart.
/// </summary>
public static class LegacyMeshSupport
{
    private static int s_registered;

    /// <summary>The legacy extensions, lower case with the dot.</summary>
    public static ImmutableArray<string> Extensions { get; } = [".v3d", ".vcm", ".rfm", ".rfc", ".xff"];

    /// <summary>Magic of the PlayStation 2 meshes (<c>12 87 12 87</c>), which Red Faction II's meshes share.</summary>
    public const uint Ps2Magic = 0x87128712;

    /// <summary>True when the bytes start like a Red Faction II mesh (any version, the prototypes Cairn refuses included).</summary>
    public static bool IsRedFactionII(ReadOnlySpan<byte> data) => Rf2MeshParser.Rf2VersionOf(data) is not null;

    /// <summary>
    /// The skeleton of a Red Faction II character mesh in index order, as its conversion stores it (names, inverse bind
    /// transforms, parents), read from the start of the file only (no geometry); null when the bytes are not a readable
    /// one with bones. Cheap enough to rank many meshes for a clip.
    /// </summary>
    public static ImmutableArray<V3dBone>? Rf2Skeleton(ReadOnlySpan<byte> data) =>
        Rf2MeshParser.TryReadBones(data, out var bones)
            ? [.. bones.Select(b => new V3dBone(FixedString.FromText(BoneName(b.Name), V3dBone.NameSize), Rf2MeshConverter.BoneRotation(b.Rotation), b.Translation, b.Parent))]
            : null;

    // the name as the conversion fits it: Latin-1, cut to the field
    private static string BoneName(string name)
    {
        string clean = new([.. name.Select(c => c == 0 || c > 255 ? '_' : c)]);
        return clean.Length < V3dBone.NameSize ? clean : clean[..(V3dBone.NameSize - 1)];
    }

    /// <summary>
    /// The pack suffixes a Red Faction II mesh's textures are looked for under, best first
    /// (<c>&lt;mesh&gt;_v3d.peg</c> for a static mesh, <c>&lt;mesh&gt;_vcm.peg</c> for a character, then
    /// <c>&lt;mesh&gt;.peg</c>); see <see cref="PegTextureSet.SearchOrder"/>.
    /// </summary>
    public static IReadOnlyList<string> TexturePackSuffixes(bool character) => character ? ["_vcm", ""] : ["_v3d", ""];

    /// <summary>
    /// The texture packs of a Red Faction II mesh among <paramref name="siblingNames"/> (the names beside it in its folder or
    /// packfile), in the order its textures are looked for, read through <paramref name="read"/>.
    /// </summary>
    /// <param name="meshName">The mesh's file name.</param>
    /// <param name="character">True for a character (.rfc).</param>
    /// <param name="siblingNames">The files beside the mesh.</param>
    /// <param name="read">Reads one of them by name (null when gone).</param>
    /// <param name="label">Where they are, for messages.</param>
    public static PegTextureSet TexturePacksFor(string meshName, bool character, IEnumerable<string> siblingNames, Func<string, byte[]?> read, string label)
    {
        ArgumentNullException.ThrowIfNull(meshName);
        ArgumentNullException.ThrowIfNull(siblingNames);
        ArgumentNullException.ThrowIfNull(read);
        var order = PegTextureSet.SearchOrder(Path.GetFileName(meshName), siblingNames, TexturePackSuffixes(character));
        return new PegTextureSet(label, order.Select(n => new PegPack(n, () => read(n))));
    }

    /// <summary>Registers every legacy format with <see cref="LegacyMeshFormats"/> (once; later calls do nothing).</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1) return;
        LegacyMeshFormats.Register(new ExporterV3dFormat());
        LegacyMeshFormats.Register(new ExporterVcmFormat());
        LegacyMeshFormats.Register(new RfmFormat());
        LegacyMeshFormats.Register(new RfcFormat());
        LegacyMeshFormats.Register(new XffMeshFormat());
    }

    /// <summary>True when <paramref name="name"/> is a Red Faction for N-Gage scene's (<c>.xff</c>: a model or character opens here, a level in the Levels module).</summary>
    public static bool IsXffName(string? name) => Cairn.Formats.XForge.XffFile.HasExtension(name);

    /// <summary>True when <paramref name="name"/> has one of the legacy extensions.</summary>
    public static bool IsLegacyName(string? name) =>
        !string.IsNullOrEmpty(name) && Extensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>
    /// The legacy format the bytes hold, by content (an exporter layout behind an "RF3D"/"RFCM" header, or the
    /// PS2 magic), else by the name's extension; null for anything else (a compiled .v3m/.v3c among them).
    /// </summary>
    public static ILegacyMeshFormat? Identify(ReadOnlySpan<byte> data, string name)
    {
        EnsureRegistered();
        if (data.Length >= 8)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (magic is V3dHeader.StaticSignature or V3dHeader.CharacterSignature)
            {
                if (!ExporterMeshReader.IsExporterLayout(data)) return null;
                return LegacyMeshFormats.For(magic == V3dHeader.CharacterSignature ? ".vcm" : ".v3d");
            }
            if (magic == Ps2Magic)
            {
                var named = LegacyMeshFormats.For(Path.GetExtension(name));
                if (named is not null && named.Extension is ".rfm" or ".rfc" && named.Recognises(data)) return named;
                foreach (string ext in new[] { ".rfm", ".rfc" })
                {
                    if (LegacyMeshFormats.For(ext) is { } f && f.Recognises(data)) return f;
                }
                return named is { Extension: ".rfm" or ".rfc" } ? named : LegacyMeshFormats.For(".rfm");
            }
        }
        return LegacyMeshFormats.For(Path.GetExtension(name));
    }

    /// <summary>True when the bytes are a legacy mesh by content (see <see cref="Identify"/>), whatever the name.</summary>
    public static bool IsLegacyContent(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return magic == Ps2Magic
            || (magic is V3dHeader.StaticSignature or V3dHeader.CharacterSignature && ExporterMeshReader.IsExporterLayout(data));
    }

    /// <summary>Reads a legacy mesh. Never throws for malformed input.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, string name, out LegacyMesh? mesh, out LegacyMeshError? error)
    {
        mesh = null;
        var format = Identify(data, name);
        if (format is null)
        {
            error = new LegacyMeshError($"'{name}' is not an exporter (.v3d, .vcm), PlayStation 2 or Red Faction II (.rfm, .rfc) mesh.");
            return false;
        }
        try
        {
            return format.TryRead(data, name, out mesh, out error);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            error = new LegacyMeshError($"'{name}' could not be read: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads a legacy mesh.</summary>
    /// <exception cref="AssetFormatException">It cannot be read; the message says why.</exception>
    public static LegacyMesh Read(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (TryRead(data, name, out var mesh, out var error)) return mesh!;
        throw new AssetFormatException(error?.Message ?? $"'{name}' could not be read.");
    }

    /// <summary>The PC extension a legacy mesh converts to: ".v3c" for characters, ".v3m" for static meshes.</summary>
    public static string TargetExtension(LegacyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return mesh.Description.Kind == V3dKind.Character ? ".v3c" : ".v3m";
    }

    /// <summary>
    /// The build options for a legacy mesh: an exporter character's weights are stored bytes already and are
    /// kept byte for byte; a PS2 character's (sixteenths) are normalised to bytes summing to 255.
    /// </summary>
    public static V3dBuildOptions BuildOptions(LegacyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return mesh.SourceFormat is "vcm" or "v3d" ? V3dBuildOptions.Preserve : V3dBuildOptions.Default;
    }

    /// <summary>Compiles a read legacy mesh (for viewing): the <see cref="V3dFile"/> a conversion writes.</summary>
    /// <exception cref="AssetFormatException">The mesh cannot be compiled (a limit the PC format cannot hold).</exception>
    public static V3dFile Compile(LegacyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        try
        {
            return V3dBuilder.Build(mesh.Description, BuildOptions(mesh));
        }
        catch (ArgumentException ex)
        {
            throw new AssetFormatException($"The mesh cannot be converted: {ex.Message}");
        }
    }

    /// <summary>Reads and compiles a legacy mesh in one step (documents, previews).</summary>
    /// <exception cref="AssetFormatException">It cannot be read or compiled; the message says why.</exception>
    public static (LegacyMesh Mesh, V3dFile Compiled) ReadCompiled(byte[] data, string name)
    {
        var mesh = Read(data, name);
        return (mesh, Compile(mesh));
    }

    /// <summary>
    /// Converts a legacy mesh file to its PC form. A PS2 mesh (<c>.rfm</c>/<c>.rfc</c>) whose same-named exporter
    /// file (<c>.v3d</c>/<c>.vcm</c>) is among <paramref name="sibling"/>'s files is converted from that file
    /// instead, which keeps the names and the welded vertices the PS2 build lost; the report says so.
    /// </summary>
    /// <param name="data">The legacy file.</param>
    /// <param name="name">Its file name.</param>
    /// <param name="sibling">Reads another file of the same folder or packfile by name (null when there is none), or null.</param>
    /// <param name="options">What happens to a Red Faction II mesh's textures; null for <see cref="LegacyConvertOptions.Default"/>.</param>
    /// <exception cref="AssetFormatException">The file cannot be read or compiled; the message says why.</exception>
    public static LegacyMeshConversion Convert(byte[] data, string name, Func<string, byte[]?>? sibling = null, LegacyConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(name);
        var format = Identify(data, name);
        // an N-Gage scene is read again at the chosen scale; its textures come from the files beside it
        if (format is XffMeshFormat)
            return XffMeshConverter.Finish(XffMeshConverter.Convert(data, name, (options ?? LegacyConvertOptions.Default).XffScale), name, sibling, options ?? LegacyConvertOptions.Default);
        if (format?.Extension is ".rfm" or ".rfc" && sibling is not null && !IsRedFactionII(data))
        {
            string twinName = Path.ChangeExtension(Path.GetFileName(name), format.Extension == ".rfc" ? ".vcm" : ".v3d");
            byte[]? twin = null;
            try { twin = sibling(twinName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException) { twin = null; }
            if (twin is not null && TryRead(twin, twinName, out var twinMesh, out _) && twinMesh!.SourceFormat is "v3d" or "vcm")
            {
                var converted = Convert(twinMesh, name);
                string note = $"Converted from {twinName}, the same mesh's exporter file found beside it, which keeps the submesh names and the exact geometry.";
                return converted with { TwinName = twinName, Report = converted.Report.Insert(0, note) };
            }
        }
        return Convert(Read(data, name), name, options);
    }

    /// <summary>
    /// Converts a read legacy mesh: compiles it, writes the bytes and gathers the report. A Red Faction II mesh's
    /// texture names are translated or kept (with the textures written beside it) as <paramref name="options"/> say.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="sourceName">The legacy file's name (the output takes its stem).</param>
    /// <param name="options">What happens to a Red Faction II mesh's textures; null for <see cref="LegacyConvertOptions.Default"/>.</param>
    /// <exception cref="AssetFormatException">The mesh cannot be compiled, or a Red Faction II character's textures cannot be brought within the limit; the message says why.</exception>
    public static LegacyMeshConversion Convert(LegacyMesh mesh, string sourceName, LegacyConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        // an N-Gage scene read already: as it was read (its scale), with no files to copy its textures from
        if (mesh.Xff is not null) return XffMeshConverter.Finish(mesh, sourceName, null, options ?? LegacyConvertOptions.Default);
        var textureNotes = new List<string>();
        var textures = ImmutableArray<(string, byte[])>.Empty;
        if (mesh.IsRedFactionII) (mesh, textureNotes, textures) = Rf2MeshTextures.Apply(mesh, options ?? LegacyConvertOptions.Default, sourceName);
        var compiled = Compile(mesh);
        byte[] bytes = V3dWriter.Write(compiled);
        string output = Path.ChangeExtension(Path.GetFileName(sourceName), TargetExtension(mesh));
        var report = ImmutableArray.CreateBuilder<string>();
        report.AddRange(mesh.Notes);
        report.AddRange(textureNotes);
        foreach (var d in MeshLinter.Analyze(compiled, new MeshLintContext { FileName = output }))
        {
            if (d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                report.Add($"{output}: {d.Message}");
        }
        return new LegacyMeshConversion(Path.GetFileName(sourceName), output, compiled, bytes, report.ToImmutable()) { Textures = textures };
    }

    /// <summary>
    /// A one-line description for lists ("exporter mesh: 2 submeshes, 136 triangles, 2 LODs"), or the reason it
    /// cannot be read.
    /// </summary>
    public static string Describe(LegacyMesh mesh) => FormatName(mesh) + ": " + Summary(mesh!);

    /// <summary>"2 submeshes, 136 triangles, 2 LODs" (characters: bones instead of submeshes).</summary>
    public static string Summary(LegacyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var d = mesh.Description;
        var parts = new List<string>();
        parts.Add(d.Kind == V3dKind.Character ? Count(d.Bones.Length, "bone") : Count(d.Submeshes.Length, "submesh", "submeshes"));
        int triangles = d.Submeshes.Sum(s => s.Lods.IsDefaultOrEmpty ? 0 : s.Lods[0].Groups.Sum(g => g.Triangles.Length));
        parts.Add(Count(triangles, "triangle"));
        int lods = d.Submeshes.Length == 0 ? 0 : d.Submeshes.Max(s => s.Lods.Length);
        if (lods > 1) parts.Add(Count(lods, "LOD"));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// "Exporter mesh", "PS2 mesh" and so on, for banners and lists. The extension alone cannot tell a PlayStation 2
    /// mesh from a Red Faction II one: pass <paramref name="redFactionII"/>, or use <see cref="FormatName(LegacyMesh)"/>.
    /// </summary>
    public static string FormatName(string sourceFormat, bool redFactionII = false) => sourceFormat.ToLowerInvariant() switch
    {
        "v3d" => "exporter static mesh",
        "vcm" => "exporter character mesh",
        "rfm" => redFactionII ? "Red Faction II static mesh" : "PS2 static mesh",
        "rfc" => redFactionII ? "Red Faction II character mesh" : "PS2 character mesh",
        "xff" => "Red Faction for N-Gage scene",
        _ => "legacy mesh",
    };

    /// <summary>The format name of a read mesh ("Red Faction II character mesh", "PS2 static mesh", "N-Gage character"...).</summary>
    public static string FormatName(LegacyMesh mesh) => mesh?.Xff is { } xff
        ? (xff.Facts.Kind == Cairn.Formats.XForge.XffKind.Character ? "Red Faction for N-Gage character" : "Red Faction for N-Gage model")
        : FormatName(mesh?.SourceFormat ?? "", mesh?.IsRedFactionII == true);

    private static string Count(int n, string one, string? many = null) =>
        n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many ?? one + "s");
}
