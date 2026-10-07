using System.Buffers.Binary;
using System.Globalization;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Formats;
using Cairn.Formats.Audio;
using Cairn.Formats.Imaging;
using Cairn.Formats.Rfl;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Vfx.Formats;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Facts;

/// <summary>The one-line summary of a packfile entry shown in the list's Info column.</summary>
/// <param name="Text">"1024x1024, 32-bit, RLE compressed"; empty when there is nothing to say about the type.</param>
/// <param name="IsUnreadable">True when the data could not be read as its type (<paramref name="Text"/> is then a short marker).</param>
/// <param name="Detail">Why the data could not be read (for a tooltip), or null.</param>
public sealed record VppInfoLine(string Text, bool IsUnreadable = false, string? Detail = null)
{
    /// <summary>Nothing to say (a type Cairn does not summarise).</summary>
    public static VppInfoLine None { get; } = new(string.Empty);

    /// <summary>The full text for a tooltip: the reason when unreadable, else the text itself; null when empty.</summary>
    public string? ToolTip => IsUnreadable && Detail is { Length: > 0 } ? $"{Text}: {Detail}" : Text.Length > 0 ? Text : null;
}

/// <summary>
/// Bare one-line summaries of packfile entries ("1024x1024, 32-bit, RLE compressed", "22,050 Hz, 16-bit mono,
/// 1.2 s", "Level by Author (Friday, March 06, 2026 at 17:23:10)") for the entry list. Only what a type's header
/// holds is read (whole files only for meshes, tables and animated-texture scripts, which need a walk or a parse),
/// through the same readers as <see cref="VppFacts"/>. Never throws: data that cannot be read gives
/// <see cref="UnreadableText"/>. Numbers and dates use the invariant culture so the column reads the same everywhere.
/// </summary>
public static class VppInfo
{
    /// <summary>The marker shown for data that cannot be read as its type.</summary>
    public const string UnreadableText = "unreadable";

    /// <summary>The level save time's format: "Friday, March 06, 2026 at 17:23:10".</summary>
    public const string LevelTimeFormat = "dddd, MMMM dd, yyyy 'at' HH:mm:ss";

    // Most headers fit the small read; the larger one covers tags and metadata in front of them.
    private const int SmallHead = 4 * 1024;
    private const int HeadBytes = 64 * 1024;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Summarises a packfile entry (read from wherever its data is: the archive, a file on disk, memory).</summary>
    /// <param name="item">The entry.</param>
    /// <param name="zone">The time zone level save times are shown in; the local zone when null.</param>
    public static VppInfoLine Summarize(VppItem item, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Summarize(item.Name, item.Source.Open, item.Size, zone);
    }

