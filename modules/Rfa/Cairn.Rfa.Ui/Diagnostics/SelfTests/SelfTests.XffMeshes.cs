using System.Windows;
using Cairn.Formats.XForge;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Rfa.Ui.Preview;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Views.Dialogs;
using ShellSelfTestContext = Cairn.Ui.Diagnostics.SelfTestContext;
using ShellScreenshotContext = Cairn.Ui.Diagnostics.ScreenshotContext;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Red Faction for N-Gage scenes (.xff) in the meshes module. <c>rfa.xff-meshes</c> (synthetic, always): a model and a
/// character open read-only by content (a level is declined), the character plays its own loop, textures and the
/// generated colours resolve, Convert to lists the targets by content, Convert writes the .v3m, or the .v3c with its
/// .rfa, and the textures (the scale and the copies as chosen), the batch converts and declines the level, the packfile
/// preview plays the loop. <c>rfa.xff-ngage</c> (the game's <c>redfaction.cfl</c>, when configured): <c>enemy_ug.xff</c>
/// and <c>item_healthpack.xff</c> opened from the archive with their textures, captured dark and light (the character
/// mid-animation), and the converted <c>.v3c</c> opened back in Cairn, playing the <c>.rfa</c> beside it by itself, and
/// captured too; without the <c>.rfa</c> it starts in the bind pose (no stock clip scatters its parts).
/// </summary>
internal static class XffMeshSelfTests
{
    private static readonly byte[] TexturePcx = SyntheticPcx.Write(16, 16, 8, 1, (x, y) => (x / 4 + y / 4) % 2 == 0 ? 1 : 2,
        [.. new byte[3], 200, 160, 60, 90, 60, 30, .. new byte[759]]);

    /// <summary>The name the converted meshes give <see cref="SyntheticXffMesh.Texture"/>: Red Faction's tools take .tga.</summary>
    private static readonly string TextureTga = XffTextureFix.PcName(SyntheticXffMesh.Texture);

