using System.Text;
using Cairn.Workspace;

namespace Cairn.Tbl.Text;

/// <summary>How the bytes of a table were decoded; saving writes the same encoding back.</summary>
public enum TblFileEncoding
{
    /// <summary>8-bit Windows-1252, the game's own: every stock table. Pure ASCII files are reported as this.</summary>
    Ansi,
    /// <summary>8-bit Latin-1: bytes Windows-1252 leaves undefined were present, so each byte maps to one character.</summary>
    Latin1,
    /// <summary>UTF-8 without a byte-order mark (non-ASCII text that is valid UTF-8).</summary>
    Utf8,
    /// <summary>UTF-8 with a byte-order mark.</summary>
    Utf8Bom,
    /// <summary>A UTF-8 byte-order mark followed by bytes that are not valid UTF-8 (an 8-bit file a tool marked as
    /// UTF-8): the rest is read as Latin-1, one character per byte, so every byte and the mark are written back.</summary>
    Latin1Bom,
}

/// <summary>A table's text with the details a round trip must preserve.</summary>
/// <param name="Text">The text, without any byte-order mark.</param>
/// <param name="Encoding">How the bytes were decoded.</param>
/// <param name="LineEnding">The dominant line ending.</param>
/// <param name="HasMixedLineEndings">True when more than one kind of line break is present (they are kept as they are).</param>
public sealed record TblTextFile(string Text, TblFileEncoding Encoding, LineEndingKind LineEnding, bool HasMixedLineEndings)
{
    /// <summary>The encoding as the status bar names it.</summary>
    public string EncodingName => TblTextFiles.Describe(Encoding);
}

/// <summary>Decoding and encoding table text without losing a byte.</summary>
public static class TblTextFiles
{
    /// <summary>Larger tables are refused: the biggest stock table is under 400 KB.</summary>
    public const long MaxFileBytes = 32 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static Encoding? _ansi;

    /// <summary>Windows-1252 with exception fallbacks (so a lossy conversion is detected rather than hidden).</summary>
    public static Encoding Windows1252
    {
        get
        {
            if (_ansi is null)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _ansi = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            return _ansi;
        }
    }

    /// <summary>Reads a table file from disk.</summary>
    public static TblTextFile Read(string path) => Decode(AtomicFile.ReadAllBytes(path, MaxFileBytes));

    /// <summary>
    /// Decodes table bytes. A UTF-8 byte-order mark decides first; otherwise pure ASCII is ANSI, non-ASCII
    /// that is valid UTF-8 is UTF-8, and anything else is Windows-1252 (or Latin-1 when the bytes use code
    /// points Windows-1252 leaves undefined). Never throws.
    /// </summary>
    public static TblTextFile Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string text;
        TblFileEncoding encoding;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            try
            {
                text = Utf8Strict.GetString(bytes, 3, bytes.Length - 3);
                encoding = TblFileEncoding.Utf8Bom;
            }
            catch (DecoderFallbackException)
            {
                // Decoding leniently would turn each invalid byte into U+FFFD and save it as EF BF BD.
                text = Encoding.Latin1.GetString(bytes, 3, bytes.Length - 3);
                encoding = TblFileEncoding.Latin1Bom;
            }
        }
        else if (Array.TrueForAll(bytes, b => b < 0x80))
        {
            text = Encoding.ASCII.GetString(bytes);
            encoding = TblFileEncoding.Ansi;
        }
        else
        {
            try
            {
                text = Utf8Strict.GetString(bytes);
                encoding = TblFileEncoding.Utf8;
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    text = Windows1252.GetString(bytes);
                    encoding = TblFileEncoding.Ansi;
                }
                catch (DecoderFallbackException)
                {
                    text = Encoding.Latin1.GetString(bytes);
                    encoding = TblFileEncoding.Latin1;
                }
            }
        }
        return new TblTextFile(text, encoding, LineEndings.Detect(text), LineEndings.IsMixed(text));
    }

    /// <summary>Encodes text for saving in <paramref name="encoding"/>; characters it cannot hold become '?'.</summary>
    public static byte[] Encode(string text, TblFileEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        switch (encoding)
        {
            case TblFileEncoding.Utf8:
                return Utf8NoBom.GetBytes(text);
            case TblFileEncoding.Utf8Bom:
                return [0xEF, 0xBB, 0xBF, .. Utf8NoBom.GetBytes(text)];
            case TblFileEncoding.Latin1:
                return Encoding.Latin1.GetBytes(text);
            case TblFileEncoding.Latin1Bom:
                return [0xEF, 0xBB, 0xBF, .. Encoding.Latin1.GetBytes(text)];
            default:
                try { return Windows1252.GetBytes(text); }
                catch (EncoderFallbackException)
                {
                    var lossy = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
                    return lossy.GetBytes(text);
                }
        }
    }

    /// <summary>The characters of <paramref name="text"/> that <paramref name="encoding"/> cannot store (distinct, in order).</summary>
    public static IReadOnlyList<char> Unencodable(string text, TblFileEncoding encoding)
    {
        if (encoding is TblFileEncoding.Utf8 or TblFileEncoding.Utf8Bom) return [];
        var result = new List<char>();
        foreach (char c in text)
        {
            if (c < 0x80 || result.Contains(c)) continue;
            bool ok = encoding is TblFileEncoding.Latin1 or TblFileEncoding.Latin1Bom ? c <= 0xFF : CanAnsi(c);
            if (!ok) result.Add(c);
        }
        return result;
    }

    /// <summary>Saves text atomically in the given encoding (line endings are written exactly as in the text).</summary>
    public static void Write(string path, string text, TblFileEncoding encoding) =>
        AtomicFile.WriteAllBytes(path, Encode(text, encoding));

    /// <summary>How the status bar names an encoding.</summary>
    public static string Describe(TblFileEncoding encoding) => encoding switch
    {
        TblFileEncoding.Utf8 => "UTF-8",
        TblFileEncoding.Utf8Bom => "UTF-8 with a byte-order mark",
        TblFileEncoding.Latin1 => "Latin-1",
        TblFileEncoding.Latin1Bom => "Latin-1 after a UTF-8 byte-order mark (the bytes are not valid UTF-8; kept exactly)",
        _ => "ANSI (Windows-1252)",
    };

    private static bool CanAnsi(char c)
    {
        try { Windows1252.GetByteCount([c]); return true; }
        catch (EncoderFallbackException) { return false; }
    }
}
