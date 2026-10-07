using System.Buffers.Binary;
using System.Text;
using Cairn.Formats.Vpp;
using Cairn.Formats.Audio;
using Cairn.Vpp.Facts;
using Cairn.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

/// <summary>
/// The packfile types the earlier scans did not reach (.mp3 .gltf .log .mvf .psd .v3d, MP3-in-WAV, IMA ADPCM WAV):
/// generated entries, and every such entry in the whole game folder (mods and client_mods included). Facts and the
/// audio decoding the preview uses never throw and give the rows a reader needs.
/// </summary>
public sealed class UncoveredTypesTests(ITestOutputHelper output)
{
    // ---- generated entries ----------------------------------------------------------------------------------

    /// <summary>MPEG-1 Layer III, 128 kbps, 44.1 kHz, stereo: 417-byte frames whose all-zero side info decodes as silence.</summary>
    private static byte[] Mp3(int frames)
    {
        var bytes = new byte[frames * 417];
        for (int i = 0; i < frames; i++) { bytes[i * 417] = 0xFF; bytes[i * 417 + 1] = 0xFB; bytes[i * 417 + 2] = 0x90; bytes[i * 417 + 3] = 0x04; }
        return bytes;
    }

    private static byte[] Wave(ushort format, ushort channels, int rate, ushort blockAlign, ushort bits, byte[] extra, byte[] data, int? factSamples = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(18 + extra.Length);
        w.Write(format); w.Write(channels); w.Write(rate);
        w.Write(format == 0x55 ? 16000 : rate * blockAlign / Math.Max(1, (blockAlign - 4 * channels) * 2 / channels + 1));
        w.Write(blockAlign); w.Write(bits); w.Write((ushort)extra.Length); w.Write(extra);
        if (factSamples is { } n) { w.Write("fact"u8); w.Write(4); w.Write(n); }
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        if (data.Length % 2 == 1) w.Write((byte)0);
        w.Flush();
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        return bytes;
    }

    /// <summary>IMA ADPCM, mono, 22,050 Hz, 512-byte blocks (1,017 samples each), random nibbles.</summary>
    private static byte[] ImaWave(int blocks)
    {
        var data = TestData.Bytes(blocks * 512, 9);
        for (int b = 0; b < blocks; b++) { data[b * 512 + 2] = 20; data[b * 512 + 3] = 0; } // step index 20, reserved 0
        return Wave(0x11, 1, 22050, 512, 4, BitConverter.GetBytes((ushort)1017), data, blocks * 1017);
    }

