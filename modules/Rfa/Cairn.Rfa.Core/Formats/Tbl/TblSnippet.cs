using System.Text;

namespace Cairn.Rfa.Formats.Tbl;

/// <summary>Which stock table's layout a snippet copies.</summary>
public enum TblSnippetStyle
{
    /// <summary>
    /// entity.tbl: a leading tab; the key padded to 24 characters; the quoted slot name padded to 24;
    /// the quoted clip padded to 41 (the most common widths in the stock file); a field that does not
    /// fit pushes its column out for the whole block, keeping at least two spaces.
    /// </summary>
    Entity,
    /// <summary>
    /// weapons.tbl: no indent; the key padded to 24 characters; the name and clip columns sized to
    /// the block's longest quoted value plus three spaces, as the stock weapon blocks are.
    /// </summary>
    Weapon,
}

/// <summary>One line a snippet writes.</summary>
/// <param name="Kind">State or action.</param>
/// <param name="SlotName">The state or action name, e.g. "stand".</param>
/// <param name="ClipName">The clip, any spelling (<c>x</c>, <c>x.rfa</c>, <c>folder\x.rfa</c>); written as <c>x.mvf</c>.</param>
/// <param name="Sound">An action's foley sound, or null (an action then writes <c>""</c>, as stock does). Ignored for states.</param>
/// <param name="WeaponBlock">When not null, the line goes under a <c>+Weapon Specific:</c> heading for this weapon.</param>
public sealed record TblSnippetEntry(ClipUsageKind Kind, string SlotName, string ClipName, string? Sound = null, string? WeaponBlock = null);

/// <summary>
/// Writes <c>+State:</c> / <c>+Action:</c> lines in the stock tables' own layout (measured on
/// entity.tbl and weapons.tbl): the clip spelt <c>name.mvf</c>, every value double-quoted, an
/// action always carrying a sound column (<c>""</c> when it has none) and a state never, columns
/// padded with spaces. The output reads back through <see cref="TableReaders"/> to the same values.
/// </summary>
public static class TblSnippet
{
    /// <summary>Width the key column is padded to (<c>+State:</c> plus 17 spaces) in both stock tables.</summary>
    public const int KeyWidth = 24;

    /// <summary>The stock entity.tbl width of the quoted-name column.</summary>
    public const int EntityNameWidth = 24;

    /// <summary>The stock entity.tbl width of the quoted-clip column (its most common value).</summary>
    public const int EntityClipWidth = 41;

    /// <summary>The line break snippets use (the stock tables use CRLF).</summary>
    public const string NewLine = "\r\n";

    /// <summary>
    /// The table spelling of a clip: base name plus <c>.mvf</c>, whatever folder or extension was given.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or cannot be quoted.</exception>
    public static string TableClipName(string clipName)
    {
        ArgumentNullException.ThrowIfNull(clipName);
        string key = ClipUsageIndex.ClipKey(clipName);
        if (key.Length == 0) throw new ArgumentException("A clip name is required.", nameof(clipName));
        Check(key, nameof(clipName));
        return key + ".mvf";
    }

    /// <summary>One line, e.g. <c>\t+State:                 "stand"                 "ult2_stand.mvf"</c> (no line break).</summary>
    public static string Line(ClipUsageKind kind, string slotName, string clipName, string? sound = null, TblSnippetStyle style = TblSnippetStyle.Entity) =>
        Block([new TblSnippetEntry(kind, slotName, clipName, sound)], style).TrimEnd('\r', '\n');

    /// <summary>
    /// Several lines aligned as one block, each ending in <see cref="NewLine"/>. Lines without a
    /// weapon block come first, in the order given; then each weapon block, in order of first
    /// appearance, under its <c>+Weapon Specific:</c> heading (a weapon block in entity.tbl runs to the
    /// next block or <c>$</c> key, so default lines must not follow one).
    /// </summary>
    /// <exception cref="ArgumentException">A name is empty or contains a quote or line break.</exception>
    public static string Block(IEnumerable<TblSnippetEntry> entries, TblSnippetStyle style = TblSnippetStyle.Entity)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        var rows = new List<(string Key, string Name, string Clip, string? Sound, string? Weapon)>(list.Count);
        foreach (var e in list)
        {
            ArgumentNullException.ThrowIfNull(e);
            if (string.IsNullOrWhiteSpace(e.SlotName)) throw new ArgumentException("A state or action name is required.", nameof(entries));
            Check(e.SlotName, nameof(entries));
            if (e.Sound is not null) Check(e.Sound, nameof(entries));
            if (e.WeaponBlock is not null) Check(e.WeaponBlock, nameof(entries));
            rows.Add((
                e.Kind == ClipUsageKind.State ? "+State:" : "+Action:",
                Quote(e.SlotName),
                Quote(TableClipName(e.ClipName)),
                e.Kind == ClipUsageKind.Action ? Quote(e.Sound ?? string.Empty) : null,
                string.IsNullOrEmpty(e.WeaponBlock) ? null : e.WeaponBlock));
        }
        if (rows.Count == 0) return string.Empty;

