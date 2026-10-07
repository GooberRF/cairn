using System.Globalization;
using Cairn.Assets;
using Cairn.Formats.Tbl;
using Cairn.Tbl.Index;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Assist;

/// <summary>What a completion item inserts.</summary>
public enum TblCompletionKind { Field, Section, Value, Name, File }

/// <summary>One completion suggestion.</summary>
/// <param name="Label">What the list shows.</param>
/// <param name="InsertText">What replaces <see cref="TblCompletion.ReplaceSpan"/>.</param>
/// <param name="Kind">Field, section, value, entry name or file name.</param>
/// <param name="Detail">Short type or location text.</param>
/// <param name="Documentation">Longer description, or null.</param>
/// <param name="Priority">Lower sorts first (engine order for fields).</param>
public sealed record TblCompletionItem(string Label, string InsertText, TblCompletionKind Kind, string? Detail, string? Documentation, int Priority);

/// <summary>Completion at a caret: the range to replace and the items.</summary>
public sealed record TblCompletion(TextSpan ReplaceSpan, ImmutableArray<TblCompletionItem> Items)
{
    public static TblCompletion Empty(int offset) => new(TextSpan.At(offset), []);
}

/// <summary>What is under the caret.</summary>
public enum TblSymbolKind { Field, Section, EntryName, File, Ref }

/// <summary>A symbol at an offset.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Name">Field marker, section name, entry name, file name or referenced name.</param>
/// <param name="IndexKind">For entries and references: the index kind (weapon...); for files: the file kind.</param>
/// <param name="Span">Its characters.</param>
/// <param name="Field">The field involved, if any.</param>
public sealed record TblSymbol(TblSymbolKind Kind, string Name, string? IndexKind, TextSpan Span, TblFieldNode? Field);

/// <summary>Hover text.</summary>
/// <param name="Span">The range the hover is about.</param>
/// <param name="Title">First line, e.g. <c>$Weapon Type:</c>.</param>
/// <param name="Lines">Further plain lines.</param>
public sealed record TblHover(TextSpan Span, string Title, ImmutableArray<string> Lines)
{
    public override string ToString() => Title + (Lines.IsEmpty ? "" : Environment.NewLine + string.Join(Environment.NewLine, Lines));
}

/// <summary>Where go-to-definition leads: a table entry, or a file.</summary>
/// <param name="Definition">The defining entry (in another table or this one), or null.</param>
/// <param name="File">The resolved file, or null.</param>
/// <param name="FileName">The file name as written, for a file target.</param>
public sealed record TblDefinitionTarget(TblDefinition? Definition, AssetLocation? File, string? FileName);

/// <summary>Editor assistance: completion, hover, go to definition, find usages. All methods are pure and never throw on any document.</summary>
public static class TblAssist
{
    /// <summary>The symbol at <paramref name="offset"/>, or null.</summary>
    public static TblSymbol? SymbolAt(TblDocument doc, int offset)
    {
        foreach (var r in doc.FieldAt(offset) is { } f ? TblValueRoles.Of(f) : [])
        {
            if (r.Span.ContainsInclusive(offset))
                return new TblSymbol(r.Role == TblValueRole.File ? TblSymbolKind.File : TblSymbolKind.Ref, r.Name.Trim(), r.Kind, r.Span, r.Field);
        }
        var entry = doc.EntryAt(offset);
        if (entry is not null && entry.EntryField is not null && entry.NameSpan.ContainsInclusive(offset) && entry.NameSpan != entry.EntryField.MarkerSpan)
            return new TblSymbol(TblSymbolKind.EntryName, entry.Name, entry.Section.Schema?.Defines, entry.NameSpan, entry.EntryField);
        var field = doc.FieldAt(offset);
        if (field is not null && field.MarkerSpan.ContainsInclusive(offset))
            return new TblSymbol(TblSymbolKind.Field, field.Marker, null, field.MarkerSpan, field);
        int ti = doc.TokenIndexAt(offset);
        if (ti >= 0 && doc.Tokens[ti] is { Kind: TblLexKind.Header } h && h.Span.ContainsInclusive(offset))
            return new TblSymbol(TblSymbolKind.Section, h.GetText(doc.Text)[1..].Trim(), null, h.Span, null);
        return null;
    }

