using Cairn.Vfx.Animation;

namespace Cairn.Vfx.Rfe;

/// <summary>
/// Samples a Red Faction II effect the way the game does. Transform keys have no times: n keys are spread evenly over the
/// duration, so at fraction u = elapsed / duration the game takes f = (n - 1) u and blends key floor(f) into key ceil(f)
/// (positions and scales lerped, rotations slerped the short way; <c>rf2.exe</c> 0x40a0a0, 0x4a82c0, 0x4a83b0, 0x56ad40).
/// One key is constant; none gives the identity. Material and emitter tracks are keyed in seconds and lerped, holding
/// the end values outside their keys (0x409fb0). A transform is T(position) R(rotation) S(scale); the game builds
/// R's rows from the stored quaternion as a 3x3 matrix and applies it to row vectors, which in
/// <see cref="System.Numerics"/> terms is a rotation by the quaternion's conjugate (<see cref="Rotation"/>).
/// </summary>
public static class RfeSampler
{
    /// <summary>The fixed particle step of the game (<c>rf2.exe</c> 0x5f56e4): 1/30 s.</summary>
    public const float ParticleStep = 1f / 30;

    /// <summary>Gravity on emitters with flag bit 1, m/s².</summary>
    public const float Gravity = 9.8f;

    /// <summary>
    /// Effect time for an elapsed time: looping wraps at the duration (the game's loop mode 3), otherwise it holds at the
    /// end. Non-finite input counts as 0.
    /// </summary>
    public static float EffectTime(double elapsed, float duration, bool loop)
    {
        if (!double.IsFinite(elapsed) || elapsed < 0) elapsed = 0;
        if (!(duration > 0) || !float.IsFinite(duration)) return 0;
        if (elapsed < duration) return (float)elapsed;
        return loop ? (float)(elapsed - Math.Floor(elapsed / duration) * duration) : duration;
    }

    /// <summary>The fraction of the effect at <paramref name="seconds"/> (0 to 1).</summary>
    public static float Fraction(float seconds, float duration) =>
        duration > 0 && float.IsFinite(seconds) ? Math.Clamp(seconds / duration, 0, 1) : 0;

    /// <summary>The two keys and the blend at fraction <paramref name="u"/> of a track of <paramref name="count"/> evenly spread keys.</summary>
    public static (int Key0, int Key1, float Blend) EvenKeys(int count, float u)
    {
        if (count <= 1) return (0, 0, 0);
        float f = (count - 1) * (float.IsFinite(u) ? Math.Clamp(u, 0, 1) : 0);
        int i0 = Math.Min((int)MathF.Floor(f), count - 1);
        int i1 = Math.Min((int)MathF.Ceiling(f), count - 1);
        return (i0, i1, f - i0);
    }

    /// <summary>A position or scale track at fraction <paramref name="u"/>; <paramref name="none"/> without keys.</summary>
    public static Vector3 Sample(ImmutableArray<Vector3> keys, float u, Vector3 none)
    {
        if (keys.IsDefaultOrEmpty) return none;
        var (a, b, t) = EvenKeys(keys.Length, u);
        return a == b ? keys[a] : Vector3.Lerp(keys[a], keys[b], t);
    }

    /// <summary>A rotation track (as stored) at fraction <paramref name="u"/>; the identity without keys.</summary>
    public static Quaternion Sample(ImmutableArray<Quaternion> keys, float u)
    {
        if (keys.IsDefaultOrEmpty) return Quaternion.Identity;
        var (a, b, t) = EvenKeys(keys.Length, u);
        return a == b ? keys[a] : VfxKeyframeMath.Slerp(keys[a], keys[b], t);
    }

    /// <summary>A time-keyed track at <paramref name="seconds"/>; <paramref name="none"/> without keys.</summary>
    public static float Track(ImmutableArray<RfeKey> keys, float seconds, float none)
    {
        if (keys.IsDefaultOrEmpty) return none;
        if (!(seconds > keys[0].Time)) return keys[0].Value;
        for (int i = 1; i < keys.Length; i++)
        {
            if (seconds >= keys[i].Time) continue;
            var k0 = keys[i - 1];
            var k1 = keys[i];
            float span = k1.Time - k0.Time;
            return span > 0 ? k0.Value + (k1.Value - k0.Value) * ((seconds - k0.Time) / span) : k1.Value;
        }
        return keys[^1].Value;
    }

    /// <summary>
    /// The <see cref="System.Numerics"/> rotation the game applies for a stored quaternion: its conjugate, normalised
    /// (the identity for a zero or non-finite one).
    /// </summary>
    public static Quaternion Rotation(Quaternion stored)
    {
        float l = stored.Length();
        if (!(l > 1e-12f) || !float.IsFinite(l)) return Quaternion.Identity;
        return Quaternion.Conjugate(Quaternion.Multiply(stored, 1 / l));
    }

