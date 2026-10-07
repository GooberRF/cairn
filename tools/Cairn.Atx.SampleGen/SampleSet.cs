namespace Cairn.Atx.SampleGen;

/// <summary>
/// Builds the contents of <c>samples/</c>: a scrolling hazard-stripe animation, a shared alpha
/// mask, a DXT1 pair, a deliberately mismatched frame, and two .atx files — one clean and one that
/// trips a spread of lint rules.
/// </summary>
public static class SampleSet
{
    /// <summary>Frames in the hazard-stripe animation.</summary>
    public const int HazardFrames = 8;

    /// <summary>Edge length of the hazard-stripe frames.</summary>
    public const int HazardSize = 64;

    /// <summary>Writes every sample file into <paramref name="directory"/>, creating it if needed.</summary>
    public static IReadOnlyList<string> WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();

        for (int frame = 0; frame < HazardFrames; frame++)
        {
            string name = $"hazard_strip_{frame:00}.tga";
            Write(directory, name, TinyImageWriter.Tga24(HazardSize, HazardSize, HazardStripes(frame)), written);
        }

        Write(directory, "hazard_strip_mask.tga",
            TinyImageWriter.Tga8Grey(HazardSize, HazardSize, MaskGradient()), written);

        // A 32x32 frame among 64x64 ones: the ATX011 sample.
        Write(directory, "wrong_size_32.tga", TinyImageWriter.Tga24(32, 32, SolidBgr(32, 32, 40, 40, 200)), written);

        for (int frame = 0; frame < 2; frame++)
        {
            int shift = frame;
            Write(directory, $"dxt_pulse_{frame:00}.dds",
                TinyImageWriter.DdsDxt1(64, 64, (bx, by) =>
                {
                    bool on = ((bx + by + shift) & 1) == 0;
                    ushort hot = Rgb565(255, 96, 0);
                    ushort cold = Rgb565(24, 24, 32);
                    return on ? (hot, cold, 0x00000000u) : (cold, hot, 0x00000000u);
                }),
                written);
        }

