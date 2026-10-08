using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Cairn.Formats.Imaging;

/// <summary>
/// One texture of a PEG texture pack: what its directory entry says, where its data is, and whether Cairn can
/// decode it.
/// </summary>
/// <param name="Index">Position in the pack's directory.</param>
/// <param name="Name">The texture's name as stored (usually ending in <c>.tga</c>, or <c>.vbm</c> for an animation).</param>
/// <param name="Width">Width of mip level 0.</param>
/// <param name="Height">Height of mip level 0.</param>
/// <param name="Format">The pixel format byte: 2 MPEG-2 still, 3 16-bit, 4 8-bit indexed, 5 4-bit indexed, 7 32-bit.</param>
/// <param name="PaletteFormat">The palette format byte of indexed textures: 1 = 16-bit entries, 2 = 32-bit entries.</param>
/// <param name="Flags">The flags byte (0x01 = has alpha; 0x10 = an MPEG-2 background keyed out on black, see <see cref="PegBlackKey"/>).</param>
/// <param name="FrameCount">Frames stored (at least 1).</param>
/// <param name="AnimationDelay">The animation delay byte (0 in every known file).</param>
/// <param name="MipCount">Mip levels per frame, including level 0 (at least 1).</param>
/// <param name="DataOffset">Absolute offset of the texture's data in the file.</param>
/// <param name="DataLength">Bytes available for the texture (to the next texture's data, or to the end of the file).</param>
public sealed record PegTexture(
    int Index,
    string Name,
    int Width,
    int Height,
    int Format,
    int PaletteFormat,
    int Flags,
    int FrameCount,
    int AnimationDelay,
    int MipCount,
    long DataOffset,
    long DataLength)
{
    /// <summary>The MPEG-2 compressed still format (full-screen backgrounds), decoded by <see cref="PegCodec.DecodeMpeg2"/>.</summary>
    public const int Mpeg2Format = 2;

    /// <summary>
    /// Why this texture cannot be decoded (unknown format, data missing), or null when it can be. For an MPEG-2 still
    /// (see <see cref="IsMpeg2"/>) whether its stream decodes is only known once it is decoded.
    /// </summary>
    public string? Problem { get; init; }

    /// <summary>True for an MPEG-2 compressed still (one intra-coded picture per tile, no alpha).</summary>
    public bool IsMpeg2 => Format == Mpeg2Format;

    /// <summary>True when <see cref="PegCodec.DecodeFrame"/> can decode the texture.</summary>
    public bool CanDecode => Problem is null;

    /// <summary>True when more than one frame is stored.</summary>
    public bool IsAnimated => FrameCount > 1;

    /// <summary>True when the flags byte says the texture has alpha.</summary>
    public bool HasAlphaFlag => (Flags & 1) != 0;

    /// <summary>Bits per pixel of the known direct and indexed formats, else 0.</summary>
    public int BitsPerPixel => Format switch { 3 => 16, 4 => 8, 5 => 4, 7 => 32, _ => 0 };

    /// <summary>Palette entries of an indexed format (256 or 16), else 0.</summary>
    public int PaletteEntries => Format switch { 4 => 256, 5 => 16, _ => 0 };

    /// <summary>Bytes of one frame's palette (stored in front of the frame's pixels), else 0.</summary>
    public int PaletteBytes => PaletteEntries * (PaletteFormat == 1 ? 2 : 4);

    /// <summary>Width of mip <paramref name="level"/> (1 from level 31 on: C# would wrap a shift of 32 or more back to the full size).</summary>
    public int MipWidth(int level) => level >= 31 ? 1 : Math.Max(1, Width >> Math.Max(0, level));

    /// <summary>Height of mip <paramref name="level"/> (1 from level 31 on).</summary>
    public int MipHeight(int level) => level >= 31 ? 1 : Math.Max(1, Height >> Math.Max(0, level));

    /// <summary>Pixel bytes of mip <paramref name="level"/> (a 4-bit level rounds up to whole bytes).</summary>
    public long MipBytes(int level) => ((long)MipWidth(level) * MipHeight(level) * BitsPerPixel + 7) / 8;

    /// <summary>Bytes of one frame: its palette and every mip level.</summary>
    public long FrameBytes
    {
        get
        {
            long total = PaletteBytes;
            for (int level = 0; level < MipCount; level++) total += MipBytes(level);
            return total;
        }
    }

    /// <summary>Bytes the header implies for the whole texture (every frame); 0 for MPEG-2 and unknown formats.</summary>
    public long ExpectedBytes => BitsPerPixel == 0 ? 0 : FrameBytes * FrameCount;

    /// <summary>"8-bit indexed, 32-bit palette", "16-bit", "MPEG-2 compressed", ...</summary>
    public string FormatLabel => Format switch
    {
        2 => "MPEG-2 compressed",
        3 => "16-bit",
        4 => PaletteFormat == 1 ? "8-bit indexed, 16-bit palette" : "8-bit indexed, 32-bit palette",
        5 => PaletteFormat == 1 ? "4-bit indexed, 16-bit palette" : "4-bit indexed, 32-bit palette",
        7 => "32-bit",
        _ => string.Create(CultureInfo.InvariantCulture, $"format {Format}"),
    };

    /// <summary>"256x256, 8-bit indexed, 32-bit palette, 3 mips, 12 frames" (counts only when above 1).</summary>
    public string Describe()
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}, {FormatLabel}"));
        if (MipCount > 1) text.Append(CultureInfo.InvariantCulture, $", {MipCount} mips");
        if (FrameCount > 1) text.Append(CultureInfo.InvariantCulture, $", {FrameCount} frames");
        return text.ToString();
    }
}

