using Cairn.Formats;
using Cairn.Formats.Audio;

namespace Cairn.Snd.Tests;

public class PsAdpcmTests
{
    private static short[] Decode(byte[] frame, ref int h1, ref int h2, out bool bad)
    {
        var output = new short[28];
        PsAdpcm.DecodeFrame(frame, output, ref h1, ref h2, out bad);
        return output;
    }

    [Theory]
    [InlineData(12, 1, 1)]
    [InlineData(12, 0xF, -1)]
    [InlineData(12, 8, -8)]
    [InlineData(0, 7, 28672)]
    [InlineData(0, 8, -32768)]
    [InlineData(4, 3, 3 * 256)]
    public void FilterZeroScalesResidualsByShift(int shift, int nibble, int expected)
    {
        int h1 = 1234, h2 = -999; // filter 0 ignores the history
        var samples = Decode(TestData.Frame(0, shift, 0, Enumerable.Repeat(nibble, 28).ToArray()), ref h1, ref h2, out bool bad);
        Assert.False(bad);
        Assert.All(samples, s => Assert.Equal(expected, s));
        Assert.Equal(expected, h1);
        Assert.Equal(expected, h2);
    }

    [Theory]
    [InlineData(1, 60, 0)]
    [InlineData(2, 115, -52)]
    [InlineData(3, 98, -55)]
    [InlineData(4, 122, -60)]
    public void EveryFilterPredictsFromTwoSamples(int filter, int k0, int k1)
    {
        int h1 = 1000, h2 = -500;
        var nibbles = Enumerable.Range(0, 28).Select(i => i % 16).ToArray();
        var samples = Decode(TestData.Frame(filter, 8, 0, nibbles), ref h1, ref h2, out bool bad);
        Assert.False(bad);
        // the same arithmetic written out independently
        int a = 1000, b = -500;
        for (int i = 0; i < 28; i++)
        {
            int n = nibbles[i] >= 8 ? nibbles[i] - 16 : nibbles[i];
            int expected = Math.Clamp(n * 4096 / 256 + (int)Math.Floor((a * k0 + b * k1 + 32) / 64.0), -32768, 32767);
            Assert.Equal(expected, samples[i]);
            b = a;
            a = expected;
        }
    }

    [Fact]
    public void PredictionClampsToSixteenBits()
    {
        int h1 = 32000, h2 = 0;
        var samples = Decode(TestData.Frame(1, 0, 0, Enumerable.Repeat(7, 28).ToArray()), ref h1, ref h2, out _);
        Assert.All(samples, s => Assert.Equal(short.MaxValue, s));
    }

    [Fact]
    public void OutOfRangeFilterAndShiftDecodeAsTheConsoleDoes()
    {
        int h1 = 5000, h2 = 4000;
        var badFilter = Decode(TestData.Frame(7, 12, 0, Enumerable.Repeat(1, 28).ToArray()), ref h1, ref h2, out bool bad1);
        Assert.True(bad1);
        Assert.All(badFilter, s => Assert.Equal(1, s)); // filter 0
        h1 = h2 = 0;
        var badShift = Decode(TestData.Frame(0, 14, 0, Enumerable.Repeat(1, 28).ToArray()), ref h1, ref h2, out bool bad2);
        Assert.True(bad2);
        Assert.All(badShift, s => Assert.Equal(4096 >> 9, s)); // shift 9
    }

    [Fact]
    public void ReturnsTheFlags()
    {
        int h1 = 0, h2 = 0;
        var output = new short[28];
        foreach (int flags in new[] { 0, 1, 2, 3, 4, 6, 7 })
            Assert.Equal(flags, PsAdpcm.DecodeFrame(TestData.Frame(0, 0, flags, new int[28]), output, ref h1, ref h2, out _));
    }

