using Cairn.Formats.Audio;
using Cairn.Formats.XForge;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Snd.Tests;

/// <summary>
/// The nine songs of the N-Gage release (song1 to song10, no song6), read from its archive under
/// <see cref="LocalPaths.NgageDirectory"/> when present; passes trivially without it. The archive is only read.
/// </summary>
public class NgageSongTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryNgageSongPlaysAndConverts()
    {
        if (LocalPaths.NgageDirectory is not { } root) return;
        string path = Path.Combine(root, "redfaction.cfl");
        if (!File.Exists(path)) return;
        var archive = CflArchive.Open(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var songs = archive.Entries.Where(e => e.Name.EndsWith(".xm", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(9, songs.Count);
        foreach (var entry in songs)
        {
            byte[] bytes = archive.ReadEntry(stream, entry);
            Assert.True(XmModule.IsXm(bytes), entry.Name);
            var sound = SoundDecoder.Decode(bytes, entry.Name);
            var info = XmModule.ReadInfo(bytes, entry.Name);
            output.WriteLine($"{entry.Name}: {info.Format}, {info.Tracker}, {info.Channels} channels, {info.Patterns} patterns, {info.Orders} orders, {info.Instruments} instruments, {sound.Duration}");
            Assert.StartsWith("FastTracker 2", info.Format);
            Assert.Equal(4, info.Channels);
            Assert.Empty(sound.Problems);
            Assert.InRange(sound.Duration.TotalSeconds, 10, 300);
            Assert.InRange(sound.Duration.TotalSeconds - info.Duration.TotalSeconds, -0.01, 1.0); // one pass
            Assert.True(sound.Samples.Max(s => Math.Abs((int)s)) > 2000, $"{entry.Name} is silent");
            var wav = SoundConversion.Convert(entry.Name, bytes, new SoundConvertOptions());
            Assert.True(wav.Succeeded, wav.Error);
            Assert.Equal(sound.Samples, SoundDecoder.Decode(wav.Bytes!, wav.OutputName).Samples);
        }
    }
}
