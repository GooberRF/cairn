using System.Globalization;
using System.Numerics;
using Cairn.Formats.XForge;

namespace Cairn.Vpp.Facts;

/// <summary>Red Faction for N-Gage's X-Forge types: particle systems (<c>.pr3</c>) and scenes (<c>.xff</c>).</summary>
public static partial class VppFacts
{
    /// <summary>The most frames, textures, prefabs or particle files a sheet lists one by one.</summary>
    private const int MaxListedNames = 100;

    /// <summary>The most scene problems a sheet lists one by one.</summary>
    private const int MaxListedProblems = 20;

    private static string F(float v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    private static string F(Vector3 v) => $"{F(v.X)}, {F(v.Y)}, {F(v.Z)}";

    private static string Raw(int v) => string.Format(CultureInfo.CurrentCulture, "{0:N0} (0x{0:X8})", v);

    private static string Raw(Pr3ValueGroup g) => $"kind {g.UnknownKind.ToString(CultureInfo.CurrentCulture)}, {F(g.First)}, {F(g.Second)}";

    private static string Raw(IReadOnlyList<Pr3ValueGroup> groups) => string.Join("; ", groups.Select(Raw));

    /// <summary>
    /// Where a file a scene or particle system names is: "in this archive" (or packfile), or "; missing"; null when there
    /// is no package to look in. The archive's names are compared ignoring case (A-Z), as the game does.
    /// </summary>
    private static Func<string, string>? SameArchivePresence(VppFactsContext context)
    {
        if (context.Package is not { } package) return null;
        var names = package.Items.Where(i => i.Source is not Model.MissingFileSource).Select(i => i.Name).ToHashSet(Editing.VppNames.Comparer);
        string where = Place(context);
        return name => names.Contains(name) ? $"in {where}" : $"not in {where}; missing";
    }

    /// <summary>"this archive" for a CFL archive, else "this packfile".</summary>
    private static string Place(VppFactsContext context) => context.Package?.Cfl is not null ? "this archive" : "this packfile";

    /// <summary>
    /// An X-Forge particle system (<c>.pr3</c>): the fields whose meaning is likely, with units where known, the
    /// animation frames (each checked against the archive), and every other field raw under "Unknown fields".
    /// </summary>
    private static void DescribeParticleSystem(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var ps = Pr3File.Read(input.ReadAll(), input.Name);
        var presence = SameArchivePresence(input.Context);
        sheet.Add("Format", $"X-Forge particle system, version {ps.Version.ToString(CultureInfo.CurrentCulture)}");
        sheet.Add("Frames", ps.Frames.Count);
        sheet.Add("Emission rate", $"{F(ps.EmissionRate)} per second (likely)");
        sheet.Add("Lifetime", $"{ps.LifetimeMs.ToString("N0", CultureInfo.CurrentCulture)} ms (likely)");
        sheet.Add("Particles", $"at most {ps.MaxParticles.ToString("N0", CultureInfo.CurrentCulture)} alive at once");
        sheet.Add("Particle size", $"{F(ps.StartSize)} at birth, {F(ps.EndSize)} at death (scene units; likely)");
        sheet.Add("Start alpha", $"{F(ps.StartAlpha)} of 255 (likely)");
        sheet.Add("Emitter offset", $"{F(ps.EmitterOffset)} (scene units; likely, or the emitter's extent)");
        sheet.Add("Damping", $"{F(ps.Damping)} (velocity multiplier per axis; likely)");
        sheet.Add("Gravity", $"{F(ps.Gravity)} (acceleration per axis; likely)");
        sheet.Add("Converts to", "nothing: too few of its fields are understood to make a Red Faction particle emitter");

        var missing = new List<string>();
        for (int i = 0; i < ps.Frames.Count && i < MaxListedNames; i++)
        {
            string frame = ps.Frames[i];
            string? where = presence?.Invoke(frame);
            if (where?.EndsWith("; missing", StringComparison.Ordinal) == true) missing.Add(frame);
            sheet.Add($"Frames: {(i + 1).ToString(CultureInfo.CurrentCulture)}", where is null ? frame : $"{frame}, {where}");
        }
        if (ps.Frames.Count > MaxListedNames) sheet.Add("Frames: ...", $"and {N(ps.Frames.Count - MaxListedNames)} more");
        if (ps.DroppedName is { } dropped) sheet.Add("Frames: (not used)", dropped);
        if (ps.TrailingBytes > 0) sheet.Add("Frames: after the end", $"{N(ps.TrailingBytes)} {(ps.TrailingBytes == 1 ? "byte" : "bytes")} (the shipped files end with '|||', leaving one)");
        if (missing.Count > 0)
            sheet.Warn((missing.Count == 1 ? "1 frame texture is" : $"{missing.Count} frame textures are") + $" not in {Place(input.Context)}: " + string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? ", ..." : "."));

        // every other field as stored, named by its file offset
        const string U = "Unknown fields: ";
        sheet.Add(U + "0x04", Raw(ps.Unknown04));
        sheet.Add(U + "0x14", Raw(ps.Unknown14));
        sheet.Add(U + "0x50", Raw(ps.Unknown50));
        sheet.Add(U + "0x7C", Raw(ps.Unknown7C));
        sheet.Add(U + "0x88", Raw(ps.Unknown88) + " (a particle count: the minimum or the burst)");
        sheet.Add(U + "0x90", F(ps.Unknown90) + (ps.Unknown90 == 0 ? " (the game reads 0 as 1)" : string.Empty));
        sheet.Add(U + "0x94", F(ps.Unknown94));
        sheet.Add(U + "0x98", F(ps.Unknown98));
        sheet.Add(U + "0x9C", Raw(ps.Unknown9C));
        sheet.Add(U + "0xA4", F(ps.UnknownA4));
        sheet.Add(U + "0xA8", Raw(ps.UnknownA8));
        sheet.Add(U + "0xBC", Raw(ps.UnknownBC));
        sheet.Add(U + "0xC8", F(ps.UnknownC8));
        sheet.Add(U + "0xCC", Raw(ps.UnknownCC));
        sheet.Add(U + "0xF0", F(ps.UnknownF0));
        sheet.Add(U + "0xFC", F(ps.UnknownFC));
        sheet.Add(U + "0x108", Raw(ps.Unknown108));
        foreach (var problem in ps.Problems) sheet.Warn(problem);
    }

