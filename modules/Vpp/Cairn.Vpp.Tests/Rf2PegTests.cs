using System.Buffers.Binary;
using Cairn.Formats;
using Cairn.Formats.Imaging;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Ps2;
using Cairn.Vpp.Rf2;
using Cairn.Workspace;

namespace Cairn.Vpp.Tests;

/// <summary>
/// Red Faction II PEG texture packs (version 6, the PC game's own): what the stock files hold, built synthetically,
/// plus a bounded check of the real install when it is present.
/// </summary>
public sealed class Rf2PegTests
{
    private static (byte B, byte G, byte R, byte A) Pixel(BgraImage image, int x, int y)
    {
        int at = (y * image.Width + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2], image.Pixels[at + 3]);
    }

    /// <summary>One pack with every format RF2 uses: 16-bit, 8-bit indexed with 16-bit and 32-bit palettes, 32-bit.</summary>
    private static byte[] Rf2Sample() => SyntheticPeg.V6(
        SyntheticPeg.Texture("wall.tga", 8, 4, 3, 0, 1, 2, (_, _, x, _) => x == 0 ? 0x801Fu : 0x0000u), // red opaque, then clear
        SyntheticPeg.Texture("skin.tga", 4, 4, 4, 2, 1, 1, (_, _, x, _) => (uint)x, (_, i) => i switch
        {
            0 => 0x80FF0000u, // blue, opaque
            1 => 0x9500FF00u, // green with PS2 alpha 0x95 (above 0x80): must clamp to 255, not wrap
            2 => 0x40_0000FFu,
            _ => 0x8080_8080u,
        }),
        SyntheticPeg.Texture("glass.tga", 4, 2, 4, 1, 1, 1, (_, _, x, _) => (uint)(x & 1), (_, i) => i == 0 ? 0x7C00u : 0x83E0u), // bit 15 clear = transparent
        SyntheticPeg.Texture("sky.tga", 2, 2, 7, 0, 1, 1, (_, _, x, y) => 0x80_204060u + (uint)(x + y)),
        SyntheticPeg.Texture("fire.vbm", 2, 2, 4, 2, 3, 1, (f, _, _, _) => (uint)f, (f, i) => 0x80_000000u | (uint)(i * 10)));

    [Fact]
    public void Every_rf2_format_reads_and_decodes()
    {
        var bytes = Rf2Sample();
        var pack = PegCodec.Read(bytes, "level.peg");
        Assert.Equal(6, pack.Version);
        Assert.Equal(5, pack.Textures.Count);
        Assert.All(pack.Textures, t => Assert.True(t.CanDecode, t.Problem));
        foreach (var t in pack.Textures)
            for (int f = 0; f < t.FrameCount; f++)
                for (int m = 0; m < t.MipCount; m++)
                    Assert.Equal(t.MipWidth(m) * t.MipHeight(m) * 4, PegCodec.DecodeFrame(bytes, t, f, m).Pixels.Length);
    }

    [Fact]
    public void Palette_alpha_above_0x80_clamps_to_opaque()
    {
        var bytes = Rf2Sample();
        var skin = PegCodec.Read(bytes, "level.peg").Textures[1];
        var image = PegCodec.DecodeFrame(bytes, skin);
        Assert.Equal((byte)255, Pixel(image, 0, 0).A); // 0x80
        Assert.Equal((byte)255, Pixel(image, 1, 0).A); // 0x95: clamped (wrapping would give a nearly transparent 40)
        Assert.Equal((byte)0xFF, Pixel(image, 1, 0).G);
        Assert.Equal((byte)128, Pixel(image, 2, 0).A); // 0x40 is half
        Assert.Equal((byte)255, PegCodec.ScaleAlpha(0x95));
        Assert.Equal((byte)255, PegCodec.ScaleAlpha(0xFE));
    }

    [Fact]
    public void Sixteen_bit_palette_with_bit_15_clear_is_transparent_even_without_the_alpha_flag()
    {
        var bytes = Rf2Sample();
        var glass = PegCodec.Read(bytes, "level.peg").Textures[2];
        Assert.False(glass.HasAlphaFlag); // the flag is unreliable in RF2's files: alpha comes from the pixels
        var image = PegCodec.DecodeFrame(bytes, glass);
        Assert.Equal((byte)0, Pixel(image, 0, 0).A);
        Assert.Equal((byte)255, Pixel(image, 1, 0).A);
    }

    [Fact]
    public void Header_frame_total_counts_one_more_per_animated_texture_and_is_never_a_problem()
    {
        var bytes = Rf2Sample();
        // 4 still textures + 3 frames + 1 for the animated one, as in every stock RF2 pack
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x18)));
        var pack = PegCodec.Read(bytes, "level.peg");
        Assert.Equal(7, pack.Textures.Sum(t => t.FrameCount));
        Assert.All(pack.Textures, t => Assert.Null(t.Problem));
        // any value there: still read the same (Cairn never validates it)
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18), 12345);
        Assert.Equal(5, PegCodec.Read(bytes, "level.peg").DecodableCount);
        var fire = pack.Textures[4];
        Assert.True(fire.IsAnimated);
        Assert.Equal(3, fire.FrameCount);
    }

    [Fact]
    public void Empty_peg_says_it_is_empty()
    {
        var ex = Assert.Throws<AssetFormatException>(() => PegCodec.Read([], "poop_v3d.peg"));
        Assert.Contains("empty file", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GEKV", ex.Message, StringComparison.Ordinal);
        Assert.Equal("empty", VppInfo.Summarize("poop_v3d.peg", () => new MemoryStream(), 0).Text);
        var sheet = VppFacts.Describe("poop_v3d.peg", () => new MemoryStream(), 0);
        Assert.Equal("Empty file (0 bytes)", sheet["Contents"]);
        Assert.Empty(sheet.Warnings);
    }

    [Fact]
    public void Pack_with_no_textures_reads()
    {
        var pack = PegCodec.Read(SyntheticPeg.V6(), "none.peg");
        Assert.Empty(pack.Textures);
    }

    [Fact]
    public void Rf2_type_names_apply_only_inside_an_rf2_group()
    {
        Assert.Equal("RF2 texture pack", VppFileTypes.Describe("a.peg", rf2: true).DisplayName);
        Assert.Equal("RF2 animation", VppFileTypes.Describe("a.rfa", rf2: true).DisplayName);
        Assert.Equal("PS2 texture pack", VppFileTypes.Describe("a.peg", rf2: false).DisplayName);
        Assert.Equal("Animation clip", VppFileTypes.Describe("a.rfa").DisplayName);
        Assert.Equal("WAVE audio", VppFileTypes.Describe("a.wav", rf2: true).DisplayName);
        // Red Faction II animations are read (and converted) by the animations module, no longer extract-only
        Assert.True(VppFileTypes.Describe("MFP_AP_draw.rfa", rf2: true).CairnOpens);
    }

    [Fact]
    public void Rf2_clips_are_described_with_their_kind()
    {
        byte[] clip = Cairn.Rfa.Formats.Legacy.SyntheticRf2Clip.SampleSwing();
        var facts = VppFacts.Describe("swing.rfa", () => new MemoryStream(clip), clip.Length);
        Assert.Equal("9", facts["Version"]);
        Assert.StartsWith("Red Faction II clip", facts["Kind"]);
        string info = VppInfo.Summarize("swing.rfa", () => new MemoryStream(clip), clip.Length).Text;
        Assert.True(info.StartsWith("30 frames", StringComparison.Ordinal) && info.EndsWith("2 bones", StringComparison.Ordinal), info);
    }

    /// <summary>Bounded real-data check: every .peg of the Loading_screen and Menu groups and 20 from All_Levels decode.</summary>
    [Fact]
    public void Real_rf2_pegs_decode()
    {
        string? dir = LocalPaths.Rf2GameDirectory;
        if (dir is null || !Directory.Exists(Path.Combine(dir, "pc_media"))) return;
        var items = new List<Model.VppItem>();
        foreach (string group in new[] { "Loading_screen", "Menu", "All_Levels" })
        {
            string toc = Path.Combine(dir, "pc_media", group + ".toc_group");
            if (!File.Exists(toc)) continue;
            var pegs = Rf2Packfiles.Open(toc).Items.Where(i => i.Extension == ".peg").ToList();
            items.AddRange(group == "All_Levels" ? pegs.Where((_, i) => i % Math.Max(1, pegs.Count / 20) == 0).Take(20) : pegs);
        }
        int textures = 0;
        foreach (var item in items)
        {
            var bytes = TestData.ReadEntry(item);
            if (bytes.Length == 0)
            {
                Assert.Contains("empty file", Assert.Throws<AssetFormatException>(() => PegCodec.Read(bytes, item.Name)).Message, StringComparison.Ordinal);
                continue;
            }
            var pack = PegCodec.Read(bytes, item.Name);
            Assert.Equal(6, pack.Version);
            foreach (var t in pack.Textures)
            {
                Assert.True(t.CanDecode, $"{item.Name}/{t.Name}: {t.Problem}");
                Assert.False(t.IsMpeg2);
                for (int f = 0; f < t.FrameCount; f++)
                    for (int m = 0; m < t.MipCount; m++)
                        PegCodec.DecodeFrame(bytes, t, f, m);
                textures++;
            }
        }
        Assert.True(items.Count == 0 || textures > 0);
    }
}
