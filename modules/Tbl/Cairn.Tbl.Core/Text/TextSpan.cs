namespace Cairn.Tbl.Text;

/// <summary>A half-open character range <c>[Start, Start + Length)</c> into a document's text.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>One past the last character in the span.</summary>
    public int End => Start + Length;

    /// <summary>True when the span covers no characters (a caret position).</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Creates a span from a half-open <paramref name="start"/>/<paramref name="end"/> pair.</summary>
    public static TextSpan FromBounds(int start, int end) => new(start, Math.Max(0, end - start));

    /// <summary>An empty span at <paramref name="offset"/>.</summary>
    public static TextSpan At(int offset) => new(offset, 0);

    public bool Contains(int offset) => offset >= Start && offset < End;

    /// <summary>True when <paramref name="offset"/> is inside the span or exactly at its end.</summary>
    public bool ContainsInclusive(int offset) => offset >= Start && offset <= End;

    public bool OverlapsWith(TextSpan other) => Start < other.End && other.Start < End;

    /// <summary>The smallest span covering both.</summary>
    public TextSpan Union(TextSpan other) => FromBounds(Math.Min(Start, other.Start), Math.Max(End, other.End));

    /// <summary>Extracts this span's text. Clamped to the bounds of <paramref name="text"/>.</summary>
    public string GetText(string text)
    {
        int start = Math.Clamp(Start, 0, text.Length);
        int end = Math.Clamp(End, start, text.Length);
        return text[start..end];
    }

    public override string ToString() => $"[{Start}..{End})";
}

/// <summary>A single replacement of <see cref="Span"/> with <see cref="NewText"/>.</summary>
public readonly record struct TextEdit(TextSpan Span, string NewText)
{
    /// <summary>An insertion of <paramref name="text"/> at <paramref name="offset"/>.</summary>
    public static TextEdit Insert(int offset, string text) => new(TextSpan.At(offset), text);

    /// <summary>A deletion of <paramref name="span"/>.</summary>
    public static TextEdit Delete(TextSpan span) => new(span, string.Empty);

    /// <summary>A replacement of <paramref name="span"/> with <paramref name="text"/>.</summary>
    public static TextEdit Replace(TextSpan span, string text) => new(span, text);

    /// <summary>
    /// Applies non-overlapping edits to <paramref name="text"/> (any order; applied from the end so
    /// earlier offsets stay valid). Overlapping edits after the first are skipped.
    /// </summary>
    public static string ApplyAll(string text, IEnumerable<TextEdit> edits)
    {
        var ordered = edits.OrderByDescending(e => e.Span.Start).ThenByDescending(e => e.Span.End).ToList();
        var sb = new System.Text.StringBuilder(text);
        int limit = int.MaxValue;
        foreach (var e in ordered)
        {
            int start = Math.Clamp(e.Span.Start, 0, text.Length);
            int end = Math.Clamp(e.Span.End, start, text.Length);
            if (end > limit) continue;
            sb.Remove(start, end - start).Insert(start, e.NewText);
            limit = start;
        }
        return sb.ToString();
    }

    public override string ToString() => $"{Span} => \"{NewText}\"";
}