    // ---- Completion ----

    /// <summary>Completion items at <paramref name="offset"/>.</summary>
    public static TblCompletion Complete(TblDocument doc, int offset, TblIndex? index = null)
    {
        offset = Math.Clamp(offset, 0, doc.Text.Length);
        int lineStart = doc.Lines.StartOf(doc.Lines.LineOf(offset));
        string before = doc.Text[lineStart..offset];
        string trimmed = before.TrimStart();
        int wordStart = lineStart + (before.Length - trimmed.Length);

        // Inside a comment: nothing.
        int ti = doc.TokenIndexAt(Math.Max(0, offset - 1));
        if (ti >= 0 && doc.Tokens[ti].IsComment && doc.Tokens[ti].Span.Contains(Math.Max(0, offset - 1)) && !(doc.Tokens[ti].Kind == TblLexKind.LineComment && offset == doc.Tokens[ti].Start))
            return TblCompletion.Empty(offset);

        if (trimmed.StartsWith('#') && !trimmed.Contains(' ', StringComparison.Ordinal) || trimmed.StartsWith('#') && trimmed.Length == before.Trim().Length && IsHeaderLine(doc, wordStart))
            return CompleteSections(doc, offset, wordStart, LineEnd(doc, offset));

        bool atMarker = trimmed.Length == 0 || (trimmed[0] is '$' or '+' && !trimmed.Contains(':')) || (IsIdentifier(trimmed) && !trimmed.Contains(':'));
        if (atMarker)
        {
            // Replace the whole marker under the caret (colon included) when there is one.
            int replaceEnd = offset;
            int tok = doc.TokenIndexAt(offset);
            if (tok >= 0 && doc.Tokens[tok].Kind is TblLexKind.FieldName or TblLexKind.BareKey or TblLexKind.Word && doc.Tokens[tok].Start == wordStart)
                replaceEnd = doc.Tokens[tok].End;
            return CompleteFields(doc, offset, TextSpan.FromBounds(wordStart, replaceEnd));
        }
        return CompleteValue(doc, offset, index);
    }

    private static bool IsIdentifier(string s) => s.All(c => char.IsLetterOrDigit(c) || c is '_' or ' ');

    private static bool IsHeaderLine(TblDocument doc, int at)
    {
        int ti = doc.TokenIndexAt(at);
        return ti >= 0 && doc.Tokens[ti].Kind == TblLexKind.Header;
    }

    private static int LineEnd(TblDocument doc, int offset)
    {
        int i = offset;
        while (i < doc.Text.Length && doc.Text[i] is not ('\r' or '\n')) i++;
        while (i > offset && TblLexer.IsSpace(doc.Text[i - 1])) i--;
        return i;
    }

    private static TblCompletion CompleteSections(TblDocument doc, int offset, int start, int end)
    {
        var items = new List<TblCompletionItem>();
        int priority = 0;
        var present = new HashSet<string>(doc.Sections.Where(s => s.HasHeader).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var s in doc.Schema?.Sections ?? [])
        {
            if (s.IsRoot || (present.Contains(s.BareName) && !s.Repeat)) continue;
            items.Add(new TblCompletionItem(s.Name, s.Name, TblCompletionKind.Section, s.Required ? "required section" : "section", s.Doc, priority++));
        }
        items.Add(new TblCompletionItem("#End", "#End", TblCompletionKind.Section, "ends the section", null, priority));
        return new TblCompletion(TextSpan.FromBounds(start, Math.Max(start, end)), [.. items]);
    }

