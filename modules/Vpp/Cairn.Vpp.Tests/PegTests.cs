using System.Buffers.Binary;
using System.Text;
using Cairn.Atx.Parsing;
using Cairn.Formats;
using Cairn.Formats.Imaging;
using Cairn.Formats.Vpp;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ps2;
using Cairn.Vpp.Validation;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

/// <summary>PEG texture packs (PlayStation 2): the codec on synthetic files of every format, the conversion to PC files, and the real files when configured.</summary>
public sealed class PegTests(ITestOutputHelper output)
{
    private static uint Rgba(int r, int g, int b, int a) => (uint)(r | g << 8 | b << 16 | a << 24);

    private static (PegFile Pack, byte[] Bytes) One(SyntheticPegTexture texture)
    {
        var bytes = SyntheticPeg.V6(texture);
        return (PegCodec.Read(bytes, "test.peg"), bytes);
    }

    [Fact]
    public void Format7_Direct32_ScalesAlphaAndKeepsColour()
    {
        var (pack, bytes) = One(SyntheticPeg.Texture("a.tga", 4, 1, 7, 0, 1, 1, (f, m, x, y) => x switch
        {
            0 => Rgba(10, 20, 30, 0x80),
            1 => Rgba(255, 0, 0, 0x40),
            2 => Rgba(0, 255, 0, 0),
            _ => Rgba(1, 2, 3, 0xFE),
        }, flags: 1));
        var t = Assert.Single(pack.Textures);
        Assert.Equal(6, pack.Version);
        Assert.Equal("32-bit", t.FormatLabel);
        Assert.True(t.CanDecode);
        var image = PegCodec.DecodeFrame(bytes, t);
        Assert.Equal((30, 20, 10, 255), Px(image, 0, 0));
        Assert.Equal((0, 0, 255, 128), Px(image, 1, 0));
        Assert.Equal((0, 255, 0, 0), Px(image, 2, 0));
        Assert.Equal((3, 2, 1, 255), Px(image, 3, 0)); // above 0x80: clamped
    }

    private static (int B, int G, int R, int A) Px(BgraImage image, int x, int y)
    {
        var (b, g, r, a) = image.Get(x, y);
        return (b, g, r, a);
    }

