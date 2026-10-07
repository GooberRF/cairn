using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Formats;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Interchange;

/// <summary>Options for <see cref="GltfAnimationImport"/>.</summary>
public sealed record GltfAnimationImportOptions
{
    /// <summary>Import only this animation (index into the document's animations); null imports all.</summary>
    public int? AnimationIndex { get; init; }

    /// <summary>
    /// The node -> bone mapping to use (made by <see cref="GltfAnimationImport.MapBones"/> and edited
    /// in the mapper UI); null maps automatically with <see cref="MapOptions"/>.
    /// </summary>
    public BoneMap? BoneMap { get; init; }

    /// <summary>Automatic mapping options (names: exact, canonical, stock tables, fuzzy).</summary>
    public BoneMapOptions? MapOptions { get; init; }

    /// <summary>A clip on the target skeleton whose pose (at its start) unmapped bones hold; null holds the bind pose.</summary>
    public RfaClip? ReferenceClip { get; init; }

    /// <summary>Bone weight when the file carries no <c>rf_weight</c> (REDUX's importer uses 10).</summary>
    public float DefaultWeight { get; init; } = 10f;

    /// <summary>Tick the first glTF key lands on when the file has no <c>rf_start_time</c> (stock clips start at 160).</summary>
    public int StartTick { get; init; } = RfaClip.TicksPerFrame;

    /// <summary>Resampling step for tracks that cannot keep their keys (160 ticks = 30 fps).</summary>
    public int SampleStepTicks { get; init; } = RfaClip.TicksPerFrame;

    /// <summary>RFA version when the file carries no <c>rf_version</c>.</summary>
    public int Version { get; init; } = 8;

    /// <summary>Ramp-in ticks when the file carries none.</summary>
    public int RampIn { get; init; }

    /// <summary>Ramp-out ticks when the file carries none.</summary>
    public int RampOut { get; init; }

    /// <summary>Key reduction applied to every imported track that was not restored key-for-key from <c>rf_keys</c>; null keeps every key.</summary>
    public ReduceOptions? Reduce { get; init; }

    /// <summary>Restore tracks from <c>rf_keys</c> sampler extras when they still match the glTF motion.</summary>
    public bool UseKeyExtras { get; init; } = true;
}

/// <summary>How an imported bone's track was made.</summary>
public enum GltfBoneImportMode
{
    /// <summary>Restored key-for-key from the <c>rf_keys</c> extras (an unedited RFA Workbench export).</summary>
    Restored,
    /// <summary>The glTF keys converted one for one (LINEAR / CUBICSPLINE translations, LINEAR rotations, STEP as held keys).</summary>
    Keys,
    /// <summary>Resampled every <see cref="GltfAnimationImportOptions.SampleStepTicks"/> (different hierarchy or rest frame, or CUBICSPLINE rotations).</summary>
    Resampled,
    /// <summary>No source node: holds the bind pose.</summary>
    RestPose,
    /// <summary>No source node: holds the reference clip's pose.</summary>
    ReferencePose,
}

/// <summary>One target bone's outcome.</summary>
/// <param name="Bone">Target bone index.</param>
/// <param name="Name">Target bone name.</param>
/// <param name="SourceNode">glTF node index, or -1.</param>
/// <param name="SourceName">glTF node name (suffix stripped), or null.</param>
/// <param name="Mode">How the track was made.</param>
/// <param name="RotationKeys">Rotation keys written.</param>
/// <param name="PositionKeys">Position keys written.</param>
public sealed record GltfBoneImport(int Bone, string Name, int SourceNode, string? SourceName, GltfBoneImportMode Mode, int RotationKeys, int PositionKeys);

/// <summary>The report for one imported animation.</summary>
/// <param name="AnimationIndex">Index in the document.</param>
/// <param name="Name">Animation name.</param>
/// <param name="StartTime">Clip start tick.</param>
/// <param name="EndTime">Clip end tick.</param>
/// <param name="UsedRfExtras">True when header extras (<c>rf_start_time</c> ...) were present and used.</param>
/// <param name="Bones">Per target bone.</param>
/// <param name="UnusedNodes">Animated glTF nodes no bone follows.</param>
/// <param name="Warnings">Plain-language notes.</param>
public sealed record GltfAnimationImportReport(
    int AnimationIndex, string Name, int StartTime, int EndTime, bool UsedRfExtras,
    ImmutableArray<GltfBoneImport> Bones, ImmutableArray<string> UnusedNodes, ImmutableArray<string> Warnings)
{
    /// <summary>Bones with a source node.</summary>
    public int MappedCount => Bones.Count(b => b.SourceNode >= 0);

    /// <summary>Total rotation keys.</summary>
    public int RotationKeys => Bones.Sum(b => b.RotationKeys);

    /// <summary>Total position keys.</summary>
    public int PositionKeys => Bones.Sum(b => b.PositionKeys);

    /// <summary>Duration in ticks.</summary>
    public int Duration => EndTime - StartTime;
}

