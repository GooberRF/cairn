using System.Globalization;
using Cairn.Formats.Imaging;
using Cairn.Formats.Rf2;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Conversion;

/// <summary>A file a conversion writes beside its output (an RF2 texture kept with its own name).</summary>
/// <param name="FileName">The file name, with no folder.</param>
/// <param name="Bytes">The file's bytes.</param>
public sealed record LvlSideFile(string FileName, byte[] Bytes);

/// <summary>
/// Turns a Red Faction II level into the Red Faction level every target writes, before the shared converters run:
/// texture names translated (or kept, with the RF2 textures written beside the output), RF2 lights rescaled and
/// coronas made into lights (with fill lights and the lightmap multiplier, as REDUX does), brushes made RF1-safe
/// (geoable brushes, RF2-only flags, breakable invisible trigger brushes), clutter placed as Alpine meshes, jump pads
/// rescaled, and faces without RF1 lightmap surfaces. Each change, and each RF2 thing that has no Red Faction
/// counterpart, is a report entry.
/// </summary>
internal static class Rf2LevelConversion
{
    private const int Listed = 8;

    /// <summary>The prepared level, the options to convert it with, and what the preparation reports and writes.</summary>
    public sealed record Prepared(RfLevel Level, LvlConvertOptions Options, ImmutableArray<LvlConvertIssue> Issues, ImmutableArray<LvlSideFile> SideFiles);

    public static Prepared Prepare(RfLevel level, LvlConvertOptions options, CancellationToken ct)
    {
        var r = new LvlReportBuilder();
        var rf2 = options.Rf2;
        r.Info(LvlConvertCodes.Rf2Faces, "This is a Red Faction II level; it was converted to a Red Faction level first. Entries LC090 to LC097 list what that changed.");

        Faces(level, r, ct);
        // New lights get uids above every uid of the source, also those of brushes left out below.
        int firstUid = MaxUid(level) + 1;
        level = Brushes(level, rf2, r);
        level = Lights(level, rf2, firstUid, r, ct);
        level = Objects(level, rf2, r);
        var (named, loader, sides) = Textures(level, options, r);
        Ambient(named, r);

        var prepared = options with { TextureLoader = Chain(options.TextureLoader, loader) };
        return new Prepared(named, prepared, r.Build().Issues, sides);
    }

    // ── Faces ──────────────────────────────────────────────────────────────

