using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>How <see cref="VfxEdit.SetFrameCount"/> fills frames.</summary>
public enum VfxFrameFill { Hold, Loop, Resample }

public static partial class VfxEdit
{
    /// <summary>Rebuilds every frame for (flags, keyframed): missing blocks are copied from frame 0 (or the previous transform / defaults), extra blocks dropped.</summary>
    internal static VfxMesh Relayout(VfxMesh m, uint flags, bool kf, IReadOnlyList<VfxMeshFrame>? frames = null)
    {
        var src = frames ?? m.Frames;
        var f0 = src[0];
        var outF = ImmutableArray.CreateBuilder<VfxMeshFrame>(src.Count);
        VfxTransform last = src.Select(f => f.Transform).FirstOrDefault(t => t is not null) ?? VfxBuilder.Identity;
        for (int i = 0; i < src.Count; i++)
        {
            var f = src[i];
            var l = VfxMesh.FrameLayout(VfxVersion.Current, flags, kf, i);
            if (f.Transform is not null) last = f.Transform;
            outF.Add(new VfxMeshFrame(
                l.Positions ? f.Positions ?? f0.Positions : null,
                l.FacingSize ? f.FacingSize ?? f0.FacingSize ?? Vector2.One : null,
                l.UpVector ? f.UpVector ?? m.Frames[0].UpVector ?? Vector3.UnitY : null,
                l.Uvs ? f.Uvs ?? f0.Uvs ?? m.Frames[0].Uvs : null,
                l.Transform ? last : null, null, null));
        }
        return m with
        {
            Flags = flags, IsKeyframed = (byte)(kf ? 1 : 0), Frames = outF.MoveToImmutable(),
            Pivot = kf ? m.Pivot ?? VfxBuilder.Identity : null, Keys = kf ? m.Keys ?? new VfxKeyLists([], [], []) : null,
            EndTime = src.Count == m.Frames.Length ? m.EndTime : VfxGeometry.EndTime(m.StartTime ?? 0f, src.Count, m.Fps ?? 15),
        };
    }

    private static bool Kf(VfxMesh m) => m.IsKeyframed is 1;

    private static VfxFile Mesh(VfxFile file, int index, Func<VfxMesh, VfxMesh> f) => Put(file, index, f(Get<VfxMesh>(file, index)));

    /// <summary>Sets mesh flags, re-laying out frames. The Morph bit must be unchanged (use ToMorph / ToStatic).</summary>
    public static VfxFile SetMeshFlags(VfxFile file, int index, uint flags) => Mesh(file, index, m =>
    {
        if (((flags ^ m.Flags) & VfxMeshFlags.Morph) != 0) throw new ArgumentException("Use ToMorph / ToStatic to change the Morph flag.");
        return flags == m.Flags ? m : Relayout(m, flags, Kf(m));
    });

    /// <summary>Sets fps keeping the frame count (end time recomputed).</summary>
    public static VfxFile SetFps(VfxFile file, int index, int fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        return Mesh(file, index, m => m.Fps == fps ? m : m with { Fps = fps, EndTime = VfxGeometry.EndTime(m.StartTime ?? 0, m.Frames.Length, fps) });
    }

    /// <summary>Sets the start time keeping the span (end time shifted).</summary>
    public static VfxFile SetStartTime(VfxFile file, int index, float start) => Mesh(file, index, m =>
        m.StartTime == start ? m : m with { StartTime = start, EndTime = start + (m.EndTime - m.StartTime) });

