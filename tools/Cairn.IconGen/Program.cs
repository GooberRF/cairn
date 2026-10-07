using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cairn.IconGen;

/// <summary>
/// Draws Cairn's application icon and writes it as a multi-size .ico.
///
/// The motif is a cairn: rounded stones stacked into a small tower, each slightly offset from the one
/// below, widest at the bottom and capped by a warm stone. The palette is the suite's: the deep blue
/// ground of ATX Workbench and RFA Workbench (sitting between ATX's blue and RFA's teal), pale stones
/// with a dark navy outline like RFA's joint rings, and the shared amber accent for the capstone.
///
/// Small sizes (24 px and under) drop to three stones; up to 48 px every stone edge is snapped to the
/// final pixel grid so the outlines stay one crisp pixel between stones. Small sizes are stored as
/// 32-bit BMP and large ones as PNG. Each size is rasterised at 4x and box-filtered down.
/// </summary>
internal static class Program
{
    /// <summary>Sizes stored as BMP, then sizes stored as PNG.</summary>
    private static readonly int[] BmpSizes = [16, 20, 24, 32, 40, 48];

    private static readonly int[] PngSizes = [64, 128, 256];

    private const int Supersample = 4;

    private static int Main(string[] args)
    {
        string output = args.Length > 0
            ? args[0]
            : Path.Combine("src", "Cairn.Shell", "app.ico");

        string? folder = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var entries = new List<(int Size, byte[] Data, bool IsPng)>();
        foreach (int size in BmpSizes) entries.Add((size, EncodeBmp(Render(size)), false));
        foreach (int size in PngSizes) entries.Add((size, EncodePng(Render(size)), true));

        WriteIcon(output, entries);
        if (args.Length > 1) WritePreviews(args[1]);
        Console.WriteLine(
            $"Wrote {output} ({new FileInfo(output).Length:N0} bytes, {entries.Count} sizes: "
            + string.Join(", ", entries.Select(e => e.Size)) + ")");
        return 0;
    }

    /// <summary>
    /// Writes a preview strip of every size to <paramref name="stripPath"/>, plus the 256 px and 16 px
    /// renders and the 16 px render scaled 8x without smoothing beside it, for eyeballing.
    /// </summary>
    private static void WritePreviews(string stripPath)
    {
        string full = Path.GetFullPath(stripPath);
        string? folder = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string stem = Path.Combine(folder ?? "", Path.GetFileNameWithoutExtension(full));

        File.WriteAllBytes(full, EncodePng(PreviewStrip()));
        File.WriteAllBytes(stem + "-256.png", EncodePng(Render(256)));
        var small = Render(16);
        File.WriteAllBytes(stem + "-16.png", EncodePng(small));
        File.WriteAllBytes(stem + "-16x8.png", EncodePng(Scale(small, 8)));
    }

    // ── Drawing ───────────────────────────────────────────────────────────────

    /// <summary>A straight BGRA buffer; the tool's whole drawing surface.</summary>
    private sealed class Canvas(int width, int height)
    {
        public Canvas(int size) : this(size, size) { }

        public int Width { get; } = width;

        public int Height { get; } = height;

        public int Size => Width;

        public byte[] Pixels { get; } = new byte[width * height * 4];

