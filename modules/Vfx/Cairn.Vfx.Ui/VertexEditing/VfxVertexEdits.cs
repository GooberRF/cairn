using System.Collections.Immutable;
using System.Numerics;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.VertexEditing;

/// <summary>
/// Pure vertex-mode edits. Viewport positions are in effect space (keyed/pivot or per-frame transform
/// applied, morph frames lerped); stored positions are in the mesh's own space. Every edit maps effect-space input
/// back through the inverse of the affine local-to-effect map sampled at the edit frame.
/// Delete/merge are implemented here rather than in Core for now.
/// </summary>
public static class VfxVertexEdits
{
    /// <summary>Reverses the winding of <paramref name="faces"/> only (corners 1/2, colours, face-vertex refs and per-corner UVs swapped, normal negated), like Core's whole-mesh <c>FlipWinding</c>. Its own inverse.</summary>
    public static VfxFile FlipFaces(VfxFile f, int section, IReadOnlyCollection<int> faces)
    {
        if (f.Sections[section] is not VfxMesh m || faces.Count == 0) return f;
        var set = faces.ToHashSet();
        var nf = m.Faces.Select((x, i) => !set.Contains(i) ? x : x with
        {
            V1 = x.V2, V2 = x.V1, Color1 = x.Color2, Color2 = x.Color1, FaceVertex1 = x.FaceVertex2, FaceVertex2 = x.FaceVertex1, Normal = -x.Normal,
        }).ToImmutableArray();
        var frames = m.Frames.Select(fr => fr.Uvs is { } uv && uv.Length == m.Faces.Length * 3
            ? fr with { Uvs = Enumerable.Range(0, uv.Length).Select(c => !set.Contains(c / 3) ? uv[c] : uv[c % 3 == 0 ? c : c % 3 == 1 ? c + 1 : c - 1]).ToImmutableArray() } : fr).ToImmutableArray();
        return f with { Sections = f.Sections.SetItem(section, m with { Faces = nf, Frames = frames }) };
    }

    /// <summary>UV frames edited by "this frame" scope: the stored frame being edited when it has UVs, else 0.</summary>
    public static int UvFrame(VfxMesh m, int editFrame) => (uint)editFrame < (uint)m.Frames.Length && m.Frames[editFrame].Uvs is not null ? editFrame : 0;

    /// <summary>Centre of the corner UVs of <paramref name="faces"/> on UV frame <paramref name="frame"/> (pivot for scale/rotate).</summary>
    public static Vector2 UvCentre(VfxMesh m, IReadOnlyCollection<int> faces, int frame)
    {
        if (m.Frames[frame].Uvs is not { } uv || uv.Length != m.Faces.Length * 3 || faces.Count == 0) return default;
        var c = faces.SelectMany(fc => new[] { uv[fc * 3], uv[fc * 3 + 1], uv[fc * 3 + 2] }).ToList();
        return c.Aggregate(Vector2.Zero, (a, b) => a + b) / c.Count;
    }

    /// <summary>Affine UV transform on the corners of <paramref name="faces"/> only, on UV frame <paramref name="frame"/> or every UV frame.</summary>
    public static VfxFile TransformFaceUvs(VfxFile f, int section, IReadOnlyCollection<int> faces, Matrix3x2 t, int frame, bool allFrames)
    {
        if (f.Sections[section] is not VfxMesh m || faces.Count == 0 || t.IsIdentity) return f;
        var set = faces.ToHashSet();
        var frames = m.Frames.Select((fr, k) => fr.Uvs is { } uv && uv.Length == m.Faces.Length * 3 && (allFrames || k == frame)
            ? fr with { Uvs = uv.Select((u, c) => set.Contains(c / 3) ? Vector2.Transform(u, t) : u).ToImmutableArray() } : fr).ToImmutableArray();
        return f with { Sections = f.Sections.SetItem(section, m with { Frames = frames }) };
    }

    /// <summary>Mesh ordinal (index into <c>VfxSampler.Meshes</c>) of a section, or -1.</summary>
    public static int Ordinal(VfxFile f, int section) =>
        (uint)section < (uint)f.Sections.Length && f.Sections[section] is VfxMesh ? VfxSections.OrdinalOf(f, section) : -1;

