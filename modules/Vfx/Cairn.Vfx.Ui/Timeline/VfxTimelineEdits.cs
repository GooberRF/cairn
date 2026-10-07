using System.Collections.Immutable;
using System.Numerics;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Ui.Timeline;

/// <summary>How a mesh is animated (decides what timeline rows and gizmo drags edit).</summary>
public enum VfxAnimKind { None, Static, PerFrame, Keyframed, Morph }

/// <summary>Pure timeline/gizmo edits on <see cref="VfxFile"/> (every method returns a new file; used inside one undo step).</summary>
public static class VfxTimelineEdits
{
    public const int TicksPerFrame = 320;

    public static VfxAnimKind KindOf(VfxSection s) => s switch
    {
        VfxMesh m when m.Keys is not null => VfxAnimKind.Keyframed,
        VfxMesh m when m.IsMorph => VfxAnimKind.Morph,
        VfxMesh m when m.Frames.Length > 1 && m.Frames[0].Transform is not null => VfxAnimKind.PerFrame,
        VfxMesh m when m.Frames.Length > 0 && m.Frames[0].Transform is not null => VfxAnimKind.Static,
        _ => VfxAnimKind.None,
    };

    public static int Fps(VfxMesh m) => m.Fps is > 0 ? m.Fps.Value : 15;

    /// <summary>The object's active range in effect frames (start, length), or null when it has none.</summary>
    public static (float Start, float Length)? Range(VfxSection s) => s switch
    {
        VfxMesh m => ((m.StartTime ?? (m.StartFrame ?? 0) / (float)Fps(m)) * 15f, Math.Max(1, m.Frames.Length) * 15f / Fps(m)),
        VfxParticleSystem p => (p.StartTime, Math.Max(1, p.Frames.Length)),
        VfxDummy d => (0, Math.Max(1, d.Frames.Length)),
        VfxLight l => (0, Math.Max(1, l.Frames.Length)),
        VfxSpacewarp w => (0, Math.Max(1, w.Frames.Length)),
        _ => null,
    };

    /// <summary>Object-local frame index shown at effect frame <paramref name="frame"/> (clamped).</summary>
    public static int LocalFrame(VfxSection s, float frame)
    {
        if (Range(s) is not { } r) return 0;
        int count = s switch { VfxMesh m => m.Frames.Length, VfxParticleSystem p => p.Frames.Length, VfxDummy d => d.Frames.Length, VfxLight l => l.Frames.Length, VfxSpacewarp w => w.Frames.Length, _ => 1 };
        float fpsScale = s is VfxMesh mm ? Fps(mm) / 15f : 1f;
        return Math.Clamp((int)MathF.Floor((frame - r.Start) * fpsScale + 1e-3f), 0, Math.Max(0, count - 1));
    }

