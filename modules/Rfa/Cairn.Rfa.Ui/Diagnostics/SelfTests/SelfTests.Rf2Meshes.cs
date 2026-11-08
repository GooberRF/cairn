using System.Windows;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Views.Dialogs;
using ShellSelfTestContext = Cairn.Ui.Diagnostics.SelfTestContext;
using ShellScreenshotContext = Cairn.Ui.Diagnostics.ScreenshotContext;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Red Faction II meshes (.rfm/.rfc version 0x114) in the meshes module: a static mesh and a character open read-only
/// with their geometry, skeleton and the textures inside the .peg packs beside them; Convert writes the .v3m/.v3c with
/// the texture names translated, or kept with the textures written beside it; a packfile batch finds the packs among
/// the entries; a packfile preview resolves the same textures. Synthetic files under %TEMP%, and (when configured) a
/// real character from the Red Faction II install, copied out first.
/// </summary>
internal static class Rf2MeshSelfTests
{
    /// <summary>
    /// A character over the texture limit: its tab shows no texture error, Convert writes one submesh and an atlas from
    /// the packs with the default texture option, and the two-submesh .v3c an earlier version made is merged by V3C028's
    /// fix, which writes an atlas from the textures beside it.
    /// </summary>
    private static async Task ManyTextures(ShellSelfTestContext ctx, string folder, List<DocumentViewModel> opened)
    {
        var ws = RfaModule.Workspace;
        string[] textures = [.. Enumerable.Range(0, 9).Select(i => $"busy{i}.tga")];
        string source = Path.Combine(folder, "busy.rfc");
        File.WriteAllBytes(source, SyntheticRf2Mesh.SampleManyTextures(textures));
        File.WriteAllBytes(Path.Combine(folder, "busy_vcm.peg"), SyntheticRf2Mesh.SamplePeg([.. textures.Select((t, i) => (t, 16, 16, 0xFF000000u | (uint)(i * 25) << 8))]));

        var doc = await Open(ctx, source, opened);
        if (!ctx.Check(doc is { Kind: DocumentKind.CharacterMesh }, "busy.rfc opens as a character")) return;
        ctx.Check(doc!.Current.Submeshes.Count() == 1, "a character is one submesh in its tab too");
        ctx.Check(doc.Diagnostics.All(d => d.Code != MeshRules.TooManyTextures) && doc.Diagnostics.Any(d => d.Message.Contains("atlas", StringComparison.Ordinal)),
            "the tab explains the atlas instead of reporting V3C009");
        string outFolder = Path.Combine(folder, "busy-out");
        var written = await ws.Legacy.ConvertDocumentAsync(doc, new LegacyConvertChoice(LegacyConvertTarget.Folder, outFolder, false));
        ctx.Check(written is [var mesh, var atlas] && atlas.EndsWith("busy_atlas.tga", StringComparison.Ordinal) && V3dReader.ReadFile(mesh).Submeshes.Single().Lods[0].Textures.Length == 7,
            $"Convert writes one submesh of 7 textures and the atlas ({string.Join(", ", (written ?? []).Select(Path.GetFileName))})");

        // the file an earlier version wrote: 7 and 2 textures in two submeshes, its textures beside it
        string oldFolder = Path.Combine(folder, "busy-old");
        Directory.CreateDirectory(oldFolder);
        foreach (string texture in textures) File.WriteAllBytes(Path.Combine(oldFolder, texture), TgaWriter.Write(new BgraImage(16, 16), includeAlpha: false));
        string oldPath = Path.Combine(oldFolder, "busy.v3c");
        File.WriteAllBytes(oldPath, V3dWriter.Write(V3dBuilder.Build(SplitForTest(LegacyMeshSupport.Read(File.ReadAllBytes(source), "busy.rfc").Description), V3dBuildOptions.Default)));
        if (await Open(ctx, oldPath, opened) is not { } old) return;
        var diagnostic = old.Diagnostics.FirstOrDefault(d => d.Code == MeshRules.SeveralSubmeshes);
        if (ctx.Check(diagnostic?.QuickFixes.SingleOrDefault()?.Kind == QuickFixKind.MergeWithAtlas, "V3C028 offers the merge with an atlas"))
        {
            old.ApplyQuickFix(diagnostic!.QuickFixes[0], diagnostic);
            ctx.Check(old.Current.Submeshes.Single().Lods[0].Textures.Length == 7, "the fix leaves one submesh of 7 textures");
            ctx.Check(File.Exists(Path.Combine(oldFolder, "busy_atlas.tga")), "the fix writes the atlas beside the mesh");
            ctx.Check(!old.Diagnostics.Any(d => d.Code == MeshRules.SeveralSubmeshes), "V3C028 is gone");
        }
    }

