using Cairn.Formats.Audio;
using Cairn.Vpp.Model;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Snd.Tests;

/// <summary>
/// The Ogg Vorbis writer (libvorbis through cairn-vorbis.dll): encode, decode with NVorbis, and compare. The thresholds
/// sit a few dB under what the reference encoder gives for these signals (measured the same way with ffmpeg's
/// libvorbis), so a broken build or a wrong binding fails them by a wide margin.
/// </summary>
public class OggEncoderTests(ITestOutputHelper output)
{
    /// <summary>Two tones per channel (different in each channel) with a slow swell.</summary>
    private static short[] Tones(int rate, int channels, double seconds)
    {
        int frames = (int)(rate * seconds);
        var pcm = new short[frames * channels];
        for (int c = 0; c < channels; c++)
        {
            double f1 = c == 0 ? 440 : 330, f2 = Math.Min(c == 0 ? 1500 : 2200, rate * 0.4);
            for (int i = 0; i < frames; i++)
            {
                double t = (double)i / rate, swell = 0.6 + 0.4 * Math.Sin(2 * Math.PI * 1.3 * t + c);
                pcm[i * channels + c] = (short)(32767 * swell * (0.3 * Math.Sin(2 * Math.PI * f1 * t) + 0.15 * Math.Sin(2 * Math.PI * f2 * t + c)));
            }
        }
        return pcm;
    }