/// <summary>A PEG texture pack's directory.</summary>
/// <param name="Version">The container version (6 for every retail-style file; 4 in early PlayStation 2 builds).</param>
/// <param name="Textures">The textures in directory order.</param>
public sealed record PegFile(int Version, IReadOnlyList<PegTexture> Textures)
{
    /// <summary>Textures that can be decoded.</summary>
    public int DecodableCount => Textures.Count(t => t.CanDecode);

    /// <summary>MPEG-2 compressed stills.</summary>
    public int Mpeg2Count => Textures.Count(t => t.IsMpeg2);

    /// <summary>"14 textures (2 animated, 3 MPEG-2 compressed)".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            int animated = Textures.Count(t => t.IsAnimated && !t.IsMpeg2);
            if (animated > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{animated:N0} animated"));
            if (Mpeg2Count > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Mpeg2Count:N0} MPEG-2 compressed"));
            int other = Textures.Count(t => !t.IsMpeg2 && t.Problem is not null);
            if (other > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{other:N0} unreadable"));
            string count = Textures.Count == 1 ? "1 texture" : string.Create(CultureInfo.InvariantCulture, $"{Textures.Count:N0} textures");
            return parts.Count == 0 ? count : $"{count} ({string.Join(", ", parts)})";
        }
    }
}

