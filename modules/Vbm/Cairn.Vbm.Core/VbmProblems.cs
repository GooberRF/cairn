using System.Globalization;
using Cairn.Formats.Imaging;

namespace Cairn.Vbm;

/// <summary>How serious a <see cref="VbmProblem"/> is.</summary>
public enum VbmSeverity { Error, Warning, Info }

/// <summary>Something worth telling the user about a VBM.</summary>
/// <param name="Severity">Error: the file is damaged; warning: the game may misbehave; info: worth knowing.</param>
/// <param name="Code">Stable code ("VBM002").</param>
/// <param name="Message">One or two sentences.</param>
public sealed record VbmProblem(VbmSeverity Severity, string Code, string Message);

/// <summary>The checks run on an open VBM.</summary>
public static class VbmChecks
{
    /// <summary>The problem for a file that could not be read at all.</summary>
    public static VbmProblem Unreadable(string reason) => new(VbmSeverity.Error, "VBM001", reason);

    /// <summary>
    /// Problems with <paramref name="file"/>; <paramref name="read"/> adds what only the original bytes show (a file that
    /// stops early, the raw header fields), and is null for a new or edited bitmap.
    /// </summary>
    public static IReadOnlyList<VbmProblem> Check(VbmFile file, VbmReadResult? read)
    {
        ArgumentNullException.ThrowIfNull(file);
        var list = new List<VbmProblem>();
        var c = CultureInfo.CurrentCulture;
        if (read is not null)
        {
            if (read.IsTruncated)
                list.Add(new(VbmSeverity.Error, "VBM002", string.Format(c,
                    "The file stops early: the header declares {0:N0} frames but only {1:N0} are complete ({2:N0} bytes are missing). Saving keeps the complete frames.",
                    read.DeclaredFrameCount, read.File.FrameCount, read.MissingBytes)));
            if (read.RawFrameField <= 0)
                list.Add(new(VbmSeverity.Warning, "VBM009", string.Format(c,
                    "The header's frame count is {0}; it is read as 1 frame. Saving writes 1.", read.RawFrameField)));
            if (read.RawMipField + 1 != file.MipLevels)
                list.Add(new(VbmSeverity.Warning, "VBM004", string.Format(c,
                    "The header's mip field ({0}) does not describe a chain a {1} x {2} image can hold; it is read as {3} level{4}. Saving writes the corrected value.",
                    read.RawMipField, file.Width, file.Height, file.MipLevels, file.MipLevels == 1 ? "" : "s")));
        }
        if (file.Trailing.Length > 0)
            list.Add(new(VbmSeverity.Warning, "VBM003", string.Format(c,
                "{0:N0} bytes follow the last frame. The game ignores them; Cairn keeps them when saving.", file.Trailing.Length)));
        if (file.FrameCount > VbmFile.MaxGameFrames)
            list.Add(new(VbmSeverity.Warning, "VBM005", string.Format(c,
                "{0:N0} frames: the game keeps a bitmap's frame count in one byte, so it shows at most {1}.", file.FrameCount, VbmFile.MaxGameFrames)));
        if (file.IsAnimated && file.Fps <= 0)
            list.Add(new(VbmSeverity.Warning, "VBM007", string.Format(c,
                "The frame rate is {0}: an animated bitmap needs a frame rate of at least 1 to play.", file.Fps)));
        if (file.Version is not (1 or 2))
            list.Add(new(VbmSeverity.Info, "VBM008", string.Format(c,
                "Version {0}: the stock bitmaps are version 1 or 2.", file.Version)));
        if (file.Format == VbmPixelFormat.Argb1555 && file.Version >= VbmCodec.FirstStandardAlphaVersion)
            list.Add(new(VbmSeverity.Info, "VBM010",
                "A version 2 1555 bitmap stores its alpha bit the standard way round. No stock file does this (they are all version 1), so check it in game."));
        if (!IsPowerOfTwo(file.Width) || !IsPowerOfTwo(file.Height))
            list.Add(new(VbmSeverity.Info, "VBM006", string.Format(c,
                "{0} x {1} is not a power of two on each side. Interface images can be any size; textures on level geometry and meshes should be powers of two.",
                file.Width, file.Height)));
        return list;
    }

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;
}
