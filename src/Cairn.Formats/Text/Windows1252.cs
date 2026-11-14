namespace Cairn.Formats.Text;

/// <summary>
/// Decodes 8-bit text as Windows-1252, the code page Red Faction's text and most Western 8-bit text
/// files use. It is Latin-1 except for bytes 0x80 to 0x9F, which hold characters such as €, ‘ ’ and
/// “ ” instead of control codes. The five bytes Windows-1252 leaves undefined (0x81, 0x8D, 0x8F,
/// 0x90, 0x9D) decode as their Latin-1 code points, so every byte gives exactly one character and
/// nothing becomes a replacement character.
/// </summary>
public static class Windows1252
{
    /// <summary>What bytes 0x80 to 0x9F stand for.</summary>
    private const string High =
        "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F"
        + "\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    /// <summary>The name the previews show for text read this way.</summary>
    public const string DisplayName = "Windows-1252";

    /// <summary>Decodes <paramref name="bytes"/>, one character per byte.</summary>
    public static string GetString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            chars[i] = b is >= 0x80 and <= 0x9F ? High[b - 0x80] : (char)b;
        }
        return new string(chars);
    }
}
