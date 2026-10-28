using System.Buffers.Binary;
using System.Numerics;
using Cairn.Formats.Maths;
using Cairn.Formats.Rf2;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Red Faction II clips (<c>.rfa</c> version 9): synthetic files (no game bytes), damaged input, the conversion to
/// version 8, and — when the RF2 install is configured in research/local-paths.json — every clip of the game read to
/// its last byte, the clips converted, the character a clip plays on, and the bone orientation of converted Red Faction
/// II characters checked by posing them with their clips.
/// </summary>
public class Rf2AnimationTests(ITestOutputHelper output)
{
    static Rf2AnimationTests() => LegacyMeshSupport.EnsureRegistered();

    // ── Synthetic files ─────────────────────────────────────────────────────

    [Fact]
    public void Version9_ReadsShortKeysIntoTheClipModel()
    {
        byte[] bytes = SyntheticRf2Clip.SampleSwing();
        Assert.True(Rf2AnimationConverter.IsRedFactionII(bytes));
        var probe = RfaProbe.Probe(bytes, "swing.rfa");
        Assert.True(probe.IsRedFactionII);
        Assert.Equal((2, 0, 4800, 480, 480), (probe.BoneCount, probe.StartTime, probe.EndTime, probe.RampIn, probe.RampOut));

        var clip = RfaReader.Read(bytes, "swing.rfa");
        Assert.Equal(RfaClip.Rf2Version, clip.Version);
        Assert.True(clip.IsRedFactionII);
        var arm = clip.Bones[1];
        Assert.Equal(10f, arm.Weight);
        Assert.Equal([0, 2400, 4800], arm.RotationKeys.Select(k => k.Time));
        Assert.All(arm.RotationKeys, k => Assert.Equal((0, 0, 0), (k.EaseIn, k.EaseOut, k.Pad)));
        Assert.Equal(-11584, arm.RotationKeys[1].Y);
        // position keys: the stored points, control points a third of the way to the neighbours (straight segments)
        var p = arm.PositionKeys;
        Assert.Equal(new Vector3(0, 1.2f, 0), p[1].Position);
        Assert.Equal(p[0].Position, p[0].InControl);
        Assert.True(Vector3.Distance(p[0].OutControl, new Vector3(0, 1 + 0.2f / 3, 0)) < 1e-6f);
        Assert.True(Vector3.Distance(p[1].InControl, new Vector3(0, 1.2f - 0.2f / 3, 0)) < 1e-6f);
        Assert.Equal(p[2].Position, p[2].OutControl);
        // ... so a sample half way along a segment is half way between its keys
        var mid = ClipSampler.SamplePosition(p.AsSpan(), 1200);
        Assert.True(Vector3.Distance(mid, new Vector3(0, 1.1f, 0)) < 1e-5f, $"{mid}");
        Assert.True(clip.Morph.IsEmpty);
    }

    [Fact]
    public void Version9_ReadsMorphDataAfterItsPadding()
    {
        var clip = RfaReader.Read(SyntheticRf2Clip.SampleSwing(morph: true), "talk.rfa");
        Assert.Equal([0, 1, 2], clip.Morph.VertexIndices.Select(v => (int)v));
        Assert.Equal([0, 4800], clip.Morph.KeyframeTimes.AsEnumerable());
        Assert.Equal(new Vector3(1, 1, 0), clip.Morph.GetPosition(0, 1));
        Assert.Equal(new Vector3(0, 1.5f, 1), clip.Morph.GetPosition(1, 2));
    }

