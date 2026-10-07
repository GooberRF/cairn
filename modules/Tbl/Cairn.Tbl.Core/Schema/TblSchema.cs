namespace Cairn.Tbl.Schema;

/// <summary>The value type of a schema field.</summary>
public enum TblValueType
{
    /// <summary>A quoted string (also the fallback for a type the loader does not know).</summary>
    String,
    Int,
    Float,
    /// <summary><c>true</c> / <c>false</c>.</summary>
    Bool,
    /// <summary><c>&lt;x, y, z&gt;</c>.</summary>
    Vec3,
    /// <summary>Three or four numbers (0-255).</summary>
    Color,
    /// <summary>One string out of <see cref="TblFieldSchema.Values"/>.</summary>
    Enum,
    /// <summary>A parenthesised list of quoted flag names out of <see cref="TblFieldSchema.Values"/>.</summary>
    Flags,
    /// <summary>A file name (<see cref="TblFieldSchema.FileKind"/>).</summary>
    File,
    /// <summary>The name of an entry defined in another table (<see cref="TblFieldSchema.RefKind"/>).</summary>
    Ref,
    /// <summary>Several values (see <see cref="TblFieldSchema.Items"/> for per-position types).</summary>
    List,
    /// <summary>Free text (multi-line text blocks ended by <c>#End</c>, localised strings).</summary>
    Text,
    /// <summary>A marker without a value (it only introduces sub-fields).</summary>
    None,
}

