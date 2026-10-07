using Cairn.Tbl.Text;

namespace Cairn.Tbl.Model;

/// <summary>Highlighting classes.</summary>
public enum TblTextClass
{
    /// <summary>Free text and bare words.</summary>
    Text,
    Comment,
    /// <summary><c>#Header</c> and <c>#End</c>.</summary>
    SectionHeader,
    /// <summary><c>$Name:</c>, <c>+Name:</c> and bare keys such as <c>En:</c>.</summary>
    FieldName,
    String,
    Number,
    /// <summary><c>true</c>/<c>false</c> and call names such as <c>XSTR</c>.</summary>
    Keyword,
    /// <summary>Brackets and commas.</summary>
    Brace,
    /// <summary>A file name (inside the quotes): underlined and clickable.</summary>
    FileName,
    /// <summary>A name that refers to another table's entry (inside the quotes).</summary>
    RefName,
    /// <summary>The name an entry defines (inside the quotes).</summary>
    EntryName,
}

/// <summary>A highlighted range.</summary>
public readonly record struct TblClassifiedSpan(TextSpan Span, TblTextClass Class);

/// <summary>Turns a parsed document into highlighting spans.</summary>
public static class TblClassifier
{
    /// <summary>
    /// Classified spans in text order, not overlapping; white space and line breaks are left out. File
    /// names, references and entry names cover a string's content; its quotes stay <see cref="TblTextClass.String"/>.
    /// </summary>
    public static ImmutableArray<TblClassifiedSpan> Classify(TblDocument doc)
    {
        var overrides = new Dictionary<int, (TextSpan Span, TblTextClass Class)>();
        foreach (var r in TblValueRoles.All(doc))
            overrides[r.Span.Start] = (r.Span, r.Role == TblValueRole.File ? TblTextClass.FileName : TblTextClass.RefName);
        foreach (var e in doc.Entries)
            if (e.EntryField is not null && e.NameSpan != e.EntryField.MarkerSpan) overrides[e.NameSpan.Start] = (e.NameSpan, TblTextClass.EntryName);
        var callNames = new HashSet<int>(doc.AllFields.SelectMany(f => f.Values.SelectMany(v => v.DescendantsAndSelf()))
            .Where(v => v.Kind == TblValueKind.Call).Select(v => v.Span.Start));

        var result = ImmutableArray.CreateBuilder<TblClassifiedSpan>(doc.Tokens.Length / 2);
        foreach (var t in doc.Tokens)
        {
            TblTextClass cls;
            switch (t.Kind)
            {
                case TblLexKind.Whitespace or TblLexKind.NewLine:
                    continue;
                case TblLexKind.LineComment or TblLexKind.BlockComment: cls = TblTextClass.Comment; break;
                case TblLexKind.Header: cls = TblTextClass.SectionHeader; break;
                case TblLexKind.FieldName or TblLexKind.BareKey: cls = TblTextClass.FieldName; break;
                case TblLexKind.Number: cls = TblTextClass.Number; break;
                case TblLexKind.Boolean: cls = TblTextClass.Keyword; break;
                case TblLexKind.Word: cls = callNames.Contains(t.Start) ? TblTextClass.Keyword : TblTextClass.Text; break;
                case TblLexKind.String:
                {
                    int contentStart = t.Start + 1;
                    if (overrides.TryGetValue(contentStart, out var o) && o.Span.End <= t.End)
                    {
                        result.Add(new(new TextSpan(t.Start, 1), TblTextClass.String));
                        if (o.Span.Length > 0) result.Add(new(o.Span, o.Class));
                        if (t.End > o.Span.End) result.Add(new(TextSpan.FromBounds(o.Span.End, t.End), TblTextClass.String));
                        continue;
                    }
                    cls = TblTextClass.String;
                    break;
                }
                default: cls = TblTextClass.Brace; break;
            }
            result.Add(new(t.Span, cls));
        }
        return result.ToImmutable();
    }
}