    /// <summary>Moves the start of the range to effect frame <paramref name="start"/> (meshes: seconds; particles: frames).</summary>
    public static VfxFile MoveRange(VfxFile f, int i, float start) => f.Sections[i] switch
    {
        VfxMesh => VfxEdit.SetStartTime(f, i, Math.Max(0, start) / 15f),
        VfxParticleSystem => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { StartTime = Math.Max(0, (int)MathF.Round(start)) }),
        _ => f,
    };

    /// <summary>Sets the frame count from a length in effect frames (trim, or hold-extend the last frame).</summary>
    public static VfxFile ResizeRange(VfxFile f, int i, float lengthFrames) => f.Sections[i] switch
    {
        VfxMesh m => VfxEdit.SetFrameCount(f, i, Math.Max(1, (int)MathF.Round(lengthFrames * Fps(m) / 15f)), VfxFrameFill.Hold),
        VfxParticleSystem or VfxDummy or VfxLight or VfxSpacewarp => VfxEdit.ResizeObjectFrames(f, i, Math.Max(1, (int)MathF.Round(lengthFrames))),
        _ => f,
    };

    public static ImmutableArray<int> KeyTimes(VfxMesh m, VfxKeyChannel ch) => m.Keys is not { } k ? [] : ch switch
    {
        VfxKeyChannel.Translation => [.. k.Translation.Select(x => x.Time)],
        VfxKeyChannel.Rotation => [.. k.Rotation.Select(x => x.Time)],
        _ => [.. k.Scale.Select(x => x.Time)],
    };

    /// <summary>Moves keys by <paramref name="deltaTicks"/> (keys landing on another key replace it; order of moves avoids collisions within the set).</summary>
    public static VfxFile MoveKeys(VfxFile f, IEnumerable<(int Section, VfxKeyChannel Channel, int Time)> keys, int deltaTicks)
    {
        if (deltaTicks == 0) return f;
        var ordered = deltaTicks > 0 ? keys.OrderByDescending(k => k.Time) : keys.OrderBy(k => k.Time);
        foreach (var k in ordered) f = VfxEdit.MoveKey(f, k.Section, k.Channel, k.Time, Math.Max(0, k.Time + deltaTicks));
        return f;
    }

    public static VfxFile DeleteKeys(VfxFile f, IEnumerable<(int Section, VfxKeyChannel Channel, int Time)> keys)
    {
        foreach (var k in keys)
        {
            if (f.Sections[k.Section] is not VfxMesh { Keys: { } kl }) continue;
            int count = k.Channel switch { VfxKeyChannel.Translation => kl.Translation.Length, VfxKeyChannel.Rotation => kl.Rotation.Length, _ => kl.Scale.Length };
            if (count > 1) f = VfxEdit.DeleteKey(f, k.Section, k.Channel, k.Time); // a channel keeps at least one key
        }
        return f;
    }

    /// <summary>Inserts (or overwrites) a key at <paramref name="tick"/> with the current sampled value; tangents by <see cref="VfxEdit.AutoTangents"/>.</summary>
    public static VfxFile InsertKey(VfxFile f, int i, VfxKeyChannel ch, int tick, Func<VfxTransform, VfxTransform>? change = null)
    {
        if (f.Sections[i] is not VfxMesh { Keys: { } keys }) return f;
        var t = VfxEdit.SampleKeys(keys, (float)tick);
        if (change is not null) t = change(t);
        f = ch switch
        {
            VfxKeyChannel.Rotation => VfxEdit.SetRotationKey(f, i, new VfxRotationKey(tick, t.Rotation, 0, 0, 0, 0, 0)),
            VfxKeyChannel.Translation => VfxEdit.SetKey(f, i, ch, new VfxVectorKey(tick, t.Translation, t.Translation, t.Translation)),
            _ => VfxEdit.SetKey(f, i, ch, new VfxVectorKey(tick, t.Scale, t.Scale, t.Scale)),
        };
        return ch == VfxKeyChannel.Rotation ? f : VfxEdit.AutoTangents(f, i, ch);
    }

    /// <summary>Copies keys (values relative to the earliest) and pastes them at <paramref name="tick"/>.</summary>
    public static VfxFile PasteKeys(VfxFile f, IReadOnlyList<(int Section, VfxKeyChannel Channel, object Key)> clip, int tick)
    {
        if (clip.Count == 0) return f;
        int first = clip.Min(c => c.Key is VfxVectorKey v ? v.Time : ((VfxRotationKey)c.Key).Time);
        foreach (var (s, ch, key) in clip)
        {
            if ((uint)s >= (uint)f.Sections.Length || f.Sections[s] is not VfxMesh { Keys: not null }) continue;
            if (key is VfxVectorKey v)
            {
                int to = tick + v.Time - first;
                f = VfxEdit.SetKey(f, s, ch, v with { Time = to });
            }
            else if (key is VfxRotationKey r) f = VfxEdit.SetRotationKey(f, s, r with { Time = tick + r.Time - first });
        }
        return f;
    }

    public static IEnumerable<(int Section, VfxKeyChannel Channel, object Key)> CopyKeys(VfxFile f, IEnumerable<(int Section, VfxKeyChannel Channel, int Time)> keys)
    {
        foreach (var (s, ch, t) in keys)
        {
            if (f.Sections[s] is not VfxMesh { Keys: { } k }) continue;
            object? key = ch switch
            {
                VfxKeyChannel.Translation => k.Translation.FirstOrDefault(x => x.Time == t),
                VfxKeyChannel.Rotation => k.Rotation.FirstOrDefault(x => x.Time == t),
                _ => k.Scale.FirstOrDefault(x => x.Time == t),
            };
            if (key is not null) yield return (s, ch, key);
        }
    }

    /// <summary>
    /// Applies a gizmo delta (world translation <paramref name="move"/>, world rotation <paramref name="turn"/>) to
    /// section <paramref name="i"/> at effect frame <paramref name="frame"/>. Keyframed meshes: auto-key on = set/insert
    /// keys at the frame tick; off = offset every key of the channel. Per-frame data: this frame, or every frame with
    /// <paramref name="allFrames"/>. Morph meshes move vertices (rotation is not offered for them).
    /// </summary>
    public static VfxFile ApplyGizmo(VfxFile f, int i, float frame, Vector3 move, Quaternion turn, bool autoKey, bool allFrames)
    {
        bool moves = move != Vector3.Zero, turns = !turn.IsIdentity;
        var s = f.Sections[i];
        int local = LocalFrame(s, frame);
        int? fi = allFrames ? null : local;
        Vector3 P(Vector3 p) => p + move;
        Quaternion R(Quaternion q) => Quaternion.Normalize(turn * q);
        switch (s)
        {
            case VfxMesh m when m.Keys is { } keys:
                int tick = (int)MathF.Round(frame * TicksPerFrame);
                if (autoKey)
                {
                    if (moves) f = InsertKey(f, i, VfxKeyChannel.Translation, tick, t => t with { Translation = P(t.Translation) });
                    if (turns) f = InsertKey(f, i, VfxKeyChannel.Rotation, tick, t => t with { Rotation = R(t.Rotation) });
                    return f;
                }
                var nk = keys with
                {
                    Translation = moves ? [.. keys.Translation.Select(k => k with { Value = P(k.Value), InTangent = P(k.InTangent), OutTangent = P(k.OutTangent) })] : keys.Translation,
                    Rotation = turns ? [.. keys.Rotation.Select(k => k with { Value = R(k.Value) })] : keys.Rotation,
                };
                return VfxEdit.Update<VfxMesh>(f, i, mm => mm with { Keys = nk });
            case VfxMesh m when m.IsMorph:
            {
                if (!moves) return f;
                f = VfxEdit.MoveVertices(f, i, move, null, fi);
                // between two morph frames the sampler blends both: move the next one too so the shape at the playhead follows the drag
                float lf = Range(s) is { } rg ? (float)((frame - rg.Start) * Fps(m) / 15.0) : 0;
                int next = (int)MathF.Floor(lf) + 1;
                return fi is { } a && next == a + 1 && lf - MathF.Floor(lf) > 1e-4f && next < m.Frames.Length ? VfxEdit.MoveVertices(f, i, move, null, next) : f;
            }
            case VfxMesh m when m.Frames.Length > 0 && m.Frames[0].Transform is { } t0:
                if (m.Frames.Length == 1) fi = null;
                for (int k = 0; k < m.Frames.Length; k++)
                {
                    if (fi is { } only && only != k) continue;
                    var t = m.Frames[k].Transform!;
                    f = VfxEdit.SetStaticTransform(f, i, t with { Translation = P(t.Translation), Rotation = turns ? R(t.Rotation) : t.Rotation }, k);
                }
                return f;
            case VfxDummy:
                return VfxEdit.SetObjectFrame<VfxDummyFrame>(f, i, d => d with { Position = P(d.Position), Orientation = turns ? R(d.Orientation) : d.Orientation }, fi);
            case VfxParticleSystem:
                return VfxEdit.SetObjectFrame<VfxParticleFrame>(f, i, d => d with { Position = P(d.Position), Orientation = turns ? R(d.Orientation) : d.Orientation }, fi);
            case VfxSpacewarp:
                return VfxEdit.SetObjectFrame<VfxSpacewarpFrame>(f, i, d => d with { Position = P(d.Position), Orientation = turns ? R(d.Orientation) : d.Orientation }, fi);
            case VfxLight:
                return moves ? VfxEdit.SetObjectFrame<VfxLightParams>(f, i, d => d with { Position = P(d.Position) }, fi) : f;
        }
        return f;
    }

    /// <summary>
    /// Scales section <paramref name="i"/> at effect frame <paramref name="frame"/> by <paramref name="scale"/> (component-wise,
    /// in the object's axes). Keyframed meshes: auto-key on = set/insert a scale key at the frame tick; off = multiply every
    /// scale key (value and tangent control points). Static / per-frame transforms: Scale of this frame (all with
    /// <paramref name="allFrames"/>). Morph meshes: vertices of this frame (or all) about <paramref name="centre"/>.
    /// Facing quads / rods: width (X) and height (Y) of the facing size. Other objects have no scale: unchanged.
    /// </summary>
    public static VfxFile ApplyScale(VfxFile f, int i, float frame, Vector3 scale, Vector3 centre, bool autoKey, bool allFrames)
    {
        if (scale == Vector3.One) return f;
        var s = f.Sections[i];
        int local = LocalFrame(s, frame);
        int? fi = allFrames ? null : local;
        Vector3 S(Vector3 v) => v * scale;
        switch (s)
        {
            case VfxMesh m when m.Keys is { } keys:
                int tick = (int)MathF.Round(frame * TicksPerFrame);
                if (autoKey) return InsertKey(f, i, VfxKeyChannel.Scale, tick, t => t with { Scale = S(t.Scale) });
                var nk = keys with { Scale = [.. keys.Scale.Select(k => k with { Value = S(k.Value), InTangent = S(k.InTangent), OutTangent = S(k.OutTangent) })] };
                return VfxEdit.Update<VfxMesh>(f, i, mm => mm with { Keys = nk });
            case VfxMesh m when m.Frames.Length > 0 && m.Frames[0].FacingSize is not null:
            {
                if (m.Frames.Length == 1) fi = null;
                int k0 = Math.Clamp(fi ?? 0, 0, m.Frames.Length - 1);
                var size = m.Frames[k0].FacingSize!.Value * new Vector2(scale.X, scale.Y);
                return VfxEdit.SetFacingSize(f, i, size, fi);
            }
            case VfxMesh m when m.IsMorph:
                for (int k = 0; k < m.Frames.Length; k++)
                {
                    if (fi is { } only && only != k || m.Frames[k].Positions is null) continue;
                    var p = m.DecodePositions(k)!;
                    for (int v = 0; v < p.Length; v++) p[v] = centre + (p[v] - centre) * scale;
                    f = VfxEdit.SetPositions(f, i, k, p);
                }
                return f;
            case VfxMesh m when m.Frames.Length > 0 && m.Frames[0].Transform is not null:
                if (m.Frames.Length == 1) fi = null;
                for (int k = 0; k < m.Frames.Length; k++)
                {
                    if (fi is { } only && only != k) continue;
                    var t = m.Frames[k].Transform!;
                    f = VfxEdit.SetStaticTransform(f, i, t with { Scale = S(t.Scale) }, k);
                }
                return f;
        }
        return f;
    }

    /// <summary>The object's origin and orientation at effect frame <paramref name="frame"/> (no parent composition, as the sampler).</summary>
    public static bool TryGetPlacement(VfxFile f, int i, float frame, out Vector3 centre, out Quaternion rot)
    {
        centre = default; rot = Quaternion.Identity;
        if ((uint)i >= (uint)f.Sections.Length) return false;
        var s = f.Sections[i];
        int local = LocalFrame(s, frame);
        switch (s)
        {
            case VfxMesh { Keys: { } keys }:
                var t = VfxEdit.SampleKeys(keys, frame * TicksPerFrame);
                centre = t.Translation; rot = t.Rotation; return true;
            case VfxMesh m when m.Frames.Length > 0 && m.Frames[Math.Min(local, m.Frames.Length - 1)].Transform is { } tr:
                centre = tr.Translation; rot = tr.Rotation; return true;
            case VfxMesh m when m.IsMorph && Range(s) is { } mr:
            {
                // morph: bounding-box centre of the shape the sampler shows (two frames blended linearly)
                float lf = Math.Max(0, (float)((frame - mr.Start) * Fps(m) / 15.0));
                int a = Math.Min((int)MathF.Floor(lf), m.Frames.Length - 1), b2 = Math.Min(a + 1, m.Frames.Length - 1);
                Vector3? C(int k) => m.DecodePositions(k) is { Length: > 0 } ps
                    ? (ps.Aggregate(Vector3.Min) + ps.Aggregate(Vector3.Max)) / 2 : null;
                centre = C(a) is { } ca ? (C(b2) is { } cb ? Vector3.Lerp(ca, cb, Math.Clamp(lf - a, 0, 1)) : ca) : m.BoundingCenter;
                return true;
            }
            case VfxMesh m:
                centre = m.BoundingCenter; return true;
            case VfxDummy d when d.Frames.Length > 0: centre = d.Frames[local].Position; rot = d.Frames[local].Orientation; return true;
            case VfxParticleSystem p when p.Frames.Length > 0: centre = p.Frames[local].Position; rot = p.Frames[local].Orientation; return true;
            case VfxSpacewarp w when w.Frames.Length > 0: centre = w.Frames[local].Position; rot = w.Frames[local].Orientation; return true;
            case VfxLight l when l.Frames.Length > 0: centre = l.Frames[local].Position; return true;
        }
        return false;
    }
}
