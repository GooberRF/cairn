using System.Text.Json;
using Cairn.Formats;

namespace Cairn.Lvl.Conversion;

/// <summary>What an N-Gage object scene becomes in a Red Faction level.</summary>
public enum XffObjectKind
{
    /// <summary>An entity (entity.tbl class).</summary>
    Entity,
    /// <summary>An item (items.tbl class).</summary>
    Item,
    /// <summary>A clutter object (clutter.tbl class).</summary>
    Clutter,
    /// <summary>The single-player start.</summary>
    PlayerStart,
}

/// <summary>One row of <see cref="XffObjectTable"/>.</summary>
/// <param name="FileName">The scene file the level places (<c>enemy_ug.xff</c>).</param>
/// <param name="Kind">What it becomes.</param>
/// <param name="ClassName">The Red Faction class name (empty for the player start).</param>
/// <param name="Marker">Entities: the enemy type of the scene's own marker (32xx), which a 42xx spawn point names; else null.</param>
/// <param name="Count">Items: what the item gives; else null.</param>
public sealed record XffObjectClass(string FileName, XffObjectKind Kind, string ClassName, int? Marker, int? Count);

/// <summary>
/// The table that maps the scene files a Red Faction for N-Gage level places (enemies, items, ammunition, weapons,
/// the barrel, vehicles and the player) to Red Faction classes. The default table is embedded (<c>xff-objects.json</c>,
/// a plain JSON file with comments the author edits); files it lists under "leftOut" have no counterpart and are
/// reported, never guessed.
/// </summary>
public sealed class XffObjectTable
{
    private readonly Dictionary<string, XffObjectClass> _objects;
    private readonly Dictionary<string, string> _leftOut;

    private XffObjectTable(Dictionary<string, XffObjectClass> objects, Dictionary<string, string> leftOut)
    {
        _objects = objects;
        _leftOut = leftOut;
    }

    private static readonly Lazy<XffObjectTable> Embedded = new(() =>
    {
        using var stream = typeof(XffObjectTable).Assembly.GetManifestResourceStream("Cairn.Lvl.xff-objects.json")
            ?? throw new InvalidOperationException("The N-Gage object table is missing from the build.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>The embedded table.</summary>
    public static XffObjectTable Default => Embedded.Value;

    /// <summary>Every mapped row, in file-name order.</summary>
    public IReadOnlyList<XffObjectClass> Rows => [.. _objects.Values.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The files left out, with why.</summary>
    public IReadOnlyDictionary<string, string> LeftOut => _leftOut;

    /// <summary>The row for a placed scene file (case and folder do not matter), or null.</summary>
    public XffObjectClass? Find(string fileName) => _objects.GetValueOrDefault(Key(fileName));

    /// <summary>Why a placed scene file is left out, or null when it is not.</summary>
    public string? LeftOutReason(string fileName) => _leftOut.GetValueOrDefault(Key(fileName));

    /// <summary>The entity rows whose marker is <paramref name="enemyType"/> (32xx), in file-name order.</summary>
    public IReadOnlyList<XffObjectClass> ForMarker(int enemyType) =>
        [.. _objects.Values.Where(r => r.Marker == enemyType).OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)];

    private static string Key(string fileName) => Path.GetFileName(fileName.Replace('\\', '/')).ToLowerInvariant();

    /// <summary>Reads a table in the embedded file's layout.</summary>
    /// <exception cref="AssetFormatException">The text is not such a table.</exception>
    public static XffObjectTable Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var objects = new Dictionary<string, XffObjectClass>(StringComparer.OrdinalIgnoreCase);
            var leftOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("objects", out var rows))
            {
                foreach (var row in rows.EnumerateObject())
                {
                    string file = Key(row.Name);
                    string kindText = row.Value.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                    var kind = kindText switch
                    {
                        "entity" => XffObjectKind.Entity,
                        "item" => XffObjectKind.Item,
                        "clutter" => XffObjectKind.Clutter,
                        "playerStart" => XffObjectKind.PlayerStart,
                        _ => throw new AssetFormatException($"The N-Gage object table's row '{row.Name}' has the kind '{kindText}', which is not entity, item, clutter or playerStart."),
                    };
                    string cls = row.Value.TryGetProperty("class", out var c) ? c.GetString() ?? "" : "";
                    if (kind != XffObjectKind.PlayerStart && cls.Trim().Length == 0)
                        throw new AssetFormatException($"The N-Gage object table's row '{row.Name}' has no class name.");
                    int? marker = row.Value.TryGetProperty("marker", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : null;
                    int? count = row.Value.TryGetProperty("count", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null;
                    if (!objects.TryAdd(file, new XffObjectClass(file, kind, cls.Trim(), marker, count)))
                        throw new AssetFormatException($"The N-Gage object table lists '{row.Name}' twice.");
                }
            }
            if (doc.RootElement.TryGetProperty("leftOut", out var left))
                foreach (var row in left.EnumerateObject()) leftOut[Key(row.Name)] = row.Value.GetString() ?? "";
            return new XffObjectTable(objects, leftOut);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new AssetFormatException("The N-Gage object table could not be read: " + e.Message);
        }
    }
}
