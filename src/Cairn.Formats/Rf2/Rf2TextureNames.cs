using System.Collections.Frozen;

namespace Cairn.Formats.Rf2;

/// <summary>How a Red Faction II texture name found its Red Faction name.</summary>
public enum Rf2TextureMatch
{
    /// <summary>Not in the table: the name stays as it is.</summary>
    None,

    /// <summary>The first name of the target list that ends with the RF2 name (without its material prefix).</summary>
    Suffix,

    /// <summary>One of the fixed pairs (invisible textures, jump pads, numbers, coronas, light beams and so on).</summary>
    Manual,
}

/// <summary>The Red Faction name of one Red Faction II texture.</summary>
/// <param name="Original">The RF2 name as asked for.</param>
/// <param name="Translated">The name to use: the translation, or <paramref name="Original"/> when there is none.</param>
/// <param name="Match">How it was found.</param>
/// <param name="IsWeak">
/// True for a suffix match where several target names end with the same text, so the first one was taken (short
/// names such as <c>a.tga</c> or <c>arrow.tga</c>); a report should list these for review.
/// </param>
public sealed record Rf2TextureTranslation(string Original, string Translated, Rf2TextureMatch Match, bool IsWeak = false)
{
    /// <summary>True when the name changes.</summary>
    public bool IsTranslated => Match != Rf2TextureMatch.None && !string.Equals(Original, Translated, StringComparison.Ordinal);
}

/// <summary>
/// Red Faction II texture names translated to the names of a Red Faction texture set, with the tables of the
/// author's REDUX converter: a list of RF2 names and a list of target names (embedded unchanged), joined by REDUX's
/// rule (strip a known material prefix such as <c>rck_</c> or <c>mtl_</c> from the RF2 name, then take the first
/// target, in list order, that ends with the rest, ignoring case), then REDUX's fixed pairs on top. The result is
/// built once, so it never depends on the order names are asked in.
/// <para>
/// Most targets are not stock Red Faction textures: they belong to a separate RF2 texture set made for Red Faction
/// (the <c>grf2</c> names); only the invisible textures among the fixed pairs are stock. A conversion that
/// translates names should say so.
/// </para>
/// </summary>
public static class Rf2TextureNames
{
    private static readonly Lazy<FrozenDictionary<string, Rf2TextureTranslation>> s_table = new(Build);

    /// <summary>The material prefixes stripped before the suffix match.</summary>
    public static IReadOnlyList<string> MaterialPrefixes { get; } = ["rck_", "mtl_", "wtr_", "pls_", "gls_", "drt_", "woo_", "tec_", "cpt_", "mar_", "sp0_"];

    /// <summary>A sentence for conversion reports: where the translated names come from.</summary>
    public const string TextureSetNote =
        "Red Faction II texture names were translated to the names of the RF2 texture set made for Red Faction (REDUX's tables); those textures are not part of the stock game, so the converted file needs that texture set (or textures of those names) to look right.";

    /// <summary>Every RF2 name that has a translation (case-insensitive keys).</summary>
    public static IReadOnlyDictionary<string, Rf2TextureTranslation> All => s_table.Value;

    /// <summary>
    /// The Red Faction name for <paramref name="rf2Name"/> (a bare file name; any folder part is ignored, case does not
    /// matter). A name the table does not know comes back unchanged with <see cref="Rf2TextureMatch.None"/>.
    /// </summary>
    public static Rf2TextureTranslation Translate(string rf2Name)
    {
        ArgumentNullException.ThrowIfNull(rf2Name);
        string bare = Path.GetFileName(rf2Name.Trim());
        return s_table.Value.TryGetValue(bare, out var hit) ? hit with { Original = rf2Name } : new Rf2TextureTranslation(rf2Name, rf2Name, Rf2TextureMatch.None);
    }

