using System.Diagnostics;
using Cairn.Formats;
using Cairn.Formats.Imaging;
using Cairn.Formats.Vpp;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ps2;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

/// <summary>
/// The MPEG-2 intra decoder behind PEG format 2: bit reader, code tables (spot-checked against ISO/IEC 13818-2), the
/// IDCT against a direct reference, streams from the test encoder in every coding variant, malformed and unsupported
/// streams, and the real PS2 backgrounds when configured.
/// </summary>
public sealed class Mpeg2DecoderTests(ITestOutputHelper output)
{
    private static Mpeg2BitReader Bits(string bits)
    {
        bits = bits.Replace(" ", "", StringComparison.Ordinal);
        var bytes = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++) if (bits[i] == '1') bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return new Mpeg2BitReader(bytes);
    }

    // ---- bit reader -----------------------------------------------------------------------------------------------

    [Fact]
    public void BitReader_ReadsAcrossBytes_AndStopsAtTheEnd()
    {
        var r = new Mpeg2BitReader([0b1010_1100, 0b0101_0011, 0xFF, 0x00, 0x81]);
        Assert.Equal(0b101u, r.Read(3));
        Assert.Equal(0b0_1100_0101u, r.Read(9));
        Assert.Equal(12, r.Position);
        Assert.Equal(0b0011_1111_1111_0000_0000_1000u, r.Peek(24));
        Assert.Equal(0b0011_1111_1111_0000_0000_1000_0001u, r.Read(28));
        Assert.Equal(40, r.Position);
        Assert.Equal(0u, r.Peek(8)); // past the end reads as zeros
        Assert.Throws<EndOfStreamException>(() => r.Read(1));
        var whole = new Mpeg2BitReader([0xDE, 0xAD, 0xBE, 0xEF, 0x12]);
        Assert.Equal(0xDEADBEEFu, whole.Read(32));
    }

    [Fact]
    public void BitReader_FindsStartCodes_FromTheNextByte()
    {
        var r = new Mpeg2BitReader([0xFF, 0, 0, 1, 0xB3, 0x55, 0, 0, 1, 0xB7, 0, 0]);
        Assert.Equal(0xB3, r.NextStartCode());
        Assert.Equal(40, r.Position);
        r.Read(3); // mid-byte: the search starts at the next byte
        Assert.Equal(0xB7, r.NextStartCode());
        Assert.Equal(-1, r.NextStartCode());
        Assert.Equal(r.Length, r.Position);
    }

    // ---- code tables (values from ISO/IEC 13818-2 tables B.1, B.12, B.13, B.14, B.15) -----------------------------

    [Theory]
    [InlineData("1", 1)]
    [InlineData("011", 2)]
    [InlineData("0010", 5)]
    [InlineData("0000 111", 8)]
    [InlineData("0000 0101 11", 16)]
    [InlineData("0000 0011 000", 33)]
    [InlineData("0000 0001 000", Mpeg2Tables.MacroblockEscape)]
    public void MacroblockAddressIncrement_MatchesB1(string code, int value) => Assert.Equal(value, Mpeg2IntraDecoder.ReadMacroblockIncrement(Bits(code)));

    [Theory]
    [InlineData("100", true, 0)]
    [InlineData("00", true, 1)]
    [InlineData("01", true, 2)]
    [InlineData("1110", true, 5)]
    [InlineData("1111 1111 1", true, 11)]
    [InlineData("00", false, 0)]
    [InlineData("10", false, 2)]
    [InlineData("1111 10", false, 6)]
    [InlineData("1111 1111 11", false, 11)]
    public void DcSizes_MatchB12AndB13(string code, bool luma, int size) => Assert.Equal(size, Mpeg2IntraDecoder.ReadDcSize(Bits(code), luma));

    [Theory]
    // B.15 (intra_vlc_format = 1)
    [InlineData(true, "10 0", 0, 1)]
    [InlineData(true, "110 1", 0, -2)]
    [InlineData(true, "0111 0", 0, 3)]
    [InlineData(true, "010 0", 1, 1)]
    [InlineData(true, "1110 1 0", 0, 5)]
    [InlineData(true, "0010 1 0", 2, 1)]
    [InlineData(true, "0000 0000 0111 11 0", 0, 16)]
    [InlineData(true, "0000 0000 0011 000 0", 0, 32)]
    [InlineData(true, "0000 0000 0001 1011 0", 31, 1)]
    // B.14 (intra_vlc_format = 0), coefficients after the DC
    [InlineData(false, "11 0", 0, 1)]
    [InlineData(false, "011 1", 1, -1)]
    [InlineData(false, "0100 0", 0, 2)]
    [InlineData(false, "0101 0", 2, 1)]
    [InlineData(false, "0010 1 0", 0, 3)]
    [InlineData(false, "0010 0110 0", 0, 5)]
    [InlineData(false, "0001 00 0", 7, 1)]
    [InlineData(false, "0000 0001 1101 0", 0, 8)]
    [InlineData(false, "0000 0000 0001 1011 0", 31, 1)]
    public void CoefficientCodes_MatchTheStandard(bool tableOne, string code, int run, int level)
    {
        Assert.Equal(Mpeg2IntraDecoder.CoefficientCode.RunLevel, Mpeg2IntraDecoder.ReadCoefficient(Bits(code), tableOne, out int r, out int l));
        Assert.Equal((run, level), (r, l));
    }

    [Fact]
    public void CoefficientCodes_EndOfBlockAndEscape()
    {
        Assert.Equal(Mpeg2IntraDecoder.CoefficientCode.EndOfBlock, Mpeg2IntraDecoder.ReadCoefficient(Bits("0110"), true, out _, out _));
        Assert.Equal(Mpeg2IntraDecoder.CoefficientCode.EndOfBlock, Mpeg2IntraDecoder.ReadCoefficient(Bits("10"), false, out _, out _));
        // escape: 000001, run 6 bits, level 12 bits two's complement
        Assert.Equal(Mpeg2IntraDecoder.CoefficientCode.Escape, Mpeg2IntraDecoder.ReadCoefficient(Bits("000001 000101 1111 1111 1101"), true, out int run, out int level));
        Assert.Equal((5, -3), (run, level));
        Mpeg2IntraDecoder.ReadCoefficient(Bits("000001 111111 0111 1111 1111"), false, out run, out level);
        Assert.Equal((63, 2047), (run, level));
        Assert.Equal(Mpeg2IntraDecoder.CoefficientCode.Invalid, Mpeg2IntraDecoder.ReadCoefficient(Bits("0000 0000 0000 0000"), true, out _, out _));
        Assert.Equal(111, Mpeg2Tables.RunLevelCodes(true).Count);
        Assert.Equal(111, Mpeg2Tables.RunLevelCodes(false).Count);
    }

    // ---- IDCT -----------------------------------------------------------------------------------------------------

    [Fact]
    public void InverseDct_MatchesTheDirectFormula()
    {
        var random = new Random(1180);
        Span<int> f = stackalloc int[64];
        Span<double> fast = stackalloc double[64];
        double worst = 0;
        for (int trial = 0; trial < 2000; trial++)
        {
            int range = trial % 3 == 0 ? 300 : trial % 3 == 1 ? 5 : 2048;
            for (int i = 0; i < 64; i++) f[i] = trial % 7 == 0 && i > 10 ? 0 : random.Next(-range, range);
            Mpeg2IntraDecoder.InverseDct(f, fast);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (int v = 0; v < 8; v++)
                        for (int u = 0; u < 8; u++)
                            sum += (u == 0 ? Math.Sqrt(0.5) : 1) * (v == 0 ? Math.Sqrt(0.5) : 1) * f[v * 8 + u]
                                * Math.Cos((2 * x + 1) * u * Math.PI / 16) * Math.Cos((2 * y + 1) * v * Math.PI / 16);
                    worst = Math.Max(worst, Math.Abs(sum / 4 - fast[y * 8 + x]));
                }
        }
        Assert.True(worst < 1e-9, $"worst difference {worst}");
    }

    // ---- synthetic streams ------------------------------------------------------------------------------------------

    /// <summary>Smooth colours (gentle chroma, so 4:2:0 costs little) and some texture for the AC coefficients.</summary>
    private static BgraImage Smooth(int width, int height)
    {
        var image = new BgraImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                image.Pixels[at] = (byte)(128 + 90 * Math.Sin(x / 9.0 + y / 13.0));
                image.Pixels[at + 1] = (byte)(40 + 180 * y / Math.Max(1, height - 1));
                image.Pixels[at + 2] = (byte)(30 + 190 * x / Math.Max(1, width - 1));
                image.Pixels[at + 3] = 255;
            }
        return image;
    }

    private static (int Max, double Mean) Compare(BgraImage a, BgraImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        int max = 0;
        long total = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            int d = Math.Abs(a.Pixels[i] - b.Pixels[i]);
            max = Math.Max(max, d);
            total += d;
        }
        return (max, (double)total / a.Pixels.Length);
    }

    public static TheoryData<string> Variants => ["default", "tableZero", "alternate", "nonLinear", "dc8", "dc10", "dc11", "frameDct", "quantChanges", "matrixHeader", "matrixExtension", "progressive", "coarse"];

    private static Mpeg2EncodeOptions Options(string variant)
    {
        int[] custom = [.. Enumerable.Range(0, 64).Select(i => 8 + (i % 8) * 3 + (i / 8) * 2)];
        return variant switch
        {
            "tableZero" => new(TableOne: false),
            "alternate" => new(AlternateScan: true),
            "nonLinear" => new(NonLinearQuantiser: true, QuantiserScaleCode: 2),
            "dc8" => new(DcPrecision: 0),
            "dc10" => new(DcPrecision: 2),
            "dc11" => new(DcPrecision: 3),
            "frameDct" => new(FieldDct: false),
            "quantChanges" => new(ChangeQuantiser: true, NonLinearQuantiser: true),
            "matrixHeader" => new(Matrix: custom),
            "matrixExtension" => new(Matrix: custom, MatrixInExtension: true),
            "progressive" => new(ProgressiveSequence: true, FieldDct: false),
            "coarse" => new(QuantiserScaleCode: 12),
            _ => new(),
        };
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void SyntheticStream_DecodesCloseToTheSource(string variant)
    {
        var source = Smooth(72, 40); // not a multiple of 16: the picture is cropped from whole macroblocks
        var stream = SyntheticMpeg2.Encode(source, Options(variant));
        var picture = Mpeg2IntraDecoder.Decode(stream, variant);
        Assert.Equal((72, 40), (picture.Width, picture.Height));
        var (max, mean) = Compare(source, picture.ToBgra());
        output.WriteLine($"{variant}: {stream.Length} bytes, max {max}, mean {mean:F2}");
        // most of the error is the 4:2:0 chroma and the decoder's rounded BT.601 coefficients, not the coding
        Assert.True(mean < (variant is "coarse" or "quantChanges" ? 3.5 : 2.0), $"{variant}: mean error {mean:F2}");
        Assert.True(max <= (variant is "coarse" or "quantChanges" ? 40 : 16), $"{variant}: max error {max}");
    }

    [Fact]
    public void DcOnlyStream_OfFlatColours_IsExact()
    {
        var image = new BgraImage(32, 32);
        for (int i = 0; i < 32 * 32; i++)
        {
            // one flat grey per 16 x 16 macroblock: Y = 16 + 219 * v / 255 needs no rounding for these
            int x = i % 32, y = i / 32, v = (x / 16 + 2 * (y / 16)) * 51 + 51;
            image.Pixels[i * 4] = image.Pixels[i * 4 + 1] = image.Pixels[i * 4 + 2] = (byte)v;
            image.Pixels[i * 4 + 3] = 255;
        }
        var decoded = Mpeg2IntraDecoder.Decode(SyntheticMpeg2.Encode(image, new(DcOnly: true, FieldDct: false))).ToBgra();
        Assert.True(Compare(image, decoded).Max <= 2, $"max {Compare(image, decoded).Max}");
    }

    [Fact]
    public void PegData_TilesArePlacedAndCropped_AndJunkIsIgnored()
    {
        var bytes = SyntheticPeg.V6(SyntheticPeg.Mpeg2("bg.tga", 300, 270, seed: 3), SyntheticPeg.Texture("x.tga", 2, 2, 7, 0, 1, 1, (_, _, _, _) => 0x80FFFFFFu));
        var pack = PegCodec.Read(bytes, "t.peg");
        var t = pack.Textures[0];
        Assert.True(t.CanDecode);
        var tiles = PegCodec.Mpeg2Tiles(PegCodec.RawData(bytes, t), t.Name);
        Assert.Equal(4, tiles.Count); // 256 + 48 wide, 256 + 16 high
        var image = PegCodec.DecodeFrame(bytes, t);
        Assert.Equal((300, 270), (image.Width, image.Height));
        var (max, mean) = Compare(SyntheticPeg.Mpeg2Image(300, 270, 3), image);
        output.WriteLine($"300x270 in 4 tiles: max {max}, mean {mean:F2}");
        Assert.True(mean < 2.5, $"mean {mean:F2}");
        Assert.All(Enumerable.Range(0, 300 * 270), i => Assert.Equal(255, image.Pixels[i * 4 + 3]));
    }

    // ---- unsupported and malformed ---------------------------------------------------------------------------------

    /// <summary>The offset of the byte after start code <paramref name="code"/> (and extension id, when given).</summary>
    private static int After(byte[] s, int code, int extension = -1)
    {
        for (int i = 0; i + 4 < s.Length; i++)
            if (s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 1 && s[i + 3] == code && (extension < 0 || s[i + 4] >> 4 == extension)) return i + 4;
        throw new InvalidOperationException("start code not found");
    }

    private static string DecodeError(byte[] stream) => Assert.Throws<ImageDecodeException>(() => Mpeg2IntraDecoder.Decode(stream, "tile")).Message;

    [Fact]
    public void UnsupportedStreams_SaySo()
    {
        byte[] Fresh() => SyntheticMpeg2.Encode(Smooth(32, 32));
        var p = Fresh(); p[After(p, 0x00) + 1] = (byte)((p[After(p, 0x00) + 1] & ~0x38) | (2 << 3));
        Assert.Contains("P pictures", DecodeError(p));
        var b = Fresh(); b[After(b, 0x00) + 1] = (byte)((b[After(b, 0x00) + 1] & ~0x38) | (3 << 3));
        Assert.Contains("B pictures", DecodeError(b));
        // picture coding extension: id (4), f_codes (16), intra_dc_precision (2), picture_structure (2), then flags
        var field = Fresh(); field[After(field, 0xB5, 8) + 2] = (byte)((field[After(field, 0xB5, 8) + 2] & ~3) | 1);
        Assert.Contains("field pictures", DecodeError(field));
        var conceal = Fresh(); conceal[After(conceal, 0xB5, 8) + 3] |= 0x20;
        Assert.Contains("concealment motion vectors", DecodeError(conceal));
        var chroma = Fresh(); chroma[After(chroma, 0xB5, 1) + 1] = (byte)((chroma[After(chroma, 0xB5, 1) + 1] & ~0x06) | (2 << 1));
        Assert.Contains("4:2:2", DecodeError(chroma));
        var mpeg1 = Fresh(); mpeg1[After(mpeg1, 0xB5, 1) - 1] = 0xB2; // the sequence extension becomes user data
        Assert.Contains("MPEG-1", DecodeError(mpeg1));
        var scalable = Fresh(); scalable[After(scalable, 0xB5, 2)] = (byte)((5 << 4) | (scalable[After(scalable, 0xB5, 2)] & 0x0F));
        Assert.Contains("scalable", DecodeError(scalable));
        Assert.Contains("sequence header", DecodeError([0, 0, 1, 0xB8, 0, 0, 0, 0]));
        Assert.Contains("ends in the middle", DecodeError([0, 0, 1, 0xB3, 0x10, 0x01]));
        var pack = Fresh(); pack[After(pack, 0x00) - 1] = 0xBA;
        Assert.Contains("system", DecodeError(pack));
        // a huge declared size in a tiny stream: refused before anything is allocated
        var huge = Fresh(); int at = After(huge, 0xB3); huge[at] = 0xFF; huge[at + 1] = 0xFA; huge[at + 2] = 0xF0;
        Assert.Contains("too short", DecodeError(huge));
    }

    [Fact]
    public void TruncatedAndMutatedStreams_FailCleanly()
    {
        var stream = SyntheticMpeg2.Encode(Smooth(48, 48), new(ChangeQuantiser: true));
        var watch = Stopwatch.StartNew();
        for (int length = 0; length < stream.Length - 1; length++)
        {
            try { Mpeg2IntraDecoder.Decode(stream.AsSpan(0, length)); }
            catch (ImageDecodeException) { }
        }
        var random = new Random(2108);
        int decoded = 0, failed = 0;
        for (int trial = 0; trial < 3000; trial++)
        {
            var copy = (byte[])stream.Clone();
            int flips = 1 + random.Next(4);
            for (int i = 0; i < flips; i++)
            {
                int at = random.Next(copy.Length);
                copy[at] = trial % 2 == 0 ? (byte)(copy[at] ^ (1 << random.Next(8))) : (byte)random.Next(256);
            }
            try { Mpeg2IntraDecoder.Decode(copy); decoded++; }
            catch (ImageDecodeException) { failed++; }
        }
        output.WriteLine($"mutations: {decoded} decoded, {failed} refused, {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void DamagedPegTiles_FailCleanly()
    {
        var bytes = SyntheticPeg.V6(SyntheticPeg.Mpeg2("bg.tga", 300, 270));
        var t = PegCodec.Read(bytes, "t.peg").Textures[0];
        int data = (int)t.DataOffset;
        var longTile = (byte[])bytes.Clone();
        longTile[data + 4] = 0xFF; longTile[data + 5] = 0xFF; longTile[data + 6] = 0xFF;
        Assert.Contains("tile 1", Assert.Throws<ImageDecodeException>(() => PegCodec.DecodeFrame(longTile, t)).Message);
        var noHeader = (byte[])bytes.Clone();
        noHeader[data + 19] = 0;
        Assert.Contains("sequence header", Assert.Throws<ImageDecodeException>(() => PegCodec.DecodeFrame(noHeader, t)).Message);
        // the second tile's header damaged: the walk stops after one tile, which does not cover the picture
        var tiles = PegCodec.Mpeg2Tiles(PegCodec.RawData(bytes, t), t.Name);
        var cut = (byte[])bytes.Clone();
        cut[data + tiles[1].Offset + 3] = 0;
        Assert.Contains("do not cover", Assert.Throws<ImageDecodeException>(() => PegCodec.DecodeFrame(cut, t)).Message);
        var random = new Random(7);
        for (int trial = 0; trial < 300; trial++)
        {
            var copy = (byte[])bytes.Clone();
            for (int i = 0; i < 3; i++) copy[data + random.Next((int)t.DataLength)] = (byte)random.Next(256);
            try { PegCodec.DecodeFrame(copy, t); }
            catch (ImageDecodeException) { }
        }
    }

    // ---- the real files (only read; skipped when not configured) ---------------------------------------------------

    /// <summary>
    /// Every MPEG-2 texture of the PS2 folder's packfiles and the game's user_maps decodes; where the research
    /// prototype's PNGs are present (artifacts/mpeg2-research/out/all), the pixels agree within 2 per channel.
    /// </summary>
    [Fact]
    public void RealMpeg2Textures_AllDecode_AndMatchThePrototype()
    {
        if (LocalPaths.Ps2Directory is not { } ps2 || !Directory.Exists(ps2)) { output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory")); return; }
        string? reference = LocalPaths.RepositoryRoot is { } root ? Path.Combine(root, "artifacts", "mpeg2-research", "out", "all") : null;
        if (reference is not null && !Directory.Exists(reference)) reference = null;
        var sources = new List<(string Label, string Peg, byte[] Bytes)>();
        foreach (var file in Directory.EnumerateFiles(ps2, "*.vpp").Order(StringComparer.Ordinal))
        {
            VppPackage package;
            try { package = VppPackage.Open(file); }
            catch (AssetFormatException) { continue; }
            foreach (var item in Ps2Packfiles.Pegs(package)) sources.Add(("demo_" + Path.GetFileName(file), item.Name, item.Source.ReadAll()));
        }
        if (LocalPaths.GameDirectory is { } game && Directory.Exists(Path.Combine(game, "user_maps")))
            foreach (var file in Directory.EnumerateFiles(Path.Combine(game, "user_maps"), "*.peg").Order(StringComparer.Ordinal))
                sources.Add(("user_maps", Path.GetFileName(file), File.ReadAllBytes(file)));

        int entries = 0, tiles = 0, compared = 0, worst = 0;
        string worstName = "";
        var watch = Stopwatch.StartNew();
        foreach (var (label, peg, bytes) in sources)
        {
            foreach (var t in PegCodec.Read(bytes, peg).Textures.Where(t => t.IsMpeg2))
            {
                entries++;
                tiles += PegCodec.Mpeg2Tiles(PegCodec.RawData(bytes, t), t.Name).Count;
                var image = PegCodec.DecodeFrame(bytes, t);
                Assert.Equal((t.Width, t.Height), (image.Width, image.Height));
                if (reference is null) continue;
                string png = Path.Combine(reference, $"{label}__{Path.GetFileNameWithoutExtension(peg)}__{Path.GetFileNameWithoutExtension(t.Name)}.png");
                if (!File.Exists(png)) continue;
                var expected = ImageDecoder.Decode(File.ReadAllBytes(png), png);
                var (max, _) = Compare(expected, image);
                compared++;
                if (max > worst) { worst = max; worstName = Path.GetFileName(png); }
            }
        }
        output.WriteLine($"{entries} MPEG-2 textures, {tiles} tiles decoded in {watch.Elapsed.TotalSeconds:F1} s; {compared} compared with the prototype, max difference {worst} ({worstName})");
        if (reference is not null && compared > 0) Assert.True(worst <= 2, $"max difference {worst} in {worstName}");
    }
}