    /// <summary>Field markers valid at <paramref name="offset"/> in engine order.</summary>
    private static TblCompletion CompleteFields(TblDocument doc, int offset, TextSpan replace)
    {
        var items = new List<TblCompletionItem>();
        var section = doc.SectionAt(offset);
        var ss = section?.Schema;
        if (ss is null)
        {
            // No schema: the markers this document already uses, in first-seen order.
            int p = 0;
            foreach (var m in doc.AllFields.Select(f => f.Marker).Distinct(StringComparer.OrdinalIgnoreCase))
                items.Add(new TblCompletionItem(m, m + " ", TblCompletionKind.Field, null, null, p++));
            return new TblCompletion(replace, [.. items]);
        }

        var entry = doc.EntryAt(offset);
        var fields = entry?.Fields ?? section!.Fields;
        // The field before the caret and its sub-fields first, then the top-level fields that may follow.
        var previous = doc.FieldBefore(replace.Start);
        if (previous is not null && (entry is null ? previous.Section != section : previous.Entry != entry)) previous = null;
        int priority = 0;
        // A sub-field after the caret bounds what may go before it, and keeps every top-level field out (one
        // inserted here would end the parent's sub-fields and leave that sub-field out of order).
        bool childFollows = false;
        for (var f = previous; f is not null; f = f.Parent)
        {
            // Children the game reads only in the other branch of their parent are not offered.
            if (f.Schema is not { Children.Length: > 0 } fsc || TblConditions.ChildrenRead(f) == false) continue;
            int last = -1, upper = fsc.Children.Length;
            foreach (var c in f.Children)
            {
                if (c.Schema is null) continue;
                int at = fsc.Children.IndexOf(c.Schema);
                if (c.MarkerSpan.Start < replace.Start) last = Math.Max(last, at);
                else if (c.MarkerSpan.Start >= replace.End && at >= 0) { upper = Math.Min(upper, at); childFollows = true; }
            }
            for (int k = Math.Max(0, last); k < fsc.Children.Length && k <= upper; k++)
            {
                var c = fsc.Children[k];
                if ((k == last || k == upper) && !c.Repeat) continue;
                items.Add(Item(c, priority++));
            }
            if (childFollows) break;
        }
        if (childFollows) return new TblCompletion(replace, [.. items]);
        var top = previous;
        while (top?.Parent is not null) top = top.Parent;
        int pos = top?.Schema is { } ts ? ss.Fields.IndexOf(ts) : -1;
        int next = ss.Fields.Length;
        foreach (var f in fields)
        {
            if (f.MarkerSpan.Start < replace.End || f.Schema is null) continue;
            int i = ss.Fields.IndexOf(f.Schema);
            if (i >= 0 && i < next) { next = i; break; }
        }
        for (int k = Math.Max(0, pos); k < next && k < ss.Fields.Length; k++)
        {
            var fs = ss.Fields[k];
            if (k == pos && !fs.Repeat) continue;
            if (entry is not null && ss.Entry is not null && TblSchemaNames.Same(fs.Name, ss.Entry) && k == 0 && pos >= 0) continue;
            // A field the game reads only when another field has a value ($Cycle Position: for player_wep) is offered only then.
            if (!TblConditions.IsReadAmong(fs, fields)) continue;
            items.Add(Item(fs, priority++));
        }
        // Starting a new entry is always possible after the current one.
        if (ss.Entry is { } em && !em.StartsWith('#') && ss.FindField(em).Field is { } entryField && entry is not null && !items.Any(i => TblSchemaNames.Same(i.Label, em)))
            items.Add(Item(entryField, priority++) with { Detail = "starts a new entry" });
        return new TblCompletion(replace, [.. items]);
    }

    private static TblCompletionItem Item(TblFieldSchema fs, int priority) =>
        new(fs.Name, fs.Name + " ", TblCompletionKind.Field, Describe(fs), fs.Doc, priority);

