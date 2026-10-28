using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Cairn.Workspace;

namespace Cairn.Lvl.Writing;

/// <summary>How a group file is written.</summary>
public sealed record RfgWriteOptions
{
    /// <summary>
    /// The version every record is written with. The default is the version current Alpine RED writes (300): it is
    /// the only one under which Alpine's event orientations load back, and stock RED reads it too.
    /// </summary>
    public int Version { get; init; } = RfgReader.AlpineVersion;

    /// <summary>
    /// Layout quirks to reproduce (the reader's fallbacks). Only for rewriting a file exactly as it was stored: a
    /// file written with <see cref="RfGroupFallbacks.ReduxEventMatrices"/> cannot be loaded by RED.
    /// </summary>
    public RfGroupFallbacks Layout { get; init; }

    /// <summary>
    /// Keep stored raw values: 32-bit face flags whose low half matches the RF1 flags, the Alpine mesh record
    /// layout of the source build, and the brush metadata chunk's version and entry size. Off writes the current
    /// layouts.
    /// </summary>
    public bool AsStored { get; init; }

    /// <summary>Options that reproduce <paramref name="file"/>'s own version and layouts (for byte-exact rewrites).</summary>
    public static RfgWriteOptions AsStoredFor(RfGroupFile file) => new() { Version = file.Version, Layout = file.Fallbacks, AsStored = true };
}

/// <summary>A written group file and what the writer had to change to fit the layout.</summary>
/// <param name="Bytes">The file.</param>
/// <param name="Notes">One plain-language line per kind of change (empty when the model fitted as it is).</param>
public sealed record RfgWriteResult(byte[] Bytes, ImmutableArray<string> Notes);

/// <summary>
/// Writes Red Faction editor group files (.rfg): the magic and version, the group count (from 0x6C), every group
/// with its moving data, brushes and object lists, then the Alpine chunks (meshes, coronas, the brush metadata and
/// every chunk kept as stored) in the order Alpine RED writes them. Each field is gated by the version exactly as
/// RED (and <see cref="RfgReader"/>) reads it, so reading the result gives the same model.
/// </summary>
public static class RfgWriter
{
    // Alpine RED's chunk order; ids it does not write (or Cairn does not know) go after these, before the brush metadata.
    private static readonly uint[] ChunkOrder =
        [0x0AFBAE01, 0x0AFBAE02, 0x0AFBAE03, 0x0AFBAE04, 0x0AFBAE06, 0x0AFBAE07, 0x0AFBAE08, 0x0AFBAE0A, 0x0AFBAE0C, 0x0AFBAE0B];

    private const uint MeshChunk = 0x0AFBAE01;
    private const uint CoronaChunk = 0x0AFBAE03;
    private const uint BrushMetadataChunk = 0x0AFBAE05;

    /// <summary>Writes a group file in memory.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The version is older than any group file Cairn reads.</exception>
    /// <exception cref="ArgumentException">A version before 0x6C holds exactly one group, and the file has more.</exception>
    /// <exception cref="InvalidOperationException">The file would be larger than 2 GB.</exception>
    public static RfgWriteResult Write(RfGroupFile file, RfgWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        options ??= new RfgWriteOptions();
        int v = options.Version;
        if (v < RflLevelReader.OldestVersion)
            throw new ArgumentOutOfRangeException(nameof(options), v, $"Group files older than version {RflLevelReader.OldestVersion} cannot be written.");
        var groups = file.Groups.IsDefault ? [] : file.Groups;
        if (v < 0x6C && groups.Length != 1)
            throw new ArgumentException($"A group file of version {v} holds exactly one group; this one has {groups.Length}.", nameof(file));

        var rules = LvlRules.For(v, groupFile: true);
        if ((options.Layout & RfGroupFallbacks.AlpineEventMatricesBelow300) != 0) rules = rules with { AlpineEventMatrices = true };
        if ((options.Layout & RfGroupFallbacks.NoAlpineEventMatrices) != 0) rules = rules with { AlpineEventMatrices = false };
        if ((options.Layout & RfGroupFallbacks.NoPlayerCollideAbsent) != 0) rules = rules with { NoPlayerCollideAbsent = true };
        if ((options.Layout & RfGroupFallbacks.ReduxEventMatrices) != 0) rules = rules with { ReduxEventMatrices = true };

        var w = new LvlWriter((int)Math.Clamp(Estimate(groups), 4096, 64 << 20));
        var notes = new LvlWriteNotes();
        var rw = new LvlRecordWriter(rules, w, notes, options.AsStored);

        w.U32(RfgReader.Magic).I32(v);
        if (v >= 0x6C) w.I32(groups.Length);
        foreach (var g in groups) Group(g, rw, w, notes, v);
        Chunks(file, groups, rw, w, notes, options);

        if (w.LossyStrings > 0) notes.Add("{0} names held characters a group file cannot store; they were written as '?'.", w.LossyStrings);
        if (w.LongStrings > 0) notes.Add("{0} names were longer than 65,535 characters and were cut.", w.LongStrings);
        return new RfgWriteResult(w.ToArray(), notes.Build());
    }

