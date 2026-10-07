using System.Numerics;
using Cairn.Atx.Linting;
using Cairn.Atx.Text;
using Tomlyn.Syntax;

namespace Cairn.Atx.Parsing;

/// <summary>
/// The handful of places where Tomlyn reads something the game's own TOML parser (toml++) refuses.
/// They are checked after a successful parse, because without them the workbench would show a
/// clean file that the game then will not load at all — the one kind of mistake this tool exists
/// to catch. Everything else the two parsers were compared on agreed.
/// </summary>
internal static class TomlStrictness
{
    /// <summary>The largest whole number TOML allows, written out for the messages.</summary>
    private const string MaxInteger = "9223372036854775807";

    private const string MinInteger = "-9223372036854775808";

    /// <summary>
    /// Adds an error for every value the game's parser would reject.
    /// </summary>
    /// <param name="doc">The parsed document.</param>
    /// <param name="text">The document text the spans index.</param>
    /// <param name="lines">Line lookups for <paramref name="text"/>.</param>
    /// <param name="spanOf">Converts a syntax node to a span into <paramref name="text"/>.</param>
    /// <param name="into">The diagnostic list to add to.</param>
    public static void Check(
        DocumentSyntax doc, string text, LineMap lines,
        Func<SyntaxNode, TextSpan> spanOf, List<Diagnostic> into)
    {
        Walk(doc);

        void Walk(SyntaxNode? node)
        {
            if (node is null) return;
            if (node is IntegerValueSyntax or StringValueSyntax)
            {
                var span = spanOf(node);
                string raw = span.GetText(text);
                if (node is IntegerValueSyntax) CheckInteger(raw, span, lines, into);
                else CheckString(raw, span, lines, into);
            }
            for (int i = 0; i < node.ChildrenCount; i++) Walk(node.GetChild(i));
        }
    }

    // ── Integers ──────────────────────────────────────────────────────────────

    private static void CheckInteger(string raw, TextSpan span, LineMap lines, List<Diagnostic> into)
    {
        if (!TryReadMagnitude(raw, out var value, out bool negative)) return;
        if (!negative && value <= long.MaxValue) return;
        if (negative && value <= -(BigInteger)long.MinValue) return;

        var (line, column) = lines.LineColumn(span.Start);
        into.Add(new Diagnostic(
            AtxRules.Syntax, DiagnosticSeverity.Error,
            $"Line {line}, column {column}: the number {UserText.Printable(raw)} is outside the range "
            + "of whole numbers TOML allows, so the game's TOML reader refuses the whole file.",
            $"Whole numbers have to be between {MinInteger} and {MaxInteger}. Frame times are in "
            + "milliseconds, so anything past a few thousand is already longer than a level lasts. "
            + "(This editor reads the number anyway, which is why the rest of the file still shows.)",
            span));
    }

    /// <summary>
    /// Reads the magnitude of a TOML integer token. Returns false for anything that is not a plain
    /// integer literal, which leaves it to Tomlyn — it has already accepted the token's shape.
    /// </summary>
    private static bool TryReadMagnitude(string raw, out BigInteger value, out bool negative)
    {
        value = BigInteger.Zero;
        negative = false;

        string s = raw.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        if (s.Length == 0) return false;
        if (s[0] == '+') s = s[1..];
        else if (s[0] == '-') { negative = true; s = s[1..]; }

        int radix = 10;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { radix = 16; s = s[2..]; }
        else if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) { radix = 8; s = s[2..]; }
        else if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) { radix = 2; s = s[2..]; }
        if (s.Length == 0) return false;

        foreach (char c in s)
        {
            int digit = DigitValue(c);
            if (digit < 0 || digit >= radix) return false;
            value = value * radix + digit;
        }
        return true;
    }

    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // ── Basic strings ─────────────────────────────────────────────────────────

    /// <summary>
    /// Escapes TOML 1.0 defines. Tomlyn also accepts the 1.1 draft's <c>\e</c> and <c>\xHH</c>,
    /// which the game's parser does not know.
    /// </summary>
    private const string KnownEscapes = "btnfr\"\\uU";

    private static void CheckString(string raw, TextSpan span, LineMap lines, List<Diagnostic> into)
    {
        // Only double-quoted strings carry escapes; 'literal' text is taken exactly as written.
        if (!raw.StartsWith('"')) return;
        bool multiline = raw.StartsWith("\"\"\"", StringComparison.Ordinal);
        int delimiter = multiline ? 3 : 1;
        int end = Math.Max(delimiter, raw.Length - delimiter);

        for (int i = delimiter; i < end; i++)
        {
            if (raw[i] != '\\') continue;
            if (i + 1 >= end) break;
            char next = raw[i + 1];
            if (KnownEscapes.Contains(next, StringComparison.Ordinal)) { i++; continue; }
            // A backslash at the end of a line inside a """ string swallows the line break and the
            // whitespace after it. That one is legal, and it is not followed by an escape letter.
            if (multiline && IsLineEndingBackslash(raw, i, end)) continue;

            int at = span.Start + i;
            var (line, column) = lines.LineColumn(at);
            string sequence = "\\" + next;
            into.Add(new Diagnostic(
                AtxRules.Syntax, DiagnosticSeverity.Error,
                $"Line {line}, column {column}: '{UserText.Printable(sequence)}' is not a backslash "
                + "code the game's TOML reader knows, so it refuses the whole file.",
                "Inside \"double quotes\" the only backslash codes allowed are \\b \\t \\n \\f \\r "
                + "\\\" \\\\ \\uXXXX and \\UXXXXXXXX. Write a backslash you want to keep as \\\\, or "
                + "put the text in 'single quotes', where it is taken exactly as you typed it.",
                new TextSpan(at, Math.Min(2, span.End - at))));
            return;
        }
    }

    private static bool IsLineEndingBackslash(string raw, int index, int end)
    {
        for (int i = index + 1; i < end; i++)
        {
            if (raw[i] is '\n') return true;
            if (raw[i] is ' ' or '\t' or '\r') continue;
            return false;
        }
        return false;
    }
}
