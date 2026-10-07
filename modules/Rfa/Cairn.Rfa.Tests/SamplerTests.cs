using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

public class SamplerTests
{
    // The engine stores the slerp result back to int16 (0x0051A000); the Python reference keeps it in
    // floats. One int16 step is 1/16383 per component, so this is a few steps of slack, well under
    // 0.01 degrees.
    private const float RotationTolerance = 3e-4f;
    private const float PositionTolerance = 2e-5f;

    private static RfaRotKey Key(int time, Quaternion active, sbyte easeIn = 0, sbyte easeOut = 0) =>
        RfaRotKey.Quantize(time, Quat.Conj(active), easeIn, easeOut);

    private static void AssertSameRotation(Quaternion expected, Quaternion actual, float tolerance)
    {
        var a = Quat.Align(actual, expected);
        Assert.InRange(MathF.Abs(a.X - expected.X), 0f, tolerance);
        Assert.InRange(MathF.Abs(a.Y - expected.Y), 0f, tolerance);
        Assert.InRange(MathF.Abs(a.Z - expected.Z), 0f, tolerance);
        Assert.InRange(MathF.Abs(a.W - expected.W), 0f, tolerance);
    }

    // ── Python reference ─────────────────────────────────────────────────────

    [Fact]
    public void SamplerMatchesThePythonReferenceOnStockClips()
    {
        if (TestPaths.Corpus is null) return;
        var clips = new Dictionary<string, RfaClip>(StringComparer.OrdinalIgnoreCase);
        int eased = 0;
        foreach (var s in ReferenceSamples.All)
        {
            if (!clips.TryGetValue(s.Clip, out var clip))
            {
                string? path = TestPaths.CorpusFile(s.Clip);
                Assert.NotNull(path);
                clips[s.Clip] = clip = RfaReader.ReadFile(path!);
            }
            var track = clip.Bones[s.Bone];
            var rot = ClipSampler.SampleRotation(track.RotationKeys.AsSpan(), s.Time);
            var pos = ClipSampler.SamplePosition(track.PositionKeys.AsSpan(), s.Time);
            AssertSameRotation(new Quaternion(s.Qx, s.Qy, s.Qz, s.Qw), rot, RotationTolerance);
            Assert.InRange(Vector3.Distance(new Vector3(s.Px, s.Py, s.Pz), pos), 0f, PositionTolerance);

            var keys = track.RotationKeys;
            for (int i = 1; i < keys.Length; i++)
            {
                if (keys[i - 1].Time <= s.Time && s.Time <= keys[i].Time && (keys[i - 1].EaseOut != 0 || keys[i].EaseIn != 0))
                {
                    eased++;
                    break;
                }
            }
        }
        // The reference set deliberately samples inside eased segments; make sure it still does.
        Assert.True(eased >= 20, $"only {eased} samples fall in eased segments");
    }

    // ── Ease ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EaseIsTheIdentityWithoutEasesAndAtTheEndpoints()
    {
        foreach (float u in new[] { 0f, 0.1f, 0.5f, 0.9f, 1f })
            Assert.Equal(u, ClipSampler.Ease(u, 0f, 0f));
        Assert.Equal(0f, ClipSampler.Ease(0f, 0.5f, 0.5f));
        Assert.Equal(1f, ClipSampler.Ease(1f, 0.5f, 0.5f));
    }

    [Fact]
    public void EaseFollowsTheEngineCurve()
    {
        // a = 0.5, b = 0: k = 1 / 1.5; u < a -> k / a * u^2; else -> k * (2u - a).
        float k = 1f / 1.5f;
        Assert.Equal(k / 0.5f * 0.25f * 0.25f, ClipSampler.Ease(0.25f, 0.5f, 0f), 6);
        Assert.Equal(k * (2f * 0.75f - 0.5f), ClipSampler.Ease(0.75f, 0.5f, 0f), 6);
        // b only: the tail -> 1 - k / b * (1 - u)^2.
        Assert.Equal(1f - k / 0.5f * 0.1f * 0.1f, ClipSampler.Ease(0.9f, 0f, 0.5f), 6);
        // Eases summing past 1 are scaled to sum to 1, and the curve stays continuous and monotonic.
        float prev = 0f;
        for (int i = 1; i <= 100; i++)
        {
            float v = ClipSampler.Ease(i / 100f, 1f, 1f);
            Assert.True(v >= prev - 1e-6f);
            prev = v;
        }
        Assert.Equal(0.5f, ClipSampler.Ease(0.5f, 1f, 1f), 5);
    }

    // ── Rotation edge cases ──────────────────────────────────────────────────

    [Fact]
    public void ZeroRotationKeysGiveTheIdentityAndOneKeyIsConstant()
    {
        Assert.Equal(Quaternion.Identity, ClipSampler.SampleRotation([], 500f));
        var q = Quat.FromAxisAngle(Vector3.UnitY, 0.7f);
        RfaRotKey[] one = [Key(160, q)];
        AssertSameRotation(q, ClipSampler.SampleRotation(one, 0f), 1e-4f);
        AssertSameRotation(q, ClipSampler.SampleRotation(one, 99999f), 1e-4f);
    }

