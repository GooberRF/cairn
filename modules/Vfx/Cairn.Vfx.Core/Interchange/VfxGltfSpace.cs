using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Interchange;

/// <summary>
/// REDUX's VFX &lt;-&gt; glTF space: mirror X for vectors, quaternions <c>normalize(-x, y, z, w)</c> (the same formula
/// both ways), triangle winding <c>(a,c,b)</c>, UVs unchanged. Not RFA's <c>GltfSpace</c> (its quaternion is the conjugate).
/// </summary>
public static class VfxGltfSpace
{
    public static Vector3 Vector(Vector3 v) => new(-v.X, v.Y, v.Z);

    public static Quaternion Rotation(Quaternion q)
    {
        var r = new Quaternion(-q.X, q.Y, q.Z, q.W);
        float len = r.Length();
        return len < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(r);
    }

    public static (int A, int B, int C) Winding(int a, int b, int c) => (a, c, b);

    public static VfxTransform Transform(VfxTransform t) => new(Vector(t.Translation), Rotation(t.Rotation), t.Scale);
}

/// <summary>
/// TRS helpers: keys evaluated with <see cref="VfxKeyframeMath"/>, composition and the exporter's sample grid.
/// </summary>
internal static class VfxTrs
{
    public const int TicksPerFrame = 320;
    public const float DefaultFps = 15f;