    [Theory]
    [InlineData(0x0759, 22050)]
    [InlineData(0x03AC, 11025)]
    [InlineData(0x0EB3, 44100)]
    [InlineData(0x1000, 48000)]
    [InlineData(0x0555, 16000)]
    [InlineData(0x0123, 3410)]
    public void PitchToRate(int pitch, int rate) => Assert.Equal(rate, PsAdpcm.RateFromPitch(pitch));

    [Fact]
    public void TestEncoderRoundTripsASine()
    {
        var pcm = TestData.Sine(40);
        var frames = TestData.Encode(pcm);
        var sound = Ps2Sound.Decode(TestData.Vse(frames), "sine.vse");
        Assert.Equal(pcm.Length, sound.Samples.Length);
        Assert.True(TestData.Rms(pcm, sound.Samples) < 120, $"rms {TestData.Rms(pcm, sound.Samples)}");
    }
}

public class Ps2SoundTests
{
    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    [Fact]
    public void OneShotEndsOnTheEndFlagAndSkipsTheMarker()
    {
        var pcm = TestData.Sine(10);
        var frames = TestData.Encode(pcm, f => f == 9 ? 1 : 0);
        var bytes = TestData.Vse(Concat(frames, TestData.EndMarker()), TestData.Pitch22k, ms: 13);
        var sound = Ps2Sound.Decode(bytes, "a.vse");
        Assert.Equal(1, sound.Channels);
        Assert.Equal(22050, sound.SampleRate);
        Assert.Equal(280, sound.FrameCount);
        Assert.Null(sound.Loop);
        Assert.DoesNotContain(sound.Problems, p => p.Severity != SoundSeverity.Info);
        Assert.NotNull(sound.ExactRate);
        Assert.Equal("PS ADPCM", sound.Codec);
        var info = Ps2Sound.Probe(bytes, "a.vse");
        Assert.Equal(22050, info.SampleRate);
        Assert.Equal(280.0 / 22050, info.Duration!.Value.TotalSeconds, 6);
    }

    [Fact]
    public void LoopingSoundLoopsBetweenItsFlags()
    {
        var frames = TestData.Encode(TestData.Sine(12), f => f == 0 ? 6 : f == 11 ? 3 : 2);
        var sound = Ps2Sound.Decode(TestData.Vse(frames, TestData.Pitch11k, looping: true), "loop.vse");
        Assert.Equal(11025, sound.SampleRate);
        Assert.NotNull(sound.Loop);
        Assert.True(sound.Loop!.Enabled);
        Assert.Equal(0, sound.Loop.Start);
        Assert.Equal(12 * 28, sound.Loop.End);
        Assert.True(sound.Loop.IsWhole(sound.FrameCount));
    }

    [Fact]
    public void LoopStartPartWay()
    {
        var frames = TestData.Encode(TestData.Sine(12), f => f == 4 ? 6 : f == 9 ? 3 : 2);
        var sound = Ps2Sound.Decode(TestData.Vse(frames, looping: true), "loop.vse");
        Assert.Equal(4 * 28, sound.Loop!.Start);
        Assert.Equal(10 * 28, sound.Loop.End);
    }

    [Fact]
    public void StreamingMarkersOfAOneShotAreNotLoops()
    {
        // buffer-half markers every few frames, then the end flag: decoded straight through
        var frames = TestData.Encode(TestData.Sine(20), f => f is 0 or 10 ? 6 : f is 9 ? 3 : f == 19 ? 1 : 0);
        var sound = Ps2Sound.Decode(TestData.Vse(Concat(frames, TestData.EndMarker())), "long.vse");
        Assert.Equal(20 * 28, sound.FrameCount);
        Assert.Null(sound.Loop);
        Assert.Contains(sound.Problems, p => p.Code == "SND014");
    }

