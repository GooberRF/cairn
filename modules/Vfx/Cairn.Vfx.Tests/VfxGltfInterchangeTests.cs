using System.Numerics;
using Cairn.Formats.Gltf;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Interchange;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

public sealed class VfxGltfInterchangeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("cairn-vfx-gltf-").FullName;

    public void Dispose() { try { Directory.Delete(_temp, true); } catch (IOException) { } }

    private static IEnumerable<(string Path, byte[] Data, VfxFile File)> Stock0x40006()
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) yield break;
        foreach (var path in Directory.GetFiles(dir, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            byte[] data = File.ReadAllBytes(path);
            var vfx = VfxReader.Read(data, Path.GetFileName(path));
            if (vfx.Version == VfxVersion.Current) yield return (path, data, vfx);
        }
    }

    [Fact]
    public void StockFilesRoundTripThroughGltfAndGlbByteIdentically()
    {
        var failures = new List<string>();
        int count = 0;
        foreach (var (path, data, vfx) in Stock0x40006())
        {
            count++;
            string stem = Path.GetFileNameWithoutExtension(path);
            foreach (bool glb in new[] { false, true })
            {
                string target = Path.Combine(_temp, stem + (glb ? ".glb" : ".gltf"));
                VfxGltfExport.Save(vfx, target, glb);
                var doc = GltfReader.ReadFile(target); // strict reader
                var result = VfxGltfImport.Import(doc);
                byte[] again = VfxWriter.Write(result.File);
                if (!again.AsSpan().SequenceEqual(data))
                    failures.Add($"{stem} ({(glb ? "glb" : "gltf")}): {again.Length}/{data.Length} bytes; {string.Join("; ", result.Messages)}");
            }
        }
        output.WriteLine($"{count} stock 0x40006 files round-tripped (.gltf+.bin and GLB), {failures.Count} failures");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void OlderVersionsExportAndImportAsCurrent()
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) return;
        int count = 0;
        foreach (var path in Directory.GetFiles(dir, "*.vfx"))
        {
            var vfx = VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            if (vfx.Version == VfxVersion.Current) continue;
            var doc = VfxGltfExport.Export(vfx);
            Assert.Equal(vfx.Version, doc.Nodes[0].Extras!["rf_version"]!.GetValue<int>());
            var result = VfxGltfImport.Import(GltfReader.Read(GltfWriter.ToGlb(doc), "x.glb", _ => null));
            Assert.Equal(VfxVersion.Current, result.File.Version);
            Assert.Equal(vfx.Sections.OfType<VfxMesh>().Count(m => m.Faces.Length > 0), result.File.Sections.OfType<VfxMesh>().Count());
            count++;
        }
        output.WriteLine($"{count} older-version files exported and re-imported as 0x40006 meshes");
    }

    [Fact]
    public void EditedVertexTriggersRebuildWithConsistentCounts()
    {
        var (_, _, vfx) = Stock0x40006().FirstOrDefault(s => s.File.Sections.OfType<VfxMesh>().Any(m => m.Faces.Length > 4 && !m.IsMorph));
        if (vfx is null) return;
        var mesh = vfx.Sections.OfType<VfxMesh>().First(m => m.Faces.Length > 4 && !m.IsMorph);
        var doc = VfxGltfExport.Export(vfx);
        var node = doc.Nodes.First(n => n.Name == mesh.Name && n.Mesh is not null);
        var acc = doc.Accessors[doc.Meshes[node.Mesh!.Value].Primitives[0].Attributes["POSITION"]];
        int offset = doc.BufferViews[acc.BufferView!.Value].ByteOffset + acc.ByteOffset;
        var bytes = doc.Buffers[0].Data!;
        BitConverter.TryWriteBytes(bytes.AsSpan(offset), BitConverter.ToSingle(bytes, offset) + 0.5f);
        string target = Path.Combine(_temp, "edited.gltf");
        GltfWriter.WriteGltf(doc, target);
        var result = VfxGltfImport.Import(target);
        Assert.Contains(result.Messages, m => m.Contains("rebuilt", StringComparison.Ordinal));
        var rebuilt = result.File.Sections.OfType<VfxMesh>().First(m => m.Name == mesh.Name);
        Assert.Equal(mesh.Faces.Length, rebuilt.Faces.Length);
        Assert.Equal(mesh.Frames.Length, rebuilt.Frames.Length);
        Assert.All(rebuilt.Faces, f => Assert.True(f.V0 < rebuilt.NumVertices && f.FaceVertex2 < rebuilt.FaceVertices.Length));
        var reread = VfxReader.Read(VfxWriter.Write(result.File), "rebuilt.vfx");
        Assert.Equal(vfx.Sections.Length, reread.Sections.Length);
    }

    private static GltfDocument PlainTriangle()
    {
        var doc = new GltfDocument();
        var b = new GltfBufferBuilder(doc);
        var prim = new GltfPrimitive { Indices = b.AddIndices([0, 1, 2]), Material = 0 };
        prim.Attributes["POSITION"] = b.AddVector3([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], 34962, minMax: true);
        prim.Attributes["NORMAL"] = b.AddVector3([Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ]);
        prim.Attributes["TEXCOORD_0"] = b.AddVector2([new(0, 0), new(1, 0), new(0, 1)]);
        doc.Meshes.Add(new GltfMesh { Primitives = { prim } });
        doc.Images.Add(new GltfImage { Uri = "flame.png" });
        doc.Textures.Add(new GltfTexture { Source = 0 });
        doc.Materials.Add(new GltfMaterial { Name = "Flame", PbrMetallicRoughness = new() { BaseColorTexture = new() { Index = 0 } } });
        doc.Nodes.Add(new GltfNode { Name = "Tri", Mesh = 0 });
        doc.Scenes.Add(new GltfScene { Nodes = { 0 } });
        doc.Scene = 0;
        var anim = new GltfAnimation();
        anim.Samplers.Add(new GltfAnimationSampler { Input = b.AddScalars([0f, 1f]), Output = b.AddVector3([Vector3.Zero, new(2, 0, 0)], null), Interpolation = "LINEAR" });
        anim.Channels.Add(new GltfAnimationChannel { Sampler = 0, Target = { Node = 0, Path = "translation" } });
        doc.Animations.Add(anim);
        b.Finish("plain.bin");
        return doc;
    }

    [Fact]
    public void PlainGltfImportsAsMeshWithFrames()
    {
        var result = VfxGltfImport.Import(PlainTriangle());
        var mesh = Assert.Single(result.File.Sections.OfType<VfxMesh>());
        var mat = Assert.Single(result.File.Sections.OfType<VfxMaterial>());
        Assert.Equal("flame.tga", mat.Texture0!.Name);
        Assert.Contains(result.Messages, m => m.Contains("renamed", StringComparison.Ordinal));
        Assert.Equal(16, mesh.Frames.Length);
        Assert.Equal(0, Assert.Single(mesh.Faces).SmoothingGroup);
        Assert.Equal(-2f, mesh.Frames[^1].Transform!.Translation.X, 4); // mirrored X
        Assert.Equal(-16f / 15f, mesh.Frames[8].Transform!.Translation.X, 4);
        var reread = VfxReader.Read(VfxWriter.Write(result.File), "plain.vfx");
        Assert.Equal(VfxVersion.Current, reread.Version);
    }

    [Fact]
    public void ProbeDistinguishesVfxGltfFromPlain()
    {
        string plain = Path.Combine(_temp, "plain.gltf");
        GltfWriter.WriteGltf(PlainTriangle(), plain);
        Assert.Equal(0.25f, VfxGltfImport.Probe(plain));
        var vfx = new VfxFile(VfxVersion.Current, 0, 0, null, 0, [new VfxDummy("d", "Scene Root", 0, Vector3.One, Quaternion.Identity, [])]);
        string path = Path.Combine(_temp, "dummy.gltf");
        VfxGltfExport.Save(vfx, path);
        Assert.Equal(1f, VfxGltfImport.Probe(path));
        Assert.Equal(0f, VfxGltfImport.Probe(Path.Combine(_temp, "missing.gltf")));
    }

    [Fact]
    public void ReduxWrittenGltfFilesImport()
    {
        if (LocalPaths.ReduxResearch is not { } dir || !Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.gltf", SearchOption.AllDirectories).Where(f => VfxGltfImport.Probe(f) == 1f).ToList();
        foreach (var f in files) VfxWriter.Write(VfxGltfImport.Import(f).File);
        output.WriteLine($"{files.Count} REDUX VFX glTF files imported");
    }
}
