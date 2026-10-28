using Cairn.Formats.Gltf;
using Cairn.Formats.Obj;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Lvl.Tests;

/// <summary>
/// Conversions of real levels and groups (bounded; each test passes trivially when its data is absent), and the
/// winding conventions the converters rely on, measured on real files.
/// </summary>
public class LvlConvertSampleTests(ITestOutputHelper output)
{
    private static readonly string[] Targets = ["glb", "obj", "v3m"];

    /// <summary>A few small stock levels (about 8 MB in the normal run).</summary>
    private static List<LvlSamples.Sample> SmallGameLevels() =>
        [.. LvlSamples.GameLevels(LvlSamples.CorpusEnabled ? 200L << 20 : 8L << 20)];

    [Fact]
    public void Level_faces_wind_the_way_their_stored_planes_face()
    {
        var samples = SmallGameLevels();
        if (samples.Count == 0) return;
        long agree = 0, disagree = 0;
        foreach (var s in samples.Take(4))
        {
            var level = RflLevelReader.Read(s.Load(), s.Name);
            foreach (var g in new[] { level.StaticGeometry }.Concat(level.Brushes.Select(b => b.Geometry)))
            {
                foreach (var f in g.Faces)
                {
                    if (f.Plane is not { } plane || f.Corners.Length < 3 || f.Corners.Any(c => (uint)c.Vertex >= (uint)g.Vertices.Length)) continue;
                    var n = Newell(f.Corners.Select(c => g.Vertices[c.Vertex]).ToArray());
                    if (n.LengthSquared() < 1e-8f) continue;
                    if (Vector3.Dot(Vector3.Normalize(n), plane.Normal) > 0.5f) agree++;
                    else disagree++;
                }
            }
        }
        output.WriteLine($"faces whose corner order agrees with the stored plane: {agree}, disagree: {disagree}");
        Assert.True(agree > 100 * Math.Max(1, disagree), $"{agree} agree, {disagree} disagree");
    }

    [Fact]
    public void Stock_mesh_triangles_wind_the_way_their_normals_face()
    {
        if (LocalPaths.Corpus is not { } corpus) return;
        var files = Directory.EnumerateFiles(corpus, "*.v3m").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        if (files.Count == 0) return;
        long agree = 0, disagree = 0;
        foreach (string path in files)
        {
            V3dFile mesh;
            try { mesh = V3dReader.ReadFile(path); }
            catch (Cairn.Formats.AssetFormatException) { continue; }
            foreach (var b in mesh.Submeshes.SelectMany(s => s.Lods).SelectMany(l => l.Batches))
            {
                foreach (var t in b.Triangles)
                {
                    if (t.A >= b.VertexCount || t.B >= b.VertexCount || t.C >= b.VertexCount) continue;
                    var cross = Vector3.Cross(b.Positions[t.B] - b.Positions[t.A], b.Positions[t.C] - b.Positions[t.A]);
                    var normal = b.Normals[t.A] + b.Normals[t.B] + b.Normals[t.C];
                    if (cross.LengthSquared() < 1e-12f || !float.IsFinite(normal.LengthSquared()) || normal.LengthSquared() < 1e-6f) continue;
                    if (Vector3.Dot(cross, normal) > 0) agree++;
                    else disagree++;
                }
            }
        }
        output.WriteLine($"stock triangles whose winding agrees with their normals: {agree}, disagree: {disagree}");
        Assert.True(agree > 20 * Math.Max(1, disagree), $"{agree} agree, {disagree} disagree");
    }