    public static Matrix4x4 Matrix(VfxTransform t) =>
        Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Translation);

    public static VfxTransform Decompose(Matrix4x4 m) =>
        Matrix4x4.Decompose(m, out var s, out var r, out var t) ? new VfxTransform(t, Quaternion.Normalize(r), s)
        : new VfxTransform(m.Translation, Quaternion.Identity, Vector3.One);

    /// <summary>
    /// Keyframes at <paramref name="tick"/> with the pivot applied first, evaluated exactly as REDUX bakes and checks them
    /// (<c>Compose(keyframe(t), pivot)</c> in glTF space, Bezier vec3, ease-less slerp); returned in RF space. Deliberately
    /// NOT <see cref="VfxKeyframeMath"/> (engine-accurate TCB/ease): REDUX keeps authored keys only when the glTF animation
    /// reproduces its own evaluation within 1e-4, so the interchange must evaluate the same way.
    /// </summary>
    public static VfxTransform KeyedWorld(VfxKeyLists keys, VfxTransform? pivot, int tick)
    {
        var kf = new VfxTransform(VfxGltfSpace.Vector(ReduxVec3(keys.Translation, tick, Vector3.Zero)),
            VfxGltfSpace.Rotation(ReduxQuat(keys.Rotation, tick)), ReduxVec3(keys.Scale, tick, Vector3.One));
        var pv = pivot is null ? Identity : VfxGltfSpace.Transform(pivot);
        return VfxGltfSpace.Transform(Compose(kf, pv));
    }

    /// <summary>REDUX <c>VfxKeyframeMath.EvaluateVec3</c>: cubic Bezier with the tangents as control points.</summary>
    public static Vector3 ReduxVec3(ImmutableArray<VfxVectorKey> keys, int time, Vector3 fallback)
    {
        if (keys.IsDefaultOrEmpty) return fallback;
        if (keys.Length == 1 || time <= keys[0].Time) return keys[0].Value;
        if (time >= keys[^1].Time) return keys[^1].Value;
        int i = keys.Length - 2;
        while (i > 0 && time < keys[i].Time) i--;
        VfxVectorKey a = keys[i], b = keys[i + 1];
        float span = b.Time - a.Time;
        if (span <= 0f) return a.Value;
        float u = (time - a.Time) / span, iu = 1f - u;
        return iu * iu * iu * a.Value + 3f * iu * iu * u * a.OutTangent + 3f * iu * u * u * b.InTangent + u * u * u * b.Value;
    }

    /// <summary>REDUX <c>VfxKeyframeMath.EvaluateQuat</c>: normalised slerp; TCB and ease are ignored.</summary>
    public static Quaternion ReduxQuat(ImmutableArray<VfxRotationKey> keys, int time)
    {
        static Quaternion N(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);
        if (keys.IsDefaultOrEmpty) return Quaternion.Identity;
        if (keys.Length == 1 || time <= keys[0].Time) return N(keys[0].Value);
        if (time >= keys[^1].Time) return N(keys[^1].Value);
        int i = keys.Length - 2;
        while (i > 0 && time < keys[i].Time) i--;
        VfxRotationKey a = keys[i], b = keys[i + 1];
        float span = b.Time - a.Time;
        if (span <= 0f) return N(a.Value);
        return Quaternion.Normalize(Quaternion.Slerp(N(a.Value), N(b.Value), (time - a.Time) / span));
    }

    /// <summary>REDUX's TRS composition (inner applied first; component-wise scale, no shear).</summary>
    public static VfxTransform Compose(VfxTransform outer, VfxTransform inner) =>
        new(outer.Translation + Vector3.Transform(outer.Scale * inner.Translation, outer.Rotation),
            Quaternion.Normalize(Quaternion.Multiply(outer.Rotation, inner.Rotation)), outer.Scale * inner.Scale);

    /// <summary>REDUX's <c>SamplesMatch</c> for one sample (absolute 1e-4 on T and S, |dot| on R).</summary>
    public static bool ReduxNear(VfxTransform a, VfxTransform b, float eps = 1e-4f) =>
        (a.Translation - b.Translation).Length() <= eps && (a.Scale - b.Scale).Length() <= eps
        && MathF.Abs(Quaternion.Dot(a.Rotation, b.Rotation)) >= 1f - eps;

    /// <summary>The world samples (RF space) the exporter bakes for a mesh, or null when it has no transform track.</summary>
    public static VfxTransform[]? MeshSamples(VfxMesh m)
    {
        int n = Math.Max(1, m.Frames.Length);
        if (m.Keys is { } keys) return Enumerable.Range(0, n).Select(i => KeyedWorld(keys, m.Pivot, i * TicksPerFrame)).ToArray();
        if (m.IsMorph) return null;
        var result = new VfxTransform[n];
        VfxTransform? last = null;
        for (int i = 0; i < n; i++) result[i] = last = (i < m.Frames.Length ? m.Frames[i].Transform : null) ?? last ?? Identity;
        return last is null ? null : result;
    }

    public static float[] MeshTimes(VfxMesh m, int n)
    {
        float fps = m.Fps is > 0 ? m.Fps.Value : DefaultFps, start = m.StartTime ?? 0;
        return Enumerable.Range(0, n).Select(i => start + i / fps).ToArray();
    }

    public static readonly VfxTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public static bool Near(VfxTransform a, VfxTransform b, float eps = 1e-4f) =>
        Vector3.Distance(a.Translation, b.Translation) <= eps * Math.Max(1, a.Translation.Length())
        && Math.Abs(Quaternion.Dot(a.Rotation, b.Rotation)) >= 1 - eps && Vector3.Distance(a.Scale, b.Scale) <= eps;
}

/// <summary>Lossless JSON form of the VFX records (Cairn's <c>cairn_record</c> extras key, alongside REDUX's rf_* keys).</summary>
internal static class VfxGltfRecords
{
    public const string Key = "cairn_record";

    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Dictionary<string, Type> Kinds = new[]
    {
        typeof(VfxMesh), typeof(VfxMaterial), typeof(VfxParticleSystem), typeof(VfxDummy), typeof(VfxLight),
        typeof(VfxSpacewarp), typeof(VfxOpaqueSection),
    }.ToDictionary(t => t.Name);

    public static JsonNode ToNode(VfxSection s)
    {
        var node = JsonSerializer.SerializeToNode(s, s.GetType(), Options)!.AsObject();
        node["kind"] = s.GetType().Name;
        return node;
    }

    public static VfxSection? FromNode(JsonNode? node)
    {
        if (node is not JsonObject o || o["kind"]?.GetValue<string>() is not { } kind || !Kinds.TryGetValue(kind, out var type)) return null;
        try { return (VfxSection?)o.Deserialize(type, Options); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
