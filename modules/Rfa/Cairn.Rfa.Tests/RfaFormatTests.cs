using System.Buffers.Binary;
using System.Numerics;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Tests;

public class RfaFormatTests
{
    internal static RfaClip SampleClip(int version = 8, bool morph = true)
    {
        var bones = new[]
        {
            new RfaBoneTrack(10f,
                [new RfaRotKey(160, 1, 2, 3, 16382, 5, -7, 0), new RfaRotKey(480, -100, 200, -300, 16000, 0, 127, 9)],
                [new RfaPosKey(160, new Vector3(1, 2, 3), new Vector3(1, 2, 3), new Vector3(1.5f, 2, 3)),
                 new RfaPosKey(480, new Vector3(4, 5, 6), new Vector3(3.5f, 5, 6), new Vector3(4, 5, 6))]),
            new RfaBoneTrack(2f, [], [RfaPosKey.Constant(160, new Vector3(0, 0.5f, 0))]),
            new RfaBoneTrack(0f, [new RfaRotKey(160, 0, 0, 0, 16383)], []),
        };
        var data = RfaMorph.Empty;
        if (morph && version == 8)
        {
            data = new RfaMorph([3, 7, 11], 2, [160, 480], new RfaMorphBounds(new Vector3(-1), new Vector3(1)),
                [0, 128, 255, 1, 2, 3, 4, 5, 6, 255, 255, 255, 0, 0, 0, 9, 9, 9], []);
        }
        else if (morph)
        {
            data = new RfaMorph([3, 7, 11], 2, [], null, [],
                [new(1, 2, 3), new(4, 5, 6), new(7, 8, 9), new(-1, -2, -3), new(-4, -5, -6), new(-7, -8, -9)]);
        }
        return new RfaClip
        {
            Version = version,
            PosReduction = 0.01f,
            RotReduction = 0.02f,
            StartTime = 160,
            EndTime = 480,
            RampIn = 160,
            RampOut = 320,
            TotalRotation = new Quaternion(0, 0, 0, 1),
            TotalTranslation = new Vector3(0.25f, 0, 0),
            Bones = [.. bones],
            Morph = data,
        };
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(8, false)]
    [InlineData(7, true)]
    [InlineData(7, false)]
    public void SyntheticClipsRoundTrip(int version, bool morph)
    {
        var clip = SampleClip(version, morph);
        byte[] bytes = RfaWriter.Write(clip);
        var read = RfaReader.Read(bytes, "synthetic.rfa");
        ModelAssert.Equal(clip, read);
        Assert.Equal(bytes, RfaWriter.Write(read));
    }