    [Fact]
    public void OldHeaderDropsSilentPaddingOnly()
    {
        var frames = TestData.Encode(TestData.Sine(10));
        var padding = new byte[16 * 20];
        var bytes = TestData.VseOld(Concat(frames, padding, TestData.EndMarker()), ms: 13);
        var sound = Ps2Sound.Decode(bytes, "old.vse");
        Assert.Equal(287, sound.FrameCount); // 13 ms at 22,050 Hz, rounded up
        Assert.Contains(sound.Problems, p => p.Code == "SND011");
        Assert.Contains(sound.Problems, p => p.Code == "SND012");
        Assert.Equal(287, (int)Math.Round(Ps2Sound.Probe(bytes, "old.vse").Duration!.Value.TotalSeconds * 22050));

        // a header shorter than the sound never cuts audible frames
        var cut = Ps2Sound.Decode(TestData.VseOld(Concat(frames, padding, TestData.EndMarker()), ms: 1), "old.vse");
        Assert.Equal(280, cut.FrameCount);
    }

    [Fact]
    public void VmuSplitsTheChannelsAndTheLastBlock()
    {
        // 1.5 blocks per channel: left a sine, right silence
        int frames = 1024 + 512;
        var left = TestData.Encode(TestData.Sine(frames), f => f == frames - 1 ? 0 : 2);
        var right = new byte[frames * 16];
        for (int f = 0; f < frames; f++) right[f * 16 + 1] = 2;
        var bytes = TestData.Vmu(left, right, looping: true);
        var sound = Ps2Sound.Decode(bytes, "music.vmu");
        Assert.Equal(2, sound.Channels);
        Assert.Equal(44100, sound.SampleRate);
        Assert.Equal(frames * 28, sound.FrameCount);
        Assert.True(sound.Loop!.IsWhole(sound.FrameCount));
        var l = Enumerable.Range(0, (int)sound.FrameCount).Select(i => sound.Samples[i * 2]).ToArray();
        Assert.True(TestData.Rms(TestData.Sine(frames), l) < 120);
        Assert.All(Enumerable.Range(0, (int)sound.FrameCount), i => Assert.Equal(0, sound.Samples[i * 2 + 1]));
        Assert.DoesNotContain(sound.Problems, p => p.Severity != SoundSeverity.Info);
    }

    [Fact]
    public void VmuEndMarkersEndBothChannels()
    {
        int frames = 300;
        var left = Concat(TestData.Encode(TestData.Sine(frames - 1)), TestData.EndMarker());
        var right = Concat(TestData.Encode(TestData.Sine(frames - 1, 70)), TestData.EndMarker());
        var sound = Ps2Sound.Decode(TestData.Vmu(left, right), "cut.vmu");
        Assert.Equal((frames - 1) * 28, sound.FrameCount);
        Assert.Null(sound.Loop);
    }

    [Fact]
    public void DamagedFilesAreReportedNotThrown()
    {
        var frames = TestData.Encode(TestData.Sine(10), f => f == 9 ? 1 : 0);
        // cut short: the header promises more
        var truncated = TestData.Vse(frames, declaredSize: frames.Length + 160);
        Assert.Contains(Ps2Sound.Decode(truncated, "t.vse").Problems, p => p.Code == "SND002");
        // a ragged tail
        var ragged = Concat(TestData.Vse(frames), new byte[5]);
        Assert.Contains(Ps2Sound.Decode(ragged, "r.vse").Problems, p => p.Code is "SND002" or "SND004");
        // out-of-range frames
        var odd = (byte[])frames.Clone();
        odd[16] = 0x7E;
        Assert.Contains(Ps2Sound.Decode(TestData.Vse(odd), "o.vse").Problems, p => p.Code == "SND003");
        // unusual envelope, no end flag
        var plain = TestData.Encode(TestData.Sine(4));
        var sound = Ps2Sound.Decode(TestData.Vse(plain, envelope: 0x8F7F0027), "e.vse");
        Assert.Contains(sound.Problems, p => p.Code == "SND006");
        Assert.Contains(sound.Problems, p => p.Code == "SND005");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(11)]
    public void TooShortIsRefused(int length) =>
        Assert.Throws<AssetFormatException>(() => Ps2Sound.Decode(new byte[length], "x.vse"));

