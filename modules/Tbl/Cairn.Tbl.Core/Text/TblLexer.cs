namespace Cairn.Tbl.Text;

/// <summary>The lexical class of a table token. Every character of a table belongs to exactly one token.</summary>
public enum TblLexKind : byte
{
    /// <summary>Spaces and tabs (and any other non-line-break white space).</summary>
    Whitespace,
    /// <summary>One line break: <c>\r\n</c>, <c>\n</c> or <c>\r</c>.</summary>
    NewLine,
    /// <summary><c>//</c> to the end of the line.</summary>
    LineComment,
    /// <summary><c>/* ... */</c>, possibly over several lines (unterminated: to the end of the text).</summary>
    BlockComment,
    /// <summary><c>#Name</c> to the end of the line (before any trailing comment and white space).</summary>
    Header,
    /// <summary>A <c>$Name:</c> or <c>+Name:</c> field marker, colon included.</summary>
    FieldName,
    /// <summary>A bare word ending in a colon, such as the <c>En:</c> language keys.</summary>
    BareKey,
    /// <summary>A double-quoted string, quotes included (unterminated: to the end of the line).</summary>
    String,
    /// <summary>An integer or decimal number, optionally signed.</summary>
    Number,
    /// <summary><c>true</c>, <c>false</c>, <c>yes</c> or <c>no</c> (the game accepts all four).</summary>
    Boolean,
    /// <summary>Any other run of characters: bare words, free text, <c>XSTR</c>.</summary>
    Word,
    /// <summary><c>(</c></summary>
    OpenParen,
    /// <summary><c>)</c></summary>
    CloseParen,
    /// <summary><c>{</c></summary>
    OpenBrace,
    /// <summary><c>}</c></summary>
    CloseBrace,
    /// <summary><c>&lt;</c></summary>
    OpenAngle,
    /// <summary><c>&gt;</c></summary>
    CloseAngle,
    /// <summary><c>,</c></summary>
    Comma,
}

/// <summary>Extra facts about a token.</summary>
[Flags]
public enum TblLexFlags : byte
{
    None = 0,
    /// <summary>A string without its closing quote, or a block comment without <c>*/</c>.</summary>
    Unterminated = 1,
    /// <summary>A word starting with <c>$</c> or <c>+</c> that has no colon: probably a field name missing it.</summary>
    MarkerWithoutColon = 2,
}

/// <summary>One token: a kind and a range of the text.</summary>
public readonly record struct TblLexToken(TblLexKind Kind, int Start, int Length, TblLexFlags Flags = TblLexFlags.None)
{
    public int End => Start + Length;
    public TextSpan Span => new(Start, Length);

    /// <summary>True for white space, line breaks and comments.</summary>
    public bool IsTrivia => Kind is TblLexKind.Whitespace or TblLexKind.NewLine or TblLexKind.LineComment or TblLexKind.BlockComment;

    /// <summary>True for a comment.</summary>
    public bool IsComment => Kind is TblLexKind.LineComment or TblLexKind.BlockComment;

    public string GetText(string text) => Span.GetText(text);
}

