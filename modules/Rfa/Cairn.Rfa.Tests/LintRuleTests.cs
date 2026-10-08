using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

/// <summary>Every lint rule has a test that makes it fire and one that keeps it quiet.</summary>
public class LintRuleTests
{
    // ── Synthetic clip and skeleton ──────────────────────────────────────────

    private static RfaRotKey Rot(int t, float angle = 0f) => RfaRotKey.Quantize(t, Quat.Conj(Quat.FromAxisAngle(Vector3.UnitY, angle)));

    private static RfaBoneTrack Track(float weight = 10f, Vector3? offset = null)
    {
        var p = offset ?? new Vector3(0, 0.2f, 0);
        return new RfaBoneTrack(weight, [Rot(160), Rot(480, 0.3f), Rot(800, 0.6f)], [RfaPosKey.Constant(160, p), RfaPosKey.Constant(800, p)]);
    }

    /// <summary>A healthy three-bone clip: root, pelvis, spine.</summary>
    private static RfaClip Good() => new()
    {
        Version = 8,
        StartTime = 160,
        EndTime = 800,
        RampIn = 160,
        RampOut = 160,
        Bones = [Track(offset: new Vector3(0, 1, 0)), Track(), Track()],
    };

    private static Skeleton Skeleton3() => Animation.Skeleton.FromBones(
    [
        Bone("root", -1, new Vector3(0, -1, 0)),
        Bone("pelvis", 0, new Vector3(0, -1.2f, 0)),
        Bone("spine", 1, new Vector3(0, -1.4f, 0)),
    ]);

    private static V3dBone Bone(string name, int parent, Vector3 pos) =>
        new(FixedString.FromText(name, V3dBone.NameSize), Quaternion.Identity, pos, parent);

    private static RfaClip WithBone(RfaClip clip, int bone, RfaBoneTrack track) => clip with { Bones = clip.Bones.SetItem(bone, track) };

    private static IReadOnlyList<Diagnostic> Lint(RfaClip clip, ClipLintContext? context = null) => ClipLinter.Analyze(clip, context);

    private static void Fires(string code, IReadOnlyList<Diagnostic> results) =>
        Assert.True(results.Any(d => d.Code == code), $"{code} expected; got {string.Join(", ", results.Select(r => r.Code))}");

    private static void Quiet(string code, IReadOnlyList<Diagnostic> results) =>
        Assert.False(results.Any(d => d.Code == code), $"{code} not expected: {results.FirstOrDefault(d => d.Code == code)}");

    [Fact]
    public void TheHealthyClipHasNoDiagnosticsAtAll()
    {
        var results = Lint(Good(), new ClipLintContext { Skeleton = Skeleton3(), FileName = "good.rfa", ReferenceClip = Good() });
        Assert.Empty(results);
    }

    [Fact]
    public void EveryRuleHasAUniqueCodeAndAKnownPolicy()
    {
        Assert.Equal(ClipRules.All.Length, ClipRules.All.Select(r => r.Code).Distinct().Count());
        Assert.Equal(MeshRules.All.Length, MeshRules.All.Select(r => r.Code).Distinct().Count());
        Assert.All(ClipRules.All, r => Assert.StartsWith("RFA", r.Code));
        Assert.All(MeshRules.All, r => Assert.StartsWith("V3C", r.Code));
    }

    // ── Clip rules ───────────────────────────────────────────────────────────

    [Fact]
    public void Rfa001Version()
    {
        Fires(ClipRules.UnsupportedVersion, Lint(Good() with { Version = 9 }));
        Quiet(ClipRules.UnsupportedVersion, Lint(Good() with { Version = 7 }));
    }

