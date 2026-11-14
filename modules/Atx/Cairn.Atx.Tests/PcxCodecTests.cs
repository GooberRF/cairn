using System.Buffers.Binary;
using Cairn.Formats.Imaging;
using Cairn.Formats.XForge;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Atx.Tests;

/// <summary>
/// The PCX reader on files built here (<see cref="SyntheticPcx"/>): every bits-per-pixel and plane form, padded
/// lines, the window, runs that cross scanlines, palettes, and the malformed cases. The N-Gage release's own
/// textures are decoded too when its archive is configured.
/// </summary>
public sealed class PcxCodecTests(ITestOutputHelper output)
{
    /// <summary>A 256-colour palette whose entry i is (i, 255 - i, i * 7).</summary>
    private static byte[] Ramp256()
    {
        var p = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            p[i * 3] = (byte)i;
            p[i * 3 + 1] = (byte)(255 - i);
            p[i * 3 + 2] = (byte)(i * 7);
        }
        return p;
    }

    /// <summary>A 16-colour palette whose entry i is (i * 16, 200 - i * 8, 15 + i).</summary>
    private static byte[] Ramp16()
    {
        var p = new byte[48];
        for (int i = 0; i < 16; i++)
        {
            p[i * 3] = (byte)(i * 16);
            p[i * 3 + 1] = (byte)(200 - i * 8);
            p[i * 3 + 2] = (byte)(15 + i);
        }
        return p;
    }

    private static (byte B, byte G, byte R, byte A) Colour(byte[] palette, int index) =>
        (palette[index * 3 + 2], palette[index * 3 + 1], palette[index * 3], 255);

    private static void AssertPixels(BgraImage image, Func<int, int, (byte, byte, byte, byte)> expected)
    {
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                Assert.True(expected(x, y) == image.Get(x, y), $"pixel ({x}, {y}): expected {expected(x, y)}, got {image.Get(x, y)}");
    }

    private static string Fails(byte[] bytes, string name = "bad.pcx") =>
        Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(bytes, name)).Message;

    // ── the forms ─────────────────────────────────────────────────────────────

    [Fact]
    public void Paletted8_OddWidth_PaddingIgnored_ColoursFromTheEndPalette()
    {
        var palette = Ramp256();
        static int Index(int x, int y) => (x * 37 + y * 11) % 256;
        // 5 pixels in a 6-byte line: the padding byte holds an index that must not show
        var bytes = SyntheticPcx.Write(5, 3, 8, 1, Index, palette, new SyntheticPcxOptions { Padding = 0xEE });
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(66)));

        var info = ImageProbe.Probe(bytes, "a.pcx");
        Assert.Equal(ImageContainer.Pcx, info.Container);
        Assert.Equal("PCX", info.ContainerLabel);
        Assert.Equal((5, 3), (info.Width, info.Height));
        Assert.Equal(EngineFormat.Paletted8, info.Format);
        Assert.Null(info.MipLevels);
        Assert.Contains("version 5", info.Note, StringComparison.Ordinal);

        var image = ImageDecoder.Decode(bytes, "a.pcx");
        Assert.Equal((5, 3), (image.Width, image.Height));
        AssertPixels(image, (x, y) => Colour(palette, Index(x, y)));
    }

    [Fact]
    public void Paletted8_RunsCrossingScanlines()
    {
        var palette = Ramp256();
        // a flat image encoded as one stream: the first run covers more than one 4-byte line
        static int Index(int x, int y) => y < 3 ? 200 : x;
        var bytes = SyntheticPcx.Write(4, 5, 8, 1, Index, palette, new SyntheticPcxOptions { RunsCrossLines = true });
        Assert.Equal(0xC0 | 12, bytes[PcxCodec.HeaderSize]);
        AssertPixels(ImageDecoder.Decode(bytes, "runs.pcx"), (x, y) => Colour(palette, Index(x, y)));
    }

    [Fact]
    public void Paletted8_LongRunsAndBytesAboveC0()
    {
        var palette = Ramp256();
        // runs longer than 63 and literal values 0xC0 and up, which have to be written as runs of one
        static int Index(int x, int y) => x < 100 ? 0xC3 : (x % 2 == 0 ? 0xFF : 0xC0);
        var bytes = SyntheticPcx.Write(130, 2, 8, 1, Index, palette);
        AssertPixels(ImageDecoder.Decode(bytes, "long.pcx"), (x, y) => Colour(palette, Index(x, y)));
    }

    [Fact]
    public void Stored_Encoding0()
    {
        var palette = Ramp256();
        static int Index(int x, int y) => x + y * 3;
        var bytes = SyntheticPcx.Write(3, 3, 8, 1, Index, palette, new SyntheticPcxOptions { Rle = false });
        Assert.Equal(0, bytes[2]);
        Assert.Equal(PcxCodec.HeaderSize + 3 * 4 + 769, bytes.Length);
        Assert.Contains("uncompressed", ImageProbe.Probe(bytes, "s.pcx").Note, StringComparison.Ordinal);
        AssertPixels(ImageDecoder.Decode(bytes, "s.pcx"), (x, y) => Colour(palette, Index(x, y)));
    }

    [Fact]
    public void TrueColour24_ThreePlanes_OddWidth()
    {
        static int Rgb(int x, int y) => (x * 60 << 16) | (y * 90 << 8) | (x * 13 + y);
        var bytes = SyntheticPcx.Write(3, 2, 8, 3, Rgb, options: new SyntheticPcxOptions { Padding = 0x55 });
        var info = ImageProbe.Probe(bytes, "rgb.pcx");
        Assert.Equal(EngineFormat.Rgb888, info.Format);
        Assert.Contains("24-bit", info.Note, StringComparison.Ordinal);
        AssertPixels(ImageDecoder.Decode(bytes, "rgb.pcx"), (x, y) =>
            ((byte)(x * 13 + y), (byte)(y * 90), (byte)(x * 60), 255));
    }

    [Fact]
    public void Planar_1Bit4Planes_SixteenColours()
    {
        var palette = Ramp16();
        static int Index(int x, int y) => (x + y * 9) % 16;
        // 9 pixels: 2 bytes per plane line, the last holding one pixel
        var bytes = SyntheticPcx.Write(9, 3, 1, 4, Index, palette);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(66)));
        Assert.Equal(EngineFormat.Paletted8, ImageProbe.Probe(bytes, "ega.pcx").Format);
        AssertPixels(ImageDecoder.Decode(bytes, "ega.pcx"), (x, y) => Colour(palette, Index(x, y)));
    }

    [Theory]
    [InlineData(2, 1, 5)] // CGA, four colours packed four to a byte
    [InlineData(4, 1, 3)] // sixteen colours packed two to a byte
    [InlineData(1, 2, 11)] // four colours over two planes
    [InlineData(2, 2, 7)] // sixteen colours over two packed planes
    [InlineData(1, 3, 10)] // eight colours over three planes
    public void PackedAndPlanar_HeaderPalette(int bits, int planes, int width)
    {
        var palette = Ramp16();
        int colours = 1 << (bits * planes);
        int Index(int x, int y) => (x * 3 + y) % colours;
        var bytes = SyntheticPcx.Write(width, 4, bits, planes, Index, palette, new SyntheticPcxOptions { Padding = 0xFF, BytesPerLine = (width * bits + 7) / 8 + 2 });
        AssertPixels(ImageDecoder.Decode(bytes, "p.pcx"), (x, y) => Colour(palette, Index(x, y)));
    }

    [Fact]
    public void Monochrome_BlackAndWhite_WhenTheHeaderHasNoColours()
    {
        static int Index(int x, int y) => (x + y) % 2;
        // zeroed header palette, and a version 3 file ("no palette information") with colours in it
        foreach (var bytes in new[]
        {
            SyntheticPcx.Write(13, 2, 1, 1, Index),
            SyntheticPcx.Write(13, 2, 1, 1, Index, Ramp16(), new SyntheticPcxOptions { Version = 3 }),
        })
        {
            AssertPixels(ImageDecoder.Decode(bytes, "mono.pcx"), (x, y) => Index(x, y) == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        }
        // two different colours in the header are used as they are
        var palette = Ramp16();
        AssertPixels(ImageDecoder.Decode(SyntheticPcx.Write(13, 2, 1, 1, Index, palette), "mono.pcx"), (x, y) => Colour(palette, Index(x, y)));
    }

    [Fact]
    public void Version3_SixteenColours_UseTheStandardEgaPalette()
    {
        var bytes = SyntheticPcx.Write(16, 1, 4, 1, (x, _) => x, Ramp16(), new SyntheticPcxOptions { Version = 3 });
        var image = ImageDecoder.Decode(bytes, "v3.pcx");
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), image.Get(0, 0));
        Assert.Equal(((byte)0xAA, (byte)0, (byte)0, (byte)255), image.Get(1, 0)); // blue
        Assert.Equal(((byte)0, (byte)0x55, (byte)0xAA, (byte)255), image.Get(6, 0)); // brown
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), image.Get(15, 0));
    }

    [Fact]
    public void Window_SizeIsMaxMinusMinPlusOne()
    {
        var palette = Ramp256();
        static int Index(int x, int y) => x * 10 + y;
        var bytes = SyntheticPcx.Write(4, 2, 8, 1, Index, palette, new SyntheticPcxOptions { XMin = 10, YMin = 20 });
        Assert.Equal(13, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)));
        var info = ImageProbe.Probe(bytes, "w.pcx");
        Assert.Equal((4, 2), (info.Width, info.Height));
        AssertPixels(ImageDecoder.Decode(bytes, "w.pcx"), (x, y) => Colour(palette, Index(x, y)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void OlderVersions_8Bit_StillReadTheEndPalette(int version)
    {
        var palette = Ramp256();
        var bytes = SyntheticPcx.Write(2, 2, 8, 1, (x, y) => x + y * 2 + 40, palette, new SyntheticPcxOptions { Version = version });
        AssertPixels(ImageDecoder.Decode(bytes, "old.pcx"), (x, y) => Colour(palette, x + y * 2 + 40));
    }

    [Fact]
    public void Opaque_UnlessTheColourKeyHookIsUsed()
    {
        var bytes = SyntheticPcx.Write(4, 4, 8, 1, (x, y) => x == y ? 0 : 9, Ramp256());
        var image = ImageDecoder.Decode(bytes, "k.pcx");
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(255, image.Pixels[i * 4 + 3]));
        var keyed = PcxCodec.Decode(bytes, "k.pcx", colorKeyIndex: 0);
        Assert.Equal(0, keyed.Get(2, 2).A);
        Assert.Equal(255, keyed.Get(1, 2).A);
    }

    [Fact]
    public void ReadableExtensions_IncludePcx_DetectedByExtension()
    {
        Assert.Contains(".pcx", ImageProbe.ReadableExtensions);
        Assert.Equal(ImageContainer.Pcx, ImageProbe.FromExtension("HUD.PCX"));
        Assert.Equal(ImageContainer.Pcx, ImageProbe.Detect([0x0A, 5, 1, 8], "x.pcx"));
        // a PCX's leading byte is not a signature: a .tga stays a TGA
        Assert.Equal(ImageContainer.Tga, ImageProbe.Detect([0x0A, 5, 1, 8], "x.tga"));
    }

    // ── the N-Gage release ───────────────────────────────────────────────────

    [Fact]
    public void NgageArchive_EveryPcxDecodes()
    {
        string? folder = LocalPaths.NgageDirectory;
        string? path = folder is null ? null : Path.Combine(folder, "redfaction.cfl");
        if (path is null || !File.Exists(path)) return;
        var archive = CflArchive.Open(path);
        var entries = archive.Entries.Where(e => e.Name.EndsWith(".pcx", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(342, entries.Count);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byte[] bytes = archive.ReadEntry(stream, entry);
            var info = ImageProbe.Probe(bytes, entry.Name);
            Assert.Equal(ImageContainer.Pcx, info.Container);
            Assert.Equal(EngineFormat.Paletted8, info.Format);
            var image = ImageDecoder.Decode(bytes, entry.Name);
            Assert.Equal((info.Width, info.Height), (image.Width, image.Height));
            Assert.True(image.Pixels.Where((_, i) => i % 4 == 3).All(a => a == 255), $"{entry.Name}: opaque");
            string size = $"{image.Width}x{image.Height}";
            sizes[size] = sizes.GetValueOrDefault(size) + 1;
        }
        output.WriteLine($"{entries.Count} .pcx decoded: " + string.Join(", ", sizes.OrderByDescending(s => s.Value).Select(s => $"{s.Key} ({s.Value})")));
    }

    // ── malformed files ──────────────────────────────────────────────────────

    [Fact]
    public void TruncatedRle_IsRefusedClearly()
    {
        var bytes = SyntheticPcx.Write(16, 16, 8, 1, (x, y) => (x * 31 + y * 17) % 256, Ramp256());
        // drop pixel data but keep the palette at the end
        int data = bytes.Length - PcxCodec.HeaderSize - 769;
        var cut = bytes[..(PcxCodec.HeaderSize + data / 2)].Concat(bytes[^769..]).ToArray();
        Assert.Contains("truncated", Fails(cut), StringComparison.OrdinalIgnoreCase);

        // a run's count byte as the last byte of the data, its value missing
        var lone = SyntheticPcx.Write(64, 1, 8, 1, (x, _) => x < 62 ? x + 1 : 100, Ramp256(), new SyntheticPcxOptions { EndPalette = false });
        Assert.Equal(0xC2, lone[^2]);
        var palette = new byte[769];
        palette[0] = 0x0C;
        Assert.Contains("truncated", Fails([.. lone[..^1], .. palette]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TruncatedStoredAndPlanar_AreRefused()
    {
        var stored = SyntheticPcx.Write(8, 8, 8, 1, (x, _) => x, Ramp256(), new SyntheticPcxOptions { Rle = false });
        Fails([.. stored[..(PcxCodec.HeaderSize + 20)], .. stored[^769..]]);
        var rgb = SyntheticPcx.Write(8, 8, 8, 3, (x, y) => x * y * 1000);
        Fails(rgb[..(rgb.Length - 3)]);
        var ega = SyntheticPcx.Write(8, 8, 1, 4, (x, y) => (x + y) % 16, Ramp16());
        Fails(ega[..(PcxCodec.HeaderSize + 1)]);
    }

    [Fact]
    public void MissingPalette_IsRefusedClearly()
    {
        var bytes = SyntheticPcx.Write(8, 8, 8, 1, (x, _) => x, Ramp256(), new SyntheticPcxOptions { EndPalette = false });
        Assert.Contains("256-colour palette is missing", Fails(bytes), StringComparison.Ordinal);
        // the marker is checked, not just the length
        var wrong = SyntheticPcx.Write(8, 8, 8, 1, (x, _) => x, Ramp256());
        wrong[^769] = 0x0B;
        Assert.Contains("palette", Fails(wrong), StringComparison.Ordinal);
        // a header-only file
        Assert.Contains("palette", Fails(wrong[..PcxCodec.HeaderSize]), StringComparison.Ordinal);
    }

    [Fact]
    public void AbsurdDimensions_AreRefusedBeforeAllocating()
    {
        var bytes = SyntheticPcx.Write(4, 4, 8, 1, (x, _) => x, Ramp256());
        var wide = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(wide.AsSpan(8), 60000);
        Assert.Contains("pixel limit", Fails(wide), StringComparison.Ordinal);
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(wide, "bad.pcx"));

        // within the engine's limit but far more pixels than the file holds
        var big = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(big.AsSpan(8), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(big.AsSpan(10), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(big.AsSpan(66), 16002);
        Assert.Contains("damaged", Fails(big), StringComparison.Ordinal);

        // padding so wide the planes would outweigh any image Cairn decodes
        var padded = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(padded.AsSpan(10), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(padded.AsSpan(66), 65535);
        Assert.Contains("bytes per line", Fails(padded), StringComparison.Ordinal);

        // an empty window
        var empty = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(empty.AsSpan(4), 9);
        Assert.Contains("empty window", Fails(empty), StringComparison.Ordinal);
    }

    [Fact]
    public void LinesShorterThanTheWidth_AreRefused()
    {
        var bytes = SyntheticPcx.Write(9, 2, 8, 1, (x, _) => x, Ramp256());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(66), 8);
        Assert.Contains("bytes per line", Fails(bytes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8, 4)]
    [InlineData(8, 2)]
    [InlineData(4, 4)]
    [InlineData(2, 4)]
    [InlineData(16, 1)]
    [InlineData(3, 1)]
    [InlineData(1, 0)]
    public void UnsupportedBitsAndPlanes_AreRefusedByName(int bits, int planes)
    {
        var bytes = SyntheticPcx.Write(4, 4, 8, 1, (x, _) => x, Ramp256());
        bytes[3] = (byte)bits;
        bytes[65] = (byte)planes;
        string message = Fails(bytes);
        Assert.Contains($"{planes} plane", message, StringComparison.Ordinal);
        Assert.Contains("does not read", message, StringComparison.Ordinal);
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(bytes, "bad.pcx"));
    }

    [Fact]
    public void BadHeaders_AreRefused()
    {
        var bytes = SyntheticPcx.Write(4, 4, 8, 1, (x, _) => x, Ramp256());
        var maker = (byte[])bytes.Clone();
        maker[0] = 0x0B;
        Assert.Contains("not a PCX", Fails(maker), StringComparison.Ordinal);
        var version = (byte[])bytes.Clone();
        version[1] = 1;
        Assert.Contains("version 1", Fails(version), StringComparison.Ordinal);
        var encoding = (byte[])bytes.Clone();
        encoding[2] = 2;
        Assert.Contains("encoding 2", Fails(encoding), StringComparison.Ordinal);
        Assert.Contains("too short", Fails(bytes[..100]), StringComparison.Ordinal);
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(bytes[..100], "short.pcx"));
    }

    [Fact]
    public void EveryTruncationAndRandomDamage_FailsCleanly()
    {
        var files = new[]
        {
            SyntheticPcx.Write(13, 7, 8, 1, (x, y) => (x * y + 3) % 256, Ramp256()),
            SyntheticPcx.Write(13, 7, 8, 3, (x, y) => x * 70000 + y),
            SyntheticPcx.Write(13, 7, 1, 4, (x, y) => (x + y) % 16, Ramp16()),
            SyntheticPcx.Write(13, 7, 2, 1, (x, y) => (x + y) % 4, Ramp16(), new SyntheticPcxOptions { RunsCrossLines = true }),
        };
        var random = new Random(42);
        foreach (var file in files)
        {
            for (int length = 0; length < file.Length; length++) Survives(file[..length]);
            for (int n = 0; n < 2000; n++)
            {
                var damaged = (byte[])file.Clone();
                for (int k = random.Next(1, 4); k > 0; k--) damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
                Survives(damaged);
            }
        }

        // the codec itself, not ImageDecoder, which would turn any other exception into an ImageDecodeException
        static void Survives(byte[] bytes)
        {
            try { PcxCodec.Decode(bytes, "fuzz.pcx"); }
            catch (ImageDecodeException) { }
            try { PcxCodec.Probe(bytes, "fuzz.pcx"); }
            catch (ImageDecodeException) { }
        }
    }
}