    /// <summary>Why vertex editing is unavailable for a section (null = available).</summary>
    public static string? Unavailable(VfxFile f, int section)
    {
        if (f.Version < VfxVersion.Current) return "Vertex editing needs a current-format file: convert it first.";
        if ((uint)section >= (uint)f.Sections.Length || f.Sections[section] is not VfxMesh m) return "Select a mesh to edit its vertices.";
        if ((m.Flags & (VfxMeshFlags.Facing | VfxMeshFlags.FacingRod)) != 0)
            return "Facing quads/rods: the game builds their corners from the facing size, the stored vertices are not drawn.";
        if (m.NumVertices == 0 || m.Frames.Length == 0 || m.Frames[0].Positions is null) return "This mesh has no stored vertex positions.";
        return null;
    }

    /// <summary>Local frame position of an effect frame (sampler convention: (frame/15 - start) x fps).</summary>
    public static float LocalFrame(VfxSampler s, int ordinal, float effectFrame)
    {
        var v = s.Meshes[ordinal];
        return (effectFrame / VfxTime.FramesPerSecond - v.StartSeconds) * v.Fps;
    }

    /// <summary>Effect frame at which a stored frame is shown exactly.</summary>
    public static float EffectFrameOf(VfxSampler s, int ordinal, int stored)
    {
        var v = s.Meshes[ordinal];
        return (stored / (float)Math.Max(1, v.Fps) + v.StartSeconds) * VfxTime.FramesPerSecond;
    }

    /// <summary>The stored geometry frame an edit at the playhead targets: nearest morph frame, else the first frame with positions.</summary>
    public static int EditFrame(VfxSampler s, int section, float effectFrame)
    {
        var m = (VfxMesh)s.File.Sections[section];
        if (!m.IsMorph) { for (int i = 0; i < m.Frames.Length; i++) if (m.Frames[i].Positions is not null) return i; return 0; }
        int k = (int)MathF.Round(LocalFrame(s, Ordinal(s.File, section), effectFrame));
        return Math.Clamp(k, 0, m.Frames.Length - 1);
    }

