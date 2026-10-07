namespace Cairn.Formats.Imaging;

/// <summary>
/// Separable resampling of <see cref="BgraImage"/>s for mip levels and power-of-two resizing.
/// Colour is weighted by alpha while filtering so transparent pixels do not bleed their (often
/// black) colour into the edges of cut-outs; the result is straight alpha again.
/// </summary>
public static class ImageResampler
{
    /// <summary>Resizes <paramref name="source"/> to <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static BgraImage Resize(BgraImage source, int width, int height, ResampleFilter filter)
    {
        if (width == source.Width && height == source.Height)
        {
            var copy = new BgraImage(width, height);
            Buffer.BlockCopy(source.Pixels, 0, copy.Pixels, 0, source.Pixels.Length);
            return copy;
        }

        // Premultiplied float working copy, then one pass per axis.
        int sw = source.Width, sh = source.Height;
        var src = new float[sw * sh * 4];
        var p = source.Pixels;
        for (int i = 0; i < sw * sh; i++)
        {
            float a = p[i * 4 + 3] / 255f;
            src[i * 4] = p[i * 4] * a;
            src[i * 4 + 1] = p[i * 4 + 1] * a;
            src[i * 4 + 2] = p[i * 4 + 2] * a;
            src[i * 4 + 3] = a;
        }

        var horizontal = Weights(sw, width, filter);
        var mid = new float[width * sh * 4];
        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (start, w) = horizontal[x];
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    int s = (y * sw + start + k) * 4;
                    b += src[s] * w[k];
                    g += src[s + 1] * w[k];
                    r += src[s + 2] * w[k];
                    a += src[s + 3] * w[k];
                }
                int d = (y * width + x) * 4;
                mid[d] = b; mid[d + 1] = g; mid[d + 2] = r; mid[d + 3] = a;
            }
        }

        var vertical = Weights(sh, height, filter);
        var result = new BgraImage(width, height);
        var o = result.Pixels;
        for (int y = 0; y < height; y++)
        {
            var (start, w) = vertical[y];
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    int s = ((start + k) * width + x) * 4;
                    b += mid[s] * w[k];
                    g += mid[s + 1] * w[k];
                    r += mid[s + 2] * w[k];
                    a += mid[s + 3] * w[k];
                }
                a = Math.Clamp(a, 0f, 1f);
                int d = (y * width + x) * 4;
                if (a <= 0f)
                {
                    o[d] = o[d + 1] = o[d + 2] = o[d + 3] = 0;
                    continue;
                }
                o[d] = ToByte(b / a);
                o[d + 1] = ToByte(g / a);
                o[d + 2] = ToByte(r / a);
                o[d + 3] = ToByte(a * 255f);
            }
        }
        return result;
    }

    /// <summary>The next mip level: each side halved (at least 1).</summary>
    public static BgraImage HalveForMip(BgraImage source, ResampleFilter filter) =>
        Resize(source, Math.Max(source.Width / 2, 1), Math.Max(source.Height / 2, 1), filter);

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    private static (int Start, float[] W)[] Weights(int srcSize, int dstSize, ResampleFilter filter)
    {
        float scale = (float)srcSize / dstSize;
        float stretch = Math.Max(scale, 1f);
        float radius = filter switch
        {
            ResampleFilter.Box => 0.5f,
            ResampleFilter.Triangle => 1f,
            _ => 3f,
        } * stretch;
        var table = new (int, float[])[dstSize];
        for (int i = 0; i < dstSize; i++)
        {
            float center = (i + 0.5f) * scale - 0.5f;
            int lo = Math.Max((int)MathF.Floor(center - radius + 0.5f), 0);
            int hi = Math.Min((int)MathF.Ceiling(center + radius - 0.5f), srcSize - 1);
            if (hi < lo) lo = hi = Math.Clamp((int)MathF.Round(center), 0, srcSize - 1);
            var w = new float[hi - lo + 1];
            float sum = 0;
            for (int k = lo; k <= hi; k++)
            {
                float t = (k - center) / stretch;
                float v = Kernel(filter, t);
                w[k - lo] = v;
                sum += v;
            }
            if (sum == 0)
            {
                // A box narrower than one source pixel (exact centre between two): take the nearest.
                Array.Clear(w);
                w[Math.Clamp((int)MathF.Round(center) - lo, 0, w.Length - 1)] = 1;
                sum = 1;
            }
            for (int k = 0; k < w.Length; k++) w[k] /= sum;
            table[i] = (lo, w);
        }
        return table;
    }

    private static float Kernel(ResampleFilter filter, float t)
    {
        t = Math.Abs(t);
        switch (filter)
        {
            case ResampleFilter.Box:
                return t <= 0.5f ? 1f : 0f;
            case ResampleFilter.Triangle:
                return t < 1f ? 1f - t : 0f;
            default:
                if (t < 1e-6f) return 1f;
                if (t >= 3f) return 0f;
                float x = MathF.PI * t;
                return 3f * MathF.Sin(x) * MathF.Sin(x / 3f) / (x * x);
        }
    }
}