    /// <summary>Summarises data under a name. Never throws.</summary>
    /// <param name="name">The entry name (its extension picks the reader unless the content says otherwise).</param>
    /// <param name="open">Opens a fresh stream over the data.</param>
    /// <param name="size">The data's length in bytes.</param>
    /// <param name="zone">The time zone level save times are shown in; the local zone when null.</param>
    public static VppInfoLine Summarize(string name, Func<Stream> open, long size, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(open);
        if (size == 0) return new VppInfoLine("empty");
        try
        {
            var input = new VppFactInput(name, open, size, VppFactsContext.None);
            string ext = VppNames.ExtensionOf(name);
            // Content first, as in the details pane: an entry named for a type it does not hold is read as what it is.
            string? actual = VppFacts.SniffExtension(input.ReadHead(64));
            string prefix = string.Empty;
            if (actual is not null && !VppFacts.SameFamily(actual, ext))
            {
                ext = actual;
                prefix = actual.TrimStart('.').ToUpperInvariant() + " data: ";
            }
            string text = Line(ext, input, zone ?? TimeZoneInfo.Local);
            return text.Length == 0 ? VppInfoLine.None : new VppInfoLine(prefix + text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new VppInfoLine(UnreadableText, IsUnreadable: true, ex.Message);
        }
    }

    private static string Line(string ext, VppFactInput input, TimeZoneInfo zone) => ext switch
    {
        ".tga" => Tga(input),
        ".dds" => Dds(input),
        ".vbm" => Vbm(input),
        ".png" or ".jpg" or ".jpeg" => StbImage(input),
        ".psd" => Psd(input),
        ".wav" => Pcm(HeadThenAll(input, SmallHead, b => AudioProbe.ProbeWav(b, input.Size, input.Name))),
        ".aif" or ".aiff" or ".aifc" => Pcm(Aiff(input)),
        ".ogg" => Ogg(input),
        ".mp3" => Mp3(input),
        ".v3m" or ".v3c" or ".v3d" => Mesh(input),
        ".rfa" => Clip(input),
        ".mvf" => "legacy motion file",
        ".vfx" => Effect(input),
        ".atx" => AnimatedTexture(input),
        ".tbl" => Table(input),
        ".txt" or ".log" or ".ini" => Count(LineCount(VppFacts.DecodeText(input.ReadAll(), out _)), "line"),
        ".vf" => Font(input),
        ".rfg" => Count(VppFacts.GroupCount(input.ReadHead(12), input.Name), "group"),
        ".rfl" => Level(input, zone),
        _ => string.Empty,
    };

    /// <summary>
    /// Runs <paramref name="read"/> on the first <paramref name="headBytes"/>, then on the first 64 KB, then on the whole
    /// entry, each only when the one before was not enough (a header behind a long tag or metadata block).
    /// </summary>
    private static T HeadThenAll<T>(VppFactInput input, int headBytes, Func<byte[], T> read)
    {
        int[] sizes = headBytes < HeadBytes ? [headBytes, HeadBytes] : [headBytes];
        foreach (int bytes in sizes)
        {
            byte[] head = input.ReadHead(bytes);
            if (head.Length >= input.Size) return read(head);
            try
            {
                return read(head);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // too short: try more
            }
        }
        return read(input.ReadAll());
    }

    // ---- images ------------------------------------------------------------------------------------------------

    private static string Size(int width, int height) => string.Create(Inv, $"{width}x{height}");

    private static string Tga(VppFactInput input)
    {
        byte[] b = input.ReadHead(18);
        var info = TgaCodec.Probe(b, input.Name);
        int type = b[2], depth = b[16];
        string pixels = type is 1 or 9 ? "8-bit paletted"
            : type is 3 or 11 ? $"{depth}-bit greyscale"
            : $"{depth}-bit";
        return $"{Size(info.Width, info.Height)}, {pixels}, {(type >= 9 ? "RLE compressed" : "uncompressed")}";
    }

    private static string Dds(VppFactInput input)
    {
        byte[] b = input.ReadHead(128);
        var info = DdsCodec.Probe(b, input.Name);
        int mips = info.MipLevels ?? 1;
        var parts = new List<string> { Size(info.Width, info.Height), ShortFormat(info.Format), mips > 1 ? Count(mips, "mipmap") : "no mipmaps" };
        // DDSCAPS2_CUBEMAP in dwCaps2 (file offset 112).
        if (b.Length >= 116 && (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(112)) & 0x200) != 0) parts.Add("cube map");
        return string.Join(", ", parts);
    }

    private static string Vbm(VppFactInput input)
    {
        var info = VbmCodec.ReadInfo(input.ReadHead(32), input.Name);
        return info.FrameCount > 1
            ? $"{Size(info.Width, info.Height)}, {Count(info.FrameCount, "frame")}, {info.Fps.ToString(Inv)} fps"
            : $"{Size(info.Width, info.Height)}, {ShortFormat(info.Format)}";
    }

    private static string StbImage(VppFactInput input)
    {
        var info = HeadThenAll(input, SmallHead, b => ImageProbe.Probe(b, input.Name));
        return $"{Size(info.Width, info.Height)}, {(info.Format == EngineFormat.Argb8888 ? "32-bit" : "24-bit")}";
    }

