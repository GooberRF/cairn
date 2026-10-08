using System.Globalization;
using Cairn.Formats.Imaging;
using Cairn.Workspace;

namespace Cairn.Vbm;

/// <summary>The image type frames are exported as.</summary>
public enum VbmExportFormat { Tga, Png }

/// <summary>One file an export writes.</summary>
/// <param name="Frame">The frame (0-based).</param>
/// <param name="Path">Full path of the file.</param>
public sealed record VbmExportFile(int Frame, string Path);

/// <summary>
/// Writes frames of a VBM as image files: <c>name_00.tga</c>, <c>name_01.tga</c>... numbered by the frame's own position
/// counting from 0, as the animated textures module's VBM import names frames (so exporting a selection keeps each frame's
/// number), padded so the names sort in playing order.
/// TGAs keep the alpha channel when the format has one; PNGs always carry it.
/// </summary>
public static class VbmFrameExport
{
    /// <summary>The extension written for <paramref name="format"/>.</summary>
    public static string Extension(VbmExportFormat format) => format == VbmExportFormat.Png ? ".png" : ".tga";

    /// <summary>Digits a frame number gets: enough for the highest frame of the file, at least two.</summary>
    public static int PadWidth(int frameCount) =>
        Math.Max(2, Math.Max(1, frameCount - 1).ToString(CultureInfo.InvariantCulture).Length);

    /// <summary>The default stem: the bitmap's name without its extension, made safe for a file name.</summary>
    public static string DefaultBaseName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Trim();
        var invalid = Path.GetInvalidFileNameChars();
        stem = new string([.. stem.Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch)]);
        return stem.Length == 0 ? "frame" : stem;
    }

    /// <summary>The files an export of <paramref name="frames"/> writes into <paramref name="folder"/>.</summary>
    public static IReadOnlyList<VbmExportFile> Plan(string folder, string baseName, IEnumerable<int> frames, int frameCount, VbmExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        int pad = PadWidth(frameCount);
        string ext = Extension(format);
        return [.. frames.Where(f => f >= 0 && f < frameCount).Distinct().Order()
            .Select(f => new VbmExportFile(f, Path.Combine(folder, baseName + "_" + f.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0') + ext)))];
    }

    /// <summary>The planned files that already exist.</summary>
    public static IReadOnlyList<string> Existing(IEnumerable<VbmExportFile> plan) => [.. plan.Select(p => p.Path).Where(File.Exists)];

    /// <summary>One frame's level 0 as file bytes.</summary>
    public static byte[] Encode(VbmFile file, int frame, VbmExportFormat format)
    {
        var image = file.Decode(frame);
        return format == VbmExportFormat.Png ? PngEncoder.Encode(image) : TgaWriter.Write(image, file.HasAlpha);
    }

    /// <summary>
    /// Writes <paramref name="plan"/> (each file atomically, creating the folder). Any thread. Returns how many files were
    /// written; stops at the first failure by throwing it.
    /// </summary>
    public static int Write(VbmFile file, IReadOnlyList<VbmExportFile> plan, VbmExportFormat format, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(plan);
        int done = 0;
        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetDirectoryName(item.Path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllBytes(item.Path, Encode(file, item.Frame, format));
            done++;
            progress?.Report(done);
        }
        return done;
    }
}