    [Fact]
    public void Version9_IsNeverWritten()
    {
        var clip = RfaReader.Read(SyntheticRf2Clip.SampleSwing(), "swing.rfa");
        var ex = Assert.Throws<ArgumentException>(() => RfaWriter.Write(clip));
        Assert.Contains("never written", ex.Message);
        // and a version the reader does not know is still refused, with what it reads
        byte[] bytes = SyntheticRf2Clip.SampleSwing();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 10);
        var refused = Assert.Throws<AssetFormatException>(() => RfaReader.Read(bytes, "ten.rfa"));
        Assert.Contains("9 (Red Faction II)", refused.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Convert_MakesAVersion8ClipWithTheSameKeys(bool morph)
    {
        byte[] bytes = SyntheticRf2Clip.SampleSwing(morph);
        var source = RfaReader.Read(bytes, "swing.rfa");
        var conversion = Rf2AnimationConverter.Convert(bytes, "swing.rfa");
        Assert.Equal(("swing.rfa", "swing.rfa"), (conversion.SourceName, conversion.OutputName));
        Assert.False(conversion.IsLossless);
        var back = RfaReader.Read(conversion.Bytes, "swing.rfa");
        Assert.Equal(8, back.Version);
        Assert.Equal(8, conversion.Clip.Version);
        Assert.Equal((source.StartTime, source.EndTime, source.RampIn, source.RampOut), (back.StartTime, back.EndTime, back.RampIn, back.RampOut));
        for (int b = 0; b < source.BoneCount; b++)
        {
            Assert.Equal(source.Bones[b].RotationKeys.AsEnumerable(), back.Bones[b].RotationKeys.AsEnumerable());
            Assert.Equal(source.Bones[b].PositionKeys.AsEnumerable(), back.Bones[b].PositionKeys.AsEnumerable());
            Assert.Equal(source.Bones[b].Weight, back.Bones[b].Weight);
        }
        Assert.Equal(source.Morph.VertexIndices.AsEnumerable(), back.Morph.VertexIndices.AsEnumerable());
        Assert.Equal(source.Morph.QuantizedPositions.AsEnumerable(), back.Morph.QuantizedPositions.AsEnumerable());
        // the version 8 clip plays exactly as the version 9 one reads
        for (int t = 0; t <= 4800; t += 160)
        {
            for (int b = 0; b < source.BoneCount; b++)
                Assert.Equal(ClipSampler.SampleBone(source.Bones[b], t), ClipSampler.SampleBone(back.Bones[b], t));
        }
        Assert.Contains(conversion.Report, n => n.Contains("without eases", StringComparison.Ordinal));
        Assert.Contains(conversion.Report, n => n.Contains("exactly its 2 bones", StringComparison.Ordinal));
        Assert.Equal(morph, conversion.Report.Any(n => n.Contains("vertices of the Red Faction II mesh", StringComparison.Ordinal)));
        Assert.DoesNotContain(ClipLinter.AnalyzeStructure(back), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Convert_ShortensKeysLongerThanOne()
    {
        var bone = new SyntheticRf2Clip.Bone(10f, [(0, 0, 0, 0, 16384), (160, 100, 0, 0, 16383)], [(0, Vector3.Zero), (160, Vector3.Zero)]);
        var conversion = Rf2AnimationConverter.Convert(SyntheticRf2Clip.Build(0, 160, 0, 0, [bone]), "long.rfa");
        Assert.All(conversion.Clip.Bones[0].RotationKeys, k => Assert.True((long)k.X * k.X + (long)k.Y * k.Y + (long)k.Z * k.Z + (long)k.W * k.W <= 16383L * 16383L));
        Assert.Contains(conversion.Report, n => n.Contains("2 of the 2 rotation keys", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_RefusesWhatIsNotARedFactionIIClip()
    {
        var v8 = RfaWriter.Write(RfaReader.Read(SyntheticRf2Clip.SampleSwing(), "swing.rfa") with { Version = 8 });
        Assert.Contains("needs no conversion", Assert.Throws<AssetFormatException>(() => Rf2AnimationConverter.Convert(v8, "rf1.rfa")).Message);
        Assert.Throws<AssetFormatException>(() => Rf2AnimationConverter.Convert([1, 2, 3], "junk.rfa"));
    }

    [Fact]
    public void TruncatedAndDamagedClips_OnlyRaiseAssetFormatException()
    {
        foreach (byte[] bytes in new[] { SyntheticRf2Clip.SampleSwing(), SyntheticRf2Clip.SampleSwing(morph: true) })
        {
            for (int cut = 0; cut < bytes.Length; cut++)
            {
                try { RfaReader.Read(bytes[..cut], "cut.rfa"); }
                catch (AssetFormatException) { }
            }
            var rng = new Random(9);
            for (int i = 0; i < 2000; i++)
            {
                var copy = (byte[])bytes.Clone();
                for (int k = 0; k < 3; k++) copy[rng.Next(8, copy.Length)] = (byte)rng.Next(256);
                try { Rf2AnimationConverter.Convert(copy, "fuzz.rfa"); }
                catch (AssetFormatException) { }
            }
        }
        // a key count far beyond the file is refused before anything is allocated
        byte[] huge = SyntheticRf2Clip.SampleSwing();
        int bone = BinaryPrimitives.ReadInt32LittleEndian(huge.AsSpan(RfaClip.BoneTableOffset + 4));
        BinaryPrimitives.WriteInt16LittleEndian(huge.AsSpan(bone + 4), short.MaxValue);
        Assert.Throws<AssetFormatException>(() => RfaReader.Read(huge, "huge.rfa"));
    }

    [Fact]
    public void RankMeshes_PrefersTheNameThenTheSkeletonTheClipWasMadeOn()
    {
        var clip = RfaReader.Read(SyntheticRf2Clip.SampleSwing(), "guard_swing.rfa");
        var guard = LegacyMeshSupport.Rf2Skeleton(SyntheticRf2Mesh.SampleCharacter());
        Assert.NotNull(guard);
        Assert.Equal(["root", "arm"], guard!.Value.Select(b => b.Name.Text));
        // half way through, the clip has the arm's joint 1.2 m above the root; the mesh has it at 1 m
        Assert.InRange(Rf2AnimationConverter.JointOffset(clip, guard.Value) ?? -1, 0.199, 0.201);
        var longArm = guard.Value.SetItem(1, guard.Value[1] with { Position = new Vector3(0, -1.5f, 0) });
        Assert.InRange(Rf2AnimationConverter.JointOffset(clip, longArm) ?? -1, 0.299, 0.301);
        Assert.Null(Rf2AnimationConverter.JointOffset(clip, guard.Value.RemoveAt(1)));
        IReadOnlyList<V3dBone> g = guard.Value, l = longArm, one = guard.Value.RemoveAt(1);
        var ranked = Rf2AnimationConverter.RankMeshes(clip, "guard_swing.rfa", [("other.rfc", g), ("guard_long.rfc", l), ("single.rfc", one)]);
        Assert.Equal(["guard_long.rfc", "other.rfc"], ranked.Select(r => r.Name));
        ranked = Rf2AnimationConverter.RankMeshes(clip, "zz_swing.rfa", [("other.rfc", g), ("guard_long.rfc", l)]);
        Assert.Equal(["other.rfc", "guard_long.rfc"], ranked.Select(r => r.Name));
        Assert.Null(LegacyMeshSupport.Rf2Skeleton(SyntheticRf2Mesh.SampleStatic()));
    }

    // ── Real files ──────────────────────────────────────────────────────────

    /// <summary>The groups that hold Red Faction II clips (927 entries, 798 distinct).</summary>
    private static readonly string[] ClipGroups = ["All_Levels", "Animations_1", "Animations_2", "Animations_3", "Permanent_data"];

    [Fact]
    public void RealClips_EveryOneReadsToItsLastByteAndConverts()
    {
        if (LocalPaths.Rf2GameDirectory is not { } game)
        {
            output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.Rf2GameDirectoryVariable, "rf2GameDirectory"));
            return;
        }
        var files = Rf2Files(game, ClipGroups);
        var distinct = new HashSet<string>();
        int read = 0, morph = 0, notes = 0;
        foreach (var (name, open) in files.Where(f => f.Key.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes = open();
            if (!distinct.Add(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)))) continue;
            var clip = RfaReader.Read(bytes, name);
            Assert.True(clip.IsRedFactionII, name);
            // the bytes the reader walked: the header and table, every bone's short keys, the morph data after its padding
            long size = RfaClip.BoneTableOffset + 4L * clip.BoneCount
                + clip.Bones.Sum(b => (long)RfaBoneTrack.HeaderSize + b.RotationKeys.Length * RfaReader.Rf2RotKeySize + b.PositionKeys.Length * RfaReader.Rf2PosKeySize);
            size += 2L * clip.Morph.VertexCount;
            size = (size + 3) & ~3L;
            size += 4L * clip.Morph.KeyframeTimes.Length + (clip.Morph.Bounds is null ? 0 : 24) + clip.Morph.QuantizedPositions.Length;
            Assert.True(size == bytes.Length, $"{name}: {size} bytes walked of {bytes.Length}");
            var conversion = Rf2AnimationConverter.Convert(clip, name);
            var back = RfaReader.Read(conversion.Bytes, name);
            Assert.Equal(8, back.Version);
            Assert.Equal(clip.Bones.Sum(b => b.RotationKeys.Length + b.PositionKeys.Length), back.Bones.Sum(b => b.RotationKeys.Length + b.PositionKeys.Length));
            Assert.DoesNotContain(ClipLinter.AnalyzeStructure(back), d => d.Severity == DiagnosticSeverity.Error);
            if (!clip.Morph.IsEmpty) morph++;
            notes += conversion.Report.Length;
            read++;
        }
        output.WriteLine($"{read} distinct Red Faction II clips read to their end and converted ({files.Count(f => f.Key.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))} entries), {morph} with morph data, {notes} report lines");
        Assert.True(read >= 798, $"{read} of the 798 distinct clips");
    }

    [Fact]
    public void RealClips_PickTheCharacterTheyWereMadeFor()
    {
        if (LocalPaths.Rf2GameDirectory is not { } game) return;
        var files = Rf2Files(game, "All_Levels", "Animations_1", "Animations_3", "Menu_extras");
        var skeletons = files.Where(f => f.Key.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Name: f.Key, Bones: LegacyMeshSupport.Rf2Skeleton(f.Value())))
            .Where(s => s.Bones is not null).Select(s => (s.Name, (IReadOnlyList<V3dBone>)s.Bones!.Value)).ToList();
        foreach (var (clipName, mesh) in new[]
        {
            ("mech_bash.rfa", "mech_suit.rfc"), ("tang_hudtalk60.rfa", "tangier_hudhead.rfc"), ("rpta_hudtalk60.rfa", "repta_hudhead.rfc"),
            ("medm_l05s6_spt_yell.rfa", "sopot*"), ("fp_sr_draw.rfa", "fp_sniper_rifle.rfc"), ("fp_hmg_idle.rfa", "fp_hmg.rfc"),
            ("gunship2_hover.rfa", "gunship_2.rfc"), ("swarmer_walk.rfa", "swarmer.rfc"), ("quill_talk30.rfa", "quill*"),
        })
        {
            if (!files.TryGetValue(clipName, out var read)) continue;
            var ranked = Rf2AnimationConverter.RankMeshes(RfaReader.Read(read(), clipName), clipName, skeletons);
            output.WriteLine($"{clipName}: {string.Join(", ", ranked.Take(3).Select(r => $"{r.Name} {r.JointOffset:0.000} m"))}");
            string best = ranked.FirstOrDefault().Name ?? "";
            Assert.True(mesh.EndsWith('*') ? best.StartsWith(mesh[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(best, mesh, StringComparison.OrdinalIgnoreCase),
                $"{clipName} picks {best}, not {mesh}");
        }
    }

    // ── Bone orientation of converted Red Faction II characters ────────────

    /// <summary>How a posed character holds together.</summary>
    /// <param name="JointGap">Mean distance (m) between where a bone's parent carries the bone's joint and where the clip puts it.</param>
    /// <param name="JointGapMax">The largest such distance (m).</param>
    /// <param name="Stretched">Share of triangle edges whose length changed by more than half.</param>
    /// <param name="BoxGrowth">Largest posed bounding-box diagonal over the bind pose's.</param>
    internal sealed record PoseStats(double JointGap, double JointGapMax, double Stretched, double BoxGrowth);

    /// <summary>
    /// Poses a converted character with a clip at many frames (linear-blend skinning as the game does) and measures how
    /// it holds together; <paramref name="focus"/> limits the edges to triangles touching those bones.
    /// </summary>
    internal static PoseStats Pose(V3dMeshDescription mesh, IReadOnlyList<V3dBone> bones, RfaClip clip, IReadOnlySet<int>? focus = null, int frames = 16)
    {
        var skeleton = Skeleton.FromBones(bones);
        int n = skeleton.Count;
        var locals = new Rigid[n];
        var worlds = new Rigid[n];
        var skin = new Rigid[n];
        var verts = new List<(Vector3 P, V3dBoneInfluence[] W)>();
        var edges = new List<(int A, int B)>();
        foreach (var sub in mesh.Submeshes)
        {
            foreach (var g in sub.Lods[0].Groups)
            {
                int start = verts.Count;
                foreach (var v in g.Vertices) verts.Add((v.Position + sub.Offset, [.. v.Influences]));
                foreach (var t in g.Triangles)
                {
                    if (focus is not null && !new[] { t.A, t.B, t.C }.Any(i => g.Vertices[i].Influences.Any(w => focus.Contains(w.Bone)))) continue;
                    edges.Add((start + t.A, start + t.B));
                    edges.Add((start + t.B, start + t.C));
                    edges.Add((start + t.C, start + t.A));
                }
            }
        }
        double bindBox = Diagonal(verts.Select(v => v.P));
        double gapSum = 0, gapMax = 0, growth = 1;
        long gaps = 0, counted = 0, stretched = 0;
        var posed = new Vector3[verts.Count];
        for (int f = 0; f < frames; f++)
        {
            float time = clip.StartTime + clip.Duration * (f + 0.5f) / frames;
            ClipSampler.SampleLocals(clip, time, locals);
            ForwardKinematics.Solve(skeleton, locals, worlds);
            for (int b = 0; b < n; b++) skin[b] = worlds[b].Compose(skeleton.InverseBind[b]);
            for (int b = 0; b < n; b++)
            {
                int p = skeleton.EffectiveParents[b];
                if (p < 0) continue;
                double gap = Vector3.Distance(skin[p].TransformPoint(skeleton.RestWorld[b].Position), worlds[b].Position);
                gapSum += gap;
                gaps++;
                gapMax = Math.Max(gapMax, gap);
            }
            for (int i = 0; i < verts.Count; i++)
            {
                var (position, weights) = verts[i];
                var sum = Vector3.Zero;
                float total = 0;
                foreach (var w in weights)
                {
                    if (w.Bone < 0 || w.Bone >= n || !(w.Weight > 0)) continue;
                    sum += w.Weight * skin[w.Bone].TransformPoint(position);
                    total += w.Weight;
                }
                posed[i] = total > 0 ? sum / total : position;
            }
            foreach (var (a, b) in edges)
            {
                float before = Vector3.Distance(verts[a].P, verts[b].P);
                if (before < 1e-3f) continue;
                double ratio = Vector3.Distance(posed[a], posed[b]) / before;
                counted++;
                if (ratio > 1.5 || ratio < 1 / 1.5) stretched++;
            }
            growth = Math.Max(growth, Diagonal(posed) / Math.Max(bindBox, 1e-6));
        }
        return new PoseStats(gapSum / Math.Max(1, gaps), gapMax, (double)stretched / Math.Max(1, counted), growth);
    }

    private static double Diagonal(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return min.X > max.X ? 0 : Vector3.Distance(min, max);
    }

    /// <summary>
    /// The bones read with the other storage convention for the turned bones' nine floats (the matrix maps bone space to
    /// model space): both the rest rotation and the joint (<c>-M t</c> instead of <c>-Mᵀ t</c>) change.
    /// </summary>
    internal static IReadOnlyList<V3dBone> OtherConvention(IReadOnlyList<V3dBone> bones) =>
        [.. bones.Select(b => b with { Rotation = Quaternion.Conjugate(b.Rotation) })];

    /// <summary>The clip with every rotation inverted (its quaternions read in the other convention).</summary>
    internal static RfaClip InvertedRotations(RfaClip clip) => clip with
    {
        Bones = [.. clip.Bones.Select(b => b with { RotationKeys = [.. b.RotationKeys.Select(k => k with { X = (short)-k.X, Y = (short)-k.Y, Z = (short)-k.Z })] })],
    };

    /// <summary>
    /// The verdict on the bone orientation of converted Red Faction II characters: posed with their clips, the stored
    /// convention (<c>x_bone = M x + t</c>, the matrix row-major, model to bone) keeps the joints where the clips put
    /// them and the skin around the turned arm bones together; the other convention tears it. The clips' rotations are
    /// in the Red Faction file convention: read the other way, planted feet slide. A bounded set: two characters with
    /// turned bones (security_guard_b: both arms; tangier: spine and upper arms) and one without (burke).
    /// </summary>
    [Fact]
    public void BoneOrientation_ConvertedCharactersHoldTogetherWhenPosed()
    {
        if (LocalPaths.Rf2GameDirectory is not { } game) return;
        var files = Rf2Files(game, "All_Levels");
        foreach (var (meshName, clips, maxGap) in new (string, string[], double)[]
        {
            ("security_guard_b.rfc", ["medm_talk.rfa", "medm_sitdown.rfa", "medm_hrfl_kickdoor.rfa"], 0.01),
            ("tangier.rfc", ["feml_comphack.rfa", "feml_idle2.rfa", "medm_sitdown.rfa"], 0.025),
            ("burke.rfc", ["medm_talk.rfa", "medm_pounddoor.rfa"], 0.02),
        })
        {
            if (!files.TryGetValue(meshName, out var readMesh)) continue;
            var d = LegacyMeshSupport.Read(readMesh(), meshName).Description;
            var turned = Enumerable.Range(0, d.Bones.Length).Where(i => MathF.Abs(d.Bones[i].Rotation.W) < 0.9999f).ToHashSet();
            foreach (string clipName in clips)
            {
                if (!files.TryGetValue(clipName, out var readClip)) continue;
                var clip = RfaReader.Read(readClip(), clipName);
                var stored = Pose(d, d.Bones, clip);
                output.WriteLine($"{meshName} + {clipName}: joints {stored.JointGap * 1000:0.0} mm (max {stored.JointGapMax * 1000:0} mm), {stored.Stretched:P2} of edges stretched, box x{stored.BoxGrowth:0.00}");
                Assert.True(stored.JointGap < maxGap, $"{meshName} + {clipName}: joint gap {stored.JointGap:0.0000} m");
                Assert.True(stored.Stretched < 0.08, $"{meshName} + {clipName}: {stored.Stretched:P2} of the edges stretched");
                Assert.True(stored.BoxGrowth < 1.3, $"{meshName} + {clipName}: the box grows x{stored.BoxGrowth:0.00}");
                if (turned.Count == 0) continue;
                var near = Pose(d, d.Bones, clip, turned);
                var other = Pose(d, OtherConvention(d.Bones), clip, turned);
                output.WriteLine($"    around the {turned.Count} turned bones: stored convention {near.JointGap * 1000:0.0} mm, {near.Stretched:P2} stretched; other convention {other.JointGap * 1000:0.0} mm, {other.Stretched:P2} stretched");
                Assert.True(other.JointGap > near.JointGap * 1.2, $"{meshName} + {clipName}: the other convention's joints fit as well ({other.JointGap:0.0000} vs {near.JointGap:0.0000} m)");
                Assert.True(other.Stretched > near.Stretched * 1.3, $"{meshName} + {clipName}: the other convention's skin holds as well ({other.Stretched:P2} vs {near.Stretched:P2})");
            }
        }

        // the rotation convention of the clips: sitting down, the feet stay where they are
        if (files.TryGetValue("burke.rfc", out var burke) && files.TryGetValue("medm_sitdown.rfa", out var sit))
        {
            var bones = LegacyMeshSupport.Read(burke(), "burke.rfc").Description.Bones;
            var clip = RfaReader.Read(sit(), "medm_sitdown.rfa");
            double drift = FootDrift(bones, clip), inverted = FootDrift(bones, InvertedRotations(clip));
            output.WriteLine($"medm_sitdown.rfa: the feet move {drift:0.000} m with the Red Faction rotation convention, {inverted:0.000} m with the other");
            Assert.True(drift < 0.08 && inverted > 3 * drift, $"foot drift {drift:0.000} vs {inverted:0.000} m");
        }
    }

    /// <summary>How far the feet move over the clip (the root-mean-square distance from where they are on average, both feet).</summary>
    private static double FootDrift(IReadOnlyList<V3dBone> bones, RfaClip clip)
    {
        var skeleton = Skeleton.FromBones(bones);
        int[] feet = [.. Enumerable.Range(0, skeleton.Count).Where(i => skeleton.Names[i].EndsWith("-bdbn-foot-l", StringComparison.OrdinalIgnoreCase) || skeleton.Names[i].EndsWith("-bdbn-foot-r", StringComparison.OrdinalIgnoreCase))];
        Assert.Equal(2, feet.Length);
        var locals = new Rigid[skeleton.Count];
        var worlds = new Rigid[skeleton.Count];
        var tracks = feet.Select(_ => new List<Vector3>()).ToArray();
        for (int f = 0; f < 48; f++)
        {
            ClipSampler.SampleLocals(clip, clip.StartTime + clip.Duration * (f + 0.5f) / 48, locals);
            ForwardKinematics.Solve(skeleton, locals, worlds);
            for (int i = 0; i < feet.Length; i++) tracks[i].Add(worlds[feet[i]].Position);
        }
        return tracks.Average(t =>
        {
            var mean = t.Aggregate(Vector3.Zero, (a, b) => a + b) / t.Count;
            return Math.Sqrt(t.Average(p => (double)Vector3.DistanceSquared(p, mean)));
        });
    }

    /// <summary>
    /// The files of Red Faction II groups by name (the first of a name wins): a packed group's entries from its
    /// <c>.packfile</c>, a loose group's files by their stored paths (no folder is listed).
    /// </summary>
    internal static Dictionary<string, Func<byte[]>> Rf2Files(string game, params string[] groups)
    {
        var map = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        string media = Path.Combine(game, "pc_media");
        foreach (string group in groups)
        {
            string toc = Path.Combine(media, group + ".toc_group");
            if (!File.Exists(toc)) continue;
            var g = Rf2TocGroup.Open(toc);
            if (g.IsPacked)
            {
                foreach (var (name, read) in Rf2MeshTests.PackedEntries(media, group)) map.TryAdd(name, read);
                continue;
            }
            foreach (var e in g.Entries)
            {
                if (Rf2TocGroup.LooseFilePath(toc, e) is { } path) map.TryAdd(e.Name, () => File.ReadAllBytes(path));
            }
        }
        return map;
    }
}
