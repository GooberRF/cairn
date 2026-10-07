using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

public enum VfxKeyChannel { Translation, Rotation, Scale }

public static partial class VfxEdit
{
    /// <summary>Sets the per-frame transform of a non-keyframed, non-morph mesh on one frame or all frames.</summary>
    public static VfxFile SetStaticTransform(VfxFile file, int index, VfxTransform transform, int? frame = null) => Mesh(file, index, m =>
    {
        if (m.Frames[0].Transform is null) throw new ArgumentException("Mesh has no per-frame transforms (keyframed or morph).");
        if (frame is { } fi && (uint)fi >= (uint)m.Frames.Length) throw new ArgumentOutOfRangeException(nameof(frame));
        return m with { Frames = m.Frames.Select((f, i) => (frame is null || frame == i) && f.Transform != transform ? f with { Transform = transform } : f).ToImmutableArray() };
    });

    /// <summary>
    /// The key tick at which the sampler evaluates mesh-local <paramref name="frame"/>: keys run on absolute effect
    /// time, so tick = (start seconds + frame / fps) * 15 * 320.
    /// </summary>
    public static float FrameTick(VfxMesh mesh, int frame)
    {
        int fps = mesh.Fps is > 0 ? mesh.Fps.Value : VfxTime.FramesPerSecond;
        float start = mesh.StartTime ?? (mesh.StartFrame ?? 0) / (float)fps;
        // Exact integer ticks for 15 fps meshes starting at 0 (so key times round-trip bit-exactly).
        float effectFrame = start * VfxTime.FramesPerSecond + frame * (float)VfxTime.FramesPerSecond / fps;
        return effectFrame * VfxTime.TicksPerFrame;
    }

    /// <summary>
    /// Per-frame transforms -> keyframes at each frame's tick (<see cref="FrameTick"/>, rounded) with zero TCB/ease and
    /// absolute Bezier control points one third of the way to the neighbouring keys (linear between keys, as the
    /// per-frame sampler interpolates; end keys flat); with <paramref name="reduce"/> a constant channel keeps one key.
    /// Pivot = identity.
    /// </summary>
    public static VfxFile ToKeyframes(VfxFile file, int index, bool reduce = true) => Mesh(file, index, m =>
    {
        if (Kf(m)) return m;
        if (m.IsMorph) throw new ArgumentException("Morph meshes carry no per-frame transforms.");
        var t = m.Frames.Select(f => f.Transform!).ToList();
        var ticks = Enumerable.Range(0, t.Count).Select(i => (int)MathF.Round(FrameTick(m, i))).ToList();
        bool Const<T>(Func<VfxTransform, T> sel) => reduce && t.All(x => EqualityComparer<T>.Default.Equals(sel(x), sel(t[0])));
        List<int> Frames<T>(Func<VfxTransform, T> sel) => Const(sel) ? [0] : Enumerable.Range(0, t.Count).ToList();
        ImmutableArray<VfxVectorKey> Vec(Func<VfxTransform, Vector3> sel)
        {
            var fr = Frames(sel);
            return fr.Select((i, j) => new VfxVectorKey(ticks[i], sel(t[i]),
                j > 0 ? Vector3.Lerp(sel(t[i]), sel(t[fr[j - 1]]), 1 / 3f) : sel(t[i]),
                j < fr.Count - 1 ? Vector3.Lerp(sel(t[i]), sel(t[fr[j + 1]]), 1 / 3f) : sel(t[i]))).ToImmutableArray();
        }
        var keys = new VfxKeyLists(
            Vec(x => x.Translation),
            Frames(x => x.Rotation).Select(i => new VfxRotationKey(ticks[i], t[i].Rotation, 0, 0, 0, 0, 0)).ToImmutableArray(),
            Vec(x => x.Scale));
        return Relayout(m with { Keys = keys, Pivot = VfxBuilder.Identity }, m.Flags, true);
    });