        public void Blend(int x, int y, double r, double g, double b, double a)
        {
            if (a <= 0 || (uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            int i = (y * Width + x) * 4;
            double dstA = Pixels[i + 3] / 255.0;
            double outA = a + dstA * (1 - a);
            if (outA <= 0) return;
            Pixels[i] = (byte)Math.Round((b * a + Pixels[i] / 255.0 * dstA * (1 - a)) / outA * 255);
            Pixels[i + 1] = (byte)Math.Round((g * a + Pixels[i + 1] / 255.0 * dstA * (1 - a)) / outA * 255);
            Pixels[i + 2] = (byte)Math.Round((r * a + Pixels[i + 2] / 255.0 * dstA * (1 - a)) / outA * 255);
            Pixels[i + 3] = (byte)Math.Round(outA * 255);
        }
    }

    /// <summary>The dark navy every stone is outlined in (RFA's joint ring, a shade deeper).</summary>
    private static readonly (double R, double G, double B) Outline = (0.04, 0.15, 0.30);

    /// <summary>Faint ground line under the cairn (RFA's timeline rail colour).</summary>
    private static readonly (double R, double G, double B) Rail = (0.55, 0.78, 0.92);

    /// <summary>Stone colours from the bottom up; the last is the amber capstone.</summary>
    private static readonly (double R, double G, double B)[] StoneColours4 =
    [
        (0.70, 0.84, 0.95),
        (0.95, 0.97, 1.00),
        (0.80, 0.90, 0.98),
        (0.98, 0.66, 0.20),
    ];

    private static readonly (double R, double G, double B)[] StoneColours3 =
    [
        (0.58, 0.67, 0.78),
        (0.97, 0.98, 1.00),
        (0.98, 0.66, 0.20),
    ];

    /// <summary>Tilt of each stone in degrees, bottom up (large sizes only; positive is clockwise).</summary>
    private static readonly double[] StoneTilts = [0, -3, 4, -4];

    /// <summary>One stone, as fractions of the icon: centre x, width, height.</summary>
    private readonly record struct StoneSpec(double CentreX, double Width, double Height);

    private static readonly StoneSpec[] Stones4 =
    [
        new(0.50, 0.70, 0.22),
        new(0.45, 0.54, 0.19),
        new(0.54, 0.42, 0.17),
        new(0.48, 0.30, 0.15),
    ];

    private static readonly StoneSpec[] Stones3 =
    [
        new(0.50, 0.75, 0.31),
        new(0.46, 0.56, 0.30),
        new(0.55, 0.31, 0.30),
    ];

    /// <summary>Renders the icon at one size, by way of a 4x buffer.</summary>
    private static Canvas Render(int size)
    {
        int big = size * Supersample;
        var canvas = new Canvas(big);
        double s = big;
        bool tiny = size <= 24;
        bool snap = size <= 48;

        // Rounded-square ground: a vertical gradient from deep blue to teal, between ATX's and RFA's.
        RoundedRect(canvas, 0.02 * s, 0.02 * s, 0.96 * s, 0.96 * s, 0.20 * s,
            (_, y) =>
            {
                double t = Math.Clamp((y - 0.02 * s) / (0.96 * s), 0, 1);
                return (Lerp(0.08, 0.12, t), Lerp(0.27, 0.50, t), Lerp(0.56, 0.72, t), 1.0);
            });

        StoneSpec[] specs = tiny ? Stones3 : Stones4;
        var colours = tiny ? StoneColours3 : StoneColours4;

        // Outline width in final pixels: one crisp pixel when small, about 3.5% of the icon when large.
        double outline = snap ? Math.Max(1, Math.Round(0.04 * size)) : 0.03 * size;

        // Lay the stones out in final pixels (snapped to whole pixels at small sizes), each one
        // overlapping the one below by an outline width so neighbours share a single outline.
        var boxes = new (double X0, double Y0, double X1, double Y1)[specs.Length];
        double total = specs.Sum(p => Px(p.Height * size, snap)) - outline * (specs.Length - 1);
        double bottom = Px(0.50 * size + total / 2 + (tiny ? 0 : 0.03 * size), snap);
        for (int i = 0; i < specs.Length; i++)
        {
            double w = Px(specs[i].Width * size, snap), h = Px(specs[i].Height * size, snap);
            double x0 = Px(specs[i].CentreX * size - w / 2, snap);
            boxes[i] = (x0, bottom - h, x0 + w, bottom);
            bottom = bottom - h + outline;
        }

        // A faint ground line for the cairn to stand on, at sizes big enough to carry it.
        if (size >= 48)
        {
            double groundY = (boxes[0].Y1 - outline / 2) * Supersample;
            Capsule(canvas, 0.10 * s, groundY, 0.90 * s, groundY, 0.022 * s, Rail, 0.55);
        }

        double exponent = tiny ? 2.6 : 2.2;
        for (int i = 0; i < boxes.Length; i++)
        {
            var (x0, y0, x1, y1) = boxes[i];
            var c = colours[i];
            // Hand-stacked stones sit a little askew; only where there are pixels enough to show it.
            double tilt = snap ? 0 : StoneTilts[i] * Math.PI / 180;
            Superellipse(canvas, x0 * Supersample, y0 * Supersample, x1 * Supersample, y1 * Supersample, exponent, tilt,
                (_, _) => (Outline.R, Outline.G, Outline.B, 1.0));

            double ix0 = (x0 + outline) * Supersample, iy0 = (y0 + outline) * Supersample;
            double ix1 = (x1 - outline) * Supersample, iy1 = (y1 - outline) * Supersample;
            // Flat shading at larger sizes: the lower third of each stone a step darker.
            double shadeFrom = size >= 48 ? Lerp(iy0, iy1, 0.62) : double.MaxValue;
            // At tiny sizes the fill is nearly square so its corner pixels stay solid inside the round outline.
            Superellipse(canvas, ix0, iy0, ix1, iy1, tiny ? 6 : exponent, tilt,
                (_, y) => y >= shadeFrom
                    ? (c.R * 0.84, c.G * 0.86, c.B * 0.90, 1.0)
                    : (c.R, c.G, c.B, 1.0));
        }

        return Downsample(canvas, size);
    }

    /// <summary>Rounds to a whole pixel when snapping.</summary>
    private static double Px(double value, bool snap) => snap ? Math.Round(value, MidpointRounding.AwayFromZero) : value;

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Fills a rounded rectangle with a hard inside test (the 4x supersampling anti-aliases).</summary>
    private static void RoundedRect(
        Canvas canvas, double x, double y, double width, double height, double radius,
        Func<double, double, (double R, double G, double B, double A)> colour)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        int x0 = (int)Math.Floor(x), x1 = (int)Math.Ceiling(x + width);
        int y0 = (int)Math.Floor(y), y1 = (int)Math.Ceiling(y + height);
        for (int py = y0; py < y1; py++)
        {
            for (int px = x0; px < x1; px++)
            {
                double cx = px + 0.5, cy = py + 0.5;
                if (cx < x || cx > x + width || cy < y || cy > y + height) continue;
                double dx = cx < x + radius ? x + radius - cx : cx > x + width - radius ? cx - (x + width - radius) : 0;
                double dy = cy < y + radius ? y + radius - cy : cy > y + height - radius ? cy - (y + height - radius) : 0;
                if (dx > 0 && dy > 0 && dx * dx + dy * dy > radius * radius) continue;
                var (r, g, b, a) = colour(cx, cy);
                canvas.Blend(px, py, r, g, b, a);
            }
        }
    }

