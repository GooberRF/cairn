using Cairn.Formats.Imaging;
using Cairn.Formats.Vpp;
using Cairn.Workspace;

namespace Cairn.Atx.Tests;

/// <summary>Round trips through <see cref="DdsEncoder"/> and the shared <see cref="DdsCodec"/>.</summary>
public class DdsEncoderTests
{
    private static readonly DdsTargetFormat[] Offered =
    [
        DdsTargetFormat.Dxt1, DdsTargetFormat.Dxt1Alpha, DdsTargetFormat.Dxt3, DdsTargetFormat.Dxt5,
        DdsTargetFormat.Argb8888, DdsTargetFormat.Rgb565, DdsTargetFormat.Argb1555, DdsTargetFormat.Argb4444,
    ];

    /// <summary>Smooth gradient with a soft alpha ramp, a hard-edged disc and some noise.</summary>
    private static BgraImage Synthetic(int w, int h, bool alpha, bool binaryAlpha = false)
    {
        var img = new BgraImage(w, h);
        var rng = new Random(7);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte r = (byte)(x * 255 / Math.Max(w - 1, 1));
                byte g = (byte)(y * 255 / Math.Max(h - 1, 1));
                byte b = (byte)Math.Clamp(128 + rng.Next(-6, 7), 0, 255);
                byte a = 255;
                if (alpha) a = binaryAlpha ? (byte)((x / 8 + y / 8) % 2 == 0 ? 255 : 0) : (byte)((x + y) * 255 / (w + h - 2));
                img.Set(x, y, b, g, r, a);
            }
        }
        return img;
    }

    private static double Psnr(BgraImage a, BgraImage b, bool withAlpha, bool skipCutOut = false)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        double sum = 0;
        long n = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            if (!withAlpha && i % 4 == 3) continue;
            // Colour under fully transparent pixels is irrelevant (DXT1 alpha makes it black).
            if (withAlpha && i % 4 != 3 && a.Pixels[i - i % 4 + 3] == 0) continue;
            // DXT1 with 1-bit alpha turns translucent pixels into transparent black by design.
            if (skipCutOut && b.Pixels[i - i % 4 + 3] == 0) continue;
            double d = a.Pixels[i] - b.Pixels[i];
            sum += d * d;
            n++;
        }
        double mse = sum / Math.Max(n, 1);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static double MinimumPsnr(DdsTargetFormat f) => f switch
    {
        DdsTargetFormat.Argb8888 => 99,
        DdsTargetFormat.Rgb565 or DdsTargetFormat.Argb1555 => 34,
        DdsTargetFormat.Argb4444 => 28,
        _ => 30,
    };

    [Fact]
    public void EveryOfferedFormatRoundTripsThroughTheSharedDecoder()
    {
        var source = Synthetic(64, 32, alpha: false);
        foreach (var format in Offered)
        {
            var result = DdsEncoder.EncodeDetailed(source, new DdsEncodeOptions { Format = format });
            var info = DdsCodec.Probe(result.Bytes, "t.dds");
            Assert.Equal(DdsEncoder.EngineFormatOf(format), info.Format);
            Assert.Equal(64, info.Width);
            Assert.Equal(32, info.Height);
            Assert.Equal(7, info.MipLevels);
            var decoded = DdsCodec.Decode(result.Bytes, "t.dds");
            double psnr = Psnr(source, decoded, withAlpha: false);
            Assert.True(psnr >= MinimumPsnr(format), $"{format}: PSNR {psnr:F1}");
            if (format == DdsTargetFormat.Argb8888) Assert.Equal(source.Pixels, decoded.Pixels);
        }
    }

    [Fact]
    public void AlphaFormatsKeepAlpha()
    {
        var smooth = Synthetic(32, 32, alpha: true);
        foreach (var format in new[] { DdsTargetFormat.Dxt3, DdsTargetFormat.Dxt5, DdsTargetFormat.Argb8888, DdsTargetFormat.Argb4444 })
        {
            var decoded = DdsCodec.Decode(DdsEncoder.Encode(smooth, new DdsEncodeOptions { Format = format }), "a.dds");
            Assert.True(Psnr(smooth, decoded, withAlpha: true) >= MinimumPsnr(format) - 2, format.ToString());
        }
        var binary = Synthetic(32, 32, alpha: true, binaryAlpha: true);
        foreach (var format in new[] { DdsTargetFormat.Dxt1Alpha, DdsTargetFormat.Argb1555 })
        {
            var decoded = DdsCodec.Decode(DdsEncoder.Encode(binary, new DdsEncodeOptions { Format = format }), "b.dds");
            for (int i = 3; i < binary.Pixels.Length; i += 4) Assert.Equal(binary.Pixels[i], decoded.Pixels[i]);
        }
    }

    [Fact]
    public void AutoPicksByAlphaKind()
    {
        Assert.Equal(DdsTargetFormat.Dxt1, DdsEncoder.EncodeDetailed(Synthetic(16, 16, false), new()).Target);
        Assert.Equal(DdsTargetFormat.Dxt1Alpha, DdsEncoder.EncodeDetailed(Synthetic(16, 16, true, true), new()).Target);
        Assert.Equal(DdsTargetFormat.Dxt5, DdsEncoder.EncodeDetailed(Synthetic(16, 16, true), new()).Target);
    }

    [Fact]
    public void MipChainSizesAndHeaderFieldsAreValid()
    {
        var source = Synthetic(64, 16, alpha: true);
        foreach (var format in Offered)
        {
            var r = DdsEncoder.EncodeDetailed(source, new DdsEncodeOptions { Format = format });
            var engine = DdsEncoder.EngineFormatOf(format);
            long expected = 128;
            int w = 64, h = 16;
            for (int i = 0; i < r.MipCount; i++)
            {
                expected += DdsEncoder.LevelSize(w, h, engine);
                var level = DdsEncoder.ExtractLevel(r.Bytes, i);
                Assert.NotNull(level);
                var img = DdsCodec.Decode(level!, "l.dds");
                Assert.Equal((w, h), (img.Width, img.Height));
                w = Math.Max(w / 2, 1);
                h = Math.Max(h / 2, 1);
            }
            Assert.Equal(7, r.MipCount);
            Assert.Equal(expected, r.Bytes.Length);
            uint flags = BitConverter.ToUInt32(r.Bytes, 8);
            Assert.NotEqual(0u, flags & 0x20000);            // mip count flag
            Assert.Equal(124u, BitConverter.ToUInt32(r.Bytes, 4));
            Assert.Equal(0x401008u, BitConverter.ToUInt32(r.Bytes, 108)); // texture | complex | mipmap
            Assert.NotEqual(0x30315844u, BitConverter.ToUInt32(r.Bytes, 84)); // never "DX10"
        }

        var none = DdsEncoder.EncodeDetailed(source, new DdsEncodeOptions { Mips = DdsMipMode.None });
        Assert.Equal(1, DdsCodec.Probe(none.Bytes, "n.dds").MipLevels);
        var three = DdsEncoder.EncodeDetailed(source, new DdsEncodeOptions { Mips = DdsMipMode.Count, MipCount = 3 });
        Assert.Equal(3, DdsCodec.Probe(three.Bytes, "n.dds").MipLevels);
    }

    [Fact]
    public void NonPowerOfTwoSizesFollowTheResizeRule()
    {
        var odd = Synthetic(30, 10, alpha: false);
        var r = DdsEncoder.EncodeDetailed(odd, new DdsEncodeOptions());
        Assert.True(r.Resized);
        Assert.Equal((32, 8), (r.Width, r.Height));
        Assert.Equal((32, 16), DdsEncoder.TargetSize(30, 10, true, new DdsEncodeOptions { Rounding = PowerOfTwoRounding.Larger }));
        Assert.Equal((16, 8), DdsEncoder.TargetSize(30, 10, true, new DdsEncodeOptions { Rounding = PowerOfTwoRounding.Smaller }));
        Assert.Throws<ArgumentException>(() => DdsEncoder.Encode(odd, new DdsEncodeOptions { Resize = DdsResize.Never }));

        // Uncompressed formats keep any size; multiples of 4 stay as they are for DXT.
        var keep = DdsEncoder.EncodeDetailed(odd, new DdsEncodeOptions { Format = DdsTargetFormat.Argb8888 });
        Assert.Equal((30, 10), (keep.Width, keep.Height));
        Assert.Equal(odd.Pixels, DdsCodec.Decode(keep.Bytes, "k.dds").Pixels);
        var twelve = DdsEncoder.EncodeDetailed(Synthetic(12, 20, false), new DdsEncodeOptions());
        Assert.False(twelve.Resized);
        var always = DdsEncoder.EncodeDetailed(Synthetic(12, 20, false), new DdsEncodeOptions { Resize = DdsResize.Always });
        Assert.Equal((16, 16), (always.Width, always.Height));
    }

    [Fact]
    public void FiltersProduceSaneMips()
    {
        var flat = new BgraImage(16, 16);
        for (int i = 0; i < flat.Pixels.Length; i += 4) { flat.Pixels[i] = 10; flat.Pixels[i + 1] = 200; flat.Pixels[i + 2] = 90; flat.Pixels[i + 3] = 255; }
        foreach (var filter in Enum.GetValues<ResampleFilter>())
        {
            var half = ImageResampler.HalveForMip(flat, filter);
            Assert.Equal((8, 8), (half.Width, half.Height));
            Assert.All(Enumerable.Range(0, 64), i => Assert.Equal((byte)200, half.Pixels[i * 4 + 1]));
        }
    }

    [Fact]
    public void StockTexturesRoundTrip()
    {
        if (LocalPaths.GameDirectory is not { } game) return;
        // The first top-level packfile that holds TGA textures (stock data, read only).
        VppArchive? archive = null;
        List<VppEntry> textures = [];
        foreach (var vpp in Directory.EnumerateFiles(game, "*.vpp").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = VppArchive.Open(vpp);
            textures = candidate.Entries.Where(e => e.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
            if (textures.Count > 0) { archive = candidate; break; }
        }
        if (archive is null) return;
        Assert.NotEmpty(textures);
        foreach (var entry in textures)
        {
            var source = ImageDecoder.Decode(archive.ReadEntry(entry), entry.Name);
            if (source.Width % 4 != 0 || source.Height % 4 != 0) continue;
            foreach (var format in Offered)
            {
                var bytes = DdsEncoder.Encode(source, new DdsEncodeOptions { Format = format, Quality = DdsQuality.Fast });
                var decoded = DdsCodec.Decode(bytes, entry.Name);
                double psnr = Psnr(source, decoded, withAlpha: false, skipCutOut: format == DdsTargetFormat.Dxt1Alpha);
                Assert.True(psnr >= MinimumPsnr(format) - 6, $"{entry.Name} {format}: PSNR {psnr:F1}");
            }
        }
    }
}