    private static TblCompletion CompleteValue(TblDocument doc, int offset, TblIndex? index)
    {
        var field = doc.FieldAt(offset) ?? doc.FieldBefore(offset);
        if (field?.Schema is null) return TblCompletion.Empty(offset);
        var (_, value) = doc.ValueAt(offset);
        // Positional item types.
        var fs = field.Schema;
        if (fs.Items.Length > 0)
        {
            int position = field.Values.Count(v => v.Span.End < offset || (value is not null && v.Span.Start < value.Span.Start));
            if (value is not null) position = field.Values.IndexOf(field.Values.FirstOrDefault(v => v.Span.ContainsInclusive(offset)) ?? value);
            if (position < 0) position = 0;
            if (position >= fs.Items.Length) return TblCompletion.Empty(offset);
            fs = fs.Items[position];
        }

        bool inString = value is { Kind: TblValueKind.String } && value.ContentSpan.ContainsInclusive(offset) && offset > value.Span.Start;
        TextSpan replace = inString ? value!.ContentSpan : value is { IsScalar: true } ? value.Span : TextSpan.At(offset);
        string Wrap(string s) => inString ? s : "\"" + s + "\"";

        var items = new List<TblCompletionItem>();
        int p = 0;
        switch (fs.Type)
        {
            case TblValueType.Bool:
                items.Add(new("true", "true", TblCompletionKind.Value, null, null, p++));
                items.Add(new("false", "false", TblCompletionKind.Value, null, null, p++));
                break;
            case TblValueType.Enum:
            case TblValueType.Flags:
                foreach (var v in fs.Values) items.Add(new(v, Wrap(v), TblCompletionKind.Value, null, null, p++));
                break;
            default:
                if (fs.RefKind is { } rk && index is not null && fs.Type != TblValueType.File)
                    foreach (var n in index.Names(rk)) items.Add(new(n, Wrap(n), TblCompletionKind.Name, rk, null, p++));
                else if ((fs.Type == TblValueType.File || fs.FileKind is not null) && index is not null)
                    foreach (var n in index.FileNames(fs.FileKind)) items.Add(new(n, Wrap(n), TblCompletionKind.File, fs.FileKind, null, p++));
                break;
        }
        return new TblCompletion(replace, [.. items]);
    }

    /// <summary>"float, 0 to 100 metres, required"</summary>
    public static string Describe(TblFieldSchema fs)
    {
        var parts = new List<string> { fs.TypeName };
        if (fs.FileKind is { } fk) parts[0] += $" ({fk})";
        if (fs.RefKind is { } rk) parts[0] += $" ({rk})";
        if (fs.Min is not null || fs.Max is not null)
            parts.Add($"{Fmt(fs.Min)} to {Fmt(fs.Max)}{(fs.Unit is { } u ? " " + u : "")}");
        else if (fs.Unit is { } u2) parts.Add(u2);
        if (fs.RequiredIf is { } c) parts.Add($"required when {c}{(fs.AbsentOtherwise ? ", not allowed otherwise" : "")}");
        else if (fs.RequiredAfter is { } after) parts.Add($"required after {after}");
        else parts.Add(fs.Required ? "required" : "optional");
        if (fs.Repeat) parts.Add("may repeat");
        if (fs.AlpineSince is { } a) parts.Add($"Alpine Faction {a}+");
        return string.Join(", ", parts);
    }

    private static string Fmt(double? d) => d is null ? "..." : d.Value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- Hover ----

