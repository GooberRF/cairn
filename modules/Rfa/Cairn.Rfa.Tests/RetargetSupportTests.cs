using System.Buffers.Binary;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Tests;

/// <summary>Stock rigs and clips for the retarget tests, read in place from the corpus (null when absent).</summary>
internal static class RetargetCorpus
{
    /// <summary>The three rig A source clips of the reference, with the clip token of their output names.</summary>
    public static readonly (string Source, string Clip)[] SourceClips =
    [
        ("park_jeep_driver.rfa", "jeep_driver"),
        ("park_jeep_gunner.rfa", "jeep_gunner"),
        ("ult2_on_turret.rfa", "on_turret"),
    ];

    /// <summary>
    /// The golden outputs' folders (Alpine Faction's resources/animations, and any kept earlier generation),
    /// empty when not configured (<c>CAIRN_GOLDEN_CLIPS</c> or <c>goldenClips</c> in research/local-paths.json).
    /// </summary>
    public static IReadOnlyList<string> Goldens => TestPaths.GoldenClips;

    /// <summary>
    /// The two quantisations a golden file may have been written with: the retargeter's default (the files
    /// RFA Workbench itself writes) and the reference rounding (the files retarget.py wrote).
    /// </summary>
    public static readonly KeyQuantization[] Generations = [KeyQuantization.WithinUnit, KeyQuantization.Reference];

    /// <summary>The Seated preset's options with the given rotation-key quantisation.</summary>
    public static RetargetOptions Seated(KeyQuantization quantization) =>
        RetargetPresets.Options(RetargetPreset.Seated, new RetargetOptions { Quantization = quantization });

    /// <summary>The generation's name for test output.</summary>
    public static string Describe(KeyQuantization quantization) => quantization == KeyQuantization.Reference ? "reference rounding" : "default quantisation";

    /// <summary>
    /// The quantisation(s) whose Seated-preset retarget of <paramref name="clip"/> gives exactly the golden file's
    /// bytes; empty when the file matches neither generation.
    /// </summary>
    public static KeyQuantization[] GenerationsOf(string goldenPath, RfaClip clip, RetargetRig source, RetargetRig target)
    {
        byte[] golden = File.ReadAllBytes(goldenPath);
        return Generations.Where(q =>
        {
            var result = Retargeter.Retarget(new RetargetRequest(clip, source, target) { Options = Seated(q) });
            return result.Success && RfaWriter.Write(result.Clip!).AsSpan().SequenceEqual(golden);
        }).ToArray();
    }

    public static RfaClip? Clip(string name) => TestPaths.CorpusFile(name) is { } p ? RfaReader.ReadFile(p) : null;

    public static V3dFile? Mesh(string name) => TestPaths.CorpusFile(name) is { } p ? V3dReader.ReadFile(p) : null;

    /// <summary>A built-in rig from its rest mesh and stand clip, or null when the corpus is absent.</summary>
    public static RetargetRig? Rig(RigProfile profile)
    {
        var mesh = Mesh(profile.RestMesh!);
        var stand = Clip(profile.ReferenceClip!);
        return mesh is null || stand is null ? null : RetargetRig.FromMesh(mesh, profile, stand, profile.ReferenceClip);
    }

    /// <summary>The golden file of this name in every configured folder that has it (none when not configured).</summary>
    public static string[] Golden(string name) =>
        Goldens.Select(folder => Path.Combine(folder, name)).Where(File.Exists).ToArray();
}

/// <summary>Clip comparisons used by the retarget tests.</summary>
internal static class ClipCompare
{
    /// <summary>Largest per-bone local rotation difference in degrees over samples every <paramref name="step"/> ticks.</summary>
    public static double MaxRotationDegrees(RfaClip a, RfaClip b, bool engine, int step = 160)
    {
        double worst = 0;
        foreach (int t in RetargetReport.SampleTimes(a, step))
        {
            for (int i = 0; i < Math.Min(a.BoneCount, b.BoneCount); i++)
            {
                double d = engine
                    ? RefMath.AngleDegrees(DQuat.From(ClipSampler.SampleRotation(a.Bones[i].RotationKeys.AsSpan(), t)), DQuat.From(ClipSampler.SampleRotation(b.Bones[i].RotationKeys.AsSpan(), t)))
                    : RefMath.AngleDegrees(ReferenceSampler.SampleRotation(a.Bones[i].RotationKeys.AsSpan(), t), ReferenceSampler.SampleRotation(b.Bones[i].RotationKeys.AsSpan(), t));
                worst = Math.Max(worst, d);
            }
        }
        return worst;
    }

