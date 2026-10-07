using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Assets;

/// <summary>One clip the library found: where it is, its header facts or why they could not be read.</summary>
/// <param name="Name">The file name, e.g. "ult2_stand.rfa".</param>
/// <param name="Location">Where it is (loose file or archive entry).</param>
/// <param name="Facts">The probed header, or null when the file could not be read.</param>
/// <param name="Error">A plain-language reason the file could not be read, or null.</param>
/// <param name="ShadowedBy">
/// For a copy the engine never loads because a location earlier in the search order has a file of the
/// same name: that winning location. Null for the copy the engine loads.
/// </param>
public sealed record LibraryClip(string Name, AssetLocation Location, RfaProbeResult? Facts, string? Error, AssetLocation? ShadowedBy)
{
    /// <summary>True when another copy of the same name wins (this one is never loaded).</summary>
    public bool IsShadowed => ShadowedBy is not null;

    /// <summary>The engine's identity for the clip: the base name, compared ignoring case.</summary>
    public string BaseName => Path.GetFileNameWithoutExtension(Name);

    /// <summary>The clip's bone count, when readable.</summary>
    public int? BoneCount => Facts?.BoneCount;

    /// <summary>True when the header was read.</summary>
    public bool IsReadable => Facts is not null;

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Location.DisplayLocation})";
}

/// <summary>One mesh the library found: where it is, its probed facts or why they could not be read.</summary>
/// <param name="Name">The file name, e.g. "miner.v3c".</param>
/// <param name="Location">Where it is (loose file or archive entry).</param>
/// <param name="Facts">The probe result, or null when the file could not be read.</param>
/// <param name="Error">A plain-language reason the file could not be read, or null.</param>
/// <param name="ShadowedBy">The winning location for a copy the engine never loads; null for the one it loads.</param>
public sealed record LibraryMesh(string Name, AssetLocation Location, V3dProbeResult? Facts, string? Error, AssetLocation? ShadowedBy)
{
    /// <summary>True when another copy of the same name wins (this one is never loaded).</summary>
    public bool IsShadowed => ShadowedBy is not null;

    /// <summary>Character or static mesh: from the signature when readable, else from the extension.</summary>
    public V3dKind Kind => Facts?.Kind
        ?? (Name.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase) ? V3dKind.Character : V3dKind.StaticMesh);

    /// <summary>Number of bones (0 for a static mesh or an unreadable file).</summary>
    public int BoneCount => Facts?.BoneCount ?? 0;

    /// <summary>True for a readable mesh with at least one bone: one clips can play on.</summary>
    public bool HasSkeleton => BoneCount > 0;

    /// <summary>True when the file was read.</summary>
    public bool IsReadable => Facts is not null;

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Location.DisplayLocation})";
}

/// <summary>A table line for a mesh, with the library's copy of its clip.</summary>
/// <param name="List">The route by which the tables give the mesh this clip.</param>
/// <param name="Usage">The state/action line.</param>
/// <param name="Clip">The clip the engine would load, or null when no searched location has it.</param>
public sealed record LibraryClipUsage(MeshClipList List, ClipUsage Usage, LibraryClip? Clip);

/// <summary>What a library build is doing.</summary>
public enum LibraryBuildPhase
{
    /// <summary>Reading the persistent cache file.</summary>
    LoadingCache,
    /// <summary>Listing folders and archive directories.</summary>
    Enumerating,
    /// <summary>Reading headers of files the cache does not cover.</summary>
    Probing,
    /// <summary>Building the snapshot (shadowing, families, lookups).</summary>
    Indexing,
    /// <summary>Writing the persistent cache file.</summary>
    SavingCache,
    /// <summary>Finished.</summary>
    Done,
}

/// <summary>Progress of a library build.</summary>
/// <param name="Phase">The current phase.</param>
/// <param name="Done">Items finished in this phase.</param>
/// <param name="Total">Items in this phase (0 when unknown).</param>
/// <param name="Current">The item being worked on, when known.</param>
public sealed record LibraryProgress(LibraryBuildPhase Phase, int Done, int Total, string? Current = null)
{
    /// <summary>Fraction 0..1 of the phase, or null when the total is unknown.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp(Done / (double)Total, 0, 1) : null;
}

/// <summary>What a library build did, for the status bar, diagnostics and tests.</summary>
/// <param name="Locations">Clip and mesh locations seen, shadowed copies included.</param>
/// <param name="Probed">Files or entries whose bytes were read (cache misses).</param>
/// <param name="FromCache">Locations whose facts came from the memory or persistent cache.</param>
/// <param name="ArchivesOpened">Archives whose directory had to be read.</param>
/// <param name="ArchivesFromCache">Archives whose entry list came from the cache (not opened at all).</param>
/// <param name="Errors">Locations that could not be read.</param>
/// <param name="CacheFileLoaded">True when the persistent cache file was read during this build.</param>
/// <param name="CacheFileWritten">True when the persistent cache file was written.</param>
/// <param name="CacheFileError">Why the cache file could not be read or written, if it could not.</param>
/// <param name="Elapsed">Wall-clock time of the build.</param>
public sealed record LibraryBuildStats(
    int Locations, int Probed, int FromCache, int ArchivesOpened, int ArchivesFromCache, int Errors,
    bool CacheFileLoaded, bool CacheFileWritten, string? CacheFileError, TimeSpan Elapsed)
{
    /// <summary>No build yet.</summary>
    public static LibraryBuildStats None { get; } = new(0, 0, 0, 0, 0, 0, false, false, null, TimeSpan.Zero);
}
