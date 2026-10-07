using System.Collections.Immutable;
using System.Globalization;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Retarget;

/// <summary>
/// Frame-offset retargeting (research/anim_retarget/retarget_method.md), ported line for line from
/// the reference (<c>tools/retarget.py</c>) and generalised to any pair of skeletons with a bone map.
/// <para>With <c>W</c> a bone's model-space rotation, <c>s(i)</c> the source bone of target bone
/// <c>i</c> and <c>p(i)</c> its parent:</para>
/// <code>
/// C_i         = W_src_rest(s(i))^-1 * W_tgt_rest(i)       (rest optionally aligned first)
/// W_tgt(i, t) = W_src(s(i), t) * C_i
/// L_tgt(i, t) = C_p^-1 * W_src(s(p), t)^-1 * W_src(s(i), t) * C_i
/// </code>
/// <list type="bullet">
/// <item>target parent maps to the source parent: key by key (<c>C_p^-1 * L_src * C_i</c>), times and eases kept;</item>
/// <item>target root: <c>L_src(root) * C_root</c> (roots are model space);</item>
/// <item>target parent maps to a source ancestor (reparented): the product of the source locals on the
/// chain, sampled at the union of the chain's key times plus start and end, own eases where keys coincide;</item>
/// <item>target parent maps elsewhere (cross-branch, a generalisation): full model-space evaluation per sample;</item>
/// <item>target bones without a source: two static keys (start, end) of the reference clip's pose,
/// weighted like the nearest mapped ancestor;</item>
/// <item>bone lengths: the target's own offsets (reference clip by default), written at the source
/// bone's position-key times with <c>in = out = pos</c>;</item>
/// <item>root translation: the target pelvis anchored on the source pelvis (default), or with
/// <see cref="RootMode.HipHeight"/> the target's hip centre at the source's height above the ground scaled by
/// the leg ratio (<see cref="GroundMapping"/>; lowered where a planted leg could not reach), source key times
/// kept and tangents scaled with the same map;</item>
/// <item>then two-bone IK per chain (arms and/or legs) on keys at most <see cref="RigProfile.IkMaxKeyGap"/> apart:
/// hands and feet back on the source's in model space, end-bone model rotation kept, eases 0. With the
/// hip-height root the feet are held on the target's ground where the source's touch theirs (toe-aware, every
/// 160 ticks) and let go as the source's foot rises; with <see cref="RetargetOptions.OffHandFollowsMainHand"/>
/// the left hand holds its offset from the right while the source's hands are close.</item>
/// <item>what IK held is measured on the finished clip (<see cref="RetargetResult.Contacts"/>).</item>
/// </list>
/// The arithmetic is double precision and the source is sampled with the reference's own sampler
/// (<c>ReferenceSampler</c>), so the stock goldens are reproduced byte for byte. The header is
/// copied from the source; morph data is dropped (with a warning). Unsupported inputs return a
/// failed <see cref="RetargetResult"/> with a plain-language <see cref="RetargetResult.Error"/>.
/// </summary>
public static class Retargeter
{
    /// <summary>Runs one retarget.</summary>
    /// <exception cref="ArgumentNullException">The request (or one of its parts) is null.</exception>
    public static RetargetResult Retarget(RetargetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceClip);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Options);
        try
        {
            return new Job(request).Run();
        }
        catch (RetargetException ex)
        {
            return RetargetResult.Fail(ex.Message, ex.Map, ex.Warnings);
        }
        catch (FormatException ex)
        {
            // A profile with a broken prefix pattern; the message names the pattern and the fix.
            return RetargetResult.Fail(ex.Message);
        }
    }

    private sealed class RetargetException(string message, BoneMap? map, IEnumerable<string> warnings) : Exception(message)
    {
        public BoneMap? Map { get; } = map;
        public IReadOnlyList<string> Warnings { get; } = [.. warnings];
    }

    /// <summary>A rig's working data: canonical names, effective parents, rest rotations in double precision.</summary>
    private sealed class Side
    {
        public Side(RetargetRig rig)
        {
            Rig = rig;
            var sk = rig.Skeleton;
            Count = sk.Count;
            Names = rig.Profile.CanonicalNames(sk.Names);
            Parents = [.. sk.EffectiveParents];
            Order = [.. sk.EvaluationOrder];
            // The profile's root when it names a parentless bone, else the first parentless bone.
            int named = Names.IndexOf(rig.Profile.RootBone ?? string.Empty);
            Root = named >= 0 && Parents[named] < 0 ? named : Array.IndexOf(Parents, -1);
            RestWorld = new DQuat[Count];
            bool raw = !rig.BindRotations.IsDefaultOrEmpty && rig.BindRotations.Length == Count;
            for (int i = 0; i < Count; i++)
            {
                RestWorld[i] = RefMath.Norm(DQuat.From(raw ? rig.BindRotations[i] : sk.RestWorld[i].Rotation));
            }
        }

        public RetargetRig Rig { get; }
        public int Count { get; }
        public ImmutableArray<string> Names { get; }
        public int[] Parents { get; }
        public int[] Order { get; }
        public int Root { get; }
        public DQuat[] RestWorld { get; }

        public int IndexOf(string? canonical) => string.IsNullOrEmpty(canonical) ? -1 : Names.IndexOf(canonical);

        public IEnumerable<int> Ancestors(int i)
        {
            int guard = 0;
            while (Parents[i] >= 0 && guard++ <= Count)
            {
                i = Parents[i];
                yield return i;
            }
        }

        /// <summary>Bones strictly below <paramref name="ancestor"/> down to and including <paramref name="i"/>, top first; null when it is not an ancestor.</summary>
        public List<int>? Chain(int ancestor, int i)
        {
            var path = new List<int>();
            int guard = 0;
            while (i != ancestor)
            {
                if (i < 0 || guard++ > Count) return null;
                path.Add(i);
                i = Parents[i];
            }
            path.Reverse();
            return path;
        }
    }

    /// <summary>A track of the clip being built: quantised rotation keys, double-precision positions.</summary>
    private sealed class Track
    {
        public float Weight;
        public List<RfaRotKey> Rot = [];
        public List<DPosKey> Pos = [];
    }

    private sealed class Job
    {
        private readonly RetargetRequest _request;
        private readonly RetargetOptions _opt;
        private readonly RfaClip _clip;
        private readonly List<string> _report = [];
        private readonly List<string> _warnings = [];
        private BoneMap? _map;

        private Side _src = null!;
        private Side _tgt = null!;
        private int[] _smap = [];
        private DVec3[] _lengths = [];
        private DPosKey[][] _srcPos = [];
        private Track[] _out = [];
        private int[] _span = [];
        private DQuat[] _frameOffsets = [];

        public Job(RetargetRequest request)
        {
            _request = request;
            _opt = request.Options;
            _clip = request.SourceClip;
        }

        private RetargetException Fail(string message) => new(message, _map, _warnings);

        public RetargetResult Run()
        {
            var source = _request.Source;
            var target = _request.Target;
            if (source.Skeleton.Count == 0)
                throw Fail("The source mesh has no bones. Pick the character mesh (.v3c) the source clip was made for.");
            if (target.Skeleton.Count == 0)
                throw Fail("The target mesh has no bones. Pick a character mesh (.v3c) as the target.");
            if (_clip.BoneCount != source.Skeleton.Count)
                throw Fail($"The source clip has {_clip.BoneCount} bones but the source mesh has {source.Skeleton.Count}. "
                    + "Pick the mesh the clip was made for (the same bone count and order) as the source.");
            if (_clip.EndTime < _clip.StartTime)
                throw Fail($"The source clip ends (tick {_clip.EndTime}) before it starts (tick {_clip.StartTime}). Fix its start and end times first.");
            if (_opt.ResampleStep is <= 0)
                throw Fail($"The resample step is {_opt.ResampleStep} ticks; it must be above 0 (160 is one key per frame at 30 fps), or off.");
            if (!target.BindRotations.IsDefaultOrEmpty && target.BindRotations.Length != target.Skeleton.Count)
                throw Fail("The target's stored bind rotations do not match its skeleton. Build the target rig from one mesh (RetargetRig.FromMesh).");
            if (!source.BindRotations.IsDefaultOrEmpty && source.BindRotations.Length != source.Skeleton.Count)
                throw Fail("The source's stored bind rotations do not match its skeleton. Build the source rig from one mesh (RetargetRig.FromMesh).");

            _src = new Side(source);
            _tgt = new Side(target);
            if (_src.Root < 0) throw Fail("The source skeleton has no root bone (every bone has a parent). The mesh is damaged; pick another source mesh.");
            if (_tgt.Root < 0) throw Fail("The target skeleton has no root bone (every bone has a parent). The mesh is damaged; pick another target mesh.");

            // Bone map.
            if (_request.BoneMap is { } given)
            {
                if (!given.Fits(source.Skeleton.Names, target.Skeleton.Names))
                    throw Fail("The bone map was made for other skeletons (its bone names or counts differ from the source and target meshes). "
                        + "Map the bones again for these meshes, or load the map that belongs to them.");
                _map = given;
            }
            else
            {
                _map = BoneMapper.Map(source.Skeleton, source.Profile, target.Skeleton, target.Profile);
            }
            var errors = _map.Validate().Where(p => p.Severity == BoneMapSeverity.Error).Select(p => p.Message).ToList();
            if (errors.Count > 0) throw Fail(string.Join(Environment.NewLine, errors));
            _smap = _map.ToArray();
            if (_smap.All(s => s < 0))
                throw Fail("No target bone has a source bone, so there is nothing to transfer. Check the two rigs' bone names, or map the bones by hand.");

            if (!_clip.Morph.IsEmpty)
                _warnings.Add($"The source clip has morph (vertex) animation for {_clip.Morph.VertexCount} vertices of its own mesh. "
                    + "It cannot be moved to another mesh and was dropped; only the bone animation was retargeted.");

            var reference = target.ReferenceClip;
            bool needReference = _opt.BoneLengths == BoneLengthSource.ReferenceClip
                || (_opt.ExtraBonePose == ExtraBonePose.ReferenceClip && _smap.Any(s => s < 0));
            if (reference is not null && reference.BoneCount != target.Skeleton.Count)
                throw Fail($"The target reference clip has {reference.BoneCount} bones but the target mesh has {target.Skeleton.Count}. "
                    + $"Pick the target rig's own stand clip{Suggest(target.Profile.ReferenceClip)}.");
            if (needReference && reference is null)
                throw Fail("The target has no reference clip. It supplies the target's bone lengths and the pose of bones the source lacks: "
                    + $"pick the target rig's stand clip{Suggest(target.Profile.ReferenceClip)}, or set bone lengths to 'target bind' and extra bones to 'bind'.");

            _span = [_clip.StartTime, _clip.EndTime];
            _srcPos = [.. _clip.Bones.Select(b => ReferenceSampler.Positions(b.PositionKeys.AsSpan()))];
            _lengths = BuildLengths(reference);

            Transfer(reference);
            PlaceRoot();
            if (_opt.Ik) LimbIk();
            var clip = BuildClip();
            return new RetargetResult
            {
                Success = true,
                Clip = clip,
                BoneMap = _map,
                ReportLines = [.. _report],
                Warnings = [.. _warnings],
                Contacts = MeasureContacts(clip),
                Ground = _ground,
            };
        }

        private static string Suggest(string? name) => string.IsNullOrEmpty(name) ? string.Empty : $" ({name})";

        private string ReferenceName =>
            _request.Target.ReferenceClipName ?? _request.Target.Profile.ReferenceClip ?? "the reference clip";

        // ── bone lengths ────────────────────────────────────────────────────

        private DVec3[] BuildLengths(RfaClip? reference)
        {
            var sk = _request.Target.Skeleton;
            var lengths = new DVec3[_tgt.Count];
            var fellBack = new List<string>();
            for (int i = 0; i < _tgt.Count; i++)
            {
                var bind = DVec3.From(sk.RestLocal[i].Position);
                DVec3? fromReference = reference is not null && reference.Bones[i].PositionKeys.Length > 0
                    ? DVec3.From(reference.Bones[i].PositionKeys[0].Position) : null;
                int si = _smap[i];
                switch (_opt.BoneLengths)
                {
                    case BoneLengthSource.ReferenceClip:
                        if (fromReference is { } r) lengths[i] = r;
                        else
                        {
                            lengths[i] = bind;
                            fellBack.Add(_tgt.Names[i]);
                        }
                        break;
                    case BoneLengthSource.TargetBind:
                        lengths[i] = bind;
                        break;
                    default:
                        if (si >= 0 && _srcPos[si].Length > 0) lengths[i] = _srcPos[si][0].Pos;
                        else lengths[i] = fromReference ?? bind;
                        break;
                }
            }
            if (fellBack.Count > 0)
                _warnings.Add($"The reference clip has no position keys for {string.Join(", ", fellBack)}; the bind pose offsets were used for them.");
            return lengths;
        }

        // ── rest alignment and frame offsets ────────────────────────────────

        private DQuat[] AlignedRest()
        {
            var rest = (DQuat[])_tgt.RestWorld.Clone();
            foreach (var (name, child) in _request.Target.Profile.PrimaryChildren)
            {
                int i = _tgt.IndexOf(name), c = _tgt.IndexOf(child);
                if (i < 0 || c < 0) continue;
                int si = _smap[i], sc = _smap[c];
                if (si < 0 || sc < 0) continue;
                if (_tgt.Parents[c] != i || _src.Parents[sc] != si) continue;
                if (_srcPos[sc].Length == 0) continue;
                var dSrc = RefMath.Normalize(RefMath.Rotate(_src.RestWorld[si], _srcPos[sc][0].Pos));
                var dTgt = RefMath.Normalize(RefMath.Rotate(_tgt.RestWorld[i], _lengths[c]));
                rest[i] = RefMath.Mul(RefMath.FromTo(dTgt, dSrc), _tgt.RestWorld[i]);
            }
            return rest;
        }

        private List<int> Grid()
        {
            int step = _opt.ResampleStep ?? 0;
            var times = new List<int>();
            for (long t = _clip.StartTime; t < _clip.EndTime; t += step) times.Add((int)t);
            times.Add(_clip.EndTime);
            return times;
        }

        private void Transfer(RfaClip? reference)
        {
            var tgtRest = _opt.RestAlignment ? AlignedRest() : _tgt.RestWorld;
            var C = new DQuat[_tgt.Count];
            for (int i = 0; i < _tgt.Count; i++)
            {
                if (_smap[i] >= 0) C[i] = RefMath.Mul(RefMath.Conj(_src.RestWorld[_smap[i]]), tgtRest[i]);
            }
            _frameOffsets = C;

            _out = new Track[_tgt.Count];
            bool resample = _opt.ResampleStep is not null;
            for (int i = 0; i < _tgt.Count; i++)
            {
                int si = _smap[i];
                int tp = _tgt.Parents[i];
                var track = new Track();
                IReadOnlyList<int> posTimes;
                if (si < 0)
                {
                    // Extra bone: a static pose, riding on its parent.
                    DQuat q;
                    string from;
                    if (_opt.ExtraBonePose == ExtraBonePose.ReferenceClip && reference is not null)
                    {
                        var keys = reference.Bones[i].RotationKeys;
                        q = keys.Length > 0 ? ReferenceSampler.KeyRotation(keys[0]) : DQuat.Identity;
                        from = ReferenceName;
                    }
                    else
                    {
                        q = tp < 0 ? _tgt.RestWorld[i] : RefMath.Mul(RefMath.Conj(_tgt.RestWorld[tp]), _tgt.RestWorld[i]);
                        from = "the bind pose";
                    }
                    track.Rot = MakeKeys(_span.Select(t => (t, q, (sbyte)0, (sbyte)0)));
                    track.Weight = MappedAncestorWeight(i);
                    posTimes = _span;
                    _report.Add(string.Format(CultureInfo.InvariantCulture, "{0,-14} static from {1}", _tgt.Names[i], from));
                }
                else
                {
                    var sb = _clip.Bones[si];
                    var keys = sb.RotationKeys;
                    track.Weight = sb.Weight;
                    if (tp < 0)
                    {
                        // Validated by the bone map: the source bone is a root too.
                        track.Rot = KeyByKey(keys, resample, q => RefMath.Mul(q, C[i]));
                    }
                    else
                    {
                        int stp = _smap[tp];
                        var cpInv = RefMath.Conj(C[tp]);
                        if (_src.Parents[si] == stp)
                        {
                            track.Rot = KeyByKey(keys, resample, q => RefMath.Mul(RefMath.Mul(cpInv, q), C[i]));
                        }
                        else if (_src.Chain(stp, si) is { } chain)
                        {
                            var set = new SortedSet<int>(chain.SelectMany(b => _clip.Bones[b].RotationKeys.Select(k => k.Time)));
                            set.UnionWith(_span);
                            var times = resample ? Grid() : [.. set];
                            track.Rot = Resampled(times, keys, t =>
                            {
                                var q = DQuat.Identity;
                                foreach (int b in chain) q = RefMath.Mul(q, ReferenceSampler.SampleRotation(_clip.Bones[b].RotationKeys.AsSpan(), t));
                                return RefMath.Mul(RefMath.Mul(cpInv, q), C[i]);
                            });
                            _report.Add(string.Format(CultureInfo.InvariantCulture, "{0,-14} reparented: source chain {1} resampled at {2} keys",
                                _tgt.Names[i], string.Join("*", chain.Select(b => _src.Names[b])), times.Count));
                        }
                        else
                        {
                            // Cross-branch: W_src(s(p))^-1 * W_src(s(i)), both from the source root.
                            var pathP = _src.Chain(-1, stp)!;
                            var pathI = _src.Chain(-1, si)!;
                            var set = new SortedSet<int>(pathP.Concat(pathI).SelectMany(b => _clip.Bones[b].RotationKeys.Select(k => k.Time)));
                            set.UnionWith(_span);
                            var times = resample ? Grid() : [.. set];
                            track.Rot = Resampled(times, keys, t =>
                            {
                                var wp = ChainRotation(pathP, t);
                                var wi = ChainRotation(pathI, t);
                                return RefMath.Mul(RefMath.Mul(cpInv, RefMath.Mul(RefMath.Conj(wp), wi)), C[i]);
                            });
                            _report.Add(string.Format(CultureInfo.InvariantCulture, "{0,-14} cross-branch: evaluated in model space at {1} keys",
                                _tgt.Names[i], times.Count));
                        }
                    }
                    posTimes = sb.PositionKeys.Length > 0 ? [.. sb.PositionKeys.Select(k => k.Time)] : _span;
                }
                if (posTimes.Count < 2) posTimes = _span;

                if (si >= 0 && _opt.BoneLengths == BoneLengthSource.Source && _srcPos[si].Length >= 2)
                {
                    track.Pos = [.. _srcPos[si]];
                }
                else
                {
                    var L = _lengths[i];
                    track.Pos = [.. posTimes.Select(t => DPosKey.Constant(t, L))];
                }
                _out[i] = track;
            }
        }

        private List<RfaRotKey> MakeKeys(IEnumerable<(int Time, DQuat Rot, sbyte EaseIn, sbyte EaseOut)> samples) =>
            RefMath.MakeRotKeys(samples, _opt.Quantization == KeyQuantization.WithinUnit);

        private DQuat ChainRotation(List<int> path, int t)
        {
            var q = DQuat.Identity;
            foreach (int b in path) q = RefMath.Mul(q, ReferenceSampler.SampleRotation(_clip.Bones[b].RotationKeys.AsSpan(), t));
            return q;
        }

        private List<RfaRotKey> KeyByKey(ImmutableArray<RfaRotKey> keys, bool resample, Func<DQuat, DQuat> convert)
        {
            if (resample || keys.Length == 0)
            {
                var times = resample ? Grid() : [.. _span];
                return MakeKeys(times.Select(t => (t, convert(ReferenceSampler.SampleRotation(keys.AsSpan(), t)), (sbyte)0, (sbyte)0)));
            }
            return MakeKeys(keys.Select(k => (k.Time, convert(ReferenceSampler.KeyRotation(k)), k.EaseIn, k.EaseOut)));
        }

        private List<RfaRotKey> Resampled(List<int> times, ImmutableArray<RfaRotKey> own, Func<int, DQuat> sample)
        {
            var byTime = new Dictionary<int, RfaRotKey>();
            foreach (var k in own) byTime[k.Time] = k;
            return MakeKeys(times.Select(t =>
            {
                bool has = byTime.TryGetValue(t, out var k);
                return (t, sample(t), has ? k.EaseIn : (sbyte)0, has ? k.EaseOut : (sbyte)0);
            }));
        }

        private float MappedAncestorWeight(int i)
        {
            foreach (int a in _tgt.Ancestors(i))
            {
                if (_smap[a] >= 0) return _clip.Bones[_smap[a]].Weight;
            }
            return _clip.Bones[_src.Root].Weight;
        }

        // ── root translation ────────────────────────────────────────────────

        private void PlaceRoot()
        {
            int sr = _src.Root, tr = _tgt.Root;
            var srcRootPos = _srcPos[sr];
            switch (_opt.RootMode)
            {
                case RootMode.AnchorPelvis:
                    AnchorPelvis(sr, tr);
                    break;
                case RootMode.HipHeight:
                    HipHeight(sr, tr);
                    break;
                case RootMode.CopySource:
                    _out[tr].Pos = srcRootPos.Length >= 2 ? [.. srcRootPos] : SpanKeys(srcRootPos);
                    _report.Add($"root copied from the source ({_out[tr].Pos.Count} keys)");
                    break;
                default:
                    _report.Add(string.Format(CultureInfo.InvariantCulture, "root kept in place at the target's own root offset ({0} keys)", _out[tr].Pos.Count));
                    break;
            }
        }

        private List<DPosKey> SpanKeys(DPosKey[] keys) =>
            [.. _span.Select(t => DPosKey.Constant(t, ReferenceSampler.SamplePosition(keys, t)))];

        // ── hip height (standing / locomotion) ──────────────────────────────

        /// <summary>A leg chain resolved on both rigs (toe = the end's mapped primary child, or -1).</summary>
        private sealed record Leg(int Tu, int Tl, int Te, int Tc, int Su, int Sl, int Se, int Sc);

        private List<Leg> _legs = [];
        private GroundMapping? _ground;

        private List<Leg> ResolveLegs()
        {
            var legs = new List<Leg>();
            var profile = _request.Target.Profile;
            foreach (var chain in profile.IkChains)
            {
                if (!RigProfile.IsLegChain(chain)) continue;
                int tu = _tgt.IndexOf(chain.Upper), tl = _tgt.IndexOf(chain.Lower), te = _tgt.IndexOf(chain.End);
                if (tu < 0 || tl < 0 || te < 0 || _tgt.Parents[tl] != tu || _tgt.Parents[te] != tl) continue;
                int su = _smap[tu], sl = _smap[tl], se = _smap[te];
                if (su < 0 || sl < 0 || se < 0) continue;
                int tc = profile.PrimaryChildren.TryGetValue(chain.End, out var toe) ? _tgt.IndexOf(toe) : -1;
                int sc = tc >= 0 && _tgt.Parents[tc] == te ? _smap[tc] : -1;
                if (sc < 0 || _src.Parents[sc] != se) tc = sc = -1;
                legs.Add(new Leg(tu, tl, te, tc, su, sl, se, sc));
            }
            return legs;
        }

        private static DVec3 Centre(DRigid[] world, IEnumerable<int> bones)
        {
            double x = 0, y = 0, z = 0;
            int n = 0;
            foreach (int b in bones)
            {
                x += world[b].Pos.X;
                y += world[b].Pos.Y;
                z += world[b].Pos.Z;
                n++;
            }
            return n == 0 ? DVec3.Zero : new DVec3(x / n, y / n, z / n);
        }

        private static double Sole(DRigid[] world, int ankle, int toe) =>
            toe >= 0 ? Math.Min(world[ankle].Pos.Y, world[toe].Pos.Y) : world[ankle].Pos.Y;

        /// <summary>
        /// A rig's ground (lowest ankle-or-toe point over a clip, every 160 ticks), the planted ankle's
        /// height above it, and the planted foot's model rotation at that moment.
        /// </summary>
        private static (double Ground, double Ankle, DQuat FootRot, int Foot) GroundOf(Side side, RfaClip clip, IReadOnlyList<(int Ankle, int Toe)> feet)
        {
            var pos = clip.Bones.Select(b => ReferenceSampler.Positions(b.PositionKeys.AsSpan())).ToArray();
            double ground = double.PositiveInfinity, ankle = double.PositiveInfinity;
            var footRot = DQuat.Identity;
            int foot = 0;
            var local = new DRigid[side.Count];
            foreach (int t in RetargetReport.SampleTimes(clip, 160))
            {
                for (int i = 0; i < local.Length; i++)
                    local[i] = new DRigid(ReferenceSampler.SampleRotation(clip.Bones[i].RotationKeys.AsSpan(), t), ReferenceSampler.SamplePosition(pos[i], t));
                var world = ReferenceSampler.Solve(side.Parents, side.Order, local);
                for (int f = 0; f < feet.Count; f++)
                {
                    var (a, toe) = feet[f];
                    double sole = Sole(world, a, toe);
                    if (sole < ground)
                    {
                        ground = sole;
                        footRot = world[a].Rot;
                        foot = f;
                    }
                    ankle = Math.Min(ankle, world[a].Pos.Y);
                }
            }
            return (ground, ankle - ground, footRot, foot);
        }

        private void BuildGround()
        {
            _legs = ResolveLegs();
            if (_legs.Count == 0)
                throw Fail($"Root mode 'hip height' needs a leg IK chain (upper leg, lower leg, foot) that both rigs have, and the target profile '{_request.Target.Profile.Name}' has none that maps onto the source. "
                    + "Choose another root mode (anchor pelvis, copy source, keep in place), or add the leg chains to the profile.");

            // Leg ratio: the target's mean leg (thigh + shin) over the source's. Uneven legs (the
            // female's right leg is 2 cm shorter; the park_* exports' left legs are 9 cm longer than
            // their right) are then taken as the body the hips were placed for, and ReachableRatio
            // lowers the hips only where a planted target leg could not otherwise reach.
            var legLengths = _legs.Select(l => (
                T: RefMath.Length(_lengths[l.Tl]) + RefMath.Length(_lengths[l.Te]),
                S: RefMath.Length(SourceOffset(l.Sl)) + RefMath.Length(SourceOffset(l.Se)))).ToList();
            if (legLengths.Any(r => !(r.S > 1e-6) || !(r.T > 1e-6)))
                throw Fail("Root mode 'hip height' needs legs with length on both rigs, and one rig's leg bones have no offsets. Check the clips' position keys, or choose another root mode.");
            double ratio = legLengths.Average(r => r.T) / legLengths.Average(r => r.S);

            // The source's ground: its reference (stand) clip, else the clip itself.
            var srcFeet = _legs.Select(l => (l.Se, l.Sc)).ToList();
            var srcRef = _request.Source.ReferenceClip;
            string srcFrom;
            (double Ground, double Ankle, DQuat FootRot, int Foot) sg;
            if (srcRef is not null && srcRef.BoneCount == _src.Count)
            {
                sg = GroundOf(_src, srcRef, srcFeet);
                srcFrom = _request.Source.ReferenceClipName ?? _request.Source.Profile.ReferenceClip ?? "the source's reference clip";
            }
            else
            {
                sg = GroundOf(_src, _clip, srcFeet);
                srcFrom = "the source clip itself";
                _warnings.Add("The source rig has no reference (stand) clip, so the source's ground was taken from the clip itself (its lowest foot point). "
                    + "For a clip whose feet never touch the ground (a jump, a swim) pick the source rig's stand clip, or the result will stand too low.");
            }

            // The target's ground: its reference clip, else its bind pose.
            var tgtFeet = _legs.Select(l => (l.Te, l.Tc)).ToList();
            var reference = _request.Target.ReferenceClip;
            double tGround;
            string tgtFrom;
            if (reference is not null && reference.BoneCount == _tgt.Count)
            {
                tGround = GroundOf(_tgt, reference, tgtFeet).Ground;
                tgtFrom = ReferenceName;
            }
            else
            {
                var bind = new DRigid[_tgt.Count];
                var sk = _request.Target.Skeleton;
                for (int i = 0; i < bind.Length; i++) bind[i] = new DRigid(_tgt.RestWorld[i], DVec3.From(sk.RestWorld[i].Position));
                tGround = tgtFeet.Min(f => Sole(bind, f.Te, f.Tc));
                tgtFrom = "the target's bind pose";
                _warnings.Add("The target has no reference (stand) clip, so its ground was taken from its bind pose, which may not stand where the rig's own clips do. "
                    + "Pick the target rig's stand clip for an accurate ground.");
            }

            // Ankle heights: the source's planted ankle in its reference; the target's ankle above its
            // lowest foot point when its foot points as the source's planted foot did (the frame offset
            // carries the source's foot orientation over, so this is where the target's ankle stands).
            double tAnkle = 0.0;
            var planted = _legs[sg.Foot];
            if (planted.Tc >= 0)
            {
                var drop = RefMath.Rotate(RefMath.Mul(sg.FootRot, _frameOffsets[planted.Te]), _lengths[planted.Tc]);
                tAnkle = Math.Max(0.0, -drop.Y);
            }
            _ground = new GroundMapping(sg.Ground, tGround, sg.Ankle, tAnkle, ratio, _opt.ScaleStride ? ratio : 1.0, srcFrom, tgtFrom);
            _report.Add(string.Format(CultureInfo.InvariantCulture,
                "ground: source {0:0.000} m ({1}), target {2:0.000} m ({3}); ankle heights {4:0.000} / {5:0.000} m; leg ratio {6:0.000}{7}",
                sg.Ground, srcFrom, tGround, tgtFrom, sg.Ankle, tAnkle, ratio, _opt.ScaleStride ? ", strides scaled" : string.Empty));

            // Where the source stands with a leg (almost) straight, the target's planted leg must still
            // reach its foot: lower the hips just enough (never by more than 8 % of the ratio).
            // (With scaled strides the horizontal distances move with the ratio too: iterate.)
            double reachable = ratio;
            for (int pass = 0; pass < (_opt.ScaleStride ? 3 : 1); pass++)
            {
                if (ReachableRatio(ratio, _opt.ScaleStride ? reachable : 1.0) is not { } r) break;
                reachable = r;
            }
            if (reachable < ratio)
            {
                _ground = _ground with { LegRatio = reachable, HorizontalScale = _opt.ScaleStride ? reachable : 1.0 };
                _report.Add(string.Format(CultureInfo.InvariantCulture,
                    "hips lowered: leg ratio {0:0.000} instead of {1:0.000}, so the target's planted legs reach where the source stands with a leg almost straight", reachable, ratio));
            }
        }

        /// <summary>
        /// The largest leg ratio (at most <paramref name="nominal"/>, at least 92 % of it) at which every
        /// leg held fully on the ground can reach its foot at every sampled time, or null when no planted
        /// leg limits it. The hip-to-foot height is linear in the ratio, the horizontal distance does not
        /// depend on it, so each planted leg gives its limit in closed form.
        /// </summary>
        private double? ReachableRatio(double nominal, double horizontalScale)
        {
            var g = _ground!;
            double limit = double.PositiveInfinity;
            foreach (int t in RetargetReport.SampleTimes(_clip, 160))
            {
                var ws = WorldSource(t);
                var wt = WorldOut(t);
                var srcHip = Centre(ws, _legs.Select(l => l.Su));
                var tgtHip = Centre(wt, _legs.Select(l => l.Tu));
                foreach (var leg in _legs)
                {
                    double sole = Sole(ws, leg.Se, leg.Sc);
                    if (Math.Clamp((ContactRelease - (sole - g.SourceGround)) / (ContactRelease - ContactFull), 0.0, 1.0) < 0.999) continue;
                    var o = RefMath.Sub(wt[leg.Tu].Pos, tgtHip);
                    double drop = leg.Tc >= 0 ? Math.Max(0.0, -RefMath.Rotate(wt[leg.Te].Rot, _lengths[leg.Tc]).Y) : 0.0;
                    var ankle = ws[leg.Se].Pos;
                    double dx = (srcHip.X - ankle.X) * horizontalScale + o.X, dz = (srcHip.Z - ankle.Z) * horizontalScale + o.Z;
                    double c0 = g.TargetAnkleHeight + o.Y - drop;
                    double c1 = srcHip.Y - g.SourceAnkleHeight - sole;
                    double reach = RefMath.Length(_lengths[leg.Tl]) + RefMath.Length(_lengths[leg.Te]) - 0.0005;
                    double rhs = reach * reach - dx * dx - dz * dz;
                    if (rhs <= 0.0 || c1 <= 1e-6) continue;
                    double r = (Math.Sqrt(rhs) - c0) / c1;
                    if (r < limit) limit = r;
                }
            }
            if (double.IsPositiveInfinity(limit) || limit >= nominal) return null;
            return Math.Max(limit, nominal * 0.92);
        }

        private DVec3 SourceOffset(int si) => _srcPos[si].Length > 0 ? _srcPos[si][0].Pos : DVec3.Zero;

        /// <summary>Where the target's hip centre goes for a source hip centre.</summary>
        private DVec3 MapHip(DVec3 p)
        {
            var g = _ground!;
            return new DVec3(p.X * g.HorizontalScale,
                g.TargetGround + g.TargetAnkleHeight + (p.Y - g.SourceGround - g.SourceAnkleHeight) * g.LegRatio,
                p.Z * g.HorizontalScale);
        }

        private void HipHeight(int sr, int tr)
        {
            BuildGround();
            var g = _ground!;
            var srcRootPos = _srcPos[sr].Length > 0 ? _srcPos[sr] : [.. SpanKeys(_srcPos[sr])];
            var scale = new DVec3(g.HorizontalScale, g.LegRatio, g.HorizontalScale);
            var keys = new List<DPosKey>();
            foreach (var k in srcRootPos)
            {
                int t = k.Time;
                var want = MapHip(Centre(WorldSource(t), _legs.Select(l => l.Su)));
                var wt = WorldOut(t);
                var offset = RefMath.Sub(Centre(wt, _legs.Select(l => l.Tu)), wt[tr].Pos);
                var p = RefMath.Sub(want, offset);
                // The source root's motion between keys is scaled by the same map, so its tangents are.
                keys.Add(new DPosKey(t, p, RefMath.Add(p, Mul(scale, RefMath.Sub(k.In, k.Pos))), RefMath.Add(p, Mul(scale, RefMath.Sub(k.Out, k.Pos)))));
            }
            if (keys.Count < 2) keys = [.. _span.Select(t => DPosKey.Constant(t, keys.Count > 0 ? keys[0].Pos : DVec3.Zero))];
            _out[tr].Pos = keys;
            _report.Add($"root placed so the hips stand at the source's height above the ground, scaled by the leg ratio ({keys.Count} keys)");
        }

        private static DVec3 Mul(DVec3 s, DVec3 v) => new(s.X * v.X, s.Y * v.Y, s.Z * v.Z);

        private void AnchorPelvis(int sr, int tr)
        {
            var sProfile = _request.Source.Profile;
            var tProfile = _request.Target.Profile;
            int sp = _src.IndexOf(sProfile.PelvisBone), tp = _tgt.IndexOf(tProfile.PelvisBone);
            if (sp < 0 || tp < 0)
            {
                string which = sp < 0 ? $"source ('{sProfile.Name}')" : $"target ('{tProfile.Name}')";
                throw Fail($"Root mode 'anchor pelvis' needs a pelvis bone on both rigs, but the {which} profile names none that its skeleton has. "
                    + "Set the profile's pelvis bone, or choose another root mode (copy source, scale by leg length, keep in place).");
            }
            if (_src.Parents[sp] != sr || _tgt.Parents[tp] != tr)
                throw Fail("Root mode 'anchor pelvis' needs the pelvis to be a direct child of the root on both rigs, and it is not. "
                    + "Choose another root mode (copy source, scale by leg length, keep in place).");

            var srcRootRot = _clip.Bones[sr].RotationKeys.AsSpan();
            var srcRootPos = _srcPos[sr].Length > 0 ? _srcPos[sr] : [.. SpanKeys(_srcPos[sr])];
            var outRootRot = _out[tr].Rot.ToArray();
            var keys = new List<DPosKey>();
            foreach (var k in srcRootPos)
            {
                int t = k.Time;
                var sRootRot = ReferenceSampler.SampleRotation(srcRootRot, t);
                var sPel = RefMath.Add(k.Pos, RefMath.Rotate(sRootRot, ReferenceSampler.SamplePosition(_srcPos[sp], t)));
                var tRootRot = ReferenceSampler.SampleRotation(outRootRot, t);
                var p = RefMath.Sub(sPel, RefMath.Rotate(tRootRot, _lengths[tp]));
                var d = RefMath.Sub(p, k.Pos);
                keys.Add(new DPosKey(t, p, RefMath.Add(k.In, d), RefMath.Add(k.Out, d)));
            }
            if (keys.Count < 2) keys = [.. _span.Select(t => DPosKey.Constant(t, keys.Count > 0 ? keys[0].Pos : DVec3.Zero))];
            _out[tr].Pos = keys;
            _report.Add($"root placed so the pelvis matches the source ({keys.Count} keys)");
        }

        // ── limb IK ─────────────────────────────────────────────────────────

        private SortedSet<int> ChainTimes(Func<int, IEnumerable<int>> keyTimes, Side side, int i)
        {
            var ts = new SortedSet<int>();
            int guard = 0;
            while (i >= 0 && guard++ <= side.Count)
            {
                ts.UnionWith(keyTimes(i));
                i = side.Parents[i];
            }
            return ts;
        }

        private static List<int> Densify(List<int> times, int gap)
        {
            var result = new List<int>();
            for (int j = 0; j + 1 < times.Count; j++)
            {
                int a = times[j], b = times[j + 1];
                result.Add(a);
                long n = ((long)b - a + gap - 1) / gap;
                for (long m = 1; m < n; m++) result.Add((int)(a + ((long)b - a) * m / n));
            }
            result.Add(times[^1]);
            return result;
        }

        private DRigid[] WorldOut(int t)
        {
            var local = new DRigid[_tgt.Count];
            for (int i = 0; i < local.Length; i++)
            {
                var tr = _out[i];
                local[i] = new DRigid(
                    ReferenceSampler.SampleRotation(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tr.Rot), t),
                    ReferenceSampler.SamplePosition(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tr.Pos), t));
            }
            return ReferenceSampler.Solve(_tgt.Parents, _tgt.Order, local);
        }

        private DRigid[] WorldSource(int t)
        {
            var local = new DRigid[_src.Count];
            for (int i = 0; i < local.Length; i++)
            {
                local[i] = new DRigid(ReferenceSampler.SampleRotation(_clip.Bones[i].RotationKeys.AsSpan(), t), ReferenceSampler.SamplePosition(_srcPos[i], t));
            }
            return ReferenceSampler.Solve(_src.Parents, _src.Order, local);
        }

        /// <summary>What an IK chain holds its end to.</summary>
        private enum Hold
        {
            /// <summary>The source's end joint in model space (the reference; seated riders).</summary>
            Source,
            /// <summary>The source's foot mapped onto the target's ground (<see cref="RootMode.HipHeight"/>).</summary>
            Ground,
            /// <summary>The source's end joint moved with the hips (<see cref="RootMode.HipHeight"/>, arms).</summary>
            HipShift,
            /// <summary>The source's off-hand offset from the main hand, at the target's main hand.</summary>
            OffHand,
        }

        /// <summary>One IK chain to run: target and source bones, lengths, what it holds.</summary>
        private sealed record ChainPlan(IkChain Chain, int Tu, int Tl, int Te, int Tc, int Su, int Sl, int Se, int Sc, bool IsLeg, Hold Hold, double A, double B, DVec3 Bias)
        {
            /// <summary>For <see cref="Hold.OffHand"/>: the main hand's target and source bones, and what the hand holds when the hands are apart.</summary>
            public int MainTe { get; init; } = -1;
            public int MainSe { get; init; } = -1;
            public Hold Base { get; init; } = Hold.Source;
            public bool BaseIsFk { get; init; }
        }

        private readonly List<ChainPlan> _plans = [];

        private string HeldTo(ChainPlan p) => p.Hold switch
        {
            Hold.Ground => "the ground contact",
            Hold.HipShift => "the source's position, moved with the hips",
            Hold.OffHand => "the main hand (two-handed grip)",
            _ => "the source's position",
        };

        /// <summary>The point a chain's end should reach at one time, and how firmly (0..1; only the off hand fades).</summary>
        private (DVec3 Target, double Weight) Wanted(ChainPlan p, DRigid[] ws, DRigid[] wt) => p.Hold switch
        {
            Hold.OffHand => OffHandTarget(p, ws, wt),
            _ => (BaseTarget(p, p.Hold, ws, wt), 1.0),
        };

        private DVec3 BaseTarget(ChainPlan p, Hold hold, DRigid[] ws, DRigid[] wt)
        {
            switch (hold)
            {
                case Hold.Ground:
                {
                    // The foot's lowest point (ankle or toe) stands as high above the target's ground as
                    // the source's above its own, scaled by the leg ratio; the ankle sits above that
                    // point by however far the target's toe hangs below its ankle right now.
                    var g = _ground!;
                    var ankle = ws[p.Se].Pos;
                    double sole = Sole(ws, p.Se, p.Sc);
                    double drop = p.Tc >= 0 ? Math.Max(0.0, -RefMath.Rotate(wt[p.Te].Rot, _lengths[p.Tc]).Y) : 0.0;
                    return new DVec3(ankle.X * g.HorizontalScale, g.TargetGround + (sole - g.SourceGround) * g.LegRatio + drop, ankle.Z * g.HorizontalScale);
                }
                case Hold.HipShift:
                {
                    var hip = Centre(ws, _legs.Select(l => l.Su));
                    return RefMath.Add(ws[p.Se].Pos, RefMath.Sub(MapHip(hip), hip));
                }
                default:
                    return ws[p.Se].Pos;
            }
        }

        private (DVec3, double) OffHandTarget(ChainPlan p, DRigid[] ws, DRigid[] wt)
        {
            var delta = RefMath.Sub(ws[p.Se].Pos, ws[p.MainSe].Pos);
            double d = RefMath.Length(delta);
            double w = Math.Clamp((_opt.OffHandGripDistance + OffHandFade - d) / OffHandFade, 0.0, 1.0);
            var hold = RefMath.Add(wt[p.MainTe].Pos, delta);
            var free = p.BaseIsFk ? wt[p.Te].Pos : BaseTarget(p, p.Base, ws, wt);
            return (RefMath.Add(free, RefMath.Scale(RefMath.Sub(hold, free), w)), w);
        }

        private const double OffHandFade = 0.10;

        /// <summary>Metres above the source's ground: a foot this low is held fully on the target's ground.</summary>
        private const double ContactFull = 0.10;

        /// <summary>Metres above the source's ground: a foot this high is not held at all (the leg keeps the source's angles).</summary>
        private const double ContactRelease = 0.25;

        /// <summary>How firmly a leg is held to the ground at one time: 1 while the source's foot is near its ground, fading to 0 as it rises.</summary>
        private double ContactWeight(ChainPlan p, DRigid[] ws)
        {
            double h = Sole(ws, p.Se, p.Sc) - _ground!.SourceGround;
            return Math.Clamp((ContactRelease - h) / (ContactRelease - ContactFull), 0.0, 1.0);
        }

        private void LimbIk()
        {
            var profile = _request.Target.Profile;
            var disabled = new HashSet<string>(_opt.DisabledIkChains.IsDefault ? [] : _opt.DisabledIkChains, StringComparer.OrdinalIgnoreCase);
            int gap = profile.IkMaxKeyGap > 0 ? profile.IkMaxKeyGap : 320;

            // The two-handed grip: the right-hand chain is the main hand, the left-hand chain the off hand.
            IkChain? main = null, off = null;
            if (_opt.OffHandFollowsMainHand)
            {
                foreach (var c in profile.IkChains.Where(c => !RigProfile.IsLegChain(c)))
                {
                    var tokens = BoneTokens.Parse(c.End);
                    if (!tokens.Tokens.Contains("hand")) continue;
                    if (tokens.Side == BoneSide.Right) main ??= c;
                    else if (tokens.Side == BoneSide.Left) off ??= c;
                }
                if (main is null || off is null)
                {
                    // The grip is on by default with the standing preset: a rig with no arm chains at all (a
                    // creature, a vehicle) simply has nothing to grip with, which is not worth a warning.
                    if (profile.IkChains.Any(c => !RigProfile.IsLegChain(c)))
                        _warnings.Add("The off hand cannot follow the main hand: the target profile needs a left and a right arm chain ending in a hand.");
                    else
                        _report.Add("two-handed grip: the target profile has no arm chains, so there is no off hand to hold");
                    off = null;
                }
            }
            int mainTe = main is null ? -1 : _tgt.IndexOf(main.End);
            int mainSe = mainTe >= 0 ? _smap[mainTe] : -1;
            if (off is not null && mainSe < 0)
            {
                _warnings.Add("The off hand cannot follow the main hand: the target's right hand has no source bone.");
                off = null;
            }
            // The main hand must be final before the off hand looks at it.
            var order = off is null ? profile.IkChains.ToList() : [.. profile.IkChains.Where(c => !ReferenceEquals(c, off)), off];

            foreach (var chain in order)
            {
                bool leg = RigProfile.IsLegChain(chain);
                bool isOff = ReferenceEquals(chain, off);
                bool wanted = leg ? _opt.IkLegs : _opt.IkArms;
                if (disabled.Contains(chain.Upper))
                {
                    _report.Add($"IK {chain.Upper} off");
                    continue;
                }
                if (!wanted && !isOff)
                {
                    _report.Add($"IK {chain.Upper} off ({(leg ? "legs" : "arms")} follow the source's rotations)");
                    continue;
                }
                int tu = _tgt.IndexOf(chain.Upper), tl = _tgt.IndexOf(chain.Lower), te = _tgt.IndexOf(chain.End);
                if (tu < 0 || tl < 0 || te < 0)
                {
                    _warnings.Add($"The IK chain '{chain.Upper}' was skipped: the target skeleton has no bone named '{(tu < 0 ? chain.Upper : tl < 0 ? chain.Lower : chain.End)}'. Fix the chain in the target profile.");
                    continue;
                }
                if (_tgt.Parents[tl] != tu || _tgt.Parents[te] != tl || _tgt.Parents[tu] < 0)
                {
                    _warnings.Add($"The IK chain '{chain.Upper}' was skipped: '{chain.Upper}' -> '{chain.Lower}' -> '{chain.End}' is not a parent chain under a parent bone in the target skeleton. Fix the chain in the target profile.");
                    continue;
                }
                int su = _smap[tu], sl = _smap[tl], se = _smap[te];
                if (su < 0 || sl < 0 || se < 0)
                {
                    _warnings.Add($"The IK chain '{chain.Upper}' was skipped: not all of its bones have a source bone, so there is no source hand or foot to reach for.");
                    continue;
                }
                int tc = profile.PrimaryChildren.TryGetValue(chain.End, out var child) ? _tgt.IndexOf(child) : -1;
                int sc = tc >= 0 && _tgt.Parents[tc] == te ? _smap[tc] : -1;
                if (sc < 0 || _src.Parents[sc] != se) tc = sc = -1;

                var baseHold = _ground is null ? Hold.Source : leg ? Hold.Ground : Hold.HipShift;
                var plan = new ChainPlan(chain, tu, tl, te, tc, su, sl, se, sc, leg, baseHold,
                    RefMath.Length(_lengths[tl]), RefMath.Length(_lengths[te]), RefMath.Decimal(chain.PoleBias));
                if (isOff) plan = plan with { Hold = Hold.OffHand, MainTe = mainTe, MainSe = mainSe, Base = baseHold, BaseIsFk = !wanted };
                if (RunChain(plan, gap, profile.IkPoleFade)) _plans.Add(plan);
            }
        }

        /// <summary>Solves one chain over its key times and writes its three tracks; false when nothing was written.</summary>
        private bool RunChain(ChainPlan plan, int gap, double poleFade)
        {
            var (chain, tu, tl, te) = (plan.Chain, plan.Tu, plan.Tl, plan.Te);
            int su = plan.Su, sl = plan.Sl, se = plan.Se;
            {
                var set = ChainTimes(b => _out[b].Rot.Select(k => k.Time), _tgt, te);
                set.UnionWith(ChainTimes(b => _clip.Bones[b].RotationKeys.Select(k => k.Time), _src, se));
                if (plan.Hold == Hold.OffHand)
                {
                    set.UnionWith(ChainTimes(b => _out[b].Rot.Select(k => k.Time), _tgt, plan.MainTe));
                    set.UnionWith(ChainTimes(b => _clip.Bones[b].RotationKeys.Select(k => k.Time), _src, plan.MainSe));
                }
                set.Add(_clip.StartTime);
                set.Add(_clip.EndTime);
                if (_opt.ResampleStep is not null) set.UnionWith(Grid());
                // Held to the ground or another hand, the end moves with the whole body: a key every
                // frame (160 ticks) keeps a running foot within ~1 cm between keys. The reference's own
                // chains keep its gap (the goldens depend on it).
                var times = Densify([.. set], plan.Hold == Hold.Source ? gap : Math.Min(gap, 160));

                double a = plan.A;
                double b = plan.B;
                var bias = plan.Bias;
                var up = new List<(int, DQuat, sbyte, sbyte)>();
                var lo = new List<(int, DQuat, sbyte, sbyte)>();
                var en = new List<(int, DQuat, sbyte, sbyte)>();
                double worstReach = 0.0, maxWeight = 0.0;
                foreach (int t in times)
                {
                    var wt = WorldOut(t);
                    var ws = WorldSource(t);
                    var S = wt[tu].Pos;
                    DVec3 E, T;
                    if (plan.Hold == Hold.Source)
                    {
                        // The reference, operation for operation (the goldens depend on it).
                        (E, T) = TwoBoneIk.Solve(S, ws[sl].Pos, ws[se].Pos, a, b, bias, poleFade);
                        worstReach = Math.Max(worstReach, RefMath.Length(RefMath.Sub(T, ws[se].Pos)));
                    }
                    else
                    {
                        var (wanted, weight) = Wanted(plan, ws, wt);
                        if (plan.Hold == Hold.Ground) weight = ContactWeight(plan, ws);
                        maxWeight = Math.Max(maxWeight, weight);
                        // The bend plane: the source's knee/elbow relative to its own hip/shoulder.
                        var elbow = RefMath.Add(S, RefMath.Sub(ws[sl].Pos, ws[su].Pos));
                        (E, T) = TwoBoneIk.Solve(S, elbow, wanted, a, b, bias, poleFade);
                        worstReach = Math.Max(worstReach, RefMath.Length(RefMath.Sub(T, wanted)) * weight);
                    }
                    var parRot = wt[_tgt.Parents[tu]].Rot;
                    var upRot = wt[tu].Rot;
                    var curDir = RefMath.Normalize(RefMath.Rotate(upRot, _lengths[tl]));
                    var upNew = RefMath.Mul(RefMath.FromTo(curDir, RefMath.Normalize(RefMath.Sub(E, S))), upRot);
                    var loRot = RefMath.Mul(upNew, RefMath.Conj(upRot));
                    loRot = RefMath.Mul(loRot, wt[tl].Rot);
                    curDir = RefMath.Normalize(RefMath.Rotate(loRot, _lengths[te]));
                    var loNew = RefMath.Mul(RefMath.FromTo(curDir, RefMath.Normalize(RefMath.Sub(T, E))), loRot);
                    var endWorld = wt[te].Rot;
                    var upLocal = RefMath.Mul(RefMath.Conj(parRot), upNew);
                    var loLocal = RefMath.Mul(RefMath.Conj(upNew), loNew);
                    if (plan.Hold == Hold.Ground && ContactWeight(plan, ws) is var w && w < 1.0)
                    {
                        // A foot off the ground lets go: the leg blends back to the source's joint
                        // angles (the frame-offset pose), so legs kicking out in a swim or a jump
                        // keep their shape instead of over-reaching for an airborne target.
                        upLocal = RefMath.Slerp(RefMath.Mul(RefMath.Conj(parRot), upRot), upLocal, w);
                        loLocal = RefMath.Slerp(RefMath.Mul(RefMath.Conj(upRot), wt[tl].Rot), loLocal, w);
                        loNew = RefMath.Mul(RefMath.Mul(parRot, upLocal), loLocal);
                    }
                    up.Add((t, upLocal, 0, 0));
                    lo.Add((t, loLocal, 0, 0));
                    en.Add((t, RefMath.Mul(RefMath.Conj(loNew), endWorld), 0, 0));
                }
                if (plan.Hold == Hold.OffHand && plan.BaseIsFk && maxWeight <= 0.0)
                {
                    _report.Add($"IK {chain.Upper} not needed: the source's hands are never close enough for a two-handed grip");
                    return false;
                }
                _out[tu].Rot = MakeKeys(up);
                _out[tl].Rot = MakeKeys(lo);
                _out[te].Rot = MakeKeys(en);
                _report.Add(plan.Hold == Hold.Source
                    ? string.Format(CultureInfo.InvariantCulture, "IK {0,-10} {1} keys, unreachable by up to {2:0.0} cm", chain.Upper, times.Count, worstReach * 100)
                    : string.Format(CultureInfo.InvariantCulture, "IK {0,-10} {1} keys, held to {2}, unreachable by up to {3:0.0} cm", chain.Upper, times.Count, HeldTo(plan), worstReach * 100));
                return true;
            }
        }

        // ── pinned contacts ─────────────────────────────────────────────────

        /// <summary>Measures every chain that ran on the finished clip (engine sampler, every 160 ticks).</summary>
        private ImmutableArray<PinnedContact> MeasureContacts(RfaClip output)
        {
            if (_plans.Count == 0) return [];
            var srcPose = new Pose(_request.Source.Skeleton);
            var outPose = new Pose(_request.Target.Skeleton);
            var times = RetargetReport.SampleTimes(_clip, 160);
            var stats = _plans.Select(_ => (n: 0, stretched: 0, max: 0.0, sum: 0.0, over: 0.0)).ToArray();
            foreach (int t in times)
            {
                srcPose.Sample(_clip, t);
                outPose.Sample(output, t);
                var ws = ToD(srcPose.World);
                var wt = ToD(outPose.World);
                for (int c = 0; c < _plans.Count; c++)
                {
                    var p = _plans[c];
                    var (target, weight) = Wanted(p, ws, wt);
                    // A hand that only follows the grip while the hands are together is pinned only
                    // then; a foot only while it is on the ground.
                    if (p.Hold == Hold.OffHand && p.BaseIsFk && weight < 0.999) continue;
                    if (p.Hold == Hold.Ground && ContactWeight(p, ws) < 0.999) continue;
                    double err = RefMath.Length(RefMath.Sub(wt[p.Te].Pos, target));
                    double reach = RefMath.Length(RefMath.Sub(target, wt[p.Tu].Pos));
                    bool stretched = reach > p.A + p.B + StretchTolerance || reach < Math.Abs(p.A - p.B) - StretchTolerance;
                    var s = stats[c];
                    stats[c] = (s.n + 1, s.stretched + (stretched ? 1 : 0), Math.Max(s.max, err), s.sum + err, Math.Max(s.over, reach - (p.A + p.B)));
                }
            }
            var result = new List<PinnedContact>();
            for (int c = 0; c < _plans.Count; c++)
            {
                var p = _plans[c];
                var s = stats[c];
                result.Add(new PinnedContact(p.Chain.Upper, p.Chain.End, p.Te, p.IsLeg, HeldTo(p), s.n, s.stretched,
                    s.max * 100, s.n > 0 ? s.sum / s.n * 100 : 0.0, Math.Max(0.0, s.over) * 100));
            }
            return [.. result];
        }

        /// <summary>Metres: a wanted point this far beyond the limb's full reach (or inside its folded minimum) counts as out of reach.</summary>
        private const double StretchTolerance = 0.001;

        private static DRigid[] ToD(Cairn.Formats.Maths.Rigid[] world)
        {
            var d = new DRigid[world.Length];
            for (int i = 0; i < d.Length; i++) d[i] = new DRigid(DQuat.From(world[i].Rotation), DVec3.From(world[i].Position));
            return d;
        }

        // ── output ──────────────────────────────────────────────────────────

        private RfaClip BuildClip() => new()
        {
            Version = _clip.Version,
            PosReduction = _clip.PosReduction,
            RotReduction = _clip.RotReduction,
            StartTime = _clip.StartTime,
            EndTime = _clip.EndTime,
            RampIn = _clip.RampIn,
            RampOut = _clip.RampOut,
            TotalRotation = _clip.TotalRotation,
            TotalTranslation = _clip.TotalTranslation,
            Bones = [.. _out.Select(t => new RfaBoneTrack(t.Weight, [.. t.Rot], [.. t.Pos.Select(k => k.ToKey())]))],
            Morph = RfaMorph.Empty,
        };
    }
}
