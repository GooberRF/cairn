namespace Cairn.Atx.Tests;

/// <summary>The shared 8-bit text decoding the text previews fall back to when bytes are not UTF-8.</summary>
public sealed class Windows1252Tests
{
    [Fact]
    public void Latin1Bytes_DecodeToTheirCharacters()
    {
        // 8-bit text as the N-Gage dialogue files hold it: one byte per character, é ñ ¿ ó invalid as UTF-8
        byte[] bytes = [0x44, 0xE9, 0x20, 0x75, 0x6E, 0x61, 0x2C, 0x20, 0x73, 0x65, 0xF1, 0x6F, 0x72, 0x3B, 0x20, 0xBF, 0x64, 0xF3, 0x6E, 0x64, 0x65, 0x3F];
        Assert.Equal("Dé una, señor; ¿dónde?", Windows1252.GetString(bytes));
    }

    [Fact]
    public void Bytes80To9F_AreWindows1252_NotControlCodes()
    {
        Assert.Equal("€‘’“”–—…™Ÿ", Windows1252.GetString([0x80, 0x91, 0x92, 0x93, 0x94, 0x96, 0x97, 0x85, 0x99, 0x9F]));
        // the five bytes Windows-1252 leaves undefined keep their Latin-1 code points
        Assert.Equal("\u0081\u008D\u008F\u0090\u009D", Windows1252.GetString([0x81, 0x8D, 0x8F, 0x90, 0x9D]));
    }

    [Fact]
    public void EveryByte_GivesOneCharacter_NeverAReplacement()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        string text = Windows1252.GetString(all);
        Assert.Equal(256, text.Length);
        Assert.DoesNotContain((char)0xFFFD, text); // the replacement character
        Assert.Equal(string.Empty, Windows1252.GetString([]));
        // every byte outside 0x80-0x9F is its Latin-1 character
        Assert.All(all.Where(b => b is < 0x80 or > 0x9F), b => Assert.Equal((char)b, text[b]));
    }
}
