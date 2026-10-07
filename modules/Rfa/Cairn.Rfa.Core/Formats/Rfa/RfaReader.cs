using System.Collections.Immutable;
using System.Numerics;

namespace Cairn.Rfa.Formats.Rfa;

/// <summary>
/// Reads .rfa clips. Bones and morph data are located through the file's own offset table, so the
/// canonical stock layout and other layouts (community exporters that leave the morph offsets at 0,
/// bones out of order) load alike. Every count and offset is checked against the bytes actually
/// present before anything is allocated; bad input raises <see cref="AssetFormatException"/>.
/// </summary>
public static class RfaReader
{
    /// <summary>Reads a clip from disk.</summary>
    /// <exception cref="AssetFormatException">The file is not a readable version 7 or 8 clip.</exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    public static RfaClip ReadFile(string path) => Read(FileBytes.ReadFile(path), Path.GetFileName(path));

    /// <summary>Reads a clip from a stream (for example a VPP entry).</summary>
    public static RfaClip Read(Stream stream, string name) => Read(FileBytes.Read(stream, name), name);

    /// <summary>Reads a clip from its file image.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="name">The file name, for messages.</param>
    public static RfaClip Read(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r = new BinaryCursor(data, name);
        var h = RfaProbe.ReadHeader(r);

        r.Position = RfaClip.HeaderSize;
        int morphVerticesOffset = r.ReadInt32("morph vertex offset");
        int morphKeyframesOffset = r.ReadInt32("morph keyframe offset");
        r.EnsureCount(h.BoneCount, 4, "bone offset table");
        var boneOffsets = new int[h.BoneCount];
        for (int i = 0; i < boneOffsets.Length; i++) boneOffsets[i] = r.ReadInt32();

        var bones = ImmutableArray.CreateBuilder<RfaBoneTrack>(h.BoneCount);
        long boneBytes = 0;
        for (int i = 0; i < boneOffsets.Length; i++)
        {
            r.Seek(boneOffsets[i], $"bone {i}'s offset");
            int start = r.Position;
            bones.Add(ReadBone(r, i));
            // Offsets may point anywhere, so many bones could share one record and a small file would
            // expand into bones x keys keys. Real bone records never overlap: together they fit in the file.
            boneBytes += r.Position - start;
            if (boneBytes > data.Length)
                throw r.Fail($"bone {i}'s keys overlap other bones' (the bone records add up to more than the file's {data.Length} bytes).");
        }

        var morph = ReadMorph(r, h, morphVerticesOffset, morphKeyframesOffset);

        return new RfaClip
        {
            Version = h.Version,
            PosReduction = h.PosReduction,
            RotReduction = h.RotReduction,
            StartTime = h.StartTime,
            EndTime = h.EndTime,
            RampIn = h.RampIn,
            RampOut = h.RampOut,
            TotalRotation = h.TotalRotation,
            TotalTranslation = h.TotalTranslation,
            Bones = bones.MoveToImmutable(),
            Morph = morph,
        };
    }

    private static RfaBoneTrack ReadBone(BinaryCursor r, int index)
    {
        string what = $"bone {index}";
        float weight = r.ReadSingle(what);
        short rotCount = r.ReadInt16(what);
        short posCount = r.ReadInt16(what);
        if (rotCount < 0 || posCount < 0)
            throw r.Fail($"{what} has a negative key count ({rotCount} rotation, {posCount} position).");

        r.EnsureCount(rotCount, RfaRotKey.Size, $"{what}'s rotation keys");
        var rot = new RfaRotKey[rotCount];
        for (int k = 0; k < rot.Length; k++)
        {
            int time = r.ReadInt32();
            short x = r.ReadInt16(), y = r.ReadInt16(), z = r.ReadInt16(), w = r.ReadInt16();
            sbyte easeIn = r.ReadSByte(), easeOut = r.ReadSByte();
            short pad = r.ReadInt16();
            rot[k] = new RfaRotKey(time, x, y, z, w, easeIn, easeOut, pad);
        }

        r.EnsureCount(posCount, RfaPosKey.Size, $"{what}'s position keys");
        var pos = new RfaPosKey[posCount];
        for (int k = 0; k < pos.Length; k++)
        {
            int time = r.ReadInt32();
            Vector3 p = r.ReadVector3(), tin = r.ReadVector3(), tout = r.ReadVector3();
            pos[k] = new RfaPosKey(time, p, tin, tout);
        }
        return new RfaBoneTrack(weight, [.. rot], [.. pos]);
    }

    private static RfaMorph ReadMorph(BinaryCursor r, RfaProbeResult h, int verticesOffset, int keyframesOffset)
    {
        int nmv = h.MorphVertexCount, nmk = h.MorphKeyframeCount;
        if (nmv == 0 && nmk == 0) return RfaMorph.Empty;

        var indices = ImmutableArray<short>.Empty;
        if (nmv > 0)
        {
            r.Seek(verticesOffset, "the morph vertex offset");
            r.EnsureCount(nmv, 2, "morph vertex indices");
            var list = new short[nmv];
            for (int i = 0; i < list.Length; i++) list[i] = r.ReadInt16();
            indices = [.. list];
        }

        long cells = (long)nmk * nmv;
        if (h.Version >= 8)
        {
            var times = ImmutableArray<int>.Empty;
            RfaMorphBounds? bounds = null;
            var quantized = ImmutableArray<byte>.Empty;
            if (nmk > 0)
            {
                r.Seek(keyframesOffset, "the morph keyframe offset");
                r.EnsureCount(nmk, 4, "morph keyframe times");
                var t = new int[nmk];
                for (int i = 0; i < t.Length; i++) t[i] = r.ReadInt32();
                times = [.. t];
                if (cells > 0)
                {
                    var min = r.ReadVector3("morph bounds");
                    var max = r.ReadVector3("morph bounds");
                    bounds = new RfaMorphBounds(min, max);
                    r.EnsureCount(cells, 3, "morph positions");
                    quantized = [.. r.ReadBytes((int)(cells * 3))];
                }
            }
            return new RfaMorph(indices, nmk, times, bounds, quantized, []);
        }

        var positions = ImmutableArray<Vector3>.Empty;
        if (cells > 0)
        {
            r.Seek(keyframesOffset, "the morph keyframe offset");
            r.EnsureCount(cells, 12, "morph positions");
            var p = new Vector3[cells];
            for (long i = 0; i < cells; i++) p[i] = r.ReadVector3();
            positions = [.. p];
        }
        return new RfaMorph(indices, nmk, [], null, [], positions);
    }
}
