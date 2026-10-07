using System.Globalization;

namespace Cairn.Formats.Rfl;

/// <summary>One line of a details pane: a group heading, a label and its value.</summary>
/// <param name="Section">The group the row belongs to ("Level", "Properties", "References").</param>
/// <param name="Label">What the value is.</param>
/// <param name="Value">The value, ready to show.</param>
/// <param name="Flagged">True for something the user should look at (a missing file, a damaged section).</param>
public sealed record RflFactRow(string Section, string Label, string Value, bool Flagged = false);

/// <summary>Turns an <see cref="RflSummary"/> into ordered rows for a details pane.</summary>
public static class RflFacts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Rows in pane order: identity, properties, statistics, object classes, Alpine Faction settings,
    /// referenced files, preloads, notes and the section list.
    /// </summary>
    /// <param name="summary">The level.</param>
    /// <param name="presence">
    /// Tells for a referenced file name whether it is in the same packfile, in the game data or
    /// missing; null when nothing can be checked. Missing files are flagged.
    /// </param>
    /// <param name="zone">The time zone times are shown in; the local zone when null.</param>
    public static IReadOnlyList<RflFactRow> Rows(RflSummary summary, Func<string, AssetPresence>? presence, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        zone ??= TimeZoneInfo.Local;
        var rows = new List<RflFactRow>();
        void Add(string section, string label, string? value, bool flagged = false)
        {
            if (!string.IsNullOrEmpty(value)) rows.Add(new RflFactRow(section, label, value, flagged));
        }

        // Identity.
        const string level = "Level";
        Add(level, "Name", summary.LevelName ?? "(none)");
        Add(level, "Name in header", summary.HeaderLevelName);
        Add(level, "Author", summary.Author?.Trim());
        if (summary.SavedUtc is { } saved) Add(level, "Saved", FormatTime(saved, zone));
        Add(level, "Editor's date", summary.DateText);
        Add(level, "Format version", summary.VersionLabel);
        Add(level, "Loads in", summary.Era switch
        {
            RflEra.Stock10 or RflEra.Stock12 => "Red Faction 1.2 and Alpine Faction",
            RflEra.Alpine => RflReader.AlpineReleaseFor(summary.Version) is { } since
                ? $"Alpine Faction {since} or later"
                : $"Alpine Faction newer than {RflReader.AlpineReleaseFor(RflReader.NewestKnownVersion)}",
            RflEra.PlayStation2 => "PlayStation 2 build only",
            RflEra.PreRelease => "Red Faction (pre-release format)",
            _ => "No released game",
        });
        Add(level, "Requires mod", summary.ModName);
        if (summary.IsMultiplayer is { } mp) Add(level, "Mode", mp ? "Multiplayer" : "Single player");
        if (summary.HasMovers is { } movers) Add(level, "Has movers", YesNo(movers));
        Add(level, "File size", FormatBytes(summary.FileSize));
        if (summary.IsTruncated) Add(level, "Truncated", "yes: the file ends early", flagged: true);

        // Stock level properties.
        const string props = "Properties";
        if (summary.Properties is { } p)
        {
            Add(props, "Ambient light", p.AmbientColor.ToString());
            Add(props, "Directional ambient", YesNo(p.DirectionalAmbient));
            Add(props, "Fog", p.HasFog
                ? $"colour {p.FogColor}, from {Num(p.FogNear)} to {Num(p.FogFar)}"
                : "none");
            Add(props, "Geomod texture", p.GeomodTexture.Length > 0 ? p.GeomodTexture : "(none)");
            Add(props, "Geomod hardness", summary.Version < 304 && p.Hardness == 0
                ? "0 (the game uses 55)"
                : p.Hardness.ToString(Inv));
        }
        if (summary.DashLightmapsFullDepth is { } dash) Add(props, "Dash Faction full-depth lightmaps", YesNo(dash != 0));
        if (summary.HasRedPlusData) Add(props, "Editor data", "contains RED+ editor data");
        if (summary.HasGlacierData) Add(props, "Editor data", "contains Glacier editor data");

        // Statistics.
        const string stats = "Statistics";
        if (summary.Rooms is { } rooms)
        {
            Add(stats, "Rooms", $"{N(rooms)} ({N(summary.SkyRooms ?? 0)} sky, {N(summary.LiquidRooms ?? 0)} liquid)");
            Add(stats, "Faces", N(summary.Faces ?? 0));
            Add(stats, "Vertices", N(summary.Vertices ?? 0));
            Add(stats, "Portals", N(summary.Portals ?? 0));
            Add(stats, "Geometry textures", N(summary.GeometryTextures ?? 0));
        }
        foreach (var c in summary.Counts)
        {
            string value = N(c.Count);
            if (c.Label == "Lights" && c.Count > 0)
                value += $" ({N(summary.OmniLights)} omni, {N(summary.SpotLights)} spot, {N(summary.TubeLights)} tube)";
            else if (c.Label == "Respawn points" && c.Count > 0)
                value += $" ({N(summary.RespawnRed)} red, {N(summary.RespawnBlue)} blue, {N(summary.RespawnBot)} bot)";
            Add(stats, c.Label, value);
        }
        Add(stats, "Player start", YesNo(summary.HasPlayerStart));

        ClassRows(rows, "Entity classes", summary.EntityClasses);
        ClassRows(rows, "Item classes", summary.ItemClasses);
        ClassRows(rows, "Clutter classes", summary.ClutterClasses);
        ClassRows(rows, "Event classes", summary.EventClasses);

        if (summary.Alpine is { } a) AlpineRows(rows, a);
        if (summary.HasAlpineLightmaps) rows.Add(new RflFactRow("Alpine Faction", "Alpine lightmaps", "yes"));

        // References, with where each one is found.
        const string refs = "References";
        if (summary.References.Count > 0)
        {
            var found = summary.References.Select(r => (Ref: r, Where: presence?.Invoke(r.Name) ?? AssetPresence.Unknown)).ToList();
            int missing = found.Count(f => f.Where == AssetPresence.Missing);
            Add(refs, "Files referenced", N(found.Count));
            if (presence is not null) Add(refs, "Missing", N(missing), flagged: missing > 0);
            // Missing files first, so they are seen without scrolling.
            foreach (var (r, where) in found.OrderBy(f => f.Where == AssetPresence.Missing ? 0 : 1))
            {
                string value = $"{KindName(r.Kind)}; {string.Join(", ", r.Places)}";
                if (where != AssetPresence.Unknown) value += "; " + PresenceText(where);
                Add(refs, r.Name, value, flagged: where == AssetPresence.Missing);
            }
        }

        // RED's preload lists: shown as counts, plus any missing names when they can be checked.
        const string pre = "Preloads";
        var lists = new (string Label, IReadOnlyList<string> Names)[]
        {
            ("Bitmaps", summary.Preloads.Bitmaps), ("Character meshes", summary.Preloads.CharacterMeshes),
            ("Animations", summary.Preloads.Animations), ("Static meshes", summary.Preloads.StaticMeshes),
            ("Effects", summary.Preloads.Effects),
        };
        foreach (var (label, names) in lists)
        {
            if (names.Count > 0) Add(pre, label, N(names.Count));
        }
        if (presence is not null)
        {
            foreach (var name in lists.SelectMany(l => l.Names).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (presence(name) == AssetPresence.Missing) Add(pre, name, "preloaded but missing", flagged: true);
            }
        }

        foreach (var note in summary.Notes) Add("Notes", "Note", note, flagged: true);

        foreach (var s in summary.Sections)
        {
            string value = $"{FormatBytes(s.Length)} at offset {N(s.Offset)}; {StatusText(s.Status)}";
            if (!string.IsNullOrEmpty(s.Detail)) value += $" ({s.Detail})";
            Add("Sections", s.Name, value, flagged: s.Status is RflSectionStatus.Damaged or RflSectionStatus.Truncated);
        }
        return rows;
    }

    /// <summary>"2001-08-25 06:23:29 (UTC+02:00)": the time in <paramref name="zone"/> with its offset.</summary>
    public static string FormatTime(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var offset = local.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{local.ToString("yyyy-MM-dd HH:mm:ss", Inv)} (UTC{sign}{offset.Duration():hh\\:mm})";
    }

    private static void ClassRows(List<RflFactRow> rows, string section, IReadOnlyDictionary<string, int> classes)
    {
        foreach (var (name, count) in classes.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new RflFactRow(section, name.Length > 0 ? name : "(no class)", N(count)));
        }
    }

    private static void AlpineRows(List<RflFactRow> rows, AlpineLevelProperties a)
    {
        const string s = "Alpine Faction";
        void Add(string label, string? value)
        {
            if (value is not null) rows.Add(new RflFactRow(s, label, value));
        }
        static string Opt(bool? value, bool fallback) => value is { } v ? YesNo(v) : $"{YesNo(fallback)} (default)";

        Add("Properties version", a.ChunkVersion.ToString(Inv) + (a.EndedEarly ? " (shorter than its version; the rest are defaults)" : ""));
        if (a.ChunkVersion >= 1) Add("Legacy cyclic timers", Opt(a.LegacyCyclicTimers, true));
        if (a.ChunkVersion >= 2)
        {
            Add("Legacy movers", Opt(a.LegacyMovers, true));
            Add("Starts with headlamp", Opt(a.StartsWithHeadlamp, true));
        }
        if (a.ChunkVersion >= 3)
        {
            Add("Override mesh ambient", Opt(a.OverrideStaticMeshAmbient, false));
            Add("Mesh ambient modifier", a.StaticMeshAmbientModifier is { } m ? Num(m) : "2 (default)");
        }
        if (a.ChunkVersion >= 4)
        {
            Add("RF2-style geomod", Opt(a.Rf2StyleGeomod, false));
            if (a.GeoableBrushes is { } g) Add("Geoable brushes", N(g));
            if (a.BreakableBrushes is { } b) Add("Breakable brushes", N(b));
            if (a.HoldOpenKeyframes is { } h) Add("Hold-open keyframes", N(h));
        }
        if (a.SunEnabled is { } sun)
        {
            Add("Sun", sun ? "on" : "off");
            if (a.SunYaw is { } yaw && a.SunPitch is { } pitch) Add("Sun direction", $"yaw {Num(yaw)}, pitch {Num(pitch)}");
            if (a.SunColor is { } colour) Add("Sun colour", colour.ToString());
            if (a.SunIntensity is { } intensity) Add("Sun intensity", Num(intensity));
            if (a.SunSpread is { } spread) Add("Sun spread", Num(spread));
            Add("Sun casts baked shadows", a.SunCastsBakedShadows is { } v1 ? YesNo(v1) : null);
            Add("Sun affects meshes", a.SunAffectsMeshes is { } v2 ? YesNo(v2) : null);
            Add("Sun mesh mode", a.SunMeshMode?.ToString(Inv));
            Add("Sun drives shadow map", a.SunDrivesShadowMap is { } v3 ? YesNo(v3) : null);
            Add("Legacy lighting", a.LegacyLighting is { } v4 ? YesNo(v4) : null);
            Add("High-resolution lightmaps", a.HighResolutionLightmaps is { } v5 ? YesNo(v5) : null);
            Add("Liquids occlude", a.LiquidOccludes is { } v6 ? YesNo(v6) : null);
            Add("Invisible faces occlude", a.InvisibleFacesOcclude is { } v7 ? YesNo(v7) : null);
            Add("Alpha faces occlude", a.AlphaFacesOcclude is { } v8 ? YesNo(v8) : null);
            Add("Brushes casting no shadow", a.NoShadowBrushes is { } ns ? N(ns) : null);
            Add("Meshes occlude", a.MeshesOcclude is { } v9 ? YesNo(v9) : null);
        }
        if (a.LightmapDensity is { } density)
            Add("Lightmap density", density switch { 0 => "default", 255 => "off", _ => $"{density} texels per unit" });
        if (a.LightmapFlags is not null) Add("Direct3D 11 lightmaps only", YesNo(a.D3D11OnlyLightmaps));
        Add("Lightmap compression", a.LightmapCompression?.ToString(Inv));
        if (a.FlightCeilingEnabled is { } ceiling)
            Add("Flight ceiling", ceiling ? $"on at {Num(a.FlightCeilingY ?? 0)}" : "off");
        if (a.MinimapEnabled is { } minimap)
        {
            Add("Minimap", minimap ? "on" : "off");
            if (!string.IsNullOrEmpty(a.MinimapBitmap)) Add("Minimap bitmap", a.MinimapBitmap);
            if (a.MinimapCutHeight is { } cut) Add("Minimap cut height", Num(cut));
        }
    }

    private static string KindName(RflReferenceKind kind) => kind switch
    {
        RflReferenceKind.Texture => "texture",
        RflReferenceKind.Mesh => "mesh",
        RflReferenceKind.Clip => "animation",
        RflReferenceKind.Effect => "effect",
        RflReferenceKind.Sound => "sound",
        RflReferenceKind.Music => "music",
        RflReferenceKind.Video => "video",
        RflReferenceKind.Level => "linked level",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string PresenceText(AssetPresence where) => where switch
    {
        AssetPresence.SamePackfile => "in this packfile",
        AssetPresence.GameData => "in the game data",
        AssetPresence.Missing => "missing",
        _ => "not checked",
    };

    private static string StatusText(RflSectionStatus status) => status switch
    {
        RflSectionStatus.Read => "read",
        RflSectionStatus.Skipped => "skipped",
        RflSectionStatus.Unknown => "unknown, skipped",
        RflSectionStatus.Damaged => "damaged",
        RflSectionStatus.Truncated => "runs past the end of the file",
        _ => status.ToString(),
    };

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string N(long value) => value.ToString("N0", Inv);

    private static string Num(float value) => value.ToString("0.###", Inv);

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", Inv)} KB",
        _ => $"{(bytes / (1024.0 * 1024)).ToString("0.#", Inv)} MB",
    };
}