    [Fact]
    public void TheWriterUsesTheCanonicalLayout()
    {
        var clip = SampleClip(8, true);
        byte[] b = RfaWriter.Write(clip);
        Assert.Equal(RfaClip.Signature, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x18)));
        // Bones are contiguous in index order right after the offset table.
        int bone0 = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x50));
        int bone1 = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x54));
        Assert.Equal(0x50 + 3 * 4, bone0);
        Assert.Equal(bone0 + clip.Bones[0].ByteSize, bone1);
        // Morph indices follow the bones; the keyframes start at the next multiple of 4.
        int morphVertices = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x48));
        int morphKeyframes = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x4C));
        Assert.Equal(morphVertices + 6 + 2, morphKeyframes);
        Assert.Equal(0, morphKeyframes % 4);
    }

    [Fact]
    public void KeyframesWithoutVerticesKeepTheirTimesAndNoBounds()
    {
        // One stock v8 clip stores 61 keyframe times and no vertices at all.
        var clip = SampleClip(8, false) with { Morph = new RfaMorph([], 3, [0, 0, 0], null, [], []) };
        var read = RfaReader.Read(RfaWriter.Write(clip), "x.rfa");
        Assert.Equal(3, read.Morph.KeyframeCount);
        Assert.Equal([0, 0, 0], read.Morph.KeyframeTimes.ToArray());
        Assert.Null(read.Morph.Bounds);
        Assert.False(read.Morph.IsEmpty);
    }

    [Fact]
    public void MorphPositionsDecodeForBothVersions()
    {
        var v8 = SampleClip(8, true).Morph;
        Assert.Equal(new Vector3(-1, 128 / 255f * 2 - 1, 1), v8.GetPosition(0, 0));
        var v7 = SampleClip(7, true).Morph;
        Assert.Equal(new Vector3(-4, -5, -6), v7.GetPosition(1, 1));
    }

    [Fact]
    public void BonesOutOfOrderAndZeroMorphOffsetsStillLoad()
    {
        // Hand-build a file whose bone table points backwards and whose morph offsets are 0 (a
        // community exporter layout); it loads and is rewritten canonically.
        var clip = SampleClip(8, false);
        byte[] canonical = RfaWriter.Write(clip);
        byte[] odd = (byte[])canonical.Clone();
        int b0 = BinaryPrimitives.ReadInt32LittleEndian(odd.AsSpan(0x50));
        int b1 = BinaryPrimitives.ReadInt32LittleEndian(odd.AsSpan(0x54));
        int b2 = BinaryPrimitives.ReadInt32LittleEndian(odd.AsSpan(0x58));
        // Swap the table entries of bones 0 and 2 and write a clip with those bones swapped.
        BinaryPrimitives.WriteInt32LittleEndian(odd.AsSpan(0x50), b2);
        BinaryPrimitives.WriteInt32LittleEndian(odd.AsSpan(0x58), b0);
        BinaryPrimitives.WriteInt32LittleEndian(odd.AsSpan(0x48), 0);
        BinaryPrimitives.WriteInt32LittleEndian(odd.AsSpan(0x4C), 0);
        var read = RfaReader.Read(odd, "odd.rfa");
        Assert.Equal(clip.Bones[2].Weight, read.Bones[0].Weight);
        Assert.Equal(clip.Bones[0].Weight, read.Bones[2].Weight);
        Assert.Equal(b1, BinaryPrimitives.ReadInt32LittleEndian(odd.AsSpan(0x54)));
        Assert.True(read.Morph.IsEmpty);
    }

    [Fact]
    public void ProbeReadsOnlyTheHeader()
    {
        byte[] bytes = RfaWriter.Write(SampleClip(7, true));
        var probe = RfaProbe.Probe(bytes[..0x50], "x.rfa");
        Assert.Equal(7, probe.Version);
        Assert.Equal(3, probe.BoneCount);
        Assert.Equal(3, probe.MorphVertexCount);
        Assert.Equal(2, probe.MorphKeyframeCount);
        Assert.Equal(320, probe.Duration);
        Assert.True(probe.HasMorph);
    }

    // ── Malformed input ──────────────────────────────────────────────────────

    public static TheoryData<string, Func<byte[], byte[]>> Corruptions => new()
    {
        { "empty", _ => [] },
        { "truncated header", b => b[..40] },
        { "bad signature", b => Patch(b, 0, 0x12345678) },
        { "version 9", b => Patch(b, 4, 9) },
        { "negative bones", b => Patch(b, 0x18, -1) },
        { "huge bone count", b => Patch(b, 0x18, int.MaxValue) },
        { "bone offset past the end", b => Patch(b, 0x50, b.Length + 100) },
        { "negative bone offset", b => Patch(b, 0x54, -8) },
        { "negative morph count", b => Patch(b, 0x1C, -3) },
        { "huge morph counts", b => Patch(Patch(b, 0x1C, 0x7FFF0000), 0x20, 0x7FFF0000) },
        { "morph offset past the end", b => Patch(b, 0x4C, b.Length + 4) },
        { "truncated keys", b => b[..(b.Length - 120)] },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void MalformedClipsAreRejectedWithOneExceptionType(string what, Func<byte[], byte[]> corrupt)
    {
        byte[] bytes = corrupt(RfaWriter.Write(SampleClip(8, true)));
        var ex = Assert.Throws<AssetFormatException>(() => RfaReader.Read(bytes, "bad.rfa"));
        Assert.Contains("bad.rfa", ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(what));
    }

    [Fact]
    public void ANegativeKeyCountIsRejected()
    {
        byte[] bytes = RfaWriter.Write(SampleClip(8, false));
        int bone0 = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x50));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(bone0 + 4), -1);
        Assert.Throws<AssetFormatException>(() => RfaReader.Read(bytes, "bad.rfa"));
    }

    [Fact]
    public void FuzzedClipsNeverThrowAnythingElse()
    {
        byte[] good = RfaWriter.Write(SampleClip(8, true));
        var rng = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            byte[] b = (byte[])good.Clone();
            int flips = rng.Next(1, 6);
            for (int f = 0; f < flips; f++) b[rng.Next(b.Length)] = (byte)rng.Next(256);
            if (rng.Next(4) == 0) b = b[..rng.Next(b.Length)];
            try
            {
                RfaReader.Read(b, "fuzz.rfa");
            }
            catch (AssetFormatException)
            {
            }
        }
    }

    [Fact]
    public void TheWriterRejectsInconsistentMorphData()
    {
        var clip = SampleClip(8, true);
        Assert.Throws<ArgumentException>(() => RfaWriter.Write(clip with { Morph = clip.Morph with { KeyframeTimes = [160] } }));
        Assert.Throws<ArgumentException>(() => RfaWriter.Write(clip with { Morph = clip.Morph with { Bounds = null } }));
        Assert.Throws<ArgumentException>(() => RfaWriter.Write(clip with { Version = 7 }));
        Assert.Throws<ArgumentException>(() => RfaWriter.Write(clip with { Version = 6 }));
    }

    private static byte[] Patch(byte[] b, int offset, int value)
    {
        var copy = (byte[])b.Clone();
        if (offset + 4 <= copy.Length) BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }
}