    /// <summary>
    /// An X-Forge scene (<c>.xff</c>): what it is (level, character or model, and why), its nodes, meshes, triangles,
    /// textures (each checked against the archive), animation, markers by type, prefabs and particle systems, and any
    /// chunks kept raw or problems found.
    /// </summary>
    private static void DescribeScene(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var scene = XffScene.Read(input.ReadAll(), input.Name);
        var file = scene.File;
        var presence = SameArchivePresence(input.Context);
        var (kind, reason) = XffClassifier.Explain(scene);
        const string S = "Scene: ", G = "Geometry: ";
        sheet.Add(S + "Kind", $"{kind} ({reason})");
        sheet.Add(S + "Chunks", file.Chunks.Count);
        sheet.Add(S + "Nodes", scene.Nodes.Count);
        sheet.Add(S + "Node types", string.Join(", ", scene.Nodes.Where(n => !n.IsMarker).GroupBy(n => n.Type).OrderBy(g => g.Key)
            .Select(g => $"{XffChunkTypes.Describe(g.Key)}: {N(g.Count())}")) is { Length: > 0 } types ? types : "none");
        sheet.Add(G + "Meshes", scene.Meshes.Count);
        sheet.Add(G + "Submeshes", scene.Meshes.Sum(m => m.Submeshes.Count));
        sheet.Add(G + "Vertices", file.All<XffVertexBuffer>().Sum(v => (long)v.Count));
        sheet.Add(G + "Triangles", scene.Meshes.Sum(m => m.Submeshes.Sum(s => (long)s.TriangleCount)));
        sheet.Add(G + "Materials", scene.Materials.Count);
        sheet.Add(S + "Animation", AnimationText(scene));

        var textures = TextureNames(scene);
        sheet.Add(G + "Textures", textures.Count);
        var missing = ListNames(sheet, "Textures", textures, presence);
        if (missing.Count > 0)
            sheet.Warn((missing.Count == 1 ? "1 texture this scene names is" : $"{missing.Count} textures this scene names are") + $" not in {Place(input.Context)}: "
                + string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? ", ..." : "."));

