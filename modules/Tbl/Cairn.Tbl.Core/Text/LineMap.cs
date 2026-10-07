namespace Cairn.Tbl.Text;

/// <summary>The line endings a table file can use.</summary>
public enum LineEndingKind
{
    /// <summary>Windows, <c>\r\n</c>: what the stock tables use and the default for new tables.</summary>
    CrLf,
    /// <summary>Unix, <c>\n</c>.</summary>
    Lf,
    /// <summary>Classic Mac, <c>\r</c>.</summary>
    Cr,
}

/// <summary>Line-ending detection and conversion helpers.</summary>
public static class LineEndings
{
    /// <summary>The literal string for a line-ending kind.</summary>
    public static string ToText(this LineEndingKind kind) => kind switch
    {
        LineEndingKind.Lf => "\n",
        LineEndingKind.Cr => "\r",
        _ => "\r\n",
    };

    /// <summary>
    /// The dominant line ending of <paramref name="text"/>; text with no line break at all reports
    /// <see cref="LineEndingKind.CrLf"/>.
    /// </summary>
    public static LineEndingKind Detect(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (c == '\n') lf++;
        }
        if (crlf == 0 && lf == 0 && cr == 0) return LineEndingKind.CrLf;
        if (crlf >= lf && crlf >= cr) return LineEndingKind.CrLf;
        return lf >= cr ? LineEndingKind.Lf : LineEndingKind.Cr;
    }

    /// <summary>True when <paramref name="text"/> uses more than one kind of line break.</summary>
    public static bool IsMixed(string text)
    {
        bool crlf = false, lf = false, cr = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf = true; i++; }
                else cr = true;
            }
            else if (c == '\n') lf = true;
        }
        return (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1;
    }

    /// <summary>Rewrites every line break in <paramref name="text"/> to <paramref name="kind"/>.</summary>
    public static string Normalize(string text, LineEndingKind kind)
    {
        string eol = kind.ToText();
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(eol);
            }
            else if (c == '\n') sb.Append(eol);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>Maps character offsets to 1-based lines and columns and back for one immutable text.</summary>
public sealed class LineMap
{
    private readonly int[] _starts;
    private readonly int _length;

    public LineMap(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n') starts.Add(i + 1);
        }
        _starts = [.. starts];
        _length = text.Length;
    }

    /// <summary>Number of lines (an empty text has one).</summary>
    public int LineCount => _starts.Length;

    /// <summary>The 1-based line containing <paramref name="offset"/> (clamped to the text).</summary>
    public int LineOf(int offset)
    {
        offset = Math.Clamp(offset, 0, _length);
        int index = Array.BinarySearch(_starts, offset);
        if (index < 0) index = ~index - 1;
        return index + 1;
    }

    /// <summary>The 1-based line and column of <paramref name="offset"/>.</summary>
    public (int Line, int Column) PositionOf(int offset)
    {
        int line = LineOf(offset);
        return (line, Math.Clamp(offset, 0, _length) - _starts[line - 1] + 1);
    }

    /// <summary>The offset where 1-based <paramref name="line"/> starts (clamped).</summary>
    public int StartOf(int line) => _starts[Math.Clamp(line, 1, _starts.Length) - 1];

    /// <summary>The offset of 1-based <paramref name="line"/>/<paramref name="column"/> (clamped to the text).</summary>
    public int OffsetOf(int line, int column) => Math.Clamp(StartOf(line) + Math.Max(0, column - 1), 0, _length);
}
