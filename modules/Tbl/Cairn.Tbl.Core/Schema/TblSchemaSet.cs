using System.Text.Json;

namespace Cairn.Tbl.Schema;

/// <summary>A schema file that could not be read, with why.</summary>
public sealed record TblSchemaLoadError(string Source, string Message);

/// <summary>
/// The table schemas, loaded from the JSON files embedded in this assembly. Unknown keys are ignored;
/// a file that cannot be read is skipped and listed in <see cref="LoadErrors"/> (the tests require that
/// list to be empty), never thrown.
/// </summary>
public sealed class TblSchemaSet
{
    private const string ResourcePrefix = "Cairn.Tbl.Schema.Tables.";
    private static readonly Lazy<TblSchemaSet> LazyDefault = new(LoadEmbedded);

    public TblSchemaSet(IEnumerable<TblTableSchema> tables, IEnumerable<TblSchemaLoadError>? errors = null)
    {
        Tables = [.. tables];
        LoadErrors = [.. errors ?? []];
    }

    /// <summary>The schemas embedded in Cairn.Tbl.Core.</summary>
    public static TblSchemaSet Default => LazyDefault.Value;

    public ImmutableArray<TblTableSchema> Tables { get; }
    public ImmutableArray<TblSchemaLoadError> LoadErrors { get; }