    [Fact]
    public void Format3_Direct16_RedInLowBits_Bit15Opaque()
    {
        var (pack, bytes) = One(SyntheticPeg.Texture("b.tga", 3, 1, 3, 0, 1, 1, (f, m, x, y) => x switch
        {
            0 => 0x8000 | 0x1F,          // opaque red
            1 => 0x8000 | (0x1F << 10),  // opaque blue
            _ => 0x1F << 5,              // transparent green
        }));
        var image = PegCodec.DecodeFrame(bytes, pack.Textures[0]);
        Assert.Equal((0, 0, 255, 255), Px(image, 0, 0));
        Assert.Equal((255, 0, 0, 255), Px(image, 1, 0));
        Assert.Equal((0, 255, 0, 0), Px(image, 2, 0));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void Format4_Indexed8_SwapsIndexBits3And4(int paletteFormat)
    {
        // Palette entry i has red = i (32-bit palette) or red5 = i & 31 (16-bit palette); stored index 8 must read entry 16.
        uint Entry(int f, int i) => paletteFormat == 2 ? Rgba(i, 0, 0, 0x80) : (uint)(0x8000 | (i & 0x1F));
        int[] stored = [8, 16, 0x18, 7, 0xE7, 0xFF];
        var (pack, bytes) = One(SyntheticPeg.Texture("c.tga", stored.Length, 1, 4, paletteFormat, 1, 1, (f, m, x, y) => (uint)stored[x], Entry));
        var t = pack.Textures[0];
        Assert.Equal(paletteFormat == 2 ? "8-bit indexed, 32-bit palette" : "8-bit indexed, 16-bit palette", t.FormatLabel);
        var image = PegCodec.DecodeFrame(bytes, t);
        int[] expected = [16, 8, 0x18, 7, 0xE7, 0xFF]; // bits 3 and 4 swapped; equal bits stay
        for (int x = 0; x < stored.Length; x++)
        {
            int entry = expected[x];
            byte red = paletteFormat == 2 ? (byte)entry : ChannelBits.Expand(entry & 0x1F, 5);
            Assert.Equal(red, image.Get(x, 0).R);
            Assert.Equal(255, image.Get(x, 0).A);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void Format5_Indexed4_LowNibbleFirst_NoSwap(int paletteFormat)
    {
        uint Entry(int f, int i) => paletteFormat == 2 ? Rgba(i * 16, 0, 0, i == 0 ? 0 : 0x80) : (uint)((i == 0 ? 0 : 0x8000) | (i * 2));
        int[] stored = [1, 2, 15, 0, 9];
        var (pack, bytes) = One(SyntheticPeg.Texture("d.tga", stored.Length, 1, 5, paletteFormat, 1, 1, (f, m, x, y) => (uint)stored[x], Entry));
        var t = pack.Textures[0];
        Assert.Equal(16, t.PaletteEntries);
        Assert.Equal(3, t.MipBytes(0)); // 5 pixels at 4 bits, rounded up
        var image = PegCodec.DecodeFrame(bytes, t);
        for (int x = 0; x < stored.Length; x++)
        {
            int i = stored[x];
            byte red = paletteFormat == 2 ? (byte)(i * 16) : ChannelBits.Expand(i * 2, 5);
            Assert.Equal(red, image.Get(x, 0).R);
            Assert.Equal(i == 0 ? 0 : 255, image.Get(x, 0).A);
        }
    }

    [Fact]
    public void FramesAndMips_AreLaidOutPaletteFirstPerFrame()
    {
        // 3 frames x 3 mips of 8x4, 8-bit with a per-frame palette: entry i = (frame * 50, mip-agnostic) so frame and mip are both checked.
        var texture = SyntheticPeg.Texture("anim.vbm", 8, 4, 4, 2, 3, 3,
            (f, m, x, y) => (uint)(m * 10 + x), (f, i) => Rgba(i, f * 50, 0, 0x80));
        var (pack, bytes) = One(texture);
        var t = pack.Textures[0];
        Assert.Equal(3, t.FrameCount);
        Assert.Equal(3, t.MipCount);
        Assert.Equal(3 * (1024 + 32 + 8 + 2), t.ExpectedBytes);
        Assert.Equal(t.ExpectedBytes, texture.Data.Length);
        for (int f = 0; f < 3; f++)
        {
            for (int m = 0; m < 3; m++)
            {
                var image = PegCodec.DecodeFrame(bytes, t, f, m);
                Assert.Equal(t.MipWidth(m), image.Width);
                Assert.Equal(t.MipHeight(m), image.Height);
                int stored = m * 10 + 1;
                int entry = (stored & 0xE7) | ((stored >> 1) & 8) | ((stored << 1) & 0x10);
                Assert.Equal((byte)entry, image.Get(1, 0).R);
                Assert.Equal((byte)(f * 50), image.Get(1, 0).G);
            }
        }
        Assert.Throws<ImageDecodeException>(() => PegCodec.DecodeFrame(bytes, t, 3));
        Assert.Throws<ImageDecodeException>(() => PegCodec.DecodeFrame(bytes, t, 0, 3));
    }

    [Fact]
    public void Mpeg2_IsDecodedTo24BitTga_OrListedWhenSwitchedOff()
    {
        var bytes = SyntheticPeg.V6(SyntheticPeg.Mpeg2("bg.tga"), SyntheticPeg.Texture("x.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => Rgba(1, 2, 3, 0x80)));
        var pack = PegCodec.Read(bytes, "m.peg");
        Assert.True(pack.Textures[0].IsMpeg2);
        Assert.True(pack.Textures[0].CanDecode);
        Assert.Null(pack.Textures[0].Problem);
        Assert.Equal("MPEG-2 compressed", pack.Textures[0].FormatLabel);
        Assert.Equal(1, pack.Mpeg2Count);
        Assert.Equal("2 textures (1 MPEG-2 compressed)", pack.Summary);
        var image = PegCodec.DecodeFrame(bytes, pack.Textures[0]);
        Assert.Equal((640, 448), (image.Width, image.Height));

        var result = PegConverter.Convert(bytes, "m.peg");
        Assert.Empty(result.Skipped);
        Assert.Equal(["bg.tga", "x.tga"], result.Files.Select(f => f.Name));
        Assert.Equal(1, result.Mpeg2Textures);
        var bg = result.Files[0];
        Assert.Equal(24, bg.Bytes[16]); // no alpha
        Assert.Equal("from PS2 MPEG-2 (6 tiles)", bg.Note);
        var tga = TgaCodec.Decode(bg.Bytes, bg.Name);
        Assert.Equal(image.Pixels, tga.Pixels);
        Assert.Contains("24-bit .tga (no alpha)", PegConverter.BannerFor(result));

        var off = PegConverter.Convert(bytes, "m.peg", decodeMpeg2: false);
        var skipped = Assert.Single(off.Skipped);
        Assert.True(skipped.IsMpeg2);
        Assert.Equal(PegConverter.Mpeg2Reason, skipped.Reason);
        Assert.Equal(["x.tga"], off.Files.Select(f => f.Name));
        Assert.Contains("switched off", PegConverter.BannerFor(off));
    }

    [Fact]
    public void Mpeg2_DamagedStream_IsSkippedWithItsReason()
    {
        var bytes = SyntheticPeg.V6(SyntheticPeg.Mpeg2("bg.tga", 64, 48), SyntheticPeg.Texture("x.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => Rgba(1, 2, 3, 0x80)));
        var t = PegCodec.Read(bytes, "m.peg").Textures[0];
        // the picture header says P picture
        int picture = bytes.AsSpan((int)t.DataOffset + 16).IndexOf((ReadOnlySpan<byte>)[0, 0, 1, 0]) + (int)t.DataOffset + 16 + 4;
        bytes[picture + 1] = (byte)((bytes[picture + 1] & ~0x38) | (2 << 3));
        var result = PegConverter.Convert(bytes, "m.peg");
        var skipped = Assert.Single(result.Skipped);
        Assert.True(skipped.IsMpeg2);
        Assert.StartsWith(PegConverter.Mpeg2FailedReason, skipped.Reason);
        Assert.Contains("P pictures", skipped.Reason);
        Assert.Contains("bg.tga could not be decoded", PegConverter.BannerFor(result));
        Assert.Equal(["x.tga"], result.Files.Select(f => f.Name));
    }

    [Fact]
    public void Mpeg2_NumberedFrames_GetOneLoopingAtx_PagesDoNot()
    {
        var bytes = SyntheticPeg.Mpeg2Sequence(frames: 9);
        var pack = PegCodec.Read(bytes, "interface-bg-mm.peg");
        var sequence = Assert.Single(PegConverter.FindSequences(pack));
        Assert.Equal(9, sequence.Count);
        var result = PegConverter.Convert(bytes, "interface-bg-mm.peg");
        Assert.Equal([.. Enumerable.Range(1, 9).Select(i => $"plan-000{i}.tga"), "extras01.tga", "extras02.tga", "interface-bg-mm.atx"], result.Files.Select(f => f.Name));
        var atx = result.Files[^1];
        var parsed = AtxParser.Parse(Encoding.UTF8.GetString(atx.Bytes), atx.Name);
        Assert.Equal([.. Enumerable.Range(1, 9).Select(i => $"plan-000{i}.tga")], parsed.Model!.Frames.Select(f => f.EffectiveFile));
        Assert.Equal(33, parsed.Model.Header.EffectiveFrameTimeMs);
        Assert.Equal(Cairn.Atx.Schema.AtxAnimationMode.Loop, parsed.Model.Header.EffectiveAnimationMode);
        Assert.Equal("from PS2 MPEG-2 frames, 9 frames at 30 fps (rate unverified)", atx.Note);
        Assert.Single(result.Mpeg2Sequences);
        Assert.Equal(11, result.Mpeg2Textures);
        // the .atx takes the common stem when the PEG's name is taken
        var renamed = PegConverter.Convert(bytes, "interface-bg-mm.peg", isTaken: n => n == "interface-bg-mm.atx");
        Assert.Equal("plan.atx", renamed.Files[^1].Name);
        // fewer than 8 frames, or two-digit numbers: no .atx
        Assert.Empty(PegConverter.FindSequences(PegCodec.Read(SyntheticPeg.Mpeg2Sequence(frames: 7), "a.peg")));
        var pages = SyntheticPeg.V6([.. Enumerable.Range(1, 10).Select(i => SyntheticPeg.Mpeg2($"extras{i:00}.tga", 32, 32, seed: i))]);
        Assert.Empty(PegConverter.FindSequences(PegCodec.Read(pages, "b.peg")));
        // a gap splits a run; different sizes never join
        var gap = SyntheticPeg.V6([.. new[] { 1, 2, 3, 4, 5, 6, 7, 8, 10 }.Select(i => SyntheticPeg.Mpeg2($"f{i:000}.tga", 32, 32))]);
        Assert.Equal(8, Assert.Single(PegConverter.FindSequences(PegCodec.Read(gap, "c.peg"))).Count);
        var sizes = SyntheticPeg.V6([.. Enumerable.Range(1, 8).Select(i => SyntheticPeg.Mpeg2($"g{i:000}.tga", i == 4 ? 48 : 32, 32))]);
        Assert.Empty(PegConverter.FindSequences(PegCodec.Read(sizes, "d.peg")));
        // decoding switched off: no sequence
        Assert.Empty(PegConverter.Convert(bytes, "interface-bg-mm.peg", decodeMpeg2: false).Files);
    }

    [Fact]
    public void Version4_SequentialData_IsRead()
    {
        var a = SyntheticPeg.Texture("first_texture.tga", 4, 4, 4, 1, 1, 1, (f, m, x, y) => (uint)x, (f, i) => (uint)(0x8000 | i));
        var b = SyntheticPeg.Texture("b.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => Rgba(9, 8, 7, 0x80), flags: 1);
        var c = SyntheticPeg.Texture("c3.tga", 4, 2, 3, 1, 1, 1, (f, m, x, y) => 0x801F);
        var bytes = SyntheticPeg.V4(a, b, c);
        var pack = PegCodec.Read(bytes, "v4.peg");
        Assert.Equal(4, pack.Version);
        Assert.Equal(["first_texture.tga", "b.tga", "c3.tga"], pack.Textures.Select(t => t.Name));
        Assert.All(pack.Textures, t => Assert.True(t.CanDecode, t.Problem));
        Assert.Equal((7, 8, 9, 255), Px(PegCodec.DecodeFrame(bytes, pack.Textures[1]), 1, 1));
        Assert.Equal((0, 0, 255, 255), Px(PegCodec.DecodeFrame(bytes, pack.Textures[2]), 3, 1));
        Assert.Equal(3, PegConverter.Convert(bytes, "v4.peg").Files.Count);
    }

    [Fact]
    public void UnknownVariants_AreReportedNotGuessed()
    {
        var mpegV4 = SyntheticPeg.V4(SyntheticPeg.Mpeg2("bg.tga"));
        Assert.Contains("unsupported PEG variant", Assert.Throws<AssetFormatException>(() => PegCodec.Read(mpegV4, "a.peg")).Message);
        var v6 = SyntheticPeg.Sample();
        var v5 = (byte[])v6.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(v5.AsSpan(4), 5);
        Assert.Contains("unsupported PEG variant (version 5)", Assert.Throws<AssetFormatException>(() => PegCodec.Read(v5, "b.peg")).Message);
        var badDirectory = (byte[])v6.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badDirectory.AsSpan(8), 100);
        Assert.Contains("unsupported PEG variant", Assert.Throws<AssetFormatException>(() => PegCodec.Read(badDirectory, "c.peg")).Message);
        var v4Short = SyntheticPeg.V4(SyntheticPeg.Texture("x.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => 0));
        Assert.Contains("unsupported PEG variant", Assert.Throws<AssetFormatException>(() => PegCodec.Read(v4Short[..^4], "d.peg")).Message);
        Assert.Throws<AssetFormatException>(() => PegCodec.Read(Encoding.ASCII.GetBytes("not a peg at all"), "e.peg"));
        Assert.Equal("'b.peg' uses an unsupported PEG variant (version 5): Cairn reads versions 4 and 6.", PegConverter.Convert(v5, "b.peg").Error);
    }

    [Fact]
    public void UnknownFormatOrShortData_MarksTheTexture()
    {
        var odd = SyntheticPeg.Texture("odd.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => 0) with { Format = 9 };
        var good = SyntheticPeg.Texture("good.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => Rgba(1, 1, 1, 0x80));
        var pack = PegCodec.Read(SyntheticPeg.V6(odd, good), "o.peg");
        Assert.Contains("pixel format 9", pack.Textures[0].Problem);
        Assert.True(pack.Textures[1].CanDecode);

        var bytes = SyntheticPeg.V6(SyntheticPeg.Texture("cut.tga", 16, 16, 7, 0, 1, 1, (f, m, x, y) => 0));
        var cut = PegCodec.Read(bytes[..^100], "cut.peg").Textures[0];
        Assert.Contains("shorter than its header says", cut.Problem);
        Assert.Throws<ImageDecodeException>(() => PegCodec.DecodeFrame(bytes[..^100], cut));
        var result = PegConverter.Convert(bytes[..^100], "cut.peg");
        Assert.Empty(result.Files);
        Assert.StartsWith("cannot be decoded", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public void Converter_StaticToTga_AnimationToFramesAndAtx()
    {
        var bytes = SyntheticPeg.Sample();
        var result = PegConverter.Convert(bytes, "sample.peg", fpsFor: n => n == "fire.vbm" ? 20 : null);
        Assert.Null(result.Error);
        Assert.Equal(["wall.tga", "panel.tga", "sign.tga", "grate.tga", "dots.tga", "fire_00.tga", "fire_01.tga", "fire_02.tga", "fire.atx", "menu_bg.tga"], result.Files.Select(f => f.Name));
        Assert.Equal(7, result.ConvertedTextures);
        Assert.Equal(1, result.AnimatedTextures);
        Assert.Empty(result.Mpeg2);
        Assert.Equal(1, result.Mpeg2Textures);
        foreach (var file in result.Files.Where(f => f.Name.EndsWith(".tga", StringComparison.Ordinal)))
        {
            var info = TgaCodec.Probe(file.Bytes, file.Name);
            Assert.Equal(file.Texture.Width, info.Width);
            Assert.Equal(file.Texture.Height, info.Height);
            Assert.Equal(file.Texture.IsMpeg2 ? 24 : 32, file.Bytes[16]); // 32-bit, MPEG-2 backgrounds (no alpha) 24-bit
            Assert.Equal(PegConverter.NoteFor(file.Bytes), file.Note);
            Assert.StartsWith("from PS2 ", file.Note);
        }
        // mip 0 only, pixels exactly the decoded ones
        var wall = result.Files[0];
        var decoded = TgaCodec.Decode(wall.Bytes, wall.Name);
        Assert.Equal(PegCodec.DecodeFrame(bytes, wall.Texture).Pixels, decoded.Pixels);
        Assert.Contains("2 mips (level 0 kept)", wall.Note);
        var frame2 = TgaCodec.Decode(result.Files[7].Bytes, "f2");
        Assert.Equal(PegCodec.DecodeFrame(bytes, result.Files[7].Texture, 2).Pixels, frame2.Pixels);

        string atx = Encoding.UTF8.GetString(result.Files.Single(f => f.Name == "fire.atx").Bytes);
        var parse = AtxParser.Parse(atx, "fire.atx");
        Assert.NotNull(parse.Model);
        Assert.Equal(["fire_00.tga", "fire_01.tga", "fire_02.tga"], parse.Model!.Frames.Select(f => f.EffectiveFile));
        Assert.Equal(50, parse.Model.Header.EffectiveFrameTimeMs);
        Assert.Equal(Cairn.Atx.Schema.AtxAnimationMode.Loop, parse.Model.Header.EffectiveAnimationMode);
        Assert.DoesNotContain("\n", atx.Replace("\r\n", "|", StringComparison.Ordinal)); // CRLF throughout
    }

    [Fact]
    public void FrameNames_FitTheGamesBitmapNames()
    {
        Assert.Equal(["a_00.tga", "a_01.tga"], PegConverter.FrameNames("a", 2));
        Assert.Equal("a_99.tga", PegConverter.FrameNames("a", 100)[^1]);
        Assert.Equal(["a_000.tga", "a_100.tga"], PegConverter.FrameNames("a", 101).Where((_, i) => i is 0 or 100));
        var longNames = PegConverter.FrameNames("a_very_long_animated_texture_name_here", 12);
        Assert.All(longNames, n => Assert.True(n.Length <= 31, n));
        Assert.Equal(12, longNames.Distinct().Count());
    }

    [Fact]
    public void Converter_SkipsNamesAlreadyTaken()
    {
        var bytes = SyntheticPeg.V6(
            SyntheticPeg.Texture("same.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => 0),
            SyntheticPeg.Texture("SAME.vbm", 2, 2, 7, 0, 1, 1, (f, m, x, y) => 0),
            SyntheticPeg.Texture("other.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => 0));
        var result = PegConverter.Convert(bytes, "dup.peg", isTaken: n => n == "other.tga");
        Assert.Equal(["same.tga"], result.Files.Select(f => f.Name));
        Assert.Equal(2, result.Skipped.Count);
        Assert.All(result.Skipped, s => Assert.Contains("already uses the name", s.Reason));
    }

    [Fact]
    public void Converter_LongAnimationNamesThatShortenAlikeBothConvert()
    {
        // review: both stems shorten to "explosion_big_fire_anim": the second animation was dropped
        var bytes = SyntheticPeg.V6(
            SyntheticPeg.Texture("explosion_big_fire_anim_A.vbm", 2, 2, 7, 0, 2, 1, (f, m, x, y) => (uint)(f + x)),
            SyntheticPeg.Texture("explosion_big_fire_anim_B.vbm", 2, 2, 7, 0, 2, 1, (f, m, x, y) => (uint)(f * 3 + y)));
        var result = PegConverter.Convert(bytes, "x.peg");
        Assert.Empty(result.Skipped);
        Assert.Equal(2, result.AnimatedTextures);
        Assert.Equal(6, result.Files.Count);
        Assert.Equal(6, result.Files.Select(f => f.Name).Distinct(VppNames.Comparer).Count());
        Assert.All(result.Files, f => Assert.True(f.Name.Length <= 31, f.Name));
        Assert.Contains(result.Files, f => f.Name == "explosion_big_fire_anim_A.atx");
        Assert.Contains(result.Files, f => f.Name == "explosion_big_fire_anim_B.atx");
        // the second animation's frames get the first alternative, and its .atx lists them
        Assert.Equal(["explosion_big_fire_anim1_00.tga", "explosion_big_fire_anim1_01.tga"], PegConverter.FrameNames("explosion_big_fire_anim_B", 2, 1));
        var atx = Encoding.UTF8.GetString(result.Files.Single(f => f.Name == "explosion_big_fire_anim_B.atx").Bytes);
        Assert.Contains("explosion_big_fire_anim1_00.tga", atx);
        // a short name keeps its plain frames; an alternative adds the number before the frame number
        Assert.Equal(["a2_00.tga"], PegConverter.FrameNames("a", 1, 2));
    }

    [Fact]
    public void Ps2Packfile_TexturesSeveralPegsHoldAreComparedAndTheLargestKept()
    {
        // review: a later PEG's texture of the same name was dropped as "one copy is enough" even when it differed
        var small = SyntheticPeg.Texture("envirohand.tga", 4, 4, 7, 0, 1, 1, (f, m, x, y) => Rgba(x * 40, y * 40, 0, 0x80));
        var large = SyntheticPeg.Texture("envirohand.tga", 8, 8, 7, 0, 1, 1, (f, m, x, y) => Rgba(x * 20, 0, y * 20, 0x80));
        var shared = SyntheticPeg.Texture("shared.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => Rgba(1, 2, 3, 0x80));
        var tieA = SyntheticPeg.Texture("tie.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => Rgba(9, 9, 9, 0x80));
        var tieB = SyntheticPeg.Texture("tie.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => Rgba(7, 7, 7, 0x80));
        var package = VppEdit.AddSources(VppPackage.Empty,
        [
            ("fp_riot.peg", new MemorySource(SyntheticPeg.V6(small, shared, tieA))),
            ("level.peg", new MemorySource(SyntheticPeg.V6(large, shared, tieB))),
        ], VppClashPolicy.KeepBoth).Package;
        var conversions = Ps2Packfiles.ConvertEntries(package);
        var report = Ps2Packfiles.Apply(package, conversions);
        var names = report.Package.Items.Select(i => i.Name).ToList();
        Assert.Equal(1, names.Count(n => n == "envirohand.tga"));
        Assert.Equal(1, names.Count(n => n == "shared.tga"));
        Assert.Equal(1, names.Count(n => n == "tie.tga"));
        // the 8×8 copy is kept although its PEG comes second; a tie keeps the first
        var kept = ImageDecoder.Decode(((MemorySource)report.Package.Find("envirohand.tga")!.Source).Bytes, "envirohand.tga");
        Assert.Equal((8, 8), (kept.Width, kept.Height));
        Assert.Same(conversions[0].Result.Files.Single(f => f.Name == "tie.tga").Bytes, ((MemorySource)report.Package.Find("tie.tga")!.Source).Bytes);
        Assert.Equal(1, report.IdenticalCopies);
        Assert.Equal(2, report.Conflicts!.Count);
        Assert.Contains("envirohand.tga: 2 different versions, kept 8×8 from level.peg, skipped 4×4 from fp_riot.peg", report.Conflicts);
        Assert.Contains("tie.tga: 2 different versions, kept 2×2 from fp_riot.peg, skipped 2×2 from level.peg", report.Conflicts);
        Assert.Contains("envirohand.tga: 2 different versions, kept 8×8 from level.peg", report.Summary);
        Assert.Contains("1 identical copy of textures already present left out", report.Summary);
        Assert.Contains("2 textures exist in more than one version (the largest kept)", report.Summary);
        Assert.DoesNotContain("other texture", report.Summary);
        Assert.Contains(report.Skipped, s => s.Texture == "shared.tga" && s.Peg == "level.peg" && s.Reason.Contains("identical", StringComparison.Ordinal));
        Assert.Equal(3, report.Textures); // envirohand (level.peg), shared and tie (fp_riot.peg)
    }

    [Fact]
    public void MipSize_LevelsOf32AndMoreAreOnePixel()
    {
        // C# masks a shift count to 5 bits: Width >> 32 is Width again
        var t = PegCodec.Read(SyntheticPeg.V6(SyntheticPeg.Texture("m.tga", 64, 32, 7, 0, 1, 1, (f, m, x, y) => 0)), "m.peg").Textures[0];
        Assert.Equal((32, 16), (t.MipWidth(1), t.MipHeight(1)));
        Assert.Equal((1, 1), (t.MipWidth(32), t.MipHeight(32)));
        Assert.Equal((1, 1), (t.MipWidth(200), t.MipHeight(254)));
    }

    [Fact]
    public void Ps2Packfile_IsDetected_AndConversionReplacesPegsInPlace()
    {
        var peg = SyntheticPeg.Sample();
        var package = VppEdit.AddSources(VppPackage.Empty,
        [
            ("level.tbl", new MemorySource(Encoding.ASCII.GetBytes("#x\n"))),
            ("pack.peg", new MemorySource(peg)),
            ("mesh.rfm", new MemorySource([0x12, 0x87, 0x12, 0x87])),
            ("broken.peg", new MemorySource(Encoding.ASCII.GetBytes("GEKV\u0005\0\0\0"))),
            ("wall.tga", new MemorySource([1, 2, 3])),
        ], VppClashPolicy.KeepBoth).Package;
        Assert.True(Ps2Packfiles.IsPs2(package));
        Assert.False(Ps2Packfiles.IsPs2(TestData.Small(("a.tga", 4), ("b.vcm", 4))));
        Assert.Equal(2, Ps2Packfiles.Pegs(package).Count);

        var conversions = Ps2Packfiles.ConvertEntries(package);
        var report = Ps2Packfiles.Apply(package, conversions);
        var names = report.Package.Items.Select(i => i.Name).ToList();
        // wall.tga already existed: the PEG's wall.tga is skipped, never doubled
        Assert.Equal(["level.tbl", "panel.tga", "sign.tga", "grate.tga", "dots.tga", "fire_00.tga", "fire_01.tga", "fire_02.tga", "fire.atx", "menu_bg.tga", "mesh.rfm", "broken.peg", "wall.tga"], names);
        Assert.Equal(["pack.peg"], report.ReplacedPegs);
        Assert.Single(report.Unreadable);
        Assert.Equal(0, report.Mpeg2Count);
        Assert.Equal(1, report.Mpeg2Decoded);
        Assert.Contains(report.Skipped, s => s.Texture == "wall.tga");
        Assert.All(report.Package.Items.Where(i => report.AddedFiles.Contains(i.Name)), i => Assert.Equal(VppItemState.Added, i.State));
        Assert.Contains("Converted 6 textures (1 animated, 1 MPEG-2 background) from 1 PEG texture pack into 9 files", report.Summary);
        Assert.Equal("640x448, from PS2 MPEG-2 (6 tiles)", VppInfo.Summarize(report.Package.Find("menu_bg.tga")!).Text);
        var off = Ps2Packfiles.Apply(package, Ps2Packfiles.ConvertEntries(package, decodeMpeg2: false));
        Assert.Equal(1, off.Mpeg2Count);
        Assert.Contains("1 MPEG-2 background not converted (decoding is switched off)", off.Summary);
        // a converted entry's Info line and details say where it came from
        var sign = report.Package.Find("sign.tga")!;
        Assert.Contains("from PS2 8-bit indexed, 32-bit palette", VppInfo.Summarize(sign).Text);
        Assert.Equal(PegConverter.NoteFor(((MemorySource)sign.Source).Bytes), VppFacts.Describe(sign)["Converted"]);
        // the PS2 types have names, and the problem says what they are
        Assert.Equal("PS2 texture pack", VppFileTypes.Describe("a.peg").DisplayName);
        Assert.Equal("PS2 static mesh", VppFileTypes.Describe("a.rfm").DisplayName);
        Assert.Contains(VppValidator.Validate(package), p => p.Code == "VPP009" && p.Message.Contains("PlayStation 2", StringComparison.Ordinal) && p.EntryName == "pack.peg");
    }

    [Fact]
    public void InfoAndFacts_DescribeAPeg()
    {
        var peg = SyntheticPeg.Sample();
        var line = VppInfo.Summarize("a.peg", () => new MemoryStream(peg), peg.Length);
        Assert.Equal("PEG v6: 7 textures (1 animated, 1 MPEG-2 compressed)", line.Text);
        var sheet = VppFacts.Describe("a.peg", () => new MemoryStream(peg), peg.Length);
        Assert.Equal("PS2 texture pack", sheet["Type"]);
        Assert.Equal("PEG texture pack, version 6", sheet["Format"]);
        Assert.Equal("16x16, 32-bit, 2 mips", sheet["Textures: wall.tga"]);
        Assert.Equal("640x448, MPEG-2 compressed; no alpha", sheet["Textures: menu_bg.tga"]);
        Assert.DoesNotContain(sheet.Warnings, w => w.Contains("MPEG-2", StringComparison.Ordinal));
        // a .peg entry under another name is still recognised by its signature
        Assert.Equal(".peg", VppFacts.SniffExtension(peg));
    }

    // ---- the real files (only read; skipped when not configured) ----------------------------------------------

    /// <summary>Every PEG under the PlayStation 2 folder (loose and inside its packfiles) and the game folder's user_maps.</summary>
    private static IEnumerable<(string Name, byte[] Bytes)> RealPegs()
    {
        foreach (var dir in new[] { LocalPaths.Ps2Directory, LocalPaths.GameDirectory is { } g ? Path.Combine(g, "user_maps") : null })
        {
            if (dir is null || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".peg") yield return (Path.GetFileName(file), File.ReadAllBytes(file));
                else if (ext == ".vpp" && dir == LocalPaths.Ps2Directory)
                {
                    VppPackage package;
                    try { package = VppPackage.Open(file); }
                    catch (AssetFormatException) { continue; }
                    foreach (var item in Ps2Packfiles.Pegs(package)) yield return (Path.GetFileName(file) + ">" + item.Name, item.Source.ReadAll());
                }
            }
        }
    }

    [Fact]
    public void RealPegs_EveryTextureDecodes_AndSizesLineUpExactly()
    {
        int files = 0, textures = 0, mpeg = 0, animated = 0, v4 = 0;
        var formats = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, bytes) in RealPegs())
        {
            files++;
            var pack = PegCodec.Read(bytes, name);
            if (pack.Version == 4) v4++;
            uint directory = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), data = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            Assert.Equal(bytes.Length, (pack.Version == 6 ? 32 : 28) + directory + data);
            foreach (var t in pack.Textures)
            {
                textures++;
                string key = $"{t.Format}/{t.PaletteFormat}";
                formats[key] = formats.GetValueOrDefault(key) + 1;
                if (t.IsMpeg2)
                {
                    mpeg++;
                    // an MPEG-2 sequence header in the first bytes of its data
                    Assert.True(PegCodec.RawData(bytes, t)[..64].IndexOf((ReadOnlySpan<byte>)[0, 0, 1, 0xB3]) >= 0, $"{name}: {t.Name} has no MPEG-2 sequence header");
                    Assert.True(t.CanDecode, $"{name}: {t.Name}: {t.Problem}");
                    continue;
                }
                Assert.True(t.CanDecode, $"{name}: {t.Name}: {t.Problem}");
                // exact sizes: the header's frames x (palette + mips) fills the data up to at most the 16-byte padding
                Assert.True(t.DataLength - t.ExpectedBytes is >= 0 and < 16, $"{name}: {t.Name} has {t.DataLength} bytes for {t.ExpectedBytes}");
                if (t.IsAnimated) animated++;
                for (int f = 0; f < t.FrameCount; f++)
                    for (int m = 0; m < t.MipCount; m++)
                    {
                        var image = PegCodec.DecodeFrame(bytes, t, f, m);
                        Assert.Equal(t.MipWidth(m), image.Width);
                        Assert.Equal(t.MipHeight(m), image.Height);
                    }
            }
            var result = PegConverter.Convert(bytes, name);
            Assert.Null(result.Error);
            Assert.All(result.Skipped, s => Assert.True(s.Reason.Contains("already uses the name", StringComparison.Ordinal), $"{name}: {s.Name}: {s.Reason}"));
            if (pack.Textures.Any(t => t.Name.StartsWith("plan-0", StringComparison.OrdinalIgnoreCase)))
            {
                var atx = Assert.Single(result.Mpeg2Sequences);
                output.WriteLine($"{name}: {atx.Name} ({atx.Note})");
            }
        }
        output.WriteLine($"{files} PEG files ({v4} version 4), {textures} textures, {animated} animated, {mpeg} MPEG-2; formats: {string.Join(", ", formats.Select(kv => $"{kv.Key} x{kv.Value}"))}");
        if (files == 0) output.WriteLine("No PEG files: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory"));
    }

    [Fact]
    public void RealPs2Packfiles_AreDetected_AndConvert()
    {
        if (LocalPaths.Ps2Directory is not { } dir || !Directory.Exists(dir)) { output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory")); return; }
        foreach (var file in Directory.EnumerateFiles(dir, "*.vpp"))
        {
            var package = VppPackage.Open(file);
            bool hasPeg = Ps2Packfiles.Pegs(package).Count > 0;
            if (!hasPeg) continue; // the tables packfile holds only .tbl files
            Assert.True(Ps2Packfiles.IsPs2(package), Path.GetFileName(file));
            var report = Ps2Packfiles.Apply(package, Ps2Packfiles.ConvertEntries(package));
            Assert.Empty(report.Unreadable);
            // MPEG-2 backgrounds, and textures several packs (or one pack under .tga and .vbm) share: one copy is kept,
            // the largest when they differ, and differing copies are listed
            Assert.Equal(0, report.Mpeg2Failed);
            Assert.All(report.Skipped, s => Assert.True(s.Reason.Contains("one copy is enough", StringComparison.Ordinal)
                || s.Reason.Contains(" different versions, kept ", StringComparison.Ordinal) || s.Reason.Contains(" already gives ", StringComparison.Ordinal)
                || s.Reason.Contains(" already in the packfile", StringComparison.Ordinal) || s.Reason.Contains("the packfile already has", StringComparison.Ordinal)
                || s.Reason.StartsWith(PegConverter.NameTakenReason, StringComparison.Ordinal), s.Reason));
            Assert.Equal(report.DifferentCopies + report.Conflicts!.Count(c => c.Contains(": replaced the ", StringComparison.Ordinal)), report.Conflicts!.Count);
            foreach (var conflict in report.Conflicts.Take(5)) output.WriteLine("  " + conflict);
            Assert.Empty(report.Package.Items.Where(i => Ps2Packfiles.IsPeg(i.Name)).Select(i => i.Name));
            // no name doubled by the conversion (the demo's own packfiles already hold a few doubled names)
            var counts = report.Package.Items.GroupBy(i => i.Name, VppNames.Comparer).ToDictionary(g => g.Key, g => g.Count(), VppNames.Comparer);
            Assert.All(report.AddedFiles, n => Assert.Equal(1, counts[n]));
            output.WriteLine($"{Path.GetFileName(file)}: {report.Summary}");
        }
    }

    // ---- black as transparent (MPEG-2 backgrounds) ------------------------------------------------------------

    [Fact]
    public void BlackKey_MapsEachPixelByItsBrightestComponent()
    {
        var key = new PegBlackKey(25);
        Assert.Equal(0, key.AlphaOf(0, 0, 0));
        Assert.Equal(0, key.AlphaOf(24, 24, 24));
        Assert.Equal(255, key.AlphaOf(25, 0, 0));    // one component at the threshold keeps the pixel
        Assert.Equal(255, key.AlphaOf(0, 0, 25));
        Assert.Equal(255, key.AlphaOf(10, 200, 10));
        Assert.Equal(255, key.AlphaOf(30, 30, 30));  // no soft edge: nothing half transparent
        var soft = new PegBlackKey(25, softEdge: true);
        Assert.Equal(50, soft.SoftLimit);
        Assert.Equal(0, soft.AlphaOf(24, 0, 0));
        Assert.Equal(128, soft.AlphaOf(25, 0, 0));
        Assert.Equal(128, soft.AlphaOf(49, 49, 10));
        Assert.Equal(255, soft.AlphaOf(50, 0, 0));
        // 0 keys out nothing; the threshold is clamped to 0..64
        Assert.Equal(255, new PegBlackKey(0, true).AlphaOf(0, 0, 0));
        Assert.Equal(PegBlackKey.MaxThreshold, new PegBlackKey(200).Threshold);
        Assert.Equal(0, new PegBlackKey(-5).Threshold);
        Assert.Equal(25, PegBlackKey.GameThreshold);
        Assert.Equal("black below 25 transparent, soft edge to 50", soft.Describe());

        var image = new BgraImage(3, 1);
        image.Set(0, 0, 0, 0, 0, 255);
        image.Set(1, 0, 10, 40, 10, 255);  // B, G, R: green 40
        image.Set(2, 0, 200, 200, 200, 255);
        Assert.Same(image, soft.Apply(image));
        Assert.Equal([0, 128, 255], new[] { image.Get(0, 0).A, image.Get(1, 0).A, image.Get(2, 0).A });
        Assert.Equal((10, 40, 10), (image.Get(1, 0).B, image.Get(1, 0).G, image.Get(1, 0).R)); // colour kept
    }

    [Fact]
    public void BlackKey_AppliesToMpeg2Only_AndTheGameFlagAsksForTheGameKey()
    {
        var (pack, _) = One(SyntheticPeg.Mpeg2Black("bg.tga"));
        var plain = PegCodec.Read(SyntheticPeg.V6(SyntheticPeg.Texture("a.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => 0)), "a.peg").Textures[0];
        var flagged = PegCodec.Read(SyntheticPeg.V6(SyntheticPeg.Mpeg2Black("ui.tga", flags: PegBlackKey.UsesAlphaFlag)), "f.peg").Textures[0];
        var chosen = new PegBlackKey(40, true);
        Assert.Null(PegBlackKey.For(plain, chosen));
        Assert.Null(PegBlackKey.For(pack.Textures[0], null));
        Assert.Same(chosen, PegBlackKey.For(pack.Textures[0], chosen));
        Assert.Same(PegBlackKey.Game, PegBlackKey.For(flagged, null));
        Assert.Same(chosen, PegBlackKey.For(flagged, chosen));
    }

    [Fact]
    public void Mpeg2Background_WithBlackKey_Becomes32BitTgaWithAlpha()
    {
        var bytes = SyntheticPeg.V6(SyntheticPeg.Mpeg2Black("bg.tga"), SyntheticPeg.Texture("a.tga", 2, 2, 7, 0, 1, 1, (f, m, x, y) => Rgba(0, 0, 0, 0x80)));
        var off = PegConverter.Convert(bytes, "x.peg");
        Assert.Equal(24, off.Files.Single(f => f.Name == "bg.tga").Bytes[16]);
        Assert.Contains("24-bit .tga (no alpha)", PegConverter.BannerFor(off));

        var on = PegConverter.Convert(bytes, "x.peg", blackKey: PegBlackKey.Game);
        var tga = on.Files.Single(f => f.Name == "bg.tga");
        Assert.Equal(32, tga.Bytes[16]);
        var image = TgaCodec.Decode(tga.Bytes, "bg.tga");
        Assert.Equal(0, image.Get(4, 4).A);     // the black half
        Assert.Equal(255, image.Get(60, 4).A);  // the grey half
        Assert.Equal(0, image.Get(31, 31).A);
        Assert.Contains("black below 25 transparent", tga.Note);
        Assert.Contains("32-bit .tga with black below 25 transparent", PegConverter.BannerFor(on));
        // black pixels of other formats are not touched (their alpha is their own)
        Assert.Equal(255, TgaCodec.Decode(on.Files.Single(f => f.Name == "a.tga").Bytes, "a.tga").Get(0, 0).A);

        // a background whose entry has the PS2's "uses alpha" flag gets the game's key even with the option off
        var flagged = PegConverter.Convert(SyntheticPeg.V6(SyntheticPeg.Mpeg2Black("ui.tga", flags: PegBlackKey.UsesAlphaFlag)), "f.peg");
        Assert.Equal(32, flagged.Files.Single().Bytes[16]);
        Assert.Equal(0, TgaCodec.Decode(flagged.Files.Single().Bytes, "ui.tga").Get(2, 2).A);
    }

    [Fact]
    public void Convert_OnlyTheTickedTextures()
    {
        var result = PegConverter.Convert(SyntheticPeg.Sample(), "s.peg", textures: new HashSet<int> { 0, 5 });
        Assert.Equal(["wall.tga", "fire_00.tga", "fire_01.tga", "fire_02.tga", "fire.atx"], result.Files.Select(f => f.Name));
        Assert.Equal(5, result.NotChosen);
        Assert.All(result.Skipped, s => Assert.Equal(PegConverter.NotChosenReason, s.Reason));
        Assert.DoesNotContain("left out", PegConverter.BannerFor(result));
    }

    [Fact]
    public void AtxNames_NameTheAnimationAndSequenceAtx()
    {
        var sample = PegCodec.Read(SyntheticPeg.Sample(), "s.peg");
        Assert.Equal(new Dictionary<int, string> { [5] = "fire.atx" }, PegConverter.AtxNames(sample, "s.peg"));
        var menu = PegCodec.Read(SyntheticPeg.Mpeg2Sequence(), "interface-bg-mm.peg");
        var names = PegConverter.AtxNames(menu, "interface-bg-mm.peg");
        Assert.Equal(8, names.Count);
        Assert.All(names.Values, n => Assert.Equal("interface-bg-mm.atx", n));
    }

    private static VppPackage Dups(params (string Name, byte[] Bytes)[] entries) =>
        VppEdit.AddSources(VppPackage.Empty, [.. entries.Select(e => (e.Name, (VppSource)new MemorySource(e.Bytes)))], VppClashPolicy.KeepBoth).Package;

    private static byte[] WallPeg(int size, int seed = 0, bool animation = false) => animation
        ? SyntheticPeg.V6(SyntheticPeg.Texture("wall.tga", size, size, 7, 0, 1, 1, (f, m, x, y) => Rgba(x + seed, y, size, 0x80)),
            SyntheticPeg.Texture("boom.vbm", size, size, 7, 0, 2, 1, (f, m, x, y) => Rgba(f * 50 + seed, x, size, 0x80)))
        : SyntheticPeg.V6(SyntheticPeg.Texture("wall.tga", size, size, 7, 0, 1, 1, (f, m, x, y) => Rgba(x + seed, y, size, 0x80)));

    private static Ps2ConvertReport ConvertOne(VppPackage package, string peg, bool keep = false)
    {
        int index = package.Items.ToList().FindIndex(i => i.Name == peg);
        return Ps2Packfiles.Apply(package, Ps2Packfiles.ConvertEntries(package, entries: [new PegEntryChoice(index)]), keep);
    }

    [Fact]
    public void OnePegAtATime_KeepsTheLargerVersionOfANameAlreadyPresent()
    {
        var package = Dups(("a.peg", WallPeg(8, animation: true)), ("b.peg", WallPeg(16, seed: 3, animation: true)), ("c.peg", WallPeg(8, animation: true)), ("d.peg", WallPeg(4)));
        // only the chosen .peg is converted
        var a = ConvertOne(package, "a.peg");
        Assert.Equal(["wall.tga", "boom_00.tga", "boom_01.tga", "boom.atx", "b.peg", "c.peg", "d.peg"], a.Package.Items.Select(i => i.Name));
        // the same textures again: identical, left out
        var c = ConvertOne(a.Package, "c.peg");
        Assert.Equal(2, c.IdenticalCopies);
        Assert.Empty(c.AddedFiles);
        Assert.Contains(c.Skipped, s => s.Reason.StartsWith("the packfile already holds the same wall.tga", StringComparison.Ordinal));
        // smaller: the larger one already there stays
        var d = ConvertOne(c.Package, "d.peg");
        Assert.Equal(1, d.DifferentCopies);
        Assert.Contains("wall.tga: kept the 8×8 already in the packfile, skipped 4×4 from d.peg", d.Conflicts!);
        Assert.Equal(8, TgaCodec.Decode(d.Package.Find("wall.tga")!.Source.ReadAll(), "wall.tga").Width);
        Assert.DoesNotContain("other texture", d.Summary);
        // larger: it replaces the entry (and an animation's .atx with its frames)
        var b = ConvertOne(d.Package, "b.peg");
        var names = b.Package.Items.Select(i => i.Name).ToList();
        Assert.Equal(16, TgaCodec.Decode(b.Package.Find("wall.tga")!.Source.ReadAll(), "wall.tga").Width);
        Assert.Single(names, n => n == "wall.tga");
        Assert.Single(names, n => n == "boom.atx");
        Assert.DoesNotContain("boom_00.tga", names);
        Assert.Contains("boom1_00.tga", names);
        Assert.Contains("boom1_01.tga", Encoding.UTF8.GetString(b.Package.Find("boom.atx")!.Source.ReadAll()));
        Assert.Contains("wall.tga: replaced the 8×8 already in the packfile by 16×16 from b.peg", b.Conflicts!);
        Assert.Contains("boom.atx: replaced the 8×8 already in the packfile by 16×16 from b.peg; removed its 2 old frames", b.Conflicts!);
        Assert.Equal(["boom.atx", "boom_00.tga", "boom_01.tga", "wall.tga"], b.ReplacedEntries!.Order(StringComparer.Ordinal));
        Assert.Contains("2 textures exist in more than one version (the largest kept)", b.Summary);
        Assert.Equal(2, b.Textures);
        Assert.DoesNotContain(names, Ps2Packfiles.IsPeg);
    }

    [Theory]
    [InlineData("[[frame]]\nfile = 'boom_00.tga'\n", true)]
    [InlineData("[header]\nalpha_mask = \"boom_00.tga\"\n\n[[frame]]\nfile = \"wall.tga\"\n", true)]
    [InlineData("[[frame]]\nfile = \"\"\"boom_00.tga\"\"\"\n", true)]
    [InlineData("[header]\nframe_time = 80\n\n[[frame]]\nfile = \"wall.tga\"\n", false)]
    [InlineData("this is = = not toml [[", true)]
    public void OnePegAtATime_KeepsOldFramesAnotherAtxNames(string otherAtx, bool keptBoom00)
    {
        var a = ConvertOne(Dups(("a.peg", WallPeg(8, animation: true)), ("b.peg", WallPeg(16, seed: 3, animation: true))), "a.peg");
        var package = VppEdit.AddSources(a.Package, [("other.atx", (VppSource)new MemorySource(Encoding.UTF8.GetBytes(otherAtx)))], VppClashPolicy.KeepBoth).Package;
        var b = ConvertOne(package, "b.peg");
        var names = b.Package.Items.Select(i => i.Name).ToList();
        Assert.Equal(keptBoom00, names.Contains("boom_00.tga"));
        // boom_01.tga is named by nothing else; it goes unless other.atx cannot be read
        bool unreadable = otherAtx.StartsWith("this", StringComparison.Ordinal);
        Assert.Equal(unreadable, names.Contains("boom_01.tga"));
        Assert.Equal(keptBoom00, b.ReplacedEntries!.All(n => n != "boom_00.tga"));
        var note = Assert.Single(b.Conflicts!, c => c.StartsWith("boom.atx: replaced", StringComparison.Ordinal));
        Assert.Equal(keptBoom00, note.Contains("kept boom_00.tga", StringComparison.Ordinal));
        Assert.Contains(note[..20], b.Summary);
    }

    [Fact]
    public void OnePegAtATime_AnEntryOfUnknownSizeIsKept_AndPegsCanStay()
    {
        var package = Dups(("wall.tga", [1, 2, 3]), ("a.peg", WallPeg(8)));
        var report = ConvertOne(package, "a.peg", keep: true);
        Assert.Equal(["wall.tga", "a.peg"], report.Package.Items.Select(i => i.Name));
        Assert.Equal([1, 2, 3], report.Package.Find("wall.tga")!.Source.ReadAll());
        Assert.Contains(report.Skipped, s => s.Reason.Contains("whose size cannot be read", StringComparison.Ordinal));
        // keep the .peg: the files follow it, and it can be converted again later
        var keep = ConvertOne(Dups(("a.peg", WallPeg(8, animation: true))), "a.peg", keep: true);
        Assert.Equal(["a.peg", "wall.tga", "boom_00.tga", "boom_01.tga", "boom.atx"], keep.Package.Items.Select(i => i.Name));
        var again = ConvertOne(keep.Package, "a.peg", keep: true);
        Assert.Equal(2, again.IdenticalCopies);
        Assert.Equal(keep.Package.Items.Select(i => i.Name), again.Package.Items.Select(i => i.Name));
    }

    [Fact]
    public void ConvertEntries_TexturesAndBlackKeyPerChoice()
    {
        var package = Dups(("ui.peg", SyntheticPeg.V6(SyntheticPeg.Mpeg2Black("strip.tga"), SyntheticPeg.Mpeg2Black("portrait.tga"))), ("other.peg", WallPeg(4)));
        var conversions = Ps2Packfiles.ConvertEntries(package, blackKey: new PegBlackKey(30, true), entries: [new PegEntryChoice(0, new HashSet<int> { 1 })]);
        var report = Ps2Packfiles.Apply(package, conversions);
        Assert.Equal(["portrait.tga", "other.peg"], report.Package.Items.Select(i => i.Name));
        Assert.Equal(1, report.NotChosen);
        Assert.Contains("1 texture not ticked", report.Summary);
        Assert.Equal(32, report.Package.Find("portrait.tga")!.Source.ReadAll()[16]);
        Assert.Equal(Ps2Packfiles.PegBanner, Ps2Packfiles.BannerFor(1));
        Assert.Equal(Ps2Packfiles.Banner, Ps2Packfiles.BannerFor(0));
    }

    [Fact]
    public void RealPs2Packfile_ConvertedOnePegAtATime_KeepsTheSameTextures()
    {
        if (LocalPaths.Ps2Directory is not { } dir || !Directory.Exists(dir)) { output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory")); return; }
        string? file = Directory.EnumerateFiles(dir, "*.vpp").Where(p => Ps2Packfiles.Pegs(VppPackage.Open(p)).Count > 1).OrderBy(p => new FileInfo(p).Length).FirstOrDefault();
        if (file is null) { output.WriteLine("no PS2 packfile with several PEG files"); return; }
        var package = VppPackage.Open(file);
        var all = Ps2Packfiles.Apply(package, Ps2Packfiles.ConvertEntries(package)).Package;
        var current = package;
        while (Ps2Packfiles.Pegs(current) is { Count: > 0 } pegs) current = ConvertOne(current, pegs[0].Name).Package;
        static HashSet<string> Frames(VppPackage p) => [.. p.Items.Where(i => VppNames.ExtensionOf(i.Name) == ".atx")
            .SelectMany(i => System.Text.RegularExpressions.Regex.Matches(Encoding.UTF8.GetString(i.Source.ReadAll()), "file = \"([^\"]+)\"").Select(m => m.Groups[1].Value))];
        static Dictionary<string, (int, int)?> Main(VppPackage p)
        {
            var frames = Frames(p);
            return p.Items.Where(i => !frames.Contains(i.Name)).ToDictionary(i => i.Name, i => Ps2Packfiles.EntrySize(p, i), VppNames.Comparer);
        }
        var expected = Main(all);
        var actual = Main(current);
        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), actual.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(expected, kv => Assert.Equal(kv.Value, actual[kv.Key]));
        static IEnumerable<string> Doubled(VppPackage p) => p.Items.GroupBy(i => i.Name, VppNames.Comparer).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(Doubled(current).Except(Doubled(package), VppNames.Comparer));
        output.WriteLine($"{Path.GetFileName(file)}: {Ps2Packfiles.Pegs(package).Count} PEG files one at a time give the same {expected.Count} textures as all at once");
    }
}