    /// <summary>A one-submesh description cut into 7 and 2 textures, as earlier versions wrote.</summary>
    private static V3dMeshDescription SplitForTest(V3dMeshDescription d)
    {
        var sub = d.Submeshes[0];
        V3dSubmeshDescription Part(string name, int from, int count) => sub with
        {
            Name = FixedString.FromText(name, V3dSubmesh.NameSize),
            Materials = [.. sub.Materials.Skip(from).Take(count)],
            Lods = [.. sub.Lods.Select(l => l with { Groups = [.. l.Groups.Where(g => g.Material >= from && g.Material < from + count).Select(g => g with { Material = g.Material - from })], MorphVertices = null })],
        };
        return d with { Submeshes = [Part("busy", 0, 7), Part("busy_2", 7, 2)] };
    }

    [SelfTest("rfa.rf2-meshes")]
    public static async Task Rf2Meshes(ShellSelfTestContext ctx)
    {
        var ws = RfaModule.Workspace;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-rf2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<DocumentViewModel>();
        try
        {
            string crate = Path.Combine(folder, "crate.rfm"), guard = Path.Combine(folder, "guard.rfc");
            File.WriteAllBytes(crate, SyntheticRf2Mesh.SampleStatic());
            File.WriteAllBytes(guard, SyntheticRf2Mesh.SampleCharacter());
            byte[] cratePeg = SyntheticRf2Mesh.SamplePeg(("crate_side.tga", 16, 16, 0xFF3080D0), ("crate_top.tga", 16, 16, 0xFFD08030));
            File.WriteAllBytes(Path.Combine(folder, "crate_v3d.peg"), cratePeg);
            File.WriteAllBytes(Path.Combine(folder, "guard_vcm.peg"), SyntheticRf2Mesh.SamplePeg(("guard_body.tga", 8, 8, 0xFF40C040)));

            // a static mesh: read-only tab, its LODs, its textures from crate_v3d.peg
            var doc = await Open(ctx, crate, opened);
            if (ctx.Check(doc is { IsLegacy: true, Legacy.Mesh.IsRedFactionII: true }, "crate.rfm opens as a Red Faction II mesh tab"))
            {
                ctx.Check(doc!.IsReadOnly && doc.Kind == DocumentKind.StaticMesh, "read-only static mesh");
                ctx.Check(doc.ConvertBannerText == "Red Faction II static mesh (.rfm) is read-only in Cairn. Convert to .v3m to use it in the PC game.", $"banner: {doc.ConvertBannerText}");
                ctx.Check(doc.Current.Submeshes.Single().Lods.Length == 2 && doc.Current.Submeshes.First().Name.Text == "crate", "one submesh named after the object, two LODs");
                ctx.Check(doc.Diagnostics.Any(d => d.Code == LegacyMeshSource.NoteCode && d.Message.Contains("12 m", StringComparison.Ordinal)), "Problems lists the LOD distances among the notes");
                var side = doc.Resolver.Resolve("crate_side.tga");
                ctx.Check(side is { Origin: not null } && side.DisplayLocation.Contains(".peg packs beside crate.rfm", StringComparison.Ordinal), $"crate_side.tga resolves from the .peg beside it ({side?.DisplayLocation})");
                ctx.Check(side is not null && TgaCodec.Decode(side.ReadAllBytes(), "crate_side.tga").Width == 16, "the texture decodes from the pack");
                ctx.Check(doc.DescribeTexture("crate_top.vbm").Contains(".peg", StringComparison.Ordinal), $"a .vbm name finds the pack's texture too ({doc.DescribeTexture("crate_top.vbm")})");
                ctx.Check(doc.Resolver.Resolve("not_there_rf2.tga") is null, "a texture no pack holds is not found");

                string outFolder = Path.Combine(folder, "out");
                var translated = await ws.Legacy.ConvertDocumentAsync(doc, new LegacyConvertChoice(LegacyConvertTarget.Folder, outFolder, false));
                ctx.Check(translated is [var v3m] && v3m.EndsWith("crate.v3m", StringComparison.Ordinal) && V3dReader.ReadFile(v3m).Submeshes.Single().Lods.Length == 2,
                    $"Convert writes crate.v3m ({string.Join(", ", translated ?? [])})");
                var kept = await ws.Legacy.ConvertDocumentAsync(doc, new LegacyConvertChoice(LegacyConvertTarget.Folder, Path.Combine(folder, "kept"), false) { Rf2Textures = Rf2TextureMode.KeepOriginals });
                ctx.Check(kept is { Count: 3 } && kept.Skip(1).Select(Path.GetFileName).Order().SequenceEqual(["crate_side.tga", "crate_top.tga"]),
                    $"keeping the texture names writes the textures beside the mesh ({string.Join(", ", (kept ?? []).Select(Path.GetFileName))})");
                ctx.Check(kept is { Count: 3 } && TgaCodec.Decode(File.ReadAllBytes(kept[1]), kept[1]).Width == 16, "the written textures are the pack's");
                var again = await ws.Legacy.ConvertDocumentAsync(doc, new LegacyConvertChoice(LegacyConvertTarget.Folder, Path.Combine(folder, "kept"), false) { Rf2Textures = Rf2TextureMode.KeepOriginals });
                ctx.Check(again is [var second] && second.EndsWith("crate (2).v3m", StringComparison.Ordinal),
                    "converting again gives the mesh a free name and leaves the textures it refers to as they are");

                // glTF: the textures come from the pack too, written beside the .gltf as PNG
                string gltfFolder = Path.Combine(folder, "gltf");
                Directory.CreateDirectory(gltfFolder);
                string? gltf = await ws.Gltf.ExportDocumentAsync(doc, glb: false, interactive: false, gltfFolder);
                var pngs = Directory.Exists(gltfFolder) ? Directory.GetFiles(gltfFolder, "*.png").Select(Path.GetFileName).Order().ToList() : [];
                ctx.Check(gltf is not null && File.Exists(gltf) && pngs.Count == 2,
                    $"the glTF export writes the pack's textures beside it as PNG ({Path.GetFileName(gltf)}: {string.Join(", ", pngs)})");
            }

            await ManyTextures(ctx, folder, opened);

            // a character: its skeleton, weights and the texture from guard_vcm.peg
            var character = await Open(ctx, guard, opened);
            if (ctx.Check(character is { IsLegacy: true, Kind: DocumentKind.CharacterMesh }, "guard.rfc opens as a character"))
            {
                ctx.Check(character!.HasSkeleton && character.Scene.Skeleton.Count == 2, $"its skeleton shows ({character.Scene.Skeleton.Count} bones)");
                ctx.Check(character.ConvertBannerText?.StartsWith("Red Faction II character mesh (.rfc)", StringComparison.Ordinal) == true, $"banner: {character.ConvertBannerText}");
                ctx.Check(character.Current.CollisionSpheres.Single().BoneIndex == 1 && character.Current.Submeshes.First().Lods[0].PropPoints.Single().ParentIndex == 1,
                    "the sphere and the prop point stay on the arm");
                ctx.Check(character.Resolver.Resolve("guard_body.tga") is { Origin: not null }, "guard_body.tga resolves from guard_vcm.peg");
                ctx.Check(character.Diagnostics.Any(d => d.Message.Contains("turned in the bind pose", StringComparison.Ordinal)), "Problems says a bone is turned in the bind pose");
            }

            // a packfile batch: the packs are found among the packfile's entries (selected or not)
            {
                ArchiveBatchEntry E(string name, byte[] bytes) => new(name, bytes.Length, () => bytes);
                var mesh = E("crate.rfm", SyntheticRf2Mesh.SampleStatic());
                var pack = E("crate_v3d.peg", cratePeg);
                string batchOut = Path.Combine(folder, "batch");
                string? summary = await ws.Legacy.ConvertBatchAsync(new ArchiveBatchRequest("rf2.vpp", null, [mesh])
                {
                    Interactive = false,
                    Options = new LegacyConvertChoice(LegacyConvertTarget.Folder, batchOut, false) { Rf2Textures = Rf2TextureMode.KeepOriginals },
                    AllEntries = [mesh, pack],
                });
                ctx.Check(ws.Legacy.LastBatch is { Converted: [{ Textures.Length: 2 }] } && File.Exists(Path.Combine(batchOut, "crate_top.tga")),
                    $"Convert meshes and animations... writes the mesh with its textures from the packfile's .peg ({summary})");
            }

            // a packfile preview: the same textures from the .peg entries beside the mesh
            {
                var siblings = AssetSiblings.FromMemory("rf2.vpp", [new("crate_v3d.peg", cratePeg), new("readme.txt", [1, 2, 3])]);
                var resolver = LegacyMeshTextures.PreviewResolver(SyntheticRf2Mesh.SampleStatic(), "crate.rfm", siblings, ctx.Shell.Assets.Resolver);
                ctx.Check(resolver?.Resolve("crate_side.tga") is { Origin: "rf2.vpp" } && resolver.Resolve("readme.txt") is not null,
                    "the preview resolver finds the pack's textures and the packfile's other entries");
                var plain = LegacyMeshTextures.PreviewResolver(SyntheticExporterMesh.SampleStatic(), "box.v3d", siblings, ctx.Shell.Assets.Resolver);
                ctx.Check(plain?.Resolve("crate_side.tga") is null, "other meshes do not look inside .peg packs");
            }

            // a real character, copied out of the Red Faction II install with its packs
            if (LocalPaths.Rf2GameDirectory is { } rf2 && Path.Combine(rf2, "pc_media", "Botmatch_core") is var core && File.Exists(Path.Combine(core, "burke.rfc")))
            {
                string real = Path.Combine(folder, "real");
                Directory.CreateDirectory(real);
                foreach (string f in Directory.EnumerateFiles(core, "burke*.*"))
                {
                    // the install's files are read-only; the copies must not be, or the clean-up cannot delete them
                    string copy = Path.Combine(real, Path.GetFileName(f));
                    File.Copy(f, copy);
                    File.SetAttributes(copy, FileAttributes.Normal);
                }
                var burke = await Open(ctx, Path.Combine(real, "burke.rfc"), opened);
                if (ctx.Check(burke is { IsLegacy: true, Legacy.Mesh: not null, Kind: DocumentKind.CharacterMesh }, $"burke.rfc (Red Faction II) opens as a character ({burke?.ConvertBannerText})"))
                {
                    var textures = burke!.Current.Submeshes.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var missing = await Task.Run(() => textures.Where(t => burke.Resolver.Resolve(t) is null).ToList());
                    ctx.Check(textures.Count > 0 && missing.Count == 0, $"its {textures.Count} textures resolve from the .peg packs beside it (missing: {string.Join(", ", missing)})");
                    ctx.Check(burke.Scene.Skeleton.Count > 10, $"its skeleton shows ({burke.Scene.Skeleton.Count} bones)");
                }
            }
            else ctx.Log("  (no Red Faction II install configured: no real mesh was tried)");
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (ws.Documents.Contains(d)) ws.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<MeshDocumentViewModel?> Open(ShellSelfTestContext ctx, string path, List<DocumentViewModel> opened)
    {
        bool ok = ctx.Shell.OpenFile(path);
        await ctx.SettleAsync();
        if (!ok || RfaModule.Workspace.ActiveDocument is not MeshDocumentViewModel doc) return null;
        opened.Add(doc);
        return doc;
    }

    /// <summary><c>--dialog rfa.rf2-convert</c>: the Convert window for a Red Faction II mesh, with the texture choice and its report.</summary>
    [ScreenshotDialog("rfa.rf2-convert")]
    public static Window? ConvertDialog(ShellScreenshotContext ctx)
    {
        LegacyMeshSupport.EnsureRegistered();
        byte[] bytes = SyntheticRf2Mesh.SampleStatic();
        var translated = LegacyMeshSupport.Convert(bytes, "crate.rfm");
        var kept = LegacyMeshSupport.Convert(bytes, "crate.rfm", options: new LegacyConvertOptions { Rf2Textures = Rf2TextureMode.KeepOriginals });
        return new LegacyMeshConvertWindow(ctx.Shell.Dialogs,
            [new LegacyConvertItem("crate.rfm", translated.OutputName, translated.Report, IsRedFactionII: true, KeepTexturesReport: kept.Report)],
            null, Path.Combine(Path.GetTempPath(), "meshes"), new LegacyConvertChoice(LegacyConvertTarget.NextToSource, null, false))
        {
            Owner = ctx.MainWindow,
        };
    }
}
