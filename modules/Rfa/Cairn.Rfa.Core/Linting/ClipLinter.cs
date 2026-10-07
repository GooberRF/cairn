using System.Globalization;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Linting;

/// <summary>One way the game's tables use a clip, with the mesh it plays on (for RFA013).</summary>
/// <param name="ClassName">The entity class or weapon.</param>
/// <param name="Table">The table file, e.g. "entity.tbl".</param>
/// <param name="SlotName">The state or action name.</param>
/// <param name="IsState">True for a <c>+State:</c>, false for a <c>+Action:</c>.</param>
/// <param name="MeshName">The mesh the class plays it on, when known.</param>
/// <param name="MeshBoneCount">That mesh's bone count, when the library could probe it.</param>
public sealed record ClipTableUse(string ClassName, string Table, string SlotName, bool IsState, string? MeshName, int? MeshBoneCount);

/// <summary>
/// Everything the clip rules can use besides the clip. Every member is optional: a rule whose context
/// is missing simply does not run. The App fills this asynchronously (preview mesh, tables, library)
/// and re-lints; the structural rules need none of it.
/// </summary>
public sealed record ClipLintContext
{
    /// <summary>No context: structural rules only.</summary>
    public static ClipLintContext None { get; } = new();

    /// <summary>The clip's file name (with extension), for RFA014 and messages.</summary>
    public string? FileName { get; init; }

    /// <summary>The preview mesh's skeleton (RFA002; RFA023 uses its parents to skip root bones).</summary>
    public Skeleton? Skeleton { get; init; }

    /// <summary>The preview mesh's file name, for messages.</summary>
    public string? PreviewMeshName { get; init; }

    /// <summary>The preview mesh itself, for the morph capacity rule (RFA010).</summary>
    public V3dFile? PreviewMesh { get; init; }

    /// <summary>A clip of the same skeleton family whose bone lengths are the family's (its stand clip), for RFA023.</summary>
    public RfaClip? ReferenceClip { get; init; }

    /// <summary>The reference clip's file name, for messages.</summary>
    public string? ReferenceClipName { get; init; }

    /// <summary>How the tables use this clip (RFA013).</summary>
    public IReadOnlyList<ClipTableUse> TableUses { get; init; } = [];

    /// <summary>
    /// Other clips the game can see with the same base name, described for the user (e.g.
    /// "park_jeep_driver.rfa in meshes.vpp"), for RFA024. Clip identity is the base name, global.
    /// </summary>
    public IReadOnlyList<string> CollidingClips { get; init; } = [];

    /// <summary>
    /// The file bytes of each entry of <see cref="CollidingClips"/> (same order) when they were read,
    /// else null. A copy byte-identical to the clip as it now stands is the same clip (a loose copy of a
    /// stock clip, or the stock clip itself), not a collision, so RFA024 leaves it out.
    /// </summary>
    public IReadOnlyList<byte[]?> CollidingClipBytes { get; init; } = [];
}

/// <summary>
/// Lints a clip (<see cref="ClipRules"/> lists every rule and its severity). <see cref="Analyze"/> is
/// cheap enough to run after every edit; the context-dependent rules only run when their context is
/// supplied. Results are ordered: errors first, then by bone and time.
/// </summary>
public static class ClipLinter
{
    /// <summary>At most this many diagnostics of one code are reported per bone (a broken track would otherwise flood the panel).</summary>
    public const int MaxPerBone = 3;