    /// <summary>
    /// RF2 faces already carry the RF1 meaning of their flags, scroll speeds and no lightmap surfaces (the reader maps
    /// them); the report counts what that mapping left behind.
    /// </summary>
    private static void Faces(RfLevel level, LvlReportBuilder r, CancellationToken ct)
    {
        var dropped = new SortedDictionary<uint, int>();
        int scrolled = 0, skyScroll = 0, flagWithoutSpeed = 0, faces = 0;

        void Count(RfGeometry g)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var f in g.Faces)
            {
                faces++;
                uint rest = f.RawFlags & ~RflLevelReader.Rf2ToRf1FaceFlags & ~RflLevelReader.Rf2InlineScrollFlag;
                for (uint bit = 1; rest != 0; bit <<= 1)
                {
                    if ((rest & bit) == 0) continue;
                    dropped[bit] = dropped.GetValueOrDefault(bit) + 1;
                    rest &= ~bit;
                }
                if ((f.RawFlags & RflLevelReader.Rf2InlineScrollFlag) != 0)
                {
                    if (f.Scroll is { } s && (s.X != 0 || s.Y != 0)) scrolled++;
                    else if ((f.RawFlags & (uint)RfFaceFlags.ShowSky) != 0) skyScroll++;
                }
                else if ((f.RawFlags & (uint)RfFaceFlags.ScrollTexture) != 0)
                {
                    flagWithoutSpeed++;
                }
            }
        }

        Count(level.StaticGeometry);
        foreach (var b in level.Brushes) Count(b.Geometry);
        foreach (var b in level.Movers) Count(b.Geometry);
        if (dropped.Count > 0)
        {
            string bits = string.Join(", ", dropped.Select(kv => $"0x{kv.Key:X} on {N(kv.Value)}"));
            r.Info(LvlConvertCodes.Rf2Faces, $"Red Faction II face flags with no Red Faction meaning were dropped ({bits} faces); the raw 32-bit flags stay in the face data.", LvlIssueKind.Dropped);
        }
        if (scrolled > 0)
            r.Info(LvlConvertCodes.Rf2Faces, $"{LvlReportBuilder.Count(scrolled, "face")} scroll: Red Faction II stores seconds per texture cycle, so the speed is 1 / period (REDUX's reading of the field; not checked in game).", LvlIssueKind.Approximated);
        if (skyScroll > 0)
            r.Info(LvlConvertCodes.Rf2Faces, $"{LvlReportBuilder.Count(skyScroll, "face")} that show the sky store a scroll; it was dropped, as the sky is drawn instead of their texture.", LvlIssueKind.Dropped);
        if (flagWithoutSpeed > 0)
            r.Info(LvlConvertCodes.Rf2Faces, $"{LvlReportBuilder.Count(flagWithoutSpeed, "face")} have the scroll flag but no scroll speed in Red Faction II; they do not scroll.", LvlIssueKind.Approximated);
        if (faces > 0)
            r.Info(LvlConvertCodes.Rf2Faces, "Red Faction II lights its levels with vertex colours, not lightmaps: faces get no lightmap surface, so a level made from this one needs its lighting calculated again.", LvlIssueKind.Dropped);
    }

    // ── Brushes ────────────────────────────────────────────────────────────

    private static RfLevel Brushes(RfLevel level, LvlRf2Options rf2, LvlReportBuilder r)
    {
        var geoable = new List<int>();
        var breakable = new List<int>();
        var droppedTriggers = new List<int>();
        var rf2Bits = new SortedDictionary<uint, int>();
        int undetailed = 0;

        RfBrush? Fix(RfBrush b, bool count)
        {
            uint raw = (uint)b.SolidFlags;
            bool isGeoable = (raw & (uint)RfSolidFlags.Rf2Geoable) != 0;
            if (count && rf2.DropInvisibleTriggerBrushes && IsBreakableInvisibleTrigger(b))
            {
                droppedTriggers.Add(b.Uid);
                return null;
            }
            var flags = b.Rf1SolidFlags;
            if (count)
            {
                uint rest = raw & ~(uint)flags;
                for (uint bit = 1; rest != 0; bit <<= 1)
                {
                    if ((rest & bit) == 0) continue;
                    rf2Bits[bit] = rf2Bits.GetValueOrDefault(bit) + 1;
                    rest &= ~bit;
                }
                if (isGeoable) geoable.Add(b.Uid);
                else if ((flags & RfSolidFlags.Detail) != 0 && b.Life >= 0) breakable.Add(b.Uid);
            }
            if (isGeoable && rf2.GeoableWithoutDetail && (flags & (RfSolidFlags.Air | RfSolidFlags.Portal)) == 0 && (flags & RfSolidFlags.Detail) != 0)
            {
                flags &= ~RfSolidFlags.Detail;
                if (count) undetailed++;
            }
            var fixedBrush = b with { SolidFlags = flags };
            // Alpine marks geoable brushes itself; RF2's own life value would make them breakable as well.
            if (isGeoable) fixedBrush = fixedBrush with { Life = -1, Alpine = new RfAlpineBrushData(RfAlpineBrushFlags.Geoable, 0, []) };
            return fixedBrush;
        }

        var brushes = level.Brushes.Select(b => Fix(b, count: true)).OfType<RfBrush>().ToList();
        var movers = level.Movers.Select(b => Fix(b, count: false)).OfType<RfBrush>().ToList();
        level = level with { Brushes = [.. brushes], Movers = [.. movers] };

        if (rf2Bits.Count > 0)
        {
            string bits = string.Join(", ", rf2Bits.Select(kv => $"0x{kv.Key:X} on {LvlReportBuilder.Count(kv.Value, "brush", "brushes")}"));
            r.Info(LvlConvertCodes.Rf2Brushes, $"Solid flags only Red Faction II has were dropped ({bits}); brushes keep portal, air, detail and emits steam.", LvlIssueKind.Dropped);
        }
        if (geoable.Count > 0)
            r.Info(LvlConvertCodes.Rf2Brushes, $"{LvlReportBuilder.Count(geoable.Count, "geoable brush", "geoable brushes")} (RF2 solid flag 0x20) became Alpine geoable brushes with life -1: {List(geoable)}."
                + (undetailed > 0 ? $" {LvlReportBuilder.Count(undetailed, "of them", "of them")} lost the detail flag so Red Faction can geomod {(undetailed == 1 ? "it" : "them")}." : ""), LvlIssueKind.Approximated);
        if (breakable.Count > 0)
            r.Info(LvlConvertCodes.Rf2Brushes, $"{LvlReportBuilder.Count(breakable.Count, "detail brush", "detail brushes")} have a life of their own (breakable in Red Faction II): {List(breakable)}.");
        if (droppedTriggers.Count > 0)
            r.Info(LvlConvertCodes.Rf2Brushes, $"{LvlReportBuilder.Count(droppedTriggers.Count, "breakable invisible trigger brush", "breakable invisible trigger brushes")} (an RF2-only kind with no Red Faction use) were left out: {List(droppedTriggers)}.", LvlIssueKind.Dropped);
        return level;
    }

    /// <summary>REDUX's test for RF2's breakable invisible trigger brushes: flag 0x200, a life, and at most two faces not textured "invisible".</summary>
    internal static bool IsBreakableInvisibleTrigger(RfBrush b)
    {
        if (((uint)b.SolidFlags & 0x200) == 0 || b.Life < 0) return false;
        var g = b.Geometry;
        if (g.Faces.IsEmpty) return false;
        int invisible = g.Faces.Count(f => g.TextureOf(f) is { } t && t.Contains("invisible", StringComparison.OrdinalIgnoreCase));
        return g.Faces.Length - invisible <= 2;
    }

    // ── Lights and coronas ─────────────────────────────────────────────────

    private static RfLevel Lights(RfLevel level, LvlRf2Options rf2, int nextUid, LvlReportBuilder r, CancellationToken ct)
    {
        var lights = level.Objects.Lights.Select(l => Rf2Lighting.Rescale(l, rf2.LightRangeScale, rf2.LightScale)).ToList();
        int original = lights.Count;
        if (original > 0)
            r.Info(LvlConvertCodes.Rf2Lights, $"{LvlReportBuilder.Count(original, "light")} rescaled from Red Faction II's inverse-square falloff to Red Faction's linear one: range x {F(rf2.LightRangeScale)}, intensity x {F(rf2.LightScale)} (REDUX's defaults are 3 and 1).", LvlIssueKind.Approximated);

        var coronas = level.Objects.Coronas;
        var lighting = level.Rf2?.VertexLighting;
        int coronaLights = 0, spots = 0, black = 0;
        if (rf2.CoronasToLights && coronas.Length > 0)
        {
            float range = rf2.CoronaRange ?? LvlRf2Options.FallbackCoronaRange, scale = rf2.CoronaScale ?? 1f;
            string how;
            if (rf2.CoronaRange is not null && rf2.CoronaScale is not null)
            {
                how = "the range and intensity scale given";
            }
            else if (lighting is not null && Rf2Lighting.FitCoronas(lighting, lights, coronas, ct) is { } fit)
            {
                range = rf2.CoronaRange ?? fit.Range;
                scale = rf2.CoronaScale ?? fit.Scale;
                how = $"a fit to {fit.Samples:N0} baked corners far from other lights (range {F(fit.Range)} m, intensity x {F(fit.Scale)})";
            }
            else
            {
                how = $"REDUX's fallback (too few baked corners far from other lights to fit: {F(LvlRf2Options.FallbackCoronaRange)} m, x 1)";
            }
            int first = nextUid;
            foreach (var c in coronas)
            {
                if (Rf2Lighting.FromCorona(c, nextUid, range, scale * rf2.LightScale) is not { } light)
                {
                    black++;
                    continue;
                }
                nextUid++;
                lights.Add(light);
                coronaLights++;
                if (light.Type == RfLightType.Spot) spots++;
            }
            if (coronaLights > 0)
                r.Info(LvlConvertCodes.Rf2Coronas, $"{LvlReportBuilder.Count(coronaLights, "corona")} became lights, as Red Faction II bakes coronas into its lighting ({spots} spot, {coronaLights - spots} point; uids {first} to {nextUid - 1}); range {F(range)} m and intensity x {F(scale)} from {how}."
                    + (black > 0 ? $" {LvlReportBuilder.Count(black, "corona")} gave no light (black, or no intensity)." : ""), LvlIssueKind.Approximated);
        }

        int fill = 0;
        if (rf2.FillLights && lighting is not null && (lights.Count > 0 || coronas.Length > 0))
        {
            var added = Rf2Lighting.FillLights(lighting!, lights, coronas, ref nextUid, ct);
            fill = added.Count;
            lights.AddRange(added);
            if (fill > 0)
                r.Info(LvlConvertCodes.Rf2FillLights, $"{LvlReportBuilder.Count(fill, "fill light")} added where the baked colours show light that no light or corona explains (sky or bounced light in Red Faction II's bake), at most 0.5 intensity each.", LvlIssueKind.Approximated);
        }

        float multiplier = level.Rf2?.Properties?.LightmapMultiplier ?? 1f;
        if (rf2.ApplyLightmapMultiplier && multiplier != 1f && float.IsFinite(multiplier))
        {
            for (int i = 0; i < lights.Count; i++)
                lights[i] = lights[i] with { OnIntensity = lights[i].OnIntensity * multiplier, OffIntensity = lights[i].OffIntensity * multiplier };
            r.Info(LvlConvertCodes.Rf2Lights, $"Every light's intensity was multiplied by the level's lightmap multiplier ({F(multiplier)}).", LvlIssueKind.Approximated);
        }
        if (original + coronaLights + fill > 0)
            r.Info(LvlConvertCodes.Rf2Lights, $"Lights: {original} from the level, {coronaLights} from coronas, {fill} fill; Red Faction lights only approximate Red Faction II's light bake, so check the result in the editor.");

        var keptCoronas = coronas;
        if (rf2.KeepCoronas)
        {
            keptCoronas = [.. coronas.Select(c => c with { ConeAngle = c.ConeAngle * 2f, Color = Rf2Lighting.CoronaColor(c) })];
            if (coronas.Length > 0)
                r.Info(LvlConvertCodes.Rf2Coronas, $"{LvlReportBuilder.Count(coronas.Length, "corona")} kept as Alpine coronas with the cone angle doubled (Red Faction II stores it halved); only Alpine Faction draws them.", LvlIssueKind.Approximated);
        }
        else
        {
            keptCoronas = [];
            if (coronas.Length > 0)
                r.Info(LvlConvertCodes.Rf2Coronas, $"{LvlReportBuilder.Count(coronas.Length, "corona")} were not kept as coronas.", LvlIssueKind.Dropped);
        }
        return level with { Objects = level.Objects with { Lights = [.. lights], Coronas = keptCoronas } };
    }

    // ── Other objects ──────────────────────────────────────────────────────

    private static RfLevel Objects(RfLevel level, LvlRf2Options rf2, LvlReportBuilder r)
    {
        var objects = level.Objects;

        // Jump pads: RF2's gravity is stronger, so the same jump needs less push in Red Faction.
        int pads = 0;
        var push = objects.PushRegions.Select(p =>
        {
            if ((p.Flags & 0x40) == 0) return p;
            pads++;
            return p with { Strength = p.Strength * rf2.JumpPadScale };
        }).ToList();
        if (pads > 0)
            r.Info(LvlConvertCodes.Rf2Objects, $"{LvlReportBuilder.Count(pads, "jump pad")} pushed x {F(rf2.JumpPadScale)} (Red Faction II's gravity is 15, Red Faction's 9.8).", LvlIssueKind.Approximated);

        // Clutter: the class names are RF2's, so Red Faction knows none of them; as Alpine meshes they keep their look.
        var clutter = objects.Clutter.ToList();
        var meshes = objects.AlpineMeshes.ToList();
        if (clutter.Count > 0 && rf2.ClutterAsMeshes)
        {
            if (rf2.ClutterClasses is { } classes)
            {
                var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var kept = new List<RfClutter>();
                foreach (var c in clutter)
                {
                    if (!classes.TryGetValue(c.ClassName, out var cls))
                    {
                        unknown.Add(c.ClassName);
                        kept.Add(c);
                        continue;
                    }
                    meshes.Add(new RfAlpineMesh
                    {
                        Uid = c.Uid,
                        Position = c.Position,
                        Orientation = c.Orientation,
                        ScriptName = c.ScriptName,
                        HiddenInEditor = c.HiddenInEditor,
                        MeshFile = cls.ConvertedMeshFile,
                        Collision = cls.Collision,
                        Material = 0,
                    });
                }
                int placed = clutter.Count - kept.Count;
                if (placed > 0)
                    r.Info(LvlConvertCodes.Rf2Objects, $"{LvlReportBuilder.Count(placed, "clutter object")} became Alpine static meshes named after the meshes of Red Faction II's clutter table (.v3m / .v3c names: convert those meshes too). They do not break or animate.", LvlIssueKind.Approximated);
                if (unknown.Count > 0)
                    r.Warning(LvlConvertCodes.Rf2Objects, $"{LvlReportBuilder.Count(kept.Count, "clutter object")} use classes the clutter table does not list ({List(unknown)}); they stay clutter, which Red Faction does not have.", LvlIssueKind.Dropped);
                clutter = kept;
            }
            else
            {
                r.Warning(LvlConvertCodes.Rf2Objects, $"{LvlReportBuilder.Count(clutter.Count, "clutter object")} stay clutter of Red Faction II classes ({List(clutter.Select(c => c.ClassName).Distinct(StringComparer.OrdinalIgnoreCase))}): Red Faction II's clutter table was not found, so their meshes are not known.", LvlIssueKind.Dropped);
            }
        }

        foreach (var (what, names) in new[]
        {
            ("item", objects.Items.Select(i => i.ClassName)),
            ("event", objects.Events.Select(e => e.ClassName)),
        })
        {
            var distinct = names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count > 0)
                r.Info(LvlConvertCodes.Rf2Objects, $"The {what}s keep their Red Faction II class names ({List(distinct)}); Red Faction's {what} classes differ, so check them in the editor.", LvlIssueKind.Approximated);
        }

        var notConverted = level.Sections.Where(s => s.Status == RfSectionStatus.Opaque && s.Length > 4).Select(s => s.Name).Distinct().ToList();
        if (notConverted.Count > 0)
            r.Info(LvlConvertCodes.Rf2Objects, $"Red Faction II sections whose layout is not known are not converted: {string.Join(", ", notConverted)}.", LvlIssueKind.Dropped);

        return level with { Objects = objects with { PushRegions = [.. push], Clutter = [.. clutter], AlpineMeshes = [.. meshes] } };
    }

    // ── Textures ───────────────────────────────────────────────────────────

    private static (RfLevel Level, Func<string, BgraImage?>? Loader, ImmutableArray<LvlSideFile> Sides) Textures(RfLevel level, LvlConvertOptions options, LvlReportBuilder r)
    {
        var rf2 = options.Rf2;
        var packs = rf2.TexturePacks;
        var used = UsedTextures(level);
        if (used.Count == 0) return (level, null, []);

        Func<string, string>? map = null;
        var reverse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sides = ImmutableArray<LvlSideFile>.Empty;
        if (rf2.Textures == LvlRf2TextureMode.Translate)
        {
            var translations = used.Select(Rf2TextureNames.Translate).ToList();
            var changed = translations.Where(t => t.IsTranslated).ToList();
            var byName = changed.ToDictionary(t => t.Original, t => t.Translated, StringComparer.OrdinalIgnoreCase);
            foreach (var t in changed) reverse.TryAdd(t.Translated, t.Original);
            map = name => name.Length > 0 && byName.TryGetValue(name, out var to) ? to : name;
            if (changed.Count > 0)
            {
                r.Info(LvlConvertCodes.Rf2Textures, $"Texture names translated to Red Faction names ({changed.Count:N0} of {used.Count:N0}): {List(changed.Select(t => $"{t.Original} → {t.Translated}"))}.", LvlIssueKind.Approximated);
                var weak = changed.Where(t => t.IsWeak).ToList();
                if (weak.Count > 0)
                    r.Warning(LvlConvertCodes.Rf2Textures, $"{weak.Count:N0} of those translations are uncertain (several Red Faction names end the same way and the first was taken): {List(weak.Select(t => $"{t.Original} → {t.Translated}"))}.", LvlIssueKind.Approximated);
                r.Info(LvlConvertCodes.Rf2Textures, Rf2TextureNames.TextureSetNote);
                int shared = changed.Count - reverse.Count;
                if (shared > 0)
                    r.Info(LvlConvertCodes.Rf2Textures, $"{LvlReportBuilder.Count(shared, "Red Faction II texture shares", "Red Faction II textures share")} a translated name with another; images of the output show the first one's pixels.", LvlIssueKind.Approximated);
            }
            var kept = translations.Where(t => !t.IsTranslated).Select(t => t.Original).ToList();
            if (kept.Count > 0)
                r.Info(LvlConvertCodes.Rf2Textures, $"{LvlReportBuilder.Count(kept.Count, "texture name has", "texture names have")} no Red Faction translation and keep {(kept.Count == 1 ? "its" : "their")} Red Faction II name: {List(kept)}. Convert with the Red Faction II textures kept to write them beside the output.");
        }
        else if (options.Target is LvlConvertTarget.V3m or LvlConvertTarget.Rfg)
        {
            sides = WriteOriginals(used, packs, r);
        }
        else
        {
            r.Info(LvlConvertCodes.Rf2Textures, "The Red Faction II texture names were kept; the textures themselves are the images this conversion writes.");
        }

        Func<string, BgraImage?>? loader = null;
        if (packs is not null && !packs.IsEmpty)
            loader = name => packs.Decode(reverse.TryGetValue(name, out var original) ? original : name);
        else if (options.ExportTextures && options.Target is not LvlConvertTarget.V3m and not LvlConvertTarget.Rfg)
            r.Info(LvlConvertCodes.Rf2Textures, "No Red Faction II texture packs (.peg) were given, so the textures cannot be shown; the level's own .peg sits beside it in Red Faction II.", LvlIssueKind.Dropped);

        return (map is null ? level : Rename(level, map), loader, sides);
    }

    private static ImmutableArray<LvlSideFile> WriteOriginals(IReadOnlyCollection<string> names, Cairn.Assets.PegTextureSet? packs, LvlReportBuilder r)
    {
        if (packs is null || packs.IsEmpty)
        {
            r.Warning(LvlConvertCodes.Rf2Textures, "The Red Faction II texture names were kept, but no .peg texture packs were given, so no textures were written beside the output.", LvlIssueKind.Dropped);
            return [];
        }
        var files = ImmutableArray.CreateBuilder<LvlSideFile>();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var animated = new List<string>();
        var from = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            // RF2 packs hold only uncompressed textures (16-bit, 32-bit and 8-bit palettised), so .tga loses nothing.
            if (packs.Find(name) is not { } hit || packs.ReadAsTga(name) is not { } tga)
            {
                missing.Add(name);
                continue;
            }
            string file = Path.ChangeExtension(Path.GetFileName(name), ".tga");
            if (!written.Add(file)) continue;
            files.Add(new LvlSideFile(file, tga));
            from.Add(hit.Pack);
            if (hit.Texture.IsAnimated || name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase)) animated.Add(name);
        }
        r.Info(LvlConvertCodes.Rf2Textures, $"The Red Faction II texture names were kept; {LvlReportBuilder.Count(files.Count, "texture was", "textures were")} written beside the output as .tga from {List(from)}.");
        if (animated.Count > 0)
            r.Info(LvlConvertCodes.Rf2Textures, $"{LvlReportBuilder.Count(animated.Count, "animated texture was", "animated textures were")} written as the first frame only ({List(animated)}).", LvlIssueKind.Approximated);
        if (missing.Count > 0)
            r.Warning(LvlConvertCodes.Rf2Textures, $"{LvlReportBuilder.Count(missing.Count, "texture is", "textures are")} in none of the texture packs ({List(missing)}); not written.", LvlIssueKind.Dropped);
        foreach (var (pack, reason) in packs.Unreadable)
            r.Warning(LvlConvertCodes.Rf2Textures, $"The texture pack {pack} could not be read: {reason}", LvlIssueKind.Dropped);
        return files.ToImmutable();
    }

    /// <summary>Every texture name the converted level uses: faces of every solid, decals, coronas and the geomod texture.</summary>
    private static List<string> UsedTextures(RfLevel level)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void Solid(RfGeometry g)
        {
            var usedSlots = new HashSet<int>();
            foreach (var f in g.Faces) usedSlots.Add(f.Texture);
            for (int i = 0; i < g.Textures.Length; i++)
                if (usedSlots.Contains(i) && g.Textures[i].Length > 0) set.Add(g.Textures[i]);
        }
        Solid(level.StaticGeometry);
        foreach (var b in level.Brushes) Solid(b.Geometry);
        foreach (var b in level.Movers) Solid(b.Geometry);
        foreach (var d in level.Objects.Decals) if (d.Texture.Length > 0) set.Add(d.Texture);
        foreach (var c in level.Objects.Coronas)
        {
            if (c.CoronaBitmap.Length > 0) set.Add(c.CoronaBitmap);
            if (c.VolumetricBitmap.Length > 0) set.Add(c.VolumetricBitmap);
        }
        if (level.Properties is { GeomodTexture.Length: > 0 } p) set.Add(p.GeomodTexture);
        return [.. set];
    }

    private static RfLevel Rename(RfLevel level, Func<string, string> map)
    {
        RfGeometry Solid(RfGeometry g) => g.Textures.IsEmpty ? g : g with { Textures = [.. g.Textures.Select(map)] };
        return level with
        {
            StaticGeometry = Solid(level.StaticGeometry),
            Brushes = [.. level.Brushes.Select(b => b with { Geometry = Solid(b.Geometry) })],
            Movers = [.. level.Movers.Select(b => b with { Geometry = Solid(b.Geometry) })],
            Properties = level.Properties is { } p ? p with { GeomodTexture = map(p.GeomodTexture) } : null,
            Objects = level.Objects with
            {
                Decals = [.. level.Objects.Decals.Select(d => d with { Texture = map(d.Texture) })],
                Coronas = [.. level.Objects.Coronas.Select(c => c with { CoronaBitmap = map(c.CoronaBitmap), VolumetricBitmap = map(c.VolumetricBitmap) })],
            },
        };
    }

    // ── Ambient and the rest of the level properties ───────────────────────

    private static void Ambient(RfLevel level, LvlReportBuilder r)
    {
        if (level.Properties is not { } p) return;
        var a = p.AmbientColor;
        string text = $"Red Faction II ambient light is {a.R}, {a.G}, {a.B}";
        if (level.Rf2?.VertexLighting is { } lighting && Rf2Lighting.MedianBakedColor(lighting) is { } median)
        {
            int Rec(byte raw, byte baked) => Math.Min(255, Math.Max(raw, (int)MathF.Round(baked * 1.5f)));
            text += $" and the median baked colour {median.R}, {median.G}, {median.B}; REDUX suggests a Red Faction ambient of {Rec(a.R, median.R)}, {Rec(a.G, median.G)}, {Rec(a.B, median.B)} (set it in the level properties).";
        }
        else
        {
            text += ".";
        }
        r.Info(LvlConvertCodes.Rf2Ambient, text);
        if (p.FogFar > 0)
            r.Info(LvlConvertCodes.Rf2Ambient, $"Red Faction II fog: colour {p.FogColor.R}, {p.FogColor.G}, {p.FogColor.B}, from {F(p.FogNear)} to {F(p.FogFar)} m.");
        if (level.Rf2?.Properties is { HardlightIntensity: > 0 } rf2)
            r.Info(LvlConvertCodes.Rf2Ambient, $"Red Faction II's hardlight tint ({rf2.HardlightColor.R}, {rf2.HardlightColor.G}, {rf2.HardlightColor.B}, intensity {rf2.HardlightIntensity}) and its sun have no Red Faction counterpart.", LvlIssueKind.Dropped);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static int MaxUid(RfLevel level)
    {
        int max = 0;
        foreach (var b in level.Brushes) max = Math.Max(max, b.Uid);
        foreach (var b in level.Movers) max = Math.Max(max, b.Uid);
        foreach (var o in level.Objects.All()) max = Math.Max(max, o.Uid);
        foreach (var g in level.MovingGroups.Concat(level.Groups))
            if (g.Moving is { } m)
                foreach (var k in m.Keyframes) max = Math.Max(max, k.Uid);
        return max;
    }

    private static Func<string, BgraImage?>? Chain(Func<string, BgraImage?>? first, Func<string, BgraImage?>? second) =>
        first is null ? second : second is null ? first : name => first(name) ?? second(name);

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string List(IEnumerable<object> items)
    {
        var all = items.Select(i => Convert.ToString(i, CultureInfo.InvariantCulture) ?? "").ToList();
        return string.Join(", ", all.Take(Listed)) + (all.Count > Listed ? $" and {all.Count - Listed:N0} more" : "");
    }

    private static string List(IEnumerable<string> items) => List(items.Cast<object>());

    private static string List(IEnumerable<int> items) => List(items.Cast<object>());
}

public sealed partial class LvlConversion
{
    /// <summary>The same conversion with the RF2 preparation's entries leading the report and its side files written beside the output.</summary>
    internal LvlConversion WithRf2(Rf2LevelConversion.Prepared prepared)
    {
        var report = Report with { Issues = [.. prepared.Issues, .. Report.Issues] };
        if (Output is null || _write is null) return new LvlConversion(Target, report, Output, _write, _plan);
        var sides = prepared.SideFiles;
        if (sides.IsEmpty) return new LvlConversion(Target, report, Output, _write, _plan);
        var write = _write;
        var plan = _plan;
        IReadOnlyList<string> Sides(string path)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            return [.. sides.Select(s => Path.Combine(folder, s.FileName))];
        }
        return new LvlConversion(Target, report, Output,
            path =>
            {
                var files = write(path).ToList();
                string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                Directory.CreateDirectory(folder);
                foreach (var s in sides) Cairn.Workspace.AtomicFile.WriteAllBytes(Path.Combine(folder, s.FileName), s.Bytes);
                files.AddRange(Sides(path));
                return files;
            },
            path => [.. (plan?.Invoke(path) ?? []), .. Sides(path)]);
    }
}
