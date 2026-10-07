using System.Globalization;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Model;

/// <summary>What a value is.</summary>
public enum TblValueKind
{
    /// <summary>A quoted string; <see cref="TblValueNode.Text"/> is its content without quotes.</summary>
    String,
    Number,
    Boolean,
    /// <summary>A bare word (free text, an unquoted name).</summary>
    Word,
    /// <summary><c>( ... )</c>; <see cref="TblValueNode.Items"/> are its elements.</summary>
    List,
    /// <summary><c>&lt; ... &gt;</c>.</summary>
    Vector,
    /// <summary><c>{ ... }</c>.</summary>
    Block,
    /// <summary><c>WORD( ... )</c> such as <c>XSTR(296, "Remote Charge")</c>; <see cref="TblValueNode.Text"/> is the word.</summary>
    Call,
    /// <summary>A closing bracket with nothing open to close.</summary>
    StrayCloser,
}

/// <summary>One value of a field (or free text in a section).</summary>
public sealed class TblValueNode
{
    internal TblValueNode(TblValueKind kind, TextSpan span, string text, ImmutableArray<TblValueNode> items, bool unclosed, bool unterminated)
    {
        Kind = kind; Span = span; Text = text; Items = items; IsUnclosed = unclosed; IsUnterminated = unterminated;
    }

    public TblValueKind Kind { get; }
    /// <summary>The whole value, quotes and brackets included.</summary>
    public TextSpan Span { get; }
    /// <summary>A string's content, a word or number as written, a call's name; empty for groups.</summary>
    public string Text { get; }
    /// <summary>Elements of a list, vector, block or call (commas dropped).</summary>
    public ImmutableArray<TblValueNode> Items { get; }
    /// <summary>A group without its closing bracket.</summary>
    public bool IsUnclosed { get; }
    /// <summary>A string without its closing quote.</summary>
    public bool IsUnterminated { get; }
    /// <summary>For a group: true when commas separate its elements.</summary>
    public bool HasCommas { get; internal init; }
    /// <summary>Bit k is set when a comma separates item k from item k + 1 (the first 31 gaps).</summary>
    public int CommaGaps { get; internal init; }

    /// <summary>For a string: the span of its content (inside the quotes). Otherwise <see cref="Span"/>.</summary>
    public TextSpan ContentSpan => Kind == TblValueKind.String
        ? TextSpan.FromBounds(Span.Start + 1, IsUnterminated ? Span.End : Math.Max(Span.Start + 1, Span.End - 1))
        : Span;

    /// <summary>The number, for a number value.</summary>
    public double? Number => Kind == TblValueKind.Number ? ParseNumber(Text) : null;

    /// <summary>Reads a number token: decimal, or <c>0x</c> hex as the game's integer reader accepts.</summary>
    public static double? ParseNumber(string text)
    {
        var s = text.AsSpan();
        bool negative = s.StartsWith("-");
        var body = s.TrimStart("+-");
        if (body.Length > 2 && body[0] == '0' && body[1] is 'x' or 'X')
            return long.TryParse(body[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long h) ? (negative ? -h : h) : null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    /// <summary>True for a string, word, number or boolean (not a group).</summary>
    public bool IsScalar => Kind is TblValueKind.String or TblValueKind.Number or TblValueKind.Boolean or TblValueKind.Word;

    /// <summary>This value and every nested value, depth first.</summary>
    public IEnumerable<TblValueNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var item in Items)
            foreach (var d in item.DescendantsAndSelf()) yield return d;
    }

    public override string ToString() => Kind switch
    {
        TblValueKind.String => "\"" + Text + "\"",
        TblValueKind.List => "(" + string.Join(" ", Items) + ")",
        TblValueKind.Vector => "<" + string.Join(", ", Items) + ">",
        TblValueKind.Block => "{" + string.Join(" ", Items) + "}",
        TblValueKind.Call => Text + "(" + string.Join(", ", Items) + ")",
        _ => Text,
    };
}

/// <summary>A field: its marker, its values and the sub-fields that belong to it.</summary>
public sealed class TblFieldNode
{
    internal TblFieldNode(string marker, char prefix, TextSpan markerSpan, ImmutableArray<TblValueNode> values)
    {
        Marker = marker; Prefix = prefix; MarkerSpan = markerSpan; Values = values;
    }

    /// <summary>The marker as written, colon included (<c>$Name:</c>, <c>+State:</c>, <c>En:</c>).</summary>
    public string Marker { get; }
    /// <summary><c>$</c>, <c>+</c>, or <c>'\0'</c> for a bare key such as <c>En:</c>.</summary>
    public char Prefix { get; }
    /// <summary>The marker without prefix and colon.</summary>
    public string Name => TblSchemaNames.Bare(Marker);
    public TextSpan MarkerSpan { get; }
    public ImmutableArray<TblValueNode> Values { get; internal set; }
    public ImmutableArray<TblFieldNode> Children { get; internal set; } = [];
    /// <summary>The field this one belongs to, or null for a field of an entry or section.</summary>
    public TblFieldNode? Parent { get; internal set; }
    /// <summary>The entry the field is in, or null.</summary>
    public TblEntry? Entry { get; internal set; }
    public TblSection Section { get; internal set; } = null!;
    /// <summary>The schema field matched, or null (unknown field or no schema).</summary>
    public TblFieldSchema? Schema { get; internal set; }
    /// <summary>For a text block: the <c>#End</c> line that ends it.</summary>
    public TextSpan? EndSpan { get; internal set; }

