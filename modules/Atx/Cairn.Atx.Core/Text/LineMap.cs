namespace Cairn.Atx.Text;

/// <summary>The line endings an .atx file can use.</summary>
public enum LineEndingKind
{
    /// <summary>Windows, <c>\r\n</c>. The default for files ATX Workbench creates.</summary>
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
    /// Detects the dominant line ending of <paramref name="text"/>. Files with no line break at
    /// all (and empty files) report <see cref="LineEndingKind.CrLf"/>, matching what the app uses
    /// for new documents.
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

/// <summary>Maps character offsets to line numbers and back for one immutable text snapshot.</summary>
public sealed class LineMap
{
    private readonly string _text;
    private readonly int[] _lineStarts;
    private bool[]? _valueInterior;

    public LineMap(string text)
    {
        _text = text;
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                starts.Add(i + 1);
            }
        }
        // A trailing newline produces a final empty line; keep it, it is a valid caret position.
        _lineStarts = [.. starts];
    }

    /// <summary>The text this map describes.</summary>
    public string Text => _text;

    /// <summary>Number of lines (a trailing newline yields a final empty line).</summary>
    public int LineCount => _lineStarts.Length;

    /// <summary>Offset of the first character of <paramref name="line"/> (0-based).</summary>
    public int LineStart(int line) => _lineStarts[Math.Clamp(line, 0, _lineStarts.Length - 1)];

    /// <summary>Offset just past the end of <paramref name="line"/>, including its line break.</summary>
    public int LineEndWithBreak(int line)
    {
        line = Math.Clamp(line, 0, _lineStarts.Length - 1);
        return line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;
    }

    /// <summary>Offset just past the end of <paramref name="line"/>, excluding its line break.</summary>
    public int LineEnd(int line)
    {
        int end = LineEndWithBreak(line);
        if (end > LineStart(line) && _text[end - 1] == '\n') end--;
        if (end > LineStart(line) && _text[end - 1] == '\r') end--;
        return end;
    }

    /// <summary>The 0-based line containing <paramref name="offset"/>.</summary>
    public int LineOf(int offset)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        int lo = 0, hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>1-based (line, column) for <paramref name="offset"/>, for user-facing messages.</summary>
    public (int Line, int Column) LineColumn(int offset)
    {
        int line = LineOf(offset);
        return (line + 1, offset - _lineStarts[line] + 1);
    }

    /// <summary>The text of <paramref name="line"/> without its line break.</summary>
    public string GetLine(int line) => _text[LineStart(line)..LineEnd(line)];

    /// <summary>Span of <paramref name="line"/> including its line break.</summary>
    public TextSpan LineSpanWithBreak(int line) =>
        TextSpan.FromBounds(LineStart(line), LineEndWithBreak(line));

    /// <summary>
    /// Records which lines fall inside a value written across several lines — a <c>"""…"""</c>
    /// string, or a bracketed value split over more than one line. Their text belongs to that
    /// value, so a <c>#</c> there is not a comment and an empty-looking line there is not a blank
    /// line. The parser sets this once, straight after parsing; until then every line counts as
    /// ordinary structure.
    /// </summary>
    /// <param name="interior">One flag per line, true for lines inside a multi-line value.</param>
    internal void MarkValueInterior(bool[] interior) => _valueInterior = interior;

    /// <summary>
    /// True when the line's text is the continuation of a value rather than a line of the
    /// document's own structure. Both <see cref="IsBlank"/> and <see cref="IsComment"/> answer
    /// false for such a line, because what looks like a blank line or a comment there is really
    /// just characters inside a string.
    /// </summary>
    public bool IsInsideValue(int line) =>
        _valueInterior is { } mask && (uint)line < (uint)mask.Length && mask[line];

    /// <summary>
    /// The text of <paramref name="line"/> as the document means it — the same as
    /// <see cref="GetLine"/>, except that a byte-order mark at the very start of the file is left
    /// out. The mark belongs to the encoding, not to the first line's content, and a file that
    /// opens with one must still read as a file that opens with a comment.
    /// </summary>
    public string GetContentLine(int line)
    {
        string text = GetLine(line);
        return line == 0 && text.StartsWith('﻿') ? text[1..] : text;
    }

    /// <summary>True if the line holds only whitespace.</summary>
    public bool IsBlank(int line) => !IsInsideValue(line) && GetContentLine(line).Trim().Length == 0;

    /// <summary>True if the first non-whitespace character on the line is <c>#</c>.</summary>
    public bool IsComment(int line) =>
        !IsInsideValue(line) && GetContentLine(line).TrimStart().StartsWith('#');
}
