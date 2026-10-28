using Cairn.Formats;
using Cairn.Formats.Gltf;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Cairn.Lvl.Writing;
using Cairn.Rfa.Formats.V3d;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Lvl.Tests;

/// <summary>
/// Real levels, group files, glTF/OBJ outputs and meshes converted to .rfg (each source skipped when absent). Set
/// <c>CAIRN_LVL_RFG_OUT</c> to a folder to also write the sample outputs the author checks in Alpine RED.
/// </summary>
public sealed class ToRfgSampleTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Folder for the RED sample outputs (opt-in).</summary>
    public const string OutputVariable = "CAIRN_LVL_RFG_OUT";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cairn-lvl-rfg-samples-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly LvlConvertOptions Rfg = new() { Target = LvlConvertTarget.Rfg };

    /// <summary>Writes a conversion and reads the group file back: the output must read with no problem and rewrite byte for byte.</summary>
    private RfGroupFile WriteAndRead(LvlConversion conversion, string name)
    {
        Assert.True(conversion.HasOutput, $"{name}: {conversion.Report.ToText()}");
        Assert.False(conversion.Report.HasErrors, $"{name}: {conversion.Report.ToText()}");
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, Path.GetFileNameWithoutExtension(name) + ".rfg");
        conversion.WriteTo(path);
        var bytes = File.ReadAllBytes(path);
        var file = RfgReader.Read(bytes, name);
        Assert.Empty(file.Problems);
        Assert.Equal(RfGroupFallbacks.None, file.Fallbacks);
        Assert.Equal(bytes, RfgWriter.Write(file).Bytes);
        return file;
    }

    private static RfLevel? GameLevel(string name)
    {
        var sample = LvlSamples.GameLevels().FirstOrDefault(s => s.Name.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
        return sample is null ? null : RflLevelReader.Read(sample.Load(), name);
    }

    [Fact]
    public void Stock_levels_keep_every_brush_group_and_object()
    {
        var samples = LvlSamples.Spread([.. LvlSamples.GameLevels()], LvlSamples.CorpusEnabled ? 100 : 6).ToList();
        foreach (var s in samples)
        {
            var level = RflLevelReader.Read(s.Load(), s.Name);
            var conversion = LvlConverter.Convert(LvlConvertInput.FromLevel(level), Rfg);
            var file = WriteAndRead(conversion, s.Name);

            var brushes = file.AllBrushes.ToDictionary(b => b.Uid);
            Assert.Equal(level.Brushes.Length, brushes.Count);
            // A uid two brushes share is given a new value for the second one, so only unique uids can be looked up.
            var unique = level.Brushes.GroupBy(b => b.Uid).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
            if (unique.Count < level.Brushes.Length) output.WriteLine($"{s.Name}: {level.Brushes.Length - unique.Count} brushes share a uid");
            foreach (var b in level.Brushes.Where(b => unique.Contains(b.Uid)))
            {
                var written = brushes[b.Uid];
                Assert.Equal(b.Position, written.Position);
                Assert.Equal(b.Orientation, written.Orientation);
                Assert.Equal(b.Rf1SolidFlags, written.SolidFlags);
                Assert.Equal(b.Geometry.Faces.Length, written.Geometry.Faces.Length);
                Assert.True(b.Geometry.Vertices.SequenceEqual(written.Geometry.Vertices), $"brush {b.Uid} vertices");
            }
            int moving = (level.Groups.Length > 0 ? level.Groups : level.MovingGroups).Count(g => g.IsMoving);
            Assert.Equal(moving, file.Groups.Count(g => g.IsMoving));
            Assert.Equal(level.Objects.Count, file.Groups.Sum(g => g.Objects.Count));
            output.WriteLine($"{s.Name}: {file.Groups.Length} groups ({moving} moving), {brushes.Count} brushes, {file.Groups.Sum(g => g.Objects.Count)} objects");
        }
    }

    [Fact]
    public void A_stock_level_goes_through_gltf_and_obj_and_back()
    {
        if (GameLevel("L1S1.rfl") is not { } level) return;
        var direct = LvlConverter.Convert(LvlConvertInput.FromLevel(level), Rfg).Output as RfGroupFile;
        Assert.NotNull(direct);
        var expected = direct.AllBrushes.ToDictionary(b => b.Uid);

        Directory.CreateDirectory(_folder);
        var all = new LvlConvertOptions { Geometry = LvlGeometryChoice.Brushes, ExportTextures = false, ExcludedFaceClasses = RfFaceClass.Normal };
        string gltf = Path.Combine(_folder, "L1S1.gltf");
        Assert.True(LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), gltf, all with { Target = LvlConvertTarget.Gltf }).Succeeded);
        var viaGltf = WriteAndRead(LvlConverter.FileToRfg(gltf), "L1S1_gltf.rfg");
        string obj = Path.Combine(_folder, "L1S1.obj");
        Assert.True(LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), obj, all with { Target = LvlConvertTarget.Obj }).Succeeded);
        var viaObj = WriteAndRead(LvlConverter.FileToRfg(obj), "L1S1_obj.rfg");

        foreach (var (via, faceIds, tolerance) in new[] { (viaGltf, true, 1e-5f), (viaObj, false, 2e-3f) })
        {
            var brushes = via.AllBrushes.ToDictionary(b => b.Uid);
            // Brushes without a face the converters can write (no readable face) are not in glTF or OBJ.
            Assert.Equal(expected.Values.Count(b => Readable(b).Geometry.Faces.Length > 0), brushes.Count);
            foreach (var (uid, b) in brushes)
            {
                var e = expected[uid];
                Assert.Equal(e.SolidFlags, b.SolidFlags);
                Assert.True(Vector3.Distance(e.Position, b.Position) <= tolerance * Math.Max(1, e.Position.Length()), $"brush {uid} position");
                // A face whose first corner is not an ear (collinear corners, concave faces) comes back from its
                // triangles starting at another corner: the same polygon, turned.
                ToRfgTests.AssertSameFaces(Readable(e), b, tolerance * Math.Max(1, e.Position.Length() + 10), faceIds, startCorner: false);
            }
            // glTF keeps the moving data; OBJ has no keyframes.
            int moving = direct.Groups.Count(g => g.IsMoving);
            Assert.Equal(faceIds ? moving : 0, via.Groups.Count(g => g.IsMoving));
        }
    }

    /// <summary>A brush with only the faces glTF and OBJ can hold (three corners or more, every vertex present).</summary>
    private static RfBrush Readable(RfBrush b) => b with
    {
        Geometry = b.Geometry with { Faces = [.. b.Geometry.Faces.Where(f => f.Corners.Length >= 3 && f.Corners.All(c => (uint)c.Vertex < (uint)b.Geometry.Vertices.Length) && HasArea(b.Geometry, f))] },
    };

    /// <summary>The builder's rule: faces with no area or a repeated corner are dropped.</summary>
    private static bool HasArea(RfGeometry g, RfFace f) =>
        f.Corners.Select(c => g.Vertices[c.Vertex]).Distinct().Count() == f.Corners.Length
        && RfgBrushBuilder.Newell([.. f.Corners.Select(c => g.Vertices[c.Vertex])]).LengthSquared() > 1e-18f;

    [Fact]
    public void Alpine_levels_keep_their_alpine_objects()
    {
        var levels = LvlSamples.Files(LocalPaths.ReduxResearchData, "*.rfl", recursive: true, f => !LvlSamples.InRf2Folder(f))
            .Select(s => (s, Data: s.Load())).Where(x => BitConverter.ToInt32(x.Data, 4) >= 304).Take(LvlSamples.CorpusEnabled ? 100 : 2).ToList();
        foreach (var (s, data) in levels)
        {
            var level = RflLevelReader.Read(data, s.Name);
            var file = WriteAndRead(LvlConverter.Convert(LvlConvertInput.FromLevel(level), Rfg), s.Name);
            Assert.Equal(level.Objects.AlpineMeshes.Length, file.Groups[0].Objects.AlpineMeshes.Length);
            Assert.Equal(level.Objects.Coronas.Length, file.Groups[0].Objects.Coronas.Length);
            // Flagged brushes keep their flags, except in moving groups (Alpine reads them for the first group only).
            var movingBrushes = file.Groups.Where(g => g.IsMoving).SelectMany(g => g.Brushes).Select(b => b.Uid).ToHashSet();
            Assert.Equal(level.Brushes.Count(b => b.Alpine is { Flags: not RfAlpineBrushFlags.None } && !movingBrushes.Contains(b.Uid)),
                file.Groups[0].Brushes.Count(b => b.Alpine is { Flags: not RfAlpineBrushFlags.None }));
            output.WriteLine($"{s.Name}: {level.Objects.AlpineMeshes.Length} meshes, {level.Objects.Coronas.Length} coronas");
        }
    }

    [Fact]
    public void Redux_gltf_and_obj_outputs_convert()
    {
        if (LocalPaths.ReduxResearchData is not { } root || !Directory.Exists(root)) return;
        foreach (string name in new[] { "io-utensils.gltf", "deb/turret_debris01.gltf", "io-utensils.obj", "rf2_rfls/dmc13.obj" })
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            var conversion = LvlConverter.FileToRfg(path);
            var file = WriteAndRead(conversion, Path.GetFileName(path));
            Assert.NotEmpty(file.AllBrushes);
            output.WriteLine($"{name}: {file.AllBrushes.Count()} brushes, {file.AllBrushes.Count(b => b.IsDetail)} detail");
            foreach (var issue in conversion.Report.Issues.Where(i => i.Severity > LvlIssueSeverity.Info)) output.WriteLine("  " + issue);
            if (name.EndsWith("dmc13.obj", StringComparison.Ordinal))
                Assert.Contains(conversion.Report.WithCode(LvlConvertCodes.ObjMetadata), i => i.Message.Contains("REDUX"));
        }
    }

    [Fact]
    public void Game_meshes_convert_one_brush_per_submesh()
    {
        if (LocalPaths.GameDirectory is not { } dir) return;
        string pack = Path.Combine(dir, "meshes.vpp");
        if (!File.Exists(pack)) return;
        var meshes = LvlSamples.PackEntries(pack, ".v3m").Take(4).Concat(LvlSamples.PackEntries(pack, ".v3c").Take(1)).ToList();
        foreach (var (sample, _) in meshes)
        {
            V3dFile mesh;
            try
            {
                mesh = V3dReader.Read(sample.Load(), sample.Name);
            }
            catch (AssetFormatException)
            {
                continue;
            }
            var file = WriteAndRead(LvlConverter.MeshToRfg(mesh, sample.Name), sample.Name);
            Assert.Equal(mesh.Submeshes.Count(s => s.Lods.Length > 0 && s.Lods[0].Batches.Length > 0), file.AllBrushes.Count());
        }
    }

    [Fact]
    public void Editor_groups_upgrade_to_the_current_version()
    {
        if (LocalPaths.GameDirectory is not { } dir) return;
        var groups = LvlSamples.Spread([.. LvlSamples.Files(Path.Combine(dir, "red", "groups"), "*.rfg", recursive: true)], 12);
        foreach (var s in groups)
        {
            RfGroupFile source;
            try
            {
                source = RfgReader.Read(s.Load(), s.Name);
            }
            catch (AssetFormatException)
            {
                continue;
            }
            // Some editor groups are empty; converting them reports that there is nothing to convert.
            if (!source.AllBrushes.Any() && source.Groups.All(g => g.Objects.Count == 0)) continue;
            var file = WriteAndRead(LvlConverter.Convert(LvlConvertInput.FromGroupFile(source), Rfg), s.Name);
            Assert.Equal(source.AllBrushes.Count(), file.AllBrushes.Count());
            Assert.Equal(source.Groups.Length, file.Groups.Length);
        }
    }

    // ── Samples for the author to try in Alpine RED ─────────────────────────

    [Fact]
    public void Red_samples_are_written_when_asked()
    {
        if (Environment.GetEnvironmentVariable(OutputVariable) is not { Length: > 0 } outFolder) return;
        Directory.CreateDirectory(outFolder);
        var written = new List<string>();
        void Save(LvlConversion conversion, string name)
        {
            string path = Path.Combine(outFolder, name);
            Assert.True(conversion.HasOutput, $"{name}: {conversion.Report.ToText()}");
            conversion.WriteTo(path);
            File.WriteAllText(Path.ChangeExtension(path, ".report.txt"), conversion.Report.ToText() + Environment.NewLine);
            // Every sample must read back cleanly before the author spends time on it.
            var file = RfgReader.ReadFile(path);
            Assert.Empty(file.Problems);
            written.Add($"{name}: {file.Groups.Length} groups, {file.AllBrushes.Count()} brushes, {file.Groups.Sum(g => g.Objects.Count)} objects");
        }
        string work = Path.Combine(outFolder, "intermediate");
        Directory.CreateDirectory(work);

        Save(LvlConverter.Convert(LvlConvertInput.FromLevel(ToRfgTests.SampleLevel()), Rfg), "01_synthetic_door_and_breakable.rfg");
        if (GameLevel("L1S1.rfl") is { } l1s1)
        {
            Save(LvlConverter.Convert(LvlConvertInput.FromLevel(l1s1), Rfg), "02_L1S1_brushes.rfg");
            Save(LvlConverter.Convert(LvlConvertInput.FromLevel(l1s1), Rfg with { Geometry = LvlGeometryChoice.Compiled }), "03_L1S1_compiled.rfg");
            string gltf = Path.Combine(work, "L1S1.gltf");
            LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(l1s1), gltf, new LvlConvertOptions { Geometry = LvlGeometryChoice.Brushes, ExportTextures = false, ExcludedFaceClasses = RfFaceClass.Normal });
            Save(LvlConverter.FileToRfg(gltf), "04_L1S1_via_gltf.rfg");
            string obj = Path.Combine(work, "L1S1.obj");
            LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(l1s1), obj, new LvlConvertOptions { Target = LvlConvertTarget.Obj, Geometry = LvlGeometryChoice.Brushes, ExportTextures = false, ExcludedFaceClasses = RfFaceClass.Normal });
            Save(LvlConverter.FileToRfg(obj), "05_L1S1_via_obj.rfg");
        }
        if (GameLevel("dm01.rfl") is { } dm01) Save(LvlConverter.Convert(LvlConvertInput.FromLevel(dm01), Rfg), "06_dm01_brushes.rfg");
        var alpine = LvlSamples.Files(LocalPaths.ReduxResearchData, "*.rfl", recursive: true, f => !LvlSamples.InRf2Folder(f))
            .Select(s => (s, Data: s.Load())).FirstOrDefault(x => BitConverter.ToInt32(x.Data, 4) >= 304 && RflLevelReader.Read(x.Data, x.s.Name).Objects.AlpineMeshes.Length > 0);
        if (alpine.Data is not null)
            Save(LvlConverter.Convert(LvlConvertInput.FromLevel(RflLevelReader.Read(alpine.Data, alpine.s.Name)), Rfg), $"07_alpine_{Path.GetFileNameWithoutExtension(alpine.s.Name)}.rfg");

        // Meshes from other tools: a closed cube and an open quad from a hand-written OBJ, and a stock mesh.
        string cube = Path.Combine(work, "cube_and_floor.obj");
        File.WriteAllText(cube, "o Cube\nv -1 0 -1\nv 1 0 -1\nv 1 2 -1\nv -1 2 -1\nv -1 0 1\nv 1 0 1\nv 1 2 1\nv -1 2 1\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\n"
            + "usemtl rck_wall01.tga\nf 1/1 4/4 3/3 2/2\nf 5/1 6/2 7/3 8/4\nf 1/1 2/2 6/3 5/4\nf 4/1 8/4 7/3 3/2\nf 1/1 5/2 8/3 4/4\nf 2/1 3/2 7/3 6/4\n"
            + "o Floor\nv -4 -0.5 -4\nv 4 -0.5 -4\nv 4 -0.5 4\nv -4 -0.5 4\nusemtl rck_floor01.tga\nf 9/1 12/4 11/3 10/2\n");
        Save(LvlConverter.FileToRfg(cube), "08_obj_cube_solid_and_floor_detail.rfg");
        if (LocalPaths.ReduxResearchData is { } redux)
        {
            if (File.Exists(Path.Combine(redux, "rf2_rfls", "dmc13.obj"))) Save(LvlConverter.FileToRfg(Path.Combine(redux, "rf2_rfls", "dmc13.obj")), "09_redux_obj_dmc13.rfg");
            if (File.Exists(Path.Combine(redux, "io-utensils.gltf"))) Save(LvlConverter.FileToRfg(Path.Combine(redux, "io-utensils.gltf")), "10_redux_gltf_io_utensils.rfg");
        }
        if (LocalPaths.GameDirectory is { } dir && File.Exists(Path.Combine(dir, "meshes.vpp")))
        {
            var mesh = LvlSamples.PackEntries(Path.Combine(dir, "meshes.vpp"), ".v3m").FirstOrDefault(e => e.Sample.Name.Contains("crate", StringComparison.OrdinalIgnoreCase) && !e.Sample.Name.Contains("deb", StringComparison.OrdinalIgnoreCase));
            if (mesh.Sample is not null)
                Save(LvlConverter.MeshToRfg(V3dReader.Read(mesh.Sample.Load(), mesh.Sample.Name), mesh.Sample.Name), $"11_mesh_{Path.GetFileNameWithoutExtension(mesh.Sample.Name)}.rfg");
            var old = LvlSamples.Files(Path.Combine(dir, "red", "groups", "volition"), "*.rfg", recursive: false)
                .FirstOrDefault(s => { var d = s.Load(); return BitConverter.ToInt32(d, 4) < 0xB4 && RfgReader.Read(d, s.Name).Groups.Any(g => g.IsMoving); });
            if (old is not null)
                Save(LvlConverter.Convert(LvlConvertInput.FromGroupFile(RfgReader.Read(old.Load(), old.Name)), Rfg), $"12_upgraded_{Path.GetFileNameWithoutExtension(old.Name)}.rfg");
        }
        var rf2 = LvlSamples.Files(Path.Combine(LocalPaths.ReduxResearchData ?? "", "rf2_rfls"), "*.rfl", recursive: false).FirstOrDefault();
        if (rf2 is not null)
        {
            var level = RflLevelReader.Read(rf2.Load(), rf2.Name);
            if (level.IsRf2) Save(LvlConverter.Convert(LvlConvertInput.FromLevel(level), Rfg), $"13_rf2_{Path.GetFileNameWithoutExtension(rf2.Name)}.rfg");
        }
        File.WriteAllLines(Path.Combine(outFolder, "samples.txt"), written);
        foreach (string line in written) output.WriteLine(line);
    }
}