    [Fact]
    public void HeaderOnlyFilesDecodeToSilence()
    {
        var vmu = TestData.Vmu([], []);
        var sound = Ps2Sound.Decode(vmu, "stub.vmu");
        Assert.Equal(0, sound.FrameCount);
        Assert.Contains(sound.Problems, p => p.Code == "SND013");
    }

    [Fact]
    public void FuzzedFilesNeverThrowAnythingElse()
    {
        var rng = new Random(1234);
        var good = TestData.Vse(Concat(TestData.Encode(TestData.Sine(30), f => f == 29 ? 1 : 0), TestData.EndMarker()));
        var music = TestData.Vmu(TestData.Encode(TestData.Sine(40)), TestData.Encode(TestData.Sine(40, 33)));
        for (int i = 0; i < 3000; i++)
        {
            var source = i % 2 == 0 ? good : music;
            string name = i % 2 == 0 ? "f.vse" : "f.vmu";
            var bytes = (byte[])source.Clone();
            int mutations = rng.Next(1, 12);
            for (int m = 0; m < mutations; m++) bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
            if (i % 7 == 0) bytes = bytes[..rng.Next(bytes.Length)];
            try
            {
                var s = Ps2Sound.Decode(bytes, name);
                Assert.Equal(s.FrameCount * s.Channels, s.Samples.LongLength);
                Ps2Sound.Probe(bytes, name);
            }
            catch (AssetFormatException) { }
        }
    }
}

public class WriterAndConversionTests
{
    [Fact]
    public void WavWithSmplLoopRoundTrips()
    {
        var frames = TestData.Encode(TestData.Sine(12), f => f == 0 ? 6 : f == 11 ? 3 : 2);
        var sound = Ps2Sound.Decode(TestData.Vse(frames, looping: true), "loop.vse");
        var wav = SoundWriter.ToWav(sound);
        var info = AudioProbe.ProbeWav(wav, "loop.wav");
        Assert.Equal(22050, info.SampleRate);
        Assert.Equal(16, info.BitsPerSample);
        Assert.Equal((0L, sound.FrameCount), SoundWriter.ReadSmplLoop(wav));
        var back = SoundDecoder.Decode(wav, "loop.wav");
        Assert.Equal(sound.Samples, back.Samples);
        Assert.Equal(sound.Loop!.Start, back.Loop!.Start);
        Assert.Equal(sound.Loop.End, back.Loop.End);
        // without the loop: the plain 44-byte header
        var plain = SoundWriter.ToWav(sound, writeLoop: false);
        Assert.Equal(44 + sound.Samples.Length * 2, plain.Length);
        Assert.Null(SoundWriter.ReadSmplLoop(plain));
    }