        int nameWidth, clipWidth;
        if (style == TblSnippetStyle.Entity)
        {
            nameWidth = Math.Max(EntityNameWidth, rows.Max(r => r.Name.Length) + 2);
            clipWidth = Math.Max(EntityClipWidth, rows.Max(r => r.Clip.Length) + 2);
        }
        else
        {
            nameWidth = rows.Max(r => r.Name.Length) + 3;
            clipWidth = rows.Max(r => r.Clip.Length) + 3;
        }
        string indent = style == TblSnippetStyle.Entity ? "\t" : string.Empty;

        var sb = new StringBuilder();
        void Write((string Key, string Name, string Clip, string? Sound, string? Weapon) r)
        {
            sb.Append(indent).Append(r.Key.PadRight(KeyWidth)).Append(r.Name.PadRight(nameWidth));
            if (r.Sound is null) sb.Append(r.Clip);
            else sb.Append(r.Clip.PadRight(clipWidth)).Append(r.Sound);
            sb.Append(NewLine);
        }

        foreach (var r in rows.Where(r => r.Weapon is null)) Write(r);
        foreach (var weapon in rows.Where(r => r.Weapon is not null).Select(r => r.Weapon!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(WeaponHeading(weapon, style)).Append(NewLine);
            foreach (var r in rows.Where(r => string.Equals(r.Weapon, weapon, StringComparison.OrdinalIgnoreCase))) Write(r);
        }
        return sb.ToString();
    }

    /// <summary>The <c>+Weapon Specific:</c> heading line as entity.tbl writes it (no line break).</summary>
    public static string WeaponHeading(string weaponName, TblSnippetStyle style = TblSnippetStyle.Entity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(weaponName);
        Check(weaponName, nameof(weaponName));
        return (style == TblSnippetStyle.Entity ? "\t" : string.Empty) + "+Weapon Specific: " + Quote(weaponName);
    }

    /// <summary>
    /// The entries for "the same slots as an existing class, with new clips" (batch retarget): every
    /// usage whose clip is a key of <paramref name="renames"/> (compared as clip identities, so any
    /// spelling works) becomes the same state/action — same slot, sound and weapon block — naming the
    /// new clip. Usages whose clip is not in the map are dropped, or kept with their old clip when
    /// <paramref name="keepUnmapped"/> is true. Order follows <paramref name="sourceUsages"/>.
    /// </summary>
    public static IReadOnlyList<TblSnippetEntry> Retarget(
        IEnumerable<ClipUsage> sourceUsages, IReadOnlyDictionary<string, string> renames, bool keepUnmapped = false)
    {
        ArgumentNullException.ThrowIfNull(sourceUsages);
        ArgumentNullException.ThrowIfNull(renames);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in renames) map[ClipUsageIndex.ClipKey(from)] = to;

        var result = new List<TblSnippetEntry>();
        foreach (var u in sourceUsages)
        {
            string? clip = map.TryGetValue(u.ClipBaseName, out var renamed) ? renamed : keepUnmapped ? u.Clip.Original : null;
            if (clip is null) continue;
            result.Add(new TblSnippetEntry(u.Kind, u.SlotName, clip, u.Sound, u.WeaponBlock));
        }
        return result;
    }

    /// <summary><see cref="Retarget"/> written as one aligned <see cref="Block"/>.</summary>
    public static string RetargetBlock(
        IEnumerable<ClipUsage> sourceUsages, IReadOnlyDictionary<string, string> renames, bool keepUnmapped = false,
        TblSnippetStyle style = TblSnippetStyle.Entity) =>
        Block(Retarget(sourceUsages, renames, keepUnmapped), style);

    private static string Quote(string value) => "\"" + value + "\"";

    private static void Check(string value, string parameter)
    {
        if (value.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException($"'{value}' cannot be written to a table: it contains a quote or a line break.", parameter);
    }
}
