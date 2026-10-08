using System.Collections.Immutable;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// A mesh read from a format Cairn reads and converts but never writes: the 3ds Max exporter's
/// uncompiled meshes (<c>.v3d</c>, <c>.vcm</c>) and the PlayStation 2 build tool's compiled meshes
/// (<c>.rfm</c>, <c>.rfc</c>). <see cref="Description"/> feeds <see cref="V3dBuilder.Build"/> to make
/// the PC <c>.v3m</c> or <c>.v3c</c>.
/// </summary>
/// <param name="SourceFormat">The source extension without the dot, e.g. "v3d" or "rfc".</param>
/// <param name="Description">The geometry, skeleton, spheres and prop points, ready for the builder.</param>
/// <param name="Notes">
/// What the conversion approximates or leaves out (made-up submesh names, re-joined vertices,
/// reduced weight precision and so on), in plain words for the conversion report.
/// </param>
public sealed record LegacyMesh(string SourceFormat, V3dMeshDescription Description, ImmutableArray<string> Notes);

/// <summary>Why a legacy mesh could not be read.</summary>
/// <param name="Message">A plain sentence for the Problems list, e.g. "Red Faction II mesh (version 0x114): not supported".</param>
/// <param name="Offset">The byte offset where reading stopped, when known.</param>
public sealed record LegacyMeshError(string Message, long? Offset = null);

/// <summary>One legacy mesh format: recognises and reads its files.</summary>
public interface ILegacyMeshFormat
{
    /// <summary>The extension this format reads, with the dot, lower case (".v3d", ".vcm", ".rfm", ".rfc").</summary>
    string Extension { get; }

    /// <summary>True when the bytes look like this format (magic and version), without reading the rest.</summary>
    bool Recognises(ReadOnlySpan<byte> head);

    /// <summary>Reads a whole file. Never throws for malformed input: returns an error instead.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="name">The file name, for messages and made-up submesh names.</param>
    /// <param name="mesh">The mesh when reading succeeded.</param>
    /// <param name="error">Why reading failed.</param>
    bool TryRead(ReadOnlySpan<byte> data, string name, out LegacyMesh? mesh, out LegacyMeshError? error);
}

/// <summary>
/// The legacy mesh formats Cairn knows. Readers register here once; the meshes module, the packfile
/// previews and the converters look formats up by extension.
/// </summary>
public static class LegacyMeshFormats
{
    private static readonly object Gate = new();
    private static ImmutableDictionary<string, ILegacyMeshFormat> s_formats =
        ImmutableDictionary.Create<string, ILegacyMeshFormat>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every registered format, keyed by extension (with the dot).</summary>
    public static IReadOnlyDictionary<string, ILegacyMeshFormat> All => s_formats;

    /// <summary>Adds or replaces the reader for <see cref="ILegacyMeshFormat.Extension"/>.</summary>
    public static void Register(ILegacyMeshFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        lock (Gate) s_formats = s_formats.SetItem(format.Extension, format);
    }

    /// <summary>The reader for an extension (with or without the dot), or null.</summary>
    public static ILegacyMeshFormat? For(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        string key = extension[0] == '.' ? extension : "." + extension;
        return s_formats.TryGetValue(key, out var format) ? format : null;
    }
}