    private static string Psd(VppFactInput input)
    {
        byte[] b = input.ReadHead(26);
        if (b.Length < 26 || !b.AsSpan(0, 4).SequenceEqual("8BPS"u8)) throw new AssetFormatException($"'{input.Name}' is not a Photoshop document.");
        int channels = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(12));
        int bits = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(22));
        return $"{Size((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(18)), (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(14)))}, {bits.ToString(Inv)}-bit, {Count(channels, "channel")}";
    }

    /// <summary>"DXT1", "32-bit ARGB", "16-bit RGB 565", ...</summary>
    internal static string ShortFormat(EngineFormat format) => format switch
    {
        EngineFormat.Dxt1 => "DXT1",
        EngineFormat.Dxt2 => "DXT2",
        EngineFormat.Dxt3 => "DXT3",
        EngineFormat.Dxt4 => "DXT4",
        EngineFormat.Dxt5 => "DXT5",
        EngineFormat.Argb8888 => "32-bit ARGB",
        EngineFormat.Rgb888 => "24-bit RGB",
        EngineFormat.Rgb565 => "16-bit RGB 565",
        EngineFormat.Argb1555 => "16-bit ARGB 1555",
        EngineFormat.Argb4444 => "16-bit ARGB 4444",
        EngineFormat.Paletted8 => "8-bit paletted",
        EngineFormat.Alpha8 => "8-bit alpha",
        _ => "unknown format",
    };

    // ---- audio -------------------------------------------------------------------------------------------------

    private static AudioInfo Aiff(VppFactInput input)
    {
        var info = HeadThenAll(input, SmallHead, b => AudioProbe.ProbeAiff(b, input.Name));
        // IMA4 counts its frames from the data present, so the head alone would undercount.
        return info.Codec.Contains("ima4", StringComparison.Ordinal) && input.Size > SmallHead ? AudioProbe.ProbeAiff(input.ReadAll(), input.Name) : info;
    }

    /// <summary>"22,050 Hz, 16-bit mono, 1.2 s" (PCM) or "22,050 Hz, IMA ADPCM mono, 1.2 s".</summary>
    private static string Pcm(AudioInfo info)
    {
        // AIFF-C names its codec "IMA4 ADPCM ('ima4')": the label is enough here
        int id = info.Codec.IndexOf(" ('", StringComparison.Ordinal);
        string codec = id > 0 ? info.Codec[..id] : info.Codec;
        string sample = codec.StartsWith("PCM", StringComparison.Ordinal)
            ? info.BitsPerSample is { } bits ? $"{bits.ToString(Inv)}-bit " : string.Empty
            : codec + " ";
        var parts = new List<string>();
        if (info.SampleRate is { } rate) parts.Add(Hz(rate));
        parts.Add(sample + Channels(info.Channels));
        if (info.Duration is { } d) parts.Add(Duration(d));
        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

    private static string Ogg(VppFactInput input)
    {
        AudioInfo info;
        using (var stream = input.Open()) info = AudioProbe.ProbeOgg(stream, input.Name);
        return Compressed(info, bitrate: false);
    }

    private static string Mp3(VppFactInput input) =>
        Compressed(AudioProbe.ProbeMp3(input.ReadHead(256 << 10), input.Size, input.Name), bitrate: true);

    /// <summary>"44,100 Hz stereo, 3:05" (with ", 128 kbps" before the time when asked).</summary>
    private static string Compressed(AudioInfo info, bool bitrate)
    {
        string first = string.Join(" ", new[] { info.SampleRate is { } rate ? Hz(rate) : string.Empty, Channels(info.Channels) }.Where(p => p.Length > 0));
        var parts = new List<string> { first };
        if (bitrate && info.BitrateKbps is { } kbps) parts.Add($"{kbps.ToString("N0", Inv)} kbps");
        if (info.Duration is { } d) parts.Add(Duration(d));
        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

    private static string Hz(int rate) => $"{rate.ToString("N0", Inv)} Hz";

    private static string Channels(int? channels) => channels switch
    {
        null => string.Empty,
        1 => "mono",
        2 => "stereo",
        { } n => Count(n, "channel"),
    };

    /// <summary>"1.2 s" under a minute, else "3:05" or "1:02:03".</summary>
    internal static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 59.95) return string.Create(Inv, $"{t.TotalSeconds:0.0} s");
        long seconds = (long)Math.Round(t.TotalSeconds);
        return seconds >= 3600
            ? string.Create(Inv, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(Inv, $"{seconds / 60}:{seconds % 60:00}");
    }

    // ---- meshes, clips, effects ---------------------------------------------------------------------------------

    private static string Mesh(VppFactInput input)
    {
        var mesh = V3dProbe.Probe(input.ReadAll(), input.Name);
        if (!mesh.StructureReadable && mesh.SubmeshCount == 0 && mesh.BoneCount == 0)
            throw new AssetFormatException($"'{input.Name}': the mesh's sections could not be read.");
        var parts = new List<string>
        {
            mesh.Kind == V3dKind.Character ? Count(mesh.BoneCount, "bone") : Count(mesh.SubmeshCount, "submesh", "submeshes"),
        };
        if (mesh.StructureReadable) parts.Add(Count(mesh.TriangleCounts.Sum(), "triangle"));
        int lods = mesh.LodCounts.IsDefaultOrEmpty ? 1 : mesh.LodCounts.Max();
        if (lods > 1) parts.Add(Count(lods, "LOD"));
        return string.Join(", ", parts);
    }

    private static string Clip(VppFactInput input)
    {
        RfaProbeResult clip;
        using (var stream = input.Open()) clip = RfaProbe.Probe(stream, input.Name);
        // 4,800 ticks a second; the game's clips are authored at 30 frames a second (160 ticks a frame).
        double frames = clip.Duration / 160.0;
        string text = $"{frames.ToString("#,0.#", Inv)} {(frames == 1 ? "frame" : "frames")}, {Duration(TimeSpan.FromSeconds(clip.Duration / 4800.0))}, {Count(clip.BoneCount, "bone")}";
        return clip.HasMorph ? text + ", morph" : text;
    }

    private static string Effect(VppFactInput input)
    {
        var probe = HeadThenAll(input, 1024, b => VfxProbe.Read(b, input.Name));
        var c = probe.Counts;
        int objects = c.Meshes + c.ParticleSystems + c.Lights + c.Dummies + c.Spacewarps + c.Cameras;
        // Effects play at 15 frames a second.
        return $"{Count(probe.EndFrame, "frame")}, {Duration(TimeSpan.FromSeconds(probe.EndFrame / 15.0))}, {Count(objects, "object")}";
    }

    private static string AnimatedTexture(VppFactInput input)
    {
        var parse = AtxParser.Parse(VppFacts.DecodeText(input.ReadAll(), out _), input.Name);
        if (parse.Model is not { } model) throw new AssetFormatException("the animated texture has TOML syntax errors");
        var mode = model.Header.EffectiveAnimationMode;
        string label = AtxSchema.AnimationModeLabel(mode).ToLowerInvariant();
        return mode == AtxAnimationMode.Static
            ? $"{Count(model.Frames.Count, "frame")}, {label}"
            : $"{Count(model.Frames.Count, "frame")}, {model.Header.EffectiveFrameTimeMs.ToString("N0", Inv)} ms each, {label}";
    }

    // ---- text, tables, fonts -----------------------------------------------------------------------------------

    private static int LineCount(string text)
    {
        if (text.Length == 0) return 0;
        int lines = 1;
        foreach (char ch in text) if (ch == '\n') lines++;
        return text.EndsWith('\n') ? lines - 1 : lines;
    }

    // What the table module calls each stock table (by file name), and the level tables by pattern.
    private static readonly Dictionary<string, string> TableKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapons"] = "weapons table", ["entity"] = "entities table", ["ammo"] = "ammunition table", ["items"] = "items table",
        ["clutter"] = "clutter table", ["pc_multi"] = "multiplayer characters", ["fpgun"] = "first-person arms",
        ["vclip"] = "video clips", ["emitters"] = "particle emitters", ["effects"] = "glares", ["explosion"] = "particle explosions",
        ["sounds"] = "game sounds", ["foley"] = "foley sounds", ["materials"] = "materials table", ["hud"] = "HUD layout",
        ["hud_personas"] = "HUD personas", ["personas"] = "voice personas", ["game"] = "game constants",
        ["movemodes"] = "movement modes", ["ponr"] = "points of no return", ["credits"] = "credits", ["endgame"] = "endgame texts",
        ["strings"] = "interface strings", ["events"] = "editor events", ["af_game"] = "Alpine game options",
        ["af_ui"] = "Alpine interface options", ["af_level_quirks"] = "Alpine level quirks",
    };

    /// <summary>The kind of table a file name stands for ("weapons table", "level info"), or null.</summary>
    internal static string? TableKind(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (TableKinds.TryGetValue(stem, out var kind)) return kind;
        if (stem.StartsWith("af_client", StringComparison.OrdinalIgnoreCase)) return "Alpine client options";
        if (stem.EndsWith("_info", StringComparison.OrdinalIgnoreCase)) return "level info";
        if (stem.EndsWith("_text", StringComparison.OrdinalIgnoreCase)) return "level text";
        return null;
    }

    // The field that starts each entry, where it is not $Name: (the table module's schemas say the same).
    private static readonly Dictionary<string, string> EntryMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["clutter"] = "Class Name", ["items"] = "Class Name", ["events"] = "Event Name", ["ponr"] = "Level",
    };

    /// <summary>"weapons table, 30 entries" (entry fields), "level info, 3 settings" ($ fields), else lines.</summary>
    private static string Table(VppFactInput input)
    {
        string text = VppFacts.DecodeText(input.ReadAll(), out _);
        var fields = TblParser.ParseFields(text);
        string stem = Path.GetFileNameWithoutExtension(input.Name);
        int entries = EntryMarkers.TryGetValue(stem, out var marker)
            ? fields.Count(f => f.Is('$', marker))
            : fields.Count(f => f.Is('$', "Name")) is > 0 and var named ? named : fields.Count(f => f.Is('$', "Class Name"));
        int settings = fields.Count(f => f.Prefix == '$');
        string count = entries > 0 ? Count(entries, "entry", "entries")
            : settings > 0 ? Count(settings, "setting")
            : Count(LineCount(text), "line");
        return TableKind(input.Name) is { } kind ? $"{kind}, {count}" : count;
    }

    private static string Font(VppFactInput input)
    {
        var font = VppFacts.ReadFontHeader(input.ReadHead(48), input.Name);
        return $"{Count(font.Glyphs, "glyph")}, {font.Height.ToString(Inv)} px high";
    }

    // ---- levels ------------------------------------------------------------------------------------------------

    private static string Level(VppFactInput input, TimeZoneInfo zone)
    {
        RflSummary level;
        using (var stream = input.Open()) level = RflReader.ReadIdentity(stream, input.Name);
        return LevelLine(level.LevelName, level.Author, level.SavedUtc, level.DateText, zone) is { Length: > 0 } line ? line : level.VersionLabel;
    }

    /// <summary>
    /// "NAME by AUTHOR (Friday, March 06, 2026 at 17:23:10)": the save time in <paramref name="zone"/> (falling back
    /// to the editor's own date text); missing parts are left out, never shown empty.
    /// </summary>
    public static string LevelLine(string? levelName, string? author, DateTimeOffset? savedUtc, string? dateText, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        string name = levelName?.Trim() ?? string.Empty;
        string by = author?.Trim() ?? string.Empty;
        string time = savedUtc is { } saved ? TimeZoneInfo.ConvertTime(saved, zone).ToString(LevelTimeFormat, Inv) : dateText?.Trim() ?? string.Empty;
        string text = name;
        if (by.Length > 0) text = text.Length > 0 ? $"{text} by {by}" : $"by {by}";
        if (time.Length > 0) text = text.Length > 0 ? $"{text} ({time})" : time;
        return text;
    }

    private static string Count(int n, string one, string? many = null) => $"{n.ToString("N0", Inv)} {(n == 1 ? one : many ?? one + "s")}";
}
