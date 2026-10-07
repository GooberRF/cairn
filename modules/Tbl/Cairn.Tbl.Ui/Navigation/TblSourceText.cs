using Cairn.Tbl.Index;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Ui.Navigation;

/// <summary>Reads the text of an indexed table (open document, loose file or packfile entry) for snippets and comparisons.</summary>
public static class TblSourceText
{
    /// <summary>
    /// The text of <paramref name="source"/>: <paramref name="openText"/> when it gives one (an open document's current
    /// text, by source key), else the file or packfile entry; null when it cannot be read. Any thread.
    /// </summary>
    public static string? Read(TblSource source, Func<string, string?>? openText = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (openText?.Invoke(source.Key) is { } text) return text;
        try
        {
            if (source.Location is { } location) return TblTextFiles.Decode(location.ReadAllBytes()).Text;
            if (File.Exists(source.Key)) return TblTextFiles.Read(source.Key).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Cairn.Formats.AssetFormatException)
        {
        }
        return null;
    }

    /// <summary>
    /// The lines around <paramref name="span"/> in <paramref name="text"/>: from the start of its line through
    /// <paramref name="maxLines"/> lines, clipped at <paramref name="end"/> when given (the entry's end).
    /// Returns the snippet and the offset of its first character in <paramref name="text"/>.
    /// </summary>
    public static (string Snippet, int Offset) Snippet(string text, TextSpan span, int maxLines = 40, int end = -1)
    {
        ArgumentNullException.ThrowIfNull(text);
        int start = Math.Clamp(span.Start, 0, text.Length);
        while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r') start--;
        int limit = end > start ? Math.Min(end, text.Length) : text.Length;
        int pos = start, lines = 0;
        while (pos < limit && lines < maxLines)
        {
            int nl = text.IndexOf('\n', pos, limit - pos);
            if (nl < 0) { pos = limit; break; }
            pos = nl + 1;
            lines++;
        }
        return (text[start..pos].TrimEnd('\r', '\n', ' ', '\t'), start);
    }
}
