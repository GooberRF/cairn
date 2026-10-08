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
}

/// <summary>
/// The one place the legacy mesh formats are registered, recognised by content and converted to the PC
/// formats. <c>.v3d</c> and <c>.v3m</c> share a header, so a file is identified by its structure, not
/// only its name: an exporter mesh misnamed <c>.v3m</c> is still read as one.
/// </summary>
public static class LegacyMeshSupport
{
    private static int s_registered;

    /// <summary>The legacy extensions, lower case with the dot.</summary>
    public static ImmutableArray<string> Extensions { get; } = [".v3d", ".vcm", ".rfm", ".rfc"];

    /// <summary>Magic of the PlayStation 2 meshes (<c>12 87 12 87</c>).</summary>
    public const uint Ps2Magic = 0x87128712;

    /// <summary>Registers every legacy format with <see cref="LegacyMeshFormats"/> (once; later calls do nothing).</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1) return;
        LegacyMeshFormats.Register(new ExporterV3dFormat());
        LegacyMeshFormats.Register(new ExporterVcmFormat());
        LegacyMeshFormats.Register(new RfmFormat());
        LegacyMeshFormats.Register(new RfcFormat());
    }

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
            error = new LegacyMeshError($"'{name}' is not an exporter (.v3d, .vcm) or PlayStation 2 (.rfm, .rfc) mesh.");
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
    /// <exception cref="AssetFormatException">The file cannot be read or compiled; the message says why.</exception>
    public static LegacyMeshConversion Convert(byte[] data, string name, Func<string, byte[]?>? sibling = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(name);
        var format = Identify(data, name);
        if (format?.Extension is ".rfm" or ".rfc" && sibling is not null)
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
        return Convert(Read(data, name), name);
    }

    /// <summary>Converts a read legacy mesh: compiles it, writes the bytes and gathers the report.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="sourceName">The legacy file's name (the output takes its stem).</param>
    /// <exception cref="AssetFormatException">The mesh cannot be compiled; the message says why.</exception>
    public static LegacyMeshConversion Convert(LegacyMesh mesh, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var compiled = Compile(mesh);
        byte[] bytes = V3dWriter.Write(compiled);
        string output = Path.ChangeExtension(Path.GetFileName(sourceName), TargetExtension(mesh));
        var report = ImmutableArray.CreateBuilder<string>();
        report.AddRange(mesh.Notes);
        foreach (var d in MeshLinter.Analyze(compiled, new MeshLintContext { FileName = output }))
        {
            if (d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                report.Add($"{output}: {d.Message}");
        }
        return new LegacyMeshConversion(Path.GetFileName(sourceName), output, compiled, bytes, report.ToImmutable());
    }

    /// <summary>
    /// A one-line description for lists ("exporter mesh: 2 submeshes, 136 triangles, 2 LODs"), or the reason it
    /// cannot be read.
    /// </summary>
    public static string Describe(LegacyMesh mesh) => FormatName(mesh?.SourceFormat ?? "") + ": " + Summary(mesh!);

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

    /// <summary>"Exporter mesh", "PS2 mesh" and so on, for banners and lists.</summary>
    public static string FormatName(string sourceFormat) => sourceFormat.ToLowerInvariant() switch
    {
        "v3d" => "exporter static mesh",
        "vcm" => "exporter character mesh",
        "rfm" => "PS2 static mesh",
        "rfc" => "PS2 character mesh",
        _ => "legacy mesh",
    };

    private static string Count(int n, string one, string? many = null) =>
        n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many ?? one + "s");
}