    /// <summary>Affine local-to-effect map at an effect frame (row-vector convention: effect = local x M).</summary>
    public static Matrix4x4 LocalToEffect(VfxSampler s, int section, float effectFrame)
    {
        var m = (VfxMesh)s.File.Sections[section];
        int ord = Ordinal(s.File, section);
        Func<Vector3, Vector3> map;
        if (s.Meshes[ord].IsKeyframed && m.Keys is { } keys)
        {
            float tick = effectFrame * VfxTime.TicksPerFrame;
            var pv = m.Pivot;
            Vector3 pt = pv?.Translation ?? Vector3.Zero, ps = pv?.Scale ?? Vector3.One;
            Quaternion pq = pv?.Rotation ?? Quaternion.Identity;
            Vector3 kt = VfxKeyframeMath.EvaluateVector(keys.Translation, tick, Vector3.Zero);
            Vector3 ks = VfxKeyframeMath.EvaluateVector(keys.Scale, tick, Vector3.One);
            Quaternion kq = VfxKeyframeMath.EvaluateRotation(keys.Rotation, tick);
            map = p => VfxKeyframeMath.ApplyKeyed(kt, kq, ks, pt, pq, ps, p);
        }
        else
        {
            var v = s.Meshes[ord];
            float local = Math.Max(0, LocalFrame(s, ord, effectFrame));
            int n = Math.Max(1, m.Frames.Length), i0 = Math.Min((int)MathF.Floor(local), n - 1), i1 = Math.Min(i0 + 1, n - 1);
            float frac = v.NoInterp || i1 == i0 ? 0 : Math.Clamp(local - i0, 0, 1);
            var t0 = m.Frames.Length > 0 ? m.Frames[i0].Transform : null;
            if (t0 is null) return Matrix4x4.Identity;
            var t1 = m.Frames[i1].Transform ?? t0;
            static Vector3 Apply(VfxTransform t, Vector3 p) => t.Translation + Vector3.Transform(t.Scale * p, t.Rotation);
            map = p => Vector3.Lerp(Apply(t0, p), Apply(t1, p), frac);
        }
        Vector3 o = map(Vector3.Zero), x = map(Vector3.UnitX) - o, y = map(Vector3.UnitY) - o, z = map(Vector3.UnitZ) - o;
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, o.X, o.Y, o.Z, 1);
    }

    /// <summary>Effect-space positions of a mesh's vertices at an effect frame (empty when inactive).</summary>
    public static Vector3[] EffectPositions(VfxSampler s, int section, float effectFrame)
    {
        int ord = Ordinal(s.File, section);
        var sample = new VfxMeshSample();
        if (ord < 0 || !s.SampleMesh(ord, effectFrame, sample)) return [];
        return sample.Positions.ToArray();
    }

    public static Vector3 Centroid(IReadOnlyList<Vector3> p, IEnumerable<int> verts)
    {
        Vector3 sum = Vector3.Zero; int n = 0;
        foreach (int v in verts) if ((uint)v < (uint)p.Count) { sum += p[v]; n++; }
        return n > 0 ? sum / n : Vector3.Zero;
    }

    // ---- picking (pure on projected screen points) ----

    /// <summary>Nearest vertex within <paramref name="radius"/> px of (px, py), front-most first (depth tie window 1e-3).</summary>
    public static int Pick(IReadOnlyList<(double X, double Y, double Depth, bool Visible)> screen, double px, double py, double radius = 8)
    {
        int best = -1; double bestDepth = double.MaxValue, bestDist = double.MaxValue;
        for (int i = 0; i < screen.Count; i++)
        {
            var q = screen[i];
            if (!q.Visible) continue;
            double d = Math.Sqrt((q.X - px) * (q.X - px) + (q.Y - py) * (q.Y - py));
            if (d > radius) continue;
            if (q.Depth < bestDepth - 1e-3 || Math.Abs(q.Depth - bestDepth) <= 1e-3 && d < bestDist) { best = i; bestDepth = q.Depth; bestDist = d; }
        }
        return best;
    }

    public static IEnumerable<int> InRect(IReadOnlyList<(double X, double Y, double Depth, bool Visible)> screen, double x0, double y0, double x1, double y1)
    {
        double l = Math.Min(x0, x1), r = Math.Max(x0, x1), t = Math.Min(y0, y1), b = Math.Max(y0, y1);
        for (int i = 0; i < screen.Count; i++)
            if (screen[i].Visible && screen[i].X >= l && screen[i].X <= r && screen[i].Y >= t && screen[i].Y <= b) yield return i;
    }

    // ---- topology helpers ----

    public static HashSet<int> Connected(VfxMesh m, IEnumerable<int> seed)
    {
        var set = seed.ToHashSet(); bool grew = true;
        while (grew) { int n = set.Count; set.UnionWith(Grow(m, set)); grew = set.Count > n; }
        return set;
    }

    public static HashSet<int> Grow(VfxMesh m, IReadOnlySet<int> sel)
    {
        var r = sel.ToHashSet();
        foreach (var f in m.Faces) if (sel.Contains(f.V0) || sel.Contains(f.V1) || sel.Contains(f.V2)) { r.Add(f.V0); r.Add(f.V1); r.Add(f.V2); }
        return r;
    }

    public static HashSet<int> Shrink(VfxMesh m, IReadOnlySet<int> sel)
    {
        var r = sel.ToHashSet();
        foreach (var f in m.Faces)
            if (!(sel.Contains(f.V0) && sel.Contains(f.V1) && sel.Contains(f.V2))) { r.Remove(f.V0); r.Remove(f.V1); r.Remove(f.V2); }
        return r;
    }

    /// <summary>Faces whose three vertices are all selected.</summary>
    public static int[] SelectedFaces(VfxMesh m, IReadOnlySet<int> sel) =>
        Enumerable.Range(0, m.Faces.Length).Where(i => sel.Contains(m.Faces[i].V0) && sel.Contains(m.Faces[i].V1) && sel.Contains(m.Faces[i].V2)).ToArray();

    // ---- geometry edits ----

    /// <summary>Moves vertices by an effect-space delta on the edit frame (or all geometry frames).</summary>
    public static VfxFile Move(VfxSampler s, int section, IReadOnlyCollection<int> verts, Vector3 effectDelta, float effectFrame, bool allFrames)
    {
        if (verts.Count == 0 || effectDelta == Vector3.Zero) return s.File;
        Matrix4x4.Invert(LocalToEffect(s, section, effectFrame), out var inv);
        var d = Vector3.TransformNormal(effectDelta, inv);
        return VfxEdit.MoveVertices(s.File, section, d, verts, allFrames ? null : EditFrame(s, section, effectFrame));
    }

    /// <summary>Applies an effect-space linear transform (scale/rotate) about the effect-space selection centroid.</summary>
    public static VfxFile Transform(VfxSampler s, int section, IReadOnlyCollection<int> verts, Matrix4x4 effectLinear, float effectFrame, bool allFrames)
    {
        if (verts.Count == 0) return s.File;
        var a = LocalToEffect(s, section, effectFrame);
        Matrix4x4.Invert(a, out var inv);
        var c = Centroid(EffectPositions(s, section, effectFrame), verts);
        var local = a * Matrix4x4.CreateTranslation(-c) * effectLinear * Matrix4x4.CreateTranslation(c) * inv;
        var m = (VfxMesh)s.File.Sections[section];
        int only = EditFrame(s, section, effectFrame);
        var file = s.File;
        for (int i = 0; i < m.Frames.Length; i++)
        {
            if (m.Frames[i].Positions is null || !allFrames && i != only) continue;
            var p = m.DecodePositions(i)!;
            foreach (int v in verts) p[v] = Vector3.Transform(p[v], local);
            file = VfxEdit.SetPositions(file, section, i, p);
        }
        return file;
    }

    /// <summary>Deletes the vertices and every face using them (unused vertices compacted; UVs and smoothing kept).</summary>
    public static VfxFile Delete(VfxFile f, int section, IReadOnlySet<int> verts) => Rebuild(f, section, v => verts.Contains(v) ? -2 : v, null);

    /// <summary>Merges the vertices into one at their centroid (per geometry frame); collapsed faces are removed.</summary>
    public static VfxFile Merge(VfxFile f, int section, IReadOnlyCollection<int> verts)
    {
        if (verts.Count < 2) return f;
        int target = verts.Min(); var set = verts.ToHashSet();
        return Rebuild(f, section, v => set.Contains(v) ? target : v, (p) => { var c = Centroid(p, set); p[target] = c; });
    }

    /// <summary>
    /// Shared rebuild: <paramref name="redirect"/> maps an old vertex to the old vertex it becomes (-2 = deleted);
    /// faces with a deleted or repeated vertex are dropped, unreferenced vertices compacted, derived data rebuilt.
    /// </summary>
    private static VfxFile Rebuild(VfxFile f, int section, Func<int, int> redirect, Action<Vector3[]>? perFrame)
    {
        var m = (VfxMesh)f.Sections[section];
        var keptFaces = new List<int>();
        for (int i = 0; i < m.Faces.Length; i++)
        {
            var fc = m.Faces[i]; int a = redirect(fc.V0), b = redirect(fc.V1), c = redirect(fc.V2);
            if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c) continue;
            keptFaces.Add(i);
        }
        var used = new SortedSet<int>();
        foreach (int i in keptFaces) { var fc = m.Faces[i]; used.Add(redirect(fc.V0)); used.Add(redirect(fc.V1)); used.Add(redirect(fc.V2)); }
        var newIndex = new Dictionary<int, int>(); foreach (int v in used) newIndex[v] = newIndex.Count;
        if (newIndex.Count < 3) throw new InvalidOperationException("The mesh would have no faces left.");
        var faces = keptFaces.Select(i =>
        {
            var fc = m.Faces[i];
            return fc with { V0 = newIndex[redirect(fc.V0)], V1 = newIndex[redirect(fc.V1)], V2 = newIndex[redirect(fc.V2)] };
        }).ToList();
        // Face-vertex records are preserved (vfx-format.md 6.1: the stock record count is not derivable from
        // (vertex, SG) pairs, and the engine's normals are per record): every kept corner keeps its old record,
        // remapped; records that lose all their faces are dropped. A merged-away vertex's record folds into the
        // target's record of the same smoothing group when there is one; only a corner without a usable record
        // (index out of range, or naming another vertex) gets a new (vertex, SG) record.
        var oldRecs = m.FaceVertices;
        var targetRec = new Dictionary<(int V, int Sg), int>();
        for (int r = 0; r < oldRecs.Length; r++)
        {
            int v = oldRecs[r].VertexIndex;
            if (v >= 0 && redirect(v) == v && newIndex.TryGetValue(v, out int nv)) targetRec.TryAdd((nv, oldRecs[r].SmoothingGroup), r);
        }
        var freshKeys = new List<(int V, int Sg, int Src)>(); var freshMap = new Dictionary<(int, int), int>();
        int Token(int rec, int oldV, int sg)
        {
            int nv = newIndex[redirect(oldV)];
            if ((uint)rec < (uint)oldRecs.Length && oldRecs[rec].VertexIndex == oldV)
                return redirect(oldV) == oldV || !targetRec.TryGetValue((nv, oldRecs[rec].SmoothingGroup), out int t) ? rec : t;
            if (!freshMap.TryGetValue((nv, sg), out int k)) { k = freshKeys.Count; freshMap[(nv, sg)] = k; freshKeys.Add((nv, sg, rec)); }
            return ~k;
        }
        var tokens = new int[faces.Count * 3];
        for (int j = 0; j < faces.Count; j++)
        {
            var of = m.Faces[keptFaces[j]];
            tokens[j * 3] = Token(of.FaceVertex0, of.V0, of.SmoothingGroup);
            tokens[j * 3 + 1] = Token(of.FaceVertex1, of.V1, of.SmoothingGroup);
            tokens[j * 3 + 2] = Token(of.FaceVertex2, of.V2, of.SmoothingGroup);
        }
        var keptRecs = new SortedSet<int>(tokens.Where(t => t >= 0));
        var recIndex = new Dictionary<int, int>(); foreach (int r in keptRecs) recIndex[r] = recIndex.Count;
        int Map(int t) => t >= 0 ? recIndex[t] : keptRecs.Count + ~t;
        var recList = keptRecs.Select(r => oldRecs[r] with { VertexIndex = newIndex[redirect(oldRecs[r].VertexIndex)] }).ToList();
        foreach (var (v, sg, src) in freshKeys)
            recList.Add(new VfxFaceVertex(sg, v, (uint)src < (uint)oldRecs.Length ? oldRecs[src].RawU : VfxGeometry.UnsetUv,
                (uint)src < (uint)oldRecs.Length ? oldRecs[src].RawV : VfxGeometry.UnsetUv, []));
        var newFaces = faces.Select((fc, j) => fc with { FaceVertex0 = Map(tokens[j * 3]), FaceVertex1 = Map(tokens[j * 3 + 1]), FaceVertex2 = Map(tokens[j * 3 + 2]) }).ToImmutableArray();
        var recArr = VfxGeometry.WithAdjacency(newFaces, recList.ToImmutableArray()).ToArray();
        var frames = m.Frames.Select(fr =>
        {
            if (fr.Positions is { } q)
            {
                var p = VfxPositionCodec.Decode(q);
                perFrame?.Invoke(p);
                var np = new Vector3[newIndex.Count]; foreach (var (o, n) in newIndex) np[n] = p[o];
                fr = fr with { Positions = VfxGeometry.Quantise(np) };
            }
            if (fr.Uvs is { } uv && uv.Length == m.Faces.Length * 3)
                fr = fr with { Uvs = keptFaces.SelectMany(i => new[] { uv[i * 3], uv[i * 3 + 1], uv[i * 3 + 2] }).ToImmutableArray() };
            return fr;
        }).ToImmutableArray();
        var mesh = VfxGeometry.RefreshShape(m with { NumVertices = newIndex.Count, Faces = newFaces, FaceVertices = recArr.ToImmutableArray(), Frames = frames });
        return f with { Sections = f.Sections.SetItem(section, mesh) };
    }
}