    /// <summary>Converts to every target in memory, reads each output back, and returns a one-line summary.</summary>
    private static string ConvertEverywhere(LvlConvertInput input, LvlConvertOptions baseOptions)
    {
        var parts = new List<string>();
        foreach (var target in new[] { LvlConvertTarget.Glb, LvlConvertTarget.Obj, LvlConvertTarget.V3m })
        {
            var c = LvlConverter.Convert(input, baseOptions with { Target = target });
            Assert.True(c.HasOutput, $"{input.Name} {target}: {c.Report.ToText()}");
            switch (target)
            {
                case LvlConvertTarget.Glb:
                {
                    var doc = GltfReader.Read(GltfWriter.ToGlb(c.Gltf!), "x.glb", null);
                    foreach (var prim in doc.Meshes.SelectMany(m => m.Primitives))
                    {
                        // Every index in range, every normal a unit vector.
                        int count = doc.Accessors[prim.Attributes["POSITION"]].Count;
                        Assert.All(GltfAccessorReader.ReadInts(doc, prim.Indices!.Value), i => Assert.InRange(i, 0, count - 1));
                        var n = GltfAccessorReader.ReadFloats(doc, prim.Attributes["NORMAL"]);
                        for (int i = 0; i < n.Length; i += 3) Assert.InRange(new Vector3(n[i], n[i + 1], n[i + 2]).Length(), 0.99f, 1.01f);
                    }
                    Assert.False(c.Report.HasErrors, c.Report.ToText());
                    break;
                }
                case LvlConvertTarget.Obj:
                {
                    var o = c.Obj!;
                    var back = ObjReader.Read(ObjWriter.Write(o.Obj, o.WriteOptions), "x.obj");
                    Assert.Equal(o.Obj.FaceCount, back.FaceCount);
                    Assert.False(c.Report.HasErrors, c.Report.ToText());
                    break;
                }
                default:
                {
                    var mesh = V3dReader.Read(V3dWriter.Write(c.Mesh!), "x.v3m");
                    var errors = MeshLinter.Analyze(mesh).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                    Assert.True(errors.Count == 0, $"{input.Name}: {string.Join("; ", errors.Take(3).Select(e => e.Message))}");
                    break;
                }
            }
            parts.Add($"{target} {c.Report.Stats.Faces} faces/{c.Report.Stats.Triangles} tris/{c.Report.Stats.Vertices} verts");
        }
        return string.Join(", ", parts);
    }

    [Fact]
    public void Stock_levels_convert_to_every_target()
    {
        var samples = SmallGameLevels();
        if (samples.Count == 0) return;
        foreach (var s in samples)
        {
            var level = RflLevelReader.Read(s.Load(), s.Name);
            var input = LvlConvertInput.FromLevel(level);
            output.WriteLine($"{s.Name} compiled: {ConvertEverywhere(input, new LvlConvertOptions())}");
            output.WriteLine($"{s.Name} brushes: {ConvertEverywhere(input, new LvlConvertOptions { Geometry = LvlGeometryChoice.Brushes })}");
        }
    }

    [Fact]
    public void A_stock_level_finds_and_writes_its_textures()
    {
        if (LocalPaths.GameDirectory is not { } dir) return;
        var sample = SmallGameLevels().FirstOrDefault();
        if (sample is null) return;
        var level = RflLevelReader.Read(sample.Load(), sample.Name);
        var options = new LvlConvertOptions
        {
            Target = LvlConvertTarget.Glb,
            TextureResolver = new Cairn.Assets.AssetResolver(new Cairn.Assets.AssetResolverOptions { GameDirectory = dir }),
        };
        var c = LvlConverter.Convert(LvlConvertInput.FromLevel(level), options);
        output.WriteLine(c.Report.ToText());
        Assert.True(c.Report.Stats.Textures > 0);
        Assert.True(c.Gltf!.Images.Count == c.Report.Stats.Textures);
        // Materials are deduplicated: never more than (textures used) x (render states).
        int textures = c.Gltf.Materials.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.True(c.Gltf.Materials.Count <= textures * 4);
    }