    /// <summary>Marker through the last value (or the text block's <c>#End</c>).</summary>
    public TextSpan Span
    {
        get
        {
            int end = MarkerSpan.End;
            if (Values.Length > 0) end = Math.Max(end, Values[^1].Span.End);
            if (EndSpan is { } e) end = Math.Max(end, e.End);
            return TextSpan.FromBounds(MarkerSpan.Start, end);
        }
    }

    /// <summary><see cref="Span"/> plus every child.</summary>
    public TextSpan FullSpan => Children.Length == 0 ? Span : TextSpan.FromBounds(Span.Start, Math.Max(Span.End, Children[^1].FullSpan.End));

    /// <summary>Depth below the entry or section (0 for a top-level field).</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>True when this field matches <paramref name="marker"/> (case and inner spacing ignored).</summary>
    public bool Is(string marker) => TblSchemaNames.Same(Marker, marker);

    /// <summary>The first value's text (a string's content), or null.</summary>
    public string? FirstText => Values.Length > 0 ? Values[0].Text : null;

    /// <summary>This field and its descendants, depth first.</summary>
    public IEnumerable<TblFieldNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.DescendantsAndSelf()) yield return d;
    }

    /// <summary>The value parsed by the schema type (null without a schema).</summary>
    public TblParsedValue? Parsed => Schema is null ? null : TblValueParser.Parse(this, Schema);

    public override string ToString() => Marker + " " + string.Join(" ", Values);
}

/// <summary>An entry: the fields from one entry field (<c>$Name:</c>) to the next.</summary>
public sealed class TblEntry
{
    internal TblEntry(TblSection section) { Section = section; }

    public TblSection Section { get; }
    /// <summary>The field that starts the entry, or null for an entry started by a numbered header (<c>#12</c> in strings.tbl).</summary>
    public TblFieldNode? EntryField { get; internal set; }
    /// <summary>The numbered header that starts the entry, if any.</summary>
    public TextSpan? HeaderSpan { get; internal set; }
    /// <summary>The entry's name (first value of the entry field, or the header number); empty when missing.</summary>
    public string Name { get; internal set; } = string.Empty;
    /// <summary>Where the name is written (a string's content), or the marker when there is no value.</summary>
    public TextSpan NameSpan { get; internal set; }
    /// <summary>The top-level fields, the entry field first.</summary>
    public ImmutableArray<TblFieldNode> Fields { get; internal set; } = [];
    /// <summary>From the entry field to the end of its last field (for folding).</summary>
    public TextSpan Span { get; internal set; }
    /// <summary>Position among the section's entries.</summary>
    public int Index { get; internal set; }

    public IEnumerable<TblFieldNode> AllFields() => Fields.SelectMany(f => f.DescendantsAndSelf());

    public override string ToString() => Name;
}

/// <summary>A section: <c>#Header</c> ... <c>#End</c>, or the fields outside any header.</summary>
public sealed class TblSection
{
    internal TblSection() { }

    /// <summary>The header without <c>#</c>, or empty for content outside any header.</summary>
    public string Name { get; internal set; } = string.Empty;
    /// <summary>The <c>#Header</c> token, or null.</summary>
    public TextSpan? HeaderSpan { get; internal set; }
    /// <summary>The <c>#End</c> token that closes the section, or null.</summary>
    public TextSpan? EndSpan { get; internal set; }
    /// <summary>True when another header started before this section's <c>#End</c>.</summary>
    public bool ClosedByNextHeader { get; internal set; }
    public bool HasHeader => HeaderSpan is not null;
    /// <summary>True when the game skips this section (a header it never searches for).</summary>
    public bool IsIgnored { get; internal set; }
    /// <summary>Headers inside the section that the game skips while searching for entries.</summary>
    public ImmutableArray<TextSpan> IgnoredHeaders { get; internal set; } = [];
    public TblSectionSchema? Schema { get; internal set; }
    /// <summary>The marker that starts each entry (from the schema, or inferred), or null.</summary>
    public string? EntryMarker { get; internal set; }
    /// <summary>Fields before the first entry (all fields when the section has no entries).</summary>
    public ImmutableArray<TblFieldNode> Fields { get; internal set; } = [];
    public ImmutableArray<TblEntry> Entries { get; internal set; } = [];
    /// <summary>Values not after any field marker (free text such as credits, or stray text).</summary>
    public ImmutableArray<TblValueNode> FreeValues { get; internal set; } = [];
    /// <summary>Header (or first content) through <c>#End</c> (or last content), for folding.</summary>
    public TextSpan Span { get; internal set; }
    public int Index { get; internal set; }

    /// <summary>Every field in the section, depth first.</summary>
    public IEnumerable<TblFieldNode> AllFields() =>
        Fields.SelectMany(f => f.DescendantsAndSelf()).Concat(Entries.SelectMany(e => e.AllFields()));

    public override string ToString() => HasHeader ? "#" + Name : "(no section)";
}

/// <summary>A structural problem the parser met (reported by the linter).</summary>
public enum TblSyntaxIssueKind
{
    UnterminatedString,
    UnterminatedComment,
    UnclosedBracket,
    StrayCloser,
    MissingEnd,
    StrayEnd,
    MarkerWithoutColon,
    /// <summary>Brackets nested deeper than <see cref="TblDocument.MaxNesting"/>; the deeper ones are read as plain words.</summary>
    NestedTooDeeply,
}

/// <summary>A structural problem with where it is.</summary>
public sealed record TblSyntaxIssue(TblSyntaxIssueKind Kind, TextSpan Span, string Detail);