    /// <summary>
    /// Trims or extends frames. Hold repeats the last frame, Loop cycles, Resample stretches the source frames over the
    /// new count, interpolating between neighbours (morph positions and facing sizes lerped; per-frame transforms:
    /// translation/scale lerped, rotation slerped; UVs and other blocks from the nearer frame).
    /// </summary>
    public static VfxFile SetFrameCount(VfxFile file, int index, int count, VfxFrameFill fill = VfxFrameFill.Hold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return Mesh(file, index, m =>
        {
            int n = m.Frames.Length;
            if (count == n) return m;
            var decoded = new Vector3[]?[n];
            Vector3[]? Decode(int i) => decoded[i] ??= m.DecodePositions(i);
            VfxMeshFrame Resampled(int j)
            {
                float s = count == 1 ? 0 : j * (n - 1) / (float)(count - 1);
                int i0 = Math.Min((int)MathF.Floor(s), n - 1);
                float u = s - i0;
                if (u <= 0 || i0 + 1 >= n) return m.Frames[i0];
                VfxMeshFrame a = m.Frames[i0], b = m.Frames[i0 + 1], f = u < 0.5f ? a : b;
                if (a.Positions is not null && b.Positions is not null && !ReferenceEquals(a.Positions, b.Positions)
                    && Decode(i0) is { } pa && Decode(i0 + 1) is { } pb && pa.Length == pb.Length)
                    f = f with { Positions = VfxGeometry.Quantise(pa.Select((p, v) => Vector3.Lerp(p, pb[v], u)).ToArray()) };
                if (a.FacingSize is { } sa && b.FacingSize is { } sb) f = f with { FacingSize = Vector2.Lerp(sa, sb, u) };
                if (a.Transform is { } ta && b.Transform is { } tb && ta != tb)
                    f = f with
                    {
                        Transform = new VfxTransform(Vector3.Lerp(ta.Translation, tb.Translation, u),
                            Animation.VfxKeyframeMath.Slerp(ta.Rotation, tb.Rotation, u), Vector3.Lerp(ta.Scale, tb.Scale, u)),
                    };
                return f;
            }
            var frames = Enumerable.Range(0, count).Select(j => fill switch
            {
                VfxFrameFill.Loop => m.Frames[j % n],
                VfxFrameFill.Resample => Resampled(j),
                _ => m.Frames[Math.Min(j, n - 1)],
            }).ToList();
            return VfxGeometry.RefreshShape(Relayout(m, m.Flags, Kf(m), frames));
        });
    }

    /// <summary>Facing size on one geometry frame, or all when <paramref name="frame"/> is null.</summary>
    public static VfxFile SetFacingSize(VfxFile file, int index, Vector2 size, int? frame = null) => Mesh(file, index, m =>
    {
        if (frame is { } fi && (uint)fi >= (uint)m.Frames.Length) throw new ArgumentOutOfRangeException(nameof(frame));
        if (m.Frames[0].FacingSize is null) throw new ArgumentException("Mesh is not facing / facing rod.");
        return m with { Frames = m.Frames.Select((f, i) => f.FacingSize is not null && (frame is null || frame == i) && f.FacingSize != size ? f with { FacingSize = size } : f).ToImmutableArray() };
    });

    public static VfxFile SetUpVector(VfxFile file, int index, Vector3 up) => Mesh(file, index, m =>
        m.Frames[0].UpVector is null ? throw new ArgumentException("Mesh is not a facing rod.")
        : m.Frames[0].UpVector == up ? m : m with { Frames = m.Frames.SetItem(0, m.Frames[0] with { UpVector = up }) });

    public static VfxFile SetPivot(VfxFile file, int index, VfxTransform pivot) => Mesh(file, index, m =>
        !Kf(m) ? throw new ArgumentException("Only keyframed meshes have a pivot.") : m.Pivot == pivot ? m : m with { Pivot = pivot });

    /// <summary>Replaces the mesh's material slots (indices into the material sections); face slots must stay in range.</summary>
    public static VfxFile SetMaterialSlots(VfxFile file, int index, IReadOnlyList<int> materialIndices)
    {
        int mats = file.Sections.OfType<VfxMaterial>().Count();
        if (materialIndices.Any(i => i < 0 || i >= mats)) throw new ArgumentOutOfRangeException(nameof(materialIndices));
        return Mesh(file, index, m =>
        {
            if (m.Faces.Any(f => f.MaterialIndex >= materialIndices.Count)) throw new ArgumentException("A face uses a slot that would no longer exist.");
            return m.MaterialIndices!.Value.SequenceEqual(materialIndices) ? m : m with { MaterialIndices = [.. materialIndices] };
        });
    }

    /// <summary>Sets the material slot (-1 none) of the given faces (all when null).</summary>
    public static VfxFile SetFaceMaterial(VfxFile file, int index, int slot, IEnumerable<int>? faces = null) => Mesh(file, index, m =>
    {
        if (slot < -1 || slot >= m.MaterialCount) throw new ArgumentOutOfRangeException(nameof(slot));
        var set = FaceSet(m, faces);
        return set.All(f => m.Faces[f].MaterialIndex == slot) ? m
            : m with { Faces = m.Faces.Select((f, i) => set.Contains(i) ? f with { MaterialIndex = slot } : f).ToImmutableArray() };
    });

