using System.Buffers.Binary;

namespace Cairn.Formats.Imaging;

/// <summary>The three 16-bit pixel formats a VBM stores; the values are the header's format field.</summary>
public enum VbmPixelFormat
{
    /// <summary>1-bit alpha, 5 bits per colour channel.</summary>
    Argb1555 = 0,
    /// <summary>4 bits per channel, alpha included.</summary>
    Argb4444 = 1,
    /// <summary>No alpha: 5 bits red, 6 green, 5 blue.</summary>
    Rgb565 = 2,
}

/// <summary>Helpers for <see cref="VbmPixelFormat"/>.</summary>
public static class VbmPixelFormats
{
    /// <summary>The engine format the pixels are in.</summary>
    public static EngineFormat ToEngine(this VbmPixelFormat format) => format switch
    {
        VbmPixelFormat.Argb1555 => EngineFormat.Argb1555,
        VbmPixelFormat.Argb4444 => EngineFormat.Argb4444,
        _ => EngineFormat.Rgb565,
    };

    /// <summary>The VBM format for an engine format, or null when a VBM cannot store it.</summary>
    public static VbmPixelFormat? FromEngine(EngineFormat format) => format switch
    {
        EngineFormat.Argb1555 => VbmPixelFormat.Argb1555,
        EngineFormat.Argb4444 => VbmPixelFormat.Argb4444,
        EngineFormat.Rgb565 => VbmPixelFormat.Rgb565,
        _ => null,
    };

    /// <summary>"1555 ARGB (16-bit, 1-bit alpha)".</summary>
    public static string DisplayName(this VbmPixelFormat format) => EngineFormats.DisplayName(format.ToEngine());

    /// <summary>
    /// The container version a new file in <paramref name="format"/> is written with. Every stock 1555 VBM is version 1
    /// (alpha bit stored inverted), so new 1555 files follow them; 4444 and 565 files use version 2, the newest stock one.
    /// </summary>
    public static uint DefaultVersion(this VbmPixelFormat format) => format == VbmPixelFormat.Argb1555 ? 1u : 2u;
}

/// <summary>One frame of a VBM: its mip chain, largest level first, each level the raw little-endian 16-bit pixels.</summary>
public sealed class VbmFrame
{
    /// <param name="levels">The levels' raw bytes (width x height x 2 each), largest first. Not copied: treat as immutable.</param>
    public VbmFrame(IReadOnlyList<byte[]> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0) throw new ArgumentException("A frame needs at least one level.", nameof(levels));
        Levels = levels;
    }

    /// <summary>The mip levels, largest first.</summary>
    public IReadOnlyList<byte[]> Levels { get; }

    /// <summary>Bytes the frame takes in the file.</summary>
    public long ByteCount => Levels.Sum(l => (long)l.Length);
}

/// <summary>What <see cref="VbmFile.Read"/> found besides the frames.</summary>
/// <param name="File">The file as read: every complete frame.</param>
/// <param name="DeclaredFrameCount">The header's frame count (0 and negative read as 1).</param>
/// <param name="RawFrameField">The header's frame field exactly as stored.</param>
/// <param name="RawMipField">The header's mip field exactly as stored.</param>
/// <param name="MissingBytes">Bytes the header implies but the file does not hold (0 for a whole file).</param>
public sealed record VbmReadResult(VbmFile File, int DeclaredFrameCount, int RawFrameField, int RawMipField, long MissingBytes)
{
    /// <summary>True when the file stops before the frames the header declares.</summary>
    public bool IsTruncated => MissingBytes > 0;
}