    [Fact]
    public void Redux_groups_and_alpine_levels_convert_with_their_objects()
    {
        var groups = LvlSamples.Files(LocalPaths.ReduxResearchData, "*.rfg", recursive: true).ToList();
        foreach (var s in LvlSamples.Spread(groups, LvlSamples.CorpusEnabled ? groups.Count : 6))
        {
            RfGroupFile file;
            try { file = RfgReader.Read(s.Load(), s.Name); }
            catch (Cairn.Formats.AssetFormatException) { continue; }
            if (!file.AllBrushes.Any(b => b.Geometry.Faces.Length > 0)) continue;
            var input = LvlConvertInput.FromGroupFile(file);
            output.WriteLine($"{s.Name}: {ConvertEverywhere(input, new LvlConvertOptions())}");
            var c = LvlConverter.Convert(input, new LvlConvertOptions { Target = LvlConvertTarget.Glb });
            int keyframes = file.Groups.Sum(g => g.Moving?.Keyframes.Length ?? 0);
            Assert.Equal(file.Groups.Sum(g => g.Objects.Count) + keyframes, c.Report.Stats.Objects);
        }

        var levels = LvlSamples.Files(LocalPaths.ReduxResearchData, "*.rfl", recursive: true, f => !LvlSamples.InRf2Folder(f)).ToList();
        int alpine = 0;
        foreach (var s in levels)
        {
            if (alpine >= (LvlSamples.CorpusEnabled ? int.MaxValue : 2)) break;
            byte[] data = s.Load();
            if (data.Length > 12 << 20 || LvlSamples.IsRf2Level(data) || BitConverter.ToInt32(data, 4) < 300) continue;
            var level = RflLevelReader.Read(data, s.Name);
            output.WriteLine($"{s.Name}: {ConvertEverywhere(LvlConvertInput.FromLevel(level), new LvlConvertOptions())}");
            alpine++;
        }
    }

    [Fact]
    public void Compiled_movers_sit_where_their_editor_brushes_do()
    {
        // The converters place mover geometry with the mover's own transform, as they do brushes; on real levels that
        // must land on the editor brush of the same uid.
        int compared = 0, matched = 0;
        foreach (var s in SmallGameLevels())
        {
            var level = RflLevelReader.Read(s.Load(), s.Name);
            foreach (var mover in level.Movers)
            {
                if (level.FindBrush(mover.Uid) is not { } brush || mover.Geometry.Vertices.Length == 0 || brush.Geometry.Vertices.Length == 0) continue;
                var (minA, maxA) = Bounds(mover.Geometry.Vertices.Select(mover.ToWorld));
                var (minB, maxB) = Bounds(brush.Geometry.Vertices.Select(brush.ToWorld));
                compared++;
                if (Vector3.Distance(minA, minB) < 0.05f && Vector3.Distance(maxA, maxB) < 0.05f) matched++;
            }
        }
        output.WriteLine($"movers compared {compared}, bounds matching their brush {matched}");
        if (compared == 0) return;
        Assert.True(matched >= compared * 9 / 10, $"{matched} of {compared}");
    }

    /// <summary>Set to a folder to write converted samples there for checking by eye (off by default).</summary>
    public const string OutputVariable = "CAIRN_LVL_CONVERT_OUT";

    [Fact]
    public void Writes_converted_samples_for_checking_by_eye_when_asked()
    {
        if (Environment.GetEnvironmentVariable(OutputVariable) is not { Length: > 0 } folder) return;
        var sample = SmallGameLevels().FirstOrDefault();
        if (sample is null) return;
        var level = RflLevelReader.Read(sample.Load(), sample.Name);
        var resolver = LocalPaths.GameDirectory is { } dir ? new Cairn.Assets.AssetResolver(new Cairn.Assets.AssetResolverOptions { GameDirectory = dir }) : null;
        string stem = Path.GetFileNameWithoutExtension(level.Name);
        var input = LvlConvertInput.FromLevel(level);
        foreach (var (target, geometry, suffix) in new[]
        {
            (LvlConvertTarget.Gltf, LvlGeometryChoice.Compiled, "compiled"), (LvlConvertTarget.Glb, LvlGeometryChoice.Brushes, "brushes"),
            (LvlConvertTarget.Obj, LvlGeometryChoice.Compiled, "compiled"), (LvlConvertTarget.V3m, LvlGeometryChoice.Compiled, "compiled"),
            (LvlConvertTarget.V3m, LvlGeometryChoice.Brushes, "brushes"),
        })
        {
            string path = Path.Combine(folder, $"{stem}_{suffix}{LvlConvertOptions.ExtensionOf(target)}");
            var result = LvlConverter.ConvertToFile(input, path, new LvlConvertOptions { Target = target, Geometry = geometry, TextureResolver = resolver });
            output.WriteLine($"{path}: {result.Files.Length} files");
            File.WriteAllText(path + ".report.txt", result.Report.ToText());
            Assert.True(result.Succeeded, result.Report.ToText());
        }
    }

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    private static Vector3 Newell(Vector3[] p)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return n;
    }
}