    /// <summary>
    /// Sets the smoothing group of the given faces (all when null). Faces outside the set keep their face-vertex records
    /// (vfx-format.md 6.1; review-findings-2 #4); a changed corner joins an untouched record of the same (vertex, group)
    /// when there is one, else a new (vertex, group) record. Records left without faces are dropped.
    /// </summary>
    public static VfxFile SetFaceSmoothing(VfxFile file, int index, int group, IEnumerable<int>? faces = null) => Mesh(file, index, m =>
    {
        var set = FaceSet(m, faces);
        if (set.All(f => m.Faces[f].SmoothingGroup == group)) return m;
        var old = m.FaceVertices;
        bool Valid(int r, int v) => (uint)r < (uint)old.Length && old[r].VertexIndex == v;
        var shared = new Dictionary<(int V, int Sg), int>();
        for (int i = 0; i < m.Faces.Length; i++)
        {
            if (set.Contains(i)) continue;
            var f = m.Faces[i];
            foreach (var (r, v) in new[] { (f.FaceVertex0, f.V0), (f.FaceVertex1, f.V1), (f.FaceVertex2, f.V2) })
                if (Valid(r, v)) shared.TryAdd((v, old[r].SmoothingGroup), r);
        }
        var fresh = new List<(int V, int Sg, int Src)>(); var freshMap = new Dictionary<(int, int), int>();
        int Token(int r, int v, int sg, bool changed)
        {
            if (!changed && Valid(r, v)) return r;
            if (changed && shared.TryGetValue((v, sg), out int s)) return s;
            if (!freshMap.TryGetValue((v, sg), out int k)) { k = fresh.Count; freshMap[(v, sg)] = k; fresh.Add((v, sg, r)); }
            return ~k;
        }
        var tokens = new int[m.Faces.Length * 3];
        for (int i = 0; i < m.Faces.Length; i++)
        {
            var f = m.Faces[i]; bool c = set.Contains(i); int sg = c ? group : f.SmoothingGroup;
            tokens[i * 3] = Token(f.FaceVertex0, f.V0, sg, c);
            tokens[i * 3 + 1] = Token(f.FaceVertex1, f.V1, sg, c);
            tokens[i * 3 + 2] = Token(f.FaceVertex2, f.V2, sg, c);
        }
        var kept = new SortedSet<int>(tokens.Where(t => t >= 0));
        var map = new Dictionary<int, int>(); foreach (int r in kept) map[r] = map.Count;
        int Map(int t) => t >= 0 ? map[t] : kept.Count + ~t;
        var recs = kept.Select(r => old[r]).ToList();
        foreach (var (v, sg, src) in fresh)
            recs.Add(new VfxFaceVertex(sg, v, (uint)src < (uint)old.Length ? old[src].RawU : VfxGeometry.UnsetUv,
                (uint)src < (uint)old.Length ? old[src].RawV : VfxGeometry.UnsetUv, []));
        var fs = m.Faces.Select((f, i) => f with
        {
            SmoothingGroup = set.Contains(i) ? group : f.SmoothingGroup,
            FaceVertex0 = Map(tokens[i * 3]), FaceVertex1 = Map(tokens[i * 3 + 1]), FaceVertex2 = Map(tokens[i * 3 + 2]),
        }).ToImmutableArray();
        return m with { Faces = fs, FaceVertices = VfxGeometry.WithAdjacency(fs, recs.ToImmutableArray()) };
    });

    private static HashSet<int> FaceSet(VfxMesh m, IEnumerable<int>? faces)
    {
        var set = (faces ?? Enumerable.Range(0, m.Faces.Length)).ToHashSet();
        if (set.Any(f => (uint)f >= (uint)m.Faces.Length)) throw new ArgumentOutOfRangeException(nameof(faces));
        return set;
    }

    // ---- geometry ----