/// <summary>
/// Reads PEG texture packs (signature <c>GEKV</c>), the texture container of Red Faction's PlayStation 2 version
/// (and of Red Faction II), and decodes their textures to BGRA32.
/// </summary>
/// <remarks>
/// <para>
/// <b>Version 6</b> (every retail-style file): a 32-byte header (signature, version, directory size = count x 64,
/// data size, texture count, 0, total frame count, alignment 16), then one 64-byte entry per texture (u16 width,
/// u16 height, u8 format, u8 palette format, u8 flags, u8 frame count, u8 animation delay, u8 mip count, u16 0, a
/// 48-byte NUL-padded name, u32 absolute data offset). The file is exactly header + directory + data.
/// </para>
/// <para>
/// <b>Version 4</b> (early PlayStation 2 builds): a 28-byte header without the total frame count, then entries of the
/// same 12 fixed bytes followed by a NUL-terminated name, each padded to 4 bytes, and no offsets: the textures' data
/// follows the directory back to back, which the data size field confirms.
/// </para>
/// <para>
/// A texture's data is its frames back to back; each frame is the palette (indexed formats only) followed by every
/// mip level, largest first. Pixels are linear (no GS block swizzle). 8-bit indexes swap bits 3 and 4 (the usual
/// PlayStation 2 palette layout); 4-bit indexes are stored low nibble first and are not swapped. 16-bit colours are
/// A1B5G5R5 with red in the low bits and bit 15 meaning opaque; 32-bit colours are R, G, B, A bytes with alpha 0x80
/// meaning opaque, so alpha is scaled by 255/128 and clamped.
/// </para>
/// <para>
/// <b>Format 2</b> holds an MPEG-2 compressed still (the full-screen menu backgrounds) cut into tiles of at most
/// 256 x 256, each an independent MPEG-2 video stream holding one intra-coded picture: u32 total (data length - 12),
/// u32 length of tile 0, 8 unused bytes, tile 0; then for each further tile, at the next 16-byte aligned offset, u32
/// length, 12 unused bytes, the tile. The unused bytes may hold anything (even start code patterns), so the tiles are
/// found by following the lengths, never by scanning. Tiles are placed left to right, wrapping to a new row once the
/// texture's width is covered, and the result is cropped to the texture's size. There is no alpha; the PlayStation 2
/// can key out black (<see cref="PegBlackKey"/>).
/// </para>
/// </remarks>
public static class PegCodec
{
    /// <summary>The signature: "GEKV" read as a little-endian 32-bit value.</summary>
    public const uint Signature = 0x564B4547;

    private const int V6HeaderSize = 32;
    private const int V6EntrySize = 64;
    private const int V6NameBytes = 48;
    private const int V4HeaderSize = 28;
    private const int FixedEntryBytes = 12;
    private const int MaxNameBytes = 256;

    /// <summary>True when <paramref name="head"/> starts with the PEG signature.</summary>
    public static bool IsPeg(ReadOnlySpan<byte> head) => head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Signature;

    /// <summary>Reads the directory of a PEG texture pack. Texture data is checked against the file but not decoded.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="name">The file name, for messages.</param>
    /// <exception cref="AssetFormatException">
    /// Not a PEG file, or a PEG variant whose layout Cairn does not know ("unsupported PEG variant").
    /// </exception>
    public static PegFile Read(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        name ??= "texture pack";
        if (bytes.Length < 8 || !IsPeg(bytes)) throw new AssetFormatException($"'{name}' is not a PEG texture pack (no GEKV signature).");
        int version = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        return version switch
        {
            6 => ReadV6(bytes, name),
            4 => ReadV4(bytes, name),
            _ => throw Unsupported(name, version, "Cairn reads versions 4 and 6"),
        };
    }