    /// <summary>
    /// Fills the superellipse |x/a|^n + |y/b|^n &lt;= 1 inscribed in a box: a pebble that is rounder
    /// than a rounded rectangle but flatter-sided than an ellipse. The shape is turned by
    /// <paramref name="tilt"/> radians about the box centre, and <paramref name="colour"/> is given
    /// the point in the stone's own (untilted) frame, so shading turns with the stone.
    /// </summary>
    private static void Superellipse(
        Canvas canvas, double x0, double y0, double x1, double y1, double exponent, double tilt,
        Func<double, double, (double R, double G, double B, double A)> colour)
    {
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, a = (x1 - x0) / 2, b = (y1 - y0) / 2;
        if (a <= 0 || b <= 0) return;
        double cos = Math.Cos(tilt), sin = Math.Sin(tilt);
        double reachX = Math.Abs(a * cos) + Math.Abs(b * sin), reachY = Math.Abs(a * sin) + Math.Abs(b * cos);
        for (int py = (int)Math.Floor(cy - reachY); py < (int)Math.Ceiling(cy + reachY); py++)
        {
            for (int px = (int)Math.Floor(cx - reachX); px < (int)Math.Ceiling(cx + reachX); px++)
            {
                double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                double lx = dx * cos + dy * sin, ly = -dx * sin + dy * cos;
                if (Math.Pow(Math.Abs(lx) / a, exponent) + Math.Pow(Math.Abs(ly) / b, exponent) > 1) continue;
                var (r, g, bl, al) = colour(cx + lx, cy + ly);
                canvas.Blend(px, py, r, g, bl, al);
            }
        }
    }

    /// <summary>A thick line with round caps: every pixel within <paramref name="halfWidth"/> of the segment.</summary>
    private static void Capsule(
        Canvas canvas, double ax, double ay, double bx, double by, double halfWidth,
        (double R, double G, double B) c, double alpha)
    {
        int x0 = (int)Math.Floor(Math.Min(ax, bx) - halfWidth), x1 = (int)Math.Ceiling(Math.Max(ax, bx) + halfWidth);
        int y0 = (int)Math.Floor(Math.Min(ay, by) - halfWidth), y1 = (int)Math.Ceiling(Math.Max(ay, by) + halfWidth);
        double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
        for (int py = y0; py < y1; py++)
        {
            for (int px = x0; px < x1; px++)
            {
                double qx = px + 0.5 - ax, qy = py + 0.5 - ay;
                double t = len2 > 0 ? Math.Clamp((qx * vx + qy * vy) / len2, 0, 1) : 0;
                double dx = qx - t * vx, dy = qy - t * vy;
                if (dx * dx + dy * dy <= halfWidth * halfWidth) canvas.Blend(px, py, c.R, c.G, c.B, alpha);
            }
        }
    }

