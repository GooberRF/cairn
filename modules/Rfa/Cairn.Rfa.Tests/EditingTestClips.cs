using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

/// <summary>Synthetic clips for the editing tests (no stock data), and the structural / motion checks they share.</summary>
internal static class EditingTestClips
{
    public const int Start = RfaClip.TicksPerFrame;
    public const int End = Start + 32 * RfaClip.TicksPerFrame; // 5280

    /// <summary>Rotation key times of every bone (eased keys at indices 0, 1, 2, 4).</summary>
    public static readonly int[] RotTimes = [Start, Start + 640, Start + 1600, Start + 2560, Start + 3840, End];

    /// <summary>Position key times of every bone (all on the 160-tick frame grid).</summary>
    public static readonly int[] PosTimes = [Start, Start + 960, Start + 2400, Start + 4000, End];

    /// <summary>
    /// A clip of <paramref name="bones"/> bones, each with six rotation keys (about 23 degrees apart,
    /// some with ease-in / ease-out) and five position keys with genuine (non-constant) Bezier control
    /// points, weights 2..5, ramps 320/480.
    /// </summary>
    public static RfaClip Make(int bones = 4, int version = 8)
    {
        var tracks = ImmutableArray.CreateBuilder<RfaBoneTrack>();
        for (int b = 0; b < bones; b++)
        {
            var rot = new List<RfaRotKey>();
            for (int i = 0; i < RotTimes.Length; i++)
            {
                var axis = Vector3.Normalize(new Vector3(1 + b, 0.5f * i, 1 - 0.3f * b));
                var q = Quat.FromAxisAngle(axis, 0.4f * i + 0.2f * b);
                sbyte easeIn = (sbyte)(i % 3 == 1 ? 40 : 0), easeOut = (sbyte)(i % 2 == 0 ? 30 : 0);
                rot.Add(ClipEdit.QuantizeRotation(RotTimes[i], q, rot.Count > 0 ? rot[^1] : null, easeIn, easeOut));
            }
            var pos = new List<RfaPosKey>();
            for (int i = 0; i < PosTimes.Length; i++)
            {
                var p = new Vector3(0.1f * b + 0.05f * MathF.Sin(i), 0.2f + 0.03f * i, -0.04f * i + 0.01f * b);
                var d = new Vector3(0.02f, 0.01f * (b + 1), -0.015f);
                pos.Add(new RfaPosKey(PosTimes[i], p, i == 0 ? p : p - d, i == PosTimes.Length - 1 ? p : p + d));
            }
            tracks.Add(new RfaBoneTrack(2f + b % 4, [.. rot], [.. pos]));
        }
        return new RfaClip
        {
            Version = version,
            StartTime = Start,
            EndTime = End,
            RampIn = 320,
            RampOut = 480,
            Bones = tracks.ToImmutable(),
        };
    }

    /// <summary>A small v7 morph (3 vertices, <paramref name="keyframes"/> keyframes) for <see cref="Make"/>'s range.</summary>
    public static RfaMorph MakeV7Morph(int keyframes = 5)
    {
        var positions = new List<Vector3>();
        for (int k = 0; k < keyframes; k++)
        {
            for (int v = 0; v < 3; v++) positions.Add(new Vector3(0.1f * v + 0.01f * k, 1.5f - 0.02f * k * v, 0.05f * MathF.Cos(k + v)));
        }
        return new RfaMorph([3, 7, 11], keyframes, [], null, [], [.. positions]);
    }

    /// <summary>The structural rules the linter will enforce, as test assertions.</summary>
    public static void AssertStructural(RfaClip clip)
    {
        Assert.True(clip.EndTime >= clip.StartTime, "end before start");
        RfaWriter.Validate(clip);
        for (int b = 0; b < clip.BoneCount; b++)
        {
            var t = clip.Bones[b];
            Assert.True(t.RotationKeys.Length >= 1, $"bone {b} has no rotation key");
            Assert.True(t.PositionKeys.Length >= 2, $"bone {b} has {t.PositionKeys.Length} position keys");
            for (int i = 0; i < t.RotationKeys.Length; i++)
            {
                var k = t.RotationKeys[i];
                Assert.InRange(k.Time, clip.StartTime, clip.EndTime);
                Assert.InRange(k.FileQuaternion.Length(), 0.998f, 1.002f);
                if (i > 0)
                {
                    var p = t.RotationKeys[i - 1];
                    Assert.True(k.Time > p.Time, $"bone {b} rotation keys {i - 1} and {i} are not in increasing time order");
                    long dot = (long)k.X * p.X + (long)k.Y * p.Y + (long)k.Z * p.Z + (long)k.W * p.W;
                    Assert.True(dot >= 0, $"bone {b} rotation key {i} is not sign-continuous");
                }
            }
            for (int i = 0; i < t.PositionKeys.Length; i++)
            {
                Assert.InRange(t.PositionKeys[i].Time, clip.StartTime, clip.EndTime);
                if (i > 0) Assert.True(t.PositionKeys[i].Time > t.PositionKeys[i - 1].Time, $"bone {b} position keys out of order at {i}");
            }
        }
    }