    /// <summary>A mesh object's transform at fraction <paramref name="u"/>.</summary>
    public static (Vector3 Position, Quaternion Rotation, Vector3 Scale) Transform(RfeMeshObject mesh, float u)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return (Sample(mesh.Positions, u, Vector3.Zero), Rotation(Sample(mesh.Rotations, u)), Sample(mesh.Scales, u, Vector3.One));
    }

    /// <summary>A mesh object's local-to-effect matrix at fraction <paramref name="u"/> (row vectors: scale, then rotation, then translation).</summary>
    public static Matrix4x4 Matrix(RfeMeshObject mesh, float u)
    {
        var (p, q, s) = Transform(mesh, u);
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(p);
    }

    /// <summary>
    /// The rotation the game draws a facing mesh object with (<c>rf2.exe</c> 0x566206 for facing 1, 0x566095 for facing 2),
    /// as a matrix whose rows are the object's local axes in effect space (row vectors, like
    /// <see cref="Matrix4x4.CreateFromQuaternion"/>). Both modes turn the object's local +Z towards
    /// <paramref name="eye"/>, so its local XY plane faces the camera. Facing 1 (a billboard) points +Z straight at the eye
    /// and keeps local Y as near the keyed rotation's Y as it can; facing 2 keeps the keyed Y axis and turns about it. The
    /// keyed rotation is kept when the eye is on the object's position, on the billboard's Y axis, or within 0.9999 of the
    /// axis of facing 2 (the game's own test); other modes return it unchanged.
    /// </summary>
    /// <param name="mode">The facing mode.</param>
    /// <param name="rotation">The keyed rotation at this time (<see cref="Transform"/>).</param>
    /// <param name="position">The object's position at this time.</param>
    /// <param name="eye">The camera position, in effect space.</param>
    public static Matrix4x4 FacingRotation(RfeFacing mode, Quaternion rotation, Vector3 position, Vector3 eye)
    {
        var keyed = Matrix4x4.CreateFromQuaternion(rotation);
        if (mode == RfeFacing.None) return keyed;
        var axis = new Vector3(keyed.M21, keyed.M22, keyed.M23);
        var toEye = eye - position;
        if (!(toEye.LengthSquared() > 1e-12f) || !float.IsFinite(toEye.LengthSquared()) || !(axis.LengthSquared() > 1e-12f)) return keyed;
        toEye = Vector3.Normalize(toEye);
        axis = Vector3.Normalize(axis);
        if (mode == RfeFacing.Billboard)
        {
            // z at the camera, x = keyed Y × z, y = z × x
            var x = Vector3.Cross(axis, toEye);
            if (!(x.LengthSquared() > 1e-12f)) return keyed;
            x = Vector3.Normalize(x);
            return Rows(x, Vector3.Cross(toEye, x), toEye);
        }
        // the game: d = position - eye; x = d × Y, z = x × Y (towards the camera, across the axis)
        var d = -toEye;
        float along = Vector3.Dot(axis, d);
        if (!(along < 0.9999f && along > -0.9999f)) return keyed;
        var side = Vector3.Normalize(Vector3.Cross(d, axis));
        return Rows(side, axis, Vector3.Normalize(Vector3.Cross(side, axis)));
    }

    private static Matrix4x4 Rows(Vector3 x, Vector3 y, Vector3 z) => new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);

    /// <summary>
    /// How a facing mesh object's body lies against its local XY plane, the plane the game turns to the camera: by the
    /// extents of its vertices (a side counts as flat below a thousandth of the longest).
    /// </summary>
    public static RfeFacingLie Lie(RfeMeshObject mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.FacingMode == RfeFacing.None) return RfeFacingLie.Upright;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var g in mesh.Body.Geometries)
        {
            foreach (var p in g.Positions)
            {
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        if (min.X > max.X) return RfeFacingLie.Upright;
        var size = max - min;
        float flat = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * 1e-3f;
        if (!(flat > 0)) return RfeFacingLie.Upright;
        if (mesh.FacingMode == RfeFacing.Axial && size.Y <= flat) return RfeFacingLie.AcrossAxis;
        return size.X <= flat || size.Y <= flat ? RfeFacingLie.EdgeOn : RfeFacingLie.Upright;
    }

    /// <summary>A point of a mesh object in effect space at fraction <paramref name="u"/>.</summary>
    public static Vector3 Apply(RfeMeshObject mesh, float u, Vector3 point)
    {
        var (p, q, s) = Transform(mesh, u);
        return p + Vector3.Transform(s * point, q);
    }

    /// <summary>
    /// An emitter's position and orientation at fraction <paramref name="u"/>, as <see cref="System.Numerics"/> values; the
    /// game launches particles along the orientation's local -Y (its matrix's second row, negated by the emitter update).
    /// </summary>
    public static (Vector3 Position, Quaternion Orientation) Pose(RfeEmitter emitter, float u)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        return (Sample(emitter.Positions, u, Vector3.Zero), Rotation(Sample(emitter.Rotations, u)));
    }

    /// <summary>A material's opacity (track 2) at <paramref name="seconds"/>, 0 to 1.</summary>
    public static float Opacity(RfeMaterial material, float seconds) => Math.Clamp(Track(material.Opacity, seconds, 1), 0, 1);

    /// <summary>A material's track 1 (the RGB level, probably self-illumination) at <paramref name="seconds"/>, 0 to 1.</summary>
    public static float SelfIllumination(RfeMaterial material, float seconds) => Math.Clamp(Track(material.SelfIllumination, seconds, 0), 0, 1);

    /// <summary>The opacity particles of a material use: key 0's value of track 2.</summary>
    public static float ParticleOpacity(RfeMaterial material) =>
        material.Opacity.IsDefaultOrEmpty ? 1 : Math.Clamp(float.IsFinite(material.Opacity[0].Value) ? material.Opacity[0].Value : 1, 0, 1);
}
