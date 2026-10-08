using Cairn.Formats.Imaging;

namespace Cairn.Vbm;

/// <summary>
/// Edits of a <see cref="VbmFile"/>. Each returns a new file (the input is never changed) so the result can be pushed onto
/// an undo history; frames that are not touched keep their exact bytes.
/// </summary>
public static class VbmEditing
{
    /// <summary>The highest frame rate the editor accepts.</summary>
    public const int MaxFps = 1000;

    /// <summary>The file with another frame rate.</summary>
    public static VbmFile WithFps(VbmFile file, int fps)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (fps is < 0 or > MaxFps) throw new ArgumentOutOfRangeException(nameof(fps));
        return fps == file.Fps ? file : file with { Fps = fps };
    }

    /// <summary>
    /// Encodes <paramref name="image"/> as a frame of <paramref name="file"/>: fitted to the file's size when it differs
    /// (with <paramref name="resize"/>, high quality stretched when null), converted to its pixel format, with the same
    /// number of mip levels.
    /// </summary>
    public static VbmFrame EncodeFrame(VbmFile file, BgraImage image, VbmResizeOptions? resize = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(image);
        return VbmEncoder.EncodeFrame(VbmResize.Fit(image, file.Width, file.Height, resize), file.Format, file.Version, file.MipLevels);
    }

    /// <summary>The file with frame <paramref name="index"/> replaced by <paramref name="image"/> (see <see cref="EncodeFrame"/>).</summary>
    public static VbmFile ReplaceFrame(VbmFile file, int index, BgraImage image, VbmResizeOptions? resize = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        if ((uint)index >= (uint)file.FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        var frames = file.Frames.ToArray();
        frames[index] = EncodeFrame(file, image, resize);
        return file with { Frames = frames };
    }

    /// <summary>The file with <paramref name="images"/> inserted as frames before <paramref name="index"/> (the count appends).</summary>
    public static VbmFile InsertFrames(VbmFile file, int index, IReadOnlyList<BgraImage> images, VbmResizeOptions? resize = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(images);
        if (index < 0 || index > file.FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (images.Count == 0) return file;
        var frames = file.Frames.ToList();
        frames.InsertRange(index, images.Select(i => EncodeFrame(file, i, resize)));
        return file with { Frames = frames };
    }

    /// <summary>
    /// True when frames of <paramref name="source"/> can go into <paramref name="target"/> byte for byte: the same size,
    /// pixel format, version and mip levels.
    /// </summary>
    public static bool SameLayout(VbmFile source, VbmFile target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return source.Width == target.Width && source.Height == target.Height && source.Format == target.Format
            && source.Version == target.Version && source.MipLevels == target.MipLevels;
    }

    /// <summary>
    /// The file with the frames at <paramref name="indices"/> of <paramref name="source"/> inserted before
    /// <paramref name="index"/>: their exact data when both files have the same layout (<see cref="SameLayout"/>), else
    /// each frame's full-size image fitted and encoded as for <see cref="InsertFrames"/>.
    /// </summary>
    public static VbmFile InsertFramesFrom(VbmFile file, int index, VbmFile source, IReadOnlyList<int> indices, VbmResizeOptions? resize = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(indices);
        if (index < 0 || index > file.FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        var pick = indices.Where(i => i >= 0 && i < source.FrameCount).ToList();
        if (pick.Count == 0) return file;
        if (!SameLayout(source, file)) return InsertFrames(file, index, [.. pick.Select(i => source.Decode(i))], resize);
        var frames = file.Frames.ToList();
        frames.InsertRange(index, pick.Select(i => source.Frames[i]));
        return file with { Frames = frames };
    }

    /// <summary>
    /// The file with the frames at <paramref name="indices"/> moved, as a block in their order, to just before frame
    /// <paramref name="before"/> (an index in the file as it is; the frame count drops them at the end).
    /// <paramref name="moved"/> receives their new positions. Unchanged (the same instance) when nothing moves.
    /// </summary>
    public static VbmFile MoveFramesTo(VbmFile file, IEnumerable<int> indices, int before, out IReadOnlyList<int> moved)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (before < 0 || before > file.FrameCount) throw new ArgumentOutOfRangeException(nameof(before));
        var pick = indices.Where(i => i >= 0 && i < file.FrameCount).Distinct().Order().ToList();
        moved = pick;
        if (pick.Count == 0) return file;
        var set = pick.ToHashSet();
        var rest = Enumerable.Range(0, file.FrameCount).Where(i => !set.Contains(i)).ToList();
        int at = before - pick.Count(i => i < before);
        var order = new List<int>(rest);
        order.InsertRange(at, pick);
        moved = [.. Enumerable.Range(at, pick.Count)];
        return Reorder(file, order);
    }

    /// <summary>The file without the frames at <paramref name="indices"/>. At least one frame must remain.</summary>
    public static VbmFile RemoveFrames(VbmFile file, IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(file);
        var drop = indices.Where(i => i >= 0 && i < file.FrameCount).ToHashSet();
        if (drop.Count == 0) return file;
        if (drop.Count >= file.FrameCount) throw new InvalidOperationException("A bitmap needs at least one frame.");
        return file with { Frames = [.. file.Frames.Where((_, i) => !drop.Contains(i))] };
    }

    /// <summary>The file with copies of the frames at <paramref name="indices"/> inserted after the last of them.</summary>
    public static VbmFile DuplicateFrames(VbmFile file, IEnumerable<int> indices, out IReadOnlyList<int> copies)
    {
        ArgumentNullException.ThrowIfNull(file);
        var pick = indices.Where(i => i >= 0 && i < file.FrameCount).Distinct().Order().ToList();
        copies = [];
        if (pick.Count == 0) return file;
        var frames = file.Frames.ToList();
        int at = pick[^1] + 1;
        frames.InsertRange(at, pick.Select(i => file.Frames[i]));
        copies = [.. Enumerable.Range(at, pick.Count)];
        return file with { Frames = frames };
    }

    /// <summary>
    /// The file with the frames at <paramref name="indices"/> moved one place earlier (<paramref name="delta"/> -1) or later
    /// (+1), as a block that keeps its order; <paramref name="moved"/> receives their new positions. Unchanged (the same
    /// instance) when the block is already at that end.
    /// </summary>
    public static VbmFile MoveFrames(VbmFile file, IEnumerable<int> indices, int delta, out IReadOnlyList<int> moved)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (delta is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(delta));
        var pick = indices.Where(i => i >= 0 && i < file.FrameCount).Distinct().Order().ToList();
        moved = pick;
        if (pick.Count == 0 || (delta < 0 ? pick[0] == 0 : pick[^1] == file.FrameCount - 1)) return file;
        var order = Enumerable.Range(0, file.FrameCount).ToList();
        var set = pick.ToHashSet();
        if (delta < 0)
        {
            for (int i = 1; i < order.Count; i++)
                if (set.Contains(order[i]) && !set.Contains(order[i - 1])) (order[i - 1], order[i]) = (order[i], order[i - 1]);
        }
        else
        {
            for (int i = order.Count - 2; i >= 0; i--)
                if (set.Contains(order[i]) && !set.Contains(order[i + 1])) (order[i + 1], order[i]) = (order[i], order[i + 1]);
        }
        moved = [.. pick.Select(p => order.IndexOf(p))];
        return Reorder(file, order);
    }

    /// <summary>The file with its frames in <paramref name="order"/> (a permutation of the frame indices).</summary>
    public static VbmFile Reorder(VbmFile file, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(order);
        if (order.Count != file.FrameCount || order.Distinct().Count() != order.Count || order.Any(i => i < 0 || i >= file.FrameCount))
            throw new ArgumentException("The order must name every frame once.", nameof(order));
        if (order.Select((v, i) => v == i).All(same => same)) return file;
        return file with { Frames = [.. order.Select(i => file.Frames[i])] };
    }

    /// <summary>The file with its frames in reverse order.</summary>
    public static VbmFile Reverse(VbmFile file) => Reorder(file, [.. Enumerable.Range(0, file.FrameCount).Reverse()]);

    /// <summary>
    /// The file in another pixel format: every frame's level 0 decoded and encoded again (lossy when the new format has
    /// fewer bits for a channel), mips rebuilt from it. Version follows <see cref="VbmPixelFormats.DefaultVersion"/>.
    /// </summary>
    public static VbmFile ConvertFormat(VbmFile file, VbmPixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (format == file.Format) return file;
        uint version = format.DefaultVersion();
        var frames = Enumerable.Range(0, file.FrameCount)
            .Select(i => VbmEncoder.EncodeFrame(file.Decode(i), format, version, file.MipLevels)).ToArray();
        return file with { Format = format, Version = version, Frames = frames };
    }

    /// <summary>The file with <paramref name="levels"/> mip levels per frame, rebuilt from each frame's level 0.</summary>
    public static VbmFile WithMipLevels(VbmFile file, int levels)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (levels < 1 || levels > VbmFile.MaxMipLevels(file.Width, file.Height)) throw new ArgumentOutOfRangeException(nameof(levels));
        if (levels == file.MipLevels) return file;
        var frames = file.Frames.Select(f => levels < f.Levels.Count
            ? new VbmFrame([.. f.Levels.Take(levels)])
            : VbmEncoder.EncodeFrame(VbmEncoder.DecodeLevel(f.Levels[0], file.Width, file.Height, file.Format, file.Version),
                file.Format, file.Version, levels)).ToArray();
        return file with { MipLevels = levels, Frames = frames };
    }

    /// <summary>
    /// A new bitmap from <paramref name="images"/> (one frame each, in order). The size is <paramref name="width"/> x
    /// <paramref name="height"/>, or the first image's when those are 0; other images are resized to it.
    /// </summary>
    public static VbmFile Create(IReadOnlyList<BgraImage> images, VbmPixelFormat format, int fps, int mipLevels, int width = 0, int height = 0,
        VbmResizeOptions? resize = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0) throw new ArgumentException("A bitmap needs at least one image.", nameof(images));
        if (width <= 0 || height <= 0) (width, height) = (images[0].Width, images[0].Height);
        if (fps is < 0 or > MaxFps) throw new ArgumentOutOfRangeException(nameof(fps));
        mipLevels = Math.Clamp(mipLevels, 1, VbmFile.MaxMipLevels(width, height));
        var file = new VbmFile(format.DefaultVersion(), width, height, format, fps, mipLevels, [], []);
        return file with { Frames = [.. images.Select(i => EncodeFrame(file, i, resize))] };
    }

    /// <summary>
    /// The pixel format a set of images suits: 565 when every pixel is opaque, 1555 when alpha is only ever fully on or
    /// off, else 4444.
    /// </summary>
    public static VbmPixelFormat SuggestFormat(IEnumerable<BgraImage> images)
    {
        bool partial = false, any = false;
        foreach (var image in images)
        {
            var p = image.Pixels;
            for (int i = 3; i < p.Length; i += 4)
            {
                if (p[i] == 255) continue;
                any = true;
                if (p[i] != 0) { partial = true; break; }
            }
            if (partial) break;
        }
        return partial ? VbmPixelFormat.Argb4444 : any ? VbmPixelFormat.Argb1555 : VbmPixelFormat.Rgb565;
    }
}
