using System.Globalization;
using Cairn.Assets;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Ui.Preview;
using Cairn.Previews;
using Cairn.Ui.Modules;

namespace Cairn.Lvl.Ui.Documents;

/// <summary>
/// The facts beside a level or group file's preview. Levels: the same rows the packfile module shows for a level
/// (identity, properties, statistics, object classes, Alpine settings, references with where each one is found,
/// preloads, sections), plus what the full reader found (brushes, movers, groups, problems). Groups: the groups with
/// their brushes, objects and movement, the textures they use, and the Alpine data.
/// </summary>
internal static class LvlFacts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Where a referenced name is found: beside the file (its packfile), in the game data, or nowhere.</summary>
    internal static Func<string, AssetPresence>? Presence(IAssetSiblings? siblings, AssetResolver? resolver)
    {
        bool canResolve = resolver is not null && (resolver.Options.GameDirectory is not null || resolver.Options.SearchFolders.Count > 0);
        if (siblings is null && !canResolve) return null;
        var known = new Dictionary<string, AssetPresence>(StringComparer.OrdinalIgnoreCase);
        return name =>
        {
            // the names the engine asks for: an .mvf animation loads as .rfa, an exporter mesh as the saved mesh
            var candidates = AssetLookup.EngineNames(name);
            if (candidates.Count == 0) return AssetPresence.Unknown;
            string key = candidates[0];
            if (known.TryGetValue(key, out var cached)) return cached;
            AssetPresence found;
            if (siblings is not null && candidates.Any(c => siblings.Find(c) is not null)) found = AssetPresence.SamePackfile;
            else if (!canResolve) found = AssetPresence.Unknown;
            else
            {
                bool any = false;
                foreach (var candidate in candidates)
                {
                    try { any = resolver!.Resolve(candidate) is not null; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    if (any) break;
                }
                found = any ? AssetPresence.GameData : AssetPresence.Missing;
            }
            return known[key] = found;
        };
    }

    /// <summary>
    /// Where a texture a Red Faction II level names is found: in its own <c>.peg</c> packs (beside it, or in its packfile)
    /// or in its install's shared packs. Anything else (meshes, sounds and animations live in RF2's other packfiles), and a
    /// texture in neither, is left without a location rather than called missing. Null when there are no packs to search.
    /// </summary>
    /// <param name="packs">The level's packs in search order (see <see cref="LvlTexturePacks.PacksFor"/>), or null.</param>
    /// <param name="sharedPackNames">The names of the install's shared packs among them.</param>
    internal static Func<string, AssetPresence>? Rf2Presence(PegTextureSet? packs, IEnumerable<string> sharedPackNames)
    {
        if (packs is null || packs.IsEmpty) return null;
        var shared = new HashSet<string>(sharedPackNames, StringComparer.OrdinalIgnoreCase);
        var known = new Dictionary<string, AssetPresence>(StringComparer.OrdinalIgnoreCase);
        return name =>
        {
            string bare = Path.GetFileName(name.Trim());
            if (bare.Length == 0 || !AssetResolver.IsTextureName(bare)) return AssetPresence.Unknown;
            if (known.TryGetValue(bare, out var cached)) return cached;
            var hit = packs.Find(bare);
            return known[bare] = hit is null ? AssetPresence.Unknown : shared.Contains(hit.Pack) ? AssetPresence.GameData : AssetPresence.SamePackfile;
        };
    }

    /// <summary>The details of <paramref name="source"/> (pool thread; cancellable between groups of rows).</summary>
    public static AssetDetails Build(LvlSource source, Func<string, AssetPresence>? presence, CancellationToken ct)
    {
        var rows = new List<AssetDetailRow>();
        var warnings = new List<string>();
        if (source.Level is { } level) LevelRows(source, level, presence, rows, warnings, ct);
        else if (source.GroupFile is { } file) GroupRows(source, file, presence, rows, warnings, ct);
        return new AssetDetails(source.Name, rows, warnings);
    }

    private static void LevelRows(LvlSource source, RfLevel level, Func<string, AssetPresence>? presence, List<AssetDetailRow> rows, List<string> warnings, CancellationToken ct)
    {
        var missing = new List<string>();
        if (source.Bytes.Length > 0)
        {
            RflSummary? summary = null;
            try { summary = RflReader.ReadSummary(source.Bytes, source.Name); }
            catch (Cairn.Formats.AssetFormatException ex) { warnings.Add("The level's summary could not be read: " + ex.Message); }
            if (summary is not null)
            {
                // a Red Faction II level's rows come from the summary reader's RF2 layouts the same way
                foreach (var row in RflFacts.Rows(summary, presence))
                {
                    rows.Add(new AssetDetailRow(row.Section, row.Label, row.Value, row.Flagged));
                    if (row.Flagged && row.Section is "References" or "Preloads" && row.Label != "Missing") missing.Add(row.Label);
                    if (row.Flagged && row.Section == "Notes") warnings.Add(row.Value);
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        else
        {
            rows.Add(new AssetDetailRow("Level", "Name", level.Info?.Name ?? level.Header.LevelName));
            rows.Add(new AssetDetailRow("Level", "Format version", level.Version.ToString(Inv)));
        }

        const string geo = "Geometry";
        var g = level.StaticGeometry;
        rows.Add(new AssetDetailRow(geo, "Compiled triangles", N(source.TriangleCount(compiled: true))));
        rows.Add(new AssetDetailRow(geo, "Brushes", BrushSummary(level.Brushes)));
        rows.Add(new AssetDetailRow(geo, "Brush triangles", N(source.TriangleCount(compiled: false))));
        if (level.Movers.Length > 0) rows.Add(new AssetDetailRow(geo, "Movers", N(level.Movers.Length)));
        var classCounts = new Dictionary<RfFaceClass, int>();
        foreach (var face in g.Faces)
        {
            var c = RfFaceClasses.OfCompiled(face);
            foreach (var k in RfFaceClasses.All) if ((c & k) != 0) classCounts[k] = classCounts.GetValueOrDefault(k) + 1;
        }
        foreach (var k in RfFaceClasses.All)
            if (classCounts.TryGetValue(k, out int n)) rows.Add(new AssetDetailRow(geo, RfFaceClasses.DisplayName(k) + " faces", N(n)));

        const string groups = "Groups";
        var groupList = level.Groups.Length > 0 ? level.Groups : level.MovingGroups;
        if (groupList.Length > 0)
        {
            rows.Add(new AssetDetailRow(groups, "Groups", $"{N(groupList.Length)} ({N(groupList.Count(x => x.IsMoving))} moving)"));
            foreach (var group in groupList.Take(200))
            {
                string value = $"{N(group.BrushUids.Length)} brushes, {N(group.ObjectUids.Length)} objects";
                if (group.Moving is { } moving) value += $"; moves ({N(moving.Keyframes.Length)} keyframes, {MovementText(moving.MovementType)})";
                rows.Add(new AssetDetailRow(groups, group.Name.Length > 0 ? group.Name : "(no name)", value));
            }
        }
        ct.ThrowIfCancellationRequested();

        if (level.Lightmaps.Length > 0) rows.Add(new AssetDetailRow(geo, "Lightmaps", N(level.Lightmaps.Length) + " (not drawn in the preview)"));
        if (level.OpaqueSections.Length > 0)
            rows.Add(new AssetDetailRow("Reading", "Kept as stored", string.Join(", ", level.OpaqueSections.Select(s => s.Name).Distinct().Take(12))));
        foreach (var problem in level.Problems)
        {
            rows.Add(new AssetDetailRow("Reading", "Problem", problem, Flagged: true));
            warnings.Add(problem);
        }
        if (level.IsTruncated) warnings.Add("The file ends early; everything before the end is shown.");
        if (missing.Count > 0)
            warnings.Add((missing.Count == 1 ? "1 file the level refers to is" : $"{missing.Count:N0} files the level refers to are")
                + " not beside it or in the game data: " + string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? ", ..." : "."));
    }

    private static void GroupRows(LvlSource source, RfGroupFile file, Func<string, AssetPresence>? presence, List<AssetDetailRow> rows, List<string> warnings, CancellationToken ct)
    {
        const string s = "Group file";
        rows.Add(new AssetDetailRow(s, "Format version", file.Version.ToString(Inv) + (file.Version >= 300 ? " (Alpine Faction)" : "")));
        rows.Add(new AssetDetailRow(s, "Loads in", file.Version >= 300 ? "Alpine Faction's level editor" : "Red Faction's level editor and Alpine Faction's"));
        rows.Add(new AssetDetailRow(s, "File size", PreviewUi.Size(source.Bytes.LongLength)));
        rows.Add(new AssetDetailRow(s, "Groups", N(file.Groups.Length)));
        var brushes = file.AllBrushes.ToList();
        rows.Add(new AssetDetailRow(s, "Brushes", BrushSummary([.. brushes])));
        rows.Add(new AssetDetailRow(s, "Faces", N(brushes.Sum(b => b.Geometry.Faces.Length))));
        rows.Add(new AssetDetailRow(s, "Triangles", N(source.TriangleCount(compiled: false))));
        int objects = file.Groups.Sum(g => g.Objects.Count);
        rows.Add(new AssetDetailRow(s, "Objects", N(objects)));
        if (file.BrushMetadata is not null) rows.Add(new AssetDetailRow(s, "Alpine brush data", "yes"));
        if (file.OpaqueChunks.Length > 0) rows.Add(new AssetDetailRow(s, "Alpine data kept as stored", string.Join(", ", file.OpaqueChunks.Select(c => c.Name).Distinct().Take(12))));

        const string groups = "Groups";
        foreach (var group in file.Groups.Take(400))
        {
            string value = $"{N(group.Brushes.Length)} brushes, {N(group.Objects.Count)} objects";
            if (group.Moving is { } moving) value += $"; moves ({N(moving.Keyframes.Length)} keyframes, {MovementText(moving.MovementType)})";
            rows.Add(new AssetDetailRow(groups, group.Name.Length > 0 ? group.Name : "(no name)", value));
        }

        const string kinds = "Objects";
        foreach (var kind in file.Groups.SelectMany(g => g.Objects.All()).GroupBy(LvlMarkers.TypeTitle).OrderByDescending(k => k.Count()))
            rows.Add(new AssetDetailRow(kinds, kind.Key, N(kind.Count())));
        ct.ThrowIfCancellationRequested();

        const string refs = "Textures";
        var textures = brushes.SelectMany(b => b.Geometry.Textures).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = new List<string>();
        foreach (var t in textures)
        {
            var where = presence?.Invoke(t) ?? AssetPresence.Unknown;
            if (where == AssetPresence.Missing) missing.Add(t);
            rows.Add(new AssetDetailRow(refs, t, where switch
            {
                AssetPresence.SamePackfile => "beside the file",
                AssetPresence.GameData => "in the game data",
                AssetPresence.Missing => "missing",
                _ => "not checked",
            }, where == AssetPresence.Missing));
        }
        foreach (var problem in file.Problems)
        {
            rows.Add(new AssetDetailRow("Reading", "Problem", problem, Flagged: true));
            warnings.Add(problem);
        }
        if (missing.Count > 0)
            warnings.Add((missing.Count == 1 ? "1 texture is" : $"{missing.Count:N0} textures are") + " not beside the file or in the game data: "
                + string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? ", ..." : "."));
    }

    private static string BrushSummary(IReadOnlyList<RfBrush> brushes)
    {
        if (brushes.Count == 0) return "0";
        int air = brushes.Count(b => b.IsAir), detail = brushes.Count(b => b.IsDetail), portal = brushes.Count(b => b.IsPortal);
        var parts = new List<string>();
        if (air > 0) parts.Add($"{N(air)} air");
        if (detail > 0) parts.Add($"{N(detail)} detail");
        if (portal > 0) parts.Add($"{N(portal)} portal");
        return N(brushes.Count) + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "");
    }

    internal static string MovementText(int type) => type switch
    {
        1 => "one way",
        2 => "ping-pong once",
        3 => "ping-pong forever",
        4 => "loop once",
        5 => "loop forever",
        6 => "lift",
        _ => "movement " + type.ToString(Inv),
    };

    private static string N(long n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
