using System.Buffers.Binary;
using Cairn.Formats;
using Cairn.Formats.Audio;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

public sealed class FactsAndAudioTests(ITestOutputHelper output)
{
    /// <summary>The row every entry of a type must produce.</summary>
    private static readonly Dictionary<string, string> KeyRows = new(StringComparer.OrdinalIgnoreCase)
    {
        [".tga"] = "Dimensions", [".dds"] = "Dimensions", [".vbm"] = "Frames", [".png"] = "Dimensions", [".jpg"] = "Dimensions",
        [".psd"] = "Dimensions", [".wav"] = "Codec", [".ogg"] = "Duration", [".aif"] = "Duration", [".mp3"] = "Sample rate",
        [".v3m"] = "Submeshes", [".v3c"] = "Bones", [".v3d"] = "Submeshes", [".rfa"] = "Bones", [".mvf"] = "Version",
        [".vfx"] = "Problems", [".atx"] = "Frames", [".tbl"] = "Lines", [".txt"] = "Lines", [".log"] = "Lines", [".gltf"] = "Lines",
        [".vf"] = "Glyphs", [".rfg"] = "Groups", [".rfl"] = "Format version",
    };

    private static Dictionary<string, List<VppItem>>? _samples;

    /// <summary>Up to six entries of each type found in the game data (root packfiles first).</summary>
    internal static Dictionary<string, List<VppItem>> Samples()
    {
        if (_samples is not null) return _samples;
        var found = new Dictionary<string, List<VppItem>>(StringComparer.OrdinalIgnoreCase);
        var files = TestData.RootPackfiles().Concat(TestData.GamePackfiles().OrderBy(f => f.Length));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!seen.Add(file.FullName)) continue;
            VppPackage package;
            try { package = VppPackage.Open(file.FullName); }
            catch (AssetFormatException) { continue; }
            if (Validation.VppValidator.Validate(package).Any(p => p.Code == "VPP011")) continue;
            foreach (var item in package.Items)
            {
                if (item.Size == 0) continue;
                string ext = item.Extension;
                if (!found.TryGetValue(ext, out var list)) found[ext] = list = [];
                if (list.Count < 6) list.Add(item);
            }
            if (KeyRows.Keys.All(k => found.TryGetValue(k, out var l) && l.Count >= 6)) break;
        }
        return _samples = found;
    }

    [Fact]
    public void Facts_ForEveryTypeInTheGameData_NeverThrow_AndGiveKeyRows()
    {
        if (TestData.GamePackfiles().Count == 0) return;
        var samples = Samples();
        var missing = new List<string>();
        foreach (var (ext, items) in samples.OrderBy(kv => kv.Key))
        {
            int good = 0;
            foreach (var item in items)
            {
                var sheet = VppFacts.Describe(item);
                Assert.Equal(VppFileTypes.Describe(item.Name).DisplayName, sheet["Type"]);
                Assert.NotNull(sheet["Size"]);
                if (!KeyRows.TryGetValue(ext, out string? key)) { Assert.NotNull(sheet["First bytes"] ?? sheet["Lines"] ?? sheet["Error"]); good++; continue; }
                if (sheet[key] is not null && sheet["Error"] is null && sheet["Content"] is null) good++;
            }
            var example = VppFacts.Describe(items[0]);
            output.WriteLine($"{ext} ({items.Count} tried, {good} complete): {string.Join("; ", example.Rows.Select(r => $"{r.Label}={r.Value.Replace(Environment.NewLine, " | ")}"))}");
            if (good == 0) missing.Add(ext);
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void Facts_SpotChecks()
    {
        if (TestData.GamePackfiles().Count == 0) return;
        var samples = Samples();
        if (samples.TryGetValue(".v3c", out var v3c))
        {
            var sheet = VppFacts.Describe(v3c[0]);
            Assert.Equal("Character mesh", sheet["Kind"]);
            Assert.True(int.Parse(sheet["Bones"]!.Replace(",", "")) > 0);
        }
        if (samples.TryGetValue(".v3m", out var v3m)) Assert.Equal("Static mesh", VppFacts.Describe(v3m[0])["Kind"]);
        if (samples.TryGetValue(".ogg", out var ogg)) Assert.Equal("Vorbis", VppFacts.Describe(ogg[0])["Codec"]);
        if (samples.TryGetValue(".aif", out var aif))
        {
            var sheet = VppFacts.Describe(aif[0]);
            Assert.Equal("AIFF-C", sheet["Container"]);
            Assert.Contains("ima4", sheet["Codec"]);
            Assert.Contains(sheet["Sample rate"], new[] { "11,025 Hz", "22,050 Hz" });
        }
        if (samples.TryGetValue(".tbl", out var tbl)) Assert.Equal("CRLF (Windows)", VppFacts.Describe(tbl[0])["Line endings"]);
        if (samples.TryGetValue(".rfa", out var rfa)) Assert.Contains(VppFacts.Describe(rfa[0])["Version"], new[] { "7", "8" });
        if (samples.TryGetValue(".vf", out var vf)) Assert.Equal("32", VppFacts.Describe(vf[0])["First character"]);
        if (samples.TryGetValue(".rfg", out var rfg)) Assert.Equal("200", VppFacts.Describe(rfg[0])["Version"]);
    }

    [Fact]
    public void Facts_UnknownAndBrokenData_DoNotThrow()
    {
        var unknown = VppFacts.Describe("thing.xyz", () => new MemoryStream([1, 2, 3, 0xAB]), 4);
        Assert.Equal("01 02 03 AB", unknown["First bytes"]);
        foreach (string name in KeyRows.Keys.Select(k => "broken" + k))
        {
            var sheet = VppFacts.Describe(name, () => new MemoryStream(TestData.Bytes(100, 3)), 100);
            Assert.NotNull(sheet["Type"]);
            var empty = VppFacts.Describe(name, () => new MemoryStream([]), 0);
            Assert.NotNull(empty["Size"]);
        }
        var throwing = VppFacts.Describe("gone.tga", () => throw new IOException("vanished"), 10);
        Assert.Contains("vanished", throwing["Error"]);
        // Named as a sound, holding a web page.
        var html = VppFacts.Describe("fake.wav", () => new MemoryStream("<!DOCTYPE html><html></html>"u8.ToArray()), 28);
        Assert.NotNull(html["Content"]);
        Assert.NotEmpty(html.Warnings);
    }

    [Fact]
    public void Facts_RflSlotIsPluggable()
    {
        var before = VppFacts.DescriberFor(".rfl");
        Assert.NotNull(before);
        try
        {
            VppFacts.Register(".rfl", (input, sheet) => sheet.Add("Level name", "plugged"));
            var sheet = VppFacts.Describe("x.rfl", () => new MemoryStream([0x55, 0xDA, 0xBA, 0xD4, 200, 0, 0, 0]), 8);
            Assert.Equal("plugged", sheet["Level name"]);
        }
        finally
        {
            VppFacts.Register(".rfl", before!);
        }
    }

    [Fact]
    public void AudioDecoding_RealOggAndAif_GiveValidWav()
    {
        if (TestData.GamePackfiles().Count == 0) return;
        var samples = Samples();
        int decoded = 0;
        foreach (string ext in new[] { ".ogg", ".aif", ".wav" })
        {
            if (!samples.TryGetValue(ext, out var items)) continue;
            foreach (var item in items.Take(4))
            {
                var facts = VppFacts.Describe(item);
                if (facts["Content"] is not null || facts["Error"] is not null) continue;
                using var stream = item.Source.Open();
                var playback = AudioDecoding.ForPlayback(item.Name, stream);
                if (playback.Extension == ".mp3") continue;
                var wav = playback.Bytes;
                Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
                Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
                var info = AudioProbe.ProbeWav(wav, item.Name);
                Assert.Equal("PCM", info.Codec);
                Assert.True(info.Duration!.Value.TotalSeconds > 0.01, $"{item.Name}: {info.Duration}");
                // The decoded length agrees with the duration the facts report (within 5 %, ADPCM blocks round up).
                double expected = ParseSeconds(facts["Duration"]!);
                Assert.InRange(info.Duration.Value.TotalSeconds, expected * 0.95 - 0.05, expected * 1.05 + 0.05);
                output.WriteLine($"{item.Name}: {facts["Codec"]} {facts["Sample rate"]} {facts["Channels"]} {facts["Duration"]} -> PCM {info.SampleRate} Hz x{info.Channels}, {info.Duration.Value.TotalSeconds:0.00} s");
                decoded++;
            }
        }
        Assert.True(decoded > 0);
    }

    [Fact]
    public void AudioDecoding_RealAdpcmWaves_DecodeToPcmOfTheSameLength()
    {
        if (TestData.GamePackfiles().Count == 0) return;
        var wanted = new Dictionary<int, VppItem?> { [2] = null, [0x11] = null };
        foreach (var file in TestData.RootPackfiles().Concat(TestData.GamePackfiles().OrderBy(f => f.Length).Take(600)))
        {
            VppPackage package;
            try { package = VppPackage.Open(file.FullName); } catch (AssetFormatException) { continue; }
            foreach (var item in package.Items.Where(i => i.Extension == ".wav" && i.Size > 64))
            {
                var head = new byte[22];
                using (var s = item.Source.Open()) s.ReadExactly(head);
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(20));
                if (head.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wanted.TryGetValue(tag, out var have) && have is null) wanted[tag] = item;
            }
            if (wanted.Values.All(v => v is not null)) break;
        }
        foreach (var (tag, item) in wanted)
        {
            if (item is null) { output.WriteLine($"no WAVE with format 0x{tag:X} found"); continue; }
            var facts = VppFacts.Describe(item);
            using var stream = item.Source.Open();
            var wav = AudioDecoding.ToWav(item.Name, stream)!;
            var info = AudioProbe.ProbeWav(wav, item.Name);
            Assert.Equal("PCM", info.Codec);
            double expected = ParseSeconds(facts["Duration"]!);
            Assert.InRange(info.Duration!.Value.TotalSeconds, expected * 0.95 - 0.05, expected * 1.05 + 0.05);
            // Decoded audio is not silence or noise saturation: some samples, not all, near full scale.
            var samples = wav.AsSpan(44);
            int loud = 0, nonzero = 0;
            for (int i = 0; i + 1 < samples.Length; i += 2)
            {
                int v = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(samples[i..]));
                if (v > 0) nonzero++;
                if (v > 32000) loud++;
            }
            Assert.True(nonzero > 0 && loud < samples.Length / 2 / 10, $"{item.Name}: {nonzero} non-zero, {loud} saturated");
            output.WriteLine($"{item.Name}: {facts["Codec"]} {facts["Duration"]} -> PCM {info.Duration.Value.TotalSeconds:0.00} s");
        }
    }

    [Fact]
    public void AudioDecoding_Ima4OverTheOutputCap_IsRefusedBeforeDecoding()
    {
        // Review finding 13: IMA4 output is ~3.8x the input; past MaxOutputBytes it must be refused, not allocated (twice).
        int packets = (int)(AudioDecoding.MaxOutputBytes / 128) + 1; // 64 samples x 2 bytes per packet (mono)
        var bytes = Ima4Aiff(packets);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<AssetFormatException>(() => AudioDecoding.ToWav("big.aif", new MemoryStream(bytes)));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("too long", ex.Message);
        // reading the stream into memory costs ~3x the input (buffer growth); decoding would add ~2 x 268 MB on top
        Assert.True(allocated < bytes.Length * 4L, $"allocated {allocated:N0} bytes for a {bytes.Length:N0}-byte input");
    }

    private static byte[] Ima4Aiff(int packets)
    {
        var comm = new byte[24];
        BinaryPrimitives.WriteInt16BigEndian(comm, 1);
        BinaryPrimitives.WriteUInt32BigEndian(comm.AsSpan(2), (uint)packets);
        BinaryPrimitives.WriteInt16BigEndian(comm.AsSpan(6), 16);
        comm[8] = 0x40; comm[9] = 0x0D; comm[10] = 0xAC; comm[11] = 0x44; // 22050 as an 80-bit extended
        "ima4"u8.CopyTo(comm.AsSpan(18));
        var ssnd = new byte[8 + (long)packets * 34];
        using var aiff = new MemoryStream();
        void Chunk(string id, byte[] body)
        {
            aiff.Write(System.Text.Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)body.Length);
            aiff.Write(size);
            aiff.Write(body);
        }
        aiff.Write("FORM"u8);
        aiff.Write(new byte[4]);
        aiff.Write("AIFC"u8);
        Chunk("COMM", comm);
        Chunk("SSND", ssnd);
        return aiff.ToArray();
    }

    [Fact]
    public void AudioDecoding_Ima4AndAdpcmDecoders_AreSelfConsistent()
    {
        // A synthetic AIFF-C 'ima4' with silence packets decodes to silence of the right length.
        int packets = 10;
        var comm = new byte[24];
        BinaryPrimitives.WriteInt16BigEndian(comm, 1);
        BinaryPrimitives.WriteUInt32BigEndian(comm.AsSpan(2), (uint)packets);
        BinaryPrimitives.WriteInt16BigEndian(comm.AsSpan(6), 16);
        comm[8] = 0x40; comm[9] = 0x0D; comm[10] = 0xAC; comm[11] = 0x44; // 22050 as an 80-bit extended
        "ima4"u8.CopyTo(comm.AsSpan(18));
        var ssnd = new byte[8 + packets * 34];
        using var aiff = new MemoryStream();
        void Chunk(string id, byte[] body)
        {
            aiff.Write(System.Text.Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)body.Length);
            aiff.Write(size);
            aiff.Write(body);
        }
        aiff.Write("FORM"u8);
        aiff.Write(new byte[4]);
        aiff.Write("AIFC"u8);
        Chunk("COMM", comm);
        Chunk("SSND", ssnd);
        var bytes = aiff.ToArray();
        var probe = AudioProbe.ProbeAiff(bytes, "s.aif");
        Assert.Equal(22050, probe.SampleRate);
        Assert.Equal(packets * 64 / 22050.0, probe.Duration!.Value.TotalSeconds, 3);
        var wav = AudioDecoding.ToWav("s.aif", new MemoryStream(bytes))!;
        Assert.Equal(44 + packets * 64 * 2, wav.Length);

        Assert.Null(AudioDecoding.ToWav("x.mp3", new MemoryStream([0xFF, 0xFB, 0x90, 0x64, 0, 0, 0, 0])));
        Assert.Throws<AssetFormatException>(() => AudioDecoding.ToWav("x.wav", new MemoryStream(TestData.Bytes(64, 1))));
    }

    [Fact]
    public void RecoveryManifest_RoundTrips()
    {
        using var temp = new TempFolder();
        string archive = PackageTests.BuildArchive(temp, "rec.vpp");
        var package = VppPackage.Open(archive);
        string file = temp.Write("added.tbl", TestData.Bytes(500, 9));
        package = VppEdit.AddFiles(package, [file], VppClashPolicy.Replace).Package;
        package = VppEdit.AddBytes(package, "small.txt", TestData.Bytes(100, 10), VppClashPolicy.Replace).Package;
        package = VppEdit.AddBytes(package, "big.txt", TestData.Bytes(5000, 11), VppClashPolicy.Replace).Package;
        package = VppEdit.Rename(package, package.Items[0].Name, "renamed.tbl");
        package = VppEdit.Remove(package, [package.Items[1].Name]);
        package = VppEdit.Sort(package, VppSortKey.Name);

        var manifest = VppRecoveryManifest.Capture(package, maxInlineBytes: 1000);
        var json = manifest.ToJson();
        Assert.True(json.Length < 4000);
        var restored = VppRecoveryManifest.FromJson(json).Restore(out var lost);
        Assert.Equal(["big.txt"], lost);
        var expected = package.Items.Where(i => i.Name != "big.txt").ToList();
        Assert.Equal(expected.Select(i => (i.Name, i.State, i.OriginalName)), restored.Items.Select(i => (i.Name, i.State, i.OriginalName)));
        for (int i = 0; i < expected.Count; i++) Assert.Equal(TestData.ReadEntry(expected[i]), TestData.ReadEntry(restored.Items[i]));
        Assert.Null(restored.Path); // an entry is missing: the rest is a new packfile, never saved over the original unasked
        Assert.Throws<AssetFormatException>(() => VppRecoveryManifest.FromJson("{nope"u8));
    }

    private static double ParseSeconds(string text)
    {
        text = text.Trim();
        if (text.EndsWith(" s", StringComparison.Ordinal)) return double.Parse(text[..^2], System.Globalization.CultureInfo.InvariantCulture);
        var parts = text.Split(':');
        double seconds = 0;
        foreach (var part in parts) seconds = seconds * 60 + double.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
        return seconds;
    }
}