        // markers by type
        foreach (var group in scene.MarkersByType)
        {
            var names = group.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
            sheet.Add($"Markers: {group.Key.ToString(CultureInfo.InvariantCulture)} {MarkerMeaning(group.Key)}",
                $"{N(group.Count())}: {string.Join(", ", names.Take(8))}{(names.Count > 8 ? ", ..." : string.Empty)}");
        }
        if (scene.Markers.Count == 0) sheet.Add("Markers: none", "the scene has no game-object markers");

        // the scenes it places, and the particle systems it runs
        var prefabs = scene.Prefabs.GroupBy(p => p.Prefab!.FileName, Editing.VppNames.Comparer).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (prefabs.Count > 0) sheet.Add(S + "Prefabs", $"{N(scene.Prefabs.Count)}, placing {N(prefabs.Count)} {(prefabs.Count == 1 ? "scene" : "scenes")}");
        var missingPrefabs = new List<string>();
        foreach (var group in prefabs.Take(MaxListedNames))
        {
            string? where = presence?.Invoke(group.Key);
            if (where?.EndsWith("; missing", StringComparison.Ordinal) == true) missingPrefabs.Add(group.Key);
            sheet.Add("Prefabs: " + group.Key, N(group.Count()) + (where is null ? string.Empty : ", " + where));
        }
        if (prefabs.Count > MaxListedNames) sheet.Add("Prefabs: ...", $"and {N(prefabs.Count - MaxListedNames)} more");
        if (missingPrefabs.Count > 0)
            sheet.Warn((missingPrefabs.Count == 1 ? "1 scene this one places is" : $"{missingPrefabs.Count} scenes this one places are") + $" not in {Place(input.Context)}: "
                + string.Join(", ", missingPrefabs.Take(10)) + (missingPrefabs.Count > 10 ? ", ..." : "."));
        var effects = file.All<XffEmitter>().Select(e => e.EffectFile).Where(n => n.Length > 0).Distinct(Editing.VppNames.Comparer).ToList();
        ListNames(sheet, "Particle systems", effects, presence);

