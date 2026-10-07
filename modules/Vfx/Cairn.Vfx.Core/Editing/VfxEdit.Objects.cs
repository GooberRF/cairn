using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>Material sample tracks.</summary>
public enum VfxMaterialTrack { Opacity, SelfIllumination, Mix }

public static partial class VfxEdit
{
    /// <summary>
    /// Sets any material field via <paramref name="update"/>, then checks the 0x40006 type gates
    /// (textures/spec for types 0-1, mix for 1, solid colour for 2).
    /// </summary>
    public static VfxFile UpdateMaterial(VfxFile file, int index, Func<VfxMaterial, VfxMaterial> update) => Update<VfxMaterial>(file, index, m =>
    {
        var r = update(m);
        bool tex = r.Type is 0 or 1;
        if (r.Type is < 0 or > 2 || r.Fps is null || r.Additive is null || (r.Texture0 is not null) != tex || (r.Texture1 is not null) != (r.Type == 1)
            || (r.Mix is not null) != (r.Type == 1) || (r.SpecularGlossReflection is not null) != tex || (r.ReflectionTexture is not null) != tex
            || (r.SolidColor is not null) != (r.Type == 2) || r.Opacity is null || r.LegacyFps is not null || r.SelfIllumination.IsDefault)
            throw new ArgumentException("Material fields do not match its type at version 0x40006.");
        return r;
    });

    /// <summary>
    /// Changes a material's type (0 image, 1 two-texture mix, 2 colour only), adding the fields the new type needs with
    /// stock-like defaults (grey colour, second texture = the first, half mix, no specular or reflection) and dropping the rest.
    /// </summary>
    public static VfxFile SetMaterialType(VfxFile file, int index, int type)
    {
        if (type is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(type));
        return UpdateMaterial(file, index, m =>
        {
            if (m.Type == type) return m;
            bool tex = type is 0 or 1;
            var t0 = tex ? m.Texture0 ?? new VfxTexture("", 0, 0, 0) : null;
            return m with
            {
                Type = type,
                Texture0 = t0,
                Texture1 = type == 1 ? m.Texture1 ?? t0 : null,
                Mix = type == 1 ? m.Mix ?? [0.5f] : null,
                SpecularGlossReflection = tex ? m.SpecularGlossReflection ?? System.Numerics.Vector3.Zero : null,
                ReflectionTexture = tex ? m.ReflectionTexture ?? "" : null,
                SolidColor = type == 2 ? m.SolidColor ?? new VfxColorI(128, 128, 128) : null,
            };
        });
    }

    public static VfxFile SetTexture(VfxFile file, int index, string name, int slot = 0) => UpdateMaterial(file, index, m => slot switch
    {
        0 when m.Texture0 is { } t => t.Name == name ? m : m with { Texture0 = t with { Name = name } },
        1 when m.Texture1 is { } t => t.Name == name ? m : m with { Texture1 = t with { Name = name } },
        _ => throw new ArgumentException("Material has no such texture slot."),
    });

    public static VfxFile SetAdditive(VfxFile file, int index, bool additive) =>
        UpdateMaterial(file, index, m => m.Additive == (additive ? 1 : 0) ? m : m with { Additive = (byte)(additive ? 1 : 0) });

    private static ImmutableArray<float> Track(VfxMaterial m, VfxMaterialTrack t) => t switch
    {
        VfxMaterialTrack.Opacity => m.Opacity!.Value, VfxMaterialTrack.SelfIllumination => m.SelfIllumination,
        _ => m.Mix ?? throw new ArgumentException("Only mix materials have a mix track."),
    };

    private static VfxMaterial WithTrack(VfxMaterial m, VfxMaterialTrack t, ImmutableArray<float> v) =>
        Track(m, t).SequenceEqual(v) ? m : t switch
        {
            VfxMaterialTrack.Opacity => m with { Opacity = v }, VfxMaterialTrack.SelfIllumination => m with { SelfIllumination = v }, _ => m with { Mix = v },
        };

    public static VfxFile SetTrackSample(VfxFile file, int index, VfxMaterialTrack track, int sample, float value) => UpdateMaterial(file, index, m =>
    {
        var a = Track(m, track);
        if ((uint)sample >= (uint)a.Length) throw new ArgumentOutOfRangeException(nameof(sample));
        return WithTrack(m, track, a.SetItem(sample, value));
    });

