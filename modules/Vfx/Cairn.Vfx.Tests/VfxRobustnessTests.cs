extern alias vfxui;
using System.Diagnostics;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using vfxui::Cairn.Vfx.Ui.VertexEditing;
using Cairn.Workspace;

namespace Cairn.Vfx.Tests;

/// <summary>Review-findings-2 #3, #4, #5, #7, #8, #10: simulator bounds, record-preserving vertex edits, NaN guards.</summary>
public sealed class VfxRobustnessTests
{
    private static IEnumerable<(string Name, VfxFile File)> Stock() =>
        LocalPaths.Corpus is { } dir && Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.vfx").Order(StringComparer.OrdinalIgnoreCase).Select(f => (Path.GetFileName(f), VfxReader.Read(File.ReadAllBytes(f), Path.GetFileName(f))))
            : [];

    private static VfxFile? ParticleFile() =>
        Stock().Select(x => x.File).FirstOrDefault(x => x.Sections.OfType<VfxParticleSystem>().Any(p => p.ParticleCount > 0));

    [Fact]
    public async Task Simulator_AdvanceNonFinite_Terminates()
    {
        if (ParticleFile() is not { } f) return;
        var sim = new VfxParticleSimulator(new VfxSampler(f), 0);
        var t = Task.Run(() => { sim.Advance(float.PositiveInfinity); sim.Advance(float.NaN); sim.Advance(1e30f); });
        Assert.True(await Task.WhenAny(t, Task.Delay(20000)) == t, "Advance(+Infinity / NaN / 1e30) still running after 20 s");
        await t;
    }

