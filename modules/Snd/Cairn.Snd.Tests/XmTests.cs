using Cairn.Formats;
using Cairn.Formats.Audio;

namespace Cairn.Snd.Tests;

/// <summary>
/// FastTracker 2 modules through libopenmpt (cairn-openmpt.dll, copied next to the tests): the probe, the facts, the
/// render (one pass, 44,100 Hz stereo) and the conversions, on a small module written by <see cref="SyntheticSounds.Xm"/>;
/// damaged files fail only as unreadable files.
/// </summary>
public class XmTests
{
    private static readonly byte[] Song = SyntheticSounds.Xm();

    [Fact]
    public void ThePlayerShipsWithTheTests()
    {
        Assert.True(XmModule.IsAvailable, XmModule.MissingMessage);
        Assert.StartsWith("libopenmpt 0.8.", XmModule.Version);
    }

    [Fact]
    public void TheProbeIsTheExtendedModuleHeader()
    {
        Assert.True(XmModule.IsXm(Song));
        Assert.False(XmModule.IsXm("Extended Module"u8));
        Assert.False(XmModule.IsXm(SoundWriter.Wav16(new short[100], 1, 22050)));
        Assert.False(XmModule.IsXm([]));
        Assert.True(SoundDecoder.CanDecode("song1.xm"));
        Assert.True(SoundDecoder.CanDecode("SONG1.XM"));
    }

    [Fact]
    public void FactsComeFromTheModule()
    {
        var info = XmModule.ReadInfo(Song, "test.xm");
        Assert.Equal("Cairn test song", info.Title);
        Assert.Equal("Cairn tests", info.Tracker);
        Assert.Equal("FastTracker 2 v1.04", info.Format);
        Assert.Equal(0x0104, info.Version);
        Assert.Equal(2, info.Channels);
        Assert.Equal(1, info.Patterns);
        Assert.Equal(1, info.Orders);
        Assert.Equal(1, info.Instruments);
        Assert.Equal(1, info.Samples);
        Assert.Equal(1, info.Subsongs);
        Assert.True(info.LinearFrequencies);
        Assert.Equal(SyntheticSounds.XmSpeed, info.Speed);
        Assert.Equal(SyntheticSounds.XmTempo, info.Tempo);
        Assert.Equal(SyntheticSounds.XmLength.TotalSeconds, info.Duration.TotalSeconds, 2);
    }

    [Fact]
    public void ARenderIsOnePassOfStereoAt44kHzAndNotSilent()
    {
        var sound = SoundDecoder.Decode(Song, "test.xm");
        Assert.Equal("FastTracker 2 module", sound.Format);
        Assert.Equal(XmModule.SampleRate, sound.SampleRate);
        Assert.Equal(2, sound.Channels);
        Assert.Null(sound.Loop);
        Assert.Empty(sound.Problems);
        // the song once, plus at most libopenmpt's short fade at its end
        double seconds = sound.Duration.TotalSeconds, song = SyntheticSounds.XmLength.TotalSeconds;
        Assert.InRange(seconds, song - 0.01, song + 1.0);
        int peak = sound.Samples.Max(s => Math.Abs((int)s));
        Assert.True(peak > 2000, $"peak {peak}");
        // both channels sound (the module's channels are panned to the middle)
        Assert.Contains(sound.Samples.Where((_, i) => i % 2 == 0), s => s != 0);
        Assert.Contains(sound.Samples.Where((_, i) => i % 2 == 1), s => s != 0);
        Assert.Contains(sound.Details, d => d.Label == "Module channels" && d.Value == "2");
        Assert.Contains(sound.Details, d => d.Label == "Title" && d.Value == "Cairn test song");
        Assert.Contains(sound.Details, d => d.Label == "Tracker" && d.Value == "Cairn tests");
        Assert.Contains(sound.Details, d => d.Label == "Format" && d.Value == "FastTracker 2 v1.04");

        // the same render every time, on any thread (one module handle per render)
        var renders = new short[8][];
        Parallel.For(0, renders.Length, i => renders[i] = XmModule.Render(Song, "test.xm").Samples);
        foreach (var r in renders) Assert.Equal(sound.Samples, r);
    }