    /// <summary>
    /// Asserts that two clips sample to the same local motion on every bone at every
    /// <paramref name="step"/> ticks in [<paramref name="from"/>, <paramref name="to"/>]; <paramref name="map"/>
    /// maps a time of <paramref name="expected"/> to the time to sample <paramref name="actual"/> at.
    /// </summary>
    public static void AssertSameMotion(
        RfaClip expected, RfaClip actual, int from, int to, int step = 8, float rotDegrees = 0.2f, float pos = 1e-4f, Func<double, double>? map = null)
    {
        Assert.Equal(expected.BoneCount, actual.BoneCount);
        var (r, p) = MaxDifference(expected, actual, from, to, step, map);
        Assert.True(r <= rotDegrees, $"rotation differs by {r} degrees (limit {rotDegrees})");
        Assert.True(p <= pos, $"position differs by {p} m (limit {pos})");
    }

    /// <summary>The largest rotation (degrees) and position (m) difference, sampled independently of ClipEdit.</summary>
    public static (float Rot, float Pos) MaxDifference(RfaClip expected, RfaClip actual, int from, int to, int step, Func<double, double>? map = null)
    {
        float maxR = 0f, maxP = 0f;
        for (int b = 0; b < expected.BoneCount; b++)
        {
            var e = expected.Bones[b];
            var a = actual.Bones[b];
            for (long t = from; t <= to; t += step)
            {
                float ta = (float)(map?.Invoke(t) ?? t);
                var qe = ClipSampler.SampleRotation(e.RotationKeys.AsSpan(), t);
                var qa = ClipSampler.SampleRotation(a.RotationKeys.AsSpan(), ta);
                maxR = MathF.Max(maxR, Angle(qe, qa));
                maxP = MathF.Max(maxP, Vector3.Distance(
                    ClipSampler.SamplePosition(e.PositionKeys.AsSpan(), t), ClipSampler.SamplePosition(a.PositionKeys.AsSpan(), ta)));
            }
        }
        return (maxR, maxP);
    }

    /// <summary>
    /// The angle between two rotations in degrees, accurate for tiny angles (chord formula in double;
    /// Quat.AngleDegrees cannot resolve below about 0.05 degrees).
    /// </summary>
    public static float Angle(Quaternion a, Quaternion b)
    {
        a = Quat.Normalize(a);
        b = Quat.Align(Quat.Normalize(b), a);
        double dx = (double)a.X - b.X, dy = (double)a.Y - b.Y, dz = (double)a.Z - b.Z, dw = (double)a.W - b.W;
        return (float)(4.0 * Math.Asin(Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw) / 2.0)) * 180.0 / Math.PI);
    }

    /// <summary>Asserts both clips have exactly the same keys (raw struct equality), weights, header and morph.</summary>
    public static void AssertIdentical(RfaClip expected, RfaClip actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.StartTime, actual.StartTime);
        Assert.Equal(expected.EndTime, actual.EndTime);
        Assert.Equal(expected.RampIn, actual.RampIn);
        Assert.Equal(expected.RampOut, actual.RampOut);
        Assert.Equal(expected.BoneCount, actual.BoneCount);
        for (int b = 0; b < expected.BoneCount; b++)
        {
            Assert.Equal(expected.Bones[b].Weight, actual.Bones[b].Weight);
            AssertKeys(expected.Bones[b].RotationKeys, actual.Bones[b].RotationKeys);
            AssertKeys(expected.Bones[b].PositionKeys, actual.Bones[b].PositionKeys);
        }
        Assert.Equal(RfaWriter.Write(expected), RfaWriter.Write(actual));
    }

    /// <summary>Asserts two key lists hold the same keys (raw struct equality), in order. (xunit compares ImmutableArray by reference.)</summary>
    public static void AssertKeys<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Assert.Equal(expected.ToArray(), actual.ToArray());

    /// <summary>Asserts two selections address the same keys.</summary>
    public static void AssertSelection(KeySelection expected, KeySelection actual) => Assert.Equal(expected.Keys.ToArray(), actual.Keys.ToArray());

    /// <summary>Every stock clip, read in place (empty when the corpus is absent).</summary>
    public static IEnumerable<(string Name, RfaClip Clip, byte[] Bytes)> StockClips() => StockClipCache.Value;

    /// <summary>
    /// The stock clips read once per test run and shared (clips are immutable; no test writes into the byte
    /// arrays): several corpus classes walk all 1009 of them in parallel.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<(string Name, RfaClip Clip, byte[] Bytes)>> StockClipCache = new(() =>
    {
        if (TestPaths.Corpus is null) return [];
        var list = new List<(string, RfaClip, byte[])>();
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus, "*.rfa").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.GetExtension(path).Equals(".rfa", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            list.Add((Path.GetFileName(path), RfaReader.Read(bytes, Path.GetFileName(path)), bytes));
        }
        return list;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>One stock clip by name, or null when the corpus is absent.</summary>
    public static RfaClip? Stock(string name) => TestPaths.CorpusFile(name) is { } path ? RfaReader.ReadFile(path) : null;
}