    [Fact]
    public void Reader_HugeMeshFrameCount_IsRejectedQuickly()
    {
        var f = VfxEdit.AddSection(VfxBuilder.NewFile(), VfxBuilder.ImageMaterial("a.tga"));
        f = VfxEdit.AddSection(f, VfxPrimitives.Plane("P"));
        int meshIndex = f.Sections.IndexOf(f.Sections.OfType<VfxMesh>().Single());
        // Locate the mesh frame-count field at its real offset: write the same mesh with 1 and with 2 frames and
        // take the offset where the first file holds int 1 and the second int 2, preceded by the 2-frame mesh's
        // (start time, end time) pair (the reader's layout: ..., start, end, frame count).
        var f2 = VfxEdit.SetFrameCount(f, meshIndex, 2);
        var mesh1 = (VfxMesh)f.Sections[meshIndex];
        var mesh2 = (VfxMesh)f2.Sections[meshIndex];
        Assert.Single(mesh1.Frames);
        Assert.Equal(2, mesh2.Frames.Length);
        byte[] bytes = VfxWriter.Write(f);
        byte[] bytes2 = VfxWriter.Write(f2);
        Assert.True(bytes.Length < 1024, $"test file is {bytes.Length} bytes");
        var times2 = new byte[8];
        BitConverter.TryWriteBytes(times2.AsSpan(0), mesh2.StartTime ?? 0f);
        BitConverter.TryWriteBytes(times2.AsSpan(4), mesh2.EndTime ?? 0f);
        var candidates = Enumerable.Range(8, Math.Min(bytes.Length, bytes2.Length) - 12)
            .Where(i => BitConverter.ToInt32(bytes, i) == 1 && BitConverter.ToInt32(bytes2, i) == 2
                        && bytes2.AsSpan(i - 8, 8).SequenceEqual(times2))
            .ToList();
        Assert.True(candidates.Count == 1, $"frame count field: {candidates.Count} candidate offsets");
        int at = candidates[0];
        VfxReader.Read(bytes, "huge-frames.vfx"); // the intact file reads (and the reader is JIT-compiled before timing)
        // 100,000 = the reader's frame-count limit: passes the range check, so only the remaining-bytes check rejects it
        BitConverter.TryWriteBytes(bytes.AsSpan(at), 100_000);
        var sw = Stopwatch.StartNew();
        Assert.Throws<Cairn.Formats.AssetFormatException>(() => VfxReader.Read(bytes, "huge-frames.vfx"));
        Assert.True(sw.ElapsedMilliseconds < 100, $"rejecting a {bytes.Length}-byte file that claims 100,000 frames took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Simulator_BackwardScrub_IsBoundedAndDeterministic()
    {
        if (ParticleFile() is not { } f) return;
        var sampler = new VfxSampler(f);
        var sim = new VfxParticleSimulator(sampler, 0);
        sim.Advance(9000);
        long before = sim.StepsRun;
        var sw = Stopwatch.StartNew();
        sim.Advance(8999);
        long back = sw.ElapsedMilliseconds;
        long replayed = sim.StepsRun - before;
        var fresh = new VfxParticleSimulator(sampler, 0);
        fresh.Advance(8999);
        Assert.Equal(fresh.Particles.ToArray(), sim.Particles.ToArray());
        // Deterministic bound: one frame back replays from the nearest snapshot, not from frame 0
        // (a wall-clock bound here failed under parallel test load). The time bound is only a gross-regression net.
        Assert.True(replayed * 10 < fresh.StepsRun, $"one frame back at frame 9000 replayed {replayed} of {fresh.StepsRun} steps");
        Assert.True(back < 2000, $"one frame back at frame 9000 took {back} ms");
    }

    [Fact]
    public void Track_HugeOrNaNFrame_HoldsLastKey()
    {
        Assert.Equal(1f, VfxSampler.Track([0.5f, 1f], 15, 3e9f, 0));
        Assert.Equal(1f, VfxSampler.Track([0.5f, 1f], 1e30f, 10, 0));
        Assert.Equal(1f, VfxSampler.Track([0.5f, 1f], 15, float.NaN, 0));
    }

    [Fact]
    public void Quantise_NonFinite_Throws()
    {
        Assert.Throws<ArgumentException>(() => VfxGeometry.Quantise([new Vector3(0, 0, 0), new Vector3(float.NaN, 1, 1), new Vector3(1, 1, 1)]));
        Assert.Throws<ArgumentException>(() => VfxGeometry.Quantise([new Vector3(float.PositiveInfinity, 0, 0)]));
    }

    [Fact]
    public void FrameCount_AgreesWithEndTime_ForAnyFps()
    {
        foreach (int fps in new[] { -5, 0, 1, 15, 30 })
            foreach (int n in new[] { 1, 2, 10 })
                Assert.Equal(n, VfxGeometry.FrameCount(0, VfxGeometry.EndTime(0, n, fps), fps));
        Assert.Equal(1, VfxGeometry.FrameCount(0, float.NaN, 15));
    }

    /// <summary>Setting one face's smoothing group keeps every record whose faces are all untouched (it used to rebuild all of them).</summary>
    [Fact]
    public void SetFaceSmoothing_PreservesUntouchedRecords()
    {
        int checkedMeshes = 0;
        foreach (var (name, f0) in Stock().Take(40))
        {
            var f = f0.Version == VfxVersion.Current ? f0 : VfxUpgrade.ToCurrent(f0);
            for (int i = 0; i < f.Sections.Length; i++)
            {
                if (f.Sections[i] is not VfxMesh m || m.Faces.Length < 4) continue;
                var recs = m.FaceVertices;
                if (!m.Faces.All(fc => new[] { (fc.FaceVertex0, fc.V0), (fc.FaceVertex1, fc.V1), (fc.FaceVertex2, fc.V2) }
                    .All(p => (uint)p.Item1 < (uint)recs.Length && recs[p.Item1].VertexIndex == p.Item2))) continue;
                int group = m.Faces[0].SmoothingGroup == 0 ? 1 << 30 : 0;
                var rm = (VfxMesh)VfxEdit.SetFaceSmoothing(f, i, group, [0]).Sections[i];
                checkedMeshes++;
                var untouched = recs.Select((r, k) => (r, k)).Where(x => x.r.AdjacentFaces.Length > 0 && !x.r.AdjacentFaces.Contains(0)).ToArray();
                foreach (var (r, k) in untouched)
                    Assert.True(rm.FaceVertices.Any(n => n.VertexIndex == r.VertexIndex && n.SmoothingGroup == r.SmoothingGroup && n.AdjacentFaces.Where(a => a != 0).ToHashSet().SetEquals(r.AdjacentFaces)), // face 0 may join it
                        $"{name}#{i} ({m.Name}): record {k} (v{r.VertexIndex}, sg {r.SmoothingGroup}) lost by a smoothing edit on face 0");
            }
        }
    }

    /// <summary>
    /// Deleting one vertex keeps every surviving face-vertex record (vfx-format.md 6.1): the record count drops only by the
    /// records whose faces were all removed, faces away from the edit keep identical engine normals, and the input is untouched.
    /// </summary>
    [Fact]
    public void VertexDelete_PreservesStockFaceVertexRecords()
    {
        var fails = new List<string>();
        int checkedMeshes = 0;
        foreach (var (name, f0) in Stock())
        {
            var f = f0.Version == VfxVersion.Current ? f0 : VfxUpgrade.ToCurrent(f0);
            for (int i = 0; i < f.Sections.Length; i++)
            {
                if (f.Sections[i] is not VfxMesh m || m.Faces.Length < 4 || VfxVertexEdits.Unavailable(f, i) is not null) continue;
                if (m.Frames.Length == 0 || m.Frames[0].Positions is not { } q0) continue;
                var recs = m.FaceVertices;
                bool valid = m.Faces.All(fc => Ok(fc.FaceVertex0, fc.V0) && Ok(fc.FaceVertex1, fc.V1) && Ok(fc.FaceVertex2, fc.V2));
                bool Ok(int r, int v) => (uint)r < (uint)recs.Length && recs[r].VertexIndex == v;
                if (!valid) continue;
                int del = m.Faces[0].V0;
                var before = VfxWriter.Write(f);
                VfxFile r;
                try { r = VfxVertexEdits.Delete(f, i, new HashSet<int> { del }); }
                catch (InvalidOperationException) { continue; }
                Assert.Equal(before, VfxWriter.Write(f)); // the edit is pure: undo restores identical bytes
                checkedMeshes++;
                var rm = (VfxMesh)r.Sections[i];
                var kept = Enumerable.Range(0, m.Faces.Length).Where(k => m.Faces[k] is var fc && fc.V0 != del && fc.V1 != del && fc.V2 != del).ToArray();
                int expected = kept.SelectMany(k => new[] { m.Faces[k].FaceVertex0, m.Faces[k].FaceVertex1, m.Faces[k].FaceVertex2 }).Distinct().Count();
                if (rm.FaceVertices.Length != expected) { fails.Add($"{name}#{i} ({m.Name}): records {recs.Length} -> {rm.FaceVertices.Length}, expected {expected}"); continue; }
                var removed = Enumerable.Range(0, m.Faces.Length).Except(kept).ToHashSet();
                var oldN = new Vector3[recs.Length]; var newN = new Vector3[rm.FaceVertices.Length];
                VfxSampler.ComputeNormals(m, VfxPositionCodec.Decode(q0), oldN);
                VfxSampler.ComputeNormals(rm, VfxPositionCodec.Decode(rm.Frames[0].Positions!), newN);
                for (int j = 0; j < kept.Length; j++)
                {
                    var of = m.Faces[kept[j]]; var nf = rm.Faces[j];
                    foreach (var (o, n) in new[] { (of.FaceVertex0, nf.FaceVertex0), (of.FaceVertex1, nf.FaceVertex1), (of.FaceVertex2, nf.FaceVertex2) })
                        if (!recs[o].AdjacentFaces.Any(removed.Contains) && Vector3.Distance(oldN[o], newN[n]) > 1e-3f)
                        { fails.Add($"{name}#{i} ({m.Name}): face {kept[j]} normal {oldN[o]} -> {newN[n]}"); break; }
                }
            }
        }
        Assert.True(fails.Count == 0, $"{fails.Count} meshes of {checkedMeshes}: " + string.Join("; ", fails.Take(10)));
    }
}