/// <summary>
/// The lossless table lexer: concatenating the tokens' text gives back the input exactly. Follows the
/// game's parser: <c>//</c> comments run to the end of the line and <c>/* */</c> comments may span lines
/// (both only outside strings); <c>$Key:</c> and <c>+Key:</c> markers may contain spaces and
/// parentheses (<c>$Body Temperature(F):</c>) and end at the first colon on the line; a bare word ending
/// in a colon is a key (<c>En:</c>); <c>#Header</c> runs to the end of the line; a string ends at its
/// closing quote or the end of the line. Tolerant of any input; never throws.
/// </summary>
public static class TblLexer
{
    /// <summary>Tokenises <paramref name="text"/>.</summary>
    public static ImmutableArray<TblLexToken> Lex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = ImmutableArray.CreateBuilder<TblLexToken>(Math.Max(16, text.Length / 4));
        int n = text.Length, i = 0;
        // Where the last colon scan stopped and what it found: a later marker before that point stops there too.
        int colonScanStop = -1, colonScanResult = -1;
        while (i < n)
        {
            char c = text[i];
            int start = i;
            if (c == '\r' || c == '\n')
            {
                i += c == '\r' && i + 1 < n && text[i + 1] == '\n' ? 2 : 1;
                tokens.Add(new(TblLexKind.NewLine, start, i - start));
                continue;
            }
            if (IsSpace(c))
            {
                while (i < n && IsSpace(text[i])) i++;
                tokens.Add(new(TblLexKind.Whitespace, start, i - start));
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                i = LineEnd(text, i);
                tokens.Add(new(TblLexKind.LineComment, start, i - start));
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2;
                tokens.Add(new(TblLexKind.BlockComment, start, i - start, close < 0 ? TblLexFlags.Unterminated : TblLexFlags.None));
                continue;
            }
            if (c == '"')
            {
                i++;
                // The game ends a string only at its quote; a CR (or the end of the file) first is the fatal
                // case. A bare LF is an ordinary character inside it.
                while (i < n && text[i] is not ('"' or '\r')) i++;
                bool closed = i < n && text[i] == '"';
                if (closed) i++;
                tokens.Add(new(TblLexKind.String, start, i - start, closed ? TblLexFlags.None : TblLexFlags.Unterminated));
                continue;
            }
            if (c == '#')
            {
                int end = HeaderEnd(text, i);
                i = end;
                tokens.Add(new(TblLexKind.Header, start, i - start));
                continue;
            }
            if (c is '$' or '+' && !(c == '+' && i + 1 < n && (char.IsAsciiDigit(text[i + 1]) || text[i + 1] == '.')))
            {
                int colon = KeyColon(text, i + 1, ref colonScanStop, ref colonScanResult);
                if (colon > i + 1)
                {
                    i = colon + 1;
                    tokens.Add(new(TblLexKind.FieldName, start, i - start));
                    continue;
                }
            }
            TblLexKind single = c switch
            {
                '(' => TblLexKind.OpenParen,
                ')' => TblLexKind.CloseParen,
                '{' => TblLexKind.OpenBrace,
                '}' => TblLexKind.CloseBrace,
                '<' => TblLexKind.OpenAngle,
                '>' => TblLexKind.CloseAngle,
                ',' => TblLexKind.Comma,
                _ => TblLexKind.Word,
            };
            if (single != TblLexKind.Word)
            {
                i++;
                tokens.Add(new(single, start, 1));
                continue;
            }

            while (i < n && !IsWordBreak(text, i)) i++;
            if (i == start) i++; // cannot happen, but never loop forever
            tokens.Add(ClassifyWord(text, start, i));
        }
        return tokens.ToImmutable();
    }

    /// <summary>True for a character the game treats as white space, other than a line break: space, tab, VT and
    /// FF only (its white is space and 0x09-0x0D). A non-breaking space or other Unicode white space is text.</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\v' or '\f';

    /// <summary>True for the game's white space, line breaks included.</summary>
    public static bool IsEngineWhite(char c) => c is ' ' or (>= '\t' and <= '\r');

    private static TblLexToken ClassifyWord(string text, int start, int end)
    {
        int length = end - start;
        var span = text.AsSpan(start, length);
        if (length > 1 && span[^1] == ':' && span[0] is not ('$' or '+'))
            return new(TblLexKind.BareKey, start, length);
        if (IsNumber(span)) return new(TblLexKind.Number, start, length);
        if (span.Equals("true", StringComparison.OrdinalIgnoreCase) || span.Equals("false", StringComparison.OrdinalIgnoreCase)
            || span.Equals("yes", StringComparison.OrdinalIgnoreCase) || span.Equals("no", StringComparison.OrdinalIgnoreCase))
            return new(TblLexKind.Boolean, start, length);
        var flags = span[0] is '$' or '+' && length > 1 ? TblLexFlags.MarkerWithoutColon : TblLexFlags.None;
        return new(TblLexKind.Word, start, length, flags);
    }

    /// <summary>
    /// True for <c>[+-]digits[.digits][e[+-]digits]</c> (or a leading/trailing dot) and <c>0x</c> hex. Wider than
    /// the game accepts (no <c>+</c>, no exponent): the linter reports those rather than the lexer hiding them.
    /// </summary>
    public static bool IsNumber(ReadOnlySpan<char> s)
    {
        int i = 0;
        if (s.Length > 0 && s[0] is '+' or '-') i++;
        if (s.Length - i > 2 && s[i] == '0' && s[i + 1] is 'x' or 'X')
        {
            for (int h = i + 2; h < s.Length; h++) if (!char.IsAsciiHexDigit(s[h])) return false;
            return true;
        }
        int digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        }
        if (digits == 0) return false;
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            int save = i++;
            if (i < s.Length && s[i] is '+' or '-') i++;
            int exp = 0;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; exp++; }
            if (exp == 0) i = save;
        }
        return i == s.Length;
    }

    private static bool IsWordBreak(string text, int i)
    {
        char c = text[i];
        if (IsEngineWhite(c)) return true;
        if (c is '"' or '(' or ')' or '{' or '}' or '<' or '>' or ',') return true;
        return c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*';
    }

    private static int LineEnd(string text, int i)
    {
        while (i < text.Length && text[i] is not ('\n' or '\r')) i++;
        return i;
    }

    // A header runs to the end of the line, stopping before a comment and before trailing white space.
    private static int HeaderEnd(string text, int i)
    {
        int end = i + 1;
        int lastSolid = i + 1;
        while (end < text.Length && text[end] is not ('\n' or '\r'))
        {
            if (text[end] == '/' && end + 1 < text.Length && text[end + 1] is '/' or '*') break;
            if (!IsSpace(text[end])) lastSolid = end + 1;
            end++;
        }
        return lastSolid;
    }

    // The first colon from i on the same line (before a quote or comment), or -1. The scan stops at the same
    // character for every start up to that character, so the result is reused (linear on "$ $ $ ..." lines).
    private static int KeyColon(string text, int i, ref int lastStop, ref int lastResult)
    {
        if (i <= lastStop) return lastResult;
        int result = -1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == ':') { result = i; break; }
            if (c is '\n' or '\r' or '"') break;
            if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*') break;
            i++;
        }
        lastStop = i;
        lastResult = result;
        return result;
    }
}