    [Fact]
    public void ConversionReportsWhatIsApproximated()
    {
        var frames = TestData.Encode(TestData.Sine(12), f => f == 0 ? 6 : f == 11 ? 3 : 2);
        var result = SoundConversion.Convert("loop.vse", TestData.Vse(frames, looping: true), new SoundConvertOptions());
        Assert.True(result.Succeeded);
        Assert.Equal("loop.wav", result.OutputName);
        Assert.Contains(result.Notes, n => n.Contains("lossy already", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("22,043.0 Hz", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("'smpl' chunk", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("envelope", StringComparison.Ordinal));
        var failed = SoundConversion.Convert("bad.vse", [], new SoundConvertOptions());
        Assert.False(failed.Succeeded);
        Assert.NotNull(failed.Error);
        Assert.Equal("Converted 1 sound to WAV; 1 could not be converted.", SoundConversion.Summary([result, failed], SoundOutputFormat.Wav));
    }

    [Fact]
    public void PcmWavesDecodeAtAnyWidth()
    {
        var pcm = TestData.Sine(4);
        var wav16 = SoundWriter.Wav16(pcm, 1, 11025);
        var sound = SoundDecoder.Decode(wav16, "x.wav");
        Assert.Equal(pcm, sound.Samples);
        Assert.False(sound.IsLossySource);
        Assert.Empty(SoundConversion.Notes(sound, new SoundConvertOptions()));
        // 8-bit widens exactly
        var wav8 = new byte[44 + 4];
        SoundWriter.Wav16(new short[2], 1, 8000).AsSpan(0, 44).CopyTo(wav8);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wav8.AsSpan(32), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wav8.AsSpan(34), 8);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav8.AsSpan(28), 8000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav8.AsSpan(40), 4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav8.AsSpan(4), 40);
        wav8[44] = 0x80; wav8[45] = 0xFF; wav8[46] = 0x00; wav8[47] = 0x90;
        var eight = SoundDecoder.Decode(wav8, "e.wav");
        Assert.Equal(new short[] { 0, 127 << 8, -128 << 8, 16 << 8 }, eight.Samples);
    }

    [Fact]
    public void AiffDecodesToTheSameSamples()
    {
        var pcm = TestData.Sine(6);
        var aiff = SyntheticSounds.Aiff(pcm, 1, 22050);
        var sound = SoundDecoder.Decode(aiff, "x.aif");
        Assert.Equal(22050, sound.SampleRate);
        Assert.Equal(pcm, sound.Samples);
        Assert.Contains(sound.Quirks, q => q.Contains("AIFF", StringComparison.Ordinal));
    }

    [Fact]
    public void SyntheticSamplesDecodeAsBuilt()
    {
        var shot = Ps2Sound.Decode(SyntheticSounds.OneShotVse(), "s.vse");
        Assert.Equal(400 * 28, shot.FrameCount);
        Assert.Null(shot.Loop);
        var loop = Ps2Sound.Decode(SyntheticSounds.LoopingVse(), "l.vse");
        Assert.True(loop.Loop!.IsWhole(loop.FrameCount));
        var music = Ps2Sound.Decode(SyntheticSounds.Music(), "m.vmu");
        Assert.Equal(2, music.Channels);
        Assert.Equal(2000 * 28, music.FrameCount);
        Assert.DoesNotContain(music.Problems, p => p.Severity != SoundSeverity.Info);
    }

    [Fact]
    public void PlaybackOfPs2SoundsGoesThroughTheSharedDecoder()
    {
        var bytes = TestData.Vse(TestData.Encode(TestData.Sine(10)));
        var playback = AudioDecoding.ForPlayback("x.vse", new MemoryStream(bytes));
        Assert.Equal(".wav", playback.Extension);
        Assert.Equal(22050, AudioProbe.ProbeWav(playback.Bytes, "x.wav").SampleRate);
    }

    [Fact]
    public void WaveformPeaksMatchADirectScan()
    {
        var rng = new Random(5);
        var samples = Enumerable.Range(0, 20000 * 2).Select(_ => (short)rng.Next(-30000, 30000)).ToArray();
        var peaks = new Cairn.Snd.WaveformPeaks(samples, 2);
        var min = new float[37];
        var max = new float[37];
        peaks.Compute(1, 123.5, 20000 / 37.0, min, max);
        for (int x = 0; x < 37; x++)
        {
            long from = (long)Math.Floor(123.5 + x * 20000 / 37.0), to = Math.Min(20000, (long)Math.Floor(123.5 + (x + 1) * 20000 / 37.0));
            if (from >= to) continue;
            var col = Enumerable.Range((int)from, (int)(to - from)).Select(f => samples[f * 2 + 1]).ToArray();
            Assert.Equal(col.Min() / 32768f, min[x]);
            Assert.Equal(col.Max() / 32768f, max[x]);
        }
    }
}