    /// <summary>Sets the positions of one geometry frame (requantised; face shapes and bounds refreshed).</summary>
    public static VfxFile SetPositions(VfxFile file, int index, int frame, IReadOnlyList<Vector3> positions) => Mesh(file, index, m =>
    {
        if ((uint)frame >= (uint)m.Frames.Length || m.Frames[frame].Positions is null) throw new ArgumentOutOfRangeException(nameof(frame));
        if (positions.Count != m.NumVertices) throw new ArgumentException($"Need {m.NumVertices} positions.");
        var q = VfxGeometry.Quantise(positions.ToArray());
        var old = m.Frames[frame].Positions!;
        if (old.Center == q.Center && old.Multiplier == q.Multiplier && old.Raw.SequenceEqual(q.Raw)) return m;
        return VfxGeometry.RefreshShape(m with { Frames = m.Frames.SetItem(frame, m.Frames[frame] with { Positions = q }) });
    });

    /// <summary>Adds <paramref name="delta"/> to the given vertices (all when null) on one frame or every geometry frame.</summary>
    public static VfxFile MoveVertices(VfxFile file, int index, Vector3 delta, IEnumerable<int>? vertices = null, int? frame = null)
    {
        var m = Get<VfxMesh>(file, index);
        if (delta == Vector3.Zero) return file;
        var set = (vertices ?? Enumerable.Range(0, m.NumVertices)).ToHashSet();
        if (set.Any(v => (uint)v >= (uint)m.NumVertices)) throw new ArgumentOutOfRangeException(nameof(vertices));
        for (int i = 0; i < m.Frames.Length; i++)
        {
            if (m.Frames[i].Positions is null || frame is { } fi && fi != i) continue;
            var p = m.DecodePositions(i)!;
            foreach (int v in set) p[v] += delta;
            file = SetPositions(file, index, i, p);
        }
        return file;
    }

    /// <summary>Inserts a morph frame at <paramref name="at"/> (copy of the previous frame unless positions are given).</summary>
    public static VfxFile AddMorphFrame(VfxFile file, int index, int at, IReadOnlyList<Vector3>? positions = null)
    {
        file = Mesh(file, index, m =>
        {
            if (!m.IsMorph) throw new ArgumentException("Mesh is not a morph mesh.");
            if (at < 1 || at > m.Frames.Length) throw new ArgumentOutOfRangeException(nameof(at));
            return Relayout(m, m.Flags, Kf(m), m.Frames.Insert(at, m.Frames[at - 1]));
        });
        return positions is null ? Mesh(file, index, VfxGeometry.RefreshShape) : SetPositions(file, index, at, positions);
    }

    public static VfxFile RemoveMorphFrame(VfxFile file, int index, int at) => Mesh(file, index, m =>
    {
        if (!m.IsMorph) throw new ArgumentException("Mesh is not a morph mesh.");
        if (at < 0 || at >= m.Frames.Length || m.Frames.Length == 1) throw new ArgumentOutOfRangeException(nameof(at));
        return VfxGeometry.RefreshShape(Relayout(m, m.Flags, Kf(m), m.Frames.RemoveAt(at)));
    });

    /// <summary>
    /// Static -> morph: every frame gets frame 0's geometry with that frame's transform baked in (per-frame transform,
    /// or the keys sampled at <see cref="FrameTick"/> over the pivot), so the sampled positions are unchanged at frame
    /// times. The result is not keyframed and carries no transforms; facing sizes take the per-frame |scale.x|, |scale.z|
    /// as the sampler applies them.
    /// </summary>
    public static VfxFile ToMorph(VfxFile file, int index) => Mesh(file, index, m =>
    {
        if (m.IsMorph) return m;
        var p0 = m.DecodePositions(0);
        bool kf = Kf(m);
        var pv = m.Pivot ?? VfxBuilder.Identity;
        var f0 = m.Frames[0];
        bool baked = false;
        var frames = new List<VfxMeshFrame>(m.Frames.Length);
        for (int i = 0; i < m.Frames.Length; i++)
        {
            var f = m.Frames[i];
            var size = f.FacingSize ?? f0.FacingSize;
            VfxCompressedPositions? q = f0.Positions;
            if (p0 is not null && kf)
            {
                var k = SampleKeys(m.Keys!, FrameTick(m, i));
                q = VfxGeometry.Quantise(p0.Select(p => Animation.VfxKeyframeMath.ApplyKeyed(k.Translation, k.Rotation, k.Scale,
                    pv.Translation, pv.Rotation, pv.Scale, p)).ToArray());
                baked = true;
            }
            else if (f.Transform is { } t && t != VfxBuilder.Identity)
            {
                if (p0 is not null) q = VfxGeometry.Quantise(p0.Select(p => Animation.VfxSampler.Apply(t, p)).ToArray());
                if (size is { X: > 0 } sz) size = new Vector2(sz.X * MathF.Abs(t.Scale.X), sz.Y * MathF.Abs(t.Scale.Z));
                baked = true;
            }
            frames.Add(f with { Positions = q, FacingSize = size });
        }
        var outM = Relayout(m with { Keys = null, Pivot = null }, m.Flags | VfxMeshFlags.Morph, false, frames);
        return baked ? VfxGeometry.RefreshShape(outM) : outM;
    });

