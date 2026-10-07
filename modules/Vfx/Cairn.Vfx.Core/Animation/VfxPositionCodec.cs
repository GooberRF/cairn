using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Animation;

/// <summary>VSFX timing: 15 frames per second, 320 key ticks per frame, 4800 ticks per second.</summary>
public static class VfxTime
{
    public const int FramesPerSecond = 15;
    public const int TicksPerFrame = 320;
    public const int TicksPerSecond = FramesPerSecond * TicksPerFrame;
}

/// <summary>
/// The i16 position quantiser: decoded = center + raw * multiplier. Encoding follows REDUX
/// (round half away from zero) in single precision; the stock exporter apparently truncated, but the
/// difference is not observable in the engine. Parsed files keep their raw shorts and never re-encode.
/// </summary>
public static class VfxPositionCodec
{
    public const short MaxRaw = 32767;

    /// <summary>The floor multiplier: 0.1f / 32767f in single precision (bits 0x364CCE67).</summary>
    public static readonly float FloorMultiplier = MinHalfExtent / MaxRaw;

    private const float MinHalfExtent = 0.1f;

    public static Vector3[] Decode(VfxCompressedPositions p)
    {
        var raw = p.Raw.AsSpan();
        var result = new Vector3[raw.Length / 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Vector3(
                p.Center.X + raw[3 * i] * p.Multiplier.X,
                p.Center.Y + raw[3 * i + 1] * p.Multiplier.Y,
                p.Center.Z + raw[3 * i + 2] * p.Multiplier.Z);
        return result;
    }

    public static VfxCompressedPositions Encode(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty) return new(Vector3.Zero, new Vector3(FloorMultiplier), []);
        Vector3 min = points[0], max = points[0];
        for (int i = 0; i < points.Length; i++)
        {
            // One NaN would make the centre and multiplier NaN for the whole axis (review-findings-2 #5).
            var p = points[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                throw new ArgumentException($"Vertex position {i} is not finite ({p}).", nameof(points));
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        var center = new Vector3((min.X + max.X) / 2f, (min.Y + max.Y) / 2f, (min.Z + max.Z) / 2f);
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z) || !float.IsFinite(max.X - min.X) || !float.IsFinite(max.Y - min.Y) || !float.IsFinite(max.Z - min.Z))
            throw new ArgumentException("Vertex positions span a range too large to quantise.", nameof(points));
        Vector3 dev = Vector3.Zero;
        foreach (var p in points) dev = Vector3.Max(dev, Vector3.Abs(p - center));
        var mult = new Vector3(Mult(dev.X), Mult(dev.Y), Mult(dev.Z));
        var raw = ImmutableArray.CreateBuilder<short>(points.Length * 3);
        foreach (var p in points)
        {
            raw.Add(Quantise(p.X - center.X, mult.X));
            raw.Add(Quantise(p.Y - center.Y, mult.Y));
            raw.Add(Quantise(p.Z - center.Z, mult.Z));
        }
        return new(center, mult, raw.MoveToImmutable());
    }

    private static float Mult(float halfExtent) => MathF.Max(halfExtent, MinHalfExtent) / MaxRaw;

    private static short Quantise(float offset, float mult)
    {
        float q = MathF.Round(offset / mult, MidpointRounding.AwayFromZero);
        return (short)Math.Clamp(q, -MaxRaw, MaxRaw);
    }
}
