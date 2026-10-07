using System.Numerics;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>Per-frame scalar fields of particle systems, lights and spacewarps (see <see cref="VfxEdit.SetObjectValue"/>).</summary>
public enum VfxObjectValue
{
    /// <summary>Particle frames.</summary>
    Width, Height, DropSize, Speed, SpeedVariation, BirthRate,
    /// <summary>Light frames.</summary>
    Radius, Multiplier,
    /// <summary>Spacewarp frames.</summary>
    Strength, Decay, Turbulence, Frequency, Scale,
}

/// <summary>Known particle-system flag bits.</summary>
public static class VfxParticleFlags
{
    public const uint Drops = 0x100;
}

public static partial class VfxEdit
{
    /// <summary>Flag names accepted by <see cref="SetFlag"/> per section type (case-insensitive).</summary>
    public static IReadOnlyDictionary<string, uint> FlagNames(VfxSection section) => section switch
    {
        VfxMesh => MeshFlagNames,
        VfxParticleSystem => ParticleFlagNames,
        _ => throw new ArgumentException("Only meshes and particle systems have flags."),
    };

    private static readonly IReadOnlyDictionary<string, uint> MeshFlagNames = typeof(VfxMeshFlags).GetFields()
        .Where(f => f.IsLiteral && f.FieldType == typeof(uint)).ToDictionary(f => f.Name, f => (uint)f.GetRawConstantValue()!, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, uint> ParticleFlagNames =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) { [nameof(VfxParticleFlags.Drops)] = VfxParticleFlags.Drops };

    /// <summary>
    /// Sets or clears a named flag (<see cref="VfxMeshFlags"/> names on meshes, through <see cref="SetMeshFlags"/> so frames are
    /// relaid out; <see cref="VfxParticleFlags"/> names on particle systems).
    /// </summary>
    public static VfxFile SetFlag(VfxFile file, int index, string name, bool on)
    {
        var s = Get<VfxSection>(file, index);
        if (!FlagNames(s).TryGetValue(name, out uint bit)) throw new ArgumentException($"Unknown flag '{name}' for {s.GetType().Name}.", nameof(name));
        return s switch
        {
            VfxMesh m => SetMeshFlags(file, index, on ? m.Flags | bit : m.Flags & ~bit),
            // The tail distance field exists exactly when the drops bit is set, so it follows the flag in the same edit.
            VfxParticleSystem p => Put(file, index, p with { Flags = on ? (p.Flags ?? 0) | bit : (p.Flags ?? 0) & ~bit } is var q
                ? q with { TailDistance = q.IsDrops ? p.TailDistance ?? 0f : null } : p),
            _ => file,
        };
    }