    /// <summary>Hover information at <paramref name="offset"/>, or null.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="offset">Caret or mouse offset.</param>
    /// <param name="index">For entry names and references (optional).</param>
    /// <param name="resolveFile">Resolves a file name (optional): what a file name resolves to.</param>
    public static TblHover? Hover(TblDocument doc, int offset, TblIndex? index = null, Func<string, AssetLocation?>? resolveFile = null)
    {
        var symbol = SymbolAt(doc, offset);
        if (symbol is null) return null;
        var lines = new List<string>();
        switch (symbol.Kind)
        {
            case TblSymbolKind.Field:
            {
                var fs = symbol.Field!.Schema;
                if (fs is null)
                {
                    lines.Add(doc.Schema is null ? "No description is known for this table." : "The game does not read this field here.");
                    return new TblHover(symbol.Span, symbol.Name, [.. lines]);
                }
                if (fs.Doc is { } d) lines.Add(d);
                lines.Add(Describe(fs));
                if (fs.Default is { } def) lines.Add("Default: " + def);
                if (fs.Values.Length > 0) lines.Add("Values: " + string.Join(", ", fs.Values.Take(24)) + (fs.Values.Length > 24 ? ", ..." : ""));
                if (fs.AlpineSince is null && doc.Schema?.AlpineSince is { } since) lines.Add($"Alpine Faction {since}+");
                return new TblHover(symbol.Span, fs.Name, [.. lines]);
            }
            case TblSymbolKind.Section:
            {
                var ss = doc.Schema?.FindSection(symbol.Name);
                if (ss?.Doc is { } d) lines.Add(d);
                if (ss?.Defines is { } k) lines.Add($"Each entry defines a {k}.");
                if (doc.Schema?.AlpineSince is { } t) lines.Add($"{doc.Schema.File ?? doc.Schema.Title}: Alpine Faction {t} or later.");
                return new TblHover(symbol.Span, "#" + symbol.Name, [.. lines]);
            }
            case TblSymbolKind.File:
            {
                var candidates = TblFileName.Normalize(symbol.Name);
                AssetLocation? hit = null;
                if (resolveFile is not null)
                {
                    try { hit = candidates.Select(resolveFile).FirstOrDefault(l => l is not null); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { hit = null; }
                }
                if (candidates.Count > 1 || !string.Equals(candidates[0], symbol.Name, StringComparison.OrdinalIgnoreCase))
                    lines.Add("The game loads: " + string.Join(" or ", candidates));
                if (resolveFile is not null)
                    lines.Add(hit is null ? "Not found in the game data, the search folders or next to the table." : $"Found: {hit.ResolvedName} in {hit.DisplayLocation}");
                return new TblHover(symbol.Span, $"File \"{symbol.Name}\" ({symbol.IndexKind})", [.. lines]);
            }
            case TblSymbolKind.Ref:
            {
                if (index is not null)
                {
                    var defs = index.Define(symbol.IndexKind!, symbol.Name);
                    if (defs.IsEmpty) lines.Add($"No {symbol.IndexKind} with this name is defined in the indexed tables.");
                    foreach (var d in defs.Take(5)) lines.Add($"Defined in {d.Source.FileName} ({d.Source.DisplayLocation}), line {d.Line}");
                }
                return new TblHover(symbol.Span, $"{symbol.IndexKind} \"{symbol.Name}\"", [.. lines]);
            }
            default:
            {
                if (symbol.IndexKind is { } kind)
                {
                    lines.Add($"Defines a {kind}.");
                    if (index is not null) lines.Add($"Used {index.Usages(kind, symbol.Name).Length} times in the indexed tables.");
                }
                return new TblHover(symbol.Span, $"\"{symbol.Name}\"", [.. lines]);
            }
        }
    }

    // ---- Navigation ----

    /// <summary>The definition of the reference or file name at <paramref name="offset"/>, or null.</summary>
    public static TblDefinitionTarget? FindDefinition(TblDocument doc, int offset, TblIndex? index, Func<string, AssetLocation?>? resolveFile = null)
    {
        var symbol = SymbolAt(doc, offset);
        if (symbol is null) return null;
        if (symbol.Kind == TblSymbolKind.Ref && index is not null)
        {
            var def = index.Define(symbol.IndexKind!, symbol.Name).FirstOrDefault();
            return def is null ? null : new TblDefinitionTarget(def, null, null);
        }
        if (symbol.Kind == TblSymbolKind.File && resolveFile is not null)
        {
            AssetLocation? hit = null;
            try { hit = TblFileName.Normalize(symbol.Name).Select(resolveFile).FirstOrDefault(l => l is not null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { hit = null; }
            return hit is null ? null : new TblDefinitionTarget(null, hit, symbol.Name);
        }
        return null;
    }

    /// <summary>
    /// Every use of the entry name, reference or file name at <paramref name="offset"/> across the indexed
    /// tables (empty when there is nothing to look up).
    /// </summary>
    public static ImmutableArray<TblReference> FindUsages(TblDocument doc, int offset, TblIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var symbol = SymbolAt(doc, offset);
        return symbol switch
        {
            { Kind: TblSymbolKind.File } => index.FileUsages(symbol.Name),
            { Kind: TblSymbolKind.Ref or TblSymbolKind.EntryName, IndexKind: { } kind } => index.Usages(kind, symbol.Name),
            _ => [],
        };
    }

    /// <summary>The text a missing field would be inserted as (marker plus a placeholder value).</summary>
    public static string FieldTemplate(TblFieldSchema fs) => (fs.Name + " " + TblTemplates.DefaultValue(fs)).TrimEnd();
}