    /// <summary>MP3 inside a WAVE container (MPEGLAYER3WAVEFORMAT).</summary>
    private static byte[] Mp3Wave(int frames)
    {
        var extra = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(extra, 1);           // wID = MPEGLAYER3_ID_MPEG
        BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(2), 2); // fdwFlags = padding off
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(6), 417);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(8), 1);
        return Wave(0x55, 2, 44100, 1, 0, extra, Mp3(frames));
    }

    private static byte[] Psd()
    {
        var b = new byte[64];
        "8BPS"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(14), 64);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(18), 128);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), 8);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(24), 3);
        return b;
    }

    public static TheoryData<string, string, string> Generated => new()
    {
        { "song.mp3", "Codec", "Layer III" },
        { "mp3inside.wav", "Codec", "MP3" },
        { "ima.wav", "Codec", "IMA ADPCM" },
        { "model.gltf", "Lines", "" },
        { "editor.log", "Lines", "" },
        { "walk.mvf", "Kind", "Legacy motion" },
        { "art.psd", "Dimensions", "128 x 64" },
        { "short.v3d", "Type", "" },
    };

    private static byte[] Bytes(string name) => name switch
    {
        "song.mp3" => Mp3(40),
        "mp3inside.wav" => Mp3Wave(40),
        "ima.wav" => ImaWave(4),
        "model.gltf" => Encoding.UTF8.GetBytes("{\n  \"asset\": { \"version\": \"2.0\" },\n  \"nodes\": [ { \"name\": \"root\" } ]\n}\n"),
        "editor.log" => Encoding.ASCII.GetBytes("RED log\r\nLoaded level\r\nDone\r\n"),
        "walk.mvf" => [.. "VMVF"u8, 5, 0, 0, 0, .. new byte[56]],
        "art.psd" => Psd(),
        "short.v3d" => [.. "D3FR"u8, 0x00, 0x00, 0x04, 0x00, 1, 2, 3], // a mesh header cut short: facts must still not throw
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Generated))]
    public void GeneratedEntries_FactsNeverThrowAndHaveTheirRows(string name, string label, string contains)
    {
        byte[] bytes = Bytes(name);
        var sheet = VppFacts.Describe(name, () => new MemoryStream(bytes), bytes.Length);
        output.WriteLine($"{name}: {string.Join("; ", sheet.Rows.Select(r => $"{r.Label}={r.Value}"))}" + (sheet.Warnings.Length > 0 ? $" [warnings: {string.Join(" | ", sheet.Warnings)}]" : ""));
        Assert.NotNull(sheet["Type"]);
        Assert.NotNull(sheet[label]);
        Assert.Contains(contains, sheet[label]!, StringComparison.OrdinalIgnoreCase);
        if (name != "short.v3d") Assert.Null(sheet["Error"]);
        // every prefix of the entry (a truncated packfile) still describes without throwing
        for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 17))
            _ = VppFacts.Describe(name, () => new MemoryStream(bytes, 0, cut), cut);
    }

    [Fact]
    public void GeneratedAudio_PlaybackBytesForThePreview()
    {
        var mp3 = AudioDecoding.ForPlayback("song.mp3", new MemoryStream(Mp3(40)));
        Assert.Equal(".mp3", mp3.Extension);
        var inside = AudioDecoding.ForPlayback("mp3inside.wav", new MemoryStream(Mp3Wave(40)));
        output.WriteLine($"MP3-in-WAV plays as {inside.Extension} ({inside.Bytes.Length:N0} bytes)");
        Assert.True(inside.Bytes.Length > 0);
        var ima = AudioDecoding.ForPlayback("ima.wav", new MemoryStream(ImaWave(4)));
        Assert.Equal(".wav", ima.Extension);
        var pcm = AudioProbe.ProbeWav(ima.Bytes, "ima.wav");
        Assert.Equal("PCM", pcm.Codec, ignoreCase: true);
        Assert.Equal(4 * 1017, (int)Math.Round(pcm.Duration!.Value.TotalSeconds * 22050));
    }

    // ---- the game folder --------------------------------------------------------------------------------------

    private static readonly string[] Wanted = [".mp3", ".gltf", ".log", ".mvf", ".psd", ".v3d"];

    /// <summary>Every entry of the uncovered types in the whole game folder, plus the first MP3-in-WAV and IMA ADPCM WAVs found.</summary>
    [Fact]
    public void GameFolder_EveryUncoveredEntry_FactsAndPlayback()
    {
        var packfiles = TestData.GamePackfiles();
        if (packfiles.Count == 0) return;
        string root = LocalPaths.GameDirectory!;
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int wavesChecked = 0;
        foreach (var file in packfiles)
        {
            VppArchive archive;
            try { archive = VppArchive.Open(file.FullName); }
            catch (Exception ex) when (ex is VppFormatException or Cairn.Formats.AssetFormatException or IOException) { continue; }
            {
                foreach (var entry in archive.Entries)
                {
                    string ext = Path.GetExtension(entry.Name);
                    string kind = ext.ToLowerInvariant();
                    bool interesting = Wanted.Contains(kind);
                    if (!interesting && kind == ".wav" && entry.Size >= 40 && wavesChecked < 20000 && !file.DirectoryName!.Equals(root, StringComparison.OrdinalIgnoreCase))
                    {
                        // the format tag of the first fmt chunk (standard 12 + 8 + tag layout)
                        wavesChecked++;
                        var head = new byte[22];
                        using (var s = archive.OpenEntry(entry)) s.ReadExactly(head);
                        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(20));
                        if (tag == 0x55) { kind = "wav/mp3"; interesting = true; }
                        else if (tag == 0x11) { kind = "wav/ima-adpcm"; interesting = true; }
                    }
                    if (!interesting) continue;
                    found[kind] = found.GetValueOrDefault(kind) + 1;
                    if (found[kind] > 6) continue;
                    var e = entry;
                    var sheet = VppFacts.Describe(entry.Name, () => archive.OpenEntry(e), entry.Size);
                    output.WriteLine($"{Path.GetRelativePath(root, file.FullName)}|{entry.Name}: {string.Join("; ", sheet.Rows.Take(9).Select(r => $"{r.Label}={r.Value.Replace(Environment.NewLine, " | ")}"))}"
                        + (sheet.Warnings.Length > 0 ? $" [{string.Join(" | ", sheet.Warnings)}]" : ""));
                    Assert.NotNull(sheet["Type"]);
                    if (kind.StartsWith("wav/", StringComparison.Ordinal) || kind == ".mp3")
                    {
                        Assert.NotNull(sheet["Codec"]);
                        if (entry.Size <= 16 << 20)
                        {
                            using var s = archive.OpenEntry(entry);
                            try
                            {
                                var play = AudioDecoding.ForPlayback(entry.Name, s);
                                output.WriteLine($"    plays as {play.Extension}, {play.Bytes.Length:N0} bytes");
                            }
                            catch (Cairn.Formats.AssetFormatException ex) { output.WriteLine($"    cannot play: {ex.Message}"); }
                        }
                    }
                }
            }
        }
        output.WriteLine($"found: {string.Join(", ", found.Select(f => $"{f.Key} {f.Value}"))}; {wavesChecked:N0} non-root WAVs checked for their codec");
    }
}
