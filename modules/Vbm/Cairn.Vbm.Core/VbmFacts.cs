using System.Globalization;
using Cairn.Formats.Imaging;

namespace Cairn.Vbm;

/// <summary>One line of the facts panel.</summary>
/// <param name="Label">"Size".</param>
/// <param name="Value">"128 x 128".</param>
/// <param name="ToolTip">More detail, or null.</param>
public sealed record VbmFact(string Label, string Value, string? ToolTip = null);

/// <summary>What the document's facts panel lists about a bitmap.</summary>
public static class VbmFacts
{
    /// <summary>The facts about <paramref name="file"/>; <paramref name="fileSize"/> is the size on disk (null when unsaved).</summary>
    public static IReadOnlyList<VbmFact> For(VbmFile file, long? fileSize)
    {
        ArgumentNullException.ThrowIfNull(file);
        var c = CultureInfo.CurrentCulture;
        var rows = new List<VbmFact>
        {
            new("Size", string.Format(c, "{0} x {1}", file.Width, file.Height)),
            new("Format", file.Format.DisplayName(), FormatTip(file.Format)),
            new("Version", file.Version.ToString(c), file.Format == VbmPixelFormat.Argb1555
                ? file.Version < VbmCodec.FirstStandardAlphaVersion ? "Version 1: the 1555 alpha bit is stored inverted (set = transparent)." : "The 1555 alpha bit is stored the standard way (set = opaque)."
                : "The container version. It only changes how 1555 alpha is stored."),
            new("Frames", file.FrameCount.ToString("N0", c)),
        };
        if (file.IsAnimated)
        {
            rows.Add(new("Frame rate", string.Format(c, "{0} fps", file.Fps)));
            if (file.Fps > 0)
                rows.Add(new("Length", string.Format(c, "{0:0.##} s per loop", file.FrameCount / (double)file.Fps), "Frames divided by the frame rate."));
        }
        else
        {
            rows.Add(new("Frame rate", string.Format(c, "{0} fps (not used: one frame)", file.Fps)));
        }
        var (lw, lh) = file.LevelSize(file.MipLevels - 1);
        rows.Add(new("Mip levels", file.MipLevels == 1 ? "1 (no mipmaps)" : string.Format(c, "{0} ({1} x {2} down to {3} x {4})", file.MipLevels, file.Width, file.Height, lw, lh),
            "Smaller copies of each frame the renderer uses at a distance."));
        rows.Add(new("Pixel data", Size(file.ByteCount - VbmFile.HeaderSize), string.Format(c, "{0:N0} bytes per frame", file.Frames[0].ByteCount)));
        rows.Add(new("File size", fileSize is { } size ? Size(size) : Size(file.ByteCount) + " when saved",
            fileSize is { } s ? string.Format(c, "{0:N0} bytes", s) : null));
        return rows;
    }

    private static string FormatTip(VbmPixelFormat format) => format switch
    {
        VbmPixelFormat.Argb1555 => "5 bits per colour channel; each pixel is fully opaque or fully transparent.",
        VbmPixelFormat.Argb4444 => "4 bits per channel including alpha: 16 levels of transparency, coarser colour.",
        _ => "5 bits red, 6 green, 5 blue; no transparency.",
    };

    /// <summary>"12.5 KB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", bytes),
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024.0),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.##} MB", bytes / (1024.0 * 1024)),
    };
}