    /// <summary>
    /// Keyframes -> per-frame transforms: keys are evaluated at each frame's tick exactly as the sampler does
    /// (Bezier / eased slerp) and composed with the pivot (key TRS over pivot TRS). When the composition is not a TRS
    /// (non-uniform key scale over a rotated pivot) the pivot is baked into the frame-0 positions instead.
    /// </summary>
    public static VfxFile ToPerFrameTransforms(VfxFile file, int index) => Mesh(file, index, m =>
    {
        if (!Kf(m)) return m;
        if (m.IsMorph) throw new ArgumentException("Morph meshes carry no per-frame transforms.");
        var k = m.Keys!;
        var keyed = Enumerable.Range(0, m.Frames.Length).Select(i => SampleKeys(k, FrameTick(m, i))).ToList();
        var src = m.Frames.ToList();
        if (m.Pivot is { } pv && pv != VfxBuilder.Identity)
        {
            var composed = keyed.Select(t => ComposePivot(t, pv)).ToList();
            if (composed.All(c => c is not null)) keyed = composed.Select(c => c!).ToList();
            else if (m.DecodePositions(0) is { } p0)
                src[0] = src[0] with { Positions = VfxGeometry.Quantise(p0.Select(p => VfxSampler.Apply(pv, p)).ToArray()) };
        }
        var frames = src.Select((f, i) => f with { Transform = keyed[i] }).ToList();
        var outM = Relayout(m with { Keys = null, Pivot = null }, m.Flags, false, frames);
        return ReferenceEquals(src[0].Positions, m.Frames[0].Positions) ? outM : VfxGeometry.RefreshShape(outM);
    });

    // key ∘ pivot as one TRS, or null when the result has shear (non-uniform key scale over a rotated pivot).
    private static VfxTransform? ComposePivot(VfxTransform key, VfxTransform pivot)
    {
        var t = VfxSampler.Apply(key, pivot.Translation);
        var ks = key.Scale;
        if (ks.X == ks.Y && ks.Y == ks.Z) return new(t, Quaternion.Concatenate(pivot.Rotation, key.Rotation), ks.X * pivot.Scale);
        if (MathF.Abs(Quaternion.Dot(Quaternion.Normalize(pivot.Rotation), Quaternion.Identity)) >= 1 - 1e-7f)
            return new(t, key.Rotation, ks * pivot.Scale);
        return null;
    }

    /// <summary>Samples the key lists at <paramref name="time"/> ticks (see <see cref="SampleKeys(VfxKeyLists, float)"/>).</summary>
    public static VfxTransform SampleKeys(VfxKeyLists keys, int time) => SampleKeys(keys, (float)time);

    /// <summary>
    /// Samples the key lists at <paramref name="tick"/> as the engine does (<see cref="VfxKeyframeMath"/>: Bezier with
    /// absolute control points for translation/scale, eased slerp for rotation); a rotation key exactly at the tick is
    /// returned unchanged.
    /// </summary>
    public static VfxTransform SampleKeys(VfxKeyLists keys, float tick)
    {
        var r = keys.Rotation;
        var q = r.IsDefaultOrEmpty ? Quaternion.Identity
            : r.FirstOrDefault(x => x.Time == tick) is { } exact ? exact.Value
            : VfxKeyframeMath.EvaluateRotation(r, tick);
        return new VfxTransform(VfxKeyframeMath.EvaluateVector(keys.Translation, tick, Vector3.Zero), q,
            VfxKeyframeMath.EvaluateVector(keys.Scale, tick, Vector3.One));
    }

    private static VfxFile Keys(VfxFile file, int index, Func<VfxKeyLists, VfxKeyLists> f) => Mesh(file, index, m =>
    {
        if (!Kf(m)) throw new ArgumentException("Mesh is not keyframed (use ToKeyframes).");
        var k = f(m.Keys!);
        return k.Translation.SequenceEqual(m.Keys!.Translation) && k.Rotation.SequenceEqual(m.Keys.Rotation) && k.Scale.SequenceEqual(m.Keys.Scale) ? m : m with { Keys = k };
    });

