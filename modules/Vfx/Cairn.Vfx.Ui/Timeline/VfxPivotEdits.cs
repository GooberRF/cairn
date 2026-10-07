using System.Collections.Immutable;
using System.Numerics;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Ui.Timeline;

/// <summary>
/// Pivot mode: change a keyframed mesh's pivot while compensating the key tracks so that
/// world = kT + kR·(kS·(pT + pR·(pS·p))) is unchanged at every time.
/// Translation d (key space): pT += d, every translation key value and both control points -= kR·kS·d; exact only when
/// kR·kS is the same at every time (all rotation keys equal, all scale keys equal incl. control points).
/// Rotation q (key space): pR' = q·pR, pT' = q·pT, every rotation key kR' = kR·q⁻¹ (right multiplication commutes with
/// slerp and the ease); exact when every scale key is uniform (kS commutes with q). Translation keys unchanged.
/// </summary>
public static class VfxPivotEdits
{
    private static readonly VfxTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    private static bool SameVec(ImmutableArray<VfxVectorKey> keys) => keys.All(k => k.Value == keys[0].Value && k.InTangent == k.Value && k.OutTangent == k.Value);
    private static bool Uniform(Vector3 v) => MathF.Abs(v.X - v.Y) < 1e-6f && MathF.Abs(v.X - v.Z) < 1e-6f;

    /// <summary>Why a pivot translation change cannot be compensated exactly (null = it can).</summary>
    public static string? MoveBlocked(VfxFile f, int i) => f.Sections.ElementAtOrDefault(i) is not VfxMesh { Keys: { } k }
        ? "Pivot mode needs a keyframed mesh (convert with \"->key\"; static meshes have no pivot)"
        : k.Rotation.Any(r => MathF.Abs(Quaternion.Dot(r.Value, k.Rotation[0].Value)) < 1 - 1e-6f) ? "Rotation keys differ: moving the pivot would change the motion (key translation cannot follow a rotating offset exactly)"
        : k.Scale.Length > 0 && !SameVec(k.Scale) ? "Scale keys differ: moving the pivot would change the motion" : null;

    /// <summary>Why a pivot rotation change cannot be compensated exactly (null = it can).</summary>
    public static string? TurnBlocked(VfxFile f, int i) => f.Sections.ElementAtOrDefault(i) is not VfxMesh { Keys: { } k }
        ? "Pivot mode needs a keyframed mesh"
        : k.Scale.Any(s => !Uniform(s.Value) || !Uniform(s.InTangent) || !Uniform(s.OutTangent)) ? "Scale keys are non-uniform: rotating the pivot would shear the mesh" : null;

    /// <summary>Moves the pivot by <paramref name="d"/> (key space).</summary>
    public static VfxFile Move(VfxFile f, int i, Vector3 d)
    {
        if (MoveBlocked(f, i) is { } why) throw new InvalidOperationException(why);
        var m = (VfxMesh)f.Sections[i]; var k = m.Keys!; var p = m.Pivot ?? Identity;
        var r = k.Rotation.Length > 0 ? k.Rotation[0].Value : Quaternion.Identity;
        var s = k.Scale.Length > 0 ? k.Scale[0].Value : Vector3.One;
        var c = Vector3.Transform(s * d, r);
        f = VfxEdit.SetPivot(f, i, p with { Translation = p.Translation + d });
        foreach (var t in k.Translation) f = VfxEdit.SetKey(f, i, VfxKeyChannel.Translation, t with { Value = t.Value - c, InTangent = t.InTangent - c, OutTangent = t.OutTangent - c });
        return f;
    }

    /// <summary>Rotates the pivot by <paramref name="q"/> (key space, about the key-space origin).</summary>
    public static VfxFile Turn(VfxFile f, int i, Quaternion q)
    {
        if (TurnBlocked(f, i) is { } why) throw new InvalidOperationException(why);
        var m = (VfxMesh)f.Sections[i]; var k = m.Keys!; var p = m.Pivot ?? Identity;
        f = VfxEdit.SetPivot(f, i, p with { Rotation = Quaternion.Normalize(q * p.Rotation), Translation = Vector3.Transform(p.Translation, q) });
        var inv = Quaternion.Inverse(q);
        foreach (var r in k.Rotation) f = VfxEdit.SetRotationKey(f, i, r with { Value = Quaternion.Normalize(r.Value * inv) });
        return f;
    }

    /// <summary>Gizmo path: world move / world turn at the playhead, converted to key space with the key rotation and scale there.</summary>
    public static VfxFile ApplyGizmo(VfxFile f, int i, float frame, Vector3 move, Quaternion turn)
    {
        VfxTimelineEdits.TryGetPlacement(f, i, frame, out _, out var kr);
        var k = ((VfxMesh)f.Sections[i]).Keys!;
        var s = k.Scale.Length > 0 ? k.Scale[0].Value : Vector3.One;
        if (move != Vector3.Zero) f = Move(f, i, Vector3.Transform(move, Quaternion.Inverse(kr)) / s);
        if (!turn.IsIdentity) f = Turn(f, i, Quaternion.Normalize(Quaternion.Inverse(kr) * turn * kr));
        return f;
    }
}
