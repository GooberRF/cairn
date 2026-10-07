using System.Diagnostics;
using Cairn.Rfa.Animation;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using Cairn.Formats.Maths;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Interop with REDUX (read-only, skipped when its folder is absent): its Blender sample glTF imports
/// here as a mesh and as a clip and is compared with the .rfa / .v3c REDUX made from it; and when a
/// built redux.exe is present it converts a file this project exported (outputs go to a temp folder).
/// </summary>
public class GltfReduxInteropTests(ITestOutputHelper output)
{
    private static string? Sample(string name) =>
        TestPaths.ReduxResearch is { } dir && File.Exists(Path.Combine(dir, "anim", name)) ? Path.Combine(dir, "anim", name) : null;

    [Fact]
    public void ReduxSampleImportsAsMeshAndClipLikeReduxsOwnOutput()
    {
        if (Sample("DW-Fungi2Anim.gltf") is not { } gltfPath) return;
        var doc = GltfReader.ReadFile(gltfPath);

        var mesh = GltfMeshImport.Import(doc, fileName: "DW-Fungi2Anim.gltf");
        foreach (var issue in mesh.Issues) output.WriteLine($"pre-flight {issue.Severity} {issue.Code}: {issue.Message}");
        Assert.False(mesh.HasErrors);
        Assert.NotNull(mesh.Mesh);
        var ours = mesh.Mesh!;
        V3dReader.Read(V3dWriter.Write(ours), "ours.v3c");

        foreach (string reduxName in new[] { "DW-Fungi2AnimR.v3c", "DW-Fungi2V.v3c" })
        {
            if (Sample(reduxName) is not { } p) continue;
            var theirs = V3dReader.ReadFile(p);
            var a = Skeleton.FromFile(ours);
            var b = Skeleton.FromFile(theirs);
            output.WriteLine($"{reduxName}: bones ours {a.Count} [{string.Join(", ", a.Names)}] / theirs {b.Count} [{string.Join(", ", b.Names)}]");
            int n = Math.Min(a.Count, b.Count);
            float rot = 0, pos = 0;
            for (int i = 0; i < n; i++)
            {
                rot = MathF.Max(rot, Quat.AngleDegrees(a.RestWorld[i].Rotation, b.RestWorld[i].Rotation));
                pos = MathF.Max(pos, (a.RestWorld[i].Position - b.RestWorld[i].Position).Length());
            }
            var vo = ours.Submeshes.SelectMany(s => s.Lods[0].Batches).Sum(x => x.VertexCount);
            var vt = theirs.Submeshes.SelectMany(s => s.Lods[0].Batches).Sum(x => x.VertexCount);
            var to = ours.Submeshes.SelectMany(s => s.Lods[0].Batches).Sum(x => x.TriangleCount);
            var tt = theirs.Submeshes.SelectMany(s => s.Lods[0].Batches).Sum(x => x.TriangleCount);
            var boxO = Bounds(ours);
            var boxT = Bounds(theirs);
            output.WriteLine($"  rest pose differences: rotation {rot:G4} deg, position {pos:G4} m");
            output.WriteLine($"  LOD0 vertices ours {vo} / theirs {vt}; triangles ours {to} / theirs {tt}");
            output.WriteLine($"  bounds ours {boxO.Min}..{boxO.Max} / theirs {boxT.Min}..{boxT.Max}");
            output.WriteLine($"  materials ours [{string.Join(", ", ours.Submeshes.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text))}] / theirs [{string.Join(", ", theirs.Submeshes.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text))}]");
            if (reduxName.Contains("AnimR", StringComparison.Ordinal))
            {
                Assert.Equal(b.Count, a.Count);
                Assert.True(rot < 0.01f && pos < 1e-4f, $"rest pose differs from REDUX's: {rot} deg, {pos} m");
                Assert.Equal(tt, to);
                Assert.True((boxO.Min - boxT.Min).Length() < 1e-4f && (boxO.Max - boxT.Max).Length() < 1e-4f);
            }
        }

        var sk = Skeleton.FromFile(ours);
        var clip = GltfAnimationImport.Import(doc, sk, fileName: "DW-Fungi2Anim.gltf").Single();
        output.WriteLine($"clip '{clip.Name}': {clip.Report.StartTime}..{clip.Report.EndTime}, {clip.Report.RotationKeys} rotation / {clip.Report.PositionKeys} position keys, modes [{string.Join(", ", clip.Report.Bones.Select(x => x.Mode))}]");
        foreach (var w in clip.Report.Warnings) output.WriteLine($"  warning: {w}");
        foreach (string reduxName in new[] { "DW-Fungi2AnimR.rfa", "DW-Fungi2AnimV.rfa" })
        {
            if (Sample(reduxName) is not { } p) continue;
            var theirs = RfaReader.ReadFile(p);
            output.WriteLine($"{reduxName}: v{theirs.Version} {theirs.StartTime}..{theirs.EndTime} ramps {theirs.RampIn}/{theirs.RampOut} weights [{string.Join(", ", theirs.Bones.Select(x => x.Weight))}] keys {theirs.Bones.Sum(x => x.RotationKeys.Length)}/{theirs.Bones.Sum(x => x.PositionKeys.Length)}");
            if (theirs.BoneCount != clip.Clip.BoneCount) continue;
            // REDUX keeps glTF times as absolute ticks (no shift to 160); compare on its timeline.
            int shift = theirs.Bones.SelectMany(x => x.RotationKeys.Select(k => k.Time)).DefaultIfEmpty(0).Min() - clip.Clip.Bones.SelectMany(x => x.RotationKeys.Select(k => k.Time)).DefaultIfEmpty(0).Min();
            var shifted = Editing.ClipEdit.Shift(clip.Clip, shift);
            float rot = 0, pos = 0;
            var pa = new Pose(sk);
            var pb = new Pose(sk);
            for (int t = theirs.StartTime; t <= theirs.EndTime; t += 160)
            {
                pa.Sample(shifted, t);
                pb.Sample(theirs, t);
                for (int i = 0; i < sk.Count; i++)
                {
                    rot = MathF.Max(rot, Quat.AngleDegrees(pa.Local[i].Rotation, pb.Local[i].Rotation));
                    pos = MathF.Max(pos, (pa.Local[i].Position - pb.Local[i].Position).Length());
                }
            }
            output.WriteLine($"  sampled pose vs ours (shifted {shift} ticks): rotation {rot:G4} deg, position {pos:G4} m");
            // Both quantise 24 fps keys a fraction of a degree apart; the engine snaps a segment whose keys are under 0.16 degrees apart to its later key, and REDUX (truncating) and this importer (nearest within unit length) snap different segments: up to 2 x 0.16 degrees.
            if (reduxName.Contains("AnimR", StringComparison.Ordinal)) Assert.True(rot < 0.35f && pos < 1e-4f);
        }
    }