/// <summary>One field the engine reads, in its read order.</summary>
public sealed class TblFieldSchema
{
    /// <summary>The marker exactly as the engine asks for it, e.g. <c>$Weapon Type:</c>.</summary>
    public required string Name { get; init; }
    public TblValueType Type { get; init; } = TblValueType.String;
    /// <summary>The type as written in the schema file.</summary>
    public string TypeName { get; init; } = "string";
    public bool Required { get; init; }
    /// <summary>True when the engine reads the field in a loop (it may appear several times in a row).</summary>
    public bool Repeat { get; init; }
    public ImmutableArray<string> Values { get; init; } = [];
    /// <summary>For files: texture, mesh, anim, effect, sound, vfx, table, any...</summary>
    public string? FileKind { get; init; }
    /// <summary>For references: the kind of entry named (weapon, ammo, sound, ...).</summary>
    public string? RefKind { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? Unit { get; init; }
    public string? Default { get; init; }
    /// <summary>Set when only Alpine Faction reads this field: the first Alpine version that does.</summary>
    public string? AlpineSince { get; init; }
    public string? Doc { get; init; }
    /// <summary>Fields (usually <c>+Sub:</c>) the engine reads right after this one.</summary>
    public ImmutableArray<TblFieldSchema> Children { get; init; } = [];
    /// <summary>Optional per-position value types (schema key <c>parts</c> or <c>items</c>), for <c>+State: "name" "file.mvf"</c> style fields.</summary>
    public ImmutableArray<TblFieldSchema> Items { get; init; } = [];
    /// <summary>The children are read only when this boolean is true (schema <c>if: "true"</c>); otherwise whenever the field is present.</summary>
    public bool ChildrenOnlyIfTrue { get; init; }
    /// <summary>A condition on reading the children (schema <c>childrenWhen</c>), or null; their being required is then not checked.</summary>
    public string? ChildrenWhen { get; init; }
    /// <summary>The game searches ahead for this marker, skipping anything before it (schema <c>seek</c>).</summary>
    public bool Seek { get; init; }
    /// <summary>Read only by the level editor; the game skips it (schema <c>editorOnly</c>).</summary>
    public bool EditorOnly { get; init; }
    /// <summary>The most bytes a string may have (schema <c>maxLength</c>), or null.</summary>
    public int? MaxLength { get; init; }
    /// <summary>The most elements a list has room for in the game (schema <c>maxCount</c>), or null.</summary>
    public int? MaxCount { get; init; }
    /// <summary>A repeated field read exactly N times, N being the value of this field (schema <c>countFrom</c>).</summary>
    public string? CountFrom { get; init; }
    /// <summary>Special syntax: <c>substring-flags</c>, <c>number-after-marker</c>, <c>lines</c>, <c>xstr</c>, <c>braces</c>...</summary>
    public string? Syntax { get; init; }
    /// <summary>Unmarked rows that follow the field's values (schema <c>rows</c>): the type of each row value.</summary>
    public ImmutableArray<TblFieldSchema> Rows { get; init; } = [];
    /// <summary>For a list: the element type (schema key <c>of</c>), or null when elements are mixed.</summary>
    public TblValueType? ElementType { get; init; }
    /// <summary>False when <see cref="Values"/> are only suggestions (schema <c>"strict": false</c>).</summary>
    public bool Strict { get; init; } = true;
    /// <summary>With <see cref="ChildrenWhen"/> <c>known</c>: values in <see cref="Values"/> that still read no children (schema <c>childrenUnless</c>).</summary>
    public ImmutableArray<string> ChildrenUnless { get; init; } = [];
    /// <summary>The schema's condition text (schema <c>when</c>), or null; the forms the linter understands are parsed into <see cref="RequiredIf"/> and <see cref="RequiredAfter"/>.</summary>
    public string? When { get; init; }
    /// <summary>The field is read only when another field of the entry has a value (<c>"required if $Flags contains player_wep"</c>), or null.</summary>
    public TblFieldCondition? RequiredIf { get; init; }
    /// <summary>The field is required when this earlier sibling is present (<c>"required after +Length"</c>), or null.</summary>
    public string? RequiredAfter { get; init; }
    /// <summary>With <see cref="RequiredIf"/>: true when the game does not read the field at all while the condition is false (schema <c>"otherwise": "absent"</c>), so writing it then stops the game with an error; false when it is optional then.</summary>
    public bool AbsentOtherwise { get; init; }
    /// <summary>Alpine line tables: whether the value may be quoted (schema <c>quotes</c>: <c>optional</c>, <c>required</c>), or null.</summary>
    public string? Quotes { get; init; }
    /// <summary>
    /// File fields: the suffix the game inserts before the extension when it loads the file (schema <c>nameSuffix</c>;
    /// HUD bitmaps: <c>reticle.tga</c> is loaded as <c>reticle_0.tga</c>), or null.
    /// </summary>
    public string? NameSuffix { get; init; }

    /// <summary>
    /// The disk names the game may load for <paramref name="name"/> written in this field: the engine's extension
    /// mapping (<see cref="Cairn.Formats.Tbl.TblFileName.Normalize"/>) with <see cref="NameSuffix"/> inserted before the
    /// extension (the stock game data holds only the suffixed HUD bitmaps, e.g. <c>reticle_0.tga</c>).
    /// </summary>
    public IReadOnlyList<string> EngineFileNames(string name)
    {
        var names = Cairn.Formats.Tbl.TblFileName.Normalize(name);
        if (string.IsNullOrEmpty(NameSuffix)) return names;
        return [.. names.Select(n => Path.GetFileNameWithoutExtension(n).EndsWith(NameSuffix, StringComparison.OrdinalIgnoreCase)
            ? n : Path.GetFileNameWithoutExtension(n) + NameSuffix + Path.GetExtension(n))];
    }

    /// <summary>The name without its prefix and colon (<c>Weapon Type</c>).</summary>
    public string BareName => TblSchemaNames.Bare(Name);

    public override string ToString() => Name;
}

/// <summary>A condition on another field of the same entry: <paramref name="Field"/> (a marker) has <paramref name="Value"/> among its values (a flag, an enum value).</summary>
public sealed record TblFieldCondition(string Field, string Value)
{
    /// <summary>"$Flags has player_wep".</summary>
    public override string ToString() => $"{Field} has {Value}";
}

/// <summary>How a section's content is laid out.</summary>
public enum TblSectionLayout
{
    /// <summary>Marked fields (<c>$Name:</c> ...), possibly grouped into entries.</summary>
    Fields,
    /// <summary>Unmarked rows of values, one per line (sounds.tbl, hud.tbl coordinates, level text).</summary>
    Rows,
    /// <summary>A table of names and numbers (materials.tbl hit sounds).</summary>
    Matrix,
    /// <summary>Raw lines of text (credits).</summary>
    Text,
    /// <summary>Entries whose fields are blocks of raw lines ended by <c>#End</c> (endgame).</summary>
    TextBlocks,
}

/// <summary>One section of a table (a headerless table has one section with an empty name).</summary>
public sealed class TblSectionSchema
{
    /// <summary>The header with its <c>#</c> (<c>#Primary Weapons</c>), or empty for fields outside any section.</summary>
    public string Name { get; init; } = string.Empty;
    public bool Required { get; init; }
    public bool Repeat { get; init; }
    public string? Doc { get; init; }
    /// <summary>The field that starts each entry (<c>$Name:</c>), or null when the section has no entries.</summary>
    public string? Entry { get; init; }
    /// <summary>The index kind entry names define (<c>weapon</c>), or null.</summary>
    public string? Defines { get; init; }
    /// <summary>The fields in engine read order (the entry field first when there are entries).</summary>
    public ImmutableArray<TblFieldSchema> Fields { get; init; } = [];
    /// <summary>The marker that ends the section (<c>#End</c>, <c>#Sounds End</c>), or null when the schema does not say.</summary>
    public string? End { get; init; }
    /// <summary>How the content is laid out: fields (the default), rows, a matrix, raw text or text blocks.</summary>
    public TblSectionLayout Layout { get; init; } = TblSectionLayout.Fields;
    /// <summary>For rows: the type of each column.</summary>
    public ImmutableArray<TblFieldSchema> Columns { get; init; } = [];
    /// <summary>How entries are identified: null (by name), <c>index</c> (position) or <c>number</c>.</summary>
    public string? Key { get; init; }
    /// <summary>True when the game finds the header by searching the text, ignoring anything before it (schema <c>locate: "search"</c>).</summary>
    public bool LocateBySearch { get; init; }
    /// <summary>True when the game finds each entry by searching for the entry marker, ignoring anything between entries.</summary>
    public bool EntrySearch { get; init; }
    /// <summary>True when the entry search is case-sensitive (raw text search for the exact marker).</summary>
    public bool EntrySearchCaseSensitive { get; init; }
    /// <summary>The most entries the game keeps, or null.</summary>
    public int? MaxEntries { get; init; }
    /// <summary>True when fields may come in any order (schema <c>order: "free"</c>).</summary>
    public bool FreeOrder { get; init; }
    /// <summary>For a matrix: the number of names in the header row, of rows and of cells in each row (schema <c>size</c>), or null.</summary>
    public int? MatrixSize { get; init; }
    /// <summary>For a matrix: the type of each number cell (schema <c>cell</c>), or null.</summary>
    public TblFieldSchema? MatrixCell { get; init; }
    /// <summary>For a matrix: the type of the header names and of the name that starts each row (schema <c>header</c>), or null.</summary>
    public TblFieldSchema? MatrixHeader { get; init; }