        Write(directory, "hazard_strip.atx", System.Text.Encoding.UTF8.GetBytes(CleanAtx()), written);
        Write(directory, "broken_example.atx", System.Text.Encoding.UTF8.GetBytes(BrokenAtx()), written);
        return written;
    }

    private static void Write(string directory, string name, byte[] bytes, List<string> written)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        written.Add(path);
    }

    /// <summary>Diagonal yellow/black hazard stripes, scrolled by one stripe per frame.</summary>
    private static byte[] HazardStripes(int frame)
    {
        const int stripe = 8;
        var pixels = new byte[HazardSize * HazardSize * 3];
        // Scroll two whole stripes across the eight frames, so the loop joins seamlessly.
        int offset = frame * (stripe * 2 / HazardFrames);
        for (int y = 0; y < HazardSize; y++)
        {
            for (int x = 0; x < HazardSize; x++)
            {
                int band = ((x + y + offset) / stripe) & 1;
                // A little vertical shading so the frames read as a surface, not a test pattern.
                int shade = 24 + (y * 24 / HazardSize);
                byte b, g, r;
                if (band == 0) { b = 0; g = (byte)(190 + shade / 4); r = (byte)(215 + shade / 8); }
                else { b = (byte)(shade / 2); g = (byte)(shade / 2); r = (byte)(shade / 2); }
                int i = (y * HazardSize + x) * 3;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
            }
        }
        return pixels;
    }

    /// <summary>A soft-edged circular mask, so the sample actually shows what a mask does.</summary>
    private static byte[] MaskGradient()
    {
        var grey = new byte[HazardSize * HazardSize];
        double centre = (HazardSize - 1) / 2.0;
        double radius = HazardSize * 0.46;
        for (int y = 0; y < HazardSize; y++)
        {
            for (int x = 0; x < HazardSize; x++)
            {
                double dx = x - centre, dy = y - centre;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                double t = Math.Clamp((radius - distance) / (radius * 0.35), 0, 1);
                grey[y * HazardSize + x] = (byte)Math.Round(t * 255);
            }
        }
        return grey;
    }

    private static byte[] SolidBgr(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 3] = b;
            pixels[i * 3 + 1] = g;
            pixels[i * 3 + 2] = r;
        }
        return pixels;
    }

    private static ushort Rgb565(int r, int g, int b) =>
        (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    private static string CleanAtx() => string.Join("\r\n",
    [
        "# hazard_strip.atx — a clean example.",
        "#",
        "# Rename this file after the texture you want to animate: an .atx called",
        "# mtl_panel01.atx takes over anywhere the level asks for mtl_panel01.tga.",
        "",
        "[header]",
        "",
        "# 12.5 frames a second.",
        "frame_time = 80",
        "",
        "# 2 = Loop.",
        "animation_mode = 2",
        "",
        "# Start playing as soon as the level loads.",
        "initially_on = true",
        "",
        "# The frames are 24-bit with no transparency of their own, so say up front that they",
        "# should be stored as 32-bit — the alpha mask below needs somewhere to live.",
        "format = \"8888\"",
        "",
        "# A shared transparency mask for every frame.",
        "alpha_mask = \"hazard_strip_mask.tga\"",
        "",
        "# Footsteps and bullet impacts on this surface sound like metal.",
        "material = \"metal\"",
        "",
        "[[frame]]",
        "file = \"hazard_strip_00.tga\"",
        "",
        "[[frame]]",
        "file = \"hazard_strip_01.tga\"",
        "",
        "[[frame]]",
        "file = \"hazard_strip_02.tga\"",
        "",
        "[[frame]]",
        "file = \"hazard_strip_03.tga\"",
        "",
        "# Hold this one a little longer so the cycle has a beat.",
        "[[frame]]",
        "file = \"hazard_strip_04.tga\"",
        "frame_time = 200",
        "",
        "[[frame]]",
        "file = \"hazard_strip_05.tga\"",
        "",
        "[[frame]]",
        "file = \"hazard_strip_06.tga\"",
        "",
        "[[frame]]",
        "file = \"hazard_strip_07.tga\"",
        "",
    ]);

    private static string BrokenAtx() => string.Join("\r\n",
    [
        "# broken_example.atx — deliberately wrong, to show what the Problems panel catches.",
        "# Open this next to hazard_strip.atx to compare.",
        "",
        "[header]",
        "",
        "# ATX021: below the 1 ms minimum, so the game clamps it.",
        "frame_time = 0",
        "",
        "# ATX022: only 0-3 are valid, so the game falls back to Static.",
        "animation_mode = 7",
        "",
        "# ATX030: has no effect while the mode is Static.",
        "initially_on = false",
        "",
        "# ATX005: not a pixel format the game knows.",
        "format = \"888_rgba\"",
        "",
        "# ATX006: not a material the game knows.",
        "material = \"metl\"",
        "",
        "# ATX023: not a setting the format has.",
        "frame_rate = 12",
        "",
        "[[frame]]",
        "file = \"hazard_strip_00.tga\"",
        "",
        "# ATX011: this frame is 32x32 while frame 0 is 64x64.",
        "[[frame]]",
        "file = \"wrong_size_32.tga\"",
        "",
        "# ATX010: no such file anywhere.",
        "[[frame]]",
        "file = \"hazard_strip_99.tga\"",
        "",
        "# ATX026 and ATX027: a folder path, and a name longer than 31 characters.",
        "[[frame]]",
        "file = \"textures/hazard_strip_extremely_long_name.tga\"",
        "",
        "# ATX004: an .atx cannot contain another .atx.",
        "[[frame]]",
        "file = \"hazard_strip.atx\"",
        "",
        "# ATX020 and ATX037: wrong type, and an empty value.",
        "[[frame]]",
        "file = \"hazard_strip_01.tga\"",
        "frame_time = \"120\"",
        "material = \"\"",
        "",
    ]);
}
