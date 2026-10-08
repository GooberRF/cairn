using System.Globalization;

namespace Cairn.Formats.Imaging;

/// <summary>
/// "Treat black as transparent" for the MPEG-2 backgrounds of PEG texture packs (format 2, which store no alpha). The
/// PlayStation 2 makes their alpha while its image processing unit turns a decoded picture into RGBA, from two
/// thresholds: a pixel whose red, green and blue are all below the first becomes transparent; with the second, one
/// whose components are all below it becomes half transparent; every other pixel is opaque. Red Faction's PS2 code sets
/// a first threshold of <see cref="GameThreshold"/> and no second one for a texture whose PEG entry has the
/// <see cref="UsesAlphaFlag"/> flag (no known file has it; every other background is opaque).
/// <see cref="SoftEdge"/> adds a half-transparent band up to twice the threshold, as the second threshold would.
/// </summary>
public sealed record PegBlackKey
{
    /// <summary>The threshold Red Faction's PlayStation 2 code uses.</summary>
    public const int GameThreshold = 25;

    /// <summary>The largest threshold offered (a quarter of the range: above it, dark colours vanish too).</summary>
    public const int MaxThreshold = 64;

    /// <summary>The PEG entry flag that makes the PlayStation 2 version key out black on that background.</summary>
    public const int UsesAlphaFlag = 0x10;

    /// <summary>A key with <paramref name="threshold"/> (clamped to 0 to <see cref="MaxThreshold"/>).</summary>
    /// <param name="threshold">Pixels whose red, green and blue are all below this become transparent; 0 keys out nothing.</param>
    /// <param name="softEdge">Pixels whose components are all below twice the threshold become half transparent.</param>
    public PegBlackKey(int threshold, bool softEdge = false)
    {
        Threshold = Math.Clamp(threshold, 0, MaxThreshold);
        SoftEdge = softEdge;
    }

    /// <summary>The PlayStation 2 version's own key (<see cref="GameThreshold"/>, no soft edge).</summary>
    public static PegBlackKey Game { get; } = new(GameThreshold);

    /// <summary>Pixels whose red, green and blue are all below this become transparent.</summary>
    public int Threshold { get; }

    /// <summary>Pixels whose components are all below twice <see cref="Threshold"/> (and not below it) become half transparent.</summary>
    public bool SoftEdge { get; }

    /// <summary>The upper end of the half-transparent band (exclusive): twice the threshold with a soft edge, else the threshold.</summary>
    public int SoftLimit => SoftEdge ? Threshold * 2 : Threshold;

    /// <summary>The alpha a pixel of this colour gets: 0, 128 (soft edge) or 255.</summary>
    public byte AlphaOf(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b));
        if (max < Threshold) return 0;
        return max < SoftLimit ? (byte)128 : (byte)255;
    }

    /// <summary>Sets the alpha of every pixel of <paramref name="image"/> (in place) and returns it.</summary>
    public BgraImage Apply(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4) p[i + 3] = AlphaOf(p[i + 2], p[i + 1], p[i]);
        return image;
    }

    /// <summary>
    /// The key a texture is decoded with: none for a texture that is not an MPEG-2 background; <paramref name="chosen"/>
    /// when given; else the game's own key when the texture's entry asks for it (<see cref="UsesAlphaFlag"/>).
    /// </summary>
    public static PegBlackKey? For(PegTexture texture, PegBlackKey? chosen)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (!texture.IsMpeg2) return null;
        return chosen ?? ((texture.Flags & UsesAlphaFlag) != 0 ? Game : null);
    }

    /// <summary>"black below 25 transparent" / "black below 25 transparent, soft edge to 50".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"black below {Threshold} transparent{(SoftEdge ? $", soft edge to {SoftLimit}" : string.Empty)}");
}