    /// <summary>Morph -> static keeping the geometry of <paramref name="keepFrame"/>; frames get the identity transform.</summary>
    public static VfxFile ToStatic(VfxFile file, int index, int keepFrame = 0) => Mesh(file, index, m =>
    {
        if (!m.IsMorph) return m;
        if ((uint)keepFrame >= (uint)m.Frames.Length) throw new ArgumentOutOfRangeException(nameof(keepFrame));
        var frames = m.Frames.SetItem(0, m.Frames[0] with { Positions = m.Frames[keepFrame].Positions });
        return VfxGeometry.RefreshShape(Relayout(m, m.Flags & ~VfxMeshFlags.Morph, Kf(m), frames));
    });

    /// <summary>Applies an affine UV transform on frame 0 or every UV frame.</summary>
    public static VfxFile TransformUvs(VfxFile file, int index, Matrix3x2 transform, bool allFrames = true) => Mesh(file, index, m =>
        transform.IsIdentity ? m : m with
        {
            Frames = m.Frames.Select((f, i) => f.Uvs is { } uv && (allFrames || i == 0)
                ? f with { Uvs = uv.Select(u => Vector2.Transform(u, transform)).ToImmutableArray() } : f).ToImmutableArray(),
        });

    public static VfxFile OffsetUvs(VfxFile file, int index, Vector2 offset, bool allFrames = true) =>
        TransformUvs(file, index, Matrix3x2.CreateTranslation(offset), allFrames);

    public static VfxFile ScaleUvs(VfxFile file, int index, Vector2 scale, Vector2 center = default, bool allFrames = true) =>
        TransformUvs(file, index, Matrix3x2.CreateScale(scale, center), allFrames);

    public static VfxFile RotateUvs(VfxFile file, int index, float radians, Vector2 center = default, bool allFrames = true) =>
        TransformUvs(file, index, Matrix3x2.CreateRotation(radians, center), allFrames);

    /// <summary>Sets DumpUvs and gives frame k the frame-0 UVs plus k * <paramref name="perFrame"/>.</summary>
    public static VfxFile GenerateUvScroll(VfxFile file, int index, Vector2 perFrame) => Mesh(file, index, m =>
    {
        var uv0 = m.Frames[0].Uvs!.Value;
        var frames = m.Frames.Select((f, k) => f with { Uvs = uv0.Select(u => u + k * perFrame).ToImmutableArray() }).ToList();
        return Relayout(m, m.Flags | VfxMeshFlags.DumpUvs, Kf(m), frames);
    });

    /// <summary>Reverses every face's winding (corners 1 and 2 swapped, normal negated). Its own inverse.</summary>
    public static VfxFile FlipWinding(VfxFile file, int index) => Mesh(file, index, m => m with
    {
        Faces = m.Faces.Select(f => f with
        {
            V1 = f.V2, V2 = f.V1, Color1 = f.Color2, Color2 = f.Color1, FaceVertex1 = f.FaceVertex2, FaceVertex2 = f.FaceVertex1, Normal = -f.Normal,
        }).ToImmutableArray(),
        Frames = m.Frames.Select(f => f.Uvs is { } uv
            ? f with { Uvs = Enumerable.Range(0, uv.Length).Select(c => uv[c % 3 == 0 ? c : c % 3 == 1 ? c + 1 : c - 1]).ToImmutableArray() } : f).ToImmutableArray(),
    });
}