        // what the reader kept raw, and what does not resolve
        if (file.RawChunkCount > 0)
            sheet.Add(S + "Raw chunks", string.Join(", ", file.RawChunkCounts.OrderBy(p => p.Key.Type).ThenBy(p => p.Key.Version)
                .Select(p => $"{N(p.Value)} {XffChunkTypes.Describe(p.Key.Type)} (version {p.Key.Version.ToString(CultureInfo.InvariantCulture)})")) + " (kept as bytes, not read)");
        sheet.Add(S + "Problems", scene.Problems.Count == 0 ? "none" : N(scene.Problems.Count));
        foreach (var problem in scene.Problems.Take(MaxListedProblems)) sheet.Warn(problem);
        if (scene.Problems.Count > MaxListedProblems) sheet.Warn($"... and {N(scene.Problems.Count - MaxListedProblems)} more problems.");
    }

    /// <summary>The textures a scene names (its texture chunks and its sprites'), each once, in file order.</summary>
    internal static List<string> TextureNames(XffScene scene) =>
        [.. scene.Textures.Select(t => t.FileName).Concat(scene.File.All<XffSprite>().Select(s => s.TextureName))
            .Where(n => n.Length > 0).Distinct(Editing.VppNames.Comparer)];

    /// <summary>One row per name under <paramref name="section"/> with where it is; the names that are missing.</summary>
    private static List<string> ListNames(VppFactSheetBuilder sheet, string section, IReadOnlyList<string> names, Func<string, string>? presence)
    {
        var missing = new List<string>();
        foreach (string name in names.Take(MaxListedNames))
        {
            string? where = presence?.Invoke(name);
            if (where?.EndsWith("; missing", StringComparison.Ordinal) == true) missing.Add(name);
            sheet.Add($"{section}: {name}", where ?? "named by the scene");
        }
        if (names.Count > MaxListedNames) sheet.Add($"{section}: ...", $"and {N(names.Count - MaxListedNames)} more");
        // a missing name past the listed ones still counts
        foreach (string name in names.Skip(MaxListedNames))
            if (presence?.Invoke(name).EndsWith("; missing", StringComparison.Ordinal) == true) missing.Add(name);
        return missing;
    }

    /// <summary>
    /// "10 tracks, 533 ms long (from -266 to 533 ms), 30 keys per second, looping", or "none": the longest track's length
    /// (tracks offset in time, such as a second foot's, share one cycle), then the times all tracks cover.
    /// </summary>
    private static string AnimationText(XffScene scene)
    {
        var tracks = scene.Tracks.Where(t => t.Keys.Count > 0).ToList();
        if (tracks.Count == 0) return scene.Tracks.Count == 0 ? "none" : $"{N(scene.Tracks.Count)} tracks without keys";
        int start = tracks.Min(t => t.Start), end = tracks.Max(t => t.End);
        var rates = tracks.Select(t => t.Rate).Distinct().Order().Select(r => F(r)).ToList();
        int loops = tracks.Count(t => t.Loops);
        string looping = loops == tracks.Count ? "looping" : loops == 0 ? "not looping" : $"{N(loops)} looping";
        var c = CultureInfo.CurrentCulture;
        return $"{(scene.Tracks.Count == 1 ? "1 track" : $"{N(scene.Tracks.Count)} tracks")}, {LongestTrackMs(scene).ToString("N0", c)} ms long "
            + $"(from {start.ToString("N0", c)} to {end.ToString("N0", c)} ms), {string.Join(" / ", rates)} keys per second, {looping}";
    }

    /// <summary>The longest keyed track's length in milliseconds (end - start), or 0 without one.</summary>
    internal static long LongestTrackMs(XffScene scene) =>
        scene.Tracks.Where(t => t.Keys.Count > 0).Select(t => (long)t.End - t.Start).DefaultIfEmpty(0).Max();

    /// <summary>What a marker type means, as far as it is known ("script trigger", "door (likely)", "unknown").</summary>
    internal static string MarkerMeaning(ushort type) => type switch
    {
        XffMarkers.Player => "player",
        2012 => "camera rig (likely)",
        XffMarkers.Area => "area",
        XffMarkers.LevelEnd => "level end (likely)",
        >= 2014 and <= 2065 => "object type (weapon, item, ammo or projectile)",
        2070 or 2071 => "vehicle",
        XffMarkers.PlayerClient or XffMarkers.PlayerServer => "multiplayer player",
        XffMarkers.DeathmatchSpawn => "deathmatch spawn (likely)",
        3104 => "instant-death volume (likely)",
        3105 => "damage volume (likely)",
        XffMarkers.ScriptTrigger => "script trigger",
        XffMarkers.Button => "button (likely)",
        XffMarkers.Door => "door or mover (likely)",
        XffMarkers.Wall => "destructible object (likely)",
        3111 => "mode-change volume (likely)",
        3112 => "level exit (likely)",
        3113 => "breakable prop (likely)",
        3114 => "laser barrier (likely)",
        3115 => "exploding barrel",
        XffMarkers.EnemyGroup => "enemy trigger volume (likely)",
        _ when XffMarkers.IsEnemyMarker(type) => "enemy type",
        XffMarkers.Torpedo => "torpedo (likely)",
        _ when XffMarkers.SpawnedEnemyType(type) is { } enemy => $"enemy spawn (of type {enemy.ToString(CultureInfo.InvariantCulture)})",
        _ => "(unknown)",
    };
}