    /// <summary>Trims or extends a track (extension holds the last sample, or 1 when empty).</summary>
    public static VfxFile ResizeTrack(VfxFile file, int index, VfxMaterialTrack track, int length) => UpdateMaterial(file, index, m =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var a = Track(m, track);
        return WithTrack(m, track, [.. a.Take(length), .. Enumerable.Repeat(a.IsEmpty ? 1f : a[^1], Math.Max(0, length - a.Length))]);
    });

    /// <summary>Fills a track with <paramref name="length"/> samples linearly interpolated from (sample index, value) points.</summary>
    public static VfxFile FillTrack(VfxFile file, int index, VfxMaterialTrack track, int length, IReadOnlyList<(float At, float Value)> points) => UpdateMaterial(file, index, m =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (points.Count == 0) throw new ArgumentException("Need at least one point.");
        var p = points.OrderBy(x => x.At).ToList();
        float At(int i)
        {
            if (i <= p[0].At) return p[0].Value;
            for (int k = 1; k < p.Count; k++)
                if (i <= p[k].At) return p[k - 1].Value + (p[k].Value - p[k - 1].Value) * (i - p[k - 1].At) / (p[k].At - p[k - 1].At);
            return p[^1].Value;
        }
        return WithTrack(m, track, [.. Enumerable.Range(0, length).Select(At)]);
    });

    // ---- per-frame objects (particle system, dummy, light, spacewarp) ----

    private static ImmutableArray<TF> FramesOf<TF>(VfxSection s) => s switch
    {
        VfxParticleSystem p when p.Frames is ImmutableArray<TF> a => a, VfxDummy d when d.Frames is ImmutableArray<TF> a => a,
        VfxLight l when l.Frames is ImmutableArray<TF> a => a, VfxSpacewarp w when w.Frames is ImmutableArray<TF> a => a,
        _ => throw new ArgumentException($"{s.GetType().Name} has no {typeof(TF).Name} frames."),
    };

    private static VfxSection WithFrames<TF>(VfxSection s, ImmutableArray<TF> f) => (s, f) switch
    {
        (VfxParticleSystem p, ImmutableArray<VfxParticleFrame> a) => p with { Frames = a }, (VfxDummy d, ImmutableArray<VfxDummyFrame> a) => d with { Frames = a },
        (VfxLight l, ImmutableArray<VfxLightParams> a) => l with { Frames = a }, (VfxSpacewarp w, ImmutableArray<VfxSpacewarpFrame> a) => w with { Frames = a },
        _ => throw new ArgumentException("Frame type does not match the section."),
    };

    /// <summary>Applies <paramref name="update"/> to one frame (or all when null) of a particle system / dummy / light / spacewarp.</summary>
    public static VfxFile SetObjectFrame<TF>(VfxFile file, int index, Func<TF, TF> update, int? frame = null)
    {
        var s = Get<VfxSection>(file, index);
        var a = FramesOf<TF>(s);
        if (frame is { } fi && (uint)fi >= (uint)a.Length) throw new ArgumentOutOfRangeException(nameof(frame));
        var n = a.Select((x, i) => frame is null || frame == i ? update(x) : x).ToImmutableArray();
        return n.SequenceEqual(a) ? file : Put(file, index, WithFrames(s, n));
    }

    /// <summary>Trims or extends (holding the last frame) the frames of a particle system / dummy / light / spacewarp.</summary>
    public static VfxFile ResizeObjectFrames(VfxFile file, int index, int count)
    {
        var s = Get<VfxSection>(file, index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (s is VfxParticleSystem && count == 0) throw new ArgumentOutOfRangeException(nameof(count));
        VfxSection r = s switch
        {
            VfxParticleSystem p => p with { Frames = Resize(p.Frames, count) }, VfxDummy d => d with { Frames = Resize(d.Frames, count) },
            VfxLight l => l with { Frames = Resize(l.Frames, count) }, VfxSpacewarp w => w with { Frames = Resize(w.Frames, count) },
            _ => throw new ArgumentException("Section has no object frames."),
        };
        return FramesCount(s) == count ? file : Put(file, index, r);

        static int FramesCount(VfxSection x) => x switch { VfxParticleSystem p => p.Frames.Length, VfxDummy d => d.Frames.Length, VfxLight l => l.Frames.Length, VfxSpacewarp w => w.Frames.Length, _ => 0 };
    }

    private static ImmutableArray<T> Resize<T>(ImmutableArray<T> a, int n) =>
        n <= a.Length ? a.RemoveRange(n, a.Length - n) : a.IsEmpty ? throw new ArgumentException("Cannot extend an empty frame list.") : a.AddRange(Enumerable.Repeat(a[^1], n - a.Length));

    /// <summary>Sets the particle-system material index (checked against the material sections).</summary>
    public static VfxFile SetParticleMaterial(VfxFile file, int index, int materialIndex)
    {
        if (materialIndex < 0 || materialIndex >= file.Sections.OfType<VfxMaterial>().Count()) throw new ArgumentOutOfRangeException(nameof(materialIndex));
        return Update<VfxParticleSystem>(file, index, p => p.MaterialIndex == materialIndex ? p : p with { MaterialIndex = materialIndex });
    }
}