/// <summary>An imported clip and its report.</summary>
/// <param name="Name">Suggested clip name (the animation's name).</param>
/// <param name="Clip">The clip, laid out for the target skeleton.</param>
/// <param name="Report">What happened.</param>
public sealed record GltfImportedClip(string Name, RfaClip Clip, GltfAnimationImportReport Report);

/// <summary>The glTF nodes treated as the source skeleton, in the order a <see cref="BoneMap"/>'s source bones use.</summary>
/// <param name="Nodes">glTF node index of each source bone.</param>
/// <param name="Names">Names (REDUX's <c>__rfbi</c> suffix stripped).</param>
/// <param name="Parents">Parent within this list, or -1.</param>
public sealed record GltfSourceSkeleton(ImmutableArray<int> Nodes, ImmutableArray<string> Names, ImmutableArray<int> Parents);

/// <summary>
/// glTF animation(s) -> <see cref="RfaClip"/>s for a target <see cref="Skeleton"/>. Nodes map to bones
/// through <see cref="BoneMapper"/> (or a given map). A bone whose node hangs off its parent's node
/// and whose rest frame equals the target's keeps the glTF keys (converted from glTF node-local
/// transforms, which in that case ARE this engine's bone locals, mirrored); any other mapped bone is
/// resampled in model space with a per-bone rest correction
/// <c>C = restWorld_gltf^-1 * restWorld_target</c>, so differing bone orientations still play right.
/// <c>rf_*</c> extras restore timing, ramps, weights, eases, version and (through <c>rf_keys</c>) the
/// exact keys when present; otherwise the first key lands on <see cref="GltfAnimationImportOptions.StartTick"/>
/// (so 30 fps frames become 160-tick multiples), weights default, eases are 0, and rotations are
/// quantised within unit length.
/// </summary>
public static class GltfAnimationImport
{
    private const double RestRotationToleranceDegrees = 0.05;
    private const float RestPositionTolerance = 1e-3f;
    private const double RestoreRotationToleranceDegrees = 0.05;
    private const float RestorePositionTolerance = 2e-4f;

    /// <summary>The names of the document's animations, by index.</summary>
    public static ImmutableArray<string> ListAnimations(GltfDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return [.. doc.Animations.Select((a, i) => string.IsNullOrWhiteSpace(a.Name) ? $"animation_{i}" : a.Name!)];
    }

