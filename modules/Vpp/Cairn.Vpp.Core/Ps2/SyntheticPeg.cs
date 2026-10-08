using System.Buffers.Binary;
using System.Text;
using Cairn.Formats.Imaging;

namespace Cairn.Vpp.Ps2;

/// <summary>A texture for <see cref="SyntheticPeg"/>: its directory fields and its raw data (every frame, palette first).</summary>
internal sealed record SyntheticPegTexture(string Name, int Width, int Height, int Format, int PaletteFormat, int Frames, int Mips, byte[] Data, int Flags = 0);

/// <summary>
/// Builds PEG texture packs in memory for the unit tests and self-tests (no game files): version 6 with offsets and
/// 16-byte aligned data, and version 4 with sequential data, both in the layouts <see cref="PegCodec"/> documents.
/// </summary>
internal static class SyntheticPeg
{
    /// <summary>
    /// A texture whose stored values come from <paramref name="pixel"/> (frame, mip, x, y): a raw u16 for format 3,
    /// RGBA packed as 0xAABBGGRR for format 7, a stored palette index for formats 4 and 5. Indexed formats take their
    /// palette from <paramref name="palette"/> (frame, entry in stored order): a raw u16 for palette format 1, RGBA
    /// packed as 0xAABBGGRR for palette format 2.
    /// </summary>
    public static SyntheticPegTexture Texture(string name, int width, int height, int format, int paletteFormat, int frames, int mips,
        Func<int, int, int, int, uint> pixel, Func<int, int, uint>? palette = null, int flags = 0)
    {
        var probe = new PegTexture(0, name, width, height, format, paletteFormat, flags, frames, 0, mips, 0, 0);
        var data = new byte[probe.ExpectedBytes];
        int at = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < probe.PaletteEntries; i++)
            {
                uint entry = palette?.Invoke(f, i) ?? 0;
                if (paletteFormat == 1) { BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), (ushort)entry); at += 2; }
                else { BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), entry); at += 4; }
            }
            for (int m = 0; m < mips; m++)
            {
                int w = probe.MipWidth(m), h = probe.MipHeight(m);
                if (format == 5)
                {
                    for (int p = 0; p < w * h; p++)
                    {
                        uint v = pixel(f, m, p % w, p / w) & 0x0F;
                        data[at + p / 2] |= (byte)((p & 1) == 0 ? v : v << 4);
                    }
                    at += (int)probe.MipBytes(m);
                    continue;
                }
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        uint v = pixel(f, m, x, y);
                        switch (format)
                        {
                            case 3: BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), (ushort)v); at += 2; break;
                            case 7: BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), v); at += 4; break;
                            default: data[at++] = (byte)v; break;
                        }
                    }
                }
            }
        }
        return new SyntheticPegTexture(name, width, height, format, paletteFormat, frames, mips, data, flags);
    }

    /// <summary>
    /// An MPEG-2 still of <see cref="Mpeg2Image"/> (<paramref name="seed"/>), coded in 256 x 256 tiles as the PS2 files
    /// are (<see cref="SyntheticMpeg2.PegData"/>), the unused header bytes holding junk and a fake start code.
    /// </summary>
    public static SyntheticPegTexture Mpeg2(string name, int width = 640, int height = 448, int seed = 0, Mpeg2EncodeOptions? options = null) =>
        new(name, width, height, PegTexture.Mpeg2Format, 0, 1, 1, SyntheticMpeg2.PegData(Mpeg2Image(width, height, seed), options, junk: 0xA5));

    /// <summary>
    /// An MPEG-2 still for "black as transparent": the left half black, the right half light grey (<see cref="BlackHalf"/>);
    /// <paramref name="flags"/> as stored (0x10 asks the PS2 to key out black).
    /// </summary>
    public static SyntheticPegTexture Mpeg2Black(string name, int width = 64, int height = 32, int flags = 0) =>
        new(name, width, height, PegTexture.Mpeg2Format, 0, 1, 1, SyntheticMpeg2.PegData(BlackHalf(width, height), null, junk: 0xA5), flags);

    /// <summary>The left half black (0, 0, 0), the right half light grey (200, 200, 200), opaque.</summary>
    public static BgraImage BlackHalf(int width, int height)
    {
        var image = new BgraImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte v = x < width / 2 ? (byte)0 : (byte)200;
                image.Set(x, y, v, v, v, 255);
            }
        return image;
    }

    /// <summary>A smooth test picture (gradients, a disc and a bar that move with <paramref name="seed"/>), opaque.</summary>
    public static BgraImage Mpeg2Image(int width, int height, int seed = 0)
    {
        var image = new BgraImage(width, height);
        double cx = width * (0.3 + 0.05 * (seed % 8)), cy = height * 0.5, radius = Math.Min(width, height) / 4.0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                bool disc = (x - cx) * (x - cx) + (y - cy) * (y - cy) < radius * radius;
                bool bar = Math.Abs(x - (seed * 13 % Math.Max(1, width))) < 6;
                image.Pixels[at] = (byte)(disc ? 40 : 255 * x / Math.Max(1, width - 1));      // blue
                image.Pixels[at + 1] = (byte)(bar ? 230 : 255 * y / Math.Max(1, height - 1)); // green
                image.Pixels[at + 2] = (byte)(disc ? 220 : 90);                                 // red
                image.Pixels[at + 3] = 255;
            }
        return image;
    }

    /// <summary>A version 6 pack: 32-byte header, 64-byte entries, each texture's data at a 16-byte aligned offset.</summary>
    public static byte[] V6(params SyntheticPegTexture[] textures)
    {
        int directory = textures.Length * 64;
        var offsets = new int[textures.Length];
        int at = 32 + directory;
        for (int i = 0; i < textures.Length; i++)
        {
            at = (at + 15) & ~15;
            offsets[i] = at;
            at += textures[i].Data.Length;
        }
        var bytes = new byte[at];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, PegCodec.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)directory);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)(at - 32 - directory));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)textures.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)textures.Sum(t => t.Frames > 1 ? t.Frames + 1 : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 16);
        for (int i = 0; i < textures.Length; i++)
        {
            int e = 32 + i * 64;
            WriteFixed(bytes, e, textures[i]);
            var name = Encoding.Latin1.GetBytes(textures[i].Name);
            name.AsSpan(0, Math.Min(name.Length, 47)).CopyTo(bytes.AsSpan(e + 12));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(e + 60), (uint)offsets[i]);
            textures[i].Data.CopyTo(bytes, offsets[i]);
        }
        return bytes;
    }

    /// <summary>A version 4 pack: 28-byte header, entries of 12 bytes plus a NUL-terminated name padded to 4 bytes, data back to back.</summary>
    public static byte[] V4(params SyntheticPegTexture[] textures)
    {
        using var directory = new MemoryStream();
        foreach (var t in textures)
        {
            var fixedPart = new byte[12];
            WriteFixed(fixedPart, 0, t);
            directory.Write(fixedPart);
            directory.Write(Encoding.Latin1.GetBytes(t.Name));
            directory.WriteByte(0);
            while (directory.Length % 4 != 0) directory.WriteByte(0xCD);
        }
        int dataSize = textures.Sum(t => t.Data.Length);
        var bytes = new byte[28 + directory.Length + dataSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, PegCodec.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)directory.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)textures.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 16);
        directory.ToArray().CopyTo(bytes, 28);
        int at = 28 + (int)directory.Length;
        foreach (var t in textures) { t.Data.CopyTo(bytes, at); at += t.Data.Length; }
        return bytes;
    }

    private static void WriteFixed(byte[] bytes, int at, SyntheticPegTexture t)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)t.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 2), (ushort)t.Height);
        bytes[at + 4] = (byte)t.Format;
        bytes[at + 5] = (byte)t.PaletteFormat;
        bytes[at + 6] = (byte)t.Flags;
        bytes[at + 7] = (byte)t.Frames;
        bytes[at + 8] = 0;
        bytes[at + 9] = (byte)t.Mips;
    }

    /// <summary>
    /// A small pack with one texture of every decodable format, an animation and an MPEG-2 background:
    /// <c>wall.tga</c> (32-bit, 2 mips), <c>panel.tga</c> (16-bit), <c>sign.tga</c> (8-bit, 32-bit palette),
    /// <c>grate.tga</c> (8-bit, 16-bit palette), <c>dots.tga</c> (4-bit), <c>fire.vbm</c> (3 frames, 8-bit) and
    /// <c>menu_bg.tga</c> (MPEG-2).
    /// </summary>
    public static byte[] Sample()
    {
        static uint Rgba(int r, int g, int b, int a) => (uint)(r | g << 8 | b << 16 | a << 24);
        return V6(
            Texture("wall.tga", 16, 16, 7, 0, 1, 2, (f, m, x, y) => Rgba(x * 16, y * 16, m * 200, 0x80), flags: 1),
            Texture("panel.tga", 8, 8, 3, 0, 1, 1, (f, m, x, y) => (uint)(0x8000 | (x * 4) | (y * 4) << 5)),
            Texture("sign.tga", 16, 8, 4, 2, 1, 1, (f, m, x, y) => (uint)(x + y * 16), (f, i) => Rgba(i, 255 - i, 128, i < 8 ? 0 : 0x80), flags: 1),
            Texture("grate.tga", 8, 8, 4, 1, 1, 1, (f, m, x, y) => (uint)((x + y) % 32), (f, i) => (uint)(0x8000 | i)),
            Texture("dots.tga", 8, 8, 5, 2, 1, 1, (f, m, x, y) => (uint)((x + y) % 16), (f, i) => Rgba(i * 16, i * 8, 255, 0x80), flags: 1),
            Texture("fire.vbm", 16, 16, 4, 2, 3, 1, (f, m, x, y) => (uint)((x + y + f * 5) % 256), (f, i) => Rgba(255, i, f * 60, 0x80), flags: 1),
            Mpeg2("menu_bg.tga"));
    }

    /// <summary>
    /// A pack like the PS2 main menu's: <paramref name="frames"/> numbered MPEG-2 stills <c>plan-0001.tga</c>, ...
    /// (a moving picture), then two numbered pages <c>extras01.tga</c> and <c>extras02.tga</c> that are not frames.
    /// </summary>
    public static byte[] Mpeg2Sequence(int frames = 8, int width = 64, int height = 48) => V6(
    [
        .. Enumerable.Range(1, frames).Select(i => Mpeg2("plan-" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + ".tga", width, height, seed: i)),
        Mpeg2("extras01.tga", width, height, seed: 20),
        Mpeg2("extras02.tga", width, height, seed: 21),
    ]);
}
