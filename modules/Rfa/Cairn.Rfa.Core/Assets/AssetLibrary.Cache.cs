using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Vpp;
using Cairn.Workspace;

namespace Cairn.Rfa.Assets;

/// <summary>A probe result as the library caches it: for one file or one archive entry.</summary>
/// <param name="Name">File or entry name.</param>
/// <param name="Offset">Entry offset inside its archive (0 for a loose file).</param>
/// <param name="Size">Entry size in bytes (the file size for a loose file).</param>
/// <param name="Clip">Clip facts, for a readable .rfa.</param>
/// <param name="Mesh">Mesh facts, for a readable .v3c / .v3m.</param>
/// <param name="Error">Why the file could not be read, or null.</param>
internal sealed record LibraryProbe(string Name, long Offset, int Size, RfaProbeResult? Clip, V3dProbeResult? Mesh, string? Error);

/// <summary>
/// An archive's indexed entries, valid while the archive's last-write time and size are unchanged, and
/// its whole directory (every entry, not only the indexed ones) when known, so the resolver can answer
/// lookups in it — a texture's supersede probes, a table — without reading the archive.
/// </summary>
internal sealed record LibraryArchiveCache(DateTime Time, long Size, ImmutableArray<LibraryProbe> Entries, VppArchive? Directory = null);

/// <summary>A loose file's probe, valid while its last-write time and size are unchanged.</summary>
internal sealed record LibraryFileCache(DateTime Time, long Size, LibraryProbe Probe);

/// <summary>
/// The persistent probe cache: a JSON file the caller places (the app uses
/// <c>%LOCALAPPDATA%\Cairn\library-cache.json</c>). Archives are keyed by path and validated by
/// their own (last-write time, size): a matching archive's entry list and every entry's facts are
/// taken as they are, without opening the archive. Loose files are keyed by path, time and size.
/// A missing, corrupt or other-version file is simply ignored.
/// </summary>
internal static class LibraryCacheFile
{
    /// <summary>Bump when the stored shape or the probes change meaning.</summary>
    public const int FormatVersion = 2;

    public static (Dictionary<string, LibraryArchiveCache> Archives, Dictionary<string, LibraryFileCache> Files) Load(string path)
    {
        var archives = new Dictionary<string, LibraryArchiveCache>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, LibraryFileCache>(StringComparer.OrdinalIgnoreCase);
        byte[] bytes = File.ReadAllBytes(path);
        var dto = JsonSerializer.Deserialize(bytes, LibraryCacheJsonContext.Default.CacheDto);
        if (dto is null || dto.Version != FormatVersion) return (archives, files);
        foreach (var a in dto.Archives ?? [])
        {
            if (a?.Path is null) continue;
            VppArchive? directory = null;
            if (a.Names is { } names && a.Sizes is { } sizes)
            {
                try { directory = VppArchive.FromDirectory(a.Path, a.VppVersion, names, sizes); }
                catch (VppFormatException) { directory = null; }
            }
            archives[a.Path] = new LibraryArchiveCache(new DateTime(a.Time, DateTimeKind.Utc), a.Size,
                [.. (a.Entries ?? []).Select(FromDto).OfType<LibraryProbe>()], directory);
        }
        foreach (var f in dto.Files ?? [])
        {
            if (f?.Path is null || FromDto(f.Probe) is not { } probe) continue;
            files[f.Path] = new LibraryFileCache(new DateTime(f.Time, DateTimeKind.Utc), f.Size, probe);
        }
        return (archives, files);
    }