    /// <summary>Runs every rule whose context is available.</summary>
    public static IReadOnlyList<Diagnostic> Analyze(RfaClip clip, ClipLintContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        context ??= ClipLintContext.None;
        var results = new List<Diagnostic>();
        CheckHeader(results, clip);
        CheckBones(results, clip, context);
        CheckMorph(results, clip);
        CheckWritable(results, clip);
        CheckPreviewMesh(results, clip, context);
        CheckTables(results, clip, context);
        CheckLibrary(results, clip, context);
        CheckFileName(results, context);
        return [.. results
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.Bone ?? -1)
            .ThenBy(d => d.Location.Time ?? int.MinValue)
            .ThenBy(d => d.Code, StringComparer.Ordinal)];
    }

    /// <summary>The structural rules only (no context): what the App runs synchronously.</summary>
    public static IReadOnlyList<Diagnostic> AnalyzeStructure(RfaClip clip) => Analyze(clip, ClipLintContext.None);

    /// <summary>True when <paramref name="diagnostics"/> holds at least one error.</summary>
    public static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    // ── Header ────────────────────────────────────────────────────────────────

    private static void CheckHeader(List<Diagnostic> results, RfaClip clip)
    {
        if (clip.Version is not (7 or 8))
        {
            results.Add(Make(ClipRules.UnsupportedVersion,
                $"The clip says it is version {clip.Version}; the game only plays versions 7 and 8.",
                "Use Clip › Convert to Version 8 (or 7). Version 8 is what the stock exporter writes.",
                DiagnosticLocation.Header("rfa.version"),
                ClipFixes.SetVersion(clip, 8)));
        }

        if (clip.BoneCount > RfaClip.MaxEngineBones)
        {
            results.Add(Make(ClipRules.TooManyBones,
                $"The clip has {clip.BoneCount} bones; the engine poses at most {RfaClip.MaxEngineBones}.",
                "A clip needs exactly the bones of the mesh it plays on, and no mesh can have more than 50. "
                + "Use Clip › Conform to Skeleton with the right mesh.",
                DiagnosticLocation.Document,
                new QuickFix("Conform to skeleton…", QuickFixKind.ConformToSkeleton)));
        }

        if (clip.EndTime < clip.StartTime)
        {
            results.Add(Make(ClipRules.EndBeforeStart,
                $"The clip ends ({UserText.Ticks(clip.EndTime)}) before it starts ({UserText.Ticks(clip.StartTime)}).",
                "The engine computes the clip's length as end minus start, so a negative length breaks its timing and ramps. "
                + "Swap the two times, or recompute them from the keys.",
                DiagnosticLocation.Header("rfa.end_time"),
                ClipFixes.RecomputeRange(clip)));
        }
        else if (clip.EndTime == clip.StartTime && clip.Bones.Any(b => b.RotationKeys.Length > 1 || b.PositionKeys.Length > 1))
        {
            results.Add(Make(ClipRules.ZeroLength,
                $"The clip starts and ends at {UserText.Ticks(clip.StartTime)}, so it has no length although it has several keys.",
                "Set the end time to the last key's time (Clip › Recompute Start/End), or trim the clip deliberately.",
                DiagnosticLocation.Header("rfa.end_time"),
                ClipFixes.RecomputeRange(clip)));
        }

        if (clip.RampIn < 0 || clip.RampOut < 0)
        {
            results.Add(Make(ClipRules.NegativeRamp,
                $"A ramp is negative (ramp in {clip.RampIn}, ramp out {clip.RampOut} ticks); the engine then never ramps that end.",
                "Set the ramps to 0 or more ticks.",
                DiagnosticLocation.Header(clip.RampIn < 0 ? "rfa.ramp_in" : "rfa.ramp_out"),
                ClipFixes.SetRamps(clip, Math.Max(0, clip.RampIn), Math.Max(0, clip.RampOut))));
        }
        else
        {
            int duration = Math.Max(0, clip.Duration);
            if (clip.RampIn + clip.RampOut > duration && (clip.RampIn > 0 || clip.RampOut > 0))
            {
                int rampIn = Math.Min(clip.RampIn, duration / 2), rampOut = Math.Min(clip.RampOut, duration - rampIn);
                results.Add(Make(ClipRules.RampsLongerThanClip,
                    $"The ramps ({clip.RampIn} in + {clip.RampOut} out ticks) are longer than the clip ({duration} ticks), "
                    + "so when it plays as an action it never reaches full weight.",
                    "Shorten the ramps so ramp in + ramp out is at most the clip's length. Where they overlap the engine uses the ramp-in.",
                    DiagnosticLocation.Header("rfa.ramp_in"),
                    ClipFixes.SetRamps(clip, rampIn, rampOut)));
            }
        }
    }

    // ── Bones and keys ────────────────────────────────────────────────────────

    private static void CheckBones(List<Diagnostic> results, RfaClip clip, ClipLintContext context)
    {
        for (int b = 0; b < clip.BoneCount; b++)
        {
            var track = clip.Bones[b];
            string bone = $"Bone {b}";

            if (!float.IsFinite(track.Weight))
            {
                results.Add(Make(ClipRules.NonFinite, $"{bone}'s weight is not a number.",
                    "Set the bone's weight in the Bone inspector (stock weights are 0 to 10).",
                    DiagnosticLocation.ForBone(b), ClipFixes.SetWeight(clip, b, ClipBlender.FullWeight)));
            }
            else if (track.Weight > ClipBlender.FullWeight)
            {
                results.Add(Make(ClipRules.WeightAboveTen,
                    string.Create(CultureInfo.InvariantCulture,
                        $"{bone}'s weight is {track.Weight:0.###}. Above 10 the engine's state factor (10 - weight) / 10 goes negative, so when this clip plays as an action every state is removed from that bone."),
                    "Use 10 to make the action fully replace the states on this bone; lower values share the bone with them.",
                    DiagnosticLocation.ForBone(b), ClipFixes.SetWeight(clip, b, ClipBlender.FullWeight)));
            }

            if (track.PositionKeys.Length == 0)
            {
                results.Add(Make(ClipRules.NoPositionKeys,
                    $"{bone} has no position keys, so the engine puts it at (0, 0, 0): it collapses onto its parent's joint.",
                    "Add position keys holding the bone's offset from its parent: at least one, normally two (start and end).",
                    DiagnosticLocation.ForBone(b), ClipFixes.EnsureMinimumKeys(clip, context.Skeleton)));
            }
            if (track.RotationKeys.Length == 0)
            {
                results.Add(Make(ClipRules.NoRotationKeys,
                    $"{bone} has no rotation keys, so the engine gives it no rotation relative to its parent.",
                    "Add rotation keys (select the bone and press K in the timeline at the start and the end, or copy them from another clip of this mesh).",
                    DiagnosticLocation.ForBone(b), ClipFixes.EnsureMinimumKeys(clip, context.Skeleton)));
            }
            else if (track.RotationKeys.Length == 1)
            {
                results.Add(Make(ClipRules.SingleRotationKey,
                    $"{bone} has only one rotation key. Before (and at) that key the engine interpolates towards the key after it, which is not there: it reads the next 16 bytes of the file as a rotation, and the bone can snap to garbage.",
                    "Hold the rotation with a second key: the quick fix copies the key to the clip's start and end.",
                    DiagnosticLocation.ForBone(b), ClipFixes.FixSingleRotationKeys(clip)));
            }

            CheckRotationKeys(results, clip, b, track);
            CheckPositionKeys(results, clip, b, track);
        }
    }

    private static void CheckRotationKeys(List<Diagnostic> results, RfaClip clip, int b, RfaBoneTrack track)
    {
        var keys = track.RotationKeys;
        int order = 0, outside = 0, zero = 0, nonUnit = 0, sign = 0, pad = 0, snap = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            var at = DiagnosticLocation.ForKey(new KeyRef(b, KeyKind.Rotation, i), k.Time);
            if (i > 0 && k.Time <= keys[i - 1].Time && order++ < MaxPerBone)
            {
                results.Add(Make(ClipRules.KeyTimesNotIncreasing,
                    $"Bone {b}'s rotation key {i} at {UserText.Ticks(k.Time)} is not after the key before it ({UserText.Ticks(keys[i - 1].Time)}).",
                    "The engine finds keys by scanning forward, so out-of-order or duplicate times make it divide by zero or jump. Sort the keys and remove duplicates (Clip › Normalise).",
                    at, ClipFixes.SortKeys(clip)));
            }
            if ((k.Time < clip.StartTime || k.Time > clip.EndTime) && outside++ < MaxPerBone)
            {
                results.Add(OutsideRange(clip, b, "rotation", i, k.Time, at));
            }
            if (k.X == 0 && k.Y == 0 && k.Z == 0 && k.W == 0)
            {
                if (zero++ < MaxPerBone)
                {
                    results.Add(Make(ClipRules.ZeroQuaternion,
                        $"Bone {b}'s rotation key {i} at {UserText.Ticks(k.Time)} is all zeros, which is not a rotation: the bone and everything below it collapse.",
                        "Give the key a rotation (Key inspector), or delete it.",
                        at));
                }
                continue;
            }
            float length = k.FileQuaternion.Length();
            if (MathF.Abs(length - 1f) > ClipRules.UnitTolerance && nonUnit++ < MaxPerBone)
            {
                results.Add(Make(ClipRules.NonUnitQuaternion,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Bone {b}'s rotation key {i} at {UserText.Ticks(k.Time)} has length {length:0.####} instead of 1, so the engine scales the bone's mesh as well as rotating it."),
                    "Normalise the quaternion (Clip › Normalise re-quantises only the keys that are off).",
                    at, ClipFixes.UnitQuaternions(clip)));
            }
            if (i > 0 && snap < 1 && SnapsInEngine(keys[i - 1], k, out float apart))
            {
                snap++;
                results.Add(Make(ClipRules.SegmentSnaps,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Bone {b}'s rotation keys {i - 1} and {i} are {apart:0.##} degrees apart, but the game will not interpolate between them: the bone jumps to key {i} at {UserText.Ticks(keys[i - 1].Time)}. The engine skips the slerp when the raw dot product of the stored keys is within 0.000001 of 1, and these keys are stored slightly longer than 1."),
                    "Re-quantise the keys so they are not longer than 1 (the quick fix does exactly that; the rotation changes by under 0.01 degrees).",
                    at, ClipFixes.RequantizeOverlong(clip, b)));
            }
            if (i > 0 && Cairn.Formats.Maths.Quat.Dot(k.FileQuaternion, keys[i - 1].FileQuaternion) < 0f && sign++ < 1)
            {
                results.Add(Make(ClipRules.SignDiscontinuity,
                    $"Bone {b}'s rotation keys change sign at key {i} ({UserText.Ticks(k.Time)}). The game interpolates the short way regardless, but other tools may spin the bone the long way.",
                    "Clip › Normalise negates the stored components of such keys (the same rotation, exactly).",
                    at, ClipFixes.SignContinuity(clip)));
            }
            if (k.Pad != 0 && pad++ < 1)
            {
                results.Add(Make(ClipRules.NonZeroPad,
                    $"Bone {b}'s rotation key {i} has a non-zero pad word ({k.Pad}); the game ignores it, stock files store 0.",
                    "Clip › Normalise clears it.",
                    at, ClipFixes.ZeroPad(clip)));
            }
        }
    }

    private static void CheckPositionKeys(List<Diagnostic> results, RfaClip clip, int b, RfaBoneTrack track)
    {
        var keys = track.PositionKeys;
        int order = 0, outside = 0, nonFinite = 0, zeroControl = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            var at = DiagnosticLocation.ForKey(new KeyRef(b, KeyKind.Position, i), k.Time);
            if (i > 0 && k.Time <= keys[i - 1].Time && order++ < MaxPerBone)
            {
                results.Add(Make(ClipRules.KeyTimesNotIncreasing,
                    $"Bone {b}'s position key {i} at {UserText.Ticks(k.Time)} is not after the key before it ({UserText.Ticks(keys[i - 1].Time)}).",
                    "The engine finds keys by scanning forward, so out-of-order or duplicate times make it divide by zero or jump. Sort the keys and remove duplicates (Clip › Normalise).",
                    at, ClipFixes.SortKeys(clip)));
            }
            if ((k.Time < clip.StartTime || k.Time > clip.EndTime) && outside++ < MaxPerBone)
            {
                results.Add(OutsideRange(clip, b, "position", i, k.Time, at));
            }
            if (!(Finite(k.Position) && Finite(k.InControl) && Finite(k.OutControl)))
            {
                if (nonFinite++ < MaxPerBone)
                {
                    results.Add(Make(ClipRules.NonFinite,
                        $"Bone {b}'s position key {i} at {UserText.Ticks(k.Time)} holds a value that is not a number.",
                        "Retype the position and control points in the Key inspector, or delete the key.",
                        at));
                }
                continue;
            }
            if (keys.Length > 1 && k.Position != Vector3.Zero
                && ((i > 0 && k.InControl == Vector3.Zero) || (i < keys.Length - 1 && k.OutControl == Vector3.Zero))
                && zeroControl++ < MaxPerBone)
            {
                results.Add(Make(ClipRules.ZeroControlPoints,
                    $"Bone {b}'s position key {i} at {UserText.Ticks(k.Time)} has a Bezier control point at (0, 0, 0). Control points are absolute positions, so the bone swings through its parent's joint between keys.",
                    "Set the control points to Auto (Linear) — the straight line between the keys — or place them by hand.",
                    at, ClipFixes.ControlPoints(clip)));
            }
        }
    }

    /// <summary>
    /// True when the engine's <c>slerp_short</c> would return the later key for the whole segment
    /// (<c>1 - rawdot/16383^2 &lt;= 1e-6</c> after the short-arc flip) although the normalised keys are more
    /// than <see cref="ClipRules.SnapReportDegrees"/> apart.
    /// </summary>
    internal static bool SnapsInEngine(RfaRotKey a, RfaRotKey b, out float apartDegrees)
    {
        apartDegrees = 0f;
        long dot = (long)a.X * b.X + (long)a.Y * b.Y + (long)a.Z * b.Z + (long)a.W * b.W;
        long sum = Sq(a.X + b.X) + Sq(a.Y + b.Y) + Sq(a.Z + b.Z) + Sq(a.W + b.W);
        long diff = Sq(a.X - b.X) + Sq(a.Y - b.Y) + Sq(a.Z - b.Z) + Sq(a.W - b.W);
        if (sum <= diff) dot = -dot;
        if (1f - (float)(dot * (double)3.725745e-9f) > ClipSampler.NearParallel) return false;
        apartDegrees = Cairn.Formats.Maths.Quat.AngleDegrees(a.FileQuaternion, b.FileQuaternion);
        return apartDegrees > ClipRules.SnapReportDegrees;

        static long Sq(long v) => v * v;
    }

    private static Diagnostic OutsideRange(RfaClip clip, int b, string kind, int i, int time, DiagnosticLocation at) =>
        Make(ClipRules.KeyOutsideRange,
            $"Bone {b}'s {kind} key {i} at {UserText.Ticks(time)} is outside the clip ({UserText.Ticks(clip.StartTime)} to {UserText.Ticks(clip.EndTime)}).",
            "The game never plays that part, and an action's ramp is computed from start and end. Either widen the range to the keys, or drop the keys outside it.",
            at, ClipFixes.RecomputeRange(clip), ClipFixes.ClampToRange(clip));

    // ── Morph and writability ─────────────────────────────────────────────────

    private static void CheckMorph(List<Diagnostic> results, RfaClip clip)
    {
        var m = clip.Morph;
        if (m is null) return;
        if (clip.Version >= 8 && !m.KeyframeTimes.IsDefaultOrEmpty)
        {
            for (int i = 1; i < m.KeyframeTimes.Length; i++)
            {
                if (m.KeyframeTimes[i] < m.KeyframeTimes[i - 1])
                {
                    results.Add(Make(ClipRules.KeyTimesNotIncreasing,
                        $"Morph keyframe {i}'s time ({UserText.Ticks(m.KeyframeTimes[i])}) is before the one before it.",
                        "The engine scans morph times forward; out-of-order times make the face jump. Re-export the vertex animation, or strip it.",
                        DiagnosticLocation.Morph, ClipFixes.StripMorph(clip)));
                    break;
                }
            }
        }
        if ((!m.Positions.IsDefaultOrEmpty && m.Positions.Any(p => !Finite(p)))
            || (m.Bounds is { } box && (!Finite(box.Min) || !Finite(box.Max))))
        {
            results.Add(Make(ClipRules.NonFinite, "The vertex (morph) animation holds a position that is not a number.",
                "Strip the morph data or re-export it.", DiagnosticLocation.Morph, ClipFixes.StripMorph(clip)));
        }
        if (m.VertexCount > 0 && m.KeyframeCount == 0)
        {
            results.Add(Make(ClipRules.MorphUnplayable,
                $"The vertex (morph) animation names {m.VertexCount} vertices but has no keyframes: the engine still morphs them, from data that is not there.",
                "Strip the morph data or re-export it.", DiagnosticLocation.Morph, ClipFixes.StripMorph(clip)));
        }
        else if (clip.Version < 8 && m.VertexCount > 0 && m.KeyframeCount > 0 && clip.EndTime <= clip.StartTime)
        {
            results.Add(Make(ClipRules.MorphUnplayable,
                "This version 7 clip has vertex (morph) animation but no length (end is not after start). The engine divides by the length to find the keyframe, gets no number, and reads a wild keyframe.",
                "Give the clip a length (Clip › Retime or the inspector's end time), or strip the morph data.", DiagnosticLocation.Morph, ClipFixes.StripMorph(clip)));
        }
    }

    private static void CheckWritable(List<Diagnostic> results, RfaClip clip)
    {
        if (clip.Version is not (7 or 8)) return; // RFA001 already says so
        try
        {
            RfaWriter.Validate(clip);
        }
        catch (ArgumentException ex)
        {
            results.Add(Make(ClipRules.NotWritable,
                "The clip cannot be saved as it is: " + ex.Message.Split(" (Parameter")[0],
                "This usually comes from morph data that does not match the version. Strip the morph data, or convert the version again.",
                DiagnosticLocation.Morph, ClipFixes.StripMorph(clip)));
        }
    }

    // ── Context rules ─────────────────────────────────────────────────────────

    private static void CheckPreviewMesh(List<Diagnostic> results, RfaClip clip, ClipLintContext context)
    {
        string mesh = context.PreviewMeshName is { Length: > 0 } name ? $"the preview mesh {UserText.Printable(name)}" : "the preview mesh";
        if (context.Skeleton is { } skeleton && skeleton.Count > 0 && skeleton.Count != clip.BoneCount)
        {
            string effect = clip.BoneCount < skeleton.Count
                ? $"the engine reads past the clip's bone table for bones {clip.BoneCount} to {skeleton.Count - 1}"
                : $"the clip's last {clip.BoneCount - skeleton.Count} tracks are ignored, and the others probably drive the wrong bones";
            results.Add(Make(ClipRules.BoneCountMismatch,
                $"The clip has {clip.BoneCount} bones but {mesh} has {skeleton.Count}. The game matches bones by index, so {effect}.",
                "If the preview mesh is the wrong one, pick the mesh this clip was made for. Otherwise use Clip › Conform to Skeleton "
                + "(reorders, adds and drops tracks by bone name) or Retarget.",
                DiagnosticLocation.Document,
                new QuickFix("Choose another preview mesh…", QuickFixKind.PickPreviewMesh),
                new QuickFix("Conform to skeleton…", QuickFixKind.ConformToSkeleton)));
        }

        if (context.PreviewMesh is { } file && MorphSampler.HasMorph(clip))
        {
            var lod0 = file.Submeshes.FirstOrDefault()?.Lods.FirstOrDefault();
            if (lod0 is not null)
            {
                int largest = clip.Morph.VertexIndices.Max(i => (ushort)i);
                if (largest >= lod0.VertexCount)
                {
                    results.Add(Make(ClipRules.MorphIndexBeyondMesh,
                        $"The clip's vertex (morph) animation moves vertex {largest}, but {mesh}'s first LOD has {lod0.VertexCount} vertices. "
                        + "Stock RF writes past the mesh; Alpine Faction refuses to play the clip.",
                        "Vertex animation belongs to one mesh. Preview the clip on the mesh it was made for, or strip the morph data.",
                        DiagnosticLocation.Morph,
                        new QuickFix("Choose another preview mesh…", QuickFixKind.PickPreviewMesh),
                        ClipFixes.StripMorph(clip)));
                }
            }
        }
    }

    private static void CheckTables(List<Diagnostic> results, RfaClip clip, ClipLintContext context)
    {
        foreach (var group in context.TableUses
                     .Where(u => u.MeshBoneCount is { } n && n > 0 && n != clip.BoneCount)
                     .GroupBy(u => u.MeshName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var use = group.First();
            string uses = string.Join(", ", group.Select(u => $"{u.ClassName} {(u.IsState ? "state" : "action")} '{u.SlotName}'").Distinct().Take(4));
            results.Add(Make(ClipRules.TableMeshBoneCount,
                $"{UserText.Printable(use.Table)} plays this clip on {UserText.Printable(use.MeshName)} ({use.MeshBoneCount} bones; {uses}), "
                + $"but the clip has {clip.BoneCount} bones. The game matches bones by index, so it will play garbage there.",
                "Either this clip is not meant for that class (change the table), or it must be conformed or retargeted to that mesh's skeleton.",
                DiagnosticLocation.Document,
                new QuickFix("Conform to skeleton…", QuickFixKind.ConformToSkeleton, Payload: use.MeshName)));
        }
    }

    private static void CheckLibrary(List<Diagnostic> results, RfaClip clip, ClipLintContext context)
    {
        var colliding = DifferentCopies(clip, context);
        if (colliding.Count > 0)
        {
            string others = string.Join("; ", colliding.Take(3).Select(c => UserText.Printable(c)));
            results.Add(Make(ClipRules.NameCollision,
                $"Another clip with the same name is visible to the game ({others}). The engine knows clips by base name only, "
                + "globally, so only one of them is ever loaded — whichever comes first in the search order.",
                "Save the clip under a new name (and point the table lines at it), unless replacing that clip is the point.",
                DiagnosticLocation.Document,
                new QuickFix("Save as…", QuickFixKind.SaveAs)));
        }

        if (context.ReferenceClip is { } reference && context.Skeleton is { } skeleton
            && reference.BoneCount == clip.BoneCount && skeleton.Count == clip.BoneCount)
        {
            var worst = new List<(int Bone, float Ours, float Theirs)>();
            for (int b = 0; b < clip.BoneCount; b++)
            {
                if (skeleton.EffectiveParents[b] < 0) continue; // the root's position is motion, not a length
                var ours = clip.Bones[b].PositionKeys;
                var theirs = reference.Bones[b].PositionKeys;
                if (ours.IsDefaultOrEmpty || theirs.IsDefaultOrEmpty) continue;
                float a = ours[0].Position.Length(), r = theirs[0].Position.Length();
                float diff = MathF.Abs(a - r);
                if (diff > ProportionAbsolute && diff > ProportionRelative * r) worst.Add((b, a, r));
            }
            if (worst.Count > 0)
            {
                string reference_ = context.ReferenceClipName is { Length: > 0 } n ? UserText.Printable(n) : "the mesh's reference clip";
                string list = string.Join(", ", worst.OrderByDescending(w => MathF.Abs(w.Ours - w.Theirs)).Take(4).Select(w =>
                    string.Create(CultureInfo.InvariantCulture, $"{BoneName(skeleton, w.Bone)} {w.Ours:0.000} m vs {w.Theirs:0.000} m")));
                results.Add(Make(ClipRules.ProportionsDiffer,
                    $"{worst.Count} bone length(s) differ from {reference_} ({list}). Every clip of a character carries its bone lengths, "
                    + "so this one will stretch the character while it plays.",
                    "Set the bone lengths from the reference clip (Clip › Set Bone Lengths), unless the different proportions are intended.",
                    DiagnosticLocation.Document,
                    ClipFixes.BoneLengthsFrom(clip, reference, skeleton.Parents)));
            }
        }
    }

    /// <summary>
    /// <see cref="ClipLintContext.CollidingClips"/> minus the copies whose bytes are exactly what this
    /// clip writes: the same clip seen twice (the stock clip opened from its own archive under another
    /// path, or a loose copy of it) collides with nothing. An edited clip no longer matches, so the
    /// warning returns as soon as it would replace the other copy with something different.
    /// </summary>
    private static List<string> DifferentCopies(RfaClip clip, ClipLintContext context)
    {
        var copies = context.CollidingClips;
        var bytes = context.CollidingClipBytes;
        if (copies.Count == 0 || !bytes.Any(b => b is not null)) return [.. copies];
        byte[]? mine;
        try
        {
            mine = RfaWriter.Write(clip);
        }
        catch (ArgumentException)
        {
            return [.. copies];
        }
        var different = new List<string>(copies.Count);
        for (int i = 0; i < copies.Count; i++)
        {
            if (i < bytes.Count && bytes[i] is { } theirs && theirs.AsSpan().SequenceEqual(mine)) continue;
            different.Add(copies[i]);
        }
        return different;
    }

    /// <summary>RFA023 fires when a bone's length differs by more than this many metres...</summary>
    public const float ProportionAbsolute = 0.02f;

    /// <summary>...and by more than this fraction of the reference length.</summary>
    public const float ProportionRelative = 0.10f;

    private static void CheckFileName(List<Diagnostic> results, ClipLintContext context)
    {
        if (context.FileName is not { Length: > 0 } path) return;
        string name = Path.GetFileName(path);
        if (name.Length > ClipRules.MaxFileNameLength)
        {
            results.Add(Make(ClipRules.FileNameTooLong,
                $"The file name '{UserText.Printable(name)}' is {name.Length} characters. The engine copies a clip's name into a 60-byte buffer "
                + $"without checking, so names over {ClipRules.MaxFileNameLength} characters (with the extension) overflow it and can crash the game.",
                $"Rename the clip so the whole file name is at most {ClipRules.MaxFileNameLength} characters, and update the table lines.",
                DiagnosticLocation.Document,
                new QuickFix("Save as…", QuickFixKind.SaveAs)));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BoneName(Skeleton skeleton, int bone) =>
        bone < skeleton.Count && skeleton.Names[bone] is { Length: > 0 } n ? UserText.Printable(n) : $"bone {bone}";

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Diagnostic Make(string code, string message, string help, DiagnosticLocation location, params QuickFix?[] fixes)
    {
        var info = ClipRules.Find(code) ?? throw new InvalidOperationException($"Unknown rule {code}.");
        return new Diagnostic(code, info.Severity, message, help, location, [.. fixes.Where(f => f is not null).Select(f => f!)]);
    }
}