    [SelfTest("rfa.xff-meshes")]
    public static async Task Synthetic(ShellSelfTestContext ctx)
    {
        var ws = RfaModule.Workspace;
        var module = ctx.Shell.Modules.OfType<RfaModule>().First();
        string folder = Path.Combine(Path.GetTempPath(), "cairn-xff-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<DocumentViewModel>();
        try
        {
            byte[] model = SyntheticXffMesh.SampleModel(), character = SyntheticXffMesh.SampleCharacter(), level = SyntheticXffMesh.SampleLevel();
            string crate = Path.Combine(folder, "crate.xff"), enemy = Path.Combine(folder, "enemy_box.xff"), room = Path.Combine(folder, "level_box.xff");
            File.WriteAllBytes(crate, model);
            File.WriteAllBytes(enemy, character);
            File.WriteAllBytes(room, level);
            File.WriteAllBytes(Path.Combine(folder, SyntheticXffMesh.Texture), TexturePcx);

            // the claims: models and characters are the module's, a level is not
            ctx.Check(RfaModule.XffKind.Claim("crate.xff", model) == ContentClaims.Recognised && RfaModule.XffKind.Claim("enemy_box.xff", character) == ContentClaims.Recognised,
                "the .xff kind recognises a model and a character");
            ctx.Check(RfaModule.XffKind.Claim("level_box.xff", level) == ContentClaims.NotMine && module.Claim("level_box.xff", level) == ContentClaims.NotMine,
                "a level is not the meshes module's (kind, preview and batch claims)");
            ctx.Check(module.CanPreview("crate.xff") && module.CanConvert("crate.xff"), "the preview and the batch take .xff by name");
            ctx.Check(ctx.Shell.Modules.SelectMany(m => m.DocumentKinds).Any(k => k.Extensions.Contains(".xff")), "a document kind lists .xff (the file associations list it)");

            // Convert to: the targets depend on the content
            string[] Targets(string name, byte[] bytes) => [.. AssetConversions.TargetsFor(ctx.Shell.Modules, name, bytes).Where(t => ReferenceEquals(t.Converter, module)).Select(t => t.Target.Extension)];
            ctx.Check(Targets("crate.xff", model).SequenceEqual([".v3m", ".gltf", ".glb", ".obj"]), $"a model converts to .v3m, glTF and OBJ ({string.Join(", ", Targets("crate.xff", model))})");
            ctx.Check(Targets("enemy_box.xff", character).SequenceEqual([".v3c", ".gltf", ".glb", ".obj"]), $"a character converts to .v3c, glTF and OBJ ({string.Join(", ", Targets("enemy_box.xff", character))})");
            ctx.Check(Targets("level_box.xff", level).Length == 0 && Targets("crate.xff", model.AsSpan(0, 16).ToArray()).Length == 0, "a level, or 16 bytes, gets no targets here");

            // the model: a read-only tab, its texture from the folder and its generated colours
            var doc = await Open(ctx, crate, opened);
            if (ctx.Check(doc is { IsLegacy: true, Legacy.Mesh.Xff: not null, Kind: DocumentKind.StaticMesh }, "crate.xff opens as an N-Gage model tab"))
            {
                ctx.Check(doc!.IsReadOnly && doc.ConvertBannerText?.StartsWith("Red Faction for N-Gage model (.xff): ", StringComparison.Ordinal) == true
                    && doc.ConvertBannerText.Contains("24 triangles", StringComparison.Ordinal), $"banner: {doc.ConvertBannerText}");
                ctx.Check(doc.Current.Submeshes.Select(s => s.Name.Text).SequenceEqual(["crate", "glow"]), "one submesh per mesh node");
                ctx.Check(doc.Current.Submeshes.SelectMany(s => s.Materials).Any(m => m.DiffuseMap.Text == TextureTga)
                    && !doc.Current.Submeshes.SelectMany(s => s.Materials).Any(m => XffTextureFix.IsPcx(m.DiffuseMap.Text)), $"the mesh names the texture {TextureTga}");
                ctx.Check(doc.Resolver.Resolve(TextureTga) is not null && doc.Resolver.Resolve("crate_colours.tga") is { Origin: not null },
                    "the texture resolves from the folder (the .pcx as the .tga) and the generated colours from the conversion");
                ctx.Check(doc.Diagnostics.Any(d => d.Code == LegacyMeshSource.NoteCode && d.Message.Contains("additive", StringComparison.Ordinal)), "Problems lists the notes (the additive part)");
                ctx.Check(((IDocumentContent)doc).ContentHead.Length == model.Length, "the tab gives its whole file to Convert to");

                string outFolder = Path.Combine(folder, "out");
                var written = await ws.Legacy.ConvertDocumentAsync(doc, new LegacyConvertChoice(LegacyConvertTarget.Folder, outFolder, false) { XffScale = 1f });
                ctx.Check(written is { Count: 3 } && written.Select(Path.GetFileName).SequenceEqual(["crate.v3m", "crate_colours.tga", TextureTga]),
                    $"Convert writes crate.v3m, the generated colours and the texture ({string.Join(", ", (written ?? []).Select(Path.GetFileName))})");
                if (written is { Count: 3 })
                {
                    var v3m = V3dReader.ReadFile(written[0]);
                    float top = v3m.Submeshes.First().Lods[0].Batches.SelectMany(b => b.Positions).Max(p => p.Y);
                    ctx.Check(MathF.Abs(top - 1f) < 1e-4f, $"the chosen scale (1) is applied (crate top at {top})");
                    var tga = ImageDecoder.Decode(File.ReadAllBytes(written[2]), TextureTga);
                    var pcx = ImageDecoder.Decode(TexturePcx, SyntheticXffMesh.Texture);
                    ctx.Check(File.ReadAllBytes(written[2]) is [0, 0, 2, ..] && tga.Pixels.AsSpan().SequenceEqual(pcx.Pixels), "the .pcx is written as a TGA with its pixels");
                    ctx.Check(!MeshLinter.Analyze(v3m).Any(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning), "the .v3m passes the mesh linter");
                }
                var none = await ws.Legacy.ConvertDocumentAsync(doc, new LegacyConvertChoice(LegacyConvertTarget.Folder, Path.Combine(folder, "bare"), false) { XffWriteTextures = false });
                ctx.Check(none is { Count: 2 } && !none.Any(p => p.EndsWith(".pcx", StringComparison.OrdinalIgnoreCase)), "with the copies off only the mesh and the generated colours are written");

                // the existing exports: OBJ with a .mtl and PNG textures (the .pcx and the generated colours among them)
                string objFolder = Path.Combine(folder, "obj");
                Directory.CreateDirectory(objFolder);
                string? obj = await RfaModule.Obj.ExportDocumentAsync(doc, interactive: false, objFolder);
                var objPngs = Directory.GetFiles(objFolder, "*.png").Select(Path.GetFileName).Order().ToList();
                ctx.Check(obj is not null && File.Exists(obj) && objPngs.Count == 2, $"the OBJ export writes the mesh with its two textures as PNG ({Path.GetFileName(obj)}: {string.Join(", ", objPngs)})");
            }

            // the character: its skeleton and its own loop playing
            var chr = await Open(ctx, enemy, opened);
            if (ctx.Check(chr is { IsLegacy: true, Kind: DocumentKind.CharacterMesh }, "enemy_box.xff opens as a character"))
            {
                ctx.Check(chr!.Scene.Skeleton.Names.SequenceEqual(["body", "head"]), $"one bone per animated part ({string.Join(", ", chr.Scene.Skeleton.Names)})");
                for (int i = 0; i < 100 && chr.PreviewClip is null; i++) await Task.Delay(50);
                ctx.Check(chr.PreviewClip is { BoneCount: 2 } && chr.SelectedPreviewClip?.Label == "enemy_box_walk.rfa", $"the tab plays its own loop ({chr.SelectedPreviewClip?.Label})");
                ctx.Check(chr.PreviewClipOptions.Count == 2, "the picker offers the bind pose and the loop only");
                ctx.Check(chr.ConvertBannerText?.Contains("enemy_box_walk.rfa", StringComparison.Ordinal) == true, $"banner: {chr.ConvertBannerText}");

                // glTF: the character with its textures (and its skeleton)
                string gltfFolder = Path.Combine(folder, "gltf");
                Directory.CreateDirectory(gltfFolder);
                string? gltf = await ws.Gltf.ExportDocumentAsync(chr, glb: false, interactive: false, gltfFolder);
                var gltfPngs = Directory.GetFiles(gltfFolder, "*.png").Select(Path.GetFileName).Order().ToList();
                ctx.Check(gltf is not null && File.Exists(gltf) && gltfPngs.Count == 2 && GltfReader.ReadFile(gltf).Skins.Count == 1,
                    $"the glTF export writes the character with its skin and two textures ({Path.GetFileName(gltf)}: {string.Join(", ", gltfPngs)})");

                string outFolder = Path.Combine(folder, "chr");
                var written = await ws.Legacy.ConvertDocumentAsync(chr, new LegacyConvertChoice(LegacyConvertTarget.Folder, outFolder, false));
                ctx.Check(written is { Count: >= 3 } && written.Take(2).Select(Path.GetFileName).SequenceEqual(["enemy_box.v3c", "enemy_box_walk.rfa"]),
                    $"Convert writes the .v3c and its clip ({string.Join(", ", (written ?? []).Select(Path.GetFileName))})");
                if (written is { Count: >= 3 })
                {
                    var v3c = V3dReader.ReadFile(written[0]);
                    var rfa = RfaReader.Read(File.ReadAllBytes(written[1]), "enemy_box_walk.rfa");
                    ctx.Check(v3c.Bones.Length == 2 && rfa.BoneCount == 2 && Math.Abs(rfa.DurationSeconds - 0.533) < 0.001, $"the clip fits the mesh ({rfa.BoneCount} bones, {rfa.DurationSeconds * 1000:0} ms)");
                    // converting again: free names, the textures left as they are
                    var again = await ws.Legacy.ConvertDocumentAsync(chr, new LegacyConvertChoice(LegacyConvertTarget.Folder, outFolder, false));
                    ctx.Check(again is { Count: 2 } && again[0].EndsWith("enemy_box (2).v3c", StringComparison.Ordinal) && again[1].EndsWith("enemy_box_walk (2).rfa", StringComparison.Ordinal),
                        $"converting again gives free names ({string.Join(", ", (again ?? []).Select(Path.GetFileName))})");
                }
            }

            // a level opened here (no Levels module would take it): the tab says why
            if (!ctx.Shell.Modules.Any(m => !ReferenceEquals(m, module) && m.DocumentKinds.Any(k => k.Extensions.Contains(".xff"))))
            {
                var lvl = await Open(ctx, room, opened);
                ctx.Check(lvl is { Legacy.Mesh: null } && lvl.ConvertBannerText?.Contains("Levels module", StringComparison.Ordinal) == true, $"a level's tab says the Levels module opens it ({lvl?.ConvertBannerText})");
            }

            // the batch: the model and the character convert, the level is declined
            {
                ArchiveBatchEntry E(string name, byte[] bytes) => new(name, bytes.Length, () => bytes);
                var entries = new[] { E("crate.xff", model), E("enemy_box.xff", character), E("level_box.xff", level) };
                string batchOut = Path.Combine(folder, "batch");
                string? summary = await ws.Legacy.ConvertBatchAsync(new ArchiveBatchRequest("redfaction.cfl", null, entries)
                {
                    Interactive = false,
                    Options = new LegacyConvertChoice(LegacyConvertTarget.Folder, batchOut, false),
                    AllEntries = [.. entries, E(SyntheticXffMesh.Texture, TexturePcx)],
                });
                ctx.Check(ws.Legacy.LastBatch is { Converted.Count: 2, Failed: [var why] } && why.Contains("level_box.xff", StringComparison.Ordinal),
                    $"Convert meshes... converts the model and the character and leaves the level ({summary})");
                ctx.Check(File.Exists(Path.Combine(batchOut, "enemy_box_walk.rfa")) && File.Exists(Path.Combine(batchOut, TextureTga)) && !File.Exists(Path.Combine(batchOut, SyntheticXffMesh.Texture)),
                    "the batch writes the clip and the texture from the archive's entries");
            }

            // the packfile preview: the character plays its loop with the archive's texture
            {
                var window = PreviewTestKit.Host();
                try
                {
                    var siblings = AssetSiblings.FromMemory("redfaction.cfl", [new(SyntheticXffMesh.Texture, TexturePcx)]);
                    var preview = (RfaPreview?)module.CreatePreview(character, "enemy_box.xff", siblings);
                    if (ctx.Check(preview is not null, "the preview is created"))
                    {
                        window.Content = preview;
                        await preview!.Loading;
                        await PreviewTestKit.SettleAsync();
                        ctx.Check(preview.Message is null && preview.MeshName == "enemy_box.xff" && preview.Playback?.IsPlaying == true,
                            $"the preview shows the character and plays its loop ({preview.Message ?? preview.MeshName})");
                        int colours = PreviewTestKit.SavePng(preview, Path.Combine(PreviewTestKit.ShotsFolder(ctx), "rfa_xff_preview.png"));
                        ctx.Check(colours > 20, $"the preview capture is not blank ({colours} colours)");
                        window.Content = null;
                        preview.Dispose();
                    }
                }
                finally { window.Close(); }
            }

            // the Convert window: the scale and the copies, a bad scale refused
            {
                var window = new LegacyMeshConvertWindow(ctx.Shell.Dialogs, [new LegacyConvertItem("enemy_box.xff", "enemy_box.v3c", [], IsXff: true, ClipName: "enemy_box_walk.rfa")],
                    null, folder, new LegacyConvertChoice(LegacyConvertTarget.NextToSource, null, false));
                ctx.Check(window.OffersXff && window.Current() is { XffScale: XffSpace.RfScale, XffWriteTextures: true }, "the window starts at 2.35 with the copies on");
                ctx.Check(window.Plan.Contains("enemy_box.v3c and enemy_box_walk.rfa", StringComparison.Ordinal), $"the plan names the clip: {window.Plan}");
                window.SetXffScale("abc");
                ctx.Check(window.Current() is null && window.Plan.StartsWith("Enter a scale", StringComparison.Ordinal), "a scale that is not a number is refused");
                window.SetXffScale("1.5");
                ctx.Check(window.Current() is { XffScale: 1.5f }, "another scale is taken");
                window.Close();
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (ws.Documents.Contains(d)) ws.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [SelfTest("rfa.xff-ngage")]
    public static async Task Ngage(ShellSelfTestContext ctx)
    {
        string? cfl = LocalPaths.NgageDirectory is { } dir ? Path.Combine(dir, "redfaction.cfl") : null;
        if (cfl is null || !File.Exists(cfl)) { ctx.Skip("no Red Faction for N-Gage archive (" + LocalPaths.HowToSet(LocalPaths.NgageDirectoryVariable, "ngageDirectory") + ")"); return; }
        var ws = RfaModule.Workspace;
        var provider = ctx.Shell.Modules.OfType<IWorkCopyProvider>().FirstOrDefault(p => p.CanOpenArchive(cfl));
        if (!ctx.Check(provider is not null, "a module opens entries of the .cfl")) return;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-xff-ngage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string shots = PreviewTestKit.ShotsFolder(ctx);
        var opened = new List<DocumentViewModel>();
        var before = ctx.Shell.Documents.ToList();
        try
        {
            ctx.Check(ctx.Shell.OpenFile(cfl), "the shell opens redfaction.cfl");
            await ctx.SettleAsync(TimeSpan.FromSeconds(60));

            var enemy = await OpenEntry(ctx, provider!, cfl, "enemy_ug.xff", opened);
            if (ctx.Check(enemy is { IsLegacy: true, Kind: DocumentKind.CharacterMesh, Legacy.Mesh: not null }, $"enemy_ug.xff opens from the archive as a character ({enemy?.ConvertBannerText})"))
            {
                ctx.Check(enemy!.Scene.Skeleton.Count == 6, $"6 bones ({string.Join(", ", enemy.Scene.Skeleton.Names)})");
                await CheckTexturesAsync(ctx, enemy);
                for (int i = 0; i < 100 && enemy.PreviewClip is null; i++) await Task.Delay(50);
                if (ctx.Check(enemy.PreviewClip is { } clip, "its loop plays in the tab"))
                {
                    enemy.Playback.Pause();
                    enemy.Playback.Seek(enemy.PreviewClip!.StartTime + enemy.PreviewClip.Duration * 0.4f);
                }
                await CaptureBothAsync(ctx, shots, "xff_enemy_ug");

                // the round trip: the converted .v3c with its .rfa opened in Cairn
                var written = await ws.Legacy.ConvertDocumentAsync(enemy, new LegacyConvertChoice(LegacyConvertTarget.Folder, folder, false));
                ctx.Check(written is { Count: >= 3 } && written.Take(2).Select(Path.GetFileName).SequenceEqual(["enemy_ug.v3c", "enemy_ug_walk.rfa"]) && written.Skip(2).Any(p => p.EndsWith("txtr_ultr_grd.tga", StringComparison.OrdinalIgnoreCase)),
                    $"Convert writes enemy_ug.v3c, its clip and its texture ({string.Join(", ", (written ?? []).Select(Path.GetFileName))})");
                if (written is { Count: >= 2 } && await Open(ctx, written[0], opened) is { } v3c)
                {
                    ctx.Check(!v3c.IsLegacy && v3c.Scene.Skeleton.Count == 6 && v3c.ErrorCount == 0, $"the .v3c opens with its 6 bones and no errors ({v3c.ErrorCount} errors, {v3c.WarningCount} warnings)");
                    await CheckTexturesAsync(ctx, v3c);
                    // the clip beside it plays by itself (a stock clip with as many bones would scatter the parts)
                    string clipName = Path.GetFileName(written[1]);
                    byte[] clipBytes = File.ReadAllBytes(written[1]);
                    for (int i = 0; i < 100 && v3c.PreviewClip?.Duration != RfaReader.Read(clipBytes, clipName).Duration; i++) await Task.Delay(50);
                    ctx.Check(v3c.PreviewClipOptions.Skip(1).FirstOrDefault() is { Group: "Beside this file" } beside && beside.Label == clipName,
                        $"the picker lists the clip beside the mesh first ({string.Join(", ", v3c.PreviewClipOptions.Take(3).Select(o => o.Label))})");
                    var rfa = v3c.PreviewClip;
                    v3c.Playback.Pause();
                    if (rfa is not null) v3c.Playback.Seek(rfa.StartTime + rfa.Duration * 0.4f);
                    ctx.Check(rfa is { BoneCount: 6 } && Math.Abs(rfa.DurationSeconds * 1000 - 533) < 1 && v3c.SelectedPreviewClip?.Label == clipName,
                        $"the clip plays on it ({rfa?.BoneCount} bones, {rfa?.DurationSeconds * 1000:0} ms, {v3c.SelectedPreviewClip?.Label})");
                    await CaptureBothAsync(ctx, shots, "xff_enemy_ug_converted");

                    // without its clip: the bind pose, though the library has stock clips with 6 bones (fp_jgun_fire)
                    string alone = Path.Combine(folder, "alone");
                    Directory.CreateDirectory(alone);
                    File.Copy(written[0], Path.Combine(alone, "enemy_ug.v3c"));
                    if (await Open(ctx, Path.Combine(alone, "enemy_ug.v3c"), opened) is { } bare)
                    {
                        await ctx.SettleAsync();
                        ctx.Check(bare.PreviewClip is null && bare.SelectedPreviewClip?.Clip is null,
                            $"without its clip the converted .v3c starts in the bind pose ({bare.SelectedPreviewClip?.Label})");
                    }
                }
            }

            var pack = await OpenEntry(ctx, provider!, cfl, "item_healthpack.xff", opened);
            if (ctx.Check(pack is { IsLegacy: true, Kind: DocumentKind.StaticMesh, Legacy.Mesh: not null }, $"item_healthpack.xff opens from the archive as a model ({pack?.ConvertBannerText})"))
            {
                await CheckTexturesAsync(ctx, pack!);
                await CaptureBothAsync(ctx, shots, "xff_item_healthpack");
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (ws.Documents.Contains(d)) ws.CloseDiscarding(d);
            foreach (var d in ctx.Shell.Documents.Except(before).ToList()) ctx.Shell.Close(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Every texture the tab's mesh names resolves.</summary>
    private static async Task CheckTexturesAsync(ShellSelfTestContext ctx, MeshDocumentViewModel doc)
    {
        var names = doc.Current.Submeshes.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var resolver = doc.Resolver;
        var missing = await Task.Run(() => names.Where(n => resolver.Resolve(n) is null).ToList());
        ctx.Check(names.Count > 0 && missing.Count == 0, $"{doc.DisplayName}: its {names.Count} textures resolve ({string.Join(", ", names)}; missing: {string.Join(", ", missing)})");
        // none draws see-through everywhere (the N-Gage 16-bit textures declare an alpha bit they never set)
        var clear = await Task.Run(() => names.Where(n => resolver.Resolve(n) is { } at && ImageDecoder.Decode(at.ReadAllBytes(), n) is var image
            && Enumerable.Range(0, image.Width * image.Height).All(i => image.Pixels[i * 4 + 3] == 0)).ToList());
        ctx.Check(clear.Count == 0, $"{doc.DisplayName}: no texture is fully see-through ({string.Join(", ", clear)})");
    }

    /// <summary>The main window in the dark and the light theme, the tab in front framed from the front and a little above.</summary>
    private static async Task CaptureBothAsync(ShellSelfTestContext ctx, string shots, string name)
    {
        var theme = ctx.Shell.Theme;
        var before = theme.Requested;
        if (RfaModule.Workspace.ActiveDocument is { } doc)
        {
            doc.Scene.RequestFrame(false);
            doc.Scene.Camera.SetView(25, 15);
        }
        try
        {
            foreach (bool light in new[] { false, true })
            {
                theme.Apply(light ? AppTheme.Light : AppTheme.Dark);
                await PreviewTestKit.SettleAsync();
                string path = Path.Combine(shots, $"{name}_{(light ? "light" : "dark")}.png");
                int colours = PreviewTestKit.SavePng((FrameworkElement)ctx.MainWindow.Content, path);
                ctx.Check(colours > 50, $"{Path.GetFileName(path)}: the capture is not blank ({colours} colours)");
            }
        }
        finally { theme.Apply(before); }
    }

    private static async Task<MeshDocumentViewModel?> OpenEntry(ShellSelfTestContext ctx, IWorkCopyProvider provider, string archive, string entry, List<DocumentViewModel> opened)
    {
        if (!provider.OpenArchiveEntry(archive, entry)) return null;
        var ws = RfaModule.Workspace;
        for (int i = 0; i < 300 && ws.ActiveDocument?.DisplayName.Equals(entry, StringComparison.OrdinalIgnoreCase) != true; i++) await Task.Delay(100);
        await ctx.SettleAsync();
        if (ws.ActiveDocument is not MeshDocumentViewModel doc || !doc.DisplayName.Equals(entry, StringComparison.OrdinalIgnoreCase)) return null;
        opened.Add(doc);
        return doc;
    }

    private static async Task<MeshDocumentViewModel?> Open(ShellSelfTestContext ctx, string path, List<DocumentViewModel> opened)
    {
        bool ok = ctx.Shell.OpenFile(path);
        await ctx.SettleAsync();
        if (!ok || RfaModule.Workspace.ActiveDocument is not MeshDocumentViewModel doc) return null;
        opened.Add(doc);
        return doc;
    }

    /// <summary><c>--dialog rfa.xff-convert</c>: the Convert window for an N-Gage character, with the scale, the texture copies and the report.</summary>
    [ScreenshotDialog("rfa.xff-convert")]
    public static Window? ConvertDialog(ShellScreenshotContext ctx)
    {
        LegacyMeshSupport.EnsureRegistered();
        var conversion = LegacyMeshSupport.Convert(SyntheticXffMesh.SampleCharacter(), "enemy_box.xff", name => name == SyntheticXffMesh.Texture ? TexturePcx : null);
        return new LegacyMeshConvertWindow(ctx.Shell.Dialogs,
            [new LegacyConvertItem("enemy_box.xff", conversion.OutputName, conversion.Report, IsXff: true, ClipName: conversion.Clip?.OutputName)],
            null, Path.Combine(Path.GetTempPath(), "meshes"), new LegacyConvertChoice(LegacyConvertTarget.NextToSource, null, false))
        {
            Owner = ctx.MainWindow,
        };
    }
}
