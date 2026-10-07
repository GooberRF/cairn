using Cairn.Assets;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Compare;

/// <summary>How an entry or field differs from stock.</summary>
public enum TblChangeKind { Added, Removed, Changed }

/// <summary>One field difference inside an entry.</summary>
/// <param name="Kind">Added (only in the modded table), removed (only in stock) or changed.</param>
/// <param name="Path">The field marker, with parent markers for sub-fields (<c>+State: / +Footstep Trigger:</c>), and <c>#n</c> for the n-th repeat.</param>
/// <param name="StockValue">The stock value text, or null when added.</param>
/// <param name="ModdedValue">The modded value text, or null when removed.</param>
/// <param name="ModdedSpan">The field in the modded text, or null when removed.</param>
/// <param name="StockSpan">The field in the stock text, or null when added.</param>
public sealed record TblFieldChange(TblChangeKind Kind, string Path, string? StockValue, string? ModdedValue, TextSpan? ModdedSpan, TextSpan? StockSpan);

/// <summary>One entry difference.</summary>
/// <param name="Kind">Added, removed or changed.</param>
/// <param name="Section">The section header without '#', or empty.</param>
/// <param name="Name">The entry name (empty for the fields of a section without entries).</param>
/// <param name="ModdedSpan">The entry in the modded text (where to jump), or null when removed.</param>
/// <param name="StockSpan">The entry in the stock text, or null when added.</param>
/// <param name="Fields">The field changes of a changed entry.</param>
public sealed record TblEntryChange(TblChangeKind Kind, string Section, string Name, TextSpan? ModdedSpan, TextSpan? StockSpan, ImmutableArray<TblFieldChange> Fields);

/// <summary>The result of a comparison.</summary>
public sealed record TblComparison(ImmutableArray<TblEntryChange> Changes)
{
    public bool IsIdentical => Changes.IsEmpty;
    public int Added => Changes.Count(c => c.Kind == TblChangeKind.Added);
    public int Removed => Changes.Count(c => c.Kind == TblChangeKind.Removed);
    public int Changed => Changes.Count(c => c.Kind == TblChangeKind.Changed);
}

/// <summary>Compares a table with the game's stock table of the same name.</summary>
public static class TblCompare
{
    /// <summary>
    /// Compares two parses. Entries are matched by section and name (case-insensitive; the n-th of several
    /// with the same name matches the n-th); fields by marker path and repeat number. Values are compared
    /// by their tokens, so spacing and comments do not count as changes. Changes are in modded-text order,
    /// removed entries after the entry that preceded them in stock.
    /// </summary>
    public static TblComparison Compare(TblDocument modded, TblDocument stock)
    {
        ArgumentNullException.ThrowIfNull(modded);
        ArgumentNullException.ThrowIfNull(stock);
        var stockUnits = Units(stock);
        var moddedUnits = Units(modded);
        var stockByKey = stockUnits.ToDictionary(u => u.Key, StringComparer.OrdinalIgnoreCase);
        var moddedKeys = new HashSet<string>(moddedUnits.Select(u => u.Key), StringComparer.OrdinalIgnoreCase);

        var changes = new List<(int Order, TblEntryChange Change)>();
        foreach (var u in moddedUnits)
        {
            if (!stockByKey.TryGetValue(u.Key, out var s))
            {
                changes.Add((u.Span.Start * 2, new TblEntryChange(TblChangeKind.Added, u.Section, u.Name, u.Span, null, [])));
                continue;
            }
            var fieldChanges = CompareFields(u.Fields, s.Fields);
            if (fieldChanges.Length > 0)
                changes.Add((u.Span.Start * 2, new TblEntryChange(TblChangeKind.Changed, u.Section, u.Name, u.Span, s.Span, fieldChanges)));
        }
        // Removed entries go after the modded position of the nearest earlier stock entry that still exists.
        int anchor = 0;
        foreach (var s in stockUnits)
        {
            if (moddedKeys.Contains(s.Key))
            {
                anchor = moddedUnits.First(m => string.Equals(m.Key, s.Key, StringComparison.OrdinalIgnoreCase)).Span.End;
                continue;
            }
            changes.Add((anchor * 2 + 1, new TblEntryChange(TblChangeKind.Removed, s.Section, s.Name, null, s.Span, [])));
        }
        return new TblComparison([.. changes.OrderBy(c => c.Order).Select(c => c.Change)]);
    }