/// <summary>
/// A whole VBM held as raw pixel data: the header fields and, per frame, every mip level's 16-bit pixels exactly as the
/// file stores them, so writing an unchanged file gives back the same bytes. Immutable: edits make a new instance
/// (<c>with</c>), so it can be the snapshot an undo history keeps.
/// </summary>
/// <param name="Version">Container version; 1 stores the 1555 alpha bit inverted (see <see cref="VbmCodec"/>).</param>
/// <param name="Width">Width of level 0.</param>
/// <param name="Height">Height of level 0.</param>
/// <param name="Format">Pixel format.</param>
/// <param name="Fps">Frames per second (the header field; meaningless for one frame).</param>
/// <param name="MipLevels">Levels per frame, including level 0 (the header stores one less).</param>
/// <param name="Frames">The frames, at least one.</param>
/// <param name="Trailing">Bytes after the last frame, kept so they survive a save (empty in every stock file).</param>
public sealed record VbmFile(uint Version, int Width, int Height, VbmPixelFormat Format, int Fps, int MipLevels,
    IReadOnlyList<VbmFrame> Frames, byte[] Trailing)
{
    /// <summary>The header's size in bytes.</summary>
    public const int HeaderSize = 32;

    /// <summary>
    /// The most frames the game holds for one bitmap: the engine keeps the count in a single byte
    /// (<c>rf::bm::BitmapEntry::num_frames</c>).
    /// </summary>
    public const int MaxGameFrames = 255;

    /// <summary>Number of frames.</summary>
    public int FrameCount => Frames.Count;

    /// <summary>True when there is more than one frame.</summary>
    public bool IsAnimated => Frames.Count > 1;

    /// <summary>True when the pixel format has an alpha channel.</summary>
    public bool HasAlpha => Format != VbmPixelFormat.Rgb565;

    /// <summary>The size the file is written with.</summary>
    public long ByteCount => HeaderSize + Frames.Sum(f => f.ByteCount) + Trailing.Length;

    /// <summary>The size of mip level <paramref name="level"/>.</summary>
    public (int Width, int Height) LevelSize(int level) => LevelSize(Width, Height, level);

    /// <summary>The size of level <paramref name="level"/> of a chain starting at <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static (int Width, int Height) LevelSize(int width, int height, int level)
    {
        for (int i = 0; i < level; i++)
        {
            width = Math.Max(width / 2, 1);
            height = Math.Max(height / 2, 1);
        }
        return (width, height);
    }

    /// <summary>How many levels a chain from <paramref name="width"/> x <paramref name="height"/> down to 1 x 1 has.</summary>
    public static int MaxMipLevels(int width, int height)
    {
        int levels = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(width / 2, 1);
            height = Math.Max(height / 2, 1);
            levels++;
        }
        return levels;
    }

    /// <summary>Decodes one level of one frame to BGRA32.</summary>
    public BgraImage Decode(int frame, int level = 0)
    {
        if ((uint)frame >= (uint)Frames.Count) throw new ArgumentOutOfRangeException(nameof(frame));
        var levels = Frames[frame].Levels;
        if ((uint)level >= (uint)levels.Count) throw new ArgumentOutOfRangeException(nameof(level));
        var (w, h) = LevelSize(level);
        return VbmEncoder.DecodeLevel(levels[level], w, h, Format, Version);
    }

    /// <summary>
    /// Reads a VBM. The header is validated by <see cref="VbmCodec.ReadInfo"/>; frames are taken whole, so a file that
    /// stops early yields the frames it completes (see <see cref="VbmReadResult.MissingBytes"/>).
    /// </summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="name">The file name, for messages.</param>
    /// <exception cref="ImageDecodeException">Not a readable VBM, or not even one frame is complete.</exception>
    public static VbmReadResult Read(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var info = VbmCodec.ReadInfo(bytes, name);
        var format = VbmPixelFormats.FromEngine(info.Format)
            ?? throw new ImageDecodeException($"'{name}' uses a pixel format a VBM cannot hold.");
        int rawFrames = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
        int rawMips = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28));

        long available = bytes.Length - HeaderSize;
        long stride = info.FrameStrideBytes;
        int whole = (int)Math.Min(info.FrameCount, available / Math.Max(1, stride));
        if (whole < 1)
            throw new ImageDecodeException($"'{name}' stops before its first frame is complete ({available:N0} of {stride:N0} bytes).");

        var frames = new VbmFrame[whole];
        int offset = HeaderSize;
        for (int f = 0; f < whole; f++)
        {
            var levels = new byte[info.MipLevels][];
            for (int l = 0; l < info.MipLevels; l++)
            {
                var (w, h) = LevelSize(info.Width, info.Height, l);
                int size = w * h * 2;
                levels[l] = bytes.AsSpan(offset, size).ToArray();
                offset += size;
            }
            frames[f] = new VbmFrame(levels);
        }
        long missing = whole < info.FrameCount ? stride * info.FrameCount - available : 0;
        byte[] trailing = missing > 0 ? [] : bytes.AsSpan(offset).ToArray();
        var file = new VbmFile((uint)info.Version, info.Width, info.Height, format, info.Fps, info.MipLevels, frames, trailing);
        return new VbmReadResult(file, info.FrameCount, rawFrames, rawMips, missing);
    }

    /// <summary>The file's bytes: the header, every frame's mip chain in order, then <see cref="Trailing"/>.</summary>
    /// <exception cref="InvalidOperationException">The frames do not match the header (wrong level count or size).</exception>
    public byte[] Write()
    {
        Validate();
        long total = ByteCount;
        if (total > int.MaxValue) throw new InvalidOperationException("The bitmap is too large to write.");
        var bytes = new byte[total];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, VbmCodec.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], Height);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], (int)Format);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], Fps);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], Frames.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], MipLevels - 1);
        int offset = HeaderSize;
        foreach (var frame in Frames)
        {
            foreach (var level in frame.Levels)
            {
                level.CopyTo(span[offset..]);
                offset += level.Length;
            }
        }
        Trailing.CopyTo(span[offset..]);
        return bytes;
    }

    /// <summary>Checks that every frame carries <see cref="MipLevels"/> levels of the right sizes.</summary>
    /// <exception cref="InvalidOperationException">It does not.</exception>
    public void Validate()
    {
        if (Width < 1 || Height < 1 || Width > EngineFormats.MaxDimension || Height > EngineFormats.MaxDimension)
            throw new InvalidOperationException($"A VBM cannot be {Width} x {Height}.");
        if (MipLevels < 1 || MipLevels > MaxMipLevels(Width, Height))
            throw new InvalidOperationException($"A {Width} x {Height} VBM cannot hold {MipLevels} mip levels.");
        if (Frames.Count < 1) throw new InvalidOperationException("A VBM needs at least one frame.");
        for (int f = 0; f < Frames.Count; f++)
        {
            var levels = Frames[f].Levels;
            if (levels.Count != MipLevels)
                throw new InvalidOperationException($"Frame {f + 1} has {levels.Count} mip levels instead of {MipLevels}.");
            for (int l = 0; l < levels.Count; l++)
            {
                var (w, h) = LevelSize(l);
                if (levels[l].Length != w * h * 2)
                    throw new InvalidOperationException($"Frame {f + 1}, mip level {l} holds {levels[l].Length} bytes instead of {w * h * 2}.");
            }
        }
    }
}

