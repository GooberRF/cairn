using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Editing;

public static partial class ClipEdit
{
    /// <summary>
    /// The clip as version <paramref name="version"/> (7 or 8). The header and bone data are the same
    /// in both versions; only the morph (vertex animation) data is converted. Converting to the
    /// clip's own version returns it unchanged.
    /// </summary>
    /// <remarks>
    /// Engine timing (RF.exe <c>Skeleton::morph_vertices</c>): version 7 stores no times — keyframe
    /// position <c>f = (t - start) / (end - start) * count</c>, keyframes <c>trunc(f)</c> and
    /// <c>min(trunc(f) + 1, count - 1)</c> lerped by the fraction, so keyframe k sits at
    /// <c>start + k (end - start) / count</c> and the last one holds to the end. Version 8 uses its
    /// stored times (clamped at both ends, linear between) and decodes bytes as
    /// <c>min + (max - min) * b * (1/255)</c>.
    /// <list type="bullet">
    /// <item>7 -> 8: times <c>start + round(k (end - start) / count)</c>; bounds = component-wise
    /// min/max over every position; bytes <c>round((p - min) / (max - min) * 255)</c> (an axis with
    /// max = min stores 0). Error: half a quantisation step per axis.</item>
    /// <item>8 -> 7: keeps the keyframe count and samples the v8 morph (linear between its times,
    /// clamped) at the v7 keyframe times <c>start + k (end - start) / count</c>. Exact when the v8
    /// times are already evenly spaced that way; otherwise the v7 morph is a resampling.</item>
    /// <item>A v8 morph with keyframe times but no vertices (one stock file stores 61 zero times and
    /// nothing else) has nothing to play and becomes an empty morph in version 7. Vertex indices with
    /// no keyframes are kept as they are.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is not 7 or 8.</exception>
    public static RfaClip ConvertVersion(RfaClip clip, int version)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (version is not (7 or 8))
            throw new ArgumentOutOfRangeException(nameof(version), $"RFA version {version} does not exist; use 7 or 8.");
        if (clip.Version == version) return clip;
        var morph = version == 8
            ? ClipEditMorph.ToV8(clip.Morph, clip.StartTime, clip.EndTime)
            : ClipEditMorph.ToV7(clip.Morph, clip.StartTime, clip.EndTime);
        return clip with { Version = version, Morph = morph };
    }

    /// <summary>The clip without morph (vertex animation) data; bones and header untouched.</summary>
    public static RfaClip StripMorph(RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return ReferenceEquals(clip.Morph, RfaMorph.Empty) ? clip : clip with { Morph = RfaMorph.Empty };
    }

    /// <summary>
    /// Every morphed vertex's position at <paramref name="time"/> ticks, timed the way the engine
    /// plays the clip's version (see <see cref="ConvertVersion"/>). Empty when the morph has no
    /// vertices or no keyframes.
    /// </summary>
    public static ImmutableArray<Vector3> SampleMorph(RfaClip clip, double time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var m = clip.Morph;
        if (m.VertexCount == 0 || m.KeyframeCount == 0) return [];
        return clip.Version >= 8 && !m.KeyframeTimes.IsDefaultOrEmpty
            ? ClipEditMorph.SampleV8(m, time)
            : ClipEditMorph.SampleV7(m, clip.StartTime, clip.EndTime, time);
    }
}

/// <summary>Morph timing and conversion helpers for the <see cref="ClipEdit"/> operations.</summary>
internal static class ClipEditMorph
{
    /// <summary>v7 keyframe k's time: <c>start + k (end - start) / count</c>.</summary>
    public static double V7Time(int start, int end, int k, int count) => start + k * (double)(end - start) / count;

    /// <summary>A v7 morph sampled at <paramref name="time"/> (engine rule, clamped to the keyframes).</summary>
    public static ImmutableArray<Vector3> SampleV7(RfaMorph m, int start, int end, double time)
    {
        int nmk = m.KeyframeCount, nmv = m.VertexCount;
        double f = end > start ? (time - start) / (end - start) * nmk : 0;
        f = Math.Clamp(f, 0, nmk - 1);
        int k0 = (int)Math.Truncate(f);
        int k1 = Math.Min(k0 + 1, nmk - 1);
        float frac = (float)(f - k0);
        var result = new Vector3[nmv];
        for (int v = 0; v < nmv; v++)
            result[v] = Vector3.Lerp(m.GetPosition(k0, v), m.GetPosition(k1, v), k1 == k0 ? 0f : frac);
        return [.. result];
    }

    /// <summary>A v8 morph sampled at <paramref name="time"/>: clamped to its first and last times, linear between.</summary>
    public static ImmutableArray<Vector3> SampleV8(RfaMorph m, double time)
    {
        int nmk = m.KeyframeCount, nmv = m.VertexCount;
        var times = m.KeyframeTimes;
        int k0, k1;
        float frac = 0f;
        if (time <= times[0]) k0 = k1 = 0;
        else if (time >= times[nmk - 1]) k0 = k1 = nmk - 1;
        else
        {
            k0 = 0;
            while (k0 + 1 < nmk && times[k0 + 1] <= time) k0++;
            k1 = Math.Min(k0 + 1, nmk - 1);
            int span = times[k1] - times[k0];
            frac = span > 0 ? (float)((time - times[k0]) / span) : 1f;
        }
        var result = new Vector3[nmv];
        for (int v = 0; v < nmv; v++)
            result[v] = Vector3.Lerp(Decode(m, k0, v), Decode(m, k1, v), frac);
        return [.. result];
    }