    [Fact]
    public void RotationClampsBeforeTheFirstAndAfterTheLastKey()
    {
        var a = Quat.FromAxisAngle(Vector3.UnitX, 0.3f);
        var b = Quat.FromAxisAngle(Vector3.UnitX, 1.3f);
        RfaRotKey[] keys = [Key(160, a), Key(480, b)];
        AssertSameRotation(a, ClipSampler.SampleRotation(keys, -1000f), 1e-4f);
        AssertSameRotation(a, ClipSampler.SampleRotation(keys, 160f), 1e-4f);
        AssertSameRotation(b, ClipSampler.SampleRotation(keys, 480f), 1e-4f);
        AssertSameRotation(b, ClipSampler.SampleRotation(keys, 1e6f), 1e-4f);
        // Halfway, linear: half the angle.
        AssertSameRotation(Quat.FromAxisAngle(Vector3.UnitX, 0.8f), ClipSampler.SampleRotation(keys, 320f), 3e-4f);
    }

    [Fact]
    public void KeysAreStoredConjugatedAndSampledInTheActiveConvention()
    {
        var active = Quat.FromAxisAngle(Vector3.UnitZ, 0.5f);
        var key = Key(0, active);
        Assert.True(key.Z < 0, "the file stores the conjugate");
        AssertSameRotation(active, ClipSampler.KeyRotation(key), 1e-4f);
    }

    [Fact]
    public void SlerpTakesTheShortArcWhenTheStoredSignsDisagree()
    {
        var a = Quat.FromAxisAngle(Vector3.UnitY, 0.2f);
        var b = Quat.FromAxisAngle(Vector3.UnitY, 0.6f);
        var flipped = Quat.Negate(Quat.Conj(b));
        RfaRotKey[] keys = [Key(0, a), RfaRotKey.Quantize(100, flipped)];
        AssertSameRotation(Quat.FromAxisAngle(Vector3.UnitY, 0.4f), ClipSampler.SampleRotation(keys, 50f), 3e-4f);
    }

    [Fact]
    public void ASlerpResultWithZeroWIsStoredAsOne()
    {
        // 180 degrees about X at both ends: w is 0 throughout, and the engine forces a stored 0 to 1.
        var k0 = new RfaRotKey(0, 16383, 0, 0, 0);
        var k1 = new RfaRotKey(100, 16383, 0, 0, 0);
        var (x, _, _, w) = ClipSampler.SlerpShort(k0, k1, 0.5f);
        Assert.Equal(1, w);
        Assert.InRange((int)x, 16382, 16383);
    }

    [Fact]
    public void KeysLessThanAboutPointOneSixDegreesApartSnapToTheLaterKey()
    {
        // 1 - dot <= 1e-6: the engine's slerp weights are 0 and 1 over the whole segment.
        var k0 = new RfaRotKey(0, 0, 0, 0, 16383);
        var k1 = new RfaRotKey(1000, 20, 0, 0, 16383);       // about 0.14 degrees
        for (float t = 0f; t <= 1f; t += 0.25f) Assert.Equal((k1.X, k1.Y, k1.Z, k1.W), ClipSampler.SlerpShort(k0, k1, t));
        // Clamped before the first key the sample still goes through the slerp, so it is k1 too.
        RfaRotKey[] keys = [k0, k1];
        Assert.Equal(ClipSampler.KeyRotation(k1), ClipSampler.SampleRotation(keys, -50f));
        // Further apart, a real slerp that reproduces the keys exactly at its ends.
        var k2 = new RfaRotKey(1000, 2000, 0, 0, 16261);
        Assert.Equal((k0.X, k0.Y, k0.Z, k0.W), ClipSampler.SlerpShort(k0, k2, 0f));
        Assert.Equal((k2.X, k2.Y, k2.Z, k2.W), ClipSampler.SlerpShort(k0, k2, 1f));
        var mid = ClipSampler.SlerpShort(k0, k2, 0.5f);
        Assert.InRange(mid.X, 1000, 1010);
    }

    [Fact]
    public void TheSlerpFlipsTheSecondKeyWhenTheSumIsNotLongerThanTheDifference()
    {
        // Orthogonal keys (dot exactly 0): the engine negates the second (<=), then interpolates.
        var k0 = new RfaRotKey(0, 0, 0, 0, 16383);
        var k1 = new RfaRotKey(100, 16383, 0, 0, 0);
        var (x, _, _, w) = ClipSampler.SlerpShort(k0, k1, 0.5f);
        Assert.True(x < 0 && w > 0, "the second key is negated before interpolating");
        Assert.Equal((-16383, 0, 0, 1), ((int)ClipSampler.SlerpShort(k0, k1, 1f).X, 0, 0, (int)ClipSampler.SlerpShort(k0, k1, 1f).W));
    }

