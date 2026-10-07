using System.Numerics;
using Cairn.Formats;
using Cairn.Formats.Gltf;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Interchange;

/// <summary>
/// One glTF animation sampler read into memory and evaluated as the glTF spec defines: LINEAR
/// (lerp, or the shortest-arc slerp for rotations), STEP, and CUBICSPLINE (Hermite with tangents
/// scaled by the key interval; rotations normalised afterwards). Clamped at both ends.
/// </summary>
public sealed class GltfChannelSampler
{
    private GltfChannelSampler(float[] times, Vector4[] values, int width, string interpolation)
    {
        Times = times;
        Values = values;
        Width = width;
        Interpolation = interpolation;
    }

    /// <summary>Key times in seconds (strictly increasing after loading; equal times keep the later key).</summary>
    public float[] Times { get; }

    /// <summary>Output values (xyz or xyzw); CUBICSPLINE stores [in tangent, value, out tangent] per key.</summary>
    public Vector4[] Values { get; }

    /// <summary>3 for translation/scale, 4 for rotation.</summary>
    public int Width { get; }

    /// <summary>LINEAR, STEP or CUBICSPLINE.</summary>
    public string Interpolation { get; }

    /// <summary>True for CUBICSPLINE.</summary>
    public bool IsCubic => Interpolation == GltfInterpolation.CubicSpline;

    /// <summary>Number of keys.</summary>
    public int Count => Times.Length;

    /// <summary>Reads a sampler for a translation (width 3) or rotation (width 4) channel.</summary>
    /// <exception cref="AssetFormatException">The accessors are missing, of the wrong type or inconsistent.</exception>
    public static GltfChannelSampler Load(GltfDocument doc, GltfAnimationSampler sampler, int width, string fileName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(sampler);
        string interpolation = (sampler.Interpolation ?? GltfInterpolation.Linear).ToUpperInvariant();
        if (interpolation is not (GltfInterpolation.Linear or GltfInterpolation.Step or GltfInterpolation.CubicSpline))
            throw new AssetFormatException($"'{fileName}' has an animation sampler with the unknown interpolation '{sampler.Interpolation}'.");
        GltfAccessorReader.Expect(doc, sampler.Input, fileName, GltfAccessorType.Scalar);
        var times = GltfAccessorReader.ReadFloats(doc, sampler.Input, fileName);
        Vector4[] values;
        if (width == 4)
        {
            GltfAccessorReader.Expect(doc, sampler.Output, fileName, GltfAccessorType.Vec4);
            values = GltfAccessorReader.ReadVector4(doc, sampler.Output, fileName);
        }
        else
        {
            GltfAccessorReader.Expect(doc, sampler.Output, fileName, GltfAccessorType.Vec3);
            values = [.. GltfAccessorReader.ReadVector3(doc, sampler.Output, fileName).Select(v => new Vector4(v, 0))];
        }
        int per = interpolation == GltfInterpolation.CubicSpline ? 3 : 1;
        if (values.Length < times.Length * per)
            throw new AssetFormatException($"'{fileName}' has an animation sampler with {times.Length} times but only {values.Length} values ({per} per key needed).");
        foreach (float t in times)
        {
            if (!float.IsFinite(t)) throw new AssetFormatException($"'{fileName}' has an animation time that is not a finite number.");
        }

        // Keep strictly increasing times (a repeated time keeps its later key).
        var keepT = new List<float>(times.Length);
        var keepV = new List<Vector4>(values.Length);
        for (int i = 0; i < times.Length; i++)
        {
            if (keepT.Count > 0 && times[i] <= keepT[^1])
            {
                if (times[i] < keepT[^1]) continue; // out of order: dropped
                keepT.RemoveAt(keepT.Count - 1);
                keepV.RemoveRange(keepV.Count - per, per);
            }
            keepT.Add(times[i]);
            for (int k = 0; k < per; k++) keepV.Add(values[i * per + k]);
        }
        return new GltfChannelSampler([.. keepT], [.. keepV], width, interpolation);
    }

    /// <summary>The key value (not a tangent) of key <paramref name="i"/>.</summary>
    public Vector4 KeyValue(int i) => IsCubic ? Values[i * 3 + 1] : Values[i];

    /// <summary>The in-tangent (CUBICSPLINE only, per second).</summary>
    public Vector4 InTangent(int i) => IsCubic ? Values[i * 3] : Vector4.Zero;

    /// <summary>The out-tangent (CUBICSPLINE only, per second).</summary>
    public Vector4 OutTangent(int i) => IsCubic ? Values[i * 3 + 2] : Vector4.Zero;

    /// <summary>The value at <paramref name="time"/> seconds.</summary>
    public Vector4 Sample(double time)
    {
        int n = Times.Length;
        if (n == 0) return Width == 4 ? new Vector4(0, 0, 0, 1) : Vector4.Zero;
        if (time <= Times[0] || n == 1) return Normal(KeyValue(0));
        if (time >= Times[n - 1]) return Normal(KeyValue(n - 1));
        int i = Array.BinarySearch(Times, (float)time);
        if (i >= 0) return Normal(KeyValue(i));
        int k1 = ~i, k0 = k1 - 1;
        double dt = Times[k1] - Times[k0];
        float u = (float)((time - Times[k0]) / dt);
        switch (Interpolation)
        {
            case GltfInterpolation.Step:
                return Normal(KeyValue(k0));
            case GltfInterpolation.CubicSpline:
            {
                float u2 = u * u, u3 = u2 * u;
                var p0 = KeyValue(k0);
                var p1 = KeyValue(k1);
                var m0 = OutTangent(k0) * (float)dt;
                var m1 = InTangent(k1) * (float)dt;
                var v = (2 * u3 - 3 * u2 + 1) * p0 + (u3 - 2 * u2 + u) * m0 + (-2 * u3 + 3 * u2) * p1 + (u3 - u2) * m1;
                return Normal(v);
            }
            default:
                if (Width == 4)
                {
                    var a = KeyValue(k0);
                    var b = KeyValue(k1);
                    var q = Quat.Slerp(new Quaternion(a.X, a.Y, a.Z, a.W), new Quaternion(b.X, b.Y, b.Z, b.W), u);
                    return new Vector4(q.X, q.Y, q.Z, q.W);
                }
                return Vector4.Lerp(KeyValue(k0), KeyValue(k1), u);
        }
    }

    /// <summary>The rotation at a time (normalised).</summary>
    public Quaternion SampleRotation(double time)
    {
        var v = Sample(time);
        return Quat.Normalize(new Quaternion(v.X, v.Y, v.Z, v.W));
    }

    /// <summary>The translation at a time.</summary>
    public Vector3 SampleVector(double time)
    {
        var v = Sample(time);
        return new Vector3(v.X, v.Y, v.Z);
    }

    private Vector4 Normal(Vector4 v)
    {
        if (Width != 4) return v;
        float len = v.Length();
        return len > 0 && float.IsFinite(len) ? v / len : new Vector4(0, 0, 0, 1);
    }
}
