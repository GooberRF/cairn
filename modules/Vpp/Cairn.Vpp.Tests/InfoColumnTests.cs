using System.Buffers.Binary;
using System.Text;
using Cairn.Formats.Rfl;
using Cairn.Vpp.Facts;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

/// <summary>The one-line summaries of the packfile list's Info column (<see cref="VppInfo"/>), from tiny files built here.</summary>
public sealed class InfoColumnTests(ITestOutputHelper output)
{
    private static VppInfoLine Info(string name, byte[] bytes, TimeZoneInfo? zone = null) =>
        VppInfo.Summarize(name, () => new MemoryStream(bytes, writable: false), bytes.Length, zone);

    private static string Text(string name, byte[] bytes, TimeZoneInfo? zone = null)
    {
        var line = Info(name, bytes, zone);
        Assert.False(line.IsUnreadable, $"{name}: {line.Detail}");
        return line.Text;
    }

    // ---- images ---------------------------------------------------------------------------------------------

    /// <summary>A TGA header (no image ID) plus <paramref name="tail"/>.</summary>
    private static byte[] Tga(int type, int depth, int width, int height, int descriptor = 0, int colorMapLength = 0, int colorMapBits = 0, int tail = 64)
    {
        var b = new byte[18 + tail];
        b[1] = (byte)(colorMapLength > 0 ? 1 : 0);
        b[2] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), (ushort)colorMapLength);
        b[7] = (byte)colorMapBits;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), (ushort)height);
        b[16] = (byte)depth;
        b[17] = (byte)descriptor;
        return b;
    }

    [Fact]
    public void Tga_SizeDepthAndCompression()
    {
        Assert.Equal("1024x1024, 32-bit, RLE compressed", Text("wall.tga", Tga(10, 32, 1024, 1024, descriptor: 8)));
        Assert.Equal("256x64, 24-bit, uncompressed", Text("wall.tga", Tga(2, 24, 256, 64)));
        Assert.Equal("128x128, 16-bit, uncompressed", Text("wall.tga", Tga(2, 16, 128, 128, descriptor: 1)));
        Assert.Equal("64x64, 8-bit paletted, uncompressed", Text("pal.tga", Tga(1, 8, 64, 64, colorMapLength: 256, colorMapBits: 24)));
        Assert.Equal("64x64, 8-bit paletted, RLE compressed", Text("pal.tga", Tga(9, 8, 64, 64, colorMapLength: 256, colorMapBits: 24)));
        Assert.Equal("32x32, 8-bit greyscale, uncompressed", Text("grey.tga", Tga(3, 8, 32, 32)));
        Assert.Equal("32x32, 8-bit greyscale, RLE compressed", Text("grey.tga", Tga(11, 8, 32, 32)));
    }

    /// <summary>A DDS header: <paramref name="fourCc"/> when given, else 32-bit ARGB.</summary>
    private static byte[] Dds(int width, int height, int mips, string? fourCc, uint caps2 = 0)
    {
        var b = new byte[128 + 64];
        "DDS "u8.CopyTo(b);
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        U32(4, 124);
        U32(8, 0x1007 | (mips > 0 ? 0x20000u : 0));
        U32(12, (uint)height);
        U32(16, (uint)width);
        U32(28, (uint)mips);
        U32(76, 32);
        if (fourCc is not null)
        {
            U32(80, 0x4);
            Encoding.ASCII.GetBytes(fourCc).CopyTo(b, 84);
        }
        else
        {
            U32(80, 0x41);
            U32(88, 32);
            U32(92, 0xFF0000); U32(96, 0xFF00); U32(100, 0xFF); U32(104, 0xFF000000);
        }
        U32(108, 0x401008);
        U32(112, caps2);
        return b;
    }

    [Fact]
    public void Dds_SizeFormatMipsAndCubeMaps()
    {
        Assert.Equal("512x512, DXT1, 10 mipmaps", Text("rock.dds", Dds(512, 512, 10, "DXT1")));
        Assert.Equal("256x128, DXT5, no mipmaps", Text("rock.dds", Dds(256, 128, 0, "DXT5")));
        Assert.Equal("64x64, 32-bit ARGB, 7 mipmaps", Text("argb.dds", Dds(64, 64, 7, null)));
        Assert.Equal("128x128, DXT1, 8 mipmaps, cube map", Text("sky.dds", Dds(128, 128, 8, "DXT1", caps2: 0xFE00)));
        // a .tga that holds a DDS says so
        Assert.Equal("DDS data: 512x512, DXT1, 10 mipmaps", Text("named.tga", Dds(512, 512, 10, "DXT1")));
    }

    [Fact]
    public void Vbm_PngAndJpeg()
    {
        var vbm = new byte[32 + 16];
        ".vbm"u8.CopyTo(vbm);
        int[] header = [1, 128, 128, 0, 15, 8, 0];
        for (int i = 0; i < header.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(vbm.AsSpan(4 + 4 * i), header[i]);
        Assert.Equal("128x128, 8 frames, 15 fps", Text("anim.vbm", vbm));
        BinaryPrimitives.WriteInt32LittleEndian(vbm.AsSpan(24), 1);
        Assert.Equal("128x128, 16-bit ARGB 1555", Text("still.vbm", vbm));

        var png = new List<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] body)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
            png.AddRange(length); png.AddRange(Encoding.ASCII.GetBytes(type)); png.AddRange(body); png.AddRange(new byte[4]);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 256);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 64);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk("IHDR", ihdr);
        Chunk("IEND", []);
        Assert.Equal("256x64, 24-bit", Text("ui.png", [.. png]));

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0, 17, 8, 0, 32, 0, 48, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9];
        Assert.Equal("48x32, 24-bit", Text("photo.jpg", jpeg));
    }

    // ---- audio ----------------------------------------------------------------------------------------------

    private static byte[] Wave(ushort format, ushort channels, int rate, ushort bits, int dataBytes)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        ushort blockAlign = (ushort)(channels * Math.Max(1, bits / 8));
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write(format); w.Write(channels); w.Write(rate); w.Write(rate * blockAlign); w.Write(blockAlign); w.Write(bits);
        w.Write("data"u8); w.Write(dataBytes); w.Write(new byte[dataBytes]);
        return s.ToArray();
    }

    [Fact]
    public void Wave_RateDepthChannelsAndLength_FromTheHeaderOnly()
    {
        // 3 s of 16-bit mono: larger than the header read, so the length comes from the data chunk's size field
        Assert.Equal("22,050 Hz, 16-bit mono, 3.0 s", Text("thunder.wav", Wave(1, 1, 22050, 16, 22050 * 2 * 3)));
        Assert.Equal("11,025 Hz, 8-bit stereo, 1.2 s", Text("creak.wav", Wave(1, 2, 11025, 8, 11025 * 2 * 6 / 5)));
        Assert.Equal("44,100 Hz, 16-bit stereo, 1:05", Text("music.wav", Wave(1, 2, 44100, 16, 44100 * 4 * 65)));
        Assert.Equal("22,050 Hz, IMA ADPCM mono, 1.0 s", Text("adpcm.wav", Wave(0x11, 1, 22050, 4, 22050)));
    }

    [Fact]
    public void Clip_FramesLengthAndBones()
    {
        var b = new byte[0x50 + 16];
        "VMVF"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), 7200); // end time: 1.5 s at 4,800 ticks a second
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), 12);
        Assert.Equal("45 frames, 1.5 s, 12 bones", Text("walk.rfa", b));
    }

    [Fact]
    public void Durations_AreShort()
    {
        Assert.Equal("1.2 s", VppInfo.Duration(TimeSpan.FromSeconds(1.23)));
        Assert.Equal("59.9 s", VppInfo.Duration(TimeSpan.FromSeconds(59.94)));
        Assert.Equal("1:00", VppInfo.Duration(TimeSpan.FromSeconds(59.96)));
        Assert.Equal("3:05", VppInfo.Duration(TimeSpan.FromSeconds(185.2)));
        Assert.Equal("1:02:03", VppInfo.Duration(TimeSpan.FromSeconds(3723)));
    }

    // ---- levels ---------------------------------------------------------------------------------------------

    /// <summary>A fixed zone (UTC-03:30, no daylight saving) so the expected text does not depend on the machine.</summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test-0330", TimeSpan.FromHours(-3.5), "Test", "Test");

    [Fact]
    public void Level_NameAuthorAndSaveTime_InTheGivenZone()
    {
        // SyntheticLevel.Timestamp is 2023-11-14 22:13:20 UTC
        Assert.Equal("Synthetic by Tester (Tuesday, November 14, 2023 at 18:43:20)", Text("level.rfl", SyntheticLevel.Stock(), Zone));
        Assert.Equal("Alpine by Me (Tuesday, November 14, 2023 at 22:13:20)", Text("alpine.rfl", SyntheticLevel.AlpineShortProps(), TimeZoneInfo.Utc));
    }

    [Fact]
    public void LevelLine_FormatsExactly_AndDegradesWithoutEmptyParts()
    {
        var saved = new DateTimeOffset(2026, 3, 6, 20, 53, 10, TimeSpan.Zero);
        Assert.Equal("LEVELNAME by AUTHORNAME (Friday, March 06, 2026 at 17:23:10)", VppInfo.LevelLine("LEVELNAME", "AUTHORNAME", saved, "ignored", Zone));
        Assert.Equal("LEVELNAME (Friday, March 06, 2026 at 17:23:10)", VppInfo.LevelLine("LEVELNAME", " ", saved, null, Zone));
        Assert.Equal("LEVELNAME by AUTHORNAME", VppInfo.LevelLine("LEVELNAME", "AUTHORNAME", null, null, Zone));
        Assert.Equal("LEVELNAME by AUTHORNAME (Friday, March 06, 2026 17:23:10)", VppInfo.LevelLine("LEVELNAME", "AUTHORNAME", null, "Friday, March 06, 2026 17:23:10", Zone));
        Assert.Equal("by AUTHORNAME", VppInfo.LevelLine(null, "AUTHORNAME", null, "", Zone));
        Assert.Equal("Friday, March 06, 2026 at 17:23:10", VppInfo.LevelLine("", null, saved, null, Zone));
        Assert.Equal(string.Empty, VppInfo.LevelLine(null, null, null, null, Zone));
    }

    [Fact]
    public void ReadIdentity_MatchesTheFullSummary()
    {
        foreach (var bytes in new[] { SyntheticLevel.Stock(), SyntheticLevel.AlpineShortProps() })
        {
            var full = RflReader.ReadSummary(new MemoryStream(bytes), "x.rfl");
            var id = RflReader.ReadIdentity(new MemoryStream(bytes), "x.rfl");
            Assert.Equal(full.LevelName, id.LevelName);
            Assert.Equal(full.Author, id.Author);
            Assert.Equal(full.DateText, id.DateText);
            Assert.Equal(full.SavedUtc, id.SavedUtc);
            Assert.Equal(full.Version, id.Version);
        }
    }

    // ---- tables, text, broken data ---------------------------------------------------------------------------

    [Fact]
    public void Tables_KindAndEntries()
    {
        static byte[] T(string s) => Encoding.Latin1.GetBytes(s.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal("weapons table, 2 entries", Text("weapons.tbl", T("#Primary Weapons\n$Name: \"a\"\n$Ammo: 1\n$Name: \"b\"\n#End\n")));
        Assert.Equal("clutter table, 2 entries", Text("clutter.tbl", T("#Clutter\n$Class Name: \"a\"\n$Life: 10\n$Class Name: \"b\"\n$Life: 5\n#End\n")));
        Assert.Equal("game constants, 2 settings", Text("game.tbl", T("$Gravity: -9.8\n$Max Speed: 10\n")));
        Assert.Equal("level info, 1 setting", Text("dm-x_info.tbl", T("#Start\n$Use Vertex Lighting: false\n#End\n")));
        Assert.Equal("level text, 3 lines", Text("l1s1_text.tbl", T("one\ntwo\nthree\n")));
        Assert.Equal("1 entry", Text("mine.tbl", T("$Name: \"x\"\n")));
        Assert.Equal("4 lines", Text("readme.txt", T("a\nb\n\nc")));
    }

    [Fact]
    public void BrokenData_IsAShortMarker_NeverAnException()
    {
        var garbage = TestData.Bytes(100, 7);
        garbage[2] = 77; // no TGA image type
        foreach (string name in new[] { "x.tga", "x.dds", "x.vbm", "x.png", "x.wav", "x.ogg", "x.v3m", "x.v3c", "x.rfa", "x.vfx", "x.vf", "x.rfg", "x.rfl", "x.aif", "x.mp3", "x.psd" })
        {
            var line = Info(name, garbage);
            Assert.True(line.IsUnreadable, $"{name}: '{line.Text}'");
            Assert.Equal(VppInfo.UnreadableText, line.Text);
            Assert.NotNull(line.ToolTip);
        }
        var gone = VppInfo.Summarize("gone.tga", () => throw new IOException("vanished"), 10);
        Assert.True(gone.IsUnreadable);
        Assert.Contains("vanished", gone.ToolTip);
        Assert.Equal("empty", Info("nothing.tga", []).Text);
        Assert.Equal(string.Empty, Info("thing.xyz", [1, 2, 3]).Text);
    }

    // ---- the game's own files (skipped without a game folder) ------------------------------------------------

    [Fact]
    public void GameData_EveryTypeSummarises_InOneShortLine()
    {
        if (TestData.GamePackfiles().Count == 0) return;
        var failures = new List<string>();
        foreach (var (ext, items) in FactsAndAudioTests.Samples().OrderBy(kv => kv.Key))
        {
            foreach (var item in items)
            {
                var line = VppInfo.Summarize(item);
                Assert.True(line.Text.Length <= 120, $"{item.Name}: {line.Text}");
                Assert.DoesNotContain('\n', line.Text);
                // .v3d files are exporter output the game never loads; the probe cannot walk most of them
                if (line.IsUnreadable && ext is not (".txt" or ".log" or ".v3d")) failures.Add($"{item.Name}: {line.Detail}");
            }
            output.WriteLine($"{ext,-6} {items[0].Name}: {VppInfo.Summarize(items[0]).Text}");
        }
        // a few broken files exist in the wild; the stock-type readers must cope with nearly all of them
        output.WriteLine($"unreadable: {failures.Count}" + (failures.Count > 0 ? " - " + string.Join("; ", failures.Take(10)) : ""));
        Assert.True(failures.Count <= 3, string.Join("; ", failures));
    }
}