    /// <summary>Box-filters the 4x buffer down to the final size.</summary>
    private static Canvas Downsample(Canvas source, int size)
    {
        var result = new Canvas(size);
        int factor = source.Size / size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (int sy = 0; sy < factor; sy++)
                {
                    for (int sx = 0; sx < factor; sx++)
                    {
                        int i = ((y * factor + sy) * source.Size + x * factor + sx) * 4;
                        double pixelAlpha = source.Pixels[i + 3] / 255.0;
                        // Premultiply while averaging, or transparent pixels darken the edges.
                        b += source.Pixels[i] * pixelAlpha;
                        g += source.Pixels[i + 1] * pixelAlpha;
                        r += source.Pixels[i + 2] * pixelAlpha;
                        a += pixelAlpha;
                    }
                }
                int samples = factor * factor;
                int o = (y * size + x) * 4;
                if (a > 0)
                {
                    result.Pixels[o] = (byte)Math.Clamp(Math.Round(b / a), 0, 255);
                    result.Pixels[o + 1] = (byte)Math.Clamp(Math.Round(g / a), 0, 255);
                    result.Pixels[o + 2] = (byte)Math.Clamp(Math.Round(r / a), 0, 255);
                }
                result.Pixels[o + 3] = (byte)Math.Clamp(Math.Round(a / samples * 255), 0, 255);
            }
        }
        return result;
    }

    /// <summary>Scales a canvas up by a whole factor without smoothing.</summary>
    private static Canvas Scale(Canvas source, int factor)
    {
        var result = new Canvas(source.Width * factor, source.Height * factor);
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                int i = ((y / factor) * source.Width + x / factor) * 4, o = (y * result.Width + x) * 4;
                Array.Copy(source.Pixels, i, result.Pixels, o, 4);
            }
        }
        return result;
    }

    /// <summary>Every size side by side (each scaled up 4x without smoothing), for review.</summary>
    private static Canvas PreviewStrip()
    {
        int[] sizes = [16, 20, 24, 32, 48, 256];
        int width = sizes.Sum(s => s == 256 ? s : s * 4) + 8 * sizes.Length;
        var strip = new Canvas(width, 256);
        int x = 0;
        foreach (int size in sizes)
        {
            var icon = Render(size);
            int scale = size == 256 ? 1 : 4;
            for (int y = 0; y < size * scale; y++)
            {
                for (int xx = 0; xx < size * scale; xx++)
                {
                    int i = ((y / scale) * size + xx / scale) * 4;
                    strip.Blend(x + xx, y, icon.Pixels[i + 2] / 255.0, icon.Pixels[i + 1] / 255.0, icon.Pixels[i] / 255.0, icon.Pixels[i + 3] / 255.0);
                }
            }
            x += size * scale + 8;
        }
        return strip;
    }

    // ── Encoding ──────────────────────────────────────────────────────────────

    /// <summary>A 32-bit BMP payload for an .ico entry: header, bottom-up BGRA, empty AND mask.</summary>
    private static byte[] EncodeBmp(Canvas canvas)
    {
        int size = canvas.Size;
        int maskStride = (size + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(size * size * 4 + maskStride * size);
        writer.Write(0); writer.Write(0);
        writer.Write(0); writer.Write(0);
        for (int y = size - 1; y >= 0; y--) writer.Write(canvas.Pixels, y * size * 4, size * 4);
        writer.Write(new byte[maskStride * size]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] EncodePng(Canvas canvas)
    {
        var bitmap = BitmapSource.Create(
            canvas.Width, canvas.Height, 96, 96, PixelFormats.Bgra32, null, canvas.Pixels, canvas.Width * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WriteIcon(string path, IReadOnlyList<(int Size, byte[] Data, bool IsPng)> entries)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)entries.Count);
        int offset = 6 + entries.Count * 16;
        foreach (var (size, data, _) in entries)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data, _) in entries) writer.Write(data);
    }
}