    [Fact]
    public void Rfa002BoneCountAgainstThePreviewMesh()
    {
        var two = Good() with { Bones = Good().Bones.RemoveAt(2) };
        var d = Lint(two, new ClipLintContext { Skeleton = Skeleton3(), PreviewMeshName = "x.v3c" }).Single(x => x.Code == ClipRules.BoneCountMismatch);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains(d.QuickFixes, f => f.Kind == QuickFixKind.PickPreviewMesh);
        // The App opens Conform to Skeleton on the preview mesh for it (no payload: the preview mesh is meant).
        Assert.Contains(d.QuickFixes, f => f.Kind == QuickFixKind.ConformToSkeleton && f.Payload is null);
        Assert.Contains("Conform to Skeleton (reorders, adds and drops tracks by bone name)", d.Help, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", d.Help, StringComparison.Ordinal);
        Quiet(ClipRules.BoneCountMismatch, Lint(Good(), new ClipLintContext { Skeleton = Skeleton3() }));
        Quiet(ClipRules.BoneCountMismatch, Lint(two)); // no preview mesh, no rule
    }

    [Fact]
    public void Rfa003And004MissingKeys()
    {
        var noPos = WithBone(Good(), 1, Good().Bones[1] with { PositionKeys = [] });
        Fires(ClipRules.NoPositionKeys, Lint(noPos));
        var noRot = WithBone(Good(), 1, Good().Bones[1] with { RotationKeys = [] });
        var r = Lint(noRot);
        Fires(ClipRules.NoRotationKeys, r);
        Assert.Equal(DiagnosticSeverity.Warning, r.First(d => d.Code == ClipRules.NoRotationKeys).Severity);
        var single = WithBone(Good(), 1, Good().Bones[1] with { PositionKeys = [RfaPosKey.Constant(160, Vector3.UnitY)] });
        Quiet(ClipRules.NoPositionKeys, Lint(single)); // one position key is a constant track, as in stock
    }

    [Fact]
    public void Rfa005KeyOrder()
    {
        var keys = Good().Bones[1].RotationKeys;
        var swapped = WithBone(Good(), 1, Good().Bones[1] with { RotationKeys = [keys[1], keys[0], keys[2]] });
        var d = Lint(swapped).First(x => x.Code == ClipRules.KeyTimesNotIncreasing);
        Assert.Equal(1, d.Location.Bone);
        Assert.Equal(Editing.KeyKind.Rotation, d.Location.Key!.Value.Kind);
        var duplicate = WithBone(Good(), 2, Good().Bones[2] with { PositionKeys = [RfaPosKey.Constant(160, Vector3.One), RfaPosKey.Constant(160, Vector3.One)] });
        Fires(ClipRules.KeyTimesNotIncreasing, Lint(duplicate));
        Quiet(ClipRules.KeyTimesNotIncreasing, Lint(Good()));
    }

    [Fact]
    public void Rfa006KeysOutsideTheRange()
    {
        Fires(ClipRules.KeyOutsideRange, Lint(Good() with { EndTime = 640 }));
        Fires(ClipRules.KeyOutsideRange, Lint(Good() with { StartTime = 320 }));
        Quiet(ClipRules.KeyOutsideRange, Lint(Good() with { StartTime = 0, EndTime = 1000 }));
    }

    [Fact]
    public void Rfa007TooManyBones()
    {
        var many = Good() with { Bones = [.. Enumerable.Repeat(Track(), 51)] };
        Fires(ClipRules.TooManyBones, Lint(many));
        Assert.Contains(Lint(many).Single(x => x.Code == ClipRules.TooManyBones).QuickFixes, f => f.Kind == QuickFixKind.ConformToSkeleton);
        Quiet(ClipRules.TooManyBones, Lint(Good() with { Bones = [.. Enumerable.Repeat(Track(), 50)] }));
    }

    [Fact]
    public void Rfa008And026Range()
    {
        Fires(ClipRules.EndBeforeStart, Lint(Good() with { StartTime = 900, EndTime = 800 }));
        Quiet(ClipRules.EndBeforeStart, Lint(Good()));
        var still = Good() with { EndTime = 160, Bones = [.. Good().Bones.Select(b => b with { RotationKeys = [Rot(160)], PositionKeys = [RfaPosKey.Constant(160, Vector3.UnitY)] })] };
        Quiet(ClipRules.ZeroLength, Lint(still)); // a one-frame pose
        Fires(ClipRules.ZeroLength, Lint(Good() with { EndTime = 160 }));
    }

    [Fact]
    public void Rfa009CannotBeWritten()
    {
        var badMorph = Good() with { Morph = new RfaMorph([1, 2], 2, [160], null, [], []) };
        Fires(ClipRules.NotWritable, Lint(badMorph));
        Quiet(ClipRules.NotWritable, Lint(Good()));
    }

    [Fact]
    public void Rfa010MorphBeyondThePreviewMesh()
    {
        var morph = new RfaMorph([0, 7], 1, [], null, [], [Vector3.Zero, Vector3.One]);
        var clip = Good() with { Version = 7, Morph = morph };
        var mesh = MeshWithLodVertices(5);
        Fires(ClipRules.MorphIndexBeyondMesh, Lint(clip, new ClipLintContext { PreviewMesh = mesh }));
        Quiet(ClipRules.MorphIndexBeyondMesh, Lint(clip, new ClipLintContext { PreviewMesh = MeshWithLodVertices(8) }));
    }

    [Fact]
    public void Rfa011NonFinite()
    {
        Fires(ClipRules.NonFinite, Lint(WithBone(Good(), 0, Good().Bones[0] with { Weight = float.NaN })));
        var nanPos = WithBone(Good(), 1, Good().Bones[1] with { PositionKeys = [RfaPosKey.Constant(160, new Vector3(float.NaN, 0, 0)), RfaPosKey.Constant(800, Vector3.One)] });
        Fires(ClipRules.NonFinite, Lint(nanPos));
        Quiet(ClipRules.NonFinite, Lint(Good()));
    }

    [Fact]
    public void Rfa012ZeroQuaternion()
    {
        var keys = Good().Bones[1].RotationKeys;
        Fires(ClipRules.ZeroQuaternion, Lint(WithBone(Good(), 1, Good().Bones[1] with { RotationKeys = keys.SetItem(1, new RfaRotKey(480, 0, 0, 0, 0)) })));
        Quiet(ClipRules.ZeroQuaternion, Lint(Good()));
    }

    [Fact]
    public void Rfa013TablesPlayTheClipOnAnotherSkeleton()
    {
        var use = new ClipTableUse("miner1", "entity.tbl", "stand", true, "miner.v3c", 25);
        Fires(ClipRules.TableMeshBoneCount, Lint(Good(), new ClipLintContext { TableUses = [use] }));
        // The conform quick fix names the table's mesh, which the App picks in the tool.
        Assert.Contains(Lint(Good(), new ClipLintContext { TableUses = [use] }).Single(x => x.Code == ClipRules.TableMeshBoneCount).QuickFixes,
            f => f.Kind == QuickFixKind.ConformToSkeleton && f.Payload == "miner.v3c");
        Quiet(ClipRules.TableMeshBoneCount, Lint(Good(), new ClipLintContext { TableUses = [use with { MeshBoneCount = 3 }] }));
        Quiet(ClipRules.TableMeshBoneCount, Lint(Good(), new ClipLintContext { TableUses = [use with { MeshBoneCount = null }] }));
    }

    [Fact]
    public void Rfa014FileNameLength()
    {
        string longName = new string('a', 56) + ".rfa"; // 60 characters
        Fires(ClipRules.FileNameTooLong, Lint(Good(), new ClipLintContext { FileName = longName }));
        Quiet(ClipRules.FileNameTooLong, Lint(Good(), new ClipLintContext { FileName = new string('a', 55) + ".rfa" }));
    }

    [Fact]
    public void Rfa020ZeroControlPoints()
    {
        var p = new Vector3(0, 0.2f, 0);
        var zero = WithBone(Good(), 1, Good().Bones[1] with { PositionKeys = [new RfaPosKey(160, p, p, Vector3.Zero), new RfaPosKey(800, p, Vector3.Zero, p)] });
        Fires(ClipRules.ZeroControlPoints, Lint(zero));
        // A bone that really sits at the origin, or the unused outer control points, are fine.
        var outer = WithBone(Good(), 1, Good().Bones[1] with { PositionKeys = [new RfaPosKey(160, p, Vector3.Zero, p), new RfaPosKey(800, p, p, Vector3.Zero)] });
        Quiet(ClipRules.ZeroControlPoints, Lint(outer));
        var origin = WithBone(Good(), 1, Good().Bones[1] with { PositionKeys = [new RfaPosKey(160, Vector3.Zero, Vector3.Zero, Vector3.Zero), new RfaPosKey(800, Vector3.Zero, Vector3.Zero, Vector3.Zero)] });
        Quiet(ClipRules.ZeroControlPoints, Lint(origin));
    }

    [Fact]
    public void Rfa021NonUnitQuaternion()
    {
        var keys = Good().Bones[1].RotationKeys;
        var scaled = keys[1] with { W = (short)(keys[1].W * 0.99f) };
        Fires(ClipRules.NonUnitQuaternion, Lint(WithBone(Good(), 1, Good().Bones[1] with { RotationKeys = keys.SetItem(1, scaled) })));
        var slightly = keys[1] with { W = (short)(keys[1].W - 10) }; // 0.06 % short: within tolerance
        Quiet(ClipRules.NonUnitQuaternion, Lint(WithBone(Good(), 1, Good().Bones[1] with { RotationKeys = keys.SetItem(1, slightly) })));
    }

    [Fact]
    public void Rfa022And027Ramps()
    {
        var d = Lint(Good() with { RampIn = 400, RampOut = 400 }).Single(x => x.Code == ClipRules.RampsLongerThanClip);
        var fixedClip = d.QuickFixes.Single().Apply(Good() with { RampIn = 400, RampOut = 400 });
        Assert.True(fixedClip.RampIn + fixedClip.RampOut <= fixedClip.Duration);
        Quiet(ClipRules.RampsLongerThanClip, Lint(fixedClip));
        Fires(ClipRules.NegativeRamp, Lint(Good() with { RampIn = -5 }));
        Quiet(ClipRules.NegativeRamp, Lint(Good()));
    }

    [Fact]
    public void Rfa023ProportionsAgainstTheReferenceClip()
    {
        var stretched = WithBone(Good(), 2, Track(offset: new Vector3(0, 0.3f, 0)));
        var context = new ClipLintContext { Skeleton = Skeleton3(), ReferenceClip = Good(), ReferenceClipName = "stand.rfa" };
        Fires(ClipRules.ProportionsDiffer, Lint(stretched, context));
        // 1 cm is within tolerance, and the root's position is motion, not a length.
        Quiet(ClipRules.ProportionsDiffer, Lint(WithBone(Good(), 2, Track(offset: new Vector3(0, 0.21f, 0))), context));
        Quiet(ClipRules.ProportionsDiffer, Lint(WithBone(Good(), 0, Track(offset: new Vector3(0, 3f, 0))), context));
    }

    [Fact]
    public void Rfa024NameCollision()
    {
        Fires(ClipRules.NameCollision, Lint(Good(), new ClipLintContext { CollidingClips = ["park_jeep_driver.rfa in meshes.vpp"] }));
        Quiet(ClipRules.NameCollision, Lint(Good(), new ClipLintContext()));
    }

    [Fact]
    public void Rfa025WeightAboveTen()
    {
        Fires(ClipRules.WeightAboveTen, Lint(WithBone(Good(), 0, Good().Bones[0] with { Weight = 12f })));
        Quiet(ClipRules.WeightAboveTen, Lint(WithBone(Good(), 0, Good().Bones[0] with { Weight = 10f })));
    }

    [Fact]
    public void Rfa028SegmentsTheEngineSnaps()
    {
        // Two keys 0.3 degrees apart about X, each stored a few units longer than 1.
        var a = new RfaRotKey(160, 0, 0, 0, 16384);
        var b = new RfaRotKey(480, 43, 0, 0, 16384);
        var clip = WithBone(Good(), 1, Good().Bones[1] with { RotationKeys = [a, b, Good().Bones[1].RotationKeys[2]] });
        var d = Lint(clip).Single(x => x.Code == ClipRules.SegmentSnaps);
        var fixedClip = d.QuickFixes.Single().Apply(clip);
        Quiet(ClipRules.SegmentSnaps, Lint(fixedClip));
        // After the fix the engine really interpolates: halfway is about half the angle.
        var mid = ClipSampler.SampleRotation(fixedClip.Bones[1].RotationKeys.AsSpan(), 320);
        Assert.InRange(Quat.AngleDegrees(Quaternion.Identity, mid), 0.1f, 0.2f);
        Quiet(ClipRules.SegmentSnaps, Lint(Good()));
    }

    [Fact]
    public void Rfa030And031TidyUps()
    {
        var keys = Good().Bones[1].RotationKeys;
        var negated = keys[1] with { X = (short)-keys[1].X, Y = (short)-keys[1].Y, Z = (short)-keys[1].Z, W = (short)-keys[1].W };
        var r = Lint(WithBone(Good(), 1, Good().Bones[1] with { RotationKeys = keys.SetItem(1, negated) }));
        Fires(ClipRules.SignDiscontinuity, r);
        Assert.Equal(DiagnosticSeverity.Info, r.First(d => d.Code == ClipRules.SignDiscontinuity).Severity);
        Fires(ClipRules.NonZeroPad, Lint(WithBone(Good(), 1, Good().Bones[1] with { RotationKeys = keys.SetItem(1, keys[1] with { Pad = 3 }) })));
        Quiet(ClipRules.SignDiscontinuity, Lint(Good()));
        Quiet(ClipRules.NonZeroPad, Lint(Good()));
    }

    [Fact]
    public void ResultsAreOrderedErrorsFirst()
    {
        var clip = Good() with { RampIn = 400, RampOut = 400, Version = 9 };
        var results = Lint(clip);
        Assert.Equal(DiagnosticSeverity.Error, results[0].Severity);
        Assert.True(results.Zip(results.Skip(1)).All(p => p.First.Severity >= p.Second.Severity));
    }

    // ── Mesh rules ───────────────────────────────────────────────────────────

    private static V3dFile MeshWithLodVertices(int vertexCount) => Mesh(lod: Lod() with { VertexCount = vertexCount });

    private static V3dLod Lod(V3dBatch? batch = null) => new()
    {
        Flags = V3dLod.FlagCharacter,
        VertexCount = 3,
        Batches = [batch ?? Batch()],
        Textures = [new V3dLodTexture(0, "skin.tga")],
    };

    private static V3dBatch Batch(V3dBoneLink? link = null)
    {
        var l = link ?? new V3dBoneLink(255, 0, 0, 0, 1, 0xFF, 0xFF, 0xFF);
        return new V3dBatch
        {
            Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            TexCoords = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            Triangles = [new V3dTriangle(0, 1, 2, 0)],
            SamePositionOffsets = [0, 0, 0],
            BoneLinks = [l, l, l],
            Sizes = V3dBatchSizes.Canonical(3, 1, true),
        };
    }

    private static V3dFile Mesh(V3dLod? lod = null, ImmutableArray<V3dBone>? bones = null, ImmutableArray<V3dCollisionSphere>? spheres = null,
        ImmutableArray<float>? distances = null, int? headerSubmeshes = null)
    {
        var l = lod ?? Lod();
        var sub = new V3dSubmesh
        {
            Name = FixedString.FromText("body", V3dSubmesh.NameSize),
            LodDistances = distances ?? [0f],
            Lods = distances is { } d ? [.. Enumerable.Repeat(l, d.Length)] : [l],
            Materials = [new V3dMaterial(FixedString.FromText("skin.tga", 32), 0, 0, 0, 0, FixedString.FromText("", 32), 0)],
            Trailers = [new V3dSubmeshTrailer(FixedString.FromText("body", 24), 0)],
        };
        var sections = new List<V3dSection> { sub };
        var sph = spheres ?? [];
        sections.AddRange(sph);
        sections.Add(new V3dBoneSection(bones ?? [Bone("root", -1, Vector3.Zero), Bone("spine", 0, Vector3.Zero)], []));
        return new V3dFile
        {
            Header = new V3dHeader(V3dHeader.CharacterSignature, V3dHeader.CurrentVersion, headerSubmeshes ?? 1, 0, 0, 0, 1, 0, 0, sph.Length),
            Sections = [.. sections],
        };
    }

    private static IReadOnlyList<Diagnostic> LintMesh(V3dFile mesh, MeshLintContext? context = null) => MeshLinter.Analyze(mesh, context);

    [Fact]
    public void TheHealthyMeshHasNoDiagnostics()
    {
        Assert.Empty(LintMesh(Mesh(), new MeshLintContext { FileName = "ok.v3c", TextureExists = _ => true }));
    }

    [Fact]
    public void V3c001And012Header()
    {
        var mesh = Mesh();
        Fires(MeshRules.UnsupportedVersion, LintMesh(mesh with { Header = mesh.Header with { Version = 0x30000 } }));
        Fires(MeshRules.HeaderCounts, LintMesh(Mesh(headerSubmeshes: 2)));
        Quiet(MeshRules.UnsupportedVersion, LintMesh(mesh));
        Quiet(MeshRules.HeaderCounts, LintMesh(mesh));
    }

    [Fact]
    public void V3c002To004And023Bones()
    {
        var many = Enumerable.Range(0, 51).Select(i => Bone($"b{i}", i - 1, Vector3.Zero)).ToImmutableArray();
        Fires(MeshRules.TooManyBones, LintMesh(Mesh(bones: many)));
        Quiet(MeshRules.TooManyBones, LintMesh(Mesh(bones: many.RemoveAt(50))));
        Fires(MeshRules.BadParent, LintMesh(Mesh(bones: [Bone("root", -1, Vector3.Zero), Bone("spine", 7, Vector3.Zero)])));
        Fires(MeshRules.BadParent, LintMesh(Mesh(bones: [Bone("root", -1, Vector3.Zero), Bone("spine", 1, Vector3.Zero)])));
        Fires(MeshRules.ParentCycle, LintMesh(Mesh(bones: [Bone("root", -1, Vector3.Zero), Bone("a", 2, Vector3.Zero), Bone("b", 1, Vector3.Zero)])));
        Quiet(MeshRules.ParentCycle, LintMesh(Mesh()));
        Fires(MeshRules.DuplicateBoneName, LintMesh(Mesh(bones: [Bone("root", -1, Vector3.Zero), Bone("ROOT", 0, Vector3.Zero)])));
        Quiet(MeshRules.DuplicateBoneName, LintMesh(Mesh()));
    }

    [Fact]
    public void V3c005And024Spheres()
    {
        var sphere = new V3dCollisionSphere(FixedString.FromText("head", 24), 1, Vector3.Zero, 0.2f, []);
        Quiet(MeshRules.SphereBone, LintMesh(Mesh(spheres: [sphere])));
        Fires(MeshRules.SphereBone, LintMesh(Mesh(spheres: [sphere with { BoneIndex = 5 }])));
        Fires(MeshRules.SphereRadius, LintMesh(Mesh(spheres: [sphere with { Radius = 0f }])));
        Quiet(MeshRules.SphereRadius, LintMesh(Mesh(spheres: [sphere])));
    }

    [Fact]
    public void V3c006PropPoints()
    {
        var prop = new V3dPropPoint(FixedString.FromText("hand", V3dPropPoint.NameSize), Quaternion.Identity, Vector3.Zero, 1);
        Quiet(MeshRules.PropPointBone, LintMesh(Mesh(lod: Lod() with { PropPoints = [prop] })));
        Fires(MeshRules.PropPointBone, LintMesh(Mesh(lod: Lod() with { PropPoints = [prop with { ParentIndex = 9 }] })));
    }

    [Fact]
    public void V3c007And020Skinning()
    {
        Fires(MeshRules.VertexBone, LintMesh(Mesh(lod: Lod(Batch(new V3dBoneLink(255, 0, 0, 0, 9, 0xFF, 0xFF, 0xFF))))));
        Fires(MeshRules.WeightSum, LintMesh(Mesh(lod: Lod(Batch(new V3dBoneLink(200, 0, 0, 0, 1, 0xFF, 0xFF, 0xFF))))));
        // The stock exporter's rounding (254) is not reported.
        Quiet(MeshRules.WeightSum, LintMesh(Mesh(lod: Lod(Batch(new V3dBoneLink(127, 127, 0, 0, 0, 1, 0xFF, 0xFF))))));
        // Static meshes carry bone links but never skin.
        var staticMesh = Mesh(lod: Lod(Batch(new V3dBoneLink(200, 0, 0, 0, 9, 0xFF, 0xFF, 0xFF))));
        staticMesh = staticMesh with { Header = staticMesh.Header with { Signature = V3dHeader.StaticSignature } };
        Quiet(MeshRules.VertexBone, LintMesh(staticMesh));
        Quiet(MeshRules.WeightSum, LintMesh(staticMesh));
    }

    [Fact]
    public void V3c008And025Lods()
    {
        Fires(MeshRules.LodCount, LintMesh(Mesh(distances: [0f, 10f, 20f, 30f])));
        Quiet(MeshRules.LodCount, LintMesh(Mesh(distances: [0f, 10f, 20f])));
        Fires(MeshRules.LodDistances, LintMesh(Mesh(distances: [0f, 10f, 5f])));
        Quiet(MeshRules.LodDistances, LintMesh(Mesh(distances: [0f, 10f, 20f])));
    }

    [Fact]
    public void V3c009To011BatchesAndTextures()
    {
        var textures = Enumerable.Range(0, 8).Select(i => new V3dLodTexture(0, $"t{i}.tga")).ToImmutableArray();
        Fires(MeshRules.TooManyTextures, LintMesh(Mesh(lod: Lod() with { Textures = textures })));
        Quiet(MeshRules.TooManyTextures, LintMesh(Mesh(lod: Lod() with { Textures = textures.RemoveAt(7) })));
        Fires(MeshRules.TriangleIndex, LintMesh(Mesh(lod: Lod(Batch() with { Triangles = [new V3dTriangle(0, 1, 5, 0)] }))));
        Fires(MeshRules.BatchTexture, LintMesh(Mesh(lod: Lod(Batch() with { TextureIndex = 3 }))));
        Quiet(MeshRules.TriangleIndex, LintMesh(Mesh()));
        Quiet(MeshRules.BatchTexture, LintMesh(Mesh()));
    }

    [Fact]
    public void V3c013NonFinite()
    {
        Fires(MeshRules.NonFinite, LintMesh(Mesh(lod: Lod(Batch() with { Positions = [Vector3.Zero, new Vector3(float.NaN), Vector3.UnitY] }))));
        Quiet(MeshRules.NonFinite, LintMesh(Mesh()));
    }

    [Fact]
    public void V3c014NotWritable()
    {
        Fires(MeshRules.NotWritable, LintMesh(Mesh(lod: Lod(Batch() with { Sizes = new V3dBatchSizes(4, 8, 6, 32, 32) }))));
        Quiet(MeshRules.NotWritable, LintMesh(Mesh()));
    }

    [Fact]
    public void V3c015MorphMap()
    {
        var lod = Lod(Batch() with { MorphMap = [0, 1, 9] }) with { Flags = V3dLod.FlagCharacter | V3dLod.FlagMorphVerticesMap };
        Fires(MeshRules.MorphMapIndex, LintMesh(Mesh(lod: lod)));
        var ok = Lod(Batch() with { MorphMap = [0, -1, 2] }) with { Flags = V3dLod.FlagCharacter | V3dLod.FlagMorphVerticesMap };
        Quiet(MeshRules.MorphMapIndex, LintMesh(Mesh(lod: ok)));
    }

    [Fact]
    public void V3c021MissingTextures()
    {
        var d = LintMesh(Mesh(), new MeshLintContext { TextureExists = _ => false }).Single(x => x.Code == MeshRules.MissingTexture);
        Assert.Contains(d.QuickFixes, f => f.Kind == QuickFixKind.LocateFile && f.Payload == "skin.tga");
        // The App's Locate fix finds the material to rename through the LOD texture entry the problem points at.
        Assert.Equal(MeshNodeKind.Texture, d.Location.MeshNode?.Kind);
        Quiet(MeshRules.MissingTexture, LintMesh(Mesh(), new MeshLintContext { TextureExists = _ => true }));
        Quiet(MeshRules.MissingTexture, LintMesh(Mesh())); // no resolver, no rule
    }

    [Fact]
    public void V3c022And026Names()
    {
        var full = FixedString.FromBytes(System.Text.Encoding.Latin1.GetBytes(new string('n', 24)));
        Fires(MeshRules.NameTooLong, LintMesh(Mesh(bones: [new V3dBone(full, Quaternion.Identity, Vector3.Zero, -1)])));
        Fires(MeshRules.NameTooLong, LintMesh(Mesh(lod: Lod() with { Textures = [new V3dLodTexture(0, new string('t', 32) + ".tga")] })));
        Quiet(MeshRules.NameTooLong, LintMesh(Mesh()));
        Fires(MeshRules.FileNameTooLong, LintMesh(Mesh(), new MeshLintContext { FileName = new string('m', 60) + ".v3c" }));
        Quiet(MeshRules.FileNameTooLong, LintMesh(Mesh(), new MeshLintContext { FileName = "m.v3c" }));
    }

    [Fact]
    public void V3c027NoSubmeshes()
    {
        // what an unreadable legacy mesh's tab would have written: a header and nothing else
        var empty = new V3dFile { Header = new V3dHeader(V3dHeader.StaticSignature, V3dHeader.CurrentVersion, 0, 0, 0, 0, 0, 0, 0, 0) };
        Fires(MeshRules.NoSubmeshes, LintMesh(empty));
        Fires(MeshRules.NoSubmeshes, LintMesh(V3dReader.Read(V3dWriter.Write(empty), "empty.v3m")));
        Quiet(MeshRules.NoSubmeshes, LintMesh(Mesh()));
    }
}