    public static void Save(string path, IReadOnlyDictionary<string, LibraryArchiveCache> archives, IReadOnlyDictionary<string, LibraryFileCache> files)
    {
        var dto = new CacheDto
        {
            Version = FormatVersion,
            Archives = [.. archives.Select(p => new ArchiveDto
            {
                Path = p.Key, Time = p.Value.Time.Ticks, Size = p.Value.Size, Entries = [.. p.Value.Entries.Select(ToDto)],
                VppVersion = p.Value.Directory?.Version ?? 0,
                Names = p.Value.Directory is { } d ? [.. d.Entries.Select(e => e.Name)] : null,
                Sizes = p.Value.Directory is { } s ? [.. s.Entries.Select(e => e.Size)] : null,
            })],
            Files = [.. files.Select(p => new FileDto { Path = p.Key, Time = p.Value.Time.Ticks, Size = p.Value.Size, Probe = ToDto(p.Value.Probe) })],
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(dto, LibraryCacheJsonContext.Default.CacheDto);
        AtomicFile.WriteAllBytes(path, json);
    }

    private static ProbeDto ToDto(LibraryProbe p) => new()
    {
        Name = p.Name,
        Offset = p.Offset,
        Size = p.Size,
        Error = p.Error,
        Clip = p.Clip is { } c ? new ClipDto
        {
            Version = c.Version, PosReduction = c.PosReduction, RotReduction = c.RotReduction, Start = c.StartTime, End = c.EndTime,
            Bones = c.BoneCount, MorphVertices = c.MorphVertexCount, MorphKeyframes = c.MorphKeyframeCount,
            RampIn = c.RampIn, RampOut = c.RampOut,
            TotalRotation = [c.TotalRotation.X, c.TotalRotation.Y, c.TotalRotation.Z, c.TotalRotation.W],
            TotalTranslation = [c.TotalTranslation.X, c.TotalTranslation.Y, c.TotalTranslation.Z],
        } : null,
        Mesh = p.Mesh is { } m ? new MeshDto
        {
            Character = m.Kind == V3dKind.Character, Submeshes = [.. m.SubmeshNames], Lods = [.. m.LodCounts],
            BoneNames = [.. m.BoneNames], BoneParents = [.. m.BoneParents], Spheres = m.CollisionSphereCount,
            Readable = m.StructureReadable,
        } : null,
    };

    private static LibraryProbe? FromDto(ProbeDto? p)
    {
        if (p?.Name is null) return null;
        RfaProbeResult? clip = null;
        if (p.Clip is { } c)
        {
            var r = c.TotalRotation is { Length: 4 } q ? new Quaternion(q[0], q[1], q[2], q[3]) : Quaternion.Identity;
            var t = c.TotalTranslation is { Length: 3 } v ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
            clip = new RfaProbeResult(c.Version, c.PosReduction, c.RotReduction, c.Start, c.End, c.Bones, c.MorphVertices,
                c.MorphKeyframes, c.RampIn, c.RampOut, r, t);
        }
        V3dProbeResult? mesh = null;
        if (p.Mesh is { } m)
        {
            var names = (m.BoneNames ?? []).ToImmutableArray();
            var parents = (m.BoneParents ?? []).ToImmutableArray();
            if (parents.Length != names.Length) return null;
            mesh = new V3dProbeResult(m.Character ? V3dKind.Character : V3dKind.StaticMesh,
                [.. m.Submeshes ?? []], [.. m.Lods ?? []], names, parents, m.Spheres, m.Readable);
        }
        if (clip is null && mesh is null && p.Error is null) return null;
        return new LibraryProbe(p.Name, p.Offset, p.Size, clip, mesh, p.Error);
    }

    // ── JSON shapes ──────────────────────────────────────────────────────────

    internal sealed class CacheDto
    {
        public int Version { get; set; }
        public List<ArchiveDto>? Archives { get; set; }
        public List<FileDto>? Files { get; set; }
    }

    internal sealed class ArchiveDto
    {
        public string? Path { get; set; }
        public long Time { get; set; }
        public long Size { get; set; }
        public List<ProbeDto>? Entries { get; set; }
        public uint VppVersion { get; set; }
        /// <summary>Every entry name in directory order (the offsets follow from the sizes).</summary>
        public string[]? Names { get; set; }
        public int[]? Sizes { get; set; }
    }

    internal sealed class FileDto
    {
        public string? Path { get; set; }
        public long Time { get; set; }
        public long Size { get; set; }
        public ProbeDto? Probe { get; set; }
    }

    internal sealed class ProbeDto
    {
        public string? Name { get; set; }
        public long Offset { get; set; }
        public int Size { get; set; }
        public string? Error { get; set; }
        public ClipDto? Clip { get; set; }
        public MeshDto? Mesh { get; set; }
    }

    internal sealed class ClipDto
    {
        public int Version { get; set; }
        public float PosReduction { get; set; }
        public float RotReduction { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public int Bones { get; set; }
        public int MorphVertices { get; set; }
        public int MorphKeyframes { get; set; }
        public int RampIn { get; set; }
        public int RampOut { get; set; }
        public float[]? TotalRotation { get; set; }
        public float[]? TotalTranslation { get; set; }
    }

    internal sealed class MeshDto
    {
        public bool Character { get; set; }
        public string[]? Submeshes { get; set; }
        public int[]? Lods { get; set; }
        public string[]? BoneNames { get; set; }
        public int[]? BoneParents { get; set; }
        public int Spheres { get; set; }
        public bool Readable { get; set; }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(LibraryCacheFile.CacheDto))]
internal sealed partial class LibraryCacheJsonContext : JsonSerializerContext;
