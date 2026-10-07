using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Retarget;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// The nine golden clips (Alpine Faction's resources/animations/af_*.rfa) reproduced from the stock
/// sources with the built-in profiles and the Seated preset. Two generations exist: the reference
/// retarget.py's (reproduced with its rounding, <see cref="RetargetOptions.Reference"/>) and RFA
/// Workbench's own (the default quantisation); each configured golden folder is checked and every file
/// must be byte-identical to one of them. Read in place; pass trivially when the corpus or the goldens
/// are absent.
/// </summary>
public class RetargetGoldenTests(ITestOutputHelper output)
{
    public static TheoryData<string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (source, clip) in RetargetCorpus.SourceClips)
        {
            foreach (string rig in new[] { "female", "merc", "civilian" }) data.Add(rig, source, clip);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void GoldenClipIsReproducedByteForByte(string rig, string sourceName, string clipName)
    {
        string name = $"af_{rig}_{clipName}.rfa";
        string[] goldens = RetargetCorpus.Golden(name);
        var sourceClip = RetargetCorpus.Clip(sourceName);
        var source = RetargetCorpus.Rig(RigProfiles.RigA);
        var targetProfile = RigProfiles.Get(rig)!;
        var target = RetargetCorpus.Rig(targetProfile);
        if (goldens.Length == 0 || sourceClip is null || source is null || target is null) return;

        // Two generations of the goldens exist: retarget.py's (the reference's rounding: half to even, keys may be
        // longer than 1) and RFA Workbench's own (the default quantisation), both with the Seated preset.
        var outputs = new Dictionary<KeyQuantization, RfaClip>();
        foreach (var quantization in RetargetCorpus.Generations)
        {
            var preset = Retargeter.Retarget(new RetargetRequest(sourceClip, source, target) { Options = RetargetCorpus.Seated(quantization) });
            Assert.True(preset.Success, preset.Error);
            // The seated preset IS the reference method: the same bytes as the plain options with that quantisation.
            var plain = Retargeter.Retarget(new RetargetRequest(sourceClip, source, target) { Options = RetargetOptions.Default with { Quantization = quantization } });
            Assert.True(plain.Success, plain.Error);
            Assert.Equal(RfaWriter.Write(plain.Clip!), RfaWriter.Write(preset.Clip!));
            outputs[quantization] = preset.Clip!;
            if (quantization == KeyQuantization.Reference)
            {
                foreach (string line in preset.ReportLines) output.WriteLine("    " + line);
            }
        }
        Assert.Equal(RetargetPreset.Seated, RetargetPresets.Suggest(sourceName, null).Preset);

        // Every configured golden must be byte-identical to one of the two.
        foreach (string golden in goldens)
        {
            string label = $"{Path.GetFileName(Path.GetDirectoryName(golden))}/{name}";
            byte[] file = File.ReadAllBytes(golden);
            var expected = RfaReader.ReadFile(golden);
            var matched = new List<KeyQuantization>();
            foreach (var (quantization, actual) in outputs)
            {
                if (RfaWriter.Write(actual).AsSpan().SequenceEqual(file)) matched.Add(quantization);
                var diff = ClipCompare.Bytes(actual, expected);
                double rotRef = ClipCompare.MaxRotationDegrees(actual, expected, engine: false);
                double rotEngine = ClipCompare.MaxRotationDegrees(actual, expected, engine: true);
                double pos = ClipCompare.MaxPositionMetres(actual, expected);
                double model = ClipCompare.MaxModelSpaceMetres(actual, expected, target.Skeleton);
                output.WriteLine($"  {label} vs {RetargetCorpus.Describe(quantization)}: bytes differing {diff.DifferingBytes}/{diff.TotalBytes}, rot components {diff.RotComponents} (max {diff.MaxRotUnits} units), "
                    + $"pos floats {diff.PosFloats} (max {diff.MaxPosUlps} ulps), structure {diff.Structure}; "
                    + $"max rotation {rotRef:0.0000} deg (reference sampler) / {rotEngine:0.0000} deg (engine), max local position {pos * 1000:0.0000} mm, model space {model * 1000:0.0000} mm");
            }
            string verdict = matched.Count == 0 ? "matches NEITHER generation"
                : "byte-identical to the Seated preset with " + string.Join(" and ", matched.Select(RetargetCorpus.Describe));
            output.WriteLine($"{label}: {verdict}");
            Assert.True(matched.Count > 0, $"{label}: {verdict}");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void GoldenOutputPassesTheChecklist(string rig, string sourceName, string clipName)
    {
        var sourceClip = RetargetCorpus.Clip(sourceName);
        var source = RetargetCorpus.Rig(RigProfiles.RigA);
        var target = RetargetCorpus.Rig(RigProfiles.Get(rig)!);
        if (sourceClip is null || source is null || target is null) return;

        var result = Retargeter.Retarget(new RetargetRequest(sourceClip, source, target));
        Assert.True(result.Success, result.Error);
        var report = RetargetReport.Build(sourceClip, source.Skeleton, source.Profile, result.Clip!, target.Skeleton, target.Profile, result.BoneMap!);
        output.WriteLine($"af_{rig}_{clipName}: {report.Summary}");
        foreach (var check in report.Checks) output.WriteLine($"  {(check.Passed ? "ok  " : "FAIL")} {check.Name}: {check.Message}");
        Assert.True(report.StructureOk, string.Join(" | ", report.Checks.Where(c => !c.Passed).Select(c => c.Message)));
        // After IK the hands and feet sit on the source's in model space (verify/fk_report.md: 0.0 cm).
        Assert.True(report.HandsModelSpaceMaxCm < 0.5, $"hands {report.HandsModelSpaceMaxCm} cm");
        Assert.True(report.FeetModelSpaceMaxCm < 0.5, $"feet {report.FeetModelSpaceMaxCm} cm");
        Assert.Equal(target.Skeleton.Count, report.Joints.Length + result.BoneMap!.Count(BoneMapStatus.Unmapped));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DefaultRoundingKeepsEverySegmentInterpolatingWhereTheGoldensSnap(string rig, string sourceName, string clipName)
    {
        string name = $"af_{rig}_{clipName}.rfa";
        string[] goldens = RetargetCorpus.Golden(name);
        var sourceClip = RetargetCorpus.Clip(sourceName);
        var source = RetargetCorpus.Rig(RigProfiles.RigA);
        var target = RetargetCorpus.Rig(RigProfiles.Get(rig)!);
        if (goldens.Length == 0 || sourceClip is null || source is null || target is null) return;

        var ours = Retargeter.Retarget(new RetargetRequest(sourceClip, source, target)).Clip!;
        // Same motion otherwise: within one int16 unit per component of the reference rounding.
        var reference = Retargeter.Retarget(new RetargetRequest(sourceClip, source, target) { Options = RetargetOptions.Reference }).Clip!;
        int Snaps(RfaClip c) => Cairn.Rfa.Linting.ClipLinter.Analyze(c).Count(d => d.Code == Cairn.Rfa.Linting.ClipRules.SegmentSnaps);
        output.WriteLine($"{name}: bones with a segment the engine snaps: reference rounding {Snaps(reference)}, default output {Snaps(ours)}");
        foreach (string golden in goldens)
        {
            byte[] file = File.ReadAllBytes(golden);
            string generation = RfaWriter.Write(ours).AsSpan().SequenceEqual(file) ? "default quantisation"
                : RfaWriter.Write(reference).AsSpan().SequenceEqual(file) ? "reference rounding" : "neither generation";
            output.WriteLine($"  {Path.GetFileName(Path.GetDirectoryName(golden))}/{name} ({generation}): {Snaps(RfaReader.ReadFile(golden))}");
        }
        Assert.Equal(0, Snaps(ours));
        Assert.True(ClipCompare.MaxRotationDegrees(ours, reference, engine: false) < 0.02);
    }
}