    /// <summary>Sets a per-frame scalar at one frame (or every frame when <paramref name="frame"/> is null).</summary>
    public static VfxFile SetObjectValue(VfxFile file, int index, VfxObjectValue field, float value, int? frame = null) => field switch
    {
        VfxObjectValue.Width => SetObjectFrame<VfxParticleFrame>(file, index, f => f with { Width = value }, frame),
        VfxObjectValue.Height => SetObjectFrame<VfxParticleFrame>(file, index, f => f with { Height = value }, frame),
        VfxObjectValue.DropSize => SetObjectFrame<VfxParticleFrame>(file, index, f => f with { DropSize = value }, frame),
        VfxObjectValue.Speed => SetObjectFrame<VfxParticleFrame>(file, index, f => f with { Speed = value }, frame),
        VfxObjectValue.SpeedVariation => SetObjectFrame<VfxParticleFrame>(file, index, f => f with { SpeedVariation = value }, frame),
        VfxObjectValue.BirthRate => SetObjectFrame<VfxParticleFrame>(file, index, f => f with { BirthRate = value }, frame),
        VfxObjectValue.Radius => SetLightFrames(file, index, l => l with { Radius = value }, frame),
        VfxObjectValue.Multiplier => SetLightFrames(file, index, l => l with { Multiplier = value }, frame),
        VfxObjectValue.Strength => SetObjectFrame<VfxSpacewarpFrame>(file, index, f => f with { Strength = value }, frame),
        VfxObjectValue.Decay => SetObjectFrame<VfxSpacewarpFrame>(file, index, f => f with { Decay = value }, frame),
        VfxObjectValue.Turbulence => SetObjectFrame<VfxSpacewarpFrame>(file, index, f => f with { Turbulence = value }, frame),
        VfxObjectValue.Frequency => SetObjectFrame<VfxSpacewarpFrame>(file, index, f => f with { Frequency = value }, frame),
        VfxObjectValue.Scale => SetObjectFrame<VfxSpacewarpFrame>(file, index, f => f with { Scale = value }, frame),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>
    /// Sets the position of a particle system, dummy, light or spacewarp at one frame, or at every frame when
    /// <paramref name="frame"/> is null (then also the dummy's static position / the light's initial values).
    /// </summary>
    public static VfxFile SetObjectPosition(VfxFile file, int index, Vector3 position, int? frame = null) => Get<VfxSection>(file, index) switch
    {
        VfxParticleSystem => SetObjectFrame<VfxParticleFrame>(file, index, f => f with { Position = position }, frame),
        VfxDummy => SetDummy(SetObjectFrame<VfxDummyFrame>(file, index, f => f with { Position = position }, frame), index, frame, x => x with { Position = position }),
        VfxLight => SetLightFrames(file, index, l => l with { Position = position }, frame),
        VfxSpacewarp => SetObjectFrame<VfxSpacewarpFrame>(file, index, f => f with { Position = position }, frame),
        _ => throw new ArgumentException("Section has no object frames."),
    };

    /// <summary>Sets the orientation of a particle system, dummy or spacewarp (all frames when <paramref name="frame"/> is null; dummies then also their static orientation).</summary>
    public static VfxFile SetObjectOrientation(VfxFile file, int index, Quaternion orientation, int? frame = null) => Get<VfxSection>(file, index) switch
    {
        VfxParticleSystem => SetObjectFrame<VfxParticleFrame>(file, index, f => f with { Orientation = orientation }, frame),
        VfxDummy => SetDummy(SetObjectFrame<VfxDummyFrame>(file, index, f => f with { Orientation = orientation }, frame), index, frame, x => x with { Orientation = orientation }),
        VfxSpacewarp => SetObjectFrame<VfxSpacewarpFrame>(file, index, f => f with { Orientation = orientation }, frame),
        _ => throw new ArgumentException("Section has no orientation."),
    };

    /// <summary>Sets a light's colour (0..1 per channel) at one frame, or at every frame and the initial values when <paramref name="frame"/> is null.</summary>
    public static VfxFile SetLightColor(VfxFile file, int index, Vector3 color, int? frame = null) =>
        SetLightFrames(file, index, l => l with { Color = color }, frame);

    /// <summary>Switches a light on or off at one frame, or at every frame and the initial values when <paramref name="frame"/> is null.</summary>
    public static VfxFile SetLightOn(VfxFile file, int index, bool on, int? frame = null) =>
        SetLightFrames(file, index, l => l with { IsOn = on ? (byte)1 : (byte)0 }, frame);

    private static VfxFile SetLightFrames(VfxFile file, int index, Func<VfxLightParams, VfxLightParams> update, int? frame)
    {
        var r = SetObjectFrame(file, index, update, frame);
        return frame is null ? Update<VfxLight>(r, index, l => update(l.Initial) is var i && i == l.Initial ? l : l with { Initial = i }) : r;
    }

    private static VfxFile SetDummy(VfxFile file, int index, int? frame, Func<VfxDummy, VfxDummy> update) =>
        frame is null ? Update<VfxDummy>(file, index, update) : file;
}