    private sealed record Unit(string Key, string Section, string Name, TextSpan Span, ImmutableArray<TblFieldNode> Fields);

    private static List<Unit> Units(TblDocument doc)
    {
        var units = new List<Unit>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in doc.Sections)
        {
            if (!s.Fields.IsEmpty || s.Entries.IsEmpty)
                units.Add(new Unit(Key(s.Name, "", counts), s.Name, string.Empty, s.Span, s.Fields));
            foreach (var e in s.Entries)
                units.Add(new Unit(Key(s.Name, e.Name.Trim(), counts), s.Name, e.Name, e.Span, e.Fields));
        }
        return units;
    }

    private static string Key(string section, string name, Dictionary<string, int> counts)
    {
        string baseKey = section.Trim() + "\u0001" + name;
        counts[baseKey] = counts.TryGetValue(baseKey, out int n) ? n + 1 : 1;
        return baseKey + "\u0001" + counts[baseKey];
    }

    private static ImmutableArray<TblFieldChange> CompareFields(ImmutableArray<TblFieldNode> modded, ImmutableArray<TblFieldNode> stock)
    {
        var m = Flatten(modded);
        var s = Flatten(stock);
        var sByPath = s.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var mPaths = new HashSet<string>(m.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        var result = new List<TblFieldChange>();
        foreach (var f in m)
        {
            if (!sByPath.TryGetValue(f.Path, out var o))
                result.Add(new TblFieldChange(TblChangeKind.Added, f.Path, null, f.Value, f.Field.Span, null));
            else if (!string.Equals(f.Value, o.Value, StringComparison.Ordinal))
                result.Add(new TblFieldChange(TblChangeKind.Changed, f.Path, o.Value, f.Value, f.Field.Span, o.Field.Span));
        }
        foreach (var o in s)
            if (!mPaths.Contains(o.Path))
                result.Add(new TblFieldChange(TblChangeKind.Removed, o.Path, o.Value, null, null, o.Field.Span));
        return [.. result];
    }

    private static List<(string Path, string Value, TblFieldNode Field)> Flatten(ImmutableArray<TblFieldNode> fields)
    {
        var list = new List<(string, string, TblFieldNode)>();
        void Walk(ImmutableArray<TblFieldNode> items, string prefix)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in items)
            {
                string marker = TblSchemaNames.Normalize(f.Marker);
                counts[marker] = counts.TryGetValue(marker, out int n) ? n + 1 : 1;
                string path = prefix + marker + (counts[marker] > 1 ? " #" + counts[marker] : "");
                list.Add((path, ValueText(f), f));
                Walk(f.Children, path + " / ");
            }
        }
        Walk(fields, string.Empty);
        return list;
    }

    /// <summary>A field's values as canonical text (token by token, single spaces).</summary>
    public static string ValueText(TblFieldNode f) => string.Join(" ", f.Values.Select(v => v.ToString()));

    /// <summary>
    /// The stock table named <paramref name="fileName"/>: the copy in a packfile of the game's install folder (never
    /// user_maps, a search folder or the document's folder), or null when there is no game directory or no such table.
    /// </summary>
    public static (string Text, AssetLocation Location)? FindStock(string? gameDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) return null;
        var resolver = new AssetResolver(new AssetResolverOptions { GameDirectory = gameDirectory });
        try
        {
            var location = resolver.Resolve(Path.GetFileName(fileName));
            if (location is null || location.Kind != AssetSourceKind.GameArchive) return null;
            return (TblTextFiles.Decode(location.ReadAllBytes()).Text, location);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}
