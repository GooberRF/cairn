using System.Buffers.Binary;
using System.Numerics;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Red Faction II clips (<c>.rfa</c> version 9) built in code for tests and self-tests: no game bytes. The layout is the
/// one <see cref="RfaReader"/> reads for version 9 (the version 8 header, offset table and morph data, with short keys).
/// </summary>
public static class SyntheticRf2Clip
{
    /// <summary>One bone: its weight, rotation keys (time, file-convention int16 components) and position keys (time, point).</summary>
    public sealed record Bone(float Weight, IReadOnlyList<(int Time, short X, short Y, short Z, short W)> Rotations, IReadOnlyList<(int Time, Vector3 Position)> Positions);

    /// <summary>Morph data as version 8 stores it: vertex indices, keyframe times, the box and 3 bytes per vertex and keyframe.</summary>
    public sealed record Morph(IReadOnlyList<short> Vertices, IReadOnlyList<int> Times, Vector3 Min, Vector3 Max, byte[] Positions);

    /// <summary>
    /// A clip for <see cref="SyntheticRf2Mesh.SampleCharacter"/> (two bones): the root still, the arm turning a quarter
    /// turn about Y and back over 30 frames while it rises 0.2 m, ramps of 480 ticks; with <paramref name="morph"/>
    /// three vertices (an odd count, so the index block is padded) move over two keyframes.
    /// </summary>
    public static byte[] SampleSwing(bool morph = false)
    {
        short s = (short)Math.Floor(16383 * Math.Sqrt(0.5)); // rounded down: no longer than 1
        var root = new Bone(10f, [(0, 0, 0, 0, 16383), (4800, 0, 0, 0, 16383)], [(0, Vector3.Zero), (4800, Vector3.Zero)]);
        // file convention: the conjugate of the active quarter turn about Y
        var arm = new Bone(10f, [(0, 0, 0, 0, 16383), (2400, 0, (short)-s, 0, s), (4800, 0, 0, 0, 16383)],
            [(0, new Vector3(0, 1, 0)), (2400, new Vector3(0, 1.2f, 0)), (4800, new Vector3(0, 1, 0))]);
        var m = morph ? new Morph([0, 1, 2], [0, 4800], new Vector3(0, 1, 0), new Vector3(1, 1.5f, 1), [0, 0, 0, 255, 0, 0, 0, 0, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255]) : null;
        return Build(0, 4800, 480, 480, [root, arm], m);
    }

    /// <summary>Writes a version 9 clip.</summary>
    public static byte[] Build(int start, int end, int rampIn, int rampOut, IReadOnlyList<Bone> bones, Morph? morph = null)
    {
        ArgumentNullException.ThrowIfNull(bones);
        var b = new List<byte>();
        void I(int v) { Span<byte> x = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(x, v); b.AddRange(x.ToArray()); }
        void S(short v) { Span<byte> x = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(x, v); b.AddRange(x.ToArray()); }
        void F(float v) => I(BitConverter.SingleToInt32Bits(v));
        void Patch(int at, int v) { var x = BitConverter.GetBytes(v); for (int i = 0; i < 4; i++) b[at + i] = x[i]; }

        I(unchecked((int)RfaClip.Signature)); I(RfaClip.Rf2Version); F(1e-5f); F(1e-5f);
        I(start); I(end); I(bones.Count); I(morph?.Vertices.Count ?? 0); I(morph?.Times.Count ?? 0); I(rampIn); I(rampOut);
        F(0); F(0); F(0); F(1); F(0); F(0); F(0);
        int table = b.Count;
        for (int i = 0; i < 2 + bones.Count; i++) I(0);
        for (int i = 0; i < bones.Count; i++)
        {
            Patch(table + 8 + 4 * i, b.Count);
            var bone = bones[i];
            F(bone.Weight); S((short)bone.Rotations.Count); S((short)bone.Positions.Count);
            foreach (var (t, x, y, z, w) in bone.Rotations) { I(t); S(x); S(y); S(z); S(w); }
            foreach (var (t, p) in bone.Positions) { I(t); F(p.X); F(p.Y); F(p.Z); }
        }
        Patch(table, b.Count);
        if (morph is not null) foreach (short v in morph.Vertices) S(v);
        while (b.Count % 4 != 0) b.Add(0);
        Patch(table + 4, b.Count);
        if (morph is not null)
        {
            foreach (int t in morph.Times) I(t);
            if (morph.Vertices.Count > 0 && morph.Times.Count > 0)
            {
                F(morph.Min.X); F(morph.Min.Y); F(morph.Min.Z); F(morph.Max.X); F(morph.Max.Y); F(morph.Max.Z);
                b.AddRange(morph.Positions);
            }
        }
        return [.. b];
    }
}
