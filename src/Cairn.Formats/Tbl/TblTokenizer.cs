using System.Text;

namespace Cairn.Formats.Tbl;

/// <summary>What a table token is.</summary>
public enum TblTokenKind
{
    /// <summary><c>#Name</c> to the end of the line, e.g. <c>#Entity Classes</c>, <c>#End</c>.</summary>
    Section,
    /// <summary>A key ending in a colon: <c>$Name:</c>, <c>+State:</c>, or a bare <c>En:</c>.</summary>
    Key,
    /// <summary>A double-quoted string (quotes removed).</summary>
    String,
    /// <summary>Any other run of non-space characters: numbers, bare words.</summary>
    Word,
    /// <summary>One of <c>( ) { } &lt; &gt; ,</c>.</summary>
    Symbol,
}

/// <summary>One token of an RF table.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Text">
/// The section name without '#', the key name without its prefix and colon, a string's contents, or
/// the word or symbol itself.
/// </param>
/// <param name="Prefix">For a key: '$', '+' or '\0' (a bare key such as <c>En:</c>). Otherwise '\0'.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Column">1-based column of the token's first character.</param>
public readonly record struct TblToken(TblTokenKind Kind, string Text, char Prefix, int Line, int Column)
{
    /// <summary>True for a key with this prefix and name (name compared ignoring case).</summary>
    public bool IsKey(char prefix, string name) =>
        Kind == TblTokenKind.Key && Prefix == prefix && string.Equals(Text, name, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Kind switch
    {
        TblTokenKind.Section => "#" + Text,
        TblTokenKind.Key => (Prefix == '\0' ? "" : Prefix.ToString()) + Text + ":",
        TblTokenKind.String => "\"" + Text + "\"",
        _ => Text,
    };
}

/// <summary>
/// Splits RF's <c>.tbl</c> text into tokens the way the game's parser sees it: <c>//</c> comments
/// run to the end of the line (outside strings), <c>$Key:</c> and <c>+Key:</c> names may contain
/// spaces and parentheses (<c>$Body Temperature(F):</c>, <c>$Corona (Glare) 1:</c>), a bare word
/// followed by a colon is a key too (the <c>En:</c> lines under <c>$ScreenName:</c>), and
/// <c>#Section</c> runs to the end of the line. Never throws: an unterminated string ends at the line
/// break.
/// </summary>
public static class TblTokenizer
{
    /// <summary>Tokenises table text.</summary>
    public static IReadOnlyList<TblToken> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<TblToken>();
        int i = 0, line = 1, lineStart = 0;
        int n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '\n')
            {
                line++;
                lineStart = ++i;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            int column = i - lineStart + 1;
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            if (c == '"')
            {
                int start = ++i;
                while (i < n && text[i] != '"' && text[i] != '\n') i++;
                tokens.Add(new TblToken(TblTokenKind.String, text[start..i], '\0', line, column));
                if (i < n && text[i] == '"') i++;
                continue;
            }
            if (c == '#')
            {
                int start = ++i;
                int end = LineEnd(text, i);
                tokens.Add(new TblToken(TblTokenKind.Section, text[start..end].Trim(), '\0', line, column));
                i = end;
                continue;
            }
            if (c is '$' or '+')
            {
                // A key runs to its colon, which must come before the end of the line, a quote or a
                // comment. "+" followed by a digit is a number, not a key.
                int colon = KeyColon(text, i + 1);
                if (colon > i + 1 && !(c == '+' && char.IsDigit(text[i + 1])))
                {
                    tokens.Add(new TblToken(TblTokenKind.Key, text[(i + 1)..colon].Trim(), c, line, column));
                    i = colon + 1;
                    continue;
                }
            }
            if (c is '(' or ')' or '{' or '}' or '<' or '>' or ',')
            {
                tokens.Add(new TblToken(TblTokenKind.Symbol, c.ToString(), '\0', line, column));
                i++;
                continue;
            }

            int wordStart = i;
            while (i < n && !char.IsWhiteSpace(text[i]) && text[i] is not ('"' or '(' or ')' or '{' or '}' or '<' or '>' or ',')
                && !(text[i] == '/' && i + 1 < n && text[i + 1] == '/'))
            {
                i++;
            }
            string word = text[wordStart..i];
            if (word.Length > 1 && word[^1] == ':')
                tokens.Add(new TblToken(TblTokenKind.Key, word[..^1], '\0', line, column));
            else
                tokens.Add(new TblToken(TblTokenKind.Word, word, '\0', line, column));
        }
        return tokens;
    }

    /// <summary>
    /// Reads a table file. Tables are 8-bit ANSI text; Latin-1 maps every byte to one character, so
    /// names survive exactly whatever the code page was.
    /// </summary>
    public static string ReadText(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    /// <summary>Decodes table bytes (for example a VPP entry) the same way as <see cref="ReadText"/>.</summary>
    public static string DecodeText(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static int LineEnd(string text, int i)
    {
        while (i < text.Length && text[i] is not ('\n' or '\r')) i++;
        return i;
    }

    private static int KeyColon(string text, int i)
    {
        while (i < text.Length)
        {
            char c = text[i];
            if (c == ':') return i;
            if (c is '\n' or '\r' or '"') return -1;
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') return -1;
            i++;
        }
        return -1;
    }
}