    [Fact]
    public void PreviewsGetAWave()
    {
        var playback = AudioDecoding.ForPlayback("song.xm", new MemoryStream(Song));
        Assert.Equal(".wav", playback.Extension);
        var info = AudioProbe.ProbeWav(playback.Bytes, "song.wav");
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(2, info.Channels);
        Assert.Equal(16, info.BitsPerSample);
        // the content decides: a module under another name is still a module
        Assert.Equal(".wav", AudioDecoding.ForPlayback("music.bin", new MemoryStream(Song)).Extension);
    }

    [Fact]
    public void ConvertsToWavExactlyAndToOgg()
    {
        var sound = SoundDecoder.Decode(Song, "theme.xm");
        Assert.Null(SoundConversion.SameFormatReason(sound, SoundOutputFormat.Wav));
        Assert.Null(SoundConversion.SameFormatReason(sound, SoundOutputFormat.Ogg));

        var wav = SoundConversion.Convert("theme.xm", Song, new SoundConvertOptions { Format = SoundOutputFormat.Wav });
        Assert.True(wav.Succeeded, wav.Error);
        Assert.Equal("theme.wav", wav.OutputName);
        Assert.Contains(wav.Notes, n => n.Contains("rendered at 44,100 Hz stereo", StringComparison.Ordinal));
        var back = SoundDecoder.Decode(wav.Bytes!, wav.OutputName);
        Assert.Equal(sound.Samples, back.Samples);
        Assert.Equal(44100, back.SampleRate);

        Assert.True(OggVorbisWriter.IsAvailable);
        var ogg = SoundConversion.Convert("theme.xm", Song, new SoundConvertOptions { Format = SoundOutputFormat.Ogg });
        Assert.True(ogg.Succeeded, ogg.Error);
        Assert.Equal("theme.ogg", ogg.OutputName);
        var decoded = SoundDecoder.Decode(ogg.Bytes!, ogg.OutputName);
        Assert.Equal(2, decoded.Channels);
        Assert.Equal(44100, decoded.SampleRate);
        Assert.Equal(sound.FrameCount, decoded.FrameCount);
        Assert.Null(decoded.Loop);
    }

    [Fact]
    public void DamagedModulesFailAsUnreadableFiles()
    {
        // cut inside the header, or not a module at all: refused by Cairn before the player sees it
        foreach (var bad in new[] { Song[..17], Song[..60], Song[..79], "not a module at all"u8.ToArray(), [] })
        {
            Assert.Throws<AssetFormatException>(() => SoundDecoder.Decode(bad, "bad.xm"));
            Assert.Throws<AssetFormatException>(() => XmModule.ReadInfo(bad, "bad.xm"));
            var result = SoundConversion.Convert("bad.xm", bad, new SoundConvertOptions());
            Assert.False(result.Succeeded);
            Assert.False(result.Skipped);
            Assert.NotNull(result.Error);
        }
        Assert.Throws<AssetFormatException>(() => AudioDecoding.ForPlayback("bad.xm", new MemoryStream("garbage!"u8.ToArray())));

        // a header with garbage after it, cut anywhere, or with bytes changed: refused or played, never anything else
        var random = new Random(1234);
        var damaged = new List<byte[]>();
        for (int cut = 80; cut < Song.Length; cut += 7) damaged.Add(Song[..cut]);
        for (int i = 0; i < 200; i++)
        {
            var copy = (byte[])Song.Clone();
            for (int k = 0; k < 4; k++) copy[random.Next(17, copy.Length)] = (byte)random.Next(256);
            damaged.Add(copy);
        }
        var junk = new byte[4000];
        random.NextBytes(junk);
        damaged.Add([.. Song[..80], .. junk]);
        int refused = 0;
        foreach (var bytes in damaged)
        {
            try
            {
                var sound = SoundDecoder.Decode(bytes, "damaged.xm");
                Assert.Equal(2, sound.Channels);
                Assert.True(sound.Duration <= XmModule.MaxDuration);
            }
            catch (AssetFormatException)
            {
                refused++;
            }
        }
        Assert.True(refused > 0);
    }
}
