using System.Buffers.Binary;
using Cairn.Formats.Imaging;

namespace Cairn.Formats.XForge;

/// <summary>
/// Red Faction for N-Gage's 16-bit textures: 39 of its <c>.tga</c> files declare one alpha bit per pixel (ARGB 1555) but
/// never set it. The N-Gage game draws them opaque; a reader that honours the header (Cairn's previews, and possibly Red
/// Faction) draws them fully see-through. <see cref="Opaque"/> clears the header's alpha bit count of such a file, so it
/// reads as the opaque 555 colour it is; the pixels are unchanged. The Meshes and Levels modules apply it to the
/// textures an N-Gage scene names, and only to those (a Red Faction texture is never changed).
/// <para>
/// The scenes also name <c>.pcx</c> textures, which Red Faction's tools and levels do not take: a conversion names each
/// as <c>.tga</c> (<see cref="PcName"/>) and writes its pixels as a 24-bit TGA (<see cref="ForPc"/>).
/// </para>
/// </summary>
public static class XffTextureFix
{
    private const int HeaderSize = 18;

    private const string PcxExtension = ".pcx";

    /// <summary>The extension a converted <c>.pcx</c> texture is named and written with.</summary>
    public const string TgaExtension = ".tga";

    /// <summary>True when <paramref name="name"/> is a <c>.pcx</c> file name.</summary>
    public static bool IsPcx(string name) => name.EndsWith(PcxExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name a conversion gives the texture <paramref name="name"/>: a <c>.pcx</c> as <c>.tga</c> (<c>lvl0_crate_01.pcx</c> → <c>lvl0_crate_01.tga</c>), any other as it is.</summary>
    public static string PcName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return IsPcx(name) ? name[..^PcxExtension.Length] + TgaExtension : name;
    }

    /// <summary>
    /// The file a conversion writes for the texture <paramref name="sourceName"/> holding <paramref name="data"/>: a
    /// <c>.pcx</c> decoded and written as a 24-bit TGA (every PCX decodes opaque); any other through <see cref="Opaque"/>.
    /// </summary>
    /// <exception cref="ImageDecodeException">A <c>.pcx</c> cannot be decoded.</exception>
    public static byte[] ForPc(byte[] data, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(sourceName);
        return IsPcx(sourceName) ? TgaWriter.Write(PcxCodec.Decode(data, sourceName), includeAlpha: false) : Opaque(data);
    }

    /// <summary>
    /// <paramref name="tga"/> with its alpha bit count cleared when it is a 16-bit true-colour TGA (raw or run-length)
    /// that declares alpha bits and sets none; otherwise <paramref name="tga"/> itself. Never throws for damaged data
    /// (it is returned as it is).
    /// </summary>
    public static byte[] Opaque(byte[] tga)
    {
        ArgumentNullException.ThrowIfNull(tga);
        return NeedsFix(tga) ? Patched(tga) : tga;
    }

    /// <summary>True when <paramref name="tga"/> is a 16-bit TGA that declares alpha bits but sets none (see <see cref="Opaque"/>).</summary>
    public static bool NeedsFix(ReadOnlySpan<byte> tga)
    {
        if (tga.Length < HeaderSize) return false;
        int idLength = tga[0], colourMapType = tga[1], imageType = tga[2];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(tga[12..]), height = BinaryPrimitives.ReadUInt16LittleEndian(tga[14..]);
        int bpp = tga[16], alphaBits = tga[17] & 0x0F;
        if (colourMapType != 0 || imageType is not (2 or 10) || bpp != 16 || alphaBits == 0 || width == 0 || height == 0) return false;
        long pixels = (long)width * height;
        int p = HeaderSize + idLength;
        long done = 0;
        while (done < pixels)
        {
            if (imageType == 2)
            {
                if (p + 2 > tga.Length) return false;
                if ((BinaryPrimitives.ReadUInt16LittleEndian(tga[p..]) & 0x8000) != 0) return false;
                p += 2;
                done++;
                continue;
            }
            if (p >= tga.Length) return false;
            int head = tga[p++], count = (head & 0x7F) + 1;
            int values = (head & 0x80) != 0 ? 1 : count;
            for (int i = 0; i < values; i++)
            {
                if (p + 2 > tga.Length) return false;
                if ((BinaryPrimitives.ReadUInt16LittleEndian(tga[p..]) & 0x8000) != 0) return false;
                p += 2;
            }
            done += count;
        }
        return true;
    }

    private static byte[] Patched(byte[] tga)
    {
        var copy = (byte[])tga.Clone();
        copy[17] = (byte)(copy[17] & 0xF0);
        return copy;
    }
}