/// <summary>Converts between BGRA32 and a VBM's 16-bit pixels, and builds frames with their mip chains.</summary>
public static class VbmEncoder
{
    /// <summary>Decodes one level's raw pixels to BGRA32 (bit replication, as <see cref="VbmCodec"/> does).</summary>
    /// <param name="data">The level's bytes, width x height x 2.</param>
    /// <param name="width">Level width.</param>
    /// <param name="height">Level height.</param>
    /// <param name="format">Pixel format.</param>
    /// <param name="version">Container version (below 2 the 1555 alpha bit means transparent).</param>
    public static BgraImage DecodeLevel(byte[] data, int width, int height, VbmPixelFormat format, uint version)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < width * height * 2) throw new ImageDecodeException($"The level holds {data.Length} bytes, too few for {width} x {height}.");
        bool inverted = version < VbmCodec.FirstStandardAlphaVersion;
        var image = new BgraImage(width, height);
        var p = image.Pixels;
        for (int i = 0, n = width * height; i < n; i++)
        {
            int v = data[i * 2] | (data[i * 2 + 1] << 8);
            int o = i * 4;
            switch (format)
            {
                case VbmPixelFormat.Argb1555:
                    p[o] = ChannelBits.Expand(v, 5);
                    p[o + 1] = ChannelBits.Expand(v >> 5, 5);
                    p[o + 2] = ChannelBits.Expand(v >> 10, 5);
                    p[o + 3] = ((v & 0x8000) != 0) != inverted ? (byte)255 : (byte)0;
                    break;
                case VbmPixelFormat.Argb4444:
                    p[o] = ChannelBits.Expand(v, 4);
                    p[o + 1] = ChannelBits.Expand(v >> 4, 4);
                    p[o + 2] = ChannelBits.Expand(v >> 8, 4);
                    p[o + 3] = ChannelBits.Expand(v >> 12, 4);
                    break;
                default:
                    p[o] = ChannelBits.Expand(v, 5);
                    p[o + 1] = ChannelBits.Expand(v >> 5, 6);
                    p[o + 2] = ChannelBits.Expand(v >> 11, 5);
                    p[o + 3] = 255;
                    break;
            }
        }
        return image;
    }

    /// <summary>
    /// Encodes BGRA32 to a VBM level's raw pixels the way the engine converts 8888 down: each channel keeps its top bits
    /// (<see cref="ChannelBits.Quantise"/>), and 1555 alpha is opaque from 128 up. Decoding a level and encoding it again
    /// gives back the same bytes.
    /// </summary>
    public static byte[] EncodeLevel(BgraImage image, VbmPixelFormat format, uint version)
    {
        ArgumentNullException.ThrowIfNull(image);
        bool inverted = version < VbmCodec.FirstStandardAlphaVersion;
        var p = image.Pixels;
        int n = image.Width * image.Height;
        var data = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            byte b = p[o], g = p[o + 1], r = p[o + 2], a = p[o + 3];
            int v = format switch
            {
                VbmPixelFormat.Argb1555 => ChannelBits.Quantise(b, 5) | (ChannelBits.Quantise(g, 5) << 5) | (ChannelBits.Quantise(r, 5) << 10)
                    | ((a >= 128) != inverted ? 0x8000 : 0),
                VbmPixelFormat.Argb4444 => ChannelBits.Quantise(b, 4) | (ChannelBits.Quantise(g, 4) << 4) | (ChannelBits.Quantise(r, 4) << 8)
                    | (ChannelBits.Quantise(a, 4) << 12),
                _ => ChannelBits.Quantise(b, 5) | (ChannelBits.Quantise(g, 6) << 5) | (ChannelBits.Quantise(r, 5) << 11),
            };
            data[i * 2] = (byte)v;
            data[i * 2 + 1] = (byte)(v >> 8);
        }
        return data;
    }

    /// <summary>
    /// Encodes a frame: <paramref name="image"/> becomes level 0 (it must already have the file's size), and each further
    /// level is the previous full-colour level halved with <paramref name="filter"/> before it is encoded.
    /// </summary>
    public static VbmFrame EncodeFrame(BgraImage image, VbmPixelFormat format, uint version, int mipLevels, ResampleFilter filter = ResampleFilter.Box)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (mipLevels < 1 || mipLevels > VbmFile.MaxMipLevels(image.Width, image.Height))
            throw new ArgumentOutOfRangeException(nameof(mipLevels));
        var levels = new byte[mipLevels][];
        var current = image;
        for (int l = 0; l < mipLevels; l++)
        {
            if (l > 0) current = ImageResampler.HalveForMip(current, filter);
            levels[l] = EncodeLevel(current, format, version);
        }
        return new VbmFrame(levels);
    }

    /// <summary>
    /// <paramref name="image"/> at <paramref name="width"/> x <paramref name="height"/>: itself when it already is,
    /// else resampled (Lanczos when shrinking or growing, alpha-weighted like every resize in Cairn).
    /// </summary>
    public static BgraImage Fit(BgraImage image, int width, int height) =>
        image.Width == width && image.Height == height ? image : ImageResampler.Resize(image, width, height, ResampleFilter.Lanczos3);
}
