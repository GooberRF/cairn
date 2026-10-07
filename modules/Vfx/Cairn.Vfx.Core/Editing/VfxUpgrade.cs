using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>
/// Converts any stock version to 0x40006 (after REDUX's VfxParser): inline materials become material sections
/// (appended in object order), legacy blobs are dropped, per-frame opacity becomes the material opacity track
/// (a shared material animated differently is duplicated), integer frame ranges become float times, legacy face UVs
/// become frame-0 UVs, and a pre-0x3000E keyframed mesh's frame-0 transform is folded into its pivot.
/// Byte-identical to REDUX's .vfx re-save for every older stock file except one documented engine-correct deviation:
/// a varying opacity ramp on a mesh with legacy start frame s > 0 gets s leading copies (material tracks have no start
/// offset). Header end frame kept; mesh end time = legacy end frame / fps; inline materials take the mesh fps and
/// self-illumination 0 before 0x30011 (all as REDUX).
/// </summary>
public static class VfxUpgrade
{
    public static VfxFile ToCurrent(VfxFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        int v = file.Version;
        if (v == VfxVersion.Current) return file;
        if (v > VfxVersion.Current || v < VfxVersion.Minimum) throw new InvalidOperationException($"Cannot upgrade version 0x{v:X}.");
        var mats = file.Sections.OfType<VfxMaterial>().Select(Material).ToList();
        var claimed = new Dictionary<int, ImmutableArray<float>>();
        int Claim(int idx, ImmutableArray<float>? curve)
        {
            if (curve is not { } c || idx < 0 || idx >= mats.Count) return idx;
            if (!claimed.TryGetValue(idx, out var have)) { claimed[idx] = c; mats[idx] = mats[idx] with { Opacity = c }; return idx; }
            if (have.SequenceEqual(c)) return idx;
            mats.Add(mats[idx] with { Opacity = c });
            claimed[mats.Count - 1] = c;
            return mats.Count - 1;
        }
        var outS = new List<VfxSection>();
        foreach (var s in file.Sections)
        {
            switch (s)
            {
                case VfxMesh m: outS.Add(Mesh(m, v, mats, Claim)); break;
                case VfxParticleSystem p: outS.Add(Particles(p, mats, Claim)); break;
                case VfxMaterial: break;
                default: outS.Add(s); break;
            }
        }
        outS.AddRange(mats);
        // The header end frame is kept (the effect's length as authored; REDUX keeps it too), not recomputed.
        return new VfxFile(VfxVersion.Current, file.HeaderFlags ?? 0, file.EndFrame, null, file.SelsetObjectCount ?? 0, [.. outS])
        { CameraFrameCount = file.CameraFrameCount };
    }

    private static ImmutableArray<float>? Curve(IEnumerable<float?> values) =>
        values.ToList() is var l && l.Count > 0 && l.All(x => x is not null) ? l.Select(x => x!.Value).ToImmutableArray() : null;

    private static VfxTexture? Tex(VfxTexture? t, int? legacyStart = null, int? legacyAnim = null) =>
        t is null ? null : new VfxTexture(t.Name, t.StartFrame ?? legacyStart ?? 0, t.PlaybackRate ?? 1f, t.AnimType ?? legacyAnim ?? 2);

    private static VfxMaterial Material(VfxMaterial m)
    {
        bool tex = m.Type is 0 or 1;
        return m with
        {
            Fps = m.Fps ?? 15, Additive = m.Additive ?? 0, Texture0 = Tex(m.Texture0), Texture1 = Tex(m.Texture1), LegacyFps = null,
            SpecularGlossReflection = tex ? m.SpecularGlossReflection ?? Vector3.Zero : null, ReflectionTexture = tex ? m.ReflectionTexture ?? "" : null,
            SolidColor = m.Type == 2 ? m.SolidColor ?? new VfxColorI(128, 128, 128) : null,
            SelfIllumination = m.SelfIllumination.IsDefaultOrEmpty ? [1f] : m.SelfIllumination, Opacity = m.Opacity ?? [1f],
        };
    }

    // Inline (pre-0x40000) materials play at their mesh's fps and had no self-illumination before 0x30011 (REDUX: 0).
    private static VfxMaterial Material(VfxInlineMaterial m, ImmutableArray<float>? opacity, int fps)
    {
        bool tex = m.Type is 0 or 1;
        return new VfxMaterial(m.Type, fps, m.Additive ?? 0, tex ? Tex(m.Texture0, m.LegacyStartFrame, m.LegacyAnimType) : null,
            m.Type == 1 ? Tex(m.Texture1, m.LegacyStartFrame, m.LegacyAnimType) : null, null, m.Type == 1 ? m.Mix ?? [] : null,
            tex ? m.SpecularGlossReflection ?? Vector3.Zero : null, tex ? m.ReflectionTexture ?? "" : null,
            m.Type == 2 ? m.SolidColor ?? new VfxColorI(128, 128, 128) : null, [m.SelfIllumination ?? 0f], opacity ?? [1f]);
    }

