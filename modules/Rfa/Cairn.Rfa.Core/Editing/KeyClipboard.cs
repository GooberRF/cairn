using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>What a <see cref="KeyClipboard"/> holds.</summary>
public enum KeyClipboardKind
{
    /// <summary>Keys copied as they are from a selection.</summary>
    Keys,
    /// <summary>A sampled pose: one rotation and one position key per bone at relative time 0.</summary>
    Pose,
}

/// <summary>What happens to pasted keys that land outside the target clip's [start, end].</summary>
public enum PasteRangePolicy
{
    /// <summary>Move the clip's start / end out so every pasted key fits (the default).</summary>
    Extend,
    /// <summary>Drop the keys that fall outside.</summary>
    Clip,
}

/// <summary>Options for <see cref="KeyClipboard.Paste"/>.</summary>
/// <param name="MatchByIndex">Match bones by index even when names are available.</param>
/// <param name="Range">What to do with keys outside [start, end]; extend by default.</param>
public sealed record PasteOptions(bool MatchByIndex = false, PasteRangePolicy Range = PasteRangePolicy.Extend);

/// <summary>The result of a paste.</summary>
/// <param name="Clip">The clip with the keys pasted.</param>
/// <param name="Pasted">The pasted keys, addressed in <paramref name="Clip"/>.</param>
/// <param name="UnmatchedBones">Copied bones with no bone to land on (their names, or <c>#index</c>).</param>
public sealed record PasteResult(RfaClip Clip, KeySelection Pasted, ImmutableArray<string> UnmatchedBones);

/// <summary>One copied bone: its name and index in the source clip and its keys at RELATIVE times.</summary>
/// <param name="Name">Bone name in the source (null when names were not available).</param>
/// <param name="Index">Bone index in the source clip.</param>
/// <param name="RotationKeys">Copied rotation keys, raw, times relative to the clipboard's origin.</param>
/// <param name="PositionKeys">Copied position keys, times relative to the clipboard's origin.</param>
public sealed record KeyClipboardBone(string? Name, int Index, ImmutableArray<RfaRotKey> RotationKeys, ImmutableArray<RfaPosKey> PositionKeys);

/// <summary>
/// Copied keys that can be pasted into any clip (across tabs, via the system clipboard as JSON).
/// Each copied bone keeps its name and index; key times are relative to the earliest copied key
/// (<see cref="SourceTime"/> in the source clip). Rotation keys are kept raw (int16 + eases + pad), so
/// a paste reproduces them bit for bit (apart from an exact sign flip for continuity).
/// <para>
/// JSON layout (stable property names, <c>"version": 1</c>):
/// <c>{"format":"rfaworkbench-keys","version":1,"kind":"keys","sourceTime":160,"bones":[{"name":"...",
/// "index":3,"rotationKeys":[{"time":0,"x":0,"y":0,"z":0,"w":16383,"easeIn":0,"easeOut":0,"pad":0}],
/// "positionKeys":[{"time":0,"position":[0,0,0],"inControl":[0,0,0],"outControl":[0,0,0]}]}]}</c>.
/// </para>
/// </summary>
public sealed record KeyClipboard
{
    /// <summary>The JSON <c>format</c> marker.</summary>
    public const string FormatName = "rfaworkbench-keys";

    /// <summary>The JSON version this code writes and reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>JSON version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Copied keys or a sampled pose.</summary>
    public KeyClipboardKind Kind { get; init; }

    /// <summary>The source time (ticks) relative time 0 corresponds to.</summary>
    public int SourceTime { get; init; }

    /// <summary>The copied bones, in source index order.</summary>
    public ImmutableArray<KeyClipboardBone> Bones { get; init; } = [];

    /// <summary>True when nothing was copied.</summary>
    public bool IsEmpty => Bones.IsDefaultOrEmpty || Bones.All(b => b.RotationKeys.IsDefaultOrEmpty && b.PositionKeys.IsDefaultOrEmpty);