    /// <summary>
    /// The schema for a table file name (folder parts ignored): an exact <c>file</c> match first, then the
    /// first matching <c>pattern</c>. Null when no schema describes the file.
    /// </summary>
    public TblTableSchema? Find(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string name = Path.GetFileName(fileName.Trim());
        foreach (var t in Tables)
            if (t.File is not null && string.Equals(t.File, name, StringComparison.OrdinalIgnoreCase)) return t;
        foreach (var t in Tables)
            if (t.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))) return t;
        foreach (var t in Tables)
            if (t.File is null && t.Pattern is not null && TblSchemaNames.Wildcard(t.Pattern, name)) return t;
        return null;
    }

    /// <summary>Every index kind some section defines (weapon, ammo, ...).</summary>
    public IEnumerable<string> DefinedKinds() =>
        Tables.SelectMany(t => t.Sections).Select(s => s.Defines).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tables whose sections define <paramref name="kind"/> (for "where is this defined" messages).</summary>
    public IEnumerable<TblTableSchema> TablesDefining(string kind) =>
        Tables.Where(t => t.Sections.Any(s => string.Equals(s.Defines, kind, StringComparison.OrdinalIgnoreCase)));

    private static TblSchemaSet LoadEmbedded()
    {
        var asm = typeof(TblSchemaSet).Assembly;
        var tables = new List<TblTableSchema>();
        var errors = new List<TblSchemaLoadError>();
        foreach (string res in asm.GetManifestResourceNames().Where(r => r.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            string source = res[ResourcePrefix.Length..];
            try
            {
                using var stream = asm.GetManifestResourceStream(res)!;
                using var reader = new StreamReader(stream);
                tables.Add(Parse(reader.ReadToEnd(), source));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or IOException or InvalidOperationException or KeyNotFoundException)
            {
                errors.Add(new TblSchemaLoadError(source, ex.Message));
            }
        }
        return new TblSchemaSet(tables, errors);
    }

    /// <summary>Loads schemas from JSON texts (tests and tools). Bad files go to <see cref="LoadErrors"/>.</summary>
    public static TblSchemaSet FromJson(IEnumerable<(string Source, string Json)> files)
    {
        var tables = new List<TblTableSchema>();
        var errors = new List<TblSchemaLoadError>();
        foreach (var (source, json) in files)
        {
            try { tables.Add(Parse(json, source)); }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                errors.Add(new TblSchemaLoadError(source, ex.Message));
            }
        }
        return new TblSchemaSet(tables, errors);
    }

    /// <summary>Parses one schema file. Throws <see cref="JsonException"/> or <see cref="FormatException"/> when it is unusable.</summary>
    public static TblTableSchema Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The schema is not a JSON object.");
        string? file = Str(root, "file");
        string? pattern = Str(root, "pattern");
        if (file is null && pattern is null) throw new FormatException("The schema has neither \"file\" nor \"pattern\".");

        var sections = new List<TblSectionSchema>();
        if (root.TryGetProperty("sections", out var secs))
        {
            if (secs.ValueKind != JsonValueKind.Array) throw new FormatException("\"sections\" is not an array.");
            foreach (var s in secs.EnumerateArray()) sections.Add(ParseSection(s));
        }
        // Convenience: a headerless table may list its fields at the top level.
        if (root.TryGetProperty("fields", out var topFields) && topFields.ValueKind == JsonValueKind.Array)
        {
            sections.Insert(0, new TblSectionSchema
            {
                Name = string.Empty,
                Entry = Str(root, "entry"),
                Defines = Str(root, "defines"),
                Fields = ParseFields(topFields),
            });
        }

        string? alpineSince = null, alpineNotes = null;
        if (root.TryGetProperty("alpine", out var alpine))
        {
            if (alpine.ValueKind == JsonValueKind.Object) { alpineSince = Str(alpine, "since"); alpineNotes = Str(alpine, "notes"); }
            else if (alpine.ValueKind == JsonValueKind.String) alpineSince = alpine.GetString();
            else if (alpine.ValueKind == JsonValueKind.True) alpineSince = "1.0";
        }

        return new TblTableSchema
        {
            File = file,
            Pattern = pattern,
            Title = Str(root, "title") ?? file ?? pattern!,
            Doc = Str(root, "doc"),
            Sections = [.. sections],
            AlpineSince = alpineSince,
            AlpineNotes = alpineNotes,
            Source = source,
            Aliases = root.TryGetProperty("aliases", out var al) ? al.ValueKind switch
            {
                JsonValueKind.String => [al.GetString()!],
                JsonValueKind.Array => [.. al.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)],
                _ => [],
            } : [],
            EditorOnly = Bool(root, "editorOnly"),
            Syntax = Str(root, "syntax"),
        };
    }

    private static TblSectionSchema ParseSection(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) throw new FormatException("A section is not a JSON object.");
        string name = Str(s, "name") ?? string.Empty;
        if (name.Length > 0 && !name.StartsWith('#')) name = "#" + name;
        string? entry = Str(s, "entry");
        if (entry is { Length: 0 }) entry = null;
        return new TblSectionSchema
        {
            Name = name,
            Required = Bool(s, "required"),
            Repeat = Bool(s, "repeat"),
            Doc = Str(s, "doc"),
            Entry = entry,
            Defines = Str(s, "defines") is { Length: > 0 } d ? d : null,
            Fields = s.TryGetProperty("fields", out var f) ? ParseFields(f) : [],
            End = Str(s, "end") is { Length: > 0 } end ? end : null,
            Layout = (Str(s, "layout") ?? "").ToLowerInvariant() switch
            {
                "rows" => TblSectionLayout.Rows,
                "matrix" => TblSectionLayout.Matrix,
                "text" => TblSectionLayout.Text,
                "text-blocks" or "textblocks" => TblSectionLayout.TextBlocks,
                _ => TblSectionLayout.Fields,
            },
            Columns = s.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array
                ? [.. cols.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => ParseItem(e, name))]
                : [],
            Key = Str(s, "key"),
            LocateBySearch = string.Equals(Str(s, "locate"), "search", StringComparison.OrdinalIgnoreCase),
            EntrySearch = Bool(s, "entrySearch") || string.Equals(Str(s, "between"), "skip", StringComparison.OrdinalIgnoreCase),
            EntrySearchCaseSensitive = Bool(s, "entrySearch"),
            MaxEntries = Num(s, "maxEntries") is double max ? (int)max : null,
            FreeOrder = string.Equals(Str(s, "order"), "free", StringComparison.OrdinalIgnoreCase),
            MatrixSize = Num(s, "size") is double size && size >= 0 ? (int)size : null,
            MatrixCell = s.TryGetProperty("cell", out var cell) && cell.ValueKind == JsonValueKind.Object ? ParseItem(cell, "cell") : null,
            MatrixHeader = s.TryGetProperty("header", out var head) && head.ValueKind == JsonValueKind.Object ? ParseItem(head, "header") : null,
        };
    }

    private static ImmutableArray<TblFieldSchema> ParseFields(JsonElement array)
    {
        if (array.ValueKind == JsonValueKind.Null) return [];
        if (array.ValueKind != JsonValueKind.Array) throw new FormatException("\"fields\" is not an array.");
        var list = ImmutableArray.CreateBuilder<TblFieldSchema>();
        foreach (var f in array.EnumerateArray()) list.Add(ParseField(f));
        return list.ToImmutable();
    }

    private static TblFieldSchema ParseField(JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object) throw new FormatException("A field is not a JSON object.");
        string name = Str(f, "name") ?? throw new FormatException("A field has no \"name\".");
        string typeName = Str(f, "type") ?? (Bool(f, "valueless") ? "none" : "string");
        return new TblFieldSchema
        {
            Name = name.Trim(),
            TypeName = typeName,
            Type = ParseType(typeName),
            Required = Bool(f, "required"),
            Repeat = Bool(f, "repeat"),
            Values = f.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : [],
            FileKind = Str(f, "file"),
            RefKind = Str(f, "ref"),
            Min = Num(f, "min"),
            Max = Num(f, "max"),
            Unit = Str(f, "unit"),
            Default = f.TryGetProperty("default", out var d) ? (d.ValueKind == JsonValueKind.String ? d.GetString() : d.ValueKind is JsonValueKind.Null ? null : d.GetRawText()) : null,
            AlpineSince = Str(f, "alpineSince"),
            Doc = Str(f, "doc"),
            Children = f.TryGetProperty("children", out var c) ? ParseFields(c) : [],
            Items = (f.TryGetProperty("parts", out var items) || f.TryGetProperty("items", out items)) && items.ValueKind == JsonValueKind.Array
                ? [.. items.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => ParseItem(e, name))]
                : [],
            ElementType = Str(f, "of") is { Length: > 0 } of ? ParseType(of) : null,
            Strict = !(f.TryGetProperty("strict", out var strict) && strict.ValueKind == JsonValueKind.False),
            ChildrenOnlyIfTrue = string.Equals(Str(f, "if"), "true", StringComparison.OrdinalIgnoreCase),
            CountFrom = Str(f, "countFrom"),
            ChildrenWhen = Str(f, "childrenWhen") ?? (f.TryGetProperty("childrenUnless", out _) ? "unless" : null),
            Seek = Bool(f, "seek"),
            EditorOnly = Bool(f, "editorOnly"),
            MaxLength = Num(f, "maxLength") is double ml ? (int)ml : null,
            MaxCount = Num(f, "maxCount") is double mc ? (int)mc : null,
            Syntax = Str(f, "syntax"),
            Rows = f.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
                ? [.. rows.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => ParseItem(e, name))]
                : [],
            ChildrenUnless = f.TryGetProperty("childrenUnless", out var unless) && unless.ValueKind == JsonValueKind.Array
                ? [.. unless.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : [],
            When = Str(f, "when"),
            RequiredIf = ParseRequiredIf(Str(f, "when")),
            RequiredAfter = Str(f, "when") is { } after && after.Trim().StartsWith("required after ", StringComparison.OrdinalIgnoreCase)
                ? MarkerWithColon(after.Trim()["required after ".Length..]) : null,
            AbsentOtherwise = string.Equals(Str(f, "otherwise"), "absent", StringComparison.OrdinalIgnoreCase),
            Quotes = Str(f, "quotes"),
            NameSuffix = Str(f, "nameSuffix"),
        };
    }

    // "required if $Flags contains player_wep" (also "has").
    private static TblFieldCondition? ParseRequiredIf(string? when)
    {
        if (when is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(when.Trim(), @"^required if\s+([$+][^:]+?):?\s+(?:contains|has)\s+(\S+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? new TblFieldCondition(m.Groups[1].Value.Trim() + ":", m.Groups[2].Value.Trim('"')) : null;
    }

    private static string MarkerWithColon(string marker)
    {
        marker = marker.Trim();
        return marker.EndsWith(':') ? marker : marker + ":";
    }

    // Positional item types need no name of their own.
    private static TblFieldSchema ParseItem(JsonElement e, string owner)
    {
        string typeName = Str(e, "type") ?? "string";
        return new TblFieldSchema
        {
            Name = Str(e, "name") ?? owner,
            TypeName = typeName,
            Type = ParseType(typeName),
            Required = Bool(e, "required"),
            Values = e.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                : [],
            FileKind = Str(e, "file"),
            RefKind = Str(e, "ref"),
            Min = Num(e, "min"),
            Max = Num(e, "max"),
            Doc = Str(e, "doc"),
            ElementType = Str(e, "of") is { Length: > 0 } of ? ParseType(of) : null,
            Strict = !(e.TryGetProperty("strict", out var strict) && strict.ValueKind == JsonValueKind.False),
            MaxLength = Num(e, "maxLength") is double ml ? (int)ml : null,
            Unit = Str(e, "unit"),
        };
    }

    /// <summary>Maps a schema type name to <see cref="TblValueType"/> (unknown names are free text).</summary>
    public static TblValueType ParseType(string name) => name.Trim().ToLowerInvariant() switch
    {
        "string" => TblValueType.String,
        "int" or "integer" => TblValueType.Int,
        "float" or "number" => TblValueType.Float,
        "bool" or "boolean" => TblValueType.Bool,
        "vec3" or "vector" => TblValueType.Vec3,
        "color" or "colour" => TblValueType.Color,
        "enum" => TblValueType.Enum,
        "flags" => TblValueType.Flags,
        "file" => TblValueType.File,
        "ref" => TblValueType.Ref,
        "list" => TblValueType.List,
        "none" or "marker" => TblValueType.None,
        _ => TblValueType.Text,
    };

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static bool Bool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static double? Num(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
        return null;
    }
}