    /// <summary>
    /// The source skeleton: the joints of the first skin a mesh uses (else the first skin), else the
    /// nodes tagged <c>rf_type = "bone"</c>, else every animated node and its ancestors. Ordered by
    /// <c>rf_bone_index</c> / <c>__rfbi</c> when those form a permutation, else as listed.
    /// </summary>
    public static GltfSourceSkeleton SourceSkeleton(GltfDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        List<int> nodes;
        int skinIndex = doc.Nodes.Where(n => n.Mesh is not null && n.Skin is { } s && s >= 0 && s < doc.Skins.Count).Select(n => n.Skin!.Value).FirstOrDefault(-1);
        if (skinIndex < 0 && doc.Skins.Count > 0) skinIndex = 0;
        if (skinIndex >= 0) nodes = [.. doc.Skins[skinIndex].Joints.Where(j => j >= 0 && j < doc.Nodes.Count).Distinct()];
        else
        {
            nodes = [.. Enumerable.Range(0, doc.Nodes.Count).Where(i => GltfExtras.TryGetString(doc.Nodes[i].Extras, GltfExtras.Type, out var t) && t == "bone")];
            if (nodes.Count == 0)
            {
                var parents = doc.ComputeParents();
                var set = new SortedSet<int>();
                foreach (var a in doc.Animations)
                {
                    foreach (var c in a.Channels)
                    {
                        if (c.Target.Node is not { } n || n < 0 || n >= doc.Nodes.Count) continue;
                        if (c.Target.Path is not ("rotation" or "translation")) continue;
                        for (int k = n, guard = 0; k >= 0 && guard <= doc.Nodes.Count; k = parents[k], guard++) set.Add(k);
                    }
                }
                nodes = [.. set];
            }
        }

        // Order by the REDUX bone index hints when they form a permutation.
        var hints = nodes.Select(n => BoneIndexHint(doc.Nodes[n])).ToList();
        if (hints.Count > 0 && hints.All(h => h >= 0) && hints.Distinct().Count() == hints.Count && hints.Max() == hints.Count - 1)
            nodes = [.. nodes.Zip(hints).OrderBy(z => z.Second).Select(z => z.First)];

        var parentsAll = doc.ComputeParents();
        var position = new Dictionary<int, int>();
        for (int i = 0; i < nodes.Count; i++) position[nodes[i]] = i;
        var names = nodes.Select(n => GltfSpace.StripBoneIndexSuffix(doc.Nodes[n].Name ?? $"node_{n}", out _)).ToImmutableArray();
        var srcParents = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            srcParents[i] = -1;
            for (int k = parentsAll[nodes[i]], guard = 0; k >= 0 && guard <= doc.Nodes.Count; k = parentsAll[k], guard++)
            {
                if (position.TryGetValue(k, out int p))
                {
                    srcParents[i] = p;
                    break;
                }
            }
        }
        return new GltfSourceSkeleton([.. nodes], names, [.. srcParents]);
    }

    /// <summary>The automatic node -> bone map (target = the RF skeleton, source = <see cref="SourceSkeleton"/>).</summary>
    public static BoneMap MapBones(GltfDocument doc, Skeleton target, BoneMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return AutoMap(SourceSkeleton(doc), target, options);
    }

    /// <summary>
    /// The same skeleton (names and parents equal, in order — e.g. an export of this very mesh) maps
    /// index to index, which also keeps stock rigs with duplicate bone names right; anything else goes
    /// through <see cref="BoneMapper"/>.
    /// </summary>
    private static BoneMap AutoMap(GltfSourceSkeleton src, Skeleton target, BoneMapOptions? options)
    {
        var sourceBones = Enumerable.Range(0, src.Nodes.Length).Select(i => new BoneMapBone(src.Names[i], src.Parents[i])).ToList();
        var targetBones = Enumerable.Range(0, target.Count).Select(i => new BoneMapBone(target.Names[i], target.EffectiveParents[i])).ToList();
        bool same = sourceBones.Count == targetBones.Count && sourceBones.Zip(targetBones).All(z =>
            string.Equals(z.First.Name, z.Second.Name, StringComparison.OrdinalIgnoreCase) && z.First.Parent == z.Second.Parent);
        if (same)
        {
            var identity = Enumerable.Range(0, targetBones.Count).ToList();
            return BoneMap.Create(sourceBones, [.. Enumerable.Range(0, target.Count).Select(i => new BoneMapBone(target.Names[i], target.Parents[i]))],
                identity, [.. identity.Select(_ => BoneMatchKind.ExactName)]);
        }
        return BoneMapper.Map(src.Names, src.Parents, RigProfile.Generic(src.Names, src.Parents),
            target.Names, target.Parents, RigProfile.Generic(target), options);
    }

    /// <summary>Imports the chosen animation(s).</summary>
    /// <exception cref="AssetFormatException">An accessor the animation uses is damaged.</exception>
    /// <exception cref="ArgumentException">The options are inconsistent (a bone map for another skeleton, a bad index).</exception>
    public static ImmutableArray<GltfImportedClip> Import(GltfDocument doc, Skeleton target, GltfAnimationImportOptions? options = null, string fileName = "glTF")
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new GltfAnimationImportOptions();
        if (options.SampleStepTicks <= 0) throw new ArgumentException("SampleStepTicks must be positive.", nameof(options));
        if (options.AnimationIndex is { } only && (only < 0 || only >= doc.Animations.Count))
            throw new ArgumentException($"The file has {doc.Animations.Count} animations; there is no animation {only}.", nameof(options));

        var src = SourceSkeleton(doc);
        var map = options.BoneMap ?? AutoMap(src, target, options.MapOptions);
        if (map.SourceBones.Length != src.Nodes.Length || map.TargetBones.Length != target.Count)
            throw new ArgumentException("The bone map was made for a different file or skeleton.", nameof(options));

        var results = ImmutableArray.CreateBuilder<GltfImportedClip>();
        for (int a = 0; a < doc.Animations.Count; a++)
        {
            if (options.AnimationIndex is { } want && want != a) continue;
            results.Add(new Importer(doc, target, src, map, options, fileName, a).Run());
        }
        return results.ToImmutable();
    }

    private static int BoneIndexHint(GltfNode node)
    {
        if (GltfExtras.TryGetInt(node.Extras, GltfExtras.BoneIndex, out int i)) return i;
        GltfSpace.StripBoneIndexSuffix(node.Name ?? string.Empty, out int fromName);
        return fromName;
    }

    /// <summary>A node's rest local transform in glTF space (scale ignored).</summary>
    internal static Rigid RestLocal(GltfNode node)
    {
        if (node.Matrix is { Length: 16 })
        {
            var m = node.LocalMatrix();
            return Matrix4x4.Decompose(m, out _, out var r, out var t) ? new Rigid(Quat.Normalize(r), t) : new Rigid(Quaternion.Identity, m.Translation);
        }
        return new Rigid(node.Rotation is { } q ? Quat.Normalize(q) : Quaternion.Identity, node.Translation ?? Vector3.Zero);
    }

    private sealed class Importer(
        GltfDocument doc, Skeleton target, GltfSourceSkeleton src, BoneMap map, GltfAnimationImportOptions options, string fileName, int animationIndex)
    {
        /// <summary>Resampled keys per bone at most: 20 minutes at 30 fps (stock clips last seconds).</summary>
        private const int MaxResampledKeys = 36_000;

        private readonly GltfAnimation _anim = doc.Animations[animationIndex];
        private readonly int[] _parents = doc.ComputeParents();
        private readonly List<string> _warnings = [];
        private readonly Dictionary<int, GltfChannelSampler> _rot = [];
        private readonly Dictionary<int, GltfChannelSampler> _pos = [];
        private readonly Dictionary<int, GltfAnimationSampler> _rotRaw = [];
        private readonly Dictionary<int, GltfAnimationSampler> _posRaw = [];
        private readonly Dictionary<int, float> _weights = [];
        private Rigid[] _restWorldGl = [];
        private double _t0;
        private bool _absolute;
        private int _start, _end;

        public GltfImportedClip Run()
        {
            string name = string.IsNullOrWhiteSpace(_anim.Name) ? $"animation_{animationIndex}" : _anim.Name!;
            LoadChannels();
            ComputeRestWorlds();
            var x = _anim.Extras;

            // Time base: REDUX's absolute ticks when the header extras are there.
            _absolute = GltfExtras.TryGetInt(x, GltfExtras.StartTime, out int startExtra);
            var allTimes = _rot.Values.Concat(_pos.Values).SelectMany(s => s.Times).ToList();
            _t0 = allTimes.Count > 0 ? allTimes.Min() : 0;
            double tMax = allTimes.Count > 0 ? allTimes.Max() : 0;
            _start = _absolute ? startExtra : options.StartTick;
            _end = GltfExtras.TryGetInt(x, GltfExtras.EndTime, out int endExtra) ? endExtra : Tick(tMax);
            if (_end < _start)
            {
                _warnings.Add($"The end time ({_end}) is before the start ({_start}); the end is set to the start.");
                _end = _start;
            }

            var tracks = new RfaBoneTrack?[target.Count];
            var infos = new GltfBoneImport[target.Count];
            var restored = new HashSet<int>();
            foreach (int bone in target.EvaluationOrder)
            {
                var (track, info) = ImportBone(bone, tracks);
                tracks[bone] = track;
                infos[bone] = info;
                if (info.Mode == GltfBoneImportMode.Restored) restored.Add(bone);
            }

            var clip = new RfaClip
            {
                Version = GltfExtras.TryGetInt(x, GltfExtras.Version, out int v) && v is 7 or 8 ? v : options.Version,
                PosReduction = GltfExtras.TryGetNumber(x, GltfExtras.PosReduction, out double pr) ? (float)pr : 0f,
                RotReduction = GltfExtras.TryGetNumber(x, GltfExtras.RotReduction, out double rr) ? (float)rr : 0f,
                StartTime = _start,
                EndTime = _end,
                RampIn = GltfExtras.TryGetInt(x, GltfExtras.RampIn, out int ri) ? ri : options.RampIn,
                RampOut = GltfExtras.TryGetInt(x, GltfExtras.RampOut, out int ro) ? ro : options.RampOut,
                TotalRotation = GltfExtras.TryGetFloats(x, GltfExtras.TotalRotation, 4, out var tr) ? new Quaternion(tr[0], tr[1], tr[2], tr[3]) : Quaternion.Identity,
                TotalTranslation = GltfExtras.TryGetFloats(x, GltfExtras.TotalTranslation, 3, out var tt) ? new Vector3(tt[0], tt[1], tt[2]) : Vector3.Zero,
                Bones = [.. tracks.Select(t => t!)],
            };
            if (GltfExtras.TryGetInt(x, GltfExtras.MorphVertexCount, out int morph) && morph > 0)
                _warnings.Add($"The source clip had morph (vertex) animation for {morph} vertices; glTF export leaves it out, so this clip has none.");

            if (options.Reduce is { } reduce)
            {
                var bones = Enumerable.Range(0, target.Count).Where(b => !restored.Contains(b)).ToList();
                if (bones.Count > 0)
                {
                    var r = ClipEdit.ReduceKeys(clip, reduce with { Bones = bones });
                    clip = r.Clip;
                    for (int b = 0; b < infos.Length; b++)
                        infos[b] = infos[b] with { RotationKeys = clip.Bones[b].RotationKeys.Length, PositionKeys = clip.Bones[b].PositionKeys.Length };
                }
            }

            var used = new HashSet<int>(map.Entries.Where(e => e.SourceIndex >= 0).Select(e => src.Nodes[e.SourceIndex]));
            var unused = _rot.Keys.Concat(_pos.Keys).Distinct().Where(n => !used.Contains(n)).Order()
                .Select(n => doc.Nodes[n].Name ?? $"node_{n}").ToImmutableArray();
            var report = new GltfAnimationImportReport(animationIndex, name, _start, _end, _absolute, [.. infos], unused, [.. _warnings]);
            return new GltfImportedClip(name, clip, report);
        }

        private int Tick(double seconds) => _absolute ? GltfSpace.Ticks(seconds) : options.StartTick + GltfSpace.Ticks(seconds - _t0);

        private void LoadChannels()
        {
            foreach (var c in _anim.Channels)
            {
                if (c.Target.Node is not { } node || node < 0 || node >= doc.Nodes.Count) continue;
                if (c.Sampler < 0 || c.Sampler >= _anim.Samplers.Count)
                    throw new AssetFormatException($"'{fileName}': animation {animationIndex} has a channel with sampler {c.Sampler}, outside its {_anim.Samplers.Count} samplers.");
                var s = _anim.Samplers[c.Sampler];
                if (c.Target.Path == "rotation")
                {
                    _rot[node] = GltfChannelSampler.Load(doc, s, 4, fileName);
                    _rotRaw[node] = s;
                }
                else if (c.Target.Path == "translation")
                {
                    _pos[node] = GltfChannelSampler.Load(doc, s, 3, fileName);
                    _posRaw[node] = s;
                }
                else if (c.Target.Path == "scale" && !_warnings.Contains("Scale channels are ignored (RF bones do not scale)."))
                {
                    _warnings.Add("Scale channels are ignored (RF bones do not scale).");
                }
                if (GltfExtras.TryGetNumber(c.Extras, GltfExtras.Weight, out double w)) _weights[node] = (float)w;
            }
        }

        private void ComputeRestWorlds()
        {
            _restWorldGl = new Rigid[doc.Nodes.Count];
            var done = new bool[doc.Nodes.Count];
            for (int i = 0; i < doc.Nodes.Count; i++) _restWorldGl[i] = RestWorld(i, done, 0);
        }

        private Rigid RestWorld(int i, bool[] done, int depth)
        {
            if (done[i]) return _restWorldGl[i];
            var local = RestLocal(doc.Nodes[i]);
            int p = _parents[i];
            _restWorldGl[i] = p >= 0 && depth < doc.Nodes.Count ? RestWorld(p, done, depth + 1).Compose(local) : local;
            done[i] = true;
            return _restWorldGl[i];
        }

        // ── One bone ────────────────────────────────────────────────────────

        private (RfaBoneTrack, GltfBoneImport) ImportBone(int bone, RfaBoneTrack?[] tracks)
        {
            string boneName = target.Names[bone];
            var entry = map.Entries[bone];
            if (entry.SourceIndex < 0)
            {
                var track = HoldTrack(bone, out var mode);
                return (track, new GltfBoneImport(bone, boneName, -1, null, mode, track.RotationKeys.Length, track.PositionKeys.Length));
            }
            int node = src.Nodes[entry.SourceIndex];
            string srcName = src.Names[entry.SourceIndex];
            float weight = _weights.TryGetValue(node, out float w) ? w : options.DefaultWeight;

            if (IsDirect(bone, node, out var ancestor))
            {
                var rot = DirectRotation(node, ancestor, out bool rotRestored, out bool rotResampled);
                var pos = DirectPosition(node, ancestor, out bool posRestored);
                var mode = rotRestored && posRestored ? GltfBoneImportMode.Restored : rotResampled ? GltfBoneImportMode.Resampled : GltfBoneImportMode.Keys;
                return (new RfaBoneTrack(weight, rot, pos), new GltfBoneImport(bone, boneName, node, srcName, mode, rot.Length, pos.Length));
            }

            var resampled = Resample(bone, node, weight, tracks);
            return (resampled, new GltfBoneImport(bone, boneName, node, srcName, GltfBoneImportMode.Resampled, resampled.RotationKeys.Length, resampled.PositionKeys.Length));
        }

        /// <summary>
        /// True when the node's glTF locals are the bone's RF locals: the map status is Mapped, the
        /// node's parent IS the parent bone's node (a root's ancestors must be still), and the rest
        /// frames of the node and its parent equal the target's. <paramref name="ancestor"/> is the
        /// still world of a root node's non-bone ancestors (identity otherwise).
        /// </summary>
        private bool IsDirect(int bone, int node, out Rigid ancestor)
        {
            ancestor = Rigid.Identity;
            if (map.Entries[bone].Status != BoneMapStatus.Mapped) return false;
            int parentBone = target.EffectiveParents[bone];
            int glParent = _parents[node];
            if (parentBone >= 0)
            {
                int ps = map.SourceOf(parentBone);
                if (ps < 0 || src.Nodes[ps] != glParent) return false;
                if (!RestMatches(parentBone, glParent)) return false;
            }
            else if (glParent >= 0)
            {
                for (int k = glParent, guard = 0; k >= 0 && guard <= doc.Nodes.Count; k = _parents[k], guard++)
                {
                    if (_rot.ContainsKey(k) || _pos.ContainsKey(k)) return false;
                }
                ancestor = _restWorldGl[glParent];
            }
            return RestMatches(bone, node);
        }

        private bool RestMatches(int bone, int node)
        {
            var rf = GltfSpace.FromGltf(_restWorldGl[node]);
            var t = target.RestWorld[bone];
            return Quat.AngleDegrees(rf.Rotation, t.Rotation) <= RestRotationToleranceDegrees
                && (rf.Position - t.Position).Length() <= RestPositionTolerance * MathF.Max(1f, t.Position.Length());
        }

        private ImmutableArray<RfaRotKey> DirectRotation(int node, Rigid ancestor, out bool restored, out bool resampled)
        {
            restored = false;
            resampled = false;
            Quaternion ToRf(Quaternion gl) => GltfSpace.FromGltf(Quat.Mul(ancestor.Rotation, Quat.Normalize(gl)));
            if (!_rot.TryGetValue(node, out var s) || s.Count == 0)
            {
                var rest = ToRf(RestLocal(doc.Nodes[node]).Rotation);
                return [ClipEdit.QuantizeRotation(_start, rest, null)];
            }
            if (options.UseKeyExtras && TryRestoreRotation(_rotRaw[node], s, ToRf) is { } raw)
            {
                restored = true;
                return raw;
            }

            var keys = new List<RfaRotKey>();
            void Add(int tick, Quaternion active)
            {
                if (keys.Count > 0 && tick <= keys[^1].Time) keys.RemoveAt(keys.Count - 1);
                keys.Add(ClipEdit.QuantizeRotation(tick, active, keys.Count > 0 ? keys[^1] : null));
            }
            switch (s.Interpolation)
            {
                case GltfInterpolation.Linear:
                    for (int i = 0; i < s.Count; i++) Add(Tick(s.Times[i]), ToRf(Q(s.KeyValue(i))));
                    break;
                case GltfInterpolation.Step:
                    for (int i = 0; i < s.Count; i++)
                    {
                        int t = Tick(s.Times[i]);
                        var q = ToRf(Q(s.KeyValue(i)));
                        Add(t, q);
                        if (i + 1 < s.Count && Tick(s.Times[i + 1]) - 1 > t) Add(Tick(s.Times[i + 1]) - 1, q);
                    }
                    break;
                default:
                    resampled = true;
                    foreach (int t in GridAndKeys(s))
                        Add(t, ToRf(s.SampleRotation(Seconds(t))));
                    break;
            }
            return [.. keys];
        }

        private ImmutableArray<RfaPosKey> DirectPosition(int node, Rigid ancestor, out bool restored)
        {
            restored = false;
            Vector3 ToRf(Vector3 gl) => GltfSpace.FromGltf(ancestor.TransformPoint(gl));
            Vector3 DirRf(Vector3 gl) => GltfSpace.FromGltf(ancestor.TransformVector(gl));
            if (!_pos.TryGetValue(node, out var s) || s.Count == 0)
            {
                var rest = ToRf(RestLocal(doc.Nodes[node]).Position);
                return _end > _start ? [RfaPosKey.Constant(_start, rest), RfaPosKey.Constant(_end, rest)] : [RfaPosKey.Constant(_start, rest)];
            }
            if (options.UseKeyExtras && TryRestorePosition(_posRaw[node], s, ToRf) is { } raw)
            {
                restored = true;
                return raw;
            }

            var times = new List<int>();
            var pts = new List<Vector3>();
            var ins = new List<Vector3>();
            var outs = new List<Vector3>();
            void Add(int tick, Vector3 p, Vector3? inC = null, Vector3? outC = null)
            {
                if (times.Count > 0 && tick <= times[^1])
                {
                    times.RemoveAt(times.Count - 1);
                    pts.RemoveAt(pts.Count - 1);
                    ins.RemoveAt(ins.Count - 1);
                    outs.RemoveAt(outs.Count - 1);
                }
                times.Add(tick);
                pts.Add(p);
                ins.Add(inC ?? p);
                outs.Add(outC ?? p);
            }
            if (s.Interpolation == GltfInterpolation.CubicSpline)
            {
                for (int i = 0; i < s.Count; i++)
                {
                    var p = ToRf(V(s.KeyValue(i)));
                    double prevDt = i > 0 ? s.Times[i] - s.Times[i - 1] : 0;
                    double nextDt = i + 1 < s.Count ? s.Times[i + 1] - s.Times[i] : 0;
                    var inC = p - DirRf(V(s.InTangent(i))) * (float)(prevDt / 3);
                    var outC = p + DirRf(V(s.OutTangent(i))) * (float)(nextDt / 3);
                    Add(Tick(s.Times[i]), p, prevDt > 0 ? inC : p, nextDt > 0 ? outC : p);
                }
            }
            else
            {
                for (int i = 0; i < s.Count; i++)
                {
                    int t = Tick(s.Times[i]);
                    var p = ToRf(V(s.KeyValue(i)));
                    Add(t, p);
                    if (s.Interpolation == GltfInterpolation.Step && i + 1 < s.Count && Tick(s.Times[i + 1]) - 1 > t) Add(Tick(s.Times[i + 1]) - 1, p);
                }
                // Linear segments: control points a third of the way to the neighbours (the Bezier is then the line).
                for (int i = 0; i < pts.Count; i++)
                {
                    if (i > 0) ins[i] = pts[i] + (pts[i - 1] - pts[i]) / 3f;
                    if (i + 1 < pts.Count) outs[i] = pts[i] + (pts[i + 1] - pts[i]) / 3f;
                }
            }
            var keys = new RfaPosKey[times.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = new RfaPosKey(times[i], pts[i], ins[i], outs[i]);
            return [.. keys];
        }

        // ── rf_keys restoration ─────────────────────────────────────────────

        private ImmutableArray<RfaRotKey>? TryRestoreRotation(GltfAnimationSampler raw, GltfChannelSampler s, Func<Quaternion, Quaternion> toRf)
        {
            if (GltfExtras.Get(raw.Extras, GltfExtras.Keys) is not JsonObject o || !GltfExtras.TryGetInt(o, "count", out int count) || count < 0) return null;
            try
            {
                ImmutableArray<RfaRotKey> keys;
                if (count == 0) keys = [];
                else
                {
                    if (!GltfExtras.TryGetInt(o, "time_origin", out int origin) || !GltfExtras.TryGetInt(o, "times", out int ta) || !GltfExtras.TryGetInt(o, "values", out int va)) return null;
                    var times = GltfAccessorReader.ReadInts(doc, ta, fileName);
                    var values = GltfAccessorReader.ReadInts(doc, va, fileName);
                    int[]? eases = GltfExtras.TryGetInt(o, "eases", out int ea) ? GltfAccessorReader.ReadInts(doc, ea, fileName) : null;
                    int[]? pads = GltfExtras.TryGetInt(o, "pads", out int pa) ? GltfAccessorReader.ReadInts(doc, pa, fileName) : null;
                    if (times.Length != count || values.Length != count * 4 || (eases is not null && eases.Length != count * 2) || (pads is not null && pads.Length != count)) return null;
                    var b = new RfaRotKey[count];
                    for (int i = 0; i < count; i++)
                    {
                        b[i] = new RfaRotKey(origin + times[i], (short)values[i * 4], (short)values[i * 4 + 1], (short)values[i * 4 + 2], (short)values[i * 4 + 3],
                            eases is null ? (sbyte)0 : (sbyte)eases[i * 2], eases is null ? (sbyte)0 : (sbyte)eases[i * 2 + 1], pads is null ? (short)0 : (short)pads[i]);
                    }
                    keys = [.. b];
                }
                // The glTF keys must still sample the same motion.
                for (int i = 0; i < s.Count; i++)
                {
                    int t = Tick(s.Times[i]);
                    var expected = keys.Length == 0 ? Quaternion.Identity : ClipSampler.SampleRotation(keys.AsSpan(), t);
                    var actual = toRf(Q(s.KeyValue(i)));
                    // A key's own value (not the engine's near-parallel snap) is what the export wrote at key times.
                    double angle = Quat.AngleDegrees(expected, actual);
                    if (angle > RestoreRotationToleranceDegrees)
                    {
                        int k = -1;
                        for (int j = 0; j < keys.Length && k < 0; j++) if (keys[j].Time == t) k = j;
                        if (k < 0 || Quat.AngleDegrees(ClipSampler.KeyRotation(keys[k]), actual) > RestoreRotationToleranceDegrees) return null;
                    }
                }
                return keys;
            }
            catch (AssetFormatException)
            {
                return null;
            }
        }

        private ImmutableArray<RfaPosKey>? TryRestorePosition(GltfAnimationSampler raw, GltfChannelSampler s, Func<Vector3, Vector3> toRf)
        {
            if (GltfExtras.Get(raw.Extras, GltfExtras.Keys) is not JsonObject o || !GltfExtras.TryGetInt(o, "count", out int count) || count < 0) return null;
            try
            {
                ImmutableArray<RfaPosKey> keys;
                if (count == 0) keys = [];
                else
                {
                    if (!GltfExtras.TryGetInt(o, "time_origin", out int origin) || !GltfExtras.TryGetInt(o, "times", out int ta) || !GltfExtras.TryGetInt(o, "points", out int pa)) return null;
                    var times = GltfAccessorReader.ReadInts(doc, ta, fileName);
                    var points = GltfAccessorReader.ReadVector3(doc, pa, fileName);
                    if (times.Length != count || points.Length != count * 3) return null;
                    var b = new RfaPosKey[count];
                    for (int i = 0; i < count; i++) b[i] = new RfaPosKey(origin + times[i], points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
                    keys = [.. b];
                }
                for (int i = 0; i < s.Count; i++)
                {
                    var expected = ClipSampler.SamplePosition(keys.AsSpan(), Tick(s.Times[i]));
                    var actual = toRf(V(s.KeyValue(i)));
                    if ((expected - actual).Length() > RestorePositionTolerance * MathF.Max(1f, expected.Length())) return null;
                }
                // Also between keys (catches edited tangents).
                if (s.Count > 1)
                {
                    for (int i = 0; i + 1 < s.Count; i++)
                    {
                        double mid = (s.Times[i] + s.Times[i + 1]) / 2;
                        int tick = Tick(mid);
                        var expected = ClipSampler.SamplePosition(keys.AsSpan(), tick);
                        var actual = toRf(s.SampleVector(Seconds(tick)));
                        if ((expected - actual).Length() > 10 * RestorePositionTolerance * MathF.Max(1f, expected.Length())) return null;
                    }
                }
                return keys;
            }
            catch (AssetFormatException)
            {
                return null;
            }
        }

        // ── Resampling in model space ───────────────────────────────────────

        private RfaBoneTrack Resample(int bone, int node, float weight, RfaBoneTrack?[] tracks)
        {
            // Rest correction: the target bone's rest frame relative to the node's rest frame.
            var restRf = GltfSpace.FromGltf(_restWorldGl[node]);
            var correction = restRf.Inverse().Compose(target.RestWorld[bone]);
            int parentBone = target.EffectiveParents[bone];
            var times = Grid();
            var rot = new List<RfaRotKey>(times.Count);
            var pts = new List<Vector3>(times.Count);
            foreach (int t in times)
            {
                var world = GltfSpace.FromGltf(NodeWorldAt(node, Seconds(t))).Compose(correction);
                var local = parentBone >= 0 ? BoneWorldAt(parentBone, t, tracks).Inverse().Compose(world) : world;
                rot.Add(ClipEdit.QuantizeRotation(t, local.Rotation, rot.Count > 0 ? rot[^1] : null));
                pts.Add(local.Position);
            }
            var pos = new RfaPosKey[pts.Count];
            for (int i = 0; i < pos.Length; i++)
            {
                var inC = i > 0 ? pts[i] + (pts[i - 1] - pts[i]) / 3f : pts[i];
                var outC = i + 1 < pts.Count ? pts[i] + (pts[i + 1] - pts[i]) / 3f : pts[i];
                pos[i] = new RfaPosKey(times[i], pts[i], inC, outC);
            }
            return new RfaBoneTrack(weight, [.. rot], [.. pos]);
        }

        /// <summary>A target bone's model-space transform at a tick from the tracks already made (parents first).</summary>
        private Rigid BoneWorldAt(int bone, int tick, RfaBoneTrack?[] tracks)
        {
            var local = tracks[bone] is { } track ? ClipSampler.SampleBone(track, tick) : target.RestLocal[bone];
            int p = target.EffectiveParents[bone];
            return p >= 0 ? BoneWorldAt(p, tick, tracks).Compose(local) : local;
        }

        /// <summary>A glTF node's world transform (glTF space) at a time, with every animated ancestor.</summary>
        private Rigid NodeWorldAt(int node, double seconds)
        {
            var result = NodeLocalAt(node, seconds);
            for (int k = _parents[node], guard = 0; k >= 0 && guard <= doc.Nodes.Count; k = _parents[k], guard++)
                result = NodeLocalAt(k, seconds).Compose(result);
            return result;
        }

        private Rigid NodeLocalAt(int node, double seconds)
        {
            var rest = RestLocal(doc.Nodes[node]);
            var r = _rot.TryGetValue(node, out var rs) && rs.Count > 0 ? rs.SampleRotation(seconds) : rest.Rotation;
            var p = _pos.TryGetValue(node, out var ps) && ps.Count > 0 ? ps.SampleVector(seconds) : rest.Position;
            return new Rigid(r, p);
        }

        private RfaBoneTrack HoldTrack(int bone, out GltfBoneImportMode mode)
        {
            Rigid pose;
            float weight = options.DefaultWeight;
            if (options.ReferenceClip is { } reference && bone < reference.BoneCount)
            {
                pose = ClipSampler.SampleBone(reference.Bones[bone], reference.StartTime);
                weight = reference.Bones[bone].Weight;
                mode = GltfBoneImportMode.ReferencePose;
            }
            else
            {
                pose = target.RestLocal[bone];
                mode = GltfBoneImportMode.RestPose;
            }
            var rot = ClipEdit.QuantizeRotation(_start, pose.Rotation, null);
            ImmutableArray<RfaPosKey> pos = _end > _start
                ? [RfaPosKey.Constant(_start, pose.Position), RfaPosKey.Constant(_end, pose.Position)]
                : [RfaPosKey.Constant(_start, pose.Position)];
            return new RfaBoneTrack(weight, [rot], pos);
        }

        private List<int> Grid()
        {
            // The range comes from the file (times or rf_end_time): refuse one no clip could need before
            // walking it, or a damaged time of a few days would resample millions of keys per bone.
            long samples = ((long)_end - _start) / options.SampleStepTicks;
            if (samples > MaxResampledKeys)
            {
                throw new AssetFormatException(
                    $"'{fileName}': animation {animationIndex} runs from tick {_start} to {_end} ({(_end - (double)_start) / RfaClip.TicksPerSecond / 60:F0} minutes), "
                    + $"which would need {samples} resampled keys per bone; at most {MaxResampledKeys} are made. Check the animation's key times and rf_end_time.");
            }
            var list = new List<int>();
            for (long t = _start; t < _end; t += options.SampleStepTicks) list.Add((int)t);
            list.Add(_end);
            return list.Distinct().ToList();
        }

        private IEnumerable<int> GridAndKeys(GltfChannelSampler s)
        {
            var set = new SortedSet<int>(Grid());
            foreach (float t in s.Times) set.Add(Tick(t));
            return set;
        }

        private double Seconds(int tick) => _absolute ? tick / (double)RfaClip.TicksPerSecond : _t0 + (tick - options.StartTick) / (double)RfaClip.TicksPerSecond;

        private static Quaternion Q(Vector4 v) => Quat.Normalize(new Quaternion(v.X, v.Y, v.Z, v.W));

        private static Vector3 V(Vector4 v) => new(v.X, v.Y, v.Z);
    }
}
