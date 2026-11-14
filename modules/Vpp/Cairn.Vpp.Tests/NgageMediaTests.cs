using Cairn.Formats.Imaging;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Tests;

/// <summary>
/// The packfile list's facts and Info column for the N-Gage release's media: PCX images (built here with
/// <see cref="SyntheticPcx"/>) and 8-bit text that is not valid UTF-8.
/// </summary>
public sealed class NgageMediaTests
{
    private static VppItem Item(string name, byte[] bytes) => new(name, new MemorySource(bytes), VppItemState.Added);

    private static byte[] Pcx(int width, int height) =>
        SyntheticPcx.Write(width, height, 8, 1, (x, y) => (x + y) % 256, [.. Enumerable.Range(0, 768).Select(i => (byte)i)]);

    [Fact]
    public void Pcx_FactsAndInfoLine()
    {
        var sheet = VppFacts.Describe(Item("hud_ammo.pcx", Pcx(64, 32)));
        Assert.Equal("PCX image", sheet["Type"]);
        Assert.Equal("Loaded by the game", sheet["Game"]);
        Assert.Equal("PCX", sheet["Format"]);
        Assert.Equal("64 x 32", sheet["Dimensions"]);
        Assert.Equal(EngineFormats.DisplayName(EngineFormat.Paletted8), sheet["Pixel format"]);
        Assert.Contains("version 5", sheet["Note"], StringComparison.Ordinal);

        byte[] hud = Pcx(64, 32);
        var line = VppInfo.Summarize("hud_ammo.pcx", () => new MemoryStream(hud), hud.Length, null);
        Assert.False(line.IsUnreadable, line.Detail);
        Assert.Equal("64x32, 8-bit paletted, RLE compressed", line.Text);

        var rgb = SyntheticPcx.Write(3, 3, 8, 3, (x, y) => x * y, options: new SyntheticPcxOptions { Rle = false });
        Assert.Equal("3x3, 24-bit, uncompressed", VppInfo.Summarize("rgb.pcx", () => new MemoryStream(rgb), rgb.Length, null).Text);
    }

    [Fact]
    public void Pcx_IsAGameTexture_ForTheChecks()
    {
        // loaded by the game (no VPP009), through the textures' 32-byte name slot, and expected in powers of two
        var package = new VppPackage(null, [Item("wall.pcx", Pcx(64, 64)), Item(new string('n', 28) + ".pcx", Pcx(8, 8))]);
        var problems = Validation.VppValidator.Validate(package);
        Assert.DoesNotContain(problems, p => p.Code == "VPP009");
        Assert.True(Validation.VppValidator.IsNameTooLongForGame(new string('n', 28) + ".pcx"));
        Assert.False(Validation.VppValidator.IsNameTooLongForGame(new string('n', 27) + ".pcx"));
        Assert.Contains(VppFacts.Describe(Item("hud.pcx", Pcx(9, 9))).Warnings, w => w.Contains("powers of two", StringComparison.Ordinal));
        Assert.Empty(VppFacts.Describe(Item("wall.pcx", Pcx(64, 64))).Warnings);
    }

    [Fact]
    public void Pcx_Damaged_IsUnreadable_NotAnException()
    {
        byte[] bad = Pcx(8, 8)[..64];
        Assert.True(VppInfo.Summarize("bad.pcx", () => new MemoryStream(bad), bad.Length, null).IsUnreadable);
        var sheet = VppFacts.Describe(Item("bad.pcx", bad));
        Assert.Null(sheet["Dimensions"]);
        Assert.Contains("too short", sheet["Error"], StringComparison.Ordinal);
    }

    [Fact]
    public void Latin1Text_IsWindows1252_NotReplacementCharacters()
    {
        // "En: ¡Ahí está!" and "Sp: canción" with é, í, ó and ¡ as single bytes (invalid UTF-8), and a 0x93/0x94 quote pair
        byte[] bytes = [.. "*1\r\nEn: ¡Ahí está!\r\nSp: canción “sí”\r\n".Select(c => c switch { '“' => (byte)0x93, '”' => (byte)0x94, _ => (byte)c })];
        string text = VppFacts.DecodeText(bytes, out string encoding);
        Assert.StartsWith("8-bit", encoding, StringComparison.Ordinal);
        Assert.Contains("¡Ahí está!", text, StringComparison.Ordinal);
        Assert.Contains("canción “sí”", text, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0xFFFD, text); // the replacement character

        var sheet = VppFacts.Describe(Item("D01.txt", bytes));
        Assert.Equal("3", sheet["Lines"]);
        Assert.Contains("En: ¡Ahí está!", sheet["First lines"], StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8Text_StaysUtf8()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("café €\n");
        Assert.Equal("café €\n", VppFacts.DecodeText(bytes, out string encoding));
        Assert.Equal("UTF-8", encoding);
        Assert.Equal("café", VppFacts.DecodeText([0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("café")], out encoding));
        Assert.Equal("UTF-8 with BOM", encoding);
    }
}