    /// <summary>Total number of copied keys.</summary>
    public int KeyCount => Bones.IsDefault ? 0 : Bones.Sum(b => b.RotationKeys.Length + b.PositionKeys.Length);

    // ── Copy ─────────────────────────────────────────────────────────────────

    /// <summary>Copies the selected keys (invalid entries are ignored).</summary>
    /// <param name="clip">The source clip.</param>
    /// <param name="selection">The keys to copy.</param>
    /// <param name="boneNames">The source bone names, so a paste can match bones by name; null = index only.</param>
    public static KeyClipboard Copy(RfaClip clip, KeySelection selection, IReadOnlyList<string>? boneNames)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(selection);
        var valid = selection.Validate(clip);
        if (valid.TimeSpan(clip) is not { } span) return new KeyClipboard();
        int origin = span.Min;
        var bones = new List<KeyClipboardBone>();
        foreach (int b in valid.SelectedBones.OrderBy(b => b))
        {
            var track = clip.Bones[b];
            var rot = valid.IndicesOf(b, KeyKind.Rotation).Select(i => track.RotationKeys[i] with { Time = track.RotationKeys[i].Time - origin });
            var pos = valid.IndicesOf(b, KeyKind.Position).Select(i => track.PositionKeys[i] with { Time = track.PositionKeys[i].Time - origin });
            bones.Add(new KeyClipboardBone(NameOf(boneNames, b), b, [.. rot], [.. pos]));
        }
        return new KeyClipboard { Kind = KeyClipboardKind.Keys, SourceTime = origin, Bones = [.. bones] };
    }

    /// <summary>
    /// Copies the pose at <paramref name="time"/>: for each bone one rotation key and one constant
    /// position key at relative time 0, sampled as the engine does. Where the bone has a rotation key
    /// exactly at <paramref name="time"/> its raw components are copied (eases cleared).
    /// </summary>
    /// <param name="clip">The source clip.</param>
    /// <param name="time">The time to sample, in ticks.</param>
    /// <param name="boneNames">The source bone names; null = index only.</param>
    /// <param name="bones">The bones to copy; null = all.</param>
    public static KeyClipboard CopyPose(RfaClip clip, int time, IReadOnlyList<string>? boneNames, IEnumerable<int>? bones = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var list = new List<KeyClipboardBone>();
        foreach (int b in PoseEditMath.BoneList(clip, bones))
        {
            var track = clip.Bones[b];
            var exact = track.RotationKeys.Where(k => k.Time == time).Take(1).ToList();
            var rot = exact.Count == 1
                ? new RfaRotKey(0, exact[0].X, exact[0].Y, exact[0].Z, exact[0].W, 0, 0, exact[0].Pad)
                : ClipEdit.QuantizeRotation(0, ClipEdit.SampleRotation(clip, b, time), null);
            var pos = RfaPosKey.Constant(0, ClipEdit.SamplePosition(clip, b, time));
            list.Add(new KeyClipboardBone(NameOf(boneNames, b), b, [rot], [pos]));
        }
        return new KeyClipboard { Kind = KeyClipboardKind.Pose, SourceTime = time, Bones = [.. list] };
    }

    private static string? NameOf(IReadOnlyList<string>? names, int bone) =>
        names is not null && bone < names.Count && !string.IsNullOrEmpty(names[bone]) ? names[bone] : null;

    // ── Paste ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pastes the keys with relative time 0 at <paramref name="time"/>.
    /// <list type="bullet">
    /// <item>Bones match by name (exact case-insensitive, then canonical name), or by index when either
    /// side has no names or <see cref="PasteOptions.MatchByIndex"/> is set. Copied bones that find no
    /// bone are reported in <see cref="PasteResult.UnmatchedBones"/>.</item>
    /// <item>A pasted key replaces a key of the same kind at the same time; other keys stay.</item>
    /// <item>Pasted rotation keys are copied bit for bit, except that one is negated (exact int16
    /// negation, the same rotation) when it would sit in the other hemisphere from the key before it
    /// (or, at the start of the track, the key after it). If that leaves the next untouched key in the
    /// other hemisphere, the following keys are negated too until continuity is restored.</item>
    /// <item>Keys landing outside [start, end] extend the clip's range by default, or are dropped with
    /// <see cref="PasteRangePolicy.Clip"/>.</item>
    /// </list>
    /// </summary>
    public PasteResult Paste(RfaClip target, int time, IReadOnlyList<string>? targetBoneNames, PasteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new PasteOptions();
        var (map, unmatched) = MatchBones(target, targetBoneNames, options);
        var incoming = new Dictionary<int, KeyClipboardBone>();
        foreach (var (bone, dest) in map) incoming[dest] = bone;
        return PasteCore(target, time, incoming, unmatched, options);
    }

    /// <summary>
    /// Mirrors the copied keys across the plane of <paramref name="mirror"/> (as
    /// <see cref="ClipEdit.MirrorClip"/> does) and pastes them: each copied bone is matched to a target
    /// bone as in <see cref="Paste"/>, then lands on that bone's PARTNER in <paramref name="pairs"/>
    /// (a map of the TARGET's bones), with its rotations reflected (in model space relative to the
    /// rest pose when <see cref="MirrorOptions.Skeleton"/>, the TARGET's skeleton, is given) and its
    /// positions reflected and scaled to the destination bone's own length in the target clip.
    /// </summary>
    public PasteResult PasteMirrored(
        RfaClip target, int time, IReadOnlyList<string>? targetBoneNames, BonePairMap pairs, MirrorOptions? mirror = null, PasteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pairs);
        mirror ??= new MirrorOptions();
        options ??= new PasteOptions();
        pairs.Validate(target.BoneCount);
        if (mirror.Skeleton is { } sk) PoseEditMath.CheckSkeleton(target, sk, nameof(mirror));
        var frames = new MirrorFrames(pairs, mirror, target.BoneCount);
        var (map, unmatched) = MatchBones(target, targetBoneNames, options);
        var incoming = new Dictionary<int, KeyClipboardBone>();
        foreach (var (bone, matched) in map)
        {
            int dest = pairs.Partner[matched];
            float scale = dest == matched ? 1f : MirrorFrames.LengthScale(target.Bones[dest].PositionKeys, bone.PositionKeys);
            incoming[dest] = bone with
            {
                RotationKeys = frames.MapRotationKeys(dest, bone.RotationKeys),
                PositionKeys = frames.MapPositionKeys(dest, bone.PositionKeys, scale),
            };
        }
        return PasteCore(target, time, incoming, unmatched, options);
    }

    private (List<(KeyClipboardBone Bone, int Dest)> Map, List<string> Unmatched) MatchBones(
        RfaClip target, IReadOnlyList<string>? targetNames, PasteOptions options)
    {
        var map = new List<(KeyClipboardBone, int)>();
        var unmatched = new List<string>();
        var bones = Bones.IsDefault ? [] : Bones;
        bool byName = !options.MatchByIndex && targetNames is not null && bones.Any(b => b.Name is not null);
        int[] dest;
        if (byName)
        {
            var targetList = Enumerable.Range(0, target.BoneCount)
                .Select(i => i < targetNames!.Count ? targetNames[i] : null).ToList();
            dest = Invert(ClipEdit.MatchBoneNames([.. bones.Select(b => b.Name)], targetList, canonical: true), bones.Length);
            // A copied bone without a name falls back to its index when that bone is still free.
            var taken = new HashSet<int>(dest.Where(d => d >= 0));
            for (int i = 0; i < dest.Length; i++)
            {
                if (dest[i] < 0 && bones[i].Name is null && (uint)bones[i].Index < (uint)target.BoneCount && taken.Add(bones[i].Index))
                    dest[i] = bones[i].Index;
            }
        }
        else
        {
            dest = [.. bones.Select(b => (uint)b.Index < (uint)target.BoneCount ? b.Index : -1)];
            var seen = new HashSet<int>();
            for (int i = 0; i < dest.Length; i++)
            {
                if (dest[i] >= 0 && !seen.Add(dest[i])) dest[i] = -1;
            }
        }
        for (int i = 0; i < bones.Length; i++)
        {
            if (dest[i] >= 0) map.Add((bones[i], dest[i]));
            else unmatched.Add(bones[i].Name ?? "#" + bones[i].Index);
        }
        return (map, unmatched);

        static int[] Invert(int[] sourceOfTarget, int count)
        {
            var result = new int[count];
            Array.Fill(result, -1);
            for (int t = 0; t < sourceOfTarget.Length; t++)
            {
                if (sourceOfTarget[t] >= 0) result[sourceOfTarget[t]] = t;
            }
            return result;
        }
    }

    private static PasteResult PasteCore(
        RfaClip target, int time, Dictionary<int, KeyClipboardBone> incoming, List<string> unmatched, PasteOptions options)
    {
        int start = target.StartTime, end = target.EndTime;
        bool Keep(int t) => options.Range == PasteRangePolicy.Extend || (t >= target.StartTime && t <= target.EndTime);
        if (options.Range == PasteRangePolicy.Extend)
        {
            foreach (var bone in incoming.Values)
            {
                foreach (var k in bone.RotationKeys) Widen(time + k.Time);
                foreach (var k in bone.PositionKeys) Widen(time + k.Time);
            }
        }

        var tracks = target.Bones.ToBuilder();
        var pastedTimes = new List<(int Bone, KeyKind Kind, int Time)>();
        foreach (var (dest, bone) in incoming.OrderBy(kv => kv.Key))
        {
            var track = tracks[dest];

            var rot = track.RotationKeys.IsDefault ? new List<RfaRotKey>() : [.. track.RotationKeys];
            var rotPasted = new List<bool>(new bool[rot.Count]);
            foreach (var k in bone.RotationKeys.IsDefault ? [] : bone.RotationKeys)
            {
                int t = time + k.Time;
                if (!Keep(t)) continue;
                int i = rot.FindIndex(x => x.Time >= t);
                var key = k with { Time = t };
                if (i >= 0 && rot[i].Time == t)
                {
                    rot[i] = key;
                    rotPasted[i] = true;
                }
                else
                {
                    if (i < 0) i = rot.Count;
                    rot.Insert(i, key);
                    rotPasted.Insert(i, true);
                }
                pastedTimes.Add((dest, KeyKind.Rotation, t));
            }

            var pos = track.PositionKeys.IsDefault ? new List<RfaPosKey>() : [.. track.PositionKeys];
            foreach (var k in bone.PositionKeys.IsDefault ? [] : bone.PositionKeys)
            {
                int t = time + k.Time;
                if (!Keep(t)) continue;
                int i = pos.FindIndex(x => x.Time >= t);
                var key = k with { Time = t };
                if (i >= 0 && pos[i].Time == t) pos[i] = key;
                else pos.Insert(i < 0 ? pos.Count : i, key);
                pastedTimes.Add((dest, KeyKind.Position, t));
            }

            var work = new RotTrackWork([.. rot]);
            for (int i = 0; i < rotPasted.Count; i++) work.Changed[i] = rotPasted[i];
            tracks[dest] = track with { RotationKeys = work.ToImmutable(), PositionKeys = [.. pos] };
        }

        // Widening a version 7 clip must not slow its morph down: v7 keyframes are spread over [start, end].
        var morph = target.Version < 8 && !target.Morph.IsEmpty && (start != target.StartTime || end != target.EndTime)
            && target.EndTime > target.StartTime && end > start
            ? ClipEditMorph.ResampleV7(target.Morph, target.StartTime, target.EndTime, start, end)
            : target.Morph;
        var clip = target with { StartTime = start, EndTime = end, Bones = tracks.MoveToImmutable(), Morph = morph };
        var selection = new List<KeyRef>();
        foreach (var (b, kind, t) in pastedTimes)
        {
            var track = clip.Bones[b];
            int index = kind == KeyKind.Rotation
                ? track.RotationKeys.Select((k, i) => (k, i)).First(x => x.k.Time == t).i
                : track.PositionKeys.Select((k, i) => (k, i)).First(x => x.k.Time == t).i;
            selection.Add(new KeyRef(b, kind, index));
        }
        return new PasteResult(clip, KeySelection.Of(selection), [.. unmatched]);

        void Widen(int t)
        {
            if (t < start) start = t;
            if (t > end) end = t;
        }
    }

    // ── Mirror ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The clipboard mirrored in the SOURCE clip's index space: each copied bone moves to its partner
    /// in <paramref name="pairs"/> (name taken from <paramref name="sourceBoneNames"/> when given)
    /// with its keys reflected as <see cref="ClipEdit.MirrorClip"/> would (positions are reflected but
    /// not length-scaled here, since the destination's own length is not known). Use
    /// <see cref="PasteMirrored"/> to mirror against the target clip instead.
    /// </summary>
    public KeyClipboard Mirror(BonePairMap pairs, MirrorOptions? options = null, IReadOnlyList<string>? sourceBoneNames = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        options ??= new MirrorOptions();
        int count = pairs.Count;
        if (options.Skeleton is { } sk && sk.Count != count)
            throw new ArgumentException($"The skeleton has {sk.Count} bones but the pair map has {count}; use the source clip's mesh.", nameof(options));
        pairs.Validate(count);
        var frames = new MirrorFrames(pairs, options, count);
        var bones = new List<KeyClipboardBone>();
        foreach (var b in Bones.IsDefault ? [] : Bones)
        {
            if ((uint)b.Index >= (uint)count)
                throw new ArgumentException($"Copied bone {b.Index} is outside the pair map's {count} bones; detect pairs for the clip the keys came from.", nameof(pairs));
            int dest = pairs.Partner[b.Index];
            string? name = dest == b.Index ? b.Name : NameOf(sourceBoneNames, dest);
            bones.Add(new KeyClipboardBone(name, dest, frames.MapRotationKeys(dest, b.RotationKeys), frames.MapPositionKeys(dest, b.PositionKeys, 1f)));
        }
        return this with { Bones = [.. bones.OrderBy(b => b.Index)] };
    }

    // ── JSON ─────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The clipboard as JSON (see the class remarks for the layout).</summary>
    public string ToJson()
    {
        var dto = new ClipboardDto
        {
            Format = FormatName,
            Version = Version,
            Kind = Kind == KeyClipboardKind.Pose ? "pose" : "keys",
            SourceTime = SourceTime,
            Bones = [.. (Bones.IsDefault ? [] : Bones).Select(b => new BoneDto
            {
                Name = b.Name,
                Index = b.Index,
                RotationKeys = [.. (b.RotationKeys.IsDefault ? [] : b.RotationKeys).Select(k => new RotDto
                {
                    Time = k.Time, X = k.X, Y = k.Y, Z = k.Z, W = k.W, EaseIn = k.EaseIn, EaseOut = k.EaseOut, Pad = k.Pad,
                })],
                PositionKeys = [.. (b.PositionKeys.IsDefault ? [] : b.PositionKeys).Select(k => new PosDto
                {
                    Time = k.Time, Position = V(k.Position), InControl = V(k.InControl), OutControl = V(k.OutControl),
                })],
            })],
        };
        return JsonSerializer.Serialize(dto, JsonOptions);

        static float[] V(Vector3 v) => [v.X, v.Y, v.Z];
    }

    /// <summary>Reads clipboard JSON written by <see cref="ToJson"/>, checking every field.</summary>
    /// <exception cref="FormatException">The text is not RFA Workbench key clipboard data, or is damaged.</exception>
    public static KeyClipboard FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ClipboardDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ClipboardDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The clipboard does not hold copied keys (the text is not valid key data): " + ex.Message, ex);
        }
        if (dto is null || dto.Format != FormatName)
            throw new FormatException("The clipboard does not hold keys copied from RFA Workbench.");
        if (dto.Version < 1 || dto.Version > CurrentVersion)
            throw new FormatException($"The copied keys use format version {dto.Version}, but this version of RFA Workbench reads version {CurrentVersion}; update RFA Workbench or copy the keys again.");
        var kind = dto.Kind switch
        {
            "keys" => KeyClipboardKind.Keys,
            "pose" => KeyClipboardKind.Pose,
            _ => throw new FormatException($"The copied data is of an unknown kind \"{dto.Kind}\"; expected \"keys\" or \"pose\"."),
        };
        var bones = new List<KeyClipboardBone>();
        foreach (var b in dto.Bones ?? [])
        {
            if (b is null) throw new FormatException("The copied keys contain an empty bone entry.");
            if (b.Index < 0) throw new FormatException($"A copied bone has the index {b.Index}; bone indices cannot be negative.");
            string who = b.Name ?? "#" + b.Index;
            var rot = new List<RfaRotKey>();
            foreach (var k in b.RotationKeys ?? [])
            {
                if (k is null) throw new FormatException($"Bone {who} has an empty rotation key.");
                if (rot.Count > 0 && k.Time <= rot[^1].Time)
                    throw new FormatException($"Bone {who}'s rotation keys are not in increasing time order.");
                rot.Add(new RfaRotKey(k.Time, k.X, k.Y, k.Z, k.W, k.EaseIn, k.EaseOut, k.Pad));
            }
            var pos = new List<RfaPosKey>();
            foreach (var k in b.PositionKeys ?? [])
            {
                if (k is null) throw new FormatException($"Bone {who} has an empty position key.");
                if (pos.Count > 0 && k.Time <= pos[^1].Time)
                    throw new FormatException($"Bone {who}'s position keys are not in increasing time order.");
                pos.Add(new RfaPosKey(k.Time, V(k.Position, who), V(k.InControl, who), V(k.OutControl, who)));
            }
            bones.Add(new KeyClipboardBone(b.Name, b.Index, [.. rot], [.. pos]));
        }
        return new KeyClipboard { Version = dto.Version, Kind = kind, SourceTime = dto.SourceTime, Bones = [.. bones] };

        static Vector3 V(float[]? a, string who)
        {
            if (a is not { Length: 3 } || !float.IsFinite(a[0]) || !float.IsFinite(a[1]) || !float.IsFinite(a[2]))
                throw new FormatException($"Bone {who} has a position key whose point or control point is not three finite numbers.");
            return new Vector3(a[0], a[1], a[2]);
        }
    }

    private sealed class ClipboardDto
    {
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("sourceTime")] public int SourceTime { get; set; }
        [JsonPropertyName("bones")] public BoneDto?[]? Bones { get; set; }
    }

    private sealed class BoneDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("rotationKeys")] public RotDto?[]? RotationKeys { get; set; }
        [JsonPropertyName("positionKeys")] public PosDto?[]? PositionKeys { get; set; }
    }

    private sealed class RotDto
    {
        [JsonPropertyName("time")] public int Time { get; set; }
        [JsonPropertyName("x")] public short X { get; set; }
        [JsonPropertyName("y")] public short Y { get; set; }
        [JsonPropertyName("z")] public short Z { get; set; }
        [JsonPropertyName("w")] public short W { get; set; }
        [JsonPropertyName("easeIn")] public sbyte EaseIn { get; set; }
        [JsonPropertyName("easeOut")] public sbyte EaseOut { get; set; }
        [JsonPropertyName("pad")] public short Pad { get; set; }
    }

    private sealed class PosDto
    {
        [JsonPropertyName("time")] public int Time { get; set; }
        [JsonPropertyName("position")] public float[]? Position { get; set; }
        [JsonPropertyName("inControl")] public float[]? InControl { get; set; }
        [JsonPropertyName("outControl")] public float[]? OutControl { get; set; }
    }
}
