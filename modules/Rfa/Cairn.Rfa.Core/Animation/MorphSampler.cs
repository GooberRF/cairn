using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Animation;

/// <summary>Where a time falls in a clip's morph keyframes: the two keyframes and the blend between them.</summary>
/// <param name="Keyframe0">The earlier keyframe.</param>
/// <param name="Keyframe1">The later keyframe (equal to <paramref name="Keyframe0"/> when no blending happens).</param>
/// <param name="Fraction">Weight of <paramref name="Keyframe1"/>, 0..1 (0 when the keyframes are equal).</param>
public readonly record struct MorphFrame(int Keyframe0, int Keyframe1, float Fraction);

/// <summary>
/// Morph (vertex) animation as RF.exe plays it (<c>Skeleton::morph_vertices</c>, 0x0053A370, and its
/// caller <c>gr_d3d_morph_character</c>, both read from the disassembly; DESIGN.md section 9):
/// <list type="bullet">
/// <item>Version 7 stores no times. With <c>f = (t - start) / (end - start) * nmk</c>, keyframe
/// <c>k0 = trunc(f)</c> blends linearly into <c>k1 = min(k0 + 1, nmk - 1)</c> by <c>f - k0</c>: keyframe k
/// sits at <c>start + k * (end - start) / nmk</c> and the last one holds until the end.</item>
/// <item>Version 8: the stored times, clamped at both ends (before the first time keyframe 0, after the
/// last the last keyframe), linear between; bytes decode as <c>min + b * ((max - min) / 255)</c>.</item>
/// <item>The positions REPLACE the morphed vertices of LOD 0 (submesh-local, absolute); each clip
/// vertex index (read as unsigned 16-bit) goes through the batch's morph map when the LOD has flag
/// 0x01 (an entry of -1 skips the vertex), otherwise it is the batch vertex itself. Same-position
/// offsets then copy each result to its duplicates (Alpine Faction's renderer; DESIGN.md section 9).</item>
/// <item>Only one clip morphs a mesh: the first playing clip whose morph vertex count is above 0, at
/// its own time. Bone weights and ramps play no part.</item>
/// </list>
/// The engine performs no bounds checks (a time exactly at <c>end</c> of a v7 clip reads one keyframe
/// past the data); this class clamps instead and documents where it does.
/// </summary>
public static class MorphSampler
{
    /// <summary>The engine's 1/255 (0x005895DC) for v8 bytes.</summary>
    private const float ByteScale = 0.0039215689f;

    /// <summary>True when the engine would morph with this clip (<c>Skeleton::has_morph_vertices</c>: vertex count above 0 and at least one keyframe).</summary>
    public static bool HasMorph(RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Morph.VertexCount > 0 && clip.Morph.KeyframeCount > 0;
    }

    /// <summary>
    /// The keyframes and blend the engine uses at <paramref name="time"/>, or null when the clip has no
    /// morph data. Times outside [start, end] are clamped into it for version 7 (the engine's instance
    /// times never leave that range; at exactly <c>end</c> the engine would read past the last keyframe,
    /// which this returns as the last keyframe instead).
    /// </summary>
    public static MorphFrame? Locate(RfaClip clip, float time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!HasMorph(clip)) return null;
        var m = clip.Morph;
        int n = m.KeyframeCount;
        if (clip.Version < 8 || m.KeyframeTimes.IsDefaultOrEmpty)
        {
            int duration = clip.EndTime - clip.StartTime;
            if (duration <= 0 || n == 1) return new MorphFrame(0, 0, 0f);
            float t = Math.Clamp(time, clip.StartTime, clip.EndTime);
            float f = (t - clip.StartTime) / duration * n;
            int k0 = Math.Min((int)f, n - 1);
            int k1 = Math.Min(k0 + 1, n - 1);
            return k0 == k1 ? new MorphFrame(k0, k0, 0f) : new MorphFrame(k0, k1, f - k0);
        }