    private static AssetFormatException Unsupported(string name, int version, string why) =>
        new($"'{name}' uses an unsupported PEG variant (version {version.ToString(CultureInfo.InvariantCulture)}): {why}.");

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    private static PegFile ReadV6(byte[] bytes, string name)
    {
        if (bytes.Length < V6HeaderSize) throw new AssetFormatException($"'{name}' is too short to be a PEG texture pack.");
        uint directorySize = U32(bytes, 8);
        uint count = U32(bytes, 16);
        if (count > (bytes.Length - V6HeaderSize) / V6EntrySize)
            throw new AssetFormatException($"'{name}' says it holds {count:N0} textures, more than its {bytes.Length:N0} bytes can hold.");
        if (directorySize != count * V6EntrySize)
            throw Unsupported(name, 6, string.Create(CultureInfo.InvariantCulture, $"its directory is {directorySize:N0} bytes, not {count * V6EntrySize:N0} for {count:N0} entries"));

        var entries = new List<(int W, int H, int Fmt, int Pal, int Flags, int Frames, int Delay, int Mips, string Name, long Offset)>((int)count);
        for (int i = 0; i < count; i++)
        {
            int at = V6HeaderSize + i * V6EntrySize;
            var (w, h, fmt, pal, flags, frames, delay, mips) = Fixed(bytes, at);
            int nameLength = Array.IndexOf(bytes, (byte)0, at + FixedEntryBytes, V6NameBytes) is int nul and >= 0 ? nul - (at + FixedEntryBytes) : V6NameBytes;
            string textureName = Encoding.Latin1.GetString(bytes, at + FixedEntryBytes, nameLength);
            long offset = U32(bytes, at + FixedEntryBytes + V6NameBytes);
            entries.Add((w, h, fmt, pal, flags, frames, delay, mips, textureName, offset));
        }

        // Each texture's data runs to the next larger offset (or the end of the file): the gap is padding to 16 bytes.
        var starts = entries.Select(e => e.Offset).Where(o => o < bytes.Length).Distinct().Order().ToArray();
        var textures = new List<PegTexture>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            long end = bytes.Length;
            int next = Array.BinarySearch(starts, e.Offset);
            if (next >= 0 && next + 1 < starts.Length) end = starts[next + 1];
            long available = e.Offset < bytes.Length ? end - e.Offset : 0;
            textures.Add(Check(new PegTexture(i, e.Name, e.W, e.H, e.Fmt, e.Pal, e.Flags, e.Frames, e.Delay, e.Mips, e.Offset, available), bytes.Length));
        }
        return new PegFile(6, textures);
    }

    private static PegFile ReadV4(byte[] bytes, string name)
    {
        if (bytes.Length < V4HeaderSize) throw new AssetFormatException($"'{name}' is too short to be a PEG texture pack.");
        long directorySize = U32(bytes, 8), dataSize = U32(bytes, 12);
        uint count = U32(bytes, 16);
        if (V4HeaderSize + directorySize + dataSize != bytes.Length)
            throw Unsupported(name, 4, "its header sizes do not add up to the file's length");
        if (count > directorySize / (FixedEntryBytes + 1))
            throw new AssetFormatException($"'{name}' says it holds {count:N0} textures, more than its directory can hold.");

        var textures = new List<PegTexture>((int)count);
        long directoryEnd = V4HeaderSize + directorySize;
        int at = V4HeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (at + FixedEntryBytes >= directoryEnd) throw Unsupported(name, 4, "its directory ends before its last entry");
            var (w, h, fmt, pal, flags, frames, delay, mips) = Fixed(bytes, at);
            int nameStart = at + FixedEntryBytes;
            int limit = (int)Math.Min(MaxNameBytes, directoryEnd - nameStart);
            int nul = Array.IndexOf(bytes, (byte)0, nameStart, limit);
            if (nul < 0) throw Unsupported(name, 4, "an entry's name is not terminated");
            string textureName = Encoding.Latin1.GetString(bytes, nameStart, nul - nameStart);
            textures.Add(new PegTexture(i, textureName, w, h, fmt, pal, flags, frames, delay, mips, 0, 0));
            at = (nul + 1 + 3) & ~3; // entries are padded to 4 bytes
        }
        if (at != directoryEnd) throw Unsupported(name, 4, "its directory does not end where its header says");
        if (textures.Any(t => t.ExpectedBytes == 0))
            throw Unsupported(name, 4, "it holds a texture whose size cannot be worked out (an MPEG-2 or unknown format), and version 4 stores no offsets");

        // No offsets: the data follows the directory back to back. Accept the layout only when it accounts for every byte.
        foreach (int align in new[] { 1, 16 })
        {
            long offset = 0;
            foreach (var t in textures) offset = (offset + align - 1) / align * align + t.ExpectedBytes;
            if (offset != dataSize) continue;
            var placed = new List<PegTexture>(textures.Count);
            offset = 0;
            foreach (var t in textures)
            {
                offset = (offset + align - 1) / align * align;
                placed.Add(Check(t with { DataOffset = directoryEnd + offset, DataLength = t.ExpectedBytes }, bytes.Length));
                offset += t.ExpectedBytes;
            }
            return new PegFile(4, placed);
        }
        throw Unsupported(name, 4, "its textures' sizes do not account for its data");
    }

    private static (int W, int H, int Fmt, int Pal, int Flags, int Frames, int Delay, int Mips) Fixed(byte[] b, int at) => (
        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)),
        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + 2)),
        b[at + 4], b[at + 5], b[at + 6],
        Math.Max(1, (int)b[at + 7]),
        b[at + 8],
        Math.Max(1, (int)b[at + 9]));

    /// <summary>Sets <see cref="PegTexture.Problem"/> when the texture cannot be decoded.</summary>
    private static PegTexture Check(PegTexture t, long fileLength)
    {
        string? problem = null;
        if (t.Width <= 0 || t.Height <= 0) problem = string.Create(CultureInfo.InvariantCulture, $"it declares invalid dimensions {t.Width} x {t.Height}");
        else if (t.Width > EngineFormats.MaxDimension || t.Height > EngineFormats.MaxDimension)
            problem = string.Create(CultureInfo.InvariantCulture, $"it is {t.Width} x {t.Height}, larger than the {EngineFormats.MaxDimension} pixel limit");
        else if (t.DataOffset >= fileLength) problem = "its data lies outside the file";
        else if (t.IsMpeg2)
            problem = t.FrameCount > 1 || t.MipCount > 1
                ? string.Create(CultureInfo.InvariantCulture, $"it is an MPEG-2 still with {t.FrameCount} frame(s) and {t.MipCount} mip level(s); Cairn knows only single pictures")
                : null;
        else if (t.BitsPerPixel == 0) problem = string.Create(CultureInfo.InvariantCulture, $"it uses pixel format {t.Format}, which Cairn does not know");
        else if (t.PaletteEntries > 0 && t.PaletteFormat is not (1 or 2))
            problem = string.Create(CultureInfo.InvariantCulture, $"it uses palette format {t.PaletteFormat}, which Cairn does not know");
        else if (t.ExpectedBytes > t.DataLength)
            problem = string.Create(CultureInfo.InvariantCulture, $"its data is shorter than its header says ({t.DataLength:N0} of {t.ExpectedBytes:N0} bytes)");
        return problem is null ? t : t with { Problem = problem };
    }

    /// <summary>The raw bytes of an MPEG-2 still (or any texture's data block) as stored.</summary>
    public static ReadOnlySpan<byte> RawData(byte[] bytes, PegTexture texture)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.DataOffset >= bytes.Length) return [];
        long length = Math.Min(texture.DataLength, bytes.Length - texture.DataOffset);
        return bytes.AsSpan((int)texture.DataOffset, (int)length);
    }

    /// <summary>Decodes one frame's mip level of a texture to BGRA32.</summary>
    /// <param name="bytes">The whole PEG file.</param>
    /// <param name="texture">The texture (from <see cref="Read"/> of the same bytes).</param>
    /// <param name="frame">The frame, counting from 0.</param>
    /// <param name="mip">The mip level, 0 = full size.</param>
    /// <param name="blackKey">For an MPEG-2 background: black as transparent (see <see cref="PegBlackKey.For"/>); ignored otherwise.</param>
    /// <exception cref="ImageDecodeException">The texture cannot be decoded (unknown format, data missing, a damaged
    /// MPEG-2 stream), or no such frame or level.</exception>
    public static BgraImage DecodeFrame(byte[] bytes, PegTexture texture, int frame = 0, int mip = 0, PegBlackKey? blackKey = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Problem is { } problem) throw new ImageDecodeException($"'{texture.Name}' cannot be decoded: {problem}.");
        if (frame < 0 || frame >= texture.FrameCount)
            throw new ImageDecodeException(string.Create(CultureInfo.InvariantCulture, $"'{texture.Name}' has {texture.FrameCount} frame(s), so there is no frame {frame}."));
        if (mip < 0 || mip >= texture.MipCount)
            throw new ImageDecodeException(string.Create(CultureInfo.InvariantCulture, $"'{texture.Name}' has {texture.MipCount} mip level(s), so there is no level {mip}."));
        if (texture.IsMpeg2) return DecodeMpeg2(bytes, texture, blackKey);

        long frameStart = texture.DataOffset + texture.FrameBytes * frame;
        long pixels = frameStart + texture.PaletteBytes;
        for (int level = 0; level < mip; level++) pixels += texture.MipBytes(level);
        int w = texture.MipWidth(mip), h = texture.MipHeight(mip);
        DecodeLimits.EnsureWithinBudget(w, h, texture.Name);
        DecodeLimits.EnsureDataAvailable(pixels + texture.MipBytes(mip), bytes.Length, texture.Name);

        var image = new BgraImage(w, h);
        var dst = image.Pixels;
        int src = (int)pixels;
        switch (texture.Format)
        {
            case 3:
                for (int i = 0; i < w * h; i++, src += 2) Put16(dst, i, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(src)));
                break;
            case 7:
                for (int i = 0; i < w * h; i++, src += 4) Put(dst, i, bytes[src], bytes[src + 1], bytes[src + 2], ScaleAlpha(bytes[src + 3]));
                break;
            case 4:
            {
                var palette = ReadPalette(bytes, (int)frameStart, texture);
                for (int i = 0; i < w * h; i++)
                {
                    int index = bytes[src + i];
                    // The PlayStation 2 palette layout: index bits 3 and 4 are swapped.
                    index = (index & 0xE7) | ((index >> 1) & 0x08) | ((index << 1) & 0x10);
                    palette[index].CopyTo(dst.AsSpan(i * 4, 4));
                }
                break;
            }
            case 5:
            {
                var palette = ReadPalette(bytes, (int)frameStart, texture);
                for (int i = 0; i < w * h; i++)
                {
                    int pair = bytes[src + (i >> 1)];
                    int index = (i & 1) == 0 ? pair & 0x0F : pair >> 4; // low nibble first
                    palette[index].CopyTo(dst.AsSpan(i * 4, 4));
                }
                break;
            }
            default:
                throw new ImageDecodeException($"'{texture.Name}' uses pixel format {texture.Format}, which Cairn does not know.");
        }
        return image;
    }

    /// <summary>The most tiles an MPEG-2 still may have (a 16384 x 16384 texture of 16 x 16 tiles is far fewer).</summary>
    private const int MaxMpeg2Tiles = 65536;

    /// <summary>
    /// The tiles of an MPEG-2 still's data block (format 2): each tile's MPEG-2 stream as (offset, length) within
    /// <paramref name="data"/>, found by following the length fields (see the class remarks).
    /// </summary>
    /// <param name="data">The texture's data (<see cref="RawData"/>).</param>
    /// <param name="name">The texture's name, for messages.</param>
    /// <exception cref="ImageDecodeException">The data does not hold the tile layout.</exception>
    public static IReadOnlyList<(int Offset, int Length)> Mpeg2Tiles(ReadOnlySpan<byte> data, string name)
    {
        ReadOnlySpan<byte> sequenceHeader = [0, 0, 1, 0xB3];
        if (data.Length < 20 || !data[16..].StartsWith(sequenceHeader))
            throw new ImageDecodeException($"'{name}' is not laid out as an MPEG-2 still (no sequence header at offset 16).");
        var tiles = new List<(int, int)>();
        int start = 16;
        long length = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        while (true)
        {
            if (length < 8 || start + length > data.Length)
                throw new ImageDecodeException(string.Create(CultureInfo.InvariantCulture, $"'{name}' is damaged: MPEG-2 tile {tiles.Count + 1} says it is {length:N0} bytes, but {data.Length - start:N0} are left."));
            tiles.Add((start, (int)length));
            if (tiles.Count > MaxMpeg2Tiles) throw new ImageDecodeException($"'{name}' is damaged: it holds too many MPEG-2 tiles.");
            int next = (int)((start + length + 15) & ~15L);
            if (next + 20 > data.Length || !data[(next + 16)..].StartsWith(sequenceHeader)) break;
            length = BinaryPrimitives.ReadUInt32LittleEndian(data[next..]);
            start = next + 16;
        }
        return tiles;
    }

    /// <summary>
    /// Decodes an MPEG-2 still (format 2) to BGRA32: every tile is decoded, placed left to right and top to bottom,
    /// and the result cropped to the texture's size. Alpha is opaque unless <paramref name="blackKey"/> is given.
    /// </summary>
    /// <param name="bytes">The whole PEG file.</param>
    /// <param name="texture">An MPEG-2 texture (from <see cref="Read"/> of the same bytes).</param>
    /// <param name="blackKey">Black as transparent, as the PlayStation 2 can (see <see cref="PegBlackKey"/>), or null.</param>
    /// <exception cref="ImageDecodeException">Not an MPEG-2 texture, damaged, or using MPEG-2 features Cairn does not decode.</exception>
    public static BgraImage DecodeMpeg2(byte[] bytes, PegTexture texture, PegBlackKey? blackKey = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(texture);
        if (!texture.IsMpeg2) throw new ImageDecodeException($"'{texture.Name}' is not an MPEG-2 still.");
        if (texture.Problem is { } problem) throw new ImageDecodeException($"'{texture.Name}' cannot be decoded: {problem}.");
        int w = texture.Width, h = texture.Height;
        DecodeLimits.EnsureWithinBudget(w, h, texture.Name);
        var data = RawData(bytes, texture);
        var tiles = Mpeg2Tiles(data, texture.Name);
        var image = new BgraImage(w, h);
        int x = 0, y = 0, rowHeight = 0;
        for (int i = 0; i < tiles.Count; i++)
        {
            if (x >= w) { x = 0; y += rowHeight; rowHeight = 0; }
            if (y >= h) break; // tiles past the picture: nothing to show
            var (offset, length) = tiles[i];
            var picture = Mpeg2IntraDecoder.Decode(data.Slice(offset, length), string.Create(CultureInfo.InvariantCulture, $"{texture.Name} (tile {i + 1})"));
            picture.CopyTo(image, x, y);
            x += picture.Width;
            rowHeight = Math.Max(rowHeight, picture.Height);
        }
        if (y < h && (x < w || y + rowHeight < h))
            throw new ImageDecodeException(string.Create(CultureInfo.InvariantCulture, $"'{texture.Name}' is damaged: its {tiles.Count} MPEG-2 tile(s) do not cover its {w} x {h} pixels."));
        return blackKey is null ? image : blackKey.Apply(image);
    }

    /// <summary>Every frame's mip level 0 of a texture (<paramref name="blackKey"/>: see <see cref="DecodeFrame"/>).</summary>
    public static IReadOnlyList<BgraImage> DecodeFrames(byte[] bytes, PegTexture texture, PegBlackKey? blackKey = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var frames = new BgraImage[texture.FrameCount];
        for (int i = 0; i < frames.Length; i++) frames[i] = DecodeFrame(bytes, texture, i, 0, blackKey);
        return frames;
    }

    /// <summary>PlayStation 2 alpha: 0x80 is opaque. Scaled by 255/128 (rounded) and clamped.</summary>
    public static byte ScaleAlpha(byte alpha) => (byte)Math.Min(255, (alpha * 255 + 64) / 128);

    private static byte[][] ReadPalette(byte[] bytes, int at, PegTexture texture)
    {
        var palette = new byte[texture.PaletteEntries][];
        for (int i = 0; i < palette.Length; i++)
        {
            var bgra = new byte[4];
            if (texture.PaletteFormat == 1) Put16(bgra, 0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + i * 2)));
            else Put(bgra, 0, bytes[at + i * 4], bytes[at + i * 4 + 1], bytes[at + i * 4 + 2], ScaleAlpha(bytes[at + i * 4 + 3]));
            palette[i] = bgra;
        }
        return palette;
    }

    /// <summary>A1B5G5R5: red in the low bits, bit 15 = opaque.</summary>
    private static void Put16(byte[] dst, int pixel, ushort v) => Put(dst, pixel,
        ChannelBits.Expand(v & 0x1F, 5), ChannelBits.Expand((v >> 5) & 0x1F, 5), ChannelBits.Expand((v >> 10) & 0x1F, 5),
        (v & 0x8000) != 0 ? (byte)255 : (byte)0);

    private static void Put(byte[] dst, int pixel, byte r, byte g, byte b, byte a)
    {
        int i = pixel * 4;
        dst[i] = b;
        dst[i + 1] = g;
        dst[i + 2] = r;
        dst[i + 3] = a;
    }
}
