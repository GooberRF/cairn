using System.Globalization;
using System.Text;
using Cairn.Atx.Text;

namespace Cairn.Atx.Workspace;

/// <summary>How the bytes of an .atx file on disk were decoded.</summary>
public enum AtxFileEncoding
{
    /// <summary>UTF-8 with no byte-order mark — what the game's TOML parser expects.</summary>
    Utf8,

    /// <summary>UTF-8 with a byte-order mark.</summary>
    Utf8Bom,

    /// <summary>UTF-16 little-endian, byte-order mark present.</summary>
    Utf16Le,

    /// <summary>UTF-16 big-endian, byte-order mark present.</summary>
    Utf16Be,

    /// <summary>UTF-32 little-endian, byte-order mark present.</summary>
    Utf32Le,

    /// <summary>UTF-32 big-endian, byte-order mark present.</summary>
    Utf32Be,

    /// <summary>
    /// The system's ANSI code page: what a plain Notepad save produced before Windows 10 1903, and
    /// what most hand-written .atx files with accented filenames actually are.
    /// </summary>
    Ansi,
}

/// <summary>An .atx file read from disk, with the details a round-trip must preserve.</summary>
/// <param name="Path">Full path on disk.</param>
/// <param name="Text">The file's text, with any byte-order mark removed.</param>
/// <param name="LineEnding">The file's dominant line ending.</param>
/// <param name="HadByteOrderMark">True when the file on disk started with a UTF-8 BOM.</param>
/// <param name="EndsWithNewLine">True when the file ended with a line break.</param>
/// <param name="Encoding">How the bytes were decoded.</param>
public sealed record AtxTextFile(
    string Path, string Text, LineEndingKind LineEnding, bool HadByteOrderMark, bool EndsWithNewLine,
    AtxFileEncoding Encoding = AtxFileEncoding.Utf8)
{
    /// <summary>
    /// False when the file was not already UTF-8, so saving it will convert it. The app raises a
    /// notice bar for these: the game's toml++ only reads UTF-8, so the conversion is the repair.
    /// </summary>
    public bool IsUtf8 => Encoding is AtxFileEncoding.Utf8 or AtxFileEncoding.Utf8Bom;

    /// <summary>The encoding named the way the notice bar says it, e.g. "ANSI (Windows-1252)".</summary>
    public string EncodingName => AtxTextFiles.DescribeEncoding(Encoding);
}

/// <summary>
/// Reading and writing document text. Files are written as UTF-8 without a byte-order mark and
/// with the line endings they already had, and the write goes through the shared
/// <see cref="AtomicFile"/>, so it is atomic — a crash mid-save can never
/// leave a half-written .atx behind.
/// </summary>
public static class AtxTextFiles
{
    /// <summary>
    /// The largest .atx file this editor will open, and the same cap the game applies:
    /// <c>read_atx_text</c> in <c>game_patch/bmpman/atx.cpp</c> refuses anything bigger outright.
    /// There is nothing to be gained from spending a minute parsing a file the game will not load.
    /// </summary>
    public const long MaxFileBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Past this size an .atx takes long enough to read and lint that the work belongs off the UI
    /// thread, with an "Opening…" state, rather than freezing the window.
    /// </summary>
    public const long LargeFileBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8Strict = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static int _codePagesRegistered;

    /// <summary>
    /// Reads an .atx file and notes its encoding and line endings.
    ///
    /// Byte-order marks decide first (UTF-32's four bytes are tested before UTF-16's two, because
    /// a UTF-32 LE mark starts with the UTF-16 LE one). Without a mark the bytes are decoded as
    /// strict UTF-8, and only when that is not valid UTF-8 does the system ANSI code page take
    /// over — the two cannot both be right, and guessing UTF-8 for a Windows-1252 file is what
    /// turns "café.tga" into "caf?.tga" and then writes that back to disk.
    /// </summary>
    public static AtxTextFile Read(string path)
    {
        // Refused before reading, not after (FileTooLargeException): a 60 MB .atx is either a
        // mistake or something hostile, and either way the game would not load it.
        byte[] bytes = AtomicFile.ReadAllBytes(path, MaxFileBytes);
        var (text, encoding) = Decode(bytes);
        return new AtxTextFile(
            path, text, LineEndings.Detect(text), encoding == AtxFileEncoding.Utf8Bom,
            text.Length > 0 && text[^1] is '\n' or '\r', encoding);
    }

    /// <summary>Decodes file bytes the way <see cref="Read"/> does. Exposed for tests.</summary>
    /// <param name="bytes">The raw file content.</param>
    public static (string Text, AtxFileEncoding Encoding) Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: false)
                .GetString(bytes, 4, bytes.Length - 4), AtxFileEncoding.Utf32Le);
        if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF))
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: false)
                .GetString(bytes, 4, bytes.Length - 4), AtxFileEncoding.Utf32Be);
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
            return (Utf8NoBom.GetString(bytes, 3, bytes.Length - 3), AtxFileEncoding.Utf8Bom);
        if (StartsWith(bytes, 0xFF, 0xFE))
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false)
                .GetString(bytes, 2, bytes.Length - 2), AtxFileEncoding.Utf16Le);
        if (StartsWith(bytes, 0xFE, 0xFF))
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false)
                .GetString(bytes, 2, bytes.Length - 2), AtxFileEncoding.Utf16Be);

        try
        {
            return (Utf8Strict.GetString(bytes), AtxFileEncoding.Utf8);
        }
        catch (DecoderFallbackException)
        {
            return (AnsiEncoding().GetString(bytes), AtxFileEncoding.Ansi);
        }
    }

    /// <summary>The system ANSI code page, registering the code-page provider on first use.</summary>
    public static Encoding AnsiEncoding()
    {
        if (Interlocked.Exchange(ref _codePagesRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // A code page the provider does not know: Latin-1 is the closest thing to a safe
            // default, and unlike UTF-8 it maps every byte to a character rather than losing it.
            return Encoding.Latin1;
        }
    }

    /// <summary>How the notice bar names an encoding.</summary>
    /// <param name="encoding">The detected encoding.</param>
    public static string DescribeEncoding(AtxFileEncoding encoding) => encoding switch
    {
        AtxFileEncoding.Utf8 => "UTF-8",
        AtxFileEncoding.Utf8Bom => "UTF-8 with a byte-order mark",
        AtxFileEncoding.Utf16Le or AtxFileEncoding.Utf16Be => "UTF-16",
        AtxFileEncoding.Utf32Le or AtxFileEncoding.Utf32Be => "UTF-32",
        _ => $"ANSI ({CodePageName()})",
    };

    /// <summary>The ANSI code page written the way Windows itself writes it, e.g. "Windows-1252".</summary>
    private static string CodePageName()
    {
        string name = AnsiEncoding().WebName;
        return name.Length == 0
            ? "unknown"
            : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> as UTF-8 without a BOM, via a
    /// temporary file in the same folder (<see cref="AtomicFile"/>) so the replacement is atomic.
    /// </summary>
    /// <param name="path">Destination path.</param>
    /// <param name="text">The text to write, already using the desired line endings.</param>
    public static void WriteAllText(string path, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);

        AtomicFile.WriteAllBytes(path, Utf8NoBom.GetBytes(text));
    }

    /// <summary>
    /// Saves document text, converting line endings to <paramref name="lineEnding"/> first so a
    /// file that arrived with CRLF leaves with CRLF.
    /// </summary>
    public static void SaveDocument(string path, string text, LineEndingKind lineEnding) =>
        WriteAllText(path, LineEndings.Normalize(text, lineEnding));
}