    private static (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) Bounds(V3dFile f)
    {
        var pts = f.Submeshes.SelectMany(s => s.Lods[0].Batches.SelectMany(b => b.Positions.Select(p => p + s.Offset))).ToList();
        return (pts.Aggregate(System.Numerics.Vector3.Min), pts.Aggregate(System.Numerics.Vector3.Max));
    }

    [Fact]
    public async Task ReduxConvertsAFileThisProjectExported()
    {
        if (TestPaths.ReduxResearch is null) return;
        // A built REDUX keeps redux.exe in its own dist folder, next to the research folder.
        string exe = Path.GetFullPath(Path.Combine(TestPaths.ReduxResearch, "..", "dist", "redux.exe"));
        if (!File.Exists(exe)) return;

        using var temp = new TempFolder();
        var mesh = V3dBuilder.Build(V3dBuilderTests.SampleDescription());
        var clip = EditingTestClips.Make(2);
        string gltf = temp.File("sample.gltf");
        GltfExport.ExportToFile(gltf, mesh, [new GltfExportClip("sample", clip)]);

        foreach (string format in new[] { "rfa", "v3c" })
        {
            var psi = new ProcessStartInfo(exe, ["-input", gltf, "-outformat", format])
            {
                WorkingDirectory = temp.Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                p.Kill(entireProcessTree: true);
                Assert.Fail($"redux.exe -outformat {format} did not finish within 20 s.");
            }
            string text = await stdout + await stderr;
            output.WriteLine($"redux.exe -outformat {format}: exit {p.ExitCode}");
            foreach (var line in text.Split('\n').Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains("WARN", StringComparison.OrdinalIgnoreCase) || l.Contains("Read glTF", StringComparison.OrdinalIgnoreCase)).Take(10))
                output.WriteLine("  " + line.Trim());
        }

        string rfa = temp.File("sample.rfa");
        Assert.True(File.Exists(rfa), "REDUX wrote no .rfa");
        var theirs = RfaReader.ReadFile(rfa);
        var sk = Skeleton.FromFile(mesh);
        var (rot, pos, _) = GltfInterchangeTests.PoseError(sk, clip, theirs);
        output.WriteLine($"REDUX .rfa: {theirs.StartTime}..{theirs.EndTime}, ramps {theirs.RampIn}/{theirs.RampOut}, keys {theirs.Bones.Sum(b => b.RotationKeys.Length)}/{theirs.Bones.Sum(b => b.PositionKeys.Length)}; sampled pose vs the original: rotation {rot:G4} deg, position {pos:G4} m");
        Assert.Equal(clip.StartTime, theirs.StartTime);
        Assert.Equal(clip.EndTime, theirs.EndTime);
        Assert.True(rot < 1f && pos < 1e-4f);

        string v3c = temp.File("sample.v3c");
        Assert.True(File.Exists(v3c), "REDUX wrote no .v3c");
        var theirMesh = V3dReader.ReadFile(v3c);
        var tk = Skeleton.FromFile(theirMesh);
        output.WriteLine($"REDUX .v3c: {theirMesh.Submeshes.Count()} submeshes, LODs {string.Join("/", theirMesh.Submeshes.Select(s => s.Lods.Length))}, {tk.Count} bones, {theirMesh.CollisionSpheres.Count()} spheres, LOD0 triangles {theirMesh.Submeshes.Sum(s => s.Lods[0].Batches.Sum(b => b.TriangleCount))}");
        Assert.Equal(sk.Count, tk.Count);
        for (int i = 0; i < sk.Count; i++)
            Assert.True((sk.RestWorld[i].Position - tk.RestWorld[i].Position).Length() < 1e-4f);
    }
}
