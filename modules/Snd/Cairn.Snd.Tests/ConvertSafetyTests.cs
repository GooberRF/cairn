using System.Buffers.Binary;
using Cairn.Formats;
using Cairn.Formats.Audio;

namespace Cairn.Snd.Tests;

/// <summary>
/// Converting never takes the source's place (same-format sounds are left out, output names never equal the source's),
/// and damaged sounds only ever fail as unreadable files (no other exception escapes a decode or a conversion).
/// </summary>
public class ConvertSafetyTests
{
    private static readonly short[] Tone = SyntheticSounds.Sine(2000, 37, 9000);

    [Fact]
    public void OutputNamesNeverEqualTheSource()
    {
        Assert.Equal("boom.wav", SoundConversion.OutputName("boom.vse", SoundOutputFormat.Wav));
        Assert.Equal("master (converted).wav", SoundConversion.OutputName("master.wav", SoundOutputFormat.Wav));
        Assert.Equal("master (converted).wav", SoundConversion.OutputName("master.WAV", SoundOutputFormat.Wav));
        Assert.Equal("master.ogg", SoundConversion.OutputName("master.wav", SoundOutputFormat.Ogg));
        Assert.Equal("theme (converted).ogg", SoundConversion.OutputName("theme.ogg", SoundOutputFormat.Ogg));
    }

    [Fact]
    public void A16BitWavToWavIsLeftOut_A24BitWavConvertsUnderAnotherName()
    {
        var wav16 = SoundWriter.Wav16(Tone, 1, 22050);
        var same = SoundConversion.Convert("master.wav", wav16, new SoundConvertOptions());
        Assert.False(same.Succeeded);
        Assert.True(same.Skipped);
        Assert.Contains("16-bit WAV already", same.Error);
        Assert.NotNull(SoundConversion.SameFormatReason(SoundDecoder.Decode(wav16, "master.wav"), SoundOutputFormat.Wav));
        Assert.Null(SoundConversion.SameFormatReason(SoundDecoder.Decode(wav16, "master.wav"), SoundOutputFormat.Ogg));

        // the reviewer's case: a 24-bit master.wav with a LIST chunk is worth converting, to "master (converted).wav"
        var wav24 = SyntheticSounds.Wav24(Tone, 1, 22050);
        var sound24 = SoundDecoder.Decode(wav24, "master.wav");
        Assert.Equal(24, sound24.SourceBits);
        Assert.Null(SoundConversion.SameFormatReason(sound24, SoundOutputFormat.Wav));
        var converted = SoundConversion.Convert("master.wav", wav24, new SoundConvertOptions());
        Assert.True(converted.Succeeded);
        Assert.Equal("master (converted).wav", converted.OutputName);
        Assert.Equal(Tone, SoundDecoder.Decode(converted.Bytes!, converted.OutputName).Samples);

        Assert.Equal("Converted 1 sound to WAV; 1 left out (already in that format, or the name is taken).", SoundConversion.Summary([converted, same], SoundOutputFormat.Wav));
    }

    [Fact]
    public void AnAiffWhoseSoundDataLiesPastTheEndIsRefusedAsDamaged()
    {
        var aif = SyntheticSounds.Aiff(Tone, 1, 22050);
        BinaryPrimitives.WriteUInt32BigEndian(aif.AsSpan(46), 0x00100000); // SSND offset field
        var ex = Assert.Throws<AssetFormatException>(() => SoundDecoder.Decode(aif, "bad.aif"));
        Assert.Contains("past the end", ex.Message);
        var result = SoundConversion.Convert("bad.aif", aif, new SoundConvertOptions());
        Assert.False(result.Succeeded);
        Assert.False(result.Skipped);
        Assert.Contains("past the end", result.Error);
    }

    [Fact]
    public void MutatedSoundsOnlyEverFailAsUnreadable()
    {
        var samples = new (string Name, byte[] Bytes)[]
        {
            ("tone.aif", SyntheticSounds.Aiff(Tone, 1, 22050)),
            ("tone.wav", SoundWriter.Wav16(Tone, 2, 22050, new SoundLoop(100, 900, true, "test"))),
            ("deep.wav", SyntheticSounds.Wav24(Tone, 1, 11025)),
            ("shot.vse", SyntheticSounds.OneShotVse(60)),
            ("music.vmu", SyntheticSounds.Music(120)),
        };
        var random = new Random(4);
        int refused = 0;
        foreach (var (name, good) in samples)
        {
            for (int i = 0; i < 1500; i++)
            {
                var bytes = (byte[])good.Clone();
                int edits = 1 + random.Next(6);
                for (int k = 0; k < edits; k++)
                {
                    int at = random.Next(bytes.Length);
                    // header fields most often: the first 64 bytes hold every size, count and offset
                    if (random.Next(3) > 0) at = random.Next(Math.Min(64, bytes.Length));
                    bytes[at] = (byte)random.Next(256);
                }
                if (random.Next(10) == 0) bytes = bytes[..random.Next(bytes.Length)];
                try { SoundDecoder.Decode(bytes, name); }
                catch (AssetFormatException) { refused++; }
                var result = SoundConversion.Convert(name, bytes, new SoundConvertOptions { WriteLoop = true });
                Assert.True(result.Succeeded || result.Error is not null);
            }
        }
        Assert.True(refused > 0);
    }
}