    [Fact]
    public void TheSlerpParameterWrapsIntoZeroToOne()
    {
        var k0 = new RfaRotKey(0, 0, 0, 0, 16383);
        var k1 = new RfaRotKey(100, 4000, 0, 0, 15887);
        Assert.Equal(ClipSampler.SlerpShort(k0, k1, 0.25f), ClipSampler.SlerpShort(k0, k1, 1.25f));
        Assert.Equal(ClipSampler.SlerpShort(k0, k1, 0.75f), ClipSampler.SlerpShort(k0, k1, -0.25f));
    }

    [Fact]
    public void EasesShapeTheRotationSegment()
    {
        var a = Quat.FromAxisAngle(Vector3.UnitX, 0f);
        var b = Quat.FromAxisAngle(Vector3.UnitX, 1f);
        RfaRotKey[] linear = [Key(0, a), Key(1000, b)];
        RfaRotKey[] eased = [Key(0, a, easeOut: 127), Key(1000, b, easeIn: 127)];
        float angleLinear = 2f * MathF.Acos(ClipSampler.SampleRotation(linear, 200f).W);
        float angleEased = 2f * MathF.Acos(ClipSampler.SampleRotation(eased, 200f).W);
        Assert.True(angleEased < angleLinear, "an ease-out start moves slower at first");
        float u = ClipSampler.Ease(0.2f, 1f, 1f);
        Assert.Equal(u, angleEased, 2);
    }

    [Fact]
    public void NonIncreasingKeyTimesDoNotDivideByZero()
    {
        var q = Quat.FromAxisAngle(Vector3.UnitY, 1f);
        RfaRotKey[] keys = [Key(0, Quaternion.Identity), Key(100, q), Key(100, Quaternion.Identity), Key(50, q), Key(200, q)];
        var r = ClipSampler.SampleRotation(keys, 120f);
        Assert.True(float.IsFinite(r.X) && float.IsFinite(r.W));
        RfaPosKey[] pos = [RfaPosKey.Constant(0, Vector3.Zero), RfaPosKey.Constant(100, Vector3.One), RfaPosKey.Constant(100, Vector3.UnitX), RfaPosKey.Constant(200, Vector3.One)];
        Assert.True(float.IsFinite(ClipSampler.SamplePosition(pos, 150f).X));
    }

    // ── Position edge cases ──────────────────────────────────────────────────

    [Fact]
    public void ZeroPositionKeysGiveTheOrigin()
    {
        Assert.Equal(Vector3.Zero, ClipSampler.SamplePosition([], 300f));
    }

    [Fact]
    public void PositionsAreACubicBezierThroughAbsoluteControlPoints()
    {
        var p0 = new Vector3(0, 0, 0);
        var p1 = new Vector3(3, 0, 0);
        RfaPosKey[] keys =
        [
            new(0, p0, p0, new Vector3(1, 2, 0)),
            new(300, p1, new Vector3(2, 2, 0), p1),
        ];
        Assert.Equal(p0, ClipSampler.SamplePosition(keys, -5f));
        Assert.Equal(p1, ClipSampler.SamplePosition(keys, 400f));
        // u = 0.5: (p0 + 3 c0 + 3 c1 + p1) / 8.
        var mid = ClipSampler.SamplePosition(keys, 150f);
        var expected = (p0 + 3 * new Vector3(1, 2, 0) + 3 * new Vector3(2, 2, 0) + p1) / 8f;
        Assert.True(Vector3.Distance(expected, mid) < 1e-5f);
        // A constant track stays put.
        RfaPosKey[] constant = [RfaPosKey.Constant(0, p1), RfaPosKey.Constant(100, p1)];
        Assert.True(Vector3.Distance(p1, ClipSampler.SamplePosition(constant, 37f)) < 1e-6f);
    }

    [Fact]
    public void SampleLocalsFillsMissingBonesFromTheFallback()
    {
        var clip = new RfaClip
        {
            Bones = [new RfaBoneTrack(10, [Key(160, Quaternion.Identity)], [RfaPosKey.Constant(160, Vector3.UnitY)])],
        };
        var locals = new Rigid[3];
        var fallback = new[] { Rigid.Identity, new Rigid(Quaternion.Identity, Vector3.UnitX), new Rigid(Quaternion.Identity, Vector3.UnitZ) };
        ClipSampler.SampleLocals(clip, 160f, locals, fallback);
        Assert.Equal(Vector3.UnitY, locals[0].Position);
        Assert.Equal(Vector3.UnitX, locals[1].Position);
        Assert.Equal(Vector3.UnitZ, locals[2].Position);
    }

    [Fact]
    public void QuantizeRoundsAndClampsToInt16()
    {
        var key = RfaRotKey.Quantize(5, new Quaternion(0.5f, -0.5f, 3f, -3f));
        Assert.Equal(8192, key.X);
        Assert.Equal(-8192, key.Y);
        Assert.Equal(short.MaxValue, key.Z);
        Assert.Equal(short.MinValue, key.W);
        Assert.Equal(5, key.Time);
    }
}