    private static ImmutableArray<T> Upsert<T>(ImmutableArray<T> list, T key, Func<T, int> time)
    {
        int at = list.ToList().FindIndex(k => time(k) >= time(key));
        if (at < 0) return list.Add(key);
        return time(list[at]) == time(key) ? list.SetItem(at, key) : list.Insert(at, key);
    }

    /// <summary>Adds a translation or scale key, replacing one at the same time.</summary>
    public static VfxFile SetKey(VfxFile file, int index, VfxKeyChannel channel, VfxVectorKey key)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(key.Time);
        return channel switch
        {
            VfxKeyChannel.Translation => Keys(file, index, k => k with { Translation = Upsert(k.Translation, key, x => x.Time) }),
            VfxKeyChannel.Scale => Keys(file, index, k => k with { Scale = Upsert(k.Scale, key, x => x.Time) }),
            _ => throw new ArgumentException("Use SetRotationKey for rotation."),
        };
    }

    /// <summary>Adds a rotation key, replacing one at the same time.</summary>
    public static VfxFile SetRotationKey(VfxFile file, int index, VfxRotationKey key)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(key.Time);
        return Keys(file, index, k => k with { Rotation = Upsert(k.Rotation, key, x => x.Time) });
    }

    /// <summary>Deletes the key at <paramref name="time"/> ticks (no-op when absent).</summary>
    public static VfxFile DeleteKey(VfxFile file, int index, VfxKeyChannel channel, int time) => Keys(file, index, k => channel switch
    {
        VfxKeyChannel.Translation => k with { Translation = k.Translation.RemoveAll(x => x.Time == time) },
        VfxKeyChannel.Rotation => k with { Rotation = k.Rotation.RemoveAll(x => x.Time == time) },
        _ => k with { Scale = k.Scale.RemoveAll(x => x.Time == time) },
    });

    /// <summary>Moves the key at <paramref name="from"/> to <paramref name="to"/> ticks (replacing a key there).</summary>
    public static VfxFile MoveKey(VfxFile file, int index, VfxKeyChannel channel, int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(to);
        var m = Get<VfxMesh>(file, index);
        if (from == to || m.Keys is null) return file;
        var k = m.Keys;
        return channel switch
        {
            VfxKeyChannel.Rotation => k.Rotation.FirstOrDefault(x => x.Time == from) is { } r
                ? SetRotationKey(DeleteKey(file, index, channel, from), index, r with { Time = to }) : throw new ArgumentException("No key at that time."),
            _ => (channel == VfxKeyChannel.Translation ? k.Translation : k.Scale).FirstOrDefault(x => x.Time == from) is { } v
                ? SetKey(DeleteKey(file, index, channel, from), index, channel, v with { Time = to }) : throw new ArgumentException("No key at that time."),
        };
    }

    /// <summary>
    /// Catmull-Rom slopes for a translation/scale channel, stored as the absolute Bezier control points the engine
    /// expects (value -/+ slope x segment length / 3); end keys flat (control point = value).
    /// </summary>
    public static VfxFile AutoTangents(VfxFile file, int index, VfxKeyChannel channel)
    {
        if (channel == VfxKeyChannel.Rotation) throw new ArgumentException("Rotation keys use TCB, not tangents.");
        return Keys(file, index, k =>
        {
            var list = channel == VfxKeyChannel.Translation ? k.Translation : k.Scale;
            var outL = list.Select((key, i) =>
            {
                if (i == 0 || i == list.Length - 1) return key with { InTangent = key.Value, OutTangent = key.Value };
                var slope = (list[i + 1].Value - list[i - 1].Value) / (list[i + 1].Time - list[i - 1].Time);
                return key with
                {
                    InTangent = key.Value - slope * ((key.Time - list[i - 1].Time) / 3f),
                    OutTangent = key.Value + slope * ((list[i + 1].Time - key.Time) / 3f),
                };
            }).ToImmutableArray();
            return channel == VfxKeyChannel.Translation ? k with { Translation = outL } : k with { Scale = outL };
        });
    }
}