    /// <summary>A v8 byte triple decoded exactly as the engine does (<c>min + (max - min) * b * (1/255)</c>).</summary>
    public static Vector3 Decode(RfaMorph m, int keyframe, int vertex)
    {
        if (m.Bounds is not { } box || m.QuantizedPositions.IsDefaultOrEmpty) return m.GetPosition(keyframe, vertex);
        int i = (keyframe * m.VertexCount + vertex) * 3;
        const float inv = 1f / 255f;
        var d = box.Max - box.Min;
        return new Vector3(
            box.Min.X + d.X * (m.QuantizedPositions[i] * inv),
            box.Min.Y + d.Y * (m.QuantizedPositions[i + 1] * inv),
            box.Min.Z + d.Z * (m.QuantizedPositions[i + 2] * inv));
    }

    /// <summary>Version 7 morph -> version 8 (see <see cref="ClipEdit.ConvertVersion"/>).</summary>
    public static RfaMorph ToV8(RfaMorph m, int start, int end)
    {
        if (m.IsEmpty) return RfaMorph.Empty;
        int nmk = m.KeyframeCount, nmv = m.VertexCount;
        var times = new int[nmk];
        for (int k = 0; k < nmk; k++) times[k] = (int)Math.Round(V7Time(start, end, k, nmk), MidpointRounding.AwayFromZero);
        if (nmk == 0 || nmv == 0) return new RfaMorph(m.VertexIndices, nmk, [.. times], null, [], []);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in m.Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var bytes = new byte[nmk * nmv * 3];
        for (int i = 0; i < m.Positions.Length; i++)
        {
            var p = m.Positions[i];
            bytes[i * 3] = Q(p.X, min.X, max.X);
            bytes[i * 3 + 1] = Q(p.Y, min.Y, max.Y);
            bytes[i * 3 + 2] = Q(p.Z, min.Z, max.Z);
        }
        return new RfaMorph(m.VertexIndices, nmk, [.. times], new RfaMorphBounds(min, max), [.. bytes], []);

        static byte Q(float v, float lo, float hi) =>
            hi > lo ? (byte)Math.Clamp(Math.Round((v - lo) / (double)(hi - lo) * 255.0, MidpointRounding.AwayFromZero), 0, 255) : (byte)0;
    }

    /// <summary>Version 8 morph -> version 7 (see <see cref="ClipEdit.ConvertVersion"/>).</summary>
    public static RfaMorph ToV7(RfaMorph m, int start, int end)
    {
        if (m.IsEmpty || m.VertexCount == 0) return RfaMorph.Empty;
        int nmk = m.KeyframeCount, nmv = m.VertexCount;
        if (nmk == 0) return new RfaMorph(m.VertexIndices, 0, [], null, [], []);
        var positions = new Vector3[nmk * nmv];
        for (int k = 0; k < nmk; k++)
        {
            var frame = SampleV8(m, V7Time(start, end, k, nmk));
            frame.CopyTo(positions, k * nmv);
        }
        return new RfaMorph(m.VertexIndices, nmk, [], null, [], [.. positions]);
    }

    /// <summary>A v7 morph re-spread from [oldStart, oldEnd] to [newStart, newEnd] so it plays the same over the new range (same keyframe count).</summary>
    public static RfaMorph ResampleV7(RfaMorph m, int oldStart, int oldEnd, int newStart, int newEnd)
    {
        int nmk = m.KeyframeCount, nmv = m.VertexCount;
        if (nmk == 0 || nmv == 0 || m.Positions.IsDefaultOrEmpty) return m;
        var positions = new Vector3[nmk * nmv];
        for (int k = 0; k < nmk; k++)
        {
            var frame = SampleV7(m, oldStart, oldEnd, V7Time(newStart, newEnd, k, nmk));
            frame.CopyTo(positions, k * nmv);
        }
        return m with { Positions = [.. positions] };
    }

    /// <summary>The morph with its keyframes in reverse order; v8 times mapped through <paramref name="map"/>.</summary>
    public static RfaMorph Reverse(RfaMorph m, Func<int, int> map)
    {
        int nmk = m.KeyframeCount, nmv = m.VertexCount;
        if (nmk == 0) return m;
        var times = m.KeyframeTimes.IsDefaultOrEmpty ? m.KeyframeTimes : [.. m.KeyframeTimes.Reverse().Select(map)];
        var bytes = m.QuantizedPositions;
        if (!bytes.IsDefaultOrEmpty)
        {
            var b = new byte[bytes.Length];
            int frame = nmv * 3;
            for (int k = 0; k < nmk; k++) bytes.AsSpan(k * frame, frame).CopyTo(b.AsSpan((nmk - 1 - k) * frame));
            bytes = [.. b];
        }
        var positions = m.Positions;
        if (!positions.IsDefaultOrEmpty)
        {
            var p = new Vector3[positions.Length];
            for (int k = 0; k < nmk; k++) positions.AsSpan(k * nmv, nmv).CopyTo(p.AsSpan((nmk - 1 - k) * nmv));
            positions = [.. p];
        }
        return m with { KeyframeTimes = times, QuantizedPositions = bytes, Positions = positions };
    }
}