    /// <summary>Writes a group file to disk atomically (the old file stays intact if the write fails).</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public static RfgWriteResult WriteFile(RfGroupFile file, string path, RfgWriteOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var result = Write(file, options);
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
        AtomicFile.WriteAllBytes(full, result.Bytes);
        return result;
    }

    private static void Group(RfGroup g, LvlRecordWriter rw, LvlWriter w, LvlWriteNotes notes, int v)
    {
        w.Str(g.Name);
        bool moving = g.Moving is not null && v >= 0x5B;
        if (g.Moving is not null && !moving) notes.Add("{0} moving groups were written as plain groups (this version stores no moving data).");
        if (v >= 0x5B) w.Bool(moving);
        if (moving) rw.MovingData(g.Moving!);
        rw.List(g.Brushes, rw.Brush);

        var o = g.Objects ?? RfObjectSet.Empty;
        // Geo regions before 0x63 have a layout no readable file uses.
        if (v >= 0x63) rw.List(o.GeoRegions, rw.GeoRegion);
        else
        {
            w.I32(0);
            notes.Add("{0} geo regions were left out (this version's geo regions are not supported).", Len(o.GeoRegions));
        }
        rw.List(o.Lights, rw.Light);
        rw.List(o.CutsceneCameras, rw.CutsceneCamera);
        if (v >= 0x8C) rw.List(o.CutscenePathNodes, rw.CutscenePathNode);
        else notes.Add("{0} cutscene path nodes were left out (this version does not store them in groups).", Len(o.CutscenePathNodes));
        rw.List(o.AmbientSounds, rw.AmbientSound);
        rw.List(o.Events, rw.Event);
        rw.List(o.RespawnPoints, rw.RespawnPoint);
        if (v < 0x8C) rw.List(o.LegacyWaypointLists, rw.LegacyWaypointList);
        else notes.Add("{0} old-style waypoint lists were left out (later versions keep waypoints as nav points).", Len(o.LegacyWaypointLists));
        rw.List(o.NavPoints, rw.NavPoint);
        rw.List(o.Entities, rw.Entity);
        rw.List(o.Items, rw.Item);
        rw.List(o.Clutter, rw.Clutter);
        rw.List(o.Triggers, rw.Trigger);
        rw.List(o.ParticleEmitters, rw.ParticleEmitter);
        rw.List(o.GasRegions, rw.GasRegion);
        rw.List(o.Decals, rw.Decal);
        rw.List(o.ClimbingRegions, rw.ClimbingRegion);
        rw.List(o.RoomEffects, rw.RoomEffect);
        if (v >= 0xB4) rw.List(o.EaxEffects, rw.EaxEffect);
        else notes.Add("{0} EAX effects were left out (this version does not store them in groups).", Len(o.EaxEffects));
        rw.List(o.BoltEmitters, rw.BoltEmitter);
        rw.List(o.Targets, rw.Target);
        rw.List(o.PushRegions, rw.PushRegion);
    }

    private static int Len<T>(ImmutableArray<T> a) => a.IsDefault ? 0 : a.Length;

    /// <summary>About how many bytes the brushes take (the buffer's first size, so large files grow less often).</summary>
    private static long Estimate(ImmutableArray<RfGroup> groups)
    {
        long bytes = 64;
        foreach (var g in groups)
        {
            if (g.Brushes.IsDefault) continue;
            foreach (var b in g.Brushes)
            {
                var geometry = b.Geometry ?? RfGeometry.Empty;
                bytes += 200L + Len(geometry.Vertices) * 12L;
                if (geometry.Faces.IsDefault) continue;
                foreach (var f in geometry.Faces) bytes += 60L + Len(f.Corners) * 20L;
            }
        }
        return bytes;
    }

    /// <summary>
    /// The Alpine chunks. They carry no group membership: Alpine RED gives every Alpine object to the first group of
    /// the import and indexes the brush metadata into the first group's brushes, so that is where they are taken from.
    /// </summary>
    private static void Chunks(RfGroupFile file, ImmutableArray<RfGroup> groups, LvlRecordWriter rw, LvlWriter w, LvlWriteNotes notes, RfgWriteOptions options)
    {
        var meshes = new List<RfAlpineMesh>();
        var coronas = new List<RfCorona>();
        for (int i = 0; i < groups.Length; i++)
        {
            var o = groups[i].Objects ?? RfObjectSet.Empty;
            if (!o.AlpineMeshes.IsDefault) meshes.AddRange(o.AlpineMeshes);
            if (!o.Coronas.IsDefault) coronas.AddRange(o.Coronas);
            if (i > 0) notes.Add("{0} Alpine meshes and coronas of later groups will join the first group when RED imports the file (Alpine objects carry no group).", Len(o.AlpineMeshes) + Len(o.Coronas));
        }

        var metadata = Metadata(file, groups, notes, options);
        var opaque = (file.OpaqueChunks.IsDefault ? [] : file.OpaqueChunks)
            .Select((c, i) => (Chunk: c, Index: i))
            .OrderBy(x => Rank(x.Chunk.Id)).ThenBy(x => x.Index)
            .Select(x => x.Chunk)
            .ToList();

        var known = new List<(uint Id, Action Body)>();
        if (meshes.Count > 0)
        {
            var (layout, tail) = MeshLayout(meshes, options.AsStored);
            known.Add((MeshChunk, () => rw.AlpineMeshes(meshes, layout, tail)));
        }
        if (coronas.Count > 0) known.Add((CoronaChunk, () => rw.Coronas(coronas)));

        // Merge the known chunks into Alpine's order; chunks kept as stored follow their own id's place.
        int k = 0;
        foreach (var chunk in opaque)
        {
            while (k < known.Count && Rank(known[k].Id) <= Rank(chunk.Id)) WriteChunk(w, known[k].Id, known[k++].Body);
            WriteChunk(w, chunk.Id, () => w.Bytes(chunk.Body.Span));
        }
        while (k < known.Count) WriteChunk(w, known[k].Id, known[k++].Body);
        if (metadata is not null) WriteChunk(w, BrushMetadataChunk, () => rw.BrushMetadata(metadata));
    }

    private static int Rank(uint id)
    {
        int i = Array.IndexOf(ChunkOrder, id);
        return i >= 0 ? i : ChunkOrder.Length;
    }

    private static void WriteChunk(LvlWriter w, uint id, Action body)
    {
        w.U32(id);
        int lengthAt = w.Length;
        w.I32(0);
        body();
        w.PatchI32(lengthAt, w.Length - lengthAt - 4);
    }

    /// <summary>The brush metadata chunk from the first group's brushes (the ones with Alpine flags), or null when none has any.</summary>
    private static RfBrushMetadata? Metadata(RfGroupFile file, ImmutableArray<RfGroup> groups, LvlWriteNotes notes, RfgWriteOptions options)
    {
        for (int i = 1; i < groups.Length; i++)
        {
            var brushes = groups[i].Brushes.IsDefault ? [] : groups[i].Brushes;
            notes.Add("{0} brushes outside the first group lost their Alpine flags (geoable, breakable, no shadow): only the first group's brushes can carry them.",
                brushes.Count(b => b.Alpine is { Flags: not RfAlpineBrushFlags.None }));
        }
        if (groups.Length == 0) return null;
        var first = groups[0].Brushes.IsDefault ? [] : groups[0].Brushes;
        var entries = new List<RfBrushMetadataEntry>();
        int extra = 0;
        for (int i = 0; i < first.Length; i++)
        {
            if (first[i].Alpine is not { } a || a.Flags == RfAlpineBrushFlags.None) continue;
            var bytes = a.ExtraBytes.IsDefault ? [] : a.ExtraBytes;
            entries.Add(new RfBrushMetadataEntry(i, a.Flags, a.Material, bytes));
            extra = Math.Max(extra, bytes.Length);
        }
        if (entries.Count == 0) return null;
        if (entries.Count > 10_000) notes.Add("{0} flagged brushes are more than Alpine reads from one group file (10,000).", entries.Count);
        uint version = 1;
        int size = 6 + extra;
        if (options.AsStored && file.BrushMetadata is { } stored)
        {
            version = stored.Version;
            size = Math.Max(size, stored.EntrySize);
        }
        return new RfBrushMetadata(version, size, [.. entries]);
    }

    /// <summary>The current layout, or (as stored) the one the source's records show.</summary>
    private static (RfAlpineMeshLayout Layout, LvlRecordWriter.MeshTail Tail) MeshLayout(List<RfAlpineMesh> meshes, bool asStored)
    {
        if (!asStored) return (RfAlpineMeshLayout.Full, LvlRecordWriter.MeshTail.Full);
        var layout = meshes.All(m => m.Material is null) ? RfAlpineMeshLayout.NoTail
            : meshes.Any(m => m.Clutter is { Corpse: null }) ? RfAlpineMeshLayout.NoCorpse
            : RfAlpineMeshLayout.Full;
        var tail = meshes.All(m => m.TrailingFlags is null) ? LvlRecordWriter.MeshTail.None
            : meshes.All(m => m.BrushGeometrySource is null) ? LvlRecordWriter.MeshTail.Flags
            : meshes.All(m => m.DrawScale is null) ? LvlRecordWriter.MeshTail.FlagsAndSourceWithoutScale
            : LvlRecordWriter.MeshTail.Full;
        return (layout, tail);
    }
}