    private static VfxMesh Mesh(VfxMesh m, int v, List<VfxMaterial> mats, Func<int, ImmutableArray<float>?, int> claim)
    {
        int fps = m.Fps ?? 15;
        var opacity = Curve(m.Frames.Select(f => f.Opacity));
        // Per-frame mesh opacity is read at the mesh's local frame, a material track at effect time * fps with no start
        // offset: a mesh starting at frame s > 0 needs s leading copies of its first value (REDUX copies the curve as is,
        // which shifts a varying ramp s frames early; engine-correct deviation).
        if (opacity is { } oc && m.StartTime is null && m.StartFrame is int s0 && s0 > 0 &&oc.Any(x => x != oc[0]))
            opacity = Enumerable.Repeat(oc[0], s0).Concat(oc).ToImmutableArray();
        ImmutableArray<int> slots;
        var faces = m.Faces;
        if (m.InlineMaterials is { } inl)
        {
            slots = inl.Select(x => { mats.Add(Material(x, opacity, fps)); return mats.Count - 1; }).ToImmutableArray();
            faces = faces.Select(f => f with { MaterialIndex = f.MaterialIndex - 1 }).ToImmutableArray(); // 1-based before 0x40000
        }
        else slots = (m.MaterialIndices ?? []).Select(i => claim(i, opacity)).ToImmutableArray();

        var frames = m.Frames.ToList();
        if (m.Faces.Any(f => f.LegacyUvs is not null) && frames.Count > 0)
            frames[0] = frames[0] with { Uvs = m.Faces.SelectMany(f => f.LegacyUvs ?? ImmutableArray.Create(new Vector2[3])).ToImmutableArray() };
        if (m.LegacyFacingSize is { } fs) frames = frames.Select(f => f.Positions is null ? f : f with { FacingSize = f.FacingSize ?? fs }).ToList();
        faces = faces.Select(f => f.LegacyUvs is null ? f : f with { LegacyUvs = null }).ToImmutableArray();

        bool kf = m.IsKeyframed is 1;
        var pivot = m.Pivot ?? VfxBuilder.Identity;
        if (kf && VfxVersion.HasFirstFrameOnlyTransformForKeyframed(v) && frames.Count > 0 && frames[0].Transform is { } t0)
        {
            var r0 = Quaternion.Normalize(t0.Rotation);
            pivot = new VfxTransform(t0.Translation + Vector3.Transform(t0.Scale * pivot.Translation, r0),
                Quaternion.Normalize(r0 * Quaternion.Normalize(pivot.Rotation)), t0.Scale * pivot.Scale);
        }
        float start = m.StartTime ?? (m.StartFrame ?? 0) / (float)fps;
        // Legacy end frame / fps, as REDUX; the engine ignores end_time (visibility comes from num_frames).
        float end = m.EndTime ?? (m.EndFrame is int ef ? ef / (float)fps : VfxGeometry.EndTime(start, m.Frames.Length, fps));
        var up = new VfxMesh(m.Name, m.Parent, m.SaveParent, m.NumVertices, null, faces, fps, start, end, null, null, slots, null,
            m.BoundingCenter, m.BoundingRadius, null, m.Flags, null, m.FaceVertices, (byte)(kf ? 1 : 0), m.Frames,
            kf ? pivot : null, kf ? m.Keys ?? new VfxKeyLists([], [], []) : null);
        return VfxEdit.Relayout(up, m.Flags, kf, frames);
    }

    private static VfxParticleSystem Particles(VfxParticleSystem p, List<VfxMaterial> mats, Func<int, ImmutableArray<float>?, int> claim)
    {
        var opacity = Curve(p.Frames.Select(f => f.Opacity));
        int mi;
        if (p.InlineMaterial is { } im)
        {
            int type = im.Type ?? 2;
            bool tex = type is 0 or 1;
            mats.Add(new VfxMaterial(type, 15, im.Additive ?? 0, tex ? new VfxTexture(im.Texture0 ?? "", 0, im.Texture0Rate ?? 1, 2) : null,
                type == 1 ? new VfxTexture(im.Texture1 ?? "", 0, im.Texture1Rate ?? 1, 2) : null, null, type == 1 ? im.Mix ?? [] : null,
                tex ? Vector3.Zero : null, tex ? "" : null, type == 2 ? im.DropsColor ?? new VfxColorI(128, 128, 128) : null,
                [im.SelfIllumination ?? 1f], opacity ?? [1f]));
            mi = mats.Count - 1;
        }
        else mi = claim(p.MaterialIndex ?? 0, opacity);
        return p with
        {
            Flags = p.Flags ?? 0, MaterialIndex = mi, InlineMaterial = null, LegacyFlags = null, LegacyBlob = null,
            Shrink = p.Shrink ?? new Vector2((p.LegacyShrinkBirth ?? 0) / 100f, (p.LegacyShrinkDeath ?? 0) / 100f),
            LegacyShrinkBirth = null, LegacyShrinkDeath = null, Fade = p.Fade ?? Vector2.Zero,
            TailDistance = p.IsDrops ? p.TailDistance ?? 0f : null,
            Frames = p.Frames.Select(f => f.Opacity is null ? f : f with { Opacity = null }).ToImmutableArray(),
        };
    }
}
