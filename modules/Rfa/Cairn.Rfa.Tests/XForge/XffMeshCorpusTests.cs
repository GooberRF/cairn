using System.Numerics;
using Cairn.Formats.XForge;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests.XForge;

/// <summary>
/// Every model and character of Red Faction for N-Gage converted from the game's <c>redfaction.cfl</c> (when
/// <c>ngageDirectory</c> is set in research/local-paths.json; skipped otherwise): each passes the mesh and clip linters or
/// has a stated reason, <c>enemy_ug</c> has its six animated parts as bones and a 533 ms clip, and the exploding barrel's
/// faces point outward after the winding swap.
/// </summary>
public class XffMeshCorpusTests(ITestOutputHelper output)
{
    /// <summary>Scenes with nothing to draw: the convert gives an empty mesh, which the linter flags.</summary>
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["camera.xff"] = "a camera rig: no geometry",
        ["firstcamera.xff"] = "the first-person camera: no geometry",
        ["10b_button01.xff"] = "a button marker only: no geometry",
        ["fsnrckt.xff"] = "a projectile of a particle system and a sprite: no geometry",
    };

    private sealed class Archive : IDisposable
    {
        private readonly FileStream _stream;
        public Archive(string path)
        {
            Cfl = CflArchive.Open(path);
            _stream = File.OpenRead(path);
        }
        public CflArchive Cfl { get; }
        public byte[]? Read(string name) => Cfl.Find(name) is { } e ? Cfl.ReadEntry(_stream, e) : null;
        public void Dispose() => _stream.Dispose();
    }

    private Archive? Open()
    {
        string? path = LocalPaths.NgageDirectory is { } dir ? Path.Combine(dir, "redfaction.cfl") : null;
        if (path is null || !File.Exists(path))
        {
            output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.NgageDirectoryVariable, "ngageDirectory"));
            return null;
        }
        return new Archive(path);
    }

    [Fact]
    public void EveryModelAndCharacter_Converts()
    {
        using var archive = Open();
        if (archive is null) return;
        var failures = new List<string>();
        int models = 0, characters = 0, levels = 0;
        foreach (var entry in archive.Cfl.Entries.Where(e => XffFile.HasExtension(e.Name)).DistinctBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes = archive.Read(entry.Name)!;
            var kind = XffScene.Read(bytes, entry.Name).Kind;
            if (kind == XffKind.Level)
            {
                levels++;
                Assert.False(LegacyMeshSupport.TryRead(bytes, entry.Name, out _, out _));
                continue;
            }
            LegacyMeshConversion conversion;
            try { conversion = LegacyMeshSupport.Convert(bytes, entry.Name, archive.Read); }
            catch (AssetFormatException ex) { failures.Add($"{entry.Name}: {ex.Message}"); continue; }
            if (kind == XffKind.Character) characters++; else models++;
            Assert.Equal(kind == XffKind.Character ? V3dKind.Character : V3dKind.StaticMesh, conversion.Mesh.Kind);
            // Red Faction's tools take .tga textures: no material names a .pcx, and none is written
            Assert.DoesNotContain(conversion.Mesh.Submeshes.SelectMany(s => s.Materials), m => XffTextureFix.IsPcx(m.DiffuseMap.Text));
            Assert.DoesNotContain(conversion.Textures, t => XffTextureFix.IsPcx(t.Name));
            var problems = MeshLinter.Analyze(conversion.Mesh, new MeshLintContext { FileName = conversion.OutputName })
                .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).Select(d => $"{d.Code} {d.Message}").ToList();
            if (conversion.Clip is { } clip)
            {
                var read = RfaReader.Read(clip.Bytes, clip.OutputName);
                Assert.Equal(conversion.Mesh.Bones.Length, read.BoneCount);
                problems.AddRange(ClipLinter.AnalyzeStructure(read).Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).Select(d => $"{d.Code} {d.Message}"));
            }
            else Assert.NotEqual(XffKind.Character, kind);
            // the round trip: the written mesh reads back
            Assert.Equal(conversion.Mesh.Submeshes.Count(), V3dReader.Read(conversion.Bytes, conversion.OutputName).Submeshes.Count());
            if (problems.Count == 0) continue;
            if (Reasons.TryGetValue(entry.Name, out var reason)) { output.WriteLine($"{entry.Name}: {reason} ({string.Join("; ", problems)})"); continue; }
            failures.Add($"{entry.Name}: {string.Join("; ", problems)}");
        }
        output.WriteLine($"{models} models, {characters} characters converted; {levels} levels left to the Levels module");
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
        Assert.Equal(25, characters);
        Assert.Equal(35, levels);
    }

    [Fact]
    public void EnemyUg_HasItsPartsAsBones_AndA533msClip()
    {
        using var archive = Open();
        if (archive is null) return;
        var conversion = LegacyMeshSupport.Convert(archive.Read("enemy_ug.xff")!, "enemy_ug.xff", archive.Read);
        Assert.Equal(["cfo_body", "Object01", "Object07", "Object09", "Object08", "Object10"], conversion.Mesh.Bones.Select(b => b.Name.Text));
        Assert.Equal([-1, 0, 0, 2, 0, 4], conversion.Mesh.Bones.Select(b => b.ParentIndex));
        var clip = RfaReader.Read(conversion.Clip!.Bytes, conversion.Clip.OutputName);
        Assert.Equal("enemy_ug_walk.rfa", conversion.Clip.OutputName);
        Assert.Equal(533, clip.DurationSeconds * 1000, 0.5);
        Assert.Equal(6, clip.BoneCount);
        Assert.All(clip.Bones, b => Assert.Equal(18, b.RotationKeys.Length));
        Assert.Contains(conversion.Textures, t => t.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(conversion.Textures, t => t.Name.EndsWith(".pcx", StringComparison.OrdinalIgnoreCase));
        output.WriteLine(string.Join(Environment.NewLine, conversion.Report));
    }

    [Fact]
    public void EnemyMasako_LeavesTheNanoShieldOut()
    {
        using var archive = Open();
        if (archive is null) return;
        var conversion = LegacyMeshSupport.Convert(archive.Read("enemy_masako.xff")!, "enemy_masako.xff", archive.Read);
        Assert.Contains(conversion.Report, n => n.Contains("g_nanoshield", StringComparison.Ordinal) && n.Contains("left out", StringComparison.Ordinal));
        // the shield is a sphere round the whole body: without it she is far narrower than she is tall
        var positions = conversion.Mesh.Submeshes.SelectMany(s => s.Lods[0].Batches).SelectMany(b => b.Positions).ToList();
        float width = positions.Max(p => p.X) - positions.Min(p => p.X), height = positions.Max(p => p.Y) - positions.Min(p => p.Y);
        output.WriteLine($"width {width:0.###} m, height {height:0.###} m");
        Assert.True(width < 0.6f * height, $"width {width}, height {height}");
    }

    [Fact]
    public void Barrel_FacesPointOutward()
    {
        using var archive = Open();
        if (archive is null) return;
        var conversion = LegacyMeshSupport.Convert(archive.Read("exploding_barrel.xff")!, "exploding_barrel.xff", archive.Read);
        var batches = conversion.Mesh.Submeshes.SelectMany(s => s.Lods[0].Batches).ToList();
        var all = batches.SelectMany(b => b.Positions).ToList();
        var centre = all.Aggregate(Vector3.Zero, (a, p) => a + p) / all.Count;
        int outward = 0, inward = 0, normalsOut = 0;
        foreach (var batch in batches)
        {
            foreach (var t in batch.Triangles)
            {
                var (a, b, c) = (batch.Positions[t.A], batch.Positions[t.B], batch.Positions[t.C]);
                var face = Vector3.Cross(b - a, c - a);
                if (face.LengthSquared() < 1e-12f) continue;
                var middle = (a + b + c) / 3;
                if (Vector3.Dot(face, middle - centre) > 0) outward++; else inward++;
                if (Vector3.Dot(batch.Normals[t.A] + batch.Normals[t.B] + batch.Normals[t.C], middle - centre) > 0) normalsOut++;
            }
        }
        output.WriteLine($"{outward} faces outward, {inward} inward, {normalsOut} with outward normals");
        Assert.True(outward >= 24 && inward == 0, $"{outward} outward, {inward} inward");
        Assert.Equal(outward, normalsOut);
    }
}