    /// <summary>True when <see cref="Layout"/> is not <see cref="TblSectionLayout.Fields"/>: the content is not checked field by field.</summary>
    public bool IsFreeLayout => Layout is not (TblSectionLayout.Fields or TblSectionLayout.TextBlocks);

    /// <summary>The header without <c>#</c>.</summary>
    public string BareName => Name.StartsWith('#') ? Name[1..].Trim() : Name.Trim();

    /// <summary>True for the section of fields outside any header.</summary>
    public bool IsRoot => BareName.Length == 0;

    /// <summary>The first field named <paramref name="marker"/> (case-insensitive), with its index, or (-1, null).</summary>
    public (int Index, TblFieldSchema? Field) FindField(string marker) => TblSchemaNames.Find(Fields, marker);

    public override string ToString() => Name;
}

/// <summary>Everything the engine reads from one table file.</summary>
public sealed class TblTableSchema
{
    /// <summary>The file name (<c>weapons.tbl</c>), or null when <see cref="Pattern"/> is used.</summary>
    public string? File { get; init; }
    /// <summary>A wildcard pattern (<c>*_info.tbl</c>), or null.</summary>
    public string? Pattern { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Doc { get; init; }
    public ImmutableArray<TblSectionSchema> Sections { get; init; } = [];
    /// <summary>Set when the whole table is read only by Alpine Faction.</summary>
    public string? AlpineSince { get; init; }
    public string? AlpineNotes { get; init; }
    /// <summary>The embedded resource it came from.</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>Other file names with the same format (<c>localized_strings.tbl</c>).</summary>
    public ImmutableArray<string> Aliases { get; init; } = [];
    /// <summary>True when only the level editor reads the table, not the game.</summary>
    public bool EditorOnly { get; init; }
    /// <summary>Special file syntax: <c>alpine-lines</c> for Alpine Faction's line-based option tables, else null.</summary>
    public string? Syntax { get; init; }

    /// <summary>True for Alpine Faction's line-based tables (any order, quotes optional, case-sensitive names).</summary>
    public bool IsAlpineLines => string.Equals(Syntax, "alpine-lines", StringComparison.OrdinalIgnoreCase);

    /// <summary>The section with this header (with or without <c>#</c>, case-insensitive), or null.</summary>
    public TblSectionSchema? FindSection(string header)
    {
        string bare = header.StartsWith('#') ? header[1..].Trim() : header.Trim();
        foreach (var s in Sections)
            if (string.Equals(s.BareName, bare, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>True when this schema describes a file named <paramref name="fileName"/>.</summary>
    public bool Matches(string fileName)
    {
        string name = Path.GetFileName(fileName);
        if (Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))) return true;
        if (File is not null) return string.Equals(File, name, StringComparison.OrdinalIgnoreCase);
        return Pattern is not null && TblSchemaNames.Wildcard(Pattern, name);
    }

    public override string ToString() => File ?? Pattern ?? Title;
}

/// <summary>Helpers for field marker names.</summary>
public static class TblSchemaNames
{
    /// <summary><c>$Weapon Type:</c> -> <c>Weapon Type</c>.</summary>
    public static string Bare(string marker)
    {
        var s = marker.AsSpan().Trim();
        if (s.Length > 0 && s[0] is '$' or '+' or '#') s = s[1..];
        if (s.Length > 0 && s[^1] == ':') s = s[..^1];
        return s.Trim().ToString();
    }

    // The game's white space (a non-breaking space is not white, so a marker after one does not match).
    private const string EngineWhite = " \t\n\v\f\r";

    /// <summary>True when two markers name the same field: case-insensitive like the game, but white space inside counts (<c>$Fire  Wait:</c> is not <c>$Fire Wait:</c>).</summary>
    public static bool Same(string a, string b)
    {
        var x = a.AsSpan().Trim(EngineWhite);
        var y = b.AsSpan().Trim(EngineWhite);
        if (x.Equals(y, StringComparison.OrdinalIgnoreCase)) return true;
        // A schema name may stand for a numbered family: "$Thruster VFX #:" matches "$Thruster VFX 1:".
        return (x.Length > 1 && x[1..].Contains('#')) ? Numbered(x, y) : (y.Length > 1 && y[1..].Contains('#')) && Numbered(y, x);
    }

    private static bool Numbered(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
    {
        int i = 0, j = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '#' && i > 0)
            {
                int start = j;
                while (j < name.Length && char.IsAsciiDigit(name[j])) j++;
                if (j == start) return false;
                i++;
                continue;
            }
            if (j >= name.Length || char.ToUpperInvariant(pattern[i]) != char.ToUpperInvariant(name[j])) return false;
            i++;
            j++;
        }
        return j == name.Length;
    }

    /// <summary>Trims a marker and collapses its white space (<c>$Body  Temp :</c> -> <c>$Body Temp:</c>).</summary>
    public static string Normalize(string marker)
    {
        var sb = new System.Text.StringBuilder(marker.Length);
        bool space = false;
        foreach (char c in marker.Trim(EngineWhite.ToCharArray()))
        {
            if (EngineWhite.Contains(c)) { space = true; continue; }
            if (space && sb.Length > 0 && c != ':') sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The first field named <paramref name="marker"/>, with its index, or (-1, null).</summary>
    public static (int Index, TblFieldSchema? Field) Find(ImmutableArray<TblFieldSchema> fields, string marker)
    {
        if (fields.IsDefaultOrEmpty) return (-1, null);
        // Cached per field list: the parser looks up every marker of a table, often thousands of times.
        var cache = Lookups.GetValue(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(fields)!,
            _ => new System.Collections.Concurrent.ConcurrentDictionary<string, (int, TblFieldSchema?)>(StringComparer.OrdinalIgnoreCase));
        return cache.GetOrAdd(marker.Trim(), m =>
        {
            for (int i = 0; i < fields.Length; i++)
                if (Same(fields[i].Name, m)) return (i, fields[i]);
            return (-1, null);
        });
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TblFieldSchema[], System.Collections.Concurrent.ConcurrentDictionary<string, (int, TblFieldSchema?)>> Lookups = new();

    /// <summary>Case-insensitive <c>*</c>/<c>?</c> match of a whole file name.</summary>
    public static bool Wildcard(string pattern, string name)
    {
        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