    private static FrozenDictionary<string, Rf2TextureTranslation> Build()
    {
        var real = Lines("rf2_real_texture_filenames.txt");
        var targets = Lines("rf2_translated_texture_filenames.txt");
        // REDUX keeps the targets in a case-insensitive set, so a name listed twice counts once (its first place).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        targets = [.. targets.Where(seen.Add)];

        var map = new Dictionary<string, Rf2TextureTranslation>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in real)
        {
            string rest = name;
            foreach (string prefix in MaterialPrefixes)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                rest = name[prefix.Length..];
                break;
            }
            string? first = null;
            int candidates = 0;
            foreach (string target in targets)
            {
                if (!target.EndsWith(rest, StringComparison.OrdinalIgnoreCase)) continue;
                first ??= target;
                candidates++;
            }
            if (first is not null) map[name] = new Rf2TextureTranslation(name, first, Rf2TextureMatch.Suffix, candidates > 1);
        }
        foreach (var (from, to) in ManualPairs) map[from] = new Rf2TextureTranslation(from, to, Rf2TextureMatch.Manual);
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> Lines(string resource)
    {
        using var stream = typeof(Rf2TextureNames).Assembly.GetManifestResourceStream("Cairn.Formats.Rf2." + resource)
            ?? throw new InvalidOperationException($"The embedded list {resource} is missing.");
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    /// <summary>REDUX's fixed pairs, applied after the suffix match (they win).</summary>
    private static readonly (string From, string To)[] ManualPairs =
    [
        ("cpt_invisible.tga", "rck_invisible04.tga"),
        ("drt_invisible.tga", "rck_invisible04.tga"),
        ("mar_invisible.tga", "sld_invisible01.tga"),
        ("mtl_invisible.tga", "mtl_invisible02.tga"),
        ("pls_invisible.tga", "sld_invisible01.tga"),
        ("rck_invisible.tga", "rck_invisible04.tga"),
        ("woo_invisible.tga", "sld_invisible01.tga"),
        ("wtr_invisible.tga", "cem_invisible03.tga"),
        ("mtl_jpad_oct1.tga", "mtl_L15S2_lift.tga"),
        ("mtl_jpad_oct2.tga", "mtl_jumppad01.tga"),
        ("mtl_jpad_oct4.tga", "mtl_jumppad02.tga"),
        ("cpt_012red01.tga", "sld_grf2012red01a.tga"),
        ("pls_013tabletop01.tga", "pls_grf2013tabletop01a.tga"),
        ("mar_012sopot05.tga", "sld_grf2012sopot05a.tga"),
        ("cpt_012banners01.tga", "sld_grf2012banners01a.tga"),
        ("number_01.tga", "dec_grf2number_01.tga"),
        ("numbers2.tga", "dec_grf2numbers2.tga"),
        ("numbers3.tga", "dec_grf2numbers3.tga"),
        ("numbers_03.tga", "dec_grf2numbers_03.tga"),
        ("numbers_05.tga", "dec_grf2numbers_05.tga"),
        ("numbers_07.tga", "dec_grf2numbers_07.tga"),
        ("numbers_08.tga", "dec_grf2numbers_08.tga"),
        ("numbers_09.tga", "dec_grf2numbers_09.tga"),
        ("numbers_10.tga", "dec_grf2numbers_10.tga"),
        ("numbers_11.tga", "dec_grf2numbers_11.tga"),
        ("numbers_12.tga", "dec_grf2numbers_12.tga"),
        ("numbers_13.tga", "dec_grf2numbers_13.tga"),
        ("numbers_15.tga", "dec_grf2numbers_15.tga"),
        ("numbers_16.tga", "dec_grf2numbers_16.tga"),
        ("woo_012tight.tga", "sld_grf2012tighta.tga"),
        ("mtl_122_grate2.tga", "mtl_grf2122_grate.tga"),
        ("l00-sign2.tga", "dec_grf2l00-sign2.tga"),
        ("l00-sign3.tga", "dec_grf2l00-sign3.tga"),
        ("l00-sign4.tga", "dec_grf2l00-sign4.tga"),
        ("pls_rounds.tga", "pls_polkafloor02_drty.vbm"),
        ("woo_cardboard.tga", "sld_grf2cardboarda.tga"),
        ("woo_CrateSideRA.tga", "sld_grf2cratesideraa.tga"),
        ("woo_CrateSideB.tga", "sld_grf2cratesideba.tga"),
        ("SideLogoRed2a.tga", "dec_grf2sidelogored2.tga"),
        ("SideLogoBlue2a.tga", "dec_grf2sidelogoblue2.tga"),
        ("woo_wood002_128.tga", "sld_grf2wood002_128a.tga"),
        ("woo_wood002B_128.tga", "sld_grf2wood002b_128a.tga"),
        ("rx_2level.tga", "dec_grf21level2.tga"),
        ("2level.tga", "dec_grf21level2.tga"),
        ("cem_dark_floor01xx.tga", "cem_grf2dark_floor01xx.tga"),
        ("ddrecthilite02.tga", "grf2_ddrecthilite02.tga"),
        ("dirtyvent.tga", "dec_grf2dirtyvent.tga"),
        ("Corona_BRw.tga", "grf2_corona_brw.tga"),
        ("Corona_BRc.tga", "grf2_corona_brc.tga"),
        ("LightBeam_Blue.tga", "grf2_lightbeam_blue.tga"),
        ("LightBeam_White.tga", "grf2_lightbeam_white.tga"),
        ("light.tga", "grf2_light.tga"),
        ("lightbeam01.tga", "grf2_lightbeam01.tga"),
        ("lightbeam_amber.tga", "grf2_lightbeam_amber.tga"),
        ("lightbeam_br.tga", "grf2_lightbeam_br.tga"),
        ("lightbeam_green.tga", "grf2_lightbeam_green.tga"),
        ("lightbeam_ltblue.tga", "grf2_lightbeam_ltblue.tga"),
        ("lightbeam_ltblue01.tga", "grf2_lightbeam_ltblue01.tga"),
        ("lightbeam_ornge.tga", "grf2_lightbeam_ornge.tga"),
        ("lightbeam_soft.tga", "grf2_lightbeam_soft.tga"),
        ("lightbeam_sp_01.tga", "grf2_lightbeam_sp_01.tga"),
        ("lightbeam_wide.tga", "grf2_lightbeam_wide.tga"),
        ("lightbeam_wide03.tga", "grf2_lightbeam_wide03.tga"),
        ("lightcorona01.tga", "grf2_lightcorona01.tga"),
        ("lightcorona01b.tga", "grf2_lightcorona01b.tga"),
        ("lightcorona02.tga", "grf2_lightcorona02.tga"),
        ("lightcorona03.tga", "grf2_lightcorona03.tga"),
        ("light_02.tga", "grf2_light_02.tga"),
        ("light_03.tga", "grf2_light_03.tga"),
        ("light_04.tga", "grf2_light_04.tga"),
        ("light_lilredf01.tga", "grf2_light_lilredf01.tga"),
        ("light_lilredflare01.tga", "grf2_light_lilredflare01.tga"),
        ("light_spot.tga", "grf2_light_spot.tga"),
        ("light_spot_grill.tga", "grf2_light_spot_grill.tga"),
        ("corona_br.tga", "grf2_corona_br.tga"),
        ("corona_brg64.tga", "grf2_corona_brg64.tga"),
        ("corona_grey.tga", "grf2_corona_grey.tga"),
        ("corona_lblue.tga", "grf2_corona_lblue.tga"),
        ("corona_ltblue.tga", "grf2_corona_ltblue.tga"),
        ("corona_ornge.tga", "grf2_corona_ornge.tga"),
        ("corona_red.tga", "grf2_corona_red.tga"),
        ("corona_white.tga", "grf2_corona_white.tga"),
        ("corona_yellow.tga", "grf2_corona_yellow.tga"),
    ];
}
