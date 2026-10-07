namespace Cairn.Atx.Text;

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

    /// <summary>Extracts this span's text. Clamped to the bounds of <paramref name="text"/>.</summary>
    public string GetText(string text)
    {
        int start = Math.Clamp(Start, 0, text.Length);
        int end = Math.Clamp(End, start, text.Length);
        return text[start..end];
    }

    public override string ToString() => $"[{Start}..{End})";
}
