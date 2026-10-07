namespace Cairn.Rfa.Formats.Rfa;

/// <summary>
/// Writes .rfa clips in the canonical layout every stock file uses: header, morph offsets, bone
/// offset table, bones contiguous in index order, morph vertex indices, zero padding to a multiple
/// of 4, then the morph keyframes. A clip read from a stock file comes back byte for byte.
/// </summary>
public static class RfaWriter
{
    /// <summary>Serialises a clip.</summary>
    /// <exception cref="ArgumentException">The clip is internally inconsistent (see <see cref="Validate"/>).</exception>
    public static byte[] Write(RfaClip clip)
    {
        Validate(clip);
        var morph = clip.Morph;
        int nmv = morph.VertexCount;
        int nmk = morph.KeyframeCount;

        int size = RfaClip.BoneTableOffset + 4 * clip.BoneCount;
        foreach (var bone in clip.Bones) size += bone.ByteSize;
        var w = new BinaryBuilder(size + 64 + morph.QuantizedPositions.Length + morph.Positions.Length * 12);

        w.WriteUInt32(RfaClip.Signature);
        w.WriteInt32(clip.Version);
        w.WriteSingle(clip.PosReduction);
        w.WriteSingle(clip.RotReduction);
        w.WriteInt32(clip.StartTime);
        w.WriteInt32(clip.EndTime);
        w.WriteInt32(clip.BoneCount);
        w.WriteInt32(nmv);
        w.WriteInt32(nmk);
        w.WriteInt32(clip.RampIn);
        w.WriteInt32(clip.RampOut);
        w.WriteQuaternion(clip.TotalRotation);
        w.WriteVector3(clip.TotalTranslation);

        int table = w.Length;
        w.WriteZeros(8 + 4 * clip.BoneCount);
        for (int i = 0; i < clip.BoneCount; i++)
        {
            w.PatchInt32(table + 8 + 4 * i, w.Length);
            var bone = clip.Bones[i];
            w.WriteSingle(bone.Weight);
            w.WriteInt16((short)bone.RotationKeys.Length);
            w.WriteInt16((short)bone.PositionKeys.Length);
            foreach (var k in bone.RotationKeys)
            {
                w.WriteInt32(k.Time);
                w.WriteInt16(k.X);
                w.WriteInt16(k.Y);
                w.WriteInt16(k.Z);
                w.WriteInt16(k.W);
                w.WriteSByte(k.EaseIn);
                w.WriteSByte(k.EaseOut);
                w.WriteInt16(k.Pad);
            }
            foreach (var k in bone.PositionKeys)
            {
                w.WriteInt32(k.Time);
                w.WriteVector3(k.Position);
                w.WriteVector3(k.InControl);
                w.WriteVector3(k.OutControl);
            }
        }

        w.PatchInt32(table, w.Length);
        foreach (short index in morph.VertexIndices) w.WriteInt16(index);
        w.Align(4);
        w.PatchInt32(table + 4, w.Length);

        if (clip.Version >= 8)
        {
            foreach (int time in morph.KeyframeTimes) w.WriteInt32(time);
            if (morph.Bounds is { } box)
            {
                w.WriteVector3(box.Min);
                w.WriteVector3(box.Max);
            }
            w.WriteBytes(morph.QuantizedPositions.AsSpan());
        }
        else
        {
            foreach (var p in morph.Positions) w.WriteVector3(p);
        }
        return w.ToArray();
    }

    /// <summary>Writes a clip to a stream.</summary>
    public static void Write(RfaClip clip, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(Write(clip));
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when the clip cannot be written as stored: a version
    /// other than 7 or 8, a bone with more keys than an int16 count holds, or morph arrays whose
    /// lengths do not match the counts and version (v8: one time per keyframe, bounds exactly when
    /// there are vertices and keyframes, 3 bytes per cell; v7: one float position per cell, no times).
    /// </summary>
    public static void Validate(RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Version is not (7 or 8))
            throw new ArgumentException($"RFA version {clip.Version} cannot be written; use 7 or 8.", nameof(clip));
        if (clip.Bones.IsDefault) throw new ArgumentException("The clip has no bone array.", nameof(clip));
        for (int i = 0; i < clip.BoneCount; i++)
        {
            var bone = clip.Bones[i] ?? throw new ArgumentException($"Bone {i} is missing.", nameof(clip));
            if (bone.RotationKeys.IsDefault || bone.PositionKeys.IsDefault)
                throw new ArgumentException($"Bone {i} has no key arrays.", nameof(clip));
            if (bone.RotationKeys.Length > short.MaxValue || bone.PositionKeys.Length > short.MaxValue)
                throw new ArgumentException($"Bone {i} has more than {short.MaxValue} keys of one kind.", nameof(clip));
        }

        var m = clip.Morph ?? throw new ArgumentException("Morph data is missing; use RfaMorph.Empty.", nameof(clip));
        if (m.VertexIndices.IsDefault || m.KeyframeTimes.IsDefault || m.QuantizedPositions.IsDefault || m.Positions.IsDefault)
            throw new ArgumentException("Morph data has an uninitialised array; use empty arrays.", nameof(clip));
        if (m.KeyframeCount < 0) throw new ArgumentException("The morph keyframe count is negative.", nameof(clip));
        long cells = (long)m.KeyframeCount * m.VertexCount;
        if (clip.Version >= 8)
        {
            if (m.KeyframeTimes.Length != m.KeyframeCount)
                throw new ArgumentException(
                    $"Version 8 morph data needs {m.KeyframeCount} keyframe times but has {m.KeyframeTimes.Length}.", nameof(clip));
            if ((cells > 0) != m.Bounds.HasValue)
                throw new ArgumentException("Version 8 morph data has bounds exactly when it has vertices and keyframes.", nameof(clip));
            if (m.QuantizedPositions.Length != cells * 3)
                throw new ArgumentException(
                    $"Version 8 morph data needs {cells * 3} position bytes but has {m.QuantizedPositions.Length}.", nameof(clip));
            if (m.Positions.Length != 0)
                throw new ArgumentException("Version 8 morph data stores quantised positions, not floats.", nameof(clip));
        }
        else
        {
            if (m.KeyframeTimes.Length != 0 || m.Bounds.HasValue || m.QuantizedPositions.Length != 0)
                throw new ArgumentException("Version 7 morph data has no keyframe times, bounds or quantised positions.", nameof(clip));
            if (m.Positions.Length != cells)
                throw new ArgumentException(
                    $"Version 7 morph data needs {cells} positions but has {m.Positions.Length}.", nameof(clip));
        }
    }
}