    /// <summary>Largest per-bone local position difference in metres.</summary>
    public static double MaxPositionMetres(RfaClip a, RfaClip b, int step = 160)
    {
        double worst = 0;
        foreach (int t in RetargetReport.SampleTimes(a, step))
        {
            for (int i = 0; i < Math.Min(a.BoneCount, b.BoneCount); i++)
            {
                var pa = ClipSampler.SamplePosition(a.Bones[i].PositionKeys.AsSpan(), t);
                var pb = ClipSampler.SamplePosition(b.Bones[i].PositionKeys.AsSpan(), t);
                worst = Math.Max(worst, (pa - pb).Length());
            }
        }
        return worst;
    }

    /// <summary>Largest model-space joint position difference in metres (engine sampler).</summary>
    public static double MaxModelSpaceMetres(RfaClip a, RfaClip b, Skeleton skeleton, int step = 160)
    {
        double worst = 0;
        var pa = new Pose(skeleton);
        var pb = new Pose(skeleton);
        foreach (int t in RetargetReport.SampleTimes(a, step))
        {
            pa.Sample(a, t);
            pb.Sample(b, t);
            for (int i = 0; i < skeleton.Count; i++) worst = Math.Max(worst, (pa.World[i].Position - pb.World[i].Position).Length());
        }
        return worst;
    }

    public sealed record ByteDiff(int DifferingBytes, int TotalBytes, int RotComponents, int MaxRotUnits, int PosFloats, long MaxPosUlps, string Structure);

    /// <summary>Byte and field level differences of two clips with the same structure.</summary>
    public static ByteDiff Bytes(RfaClip a, RfaClip b)
    {
        byte[] ba = RfaWriter.Write(a), bb = RfaWriter.Write(b);
        int bytes = 0;
        for (int i = 0; i < Math.Min(ba.Length, bb.Length); i++)
        {
            if (ba[i] != bb[i]) bytes++;
        }
        bytes += Math.Abs(ba.Length - bb.Length);
        if (a.BoneCount != b.BoneCount) return new ByteDiff(bytes, bb.Length, -1, -1, -1, -1, "bone counts differ");
        int rot = 0, maxRot = 0, pos = 0;
        long maxUlps = 0;
        string structure = "same";
        for (int i = 0; i < a.BoneCount; i++)
        {
            var x = a.Bones[i];
            var y = b.Bones[i];
            if (x.RotationKeys.Length != y.RotationKeys.Length || x.PositionKeys.Length != y.PositionKeys.Length || x.Weight != y.Weight)
            {
                structure = $"bone {i} differs in key counts or weight";
                continue;
            }
            for (int k = 0; k < x.RotationKeys.Length; k++)
            {
                var p = x.RotationKeys[k];
                var q = y.RotationKeys[k];
                if (p.Time != q.Time || p.EaseIn != q.EaseIn || p.EaseOut != q.EaseOut) structure = $"bone {i} key {k} time/ease";
                foreach (var (u, v) in new[] { (p.X, q.X), (p.Y, q.Y), (p.Z, q.Z), (p.W, q.W) })
                {
                    if (u == v) continue;
                    rot++;
                    maxRot = Math.Max(maxRot, Math.Abs(u - v));
                }
            }
            for (int k = 0; k < x.PositionKeys.Length; k++)
            {
                var p = x.PositionKeys[k];
                var q = y.PositionKeys[k];
                if (p.Time != q.Time) structure = $"bone {i} pos key {k} time";
                foreach (var (u, v) in Floats(p).Zip(Floats(q)))
                {
                    if (BitConverter.SingleToInt32Bits(u) == BitConverter.SingleToInt32Bits(v)) continue;
                    pos++;
                    maxUlps = Math.Max(maxUlps, Ulps(u, v));
                }
            }
        }
        return new ByteDiff(bytes, bb.Length, rot, maxRot, pos, maxUlps, structure);

        static IEnumerable<float> Floats(RfaPosKey k) =>
            [k.Position.X, k.Position.Y, k.Position.Z, k.InControl.X, k.InControl.Y, k.InControl.Z, k.OutControl.X, k.OutControl.Y, k.OutControl.Z];
    }

    private static long Ulps(float a, float b)
    {
        long ia = BitConverter.SingleToInt32Bits(a), ib = BitConverter.SingleToInt32Bits(b);
        if (ia < 0) ia = int.MinValue - ia;
        if (ib < 0) ib = int.MinValue - ib;
        return Math.Abs(ia - ib);
    }

    /// <summary>Reads a little-endian int16 (kept for byte-level spot checks).</summary>
    public static short Int16(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset));
}
