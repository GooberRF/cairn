using Cairn.Assets;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Xunit.Abstractions;

namespace Cairn.Lvl.Tests;

/// <summary>Red Faction II levels converted: the RF2 options, the report, and the shared targets on synthetic and real levels.</summary>
public sealed class Rf2LevelConvertTests(ITestOutputHelper output)
{
    private static RfLevel Synthetic() => RflLevelReader.Read(Rf2LevelTestData.Level(), "synthetic_rf2.rfl");

    private static PegTextureSet Packs() => new("test",
        [new PegPack("synthetic_rf2.peg", () => Rf2LevelTestData.Peg(("rck_invisible.tga", 4, 4), ("zzz_unknown.tga", 2, 2), ("corona_red.tga", 2, 2)))]);

    private static Rf2LevelConversion.Prepared Prepare(LvlRf2Options? rf2 = null, LvlConvertTarget target = LvlConvertTarget.Glb) =>
        Rf2LevelConversion.Prepare(Synthetic(), new LvlConvertOptions { Target = target, Rf2 = rf2 ?? new LvlRf2Options() }, default);

    [Fact]
    public void Texture_names_are_translated_by_default_and_the_rf2_pixels_are_kept()
    {
        var p = Prepare(new LvlRf2Options { TexturePacks = Packs() });
        // rck_invisible.tga has a fixed Red Faction name; zzz_unknown.tga has none and stays.
        Assert.Equal(new[] { "rck_invisible04.tga", "zzz_unknown.tga" }, p.Level.StaticGeometry.Textures);
        Assert.Equal("rck_invisible04.tga", p.Level.Brushes[0].Geometry.Textures[0]);
        Assert.Contains(p.Issues, i => i.Code == LvlConvertCodes.Rf2Textures && i.Message.Contains("rck_invisible.tga → rck_invisible04.tga"));
        Assert.Contains(p.Issues, i => i.Code == LvlConvertCodes.Rf2Textures && i.Message.Contains("zzz_unknown.tga") && i.Message.Contains("no Red Faction translation"));
        Assert.Contains(p.Issues, i => i.Message == Cairn.Formats.Rf2.Rf2TextureNames.TextureSetNote);
        // The loader finds a translated name's RF2 original in the packs.
        var image = p.Options.TextureLoader!("rck_invisible04.tga");
        Assert.NotNull(image);
        Assert.Equal(4, image!.Width);
        Assert.Empty(p.SideFiles);

        // Through the shared converter: OBJ images are the RF2 pixels under the Red Faction names.
        var c = LvlConverter.Convert(LvlConvertInput.FromLevel(Synthetic()), new LvlConvertOptions
        {
            Target = LvlConvertTarget.Obj,
            ExcludedFaceClasses = RfFaceClass.Portal,
            Rf2 = new LvlRf2Options { TexturePacks = Packs() },
        });
        Assert.True(c.HasOutput);
        Assert.Contains(c.Obj!.Images, i => i.FileName == "rck_invisible04.png");
        Assert.Equal(LvlConvertCodes.Rf2Faces, c.Report.Issues[0].Code);
        Assert.Empty(c.Report.MissingTextures.Where(t => t.StartsWith("rck", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Keeping_the_originals_writes_them_beside_a_red_faction_output_as_tga()
    {
        var p = Prepare(new LvlRf2Options { Textures = LvlRf2TextureMode.KeepOriginals, TexturePacks = Packs() }, LvlConvertTarget.V3m);
        Assert.Equal(Rf2LevelTestData.StaticTextures, p.Level.StaticGeometry.Textures);
        Assert.Contains(p.SideFiles, f => f.FileName == "rck_invisible.tga");
        Assert.Contains(p.SideFiles, f => f.FileName == "corona_red.tga");
        Assert.Contains(p.Issues, i => i.Code == LvlConvertCodes.Rf2Textures && i.Message.Contains("are in none of the texture packs"));

        string folder = Path.Combine(Path.GetTempPath(), "cairn-l8-" + Guid.NewGuid().ToString("N"));
        try
        {
            var c = LvlConverter.Convert(LvlConvertInput.FromLevel(Synthetic()), new LvlConvertOptions
            {
                Target = LvlConvertTarget.V3m,
                Rf2 = new LvlRf2Options { Textures = LvlRf2TextureMode.KeepOriginals, TexturePacks = Packs() },
            }, "synthetic_rf2.v3m");
            Assert.True(c.HasOutput, c.Report.ToText());
            string path = Path.Combine(folder, "synthetic_rf2.v3m");
            var planned = c.PlannedFiles(path);
            Assert.Contains(planned, f => f.EndsWith("rck_invisible.tga", StringComparison.Ordinal));
            var written = c.WriteTo(path);
            Assert.Equal(planned, written);
            Assert.All(written, f => Assert.True(File.Exists(f), f));
            var tga = File.ReadAllBytes(Path.Combine(folder, "rck_invisible.tga"));
            Assert.Equal(2, tga[2]); // uncompressed true-colour TGA
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Lights_are_rescaled_and_coronas_become_lights_with_the_lightmap_multiplier()
    {
        var p = Prepare();
        var lights = p.Level.Objects.Lights;
        // The level light: range x 3, intensity x 1, then x 2 (the lightmap multiplier).
        Assert.Equal(15f, lights[0].Range);
        Assert.Equal(1f, lights[0].OnIntensity);
        // Two coronas give a light (the unset CD CD CD colour counts as white; the one without a bitmap has no
        // intensity); too few baked corners for a fit, so 40 m.
        Assert.Equal(3, lights.Length);
        var spot = lights[1];
        Assert.Equal(RfLightType.Spot, spot.Type);
        Assert.Equal(15f, spot.Fov);
        Assert.Equal(40f, spot.Range);
        Assert.Equal(-Vector3.UnitY, spot.Orientation.Forward);
        Assert.Equal(4f, spot.OnIntensity); // corona intensity 2, x 1, x 2
        Assert.Equal(new RflColor(255, 0, 0, 255), spot.Color);
        Assert.Equal(31, spot.Uid);         // above every uid of the level (the highest is the breakable trigger brush, 30)
        Assert.Equal(2, (int)(((uint)spot.Flags >> 8) & 0xF));
        Assert.True((spot.Flags & RfLightFlags.ShadowCasting) != 0);
        var point = lights[2];
        Assert.Equal(RfLightType.Omni, point.Type);
        Assert.Equal(new RflColor(255, 255, 255, 255), point.Color);
        Assert.Equal((32, 15f, 2f), (point.Uid, point.Fov, point.OnIntensity));

        // Kept coronas: Alpine's full cone angle and the white colour.
        Assert.Equal(60f, p.Level.Objects.Coronas[0].ConeAngle);
        Assert.Equal(new RflColor(255, 255, 255, 255), p.Level.Objects.Coronas[2].Color);
        foreach (string code in (string[])[LvlConvertCodes.Rf2Lights, LvlConvertCodes.Rf2Coronas, LvlConvertCodes.Rf2Ambient])
            Assert.Contains(p.Issues, i => i.Code == code);

        // Options: no corona lights, no multiplier, coronas dropped, given range.
        var q = Prepare(new LvlRf2Options { CoronasToLights = false, ApplyLightmapMultiplier = false, KeepCoronas = false, LightScale = 2, LightRangeScale = 1 });
        Assert.Equal(1f, Assert.Single(q.Level.Objects.Lights).OnIntensity);
        Assert.Equal(5f, q.Level.Objects.Lights[0].Range);
        Assert.Empty(q.Level.Objects.Coronas);
        var r = Prepare(new LvlRf2Options { CoronaRange = 12, CoronaScale = 0.5f, ApplyLightmapMultiplier = false });
        Assert.Equal(12f, r.Level.Objects.Lights[1].Range);
        Assert.Equal(1f, r.Level.Objects.Lights[1].OnIntensity);
    }

    [Fact]
    public void Brushes_become_rf1_safe()
    {
        var p = Prepare();
        var brushes = p.Level.Brushes;
        Assert.Equal(new[] { 27, 28, 29 }, brushes.Select(b => b.Uid)); // 30 is a breakable invisible trigger brush
        var geoable = brushes[1];
        Assert.Equal(RfSolidFlags.Detail, geoable.SolidFlags);
        Assert.Equal(-1, geoable.Life);
        Assert.Equal(RfAlpineBrushFlags.Geoable, geoable.Alpine!.Flags);
        Assert.Equal(RfSolidFlags.Detail, brushes[2].SolidFlags); // 0x8 dropped
        Assert.Contains(p.Issues, i => i.Code == LvlConvertCodes.Rf2Brushes && i.Message.Contains("30"));
        Assert.Contains(p.Issues, i => i.Code == LvlConvertCodes.Rf2Brushes && i.Message.Contains("0x20 on 1 brush"));

        var q = Prepare(new LvlRf2Options { GeoableWithoutDetail = true, DropInvisibleTriggerBrushes = false });
        Assert.Equal(4, q.Level.Brushes.Length);
        Assert.Equal(RfSolidFlags.None, q.Level.Brushes[1].SolidFlags);
    }

    [Fact]
    public void Jump_pads_clutter_and_rf2_classes_are_handled_and_reported()
    {
        var table = Rf2ClutterTable.Parse("""
            // clutter.tbl
            #Clutter
            $Class Name:			"minelight01"
            $V3D Filename:			"MineLight01.V3D"
            $Flags:					("collide_weapon")

            $Class Name:			"burke_toy"
            $V3D Filename:			"burke.VCM"
            $Flags:					("collide_object" "moves")
            #End
            """);
        Assert.Equal("MineLight01.v3m", table["MINELIGHT01"].ConvertedMeshFile);
        Assert.Equal(1, table["minelight01"].Collision);
        Assert.Equal(("burke.v3c", (byte)2), (table["burke_toy"].ConvertedMeshFile, table["burke_toy"].Collision));

        var p = Prepare(new LvlRf2Options { ClutterClasses = table });
        var o = p.Level.Objects;
        Assert.Equal(10f * LvlRf2Options.DefaultJumpPadScale, o.PushRegions[0].Strength);
        Assert.Equal(5f, o.PushRegions[1].Strength);
        var mesh = Assert.Single(o.AlpineMeshes);
        Assert.Equal((21, "MineLight01.v3m", (byte)1), (mesh.Uid, mesh.MeshFile, mesh.Collision));
        Assert.Equal(new Vector3(6, 0, 0), mesh.Position);
        Assert.Equal("no_such_class", Assert.Single(o.Clutter).ClassName);
        Assert.Contains(p.Issues, i => i.Code == LvlConvertCodes.Rf2Objects && i.Message.Contains("no_such_class"));
        Assert.Contains(p.Issues, i => i.Code == LvlConvertCodes.Rf2Objects && i.Message.Contains("inven_HealthKit"));
        Assert.Contains(p.Issues, i => i.Code == LvlConvertCodes.Rf2Objects && i.Message.Contains("Entities (RF2)"));

        var none = Prepare();
        Assert.Equal(2, none.Level.Objects.Clutter.Length);
        Assert.Contains(none.Issues, i => i.Code == LvlConvertCodes.Rf2Objects && i.Severity == LvlIssueSeverity.Warning && i.Message.Contains("clutter table was not found"));
    }

    [Theory]
    [InlineData(LvlConvertTarget.Glb)]
    [InlineData(LvlConvertTarget.Gltf)]
    [InlineData(LvlConvertTarget.Obj)]
    [InlineData(LvlConvertTarget.V3m)]
    public void The_synthetic_level_converts_to_every_target(LvlConvertTarget target)
    {
        foreach (var geometry in (LvlGeometryChoice[])[LvlGeometryChoice.Compiled, LvlGeometryChoice.Brushes])
        {
            var c = LvlConverter.Convert(LvlConvertInput.FromLevel(Synthetic()),
                new LvlConvertOptions { Target = target, Geometry = geometry, Rf2 = new LvlRf2Options { TexturePacks = Packs() } }, "synthetic_rf2" + LvlConvertOptions.ExtensionOf(target));
            Assert.True(c.HasOutput, c.Report.ToText());
            Assert.False(c.Report.HasErrors, c.Report.ToText());
            Assert.StartsWith("LC09", c.Report.Issues[0].Code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_synthetic_level_converts_to_a_group_file_with_the_rf2_changes()
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-l8-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new LvlConvertOptions
            {
                Target = LvlConvertTarget.Rfg,
                Rf2 = new LvlRf2Options { ClutterClasses = Rf2ClutterTable.Parse("$Class Name: \"minelight01\"\n$V3D Filename: \"MineLight01.V3D\"\n") },
            };
            var c = LvlConverter.Convert(LvlConvertInput.FromLevel(Synthetic()), options, "synthetic_rf2.rfg");
            if (c.Report.WithCode(LvlConvertCodes.TargetUnavailable).Any()) return; // the group-file writer is not in this build
            Assert.True(c.HasOutput, c.Report.ToText());
            Assert.Equal(LvlConvertCodes.Rf2Faces, c.Report.Issues[0].Code);
            string path = Path.Combine(folder, "synthetic_rf2.rfg");
            c.WriteTo(path);
            var rfg = RfgReader.ReadFile(path);
            var brushes = rfg.AllBrushes.ToList();
            Assert.Equal(4, brushes.Count); // three brushes and the moving group's mover (the trigger brush is left out)
            Assert.DoesNotContain(brushes, b => ((uint)b.SolidFlags & ~0x17u) != 0);
            Assert.Contains(brushes, b => b.Alpine is { Flags: RfAlpineBrushFlags.Geoable } && b.Life == -1);
            Assert.Contains(brushes, b => b.Geometry.Textures.Contains("rck_invisible04.tga"));
            var objects = rfg.Groups.SelectMany(g => g.Objects.All()).ToList();
            Assert.Equal(3, objects.OfType<RfLight>().Count());
            Assert.Equal(3, objects.OfType<RfCorona>().Count());
            Assert.Equal("MineLight01.v3m", Assert.Single(objects.OfType<RfAlpineMesh>()).MeshFile);
            Assert.Equal(10f * LvlRf2Options.DefaultJumpPadScale, objects.OfType<RfPushRegion>().Single(p => p.Shape == 1).Strength);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    // ── Real levels (bounded; CAIRN_LVL_CORPUS=1 converts more) ─────────────

    private static IEnumerable<(string Name, Func<byte[]> Load, PegTextureSet Packs)> RealLevels()
    {
        string[] chosen = LvlSamples.CorpusEnabled ? ["dmc13.rfl", "ctfo02.rfl", "l01s5.rfl", "l10s1c.rfl", "dm03.rfl", "l05s1.rfl"] : ["dmc13.rfl", "ctfo02.rfl"];
        foreach (var s in Rf2Samples.ReduxLevels().Where(s => chosen.Contains(Path.GetFileName(s.Name), StringComparer.OrdinalIgnoreCase)))
            yield return (s.Name, s.Load, Rf2Samples.ReduxPacks(Path.GetFileName(s.Name)));
        if (Rf2Samples.ReduxFolder is null)
        {
            foreach (var s in Rf2Samples.PackedLevels().Where(s => chosen.Contains(Path.GetFileName(s.Name), StringComparer.OrdinalIgnoreCase)))
                yield return (s.Name, s.Load, Rf2Samples.PackedPacks(Path.GetFileName(s.Name)));
        }
    }

    [Fact]
    public void Real_rf2_levels_convert_to_gltf_obj_and_v3m()
    {
        string? outFolder = Environment.GetEnvironmentVariable("CAIRN_LVL_CONVERT_OUT");
        var clutter = Cairn.Workspace.LocalPaths.Rf2GameDirectory is { } rf2Dir ? Rf2ClutterTable.FromGameFolder(rf2Dir) : null;
        foreach (var (name, load, packs) in RealLevels())
        {
            var level = RflLevelReader.Read(load(), Path.GetFileName(name));
            foreach (var target in (LvlConvertTarget[])[LvlConvertTarget.Glb, LvlConvertTarget.Obj, LvlConvertTarget.V3m])
            {
                var options = new LvlConvertOptions { Target = target, Rf2 = new LvlRf2Options { TexturePacks = packs, ClutterClasses = clutter } };
                string file = Path.GetFileNameWithoutExtension(name) + LvlConvertOptions.ExtensionOf(target);
                var c = LvlConverter.Convert(LvlConvertInput.FromLevel(level), options, file);
                Assert.True(c.HasOutput, $"{name} {target}: {c.Report.ToText()}");
                Assert.False(c.Report.Issues.Any(i => i.Severity == LvlIssueSeverity.Error && i.Code != LvlConvertCodes.NameTooLong),
                    $"{name} {target}: {string.Join(Environment.NewLine, c.Report.Issues.Where(i => i.Severity == LvlIssueSeverity.Error))}");
                if (target == LvlConvertTarget.Glb)
                {
                    Assert.True(c.Report.Stats.Textures > 0, $"{name}: no textures from the packs");
                    Assert.Contains(c.Report.Issues, i => i.Code == LvlConvertCodes.Rf2Coronas || level.Objects.Coronas.IsEmpty);
                    output.WriteLine($"{name}: {c.Report.Stats.Faces:N0} faces, {c.Report.Stats.Textures} textures, {c.Report.MissingTextures.Length} missing, {level.Objects.Lights.Length} lights -> {c.Gltf!.Nodes.Count} nodes");
                    foreach (var issue in c.Report.Issues.Where(i => i.Code.StartsWith("LC09", StringComparison.Ordinal))) output.WriteLine("  " + issue);
                }
                if (outFolder is not null) c.WriteTo(Path.Combine(outFolder, file));
            }
            if (outFolder is not null)
            {
                // A mesh with the RF2 textures kept and written beside it, to look at in the meshes module.
                string kept = Path.GetFileNameWithoutExtension(name) + "_rf2tex.v3m";
                var options = new LvlConvertOptions { Target = LvlConvertTarget.V3m, Rf2 = new LvlRf2Options { TexturePacks = packs, Textures = LvlRf2TextureMode.KeepOriginals } };
                var c = LvlConverter.Convert(LvlConvertInput.FromLevel(level), options, kept);
                c.WriteTo(Path.Combine(outFolder, Path.GetFileNameWithoutExtension(name) + "_rf2tex", kept));
                File.WriteAllText(Path.Combine(outFolder, Path.GetFileNameWithoutExtension(name) + "_rf2tex", kept + ".report.txt"), c.Report.ToText());
                // A group file (brushes and objects) for the editor, when the group-file writer is in this build.
                var rfg = LvlConverter.Convert(LvlConvertInput.FromLevel(level),
                    new LvlConvertOptions { Target = LvlConvertTarget.Rfg, Rf2 = new LvlRf2Options { TexturePacks = packs, ClutterClasses = clutter } });
                if (rfg.HasOutput)
                {
                    string rfgPath = Path.Combine(outFolder, Path.GetFileNameWithoutExtension(name) + ".rfg");
                    rfg.WriteTo(rfgPath);
                    File.WriteAllText(rfgPath + ".report.txt", rfg.Report.ToText());
                }
            }
        }
    }
}