        var times = m.KeyframeTimes;
        if (time <= times[0]) return new MorphFrame(0, 0, 0f);
        if (time >= times[n - 1]) return new MorphFrame(n - 1, n - 1, 0f);
        int i = 0;
        while (i < n && times[i] <= time) i++;
        int t0 = times[i - 1], t1 = times[i];
        float frac = t1 > t0 ? (time - t0) / (t1 - t0) : 0f;
        return new MorphFrame(i - 1, i, frac);
    }

    /// <summary>
    /// The morphed positions at <paramref name="time"/>, one per <see cref="RfaMorph.VertexIndices"/>
    /// entry, written into <paramref name="positions"/>. Returns false (and writes nothing) when the clip
    /// has no morph data. Allocation-free.
    /// </summary>
    public static bool Sample(RfaClip clip, float time, Span<Vector3> positions)
    {
        if (Locate(clip, time) is not { } frame) return false;
        var m = clip.Morph;
        int nv = m.VertexCount;
        if (positions.Length < nv) throw new ArgumentException($"Need room for {nv} positions.", nameof(positions));
        for (int v = 0; v < nv; v++)
        {
            var p0 = Decode(m, frame.Keyframe0, v);
            positions[v] = frame.Keyframe0 == frame.Keyframe1
                ? p0
                : p0 * (1f - frame.Fraction) + Decode(m, frame.Keyframe1, v) * frame.Fraction;
        }
        if (clip.Version >= 8 && m.Bounds is { } box)
        {
            // v8 blends the scaled bytes, then adds the minimum once (as the engine does).
            for (int v = 0; v < nv; v++) positions[v] += box.Min;
        }
        return true;
    }

    /// <summary>The morphed positions at <paramref name="time"/>, or an empty array when the clip has no morph data.</summary>
    public static Vector3[] Sample(RfaClip clip, float time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!HasMorph(clip)) return [];
        var result = new Vector3[clip.Morph.VertexCount];
        Sample(clip, time, result);
        return result;
    }

    /// <summary>
    /// Applies morphed positions to one batch: copies the batch's positions into
    /// <paramref name="output"/>, writes each morphed vertex (through the morph map when
    /// <paramref name="useMorphMap"/>; indices outside the map or the batch are skipped, where the
    /// engine would write out of bounds), then propagates same-position offsets (vertex i takes vertex
    /// <c>i - offset</c>'s position when <c>0 &lt; offset &lt;= i</c>, in ascending order). Allocation-free.
    /// </summary>
    /// <param name="batch">The LOD 0 batch.</param>
    /// <param name="vertexIndices">The clip's morph vertex indices.</param>
    /// <param name="morphed">One position per index (from <see cref="Sample(RfaClip, float, Span{Vector3})"/>).</param>
    /// <param name="useMorphMap">True when the LOD has <see cref="V3dLod.FlagMorphVerticesMap"/>.</param>
    /// <param name="output">Receives the batch's vertex positions.</param>
    public static void ApplyToBatch(
        V3dBatch batch, ReadOnlySpan<short> vertexIndices, ReadOnlySpan<Vector3> morphed, bool useMorphMap, Span<Vector3> output)
    {
        ArgumentNullException.ThrowIfNull(batch);
        int count = batch.VertexCount;
        if (output.Length < count) throw new ArgumentException($"Need room for {count} vertices.", nameof(output));
        batch.Positions.AsSpan().CopyTo(output);
        var map = batch.MorphMap.AsSpan();
        int n = Math.Min(vertexIndices.Length, morphed.Length);
        for (int i = 0; i < n; i++)
        {
            int index = (ushort)vertexIndices[i];
            if (useMorphMap)
            {
                if (index >= map.Length) continue;
                index = map[index];
            }
            if (index > -1 && index < count) output[index] = morphed[i];
        }

        var same = batch.SamePositionOffsets.AsSpan();
        int m = Math.Min(same.Length, count);
        for (int i = 0; i < m; i++)
        {
            int offset = same[i];
            if (offset > 0 && offset <= i) output[i] = output[i - offset];
        }
    }

    /// <summary>
    /// The morphed vertex positions of every batch of <paramref name="lod"/> (pass LOD 0: the engine
    /// morphs no other) at <paramref name="time"/>, or null when the clip has no morph data.
    /// </summary>
    public static ImmutableArray<Vector3>[]? ApplyToLod(V3dLod lod, RfaClip clip, float time)
    {
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(clip);
        if (!HasMorph(clip)) return null;
        var morphed = Sample(clip, time);
        bool useMap = (lod.Flags & V3dLod.FlagMorphVerticesMap) != 0;
        var result = new ImmutableArray<Vector3>[lod.Batches.Length];
        for (int b = 0; b < lod.Batches.Length; b++)
        {
            var batch = lod.Batches[b];
            var output = new Vector3[batch.VertexCount];
            ApplyToBatch(batch, clip.Morph.VertexIndices.AsSpan(), morphed, useMap && !batch.MorphMap.IsDefaultOrEmpty, output);
            result[b] = [.. output];
        }
        return result;
    }

    /// <summary>
    /// One keyframe's position of one vertex as the engine computes it: a v7 float, or for v8 the byte
    /// times <c>(max - min) / 255</c> WITHOUT the minimum (added once after blending).
    /// </summary>
    private static Vector3 Decode(RfaMorph m, int keyframe, int vertex)
    {
        int i = keyframe * m.VertexCount + vertex;
        if (!m.Positions.IsDefaultOrEmpty) return m.Positions[i];
        if (m.QuantizedPositions.IsDefaultOrEmpty || m.Bounds is not { } box) return Vector3.Zero;
        var scale = (box.Max - box.Min) * ByteScale;
        return new Vector3(m.QuantizedPositions[i * 3], m.QuantizedPositions[i * 3 + 1], m.QuantizedPositions[i * 3 + 2]) * scale;
    }
}