    private static short[] Noise(int rate, int channels, double seconds, int seed)
    {
        var random = new Random(seed);
        var pcm = new short[(int)(rate * seconds) * channels];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)((random.NextDouble() * 2 - 1) * 8000);
        return pcm;
    }

    private static DecodedSound Decode(byte[] ogg) => SoundDecoder.Decode(ogg, "test.ogg");

    private static double Snr(ReadOnlySpan<short> source, ReadOnlySpan<short> decoded)
    {
        double signal = 0, noise = 0;
        int n = Math.Min(source.Length, decoded.Length);
        for (int i = 0; i < n; i++) { double d = source[i] - decoded[i]; signal += (double)source[i] * source[i]; noise += d * d; }
        return noise <= 0 ? 99 : 10 * Math.Log10(signal / noise);
    }

    /// <summary>
    /// Mean absolute difference in dB of third-octave band energies over 2,048-sample frames, per channel (bands within
    /// 50 dB of a frame's loudest only, 50 Hz to 16 kHz or 95% of Nyquist).
    /// </summary>
    internal static double BandDistance(short[] a, short[] b, int channels, int rate)
    {
        const int N = 2048;
        var edges = new List<double>();
        for (double f = 50; f < Math.Min(16000, rate * 0.475); f *= Math.Pow(2, 1.0 / 3)) edges.Add(f);
        int frames = Math.Min(a.Length, b.Length) / channels;
        double sum = 0;
        int count = 0;
        var re = new double[N];
        var im = new double[N];
        double[] Bands(short[] x, int c, int start)
        {
            for (int i = 0; i < N; i++) { re[i] = x[(start + i) * channels + c] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); im[i] = 0; }
            Fft(re, im);
            var e = new double[edges.Count - 1];
            for (int k = 1; k < N / 2; k++)
            {
                double f = (double)k * rate / N;
                int band = edges.FindLastIndex(edge => edge <= f);
                if (band >= 0 && band < e.Length) e[band] += re[k] * re[k] + im[k] * im[k];
            }
            return e;
        }
        for (int c = 0; c < channels; c++)
            for (int start = 0; start + N <= frames; start += N / 2)
            {
                var ea = Bands(a, c, start);
                var eb = Bands(b, c, start);
                double max = ea.Max();
                if (max <= 0) continue;
                for (int k = 0; k < ea.Length; k++)
                {
                    if (ea[k] < max * 1e-5) continue;
                    sum += Math.Min(30, Math.Abs(10 * Math.Log10((eb[k] + 1e-3) / (ea[k] + 1e-3))));
                    count++;
                }
            }
        return count == 0 ? 0 : sum / count;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len, wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int u = i + j, v = u + len / 2;
                    double vr = re[v] * cr - im[v] * ci, vi = re[v] * ci + im[v] * cr;
                    re[v] = re[u] - vr; im[v] = im[u] - vi;
                    re[u] += vr; im[u] += vi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    [Fact]
    public void EncoderLoads() => Assert.StartsWith("Xiph.Org libVorbis 1.3.7", OggVorbisWriter.Version);

    // reference (ffmpeg + libvorbis, same signals): q-0.1 22.6-29.1 dB, q0.5 35.5-41.3 dB, q1.0 37.8-49.3 dB
    [Theory]
    [InlineData(8000, 1)]
    [InlineData(8000, 2)]
    [InlineData(11025, 1)]
    [InlineData(11025, 2)]
    [InlineData(22050, 1)]
    [InlineData(22050, 2)]
    [InlineData(44100, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 1)]
    [InlineData(48000, 2)]
    public void TonesRoundTripAtEveryRateAndQuality(int rate, int channels)
    {
        var pcm = Tones(rate, channels, 1.0);
        int previous = 0;
        // q-1 low-passes 44.1/48 kHz sounds hard, which shows in the quiet high bands (reference: up to 2.7 dB)
        foreach (var (quality, minSnr, maxBands) in new[] { (-0.1f, 18.0, 3.5), (0.5f, 30.0, 1.5), (1.0f, 33.0, 1.5) })
        {
            var ogg = OggVorbisWriter.Write(pcm, channels, rate, quality);
            var back = Decode(ogg);
            double snr = Snr(pcm, back.Samples), bands = BandDistance(pcm, back.Samples, channels, rate);
            output.WriteLine($"{rate} Hz {channels} ch q{quality * 10:0}: {ogg.Length:N0} bytes, SNR {snr:0.00} dB, bands {bands:0.00} dB");
            Assert.Equal(("Vorbis", rate, channels), (back.Codec, back.SampleRate, back.Channels));
            Assert.Equal(pcm.Length, back.Samples.Length);
            Assert.True(snr >= minSnr, $"SNR {snr:0.00} dB < {minSnr} at q{quality}");
            Assert.True(bands <= maxBands, $"band distance {bands:0.00} dB at q{quality}");
            Assert.True(ogg.Length > previous, "a higher quality gives a larger file");
            previous = ogg.Length;
        }
    }

    // reference: noise keeps its spectrum (band distance 0.18-0.53 dB at q0.5) though the waveform differs
    [Theory]
    [InlineData(11025, 1)]
    [InlineData(44100, 2)]
    public void NoiseKeepsItsSpectrum(int rate, int channels)
    {
        var pcm = Noise(rate, channels, 1.0, rate);
        var back = Decode(OggVorbisWriter.Write(pcm, channels, rate, 0.5f));
        double bands = BandDistance(pcm, back.Samples, channels, rate);
        output.WriteLine($"noise {rate} Hz {channels} ch: bands {bands:0.00} dB");
        Assert.Equal(pcm.Length, back.Samples.Length);
        Assert.True(bands <= 1.0, $"band distance {bands:0.00} dB");
    }

    [Fact]
    public void OutputIsReproducibleAndCarriesTags()
    {
        var pcm = Tones(22050, 1, 0.3);
        var tags = new Dictionary<string, string> { ["TITLE"] = "Ünïcode test", ["LOOPSTART"] = "100", ["LOOPLENGTH"] = "2000" };
        var a = OggVorbisWriter.Write(pcm, 1, 22050, 0.4f, tags);
        var b = OggVorbisWriter.Write(pcm, 1, 22050, 0.4f, tags);
        Assert.Equal(a, b);
        Assert.Equal("OggS"u8.ToArray(), a[..4]);
        using var reader = new NVorbis.VorbisReader(new MemoryStream(a), true);
        Assert.Equal("Ünïcode test", reader.Tags.GetTagSingle("TITLE"));
        Assert.StartsWith("Xiph.Org libVorbis", reader.Tags.EncoderVendor);
        var back = Decode(a);
        Assert.Equal(100, back.Loop!.Start);
        Assert.Equal(2100, back.Loop.End);
    }

    [Fact]
    public void ShortAndEmptySoundsEncode()
    {
        foreach (int frames in new[] { 0, 1, 100, 4096, 4097 })
        {
            var pcm = Tones(11025, 2, 1.0)[..(frames * 2)];
            var ogg = OggVorbisWriter.Write(pcm, 2, 11025, 0.5f);
            using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
            var buffer = new float[16384];
            long read = 0;
            int got;
            while ((got = reader.ReadSamples(buffer, 0, buffer.Length)) > 0) read += got;
            Assert.Equal(frames * 2, read);
        }
    }

    /// <summary>
    /// A stream where NVorbis 0.10.5 decodes 80 frames past the end (22,064 frames of a 22,050 Hz mono spike at q5, the
    /// shape of the PS2 Spike_Open.vse): the last page's granule position cuts the decode to the exact length.
    /// </summary>
    [Fact]
    public void DecodeStopsAtTheFinalGranule()
    {
        int checkedCount = 0;
        foreach (int frames in new[] { 22064, 22000, 9001, 30000 })
            foreach (float quality in new[] { 0.3f, 0.5f, 0.6f })
            {
                var pcm = new short[frames];
                var random = new Random(frames);
                for (int i = 0; i < frames; i++) pcm[i] = (short)(random.NextDouble() * 20000 * Math.Exp(-i / 4000.0) * (random.Next(2) * 2 - 1));
                var ogg = OggVorbisWriter.Write(pcm, 1, 22050, quality);
                Assert.Equal(frames, AudioDecoding.FinalGranule(ogg));
                Assert.Equal(frames, Decode(ogg).FrameCount);
                checkedCount++;
            }
        Assert.Equal(12, checkedCount);
        Assert.Null(AudioDecoding.FinalGranule("OggS"u8));
        Assert.Null(AudioDecoding.FinalGranule([.. OggVorbisWriter.Write(new short[100], 1, 22050, 0.5f), 1, 2, 3]));
    }

    [Fact]
    public void RefusesWhatVorbisCannotHold()
    {
        var pcm = new short[200];
        Assert.Throws<ArgumentException>(() => OggVorbisWriter.Write(pcm, 0, 22050, 0.5f));
        Assert.Throws<ArgumentException>(() => OggVorbisWriter.Write(pcm, 9, 22050, 0.5f));
        Assert.Throws<ArgumentException>(() => OggVorbisWriter.Write(pcm, 1, 500, 0.5f));
        Assert.Throws<ArgumentException>(() => OggVorbisWriter.Write(pcm, 1, 22050, 1.5f));
        Assert.Throws<ArgumentException>(() => OggVorbisWriter.Write(pcm, 1, 22050, float.NaN));
        Assert.Throws<ArgumentException>(() => OggVorbisWriter.Write(new short[201], 2, 22050, 0.5f));
        Assert.Throws<OperationCanceledException>(() => OggVorbisWriter.Write(pcm, 1, 22050, 0.5f, cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void ConversionToOggKeepsLoopAndSaysItIsLossy()
    {
        var frames = TestData.Encode(TestData.Sine(40), f => f == 0 ? 6 : f == 39 ? 3 : 2);
        var vse = TestData.Vse(frames, looping: true);
        var source = SoundDecoder.Decode(vse, "loop.vse");
        var result = SoundConversion.Convert("loop.vse", vse, new SoundConvertOptions { Format = SoundOutputFormat.Ogg, Quality = 0.5f });
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("loop.ogg", result.OutputName);
        var back = Decode(result.Bytes!);
        Assert.Equal(source.FrameCount, back.FrameCount);
        Assert.Equal((source.Loop!.Start, source.Loop.End), (back.Loop!.Start, back.Loop.End));
        Assert.Contains(result.Notes, n => n.Contains("Ogg Vorbis (q5) is lossy too", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("LOOPSTART/LOOPLENGTH", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("no Ogg equivalent", StringComparison.Ordinal));
        var noLoop = SoundConversion.Convert("loop.vse", vse, new SoundConvertOptions { Format = SoundOutputFormat.Ogg, WriteLoop = false });
        Assert.Null(Decode(noLoop.Bytes!).Loop);
        Assert.Equal("Converted 1 sound to OGG.", SoundConversion.Summary([result], SoundOutputFormat.Ogg));
        Assert.Equal("q5, about 160 kbit/s at 44.1 kHz stereo", SoundConversion.QualityText(0.5f));
        Assert.Equal("q-1, about 45 kbit/s at 44.1 kHz stereo", SoundConversion.QualityText(-0.1f));
        Assert.Equal("q10, about 500 kbit/s at 44.1 kHz stereo", SoundConversion.QualityText(1.0f));
    }

    /// <summary>
    /// Every 40th PS2 sound of the local packfiles (and one .vmu piece of music) at q5: exact length, the
    /// loop kept, the spectrum kept (passes trivially without the files; they are only read).
    /// </summary>
    [Fact]
    public void RealSoundsConvert()
    {
        var picked = new List<(string Name, byte[] Bytes)>();
        int index = 0, vmu = 0;
        foreach (var root in new[] { LocalPaths.Ps2Directory, LocalPaths.MeshesStuffDirectory })
        {
            if (root is null || !Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.vpp", SearchOption.AllDirectories))
                foreach (var item in VppPackage.Open(file).Items.Where(i => Ps2Sound.IsPs2SoundName(i.Name)))
                {
                    bool isVmu = item.Name.EndsWith(".vmu", StringComparison.OrdinalIgnoreCase);
                    if (isVmu ? vmu++ < 1 : index++ % 40 == 0) picked.Add((item.Name, item.Source.ReadAll()));
                }
        }
        var failures = new List<string>();
        double worst = 0;
        foreach (var (name, bytes) in picked)
        {
            DecodedSound sound;
            try { sound = SoundDecoder.Decode(bytes, name); }
            catch (Cairn.Formats.AssetFormatException) { continue; }
            if (sound.FrameCount < 4096) continue;
            var result = SoundConversion.Convert(sound, new SoundConvertOptions { Format = SoundOutputFormat.Ogg });
            if (!result.Succeeded) { failures.Add($"{name}: {result.Error}"); continue; }
            var back = Decode(result.Bytes!);
            double bands = BandDistance(sound.Samples, back.Samples, sound.Channels, sound.SampleRate);
            worst = Math.Max(worst, bands);
            if (back.FrameCount != sound.FrameCount) failures.Add($"{name}: {back.FrameCount} frames, want {sound.FrameCount}");
            if ((back.Loop is null) != (sound.Loop is null)) failures.Add($"{name}: loop lost");
            // quiet, noisy effects lose most in their faint bands (Ultor_Idle_look: 2.2 dB, as with ffmpeg's libvorbis)
            if (bands > 3.0) failures.Add($"{name}: band distance {bands:0.00} dB");
        }
        output.WriteLine($"{picked.Count} sounds, worst band distance {worst:0.00} dB");
        foreach (var f in failures.Take(20)) output.WriteLine(f);
        Assert.Empty(failures);
    }
}
