using Cairn.Assets;
using Cairn.Rfa.Linting;
using Cairn.Vfx.Docs;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

public sealed class VfxLintTests(ITestOutputHelper output)
{
    private static string[]? StockFiles() =>
        LocalPaths.Corpus is { } dir && Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : null;

    private static string? TablesFolder() =>
        LocalPaths.Research is { } r && Path.Combine(r, "rfa_workbench", "rf_decomp", "tables") is var d && Directory.Exists(d) ? d : null;

    [Fact]
    public void StockFilesLintWithoutErrorsAtCurrentVersion()
    {
        if (StockFiles() is not { } files) return;
        var counts = new SortedDictionary<string, int>();
        var errors = new List<string>();
        int current = 0;
        foreach (var path in files)
        {
            var vfx = VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            var diags = VfxLinter.Lint(vfx, new VfxLintContext(Path.GetFileName(path)));
            foreach (var d in diags) counts[d.Code] = counts.GetValueOrDefault(d.Code) + 1;
            if (vfx.Version != VfxVersion.Current) continue;
            current++;
            errors.AddRange(diags.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
        }
        output.WriteLine(string.Join(", ", counts.Select(kv => $"{kv.Key}x{kv.Value}")));
        Assert.Equal(42, current);
        Assert.False(counts.ContainsKey(VfxRules.FaceMaterialOutOfRange), "stock face material indices are valid");
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(20)));
    }

    /// <summary>A 0x40006 stock file with a timed mesh, materials and a particle system.</summary>
    private static VfxFile? Sample()
    {
        if (StockFiles() is not { } files) return null;
        foreach (var path in files)
        {
            var v = VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            if (v.Version == VfxVersion.Current && v.Sections.OfType<VfxParticleSystem>().Any(p => p.MaterialIndex is not null)
                && v.Sections.OfType<VfxMesh>().Any(m => m.Frames.Length > 1 && m.StartTime is not null && m.Faces.Length > 0 && m.MaterialIndices is { Length: > 0 }))
                return v;
        }
        return null;
    }

    private static VfxFile EditFirst<T>(VfxFile f, Func<T, bool> pick, Func<T, T> edit) where T : VfxSection
    {
        int i = f.Sections.IndexOf(f.Sections.OfType<T>().First(pick));
        return f with { Sections = f.Sections.SetItem(i, edit((T)f.Sections[i])) };
    }

    private static VfxFile EditMesh(VfxFile f, Func<VfxMesh, VfxMesh> e) =>
        EditFirst(f, (VfxMesh m) => m.Frames.Length > 1 && m.StartTime is not null && m.Faces.Length > 0 && m.MaterialIndices is { Length: > 0 }, e);

    private static VfxFile EditFace(VfxFile f, Func<VfxFace, VfxFace> e) => EditMesh(f, m => m with { Faces = m.Faces.SetItem(0, e(m.Faces[0])) });

    private static VfxFile EditParticles(VfxFile f, Func<VfxParticleSystem, VfxParticleSystem> e) => EditFirst(f, (VfxParticleSystem p) => p.MaterialIndex is not null, e);

    private static VfxFile EditMaterial(VfxFile f, Func<VfxMaterial, VfxMaterial> e) => EditFirst(f, (VfxMaterial m) => m.Texture0 is not null, e);

    private static int Count(VfxFile f, string code, VfxLintContext? c = null) => VfxLinter.Lint(f, c).Count(d => d.Code == code);

    [Fact]
    public void EveryRuleFiresOnAMutatedFile()
    {
        if (Sample() is not { } f) return;
        var mutations = new Dictionary<string, Func<VfxFile, VfxFile>>
        {
            [VfxRules.BadVersion] = x => x with { Version = 0x20000 },
            [VfxRules.EngineFatalVersion] = x => x with { Version = 0x40002 },
            [VfxRules.OldVersion] = x => x with { Version = 0x3000E },
            [VfxRules.MissingFaceVertex] = x => EditFace(x, a => a with { FaceVertex0 = 999999 }),
            [VfxRules.VertexOutOfRange] = x => EditFace(x, a => a with { V0 = 999999 }),
            [VfxRules.FaceMaterialOutOfRange] = x => EditFace(x, a => a with { MaterialIndex = 99 }),
            [VfxRules.MaterialSlotOutOfRange] = x => EditMesh(x, m => m with { MaterialIndices = m.MaterialIndices!.Value.SetItem(0, 9999) }),
            [VfxRules.ParticleMaterialOutOfRange] = x => EditParticles(x, p => p with { MaterialIndex = 9999 }),
            [VfxRules.PlaceholderTexture] = x => EditMaterial(x, m => m with { Texture0 = m.Texture0! with { Name = "$original_map" } }),
            [VfxRules.TextureExtension] = x => EditMaterial(x, m => m with { Texture0 = m.Texture0! with { Name = "fire.png" } }),
            [VfxRules.ParentNotInFile] = x => EditMesh(x, m => m with { Parent = "thruster_01" }),
            [VfxRules.FrameCountMismatch] = x => EditMesh(x, m => m with { Frames = m.Frames.Add(m.Frames[^1]) }),
            [VfxRules.MeshOutsideEndFrame] = x => EditMesh(x, m => m with { StartTime = m.StartTime + 1000, EndTime = m.EndTime + 1000 }),
            [VfxRules.EndFrameMismatch] = x => x with { EndFrame = 1 },
            [VfxRules.SectionName] = x => EditMesh(x, m => m with { Name = "" }),
            [VfxRules.MissingSpacewarp] = x => EditParticles(x, p => p with { Warps = ["no such warp"] }),
            [VfxRules.ParticleRange] = x => EditParticles(x, p => p with { ParticleCount = 5000 }),
            [VfxRules.UnusedMaterial] = x => x with { Sections = x.Sections.Add(x.Sections.OfType<VfxMaterial>().First()) },
            [VfxRules.UnexercisedFeature] = x => x with { Sections = x.Sections.Add(new VfxOpaqueSection(VfxSectionTag.Camera, [])) },
            [VfxRules.NonFinite] = x => EditMesh(x, m => m with { BoundingRadius = float.NaN }),
            [VfxRules.ZeroAreaFace] = x => EditFace(x, a => a with { V1 = a.V0, V2 = a.V0 }),
            [VfxRules.EmptyMesh] = x => EditMesh(x, m => m with { NumVertices = 0 }),
        };
        var missing = new List<string>();
        foreach (var rule in VfxRules.All)
        {
            if (rule.Code == VfxRules.TextureNotFound) continue;
            Assert.True(mutations.ContainsKey(rule.Code), $"no mutation for {rule.Code}");
            var mutated = mutations[rule.Code](f);
            if (Count(mutated, rule.Code) <= Count(f, rule.Code)) missing.Add(rule.Code);
        }
        Assert.True(missing.Count == 0, "did not fire: " + string.Join(", ", missing));

        // Texture lookup through a resolver that searches only an empty folder.
        string empty = Directory.CreateTempSubdirectory("cairn-vfx-lint").FullName;
        try
        {
            var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = empty });
            Assert.True(Count(f, VfxRules.TextureNotFound, new VfxLintContext("x.vfx", resolver)) > 0);
            Assert.Equal(0, Count(f, VfxRules.TextureNotFound));
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public void QuickFixesClearTheirDiagnostics()
    {
        if (Sample() is not { } f) return;
        var cases = new (string Code, VfxFile File)[]
        {
            (VfxRules.EndFrameMismatch, f with { EndFrame = 1 }),
            (VfxRules.SectionName, EditMesh(f, m => m with { Name = "" })),
            (VfxRules.SectionName, EditMesh(f, m => m with { Name = OtherName(f, m) })),
            (VfxRules.UnusedMaterial, f with { Sections = f.Sections.Add(f.Sections.OfType<VfxMaterial>().First()) }),
            (VfxRules.UnusedMaterial, UnusedFirstMaterial(f)),
        };
        foreach (var (code, file) in cases)
        {
            var diag = VfxLinter.Lint(file).First(d => d.Code == code && d.QuickFixes.Count > 0);
            var fixedFile = diag.QuickFixes[0].Apply(file);
            Assert.True(Count(fixedFile, code) <= Count(f, code), $"{code} still reported after '{diag.QuickFixes[0].Label}'");
            Assert.DoesNotContain(VfxLinter.Lint(fixedFile), d => d.Severity == DiagnosticSeverity.Error);
        }
    }

    [Fact]
    public void RulesAndDocsAreComplete()
    {
        Assert.Equal(VfxRules.All.Length, VfxRules.All.Select(r => r.Code).Distinct().Count());
        foreach (var id in VfxFormatDocs.InspectorIds) Assert.False(string.IsNullOrEmpty(VfxFormatDocs.Find(id)?.Tooltip), id);
        Assert.NotNull(VfxFormatDocs.FindTopic("vfx.playback"));
        Assert.NotNull(VfxFormatDocs.FindSection("vfx.mesh"));
    }

    [Fact]
    public void TableUsageFindsTheStockReferences()
    {
        if (TablesFolder() is not { } dir) return;
        var refs = VfxTableUsage.Find(t => File.Exists(Path.Combine(dir, t)) ? TblFileText(Path.Combine(dir, t)) : null);
        foreach (var g in refs.GroupBy(r => r.Table))
            output.WriteLine($"{g.Key}: {g.Count()} lines, {g.Select(r => r.VfxName.ToLowerInvariant()).Distinct().Count()} files; fields {string.Join(", ", g.Select(r => r.Field).Distinct().Take(6))}");
        int Files(string t) => refs.Where(r => r.Table == t).Select(r => r.VfxName.ToLowerInvariant()).Distinct().Count();
        Assert.Equal(17, refs.Count(r => r.Table == "vclip.tbl"));
        Assert.Equal(14, Files("vclip.tbl"));
        Assert.Equal(24, Files("entity.tbl"));
        Assert.Equal(9, Files("weapons.tbl"));
        Assert.All(refs.Where(r => r.Table == "vclip.tbl"), r => Assert.True(r.IsOneShot && r.Entry is not null));
    }

    private static string OtherName(VfxFile f, VfxMesh m) =>
        f.Sections.Where(s => !ReferenceEquals(s, m)).Select(VfxLinter.NameOf).First(n => !string.IsNullOrEmpty(n))!;

    /// <summary>Inserts an unused material before the others and renumbers every reference, so removing it exercises the index fix-up.</summary>
    private static VfxFile UnusedFirstMaterial(VfxFile f)
    {
        var first = f.Sections.OfType<VfxMaterial>().First();
        var shifted = f.Sections.Select(s => s switch
        {
            VfxMesh m when m.MaterialIndices is { } mi => m with { MaterialIndices = [.. mi.Select(i => i + 1)] },
            VfxParticleSystem p when p.MaterialIndex is { } pm => p with { MaterialIndex = pm + 1 },
            _ => s,
        }).ToImmutableArray();
        return f with { Sections = shifted.Insert(shifted.IndexOf(first), first with { Type = 2 }) };
    }

    private static string TblFileText(string path) => Cairn.Formats.Tbl.TblTokenizer.ReadText(path);
}
