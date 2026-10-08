using Cairn.Formats.Imaging;

namespace Cairn.Vbm;

/// <summary>How an image is resampled when it is not the bitmap's size.</summary>
public enum VbmResizeFilter
{
    /// <summary>Each pixel copies the nearest source pixel: hard edges, for pixel art.</summary>
    Nearest,
    /// <summary>Linear blend of the neighbouring pixels (a tent filter when shrinking).</summary>
    Bilinear,
    /// <summary>Lanczos: the sharpest smooth result (the default).</summary>
    HighQuality,
}

/// <summary>How an image of another shape is fitted to the bitmap's size.</summary>
public enum VbmFitMode
{
    /// <summary>Scaled to exactly the bitmap's size (the shape may change).</summary>
    Stretch,
    /// <summary>Scaled to fit inside, keeping its shape; the rest is transparent.</summary>
    KeepAspect,
    /// <summary>Scaled to cover, keeping its shape; what is outside the bitmap is cut off evenly on both sides.</summary>
    CropCentre,
}

/// <summary>A resize filter and a fit.</summary>
public sealed record VbmResizeOptions(VbmResizeFilter Filter, VbmFitMode Fit)
{
    /// <summary>High quality, stretched (what an edit uses when nothing else is said).</summary>
    public static VbmResizeOptions Default { get; } = new(VbmResizeFilter.HighQuality, VbmFitMode.Stretch);
}

/// <summary>Where the source rectangle lands in the target, in whole pixels.</summary>
/// <param name="SourceX">Left of the part of the source used.</param>
/// <param name="SourceY">Top of the part of the source used.</param>
/// <param name="SourceWidth">Width of the part of the source used.</param>
/// <param name="SourceHeight">Height of the part of the source used.</param>
/// <param name="TargetX">Left of where it is drawn in the target.</param>
/// <param name="TargetY">Top of where it is drawn in the target.</param>
/// <param name="TargetWidth">Width it is drawn at.</param>
/// <param name="TargetHeight">Height it is drawn at.</param>
public readonly record struct VbmFitPlacement(int SourceX, int SourceY, int SourceWidth, int SourceHeight,
    int TargetX, int TargetY, int TargetWidth, int TargetHeight);

/// <summary>Resizing and fitting of images to a bitmap's frame size.</summary>
public static class VbmResize
{
    /// <summary>
    /// Where a <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> image goes in a <paramref name="width"/> x
    /// <paramref name="height"/> frame for <paramref name="fit"/>. Stretch uses all of both; keep aspect uses all of the
    /// source, centred in the largest same-shaped rectangle that fits (at least 1 pixel each way); crop centre uses the
    /// largest centred part of the source with the frame's shape, drawn over the whole frame.
    /// </summary>
    public static VbmFitPlacement Place(int sourceWidth, int sourceHeight, int width, int height, VbmFitMode fit)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        switch (fit)
        {
            case VbmFitMode.KeepAspect:
            {
                // compare sw/sh with w/h without rounding: sw*h vs w*sh
                long wide = (long)sourceWidth * height, tall = (long)width * sourceHeight;
                int tw = width, th = height;
                if (wide > tall) th = Math.Clamp((int)Math.Round((double)sourceHeight * width / sourceWidth), 1, height);
                else if (wide < tall) tw = Math.Clamp((int)Math.Round((double)sourceWidth * height / sourceHeight), 1, width);
                return new(0, 0, sourceWidth, sourceHeight, (width - tw) / 2, (height - th) / 2, tw, th);
            }
            case VbmFitMode.CropCentre:
            {
                long wide = (long)sourceWidth * height, tall = (long)width * sourceHeight;
                int sw = sourceWidth, sh = sourceHeight;
                if (wide > tall) sw = Math.Clamp((int)Math.Round((double)sourceHeight * width / height), 1, sourceWidth);
                else if (wide < tall) sh = Math.Clamp((int)Math.Round((double)sourceWidth * height / width), 1, sourceHeight);
                return new((sourceWidth - sw) / 2, (sourceHeight - sh) / 2, sw, sh, 0, 0, width, height);
            }
            default:
                return new(0, 0, sourceWidth, sourceHeight, 0, 0, width, height);
        }
    }

    /// <summary>
    /// <paramref name="image"/> fitted to <paramref name="width"/> x <paramref name="height"/> with
    /// <paramref name="options"/> (<see cref="VbmResizeOptions.Default"/> when null). An image that already has that size
    /// is returned as is.
    /// </summary>
    public static BgraImage Fit(BgraImage image, int width, int height, VbmResizeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width == width && image.Height == height) return image;
        options ??= VbmResizeOptions.Default;
        var p = Place(image.Width, image.Height, width, height, options.Fit);
        var part = p.SourceX == 0 && p.SourceY == 0 && p.SourceWidth == image.Width && p.SourceHeight == image.Height
            ? image : Crop(image, p.SourceX, p.SourceY, p.SourceWidth, p.SourceHeight);
        var scaled = Resize(part, p.TargetWidth, p.TargetHeight, options.Filter);
        if (p.TargetWidth == width && p.TargetHeight == height) return scaled;
        var result = new BgraImage(width, height); // transparent black around the image
        for (int y = 0; y < p.TargetHeight; y++)
            Buffer.BlockCopy(scaled.Pixels, y * scaled.Stride, result.Pixels, ((p.TargetY + y) * width + p.TargetX) * 4, scaled.Stride);
        return result;
    }

    /// <summary><paramref name="image"/> resampled to <paramref name="width"/> x <paramref name="height"/> with <paramref name="filter"/>.</summary>
    public static BgraImage Resize(BgraImage image, int width, int height, VbmResizeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width == width && image.Height == height) return image;
        return filter switch
        {
            VbmResizeFilter.Nearest => Nearest(image, width, height),
            VbmResizeFilter.Bilinear => ImageResampler.Resize(image, width, height, ResampleFilter.Triangle),
            _ => ImageResampler.Resize(image, width, height, ResampleFilter.Lanczos3),
        };
    }

    /// <summary>The part of <paramref name="image"/> at (<paramref name="x"/>, <paramref name="y"/>), <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static BgraImage Crop(BgraImage image, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > image.Width || y + height > image.Height)
            throw new ArgumentOutOfRangeException(nameof(width));
        var result = new BgraImage(width, height);
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(image.Pixels, ((y + row) * image.Width + x) * 4, result.Pixels, row * width * 4, width * 4);
        return result;
    }

    private static BgraImage Nearest(BgraImage image, int width, int height)
    {
        var result = new BgraImage(width, height);
        var xs = new int[width];
        for (int x = 0; x < width; x++) xs[x] = Math.Min(image.Width - 1, (int)((x + 0.5) * image.Width / width));
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Min(image.Height - 1, (int)((y + 0.5) * image.Height / height));
            int row = sy * image.Width, to = y * width * 4;
            for (int x = 0; x < width; x++)
                Buffer.BlockCopy(image.Pixels, (row + xs[x]) * 4, result.Pixels, to + x * 4, 4);
        }
        return result;
    }

    /// <summary>
    /// The mip levels a new bitmap gets when mipmaps are wanted: halving down to 16 pixels on the shorter side, as the
    /// game's own textures do (1 when the bitmap is already smaller).
    /// </summary>
    public static int DefaultMipLevels(int width, int height)
    {
        int levels = 1, w = width, h = height;
        while (Math.Min(w, h) / 2 >= 16 && levels < VbmFile.MaxMipLevels(width, height))
        {
            w = Math.Max(w / 2, 1);
            h = Math.Max(h / 2, 1);
            levels++;
        }
        return levels;
    }
}
