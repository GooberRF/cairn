using System.Windows;
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
/// Red Faction II clips (.rfa version 9): a clip opens read-only (no Save, a Convert banner, the conversion notes in
/// Problems) and plays on the Red Faction II character beside it, converted on the fly; File › Convert to lists a Red
/// Faction clip and writes a version 8 clip that reads back, never over the file the tab shows; a closed tab reopens as
/// the same read-only clip; a packfile preview finds the character among the entries; the packfile batch converts the
/// Red Faction II clips and leaves Red Faction clips alone. Synthetic files under %TEMP%, and (when configured) a real
/// clip from the Red Faction II install, read in place.
/// </summary>
internal static class Rf2ClipSelfTests
{
    [SelfTest("rfa.rf2-clips")]
    public static async Task Rf2Clips(ShellSelfTestContext ctx)
    {
        var ws = RfaModule.Workspace;
        var module = ctx.Shell.Modules.OfType<RfaModule>().First();
        string folder = Path.Combine(Path.GetTempPath(), "cairn-rf2clip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<DocumentViewModel>();
        try
        {
            byte[] clipBytes = SyntheticRf2Clip.SampleSwing();
            string clipPath = Path.Combine(folder, "guard_swing.rfa");
            File.WriteAllBytes(clipPath, clipBytes);
            File.WriteAllBytes(Path.Combine(folder, "guard.rfc"), SyntheticRf2Mesh.SampleCharacter());
            File.WriteAllBytes(Path.Combine(folder, "guard_vcm.peg"), SyntheticRf2Mesh.SamplePeg(("guard_body.tga", 8, 8, 0xFF40C040)));

            // the tab: read-only, the convert banner, notes in Problems, the character beside it
            ctx.Check(ctx.Shell.OpenFile(clipPath), "the shell opens a Red Faction II clip");
            await ctx.SettleAsync();
            var doc = ws.ActiveDocument as ClipDocumentViewModel;
            if (!ctx.Check(doc is { IsRedFactionII: true }, "... as a Red Faction II clip tab")) return;
            opened.Add(doc!);
            ctx.Check(doc!.IsReadOnly && !((IDocument)doc).CanSave && !ctx.Shell.Save(doc) && !ctx.Shell.SaveAs(doc), "read-only: Save and Save As are off");
            ctx.Check(doc.ConvertBannerText?.Contains("Red Faction II clip", StringComparison.Ordinal) == true && doc.ConvertCommand?.CanExecute(null) == true,
                $"the banner offers Convert ({doc.ConvertBannerText})");
            ctx.Check(!doc.ShowsReadOnlyBanner, "no plain read-only banner on top of it");
            ctx.Check(doc.Diagnostics.Any(d => d.Code == LegacyMeshSource.NoteCode && d.Message.Contains("without eases", StringComparison.Ordinal)),
                "Problems lists what the conversion approximates");
            ctx.Check(doc.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error), "no errors: " + string.Join(" | ", doc.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)));
            ctx.Check(!doc.Apply("probe", c => c with { RampIn = 0 }) && doc.Current.RampIn == 480, "an edit is refused");
            await WaitForMesh(ctx, doc);
            ctx.Check(doc.Rf2PreviewMeshName == "guard.rfc" && doc.FittingSkeleton?.Count == 2, $"it plays on guard.rfc beside it ({doc.Scene.MeshName}, {doc.Scene.Skeleton.Count} bones)");
            ctx.Check(doc.Scene.Notice is null, $"no notice ({doc.Scene.Notice})");
            ctx.Check(doc.Scene.TextureResolver?.Resolve("guard_body.tga") is not null, "its texture resolves from guard_vcm.peg");
            ctx.Check(doc.FileSizeText.StartsWith(clipBytes.Length.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal), $"the size is the file's ({doc.FileSizeText})");

            // File › Convert to: a Red Faction clip; written into a folder and read back as version 8
            var choices = AssetConversions.TargetsFor(ctx.Shell, doc);
            var rfa = choices.FirstOrDefault(c => c.Target.Extension == ".rfa");
            ctx.Check(rfa is { Target.Lossy: true } && choices.Any(c => c.Target.Extension == ".glb"), "Convert to lists a Red Faction clip (lossy) and glTF: " + string.Join(", ", choices.Select(c => c.Target.DisplayName)));
            ctx.Check(module.TargetsFor("guard_swing.rfa").All(t => t.Extension != ".rfa"), "a .rfa known only by its name (a Red Faction clip) is not offered .rfa");
            if (rfa is not null)
            {
                string outFolder = Path.Combine(folder, "out");
                var request = AssetConversions.RequestFor(ctx.Shell, doc, ".rfa", interactive: false) with { OutputFolder = outFolder };
                string? first = await AssetConversions.RunAsync(ctx.Shell, rfa, request);
                string written = Path.Combine(outFolder, "guard_swing.rfa");
                ctx.Check(first is not null && File.Exists(written) && RfaReader.ReadFile(written) is { Version: 8, BoneCount: 2 },
                    $"Convert to writes a version 8 clip ({first})");
                ctx.Check(File.ReadAllBytes(clipPath).AsSpan().SequenceEqual(clipBytes), "the source is untouched");
            }
            // next to the source, even replacing, the source is never overwritten
            var nextTo = await ws.Legacy.ConvertClipDocumentAsync(doc, new LegacyConvertChoice(LegacyConvertTarget.Folder, folder, Replace: true));
            ctx.Check(nextTo is [var beside] && beside.EndsWith("guard_swing (2).rfa", StringComparison.Ordinal) && File.ReadAllBytes(clipPath).AsSpan().SequenceEqual(clipBytes),
                $"converting next to the source gives the clip a free name ({string.Join(", ", (nextTo ?? []).Select(Path.GetFileName))})");

            // a closed tab: captured as the file it showed, reopened read-only and clean
            byte[]? captured = ((IDocument)doc).CaptureRecovery();
            ctx.Check(captured is not null && captured.AsSpan().SequenceEqual(clipBytes), "a closed tab is kept as the file it showed");
            if (captured is not null)
            {
                var reopened = (DocumentViewModel)RfaModule.ClipKind.Restore(new RecoverySnapshot(Guid.NewGuid().ToString("N"), null, doc.DisplayName, DateTime.UtcNow, RfaModule.ClipKind.Id, captured));
                ctx.Check(reopened is ClipDocumentViewModel { IsRedFactionII: true, IsReadOnly: true, IsDirty: false }, "reopening it gives the read-only clip again");
                reopened.Dispose();
            }

            // a packfile preview: the character among the entries, converted on the fly
            {
                var siblings = AssetSiblings.FromMemory("rf2.packfile", [new("guard.rfc", SyntheticRf2Mesh.SampleCharacter()), new("crate.rfm", SyntheticRf2Mesh.SampleStatic()),
                    new("guard_vcm.peg", SyntheticRf2Mesh.SamplePeg(("guard_body.tga", 8, 8, 0xFF40C040)))]);
                var window = PreviewTestKit.Host();
                try
                {
                    var preview = (RfaPreview?)module.CreatePreview(clipBytes, "guard_swing.rfa", siblings);
                    if (ctx.Check(preview is not null, "the packfile preview takes a Red Faction II clip"))
                    {
                        window.Content = preview;
                        await preview!.Loading;
                        var wait = await Task.WhenAny(preview.MeshLoading, Task.Delay(30_000));
                        await PreviewTestKit.SettleAsync();
                        ctx.Check(wait == preview.MeshLoading && preview.MeshName == "guard.rfc" && preview.Notice is null,
                            $"... and plays it on guard.rfc from the entries ({preview.MeshName}; {preview.Notice ?? "no notice"})");
                        ctx.Check(preview.Playback?.IsPlaying == true, "... playing");
                        window.Content = null;
                        preview.Dispose();
                    }
                }
                finally { window.Close(); }
            }

            // a packfile preview without a Red Faction II character: no Red Faction mesh stands in
            {
                var siblings = AssetSiblings.FromMemory("rf2.packfile", [new("crate.rfm", SyntheticRf2Mesh.SampleStatic())]);
                var window = PreviewTestKit.Host();
                try
                {
                    var preview = (RfaPreview?)module.CreatePreview(clipBytes, "guard_swing.rfa", siblings);
                    if (ctx.Check(preview is not null, "the packfile preview takes a Red Faction II clip without a character beside it"))
                    {
                        window.Content = preview;
                        await preview!.Loading;
                        var wait = await Task.WhenAny(preview.MeshLoading, Task.Delay(30_000));
                        await PreviewTestKit.SettleAsync();
                        ctx.Check(wait == preview.MeshLoading && preview.MeshName is null && preview.Notice?.Contains("No Red Faction II character") == true,
                            $"... plays on no mesh and says so ({preview.MeshName ?? "no mesh"}; {preview.Notice ?? "no notice"})");
                        window.Content = null;
                        preview.Dispose();
                    }
                }
                finally { window.Close(); }
            }

            // the packfile batch: Red Faction II clips and meshes convert; a Red Faction clip is left alone
            {
                ArchiveBatchEntry E(string name, byte[] bytes) => new(name, bytes.Length, () => bytes);
                byte[] rf1 = RfaWriter.Write(RfaReader.Read(clipBytes, "x.rfa") with { Version = 8 });
                var selected = new[] { E("guard_swing.rfa", clipBytes), E("walk.rfa", rf1), E("guard.rfc", SyntheticRf2Mesh.SampleCharacter()) };
                ctx.Check(selected.All(e => module.CanConvert(e.Name)), "the batch takes .rfa entries by name");
                string batchOut = Path.Combine(folder, "batch");
                string? summary = await ws.Legacy.ConvertBatchAsync(new ArchiveBatchRequest("rf2.packfile", null, selected)
                {
                    Interactive = false,
                    Options = new LegacyConvertChoice(LegacyConvertTarget.Folder, batchOut, false),
                    AllEntries = selected,
                });
                ctx.Check(ws.Legacy.LastBatch is { Clips: [{ OutputName: "guard_swing.rfa" }], Converted: [{ OutputName: "guard.v3c" }] }
                    && RfaReader.ReadFile(Path.Combine(batchOut, "guard_swing.rfa")).Version == 8 && !File.Exists(Path.Combine(batchOut, "walk.rfa")),
                    $"the batch converts the RF2 clip and mesh and leaves the Red Faction clip ({summary})");
                ctx.Check(summary?.Contains("meshes and animations", StringComparison.Ordinal) == true, "the summary names both");
                ctx.Check(module.CommandText.Contains("animations", StringComparison.Ordinal), $"the command says so ({module.CommandText})");
            }

            // a real clip from the install (read in place, never written): it plays on a Red Faction II character
            if (LocalPaths.Rf2GameDirectory is { } rf2 && Path.Combine(rf2, "pc_media", "Animations_2", "medm_hrfl_run.rfa") is var real && File.Exists(real))
            {
                ctx.Check(ctx.Shell.OpenFile(real), "a Red Faction II clip of the install opens");
                await ctx.SettleAsync();
                if (ws.ActiveDocument is ClipDocumentViewModel { IsRedFactionII: true } run)
                {
                    opened.Add(run);
                    await WaitForMesh(ctx, run);
                    ctx.Check(run.Rf2PreviewMeshName?.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase) == true && run.FittingSkeleton?.Count == 22,
                        $"it plays on a Red Faction II character of the install ({run.Scene.MeshName}, {run.Scene.Skeleton.Count} bones)");
                    ctx.Check(run.Scene.Notice is null, $"no notice ({run.Scene.Notice})");
                }
                else ctx.Check(false, "... as a Red Faction II clip tab");
            }
            else ctx.Log("  (no Red Faction II install configured: no real clip was tried)");
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (ws.Documents.Contains(d)) ws.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A clean clip tab whose file is replaced on disk by a Red Faction II clip, then by a Red Faction one again: the tab
    /// follows each time (the Version list, read-only, the convert banner and its command, the preview mesh chosen again).
    /// </summary>
    [SelfTest("rfa.rf2-clip-reload")]
    public static async Task Rf2ClipReload(ShellSelfTestContext ctx)
    {
        var ws = RfaModule.Workspace;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-rf2reload-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<DocumentViewModel>();
        try
        {
            byte[] rf2 = SyntheticRf2Clip.SampleSwing();
            byte[] rf1 = RfaWriter.Write(RfaReader.Read(rf2, "x.rfa") with { Version = 8 });
            string path = Path.Combine(folder, "guard_swing.rfa");
            File.WriteAllBytes(path, rf1);
            File.WriteAllBytes(Path.Combine(folder, "guard.rfc"), SyntheticRf2Mesh.SampleCharacter());

            ctx.Check(ctx.Shell.OpenFile(path), "the Red Faction clip opens");
            await ctx.SettleAsync();
            if (!ctx.Check(ws.ActiveDocument is ClipDocumentViewModel { IsRedFactionII: false }, "... as a Red Faction clip tab")) return;
            var doc = (ClipDocumentViewModel)ws.ActiveDocument!;
            opened.Add(doc);
            ctx.MainWindow.Activate();
            await ctx.SettleAsync();
            ctx.Check(!doc.IsReadOnly && doc.ConvertBannerText is null && doc.Inspector.Versions.SequenceEqual([7, 8]), "editable, no convert banner, versions 7 and 8");
            var raised = new List<string>();
            int convertCanExecute = 0;
            doc.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            doc.Inspector.PropertyChanged += (_, e) => raised.Add("Inspector." + e.PropertyName);
            doc.ConvertRf2Command.CanExecuteChanged += (_, _) => convertCanExecute++;

            // Red Faction -> Red Faction II
            await ReplaceOnDisk(ctx, doc, path, rf2, wantRf2: true);
            if (!ctx.Check(doc.IsRedFactionII && !doc.IsDirty, "the tab now holds the Red Faction II clip, clean")) return;
            ctx.Check(raised.Contains("Inspector." + nameof(ClipInspectorViewModel.Versions)) && doc.Inspector.Versions.Contains(RfaClip.Rf2Version),
                $"Versions is raised and lists 9 ({string.Join(",", doc.Inspector.Versions)})");
            foreach (string name in new[] { nameof(DocumentViewModel.IsReadOnly), nameof(DocumentViewModel.ShowsReadOnlyBanner), nameof(DocumentViewModel.ConvertBannerText), nameof(DocumentViewModel.ConvertCommand) })
                ctx.Check(raised.Contains(name), $"{name} is raised");
            ctx.Check(doc.IsReadOnly && !doc.ShowsReadOnlyBanner && doc.ConvertBannerText is not null && doc.ConvertCommand?.CanExecute(null) == true && convertCanExecute > 0,
                "read-only with the convert banner, and its Convert can run (CanExecuteChanged raised)");
            ctx.Check(!((IDocument)doc).CanSave, "Save is off");
            await WaitForMesh(ctx, doc, m => m == "guard.rfc");
            ctx.Check(doc.Rf2PreviewMeshName == "guard.rfc", $"the preview mesh is chosen again: the Red Faction II character beside it ({doc.Scene.MeshName})");
            ctx.Check(VersionCombo(ctx.MainWindow).Contains("selected '9'", StringComparison.Ordinal), "the Version box shows 9: " + VersionCombo(ctx.MainWindow));

            // Red Faction II -> Red Faction
            raised.Clear();
            convertCanExecute = 0;
            await ReplaceOnDisk(ctx, doc, path, rf1, wantRf2: false);
            if (!ctx.Check(!doc.IsRedFactionII && !doc.IsDirty, "the tab holds the Red Faction clip again, clean")) return;
            ctx.Check(raised.Contains("Inspector." + nameof(ClipInspectorViewModel.Versions)) && doc.Inspector.Versions.SequenceEqual([7, 8]),
                $"Versions is raised and lists 7 and 8 ({string.Join(",", doc.Inspector.Versions)})");
            foreach (string name in new[] { nameof(DocumentViewModel.IsReadOnly), nameof(DocumentViewModel.ConvertBannerText), nameof(DocumentViewModel.ConvertCommand) })
                ctx.Check(raised.Contains(name), $"{name} is raised");
            ctx.Check(!doc.IsReadOnly && doc.ConvertBannerText is null && doc.ConvertCommand is null && convertCanExecute > 0 && ((IDocument)doc).CanSave,
                "editable again: no banner, no Convert, Save is on");
            await WaitForMesh(ctx, doc, m => m != "guard.rfc");
            ctx.Check(doc.Scene.MeshName != "guard.rfc", $"the Red Faction II character is no longer the preview mesh ({doc.Scene.MeshName ?? "none"}; {doc.Scene.Notice ?? "no notice"})");
            ctx.Check(VersionCombo(ctx.MainWindow) is var combo && combo.Contains("items [7,8] selected '8' enabled True", StringComparison.Ordinal), "the Version box offers 7 and 8 again: " + VersionCombo(ctx.MainWindow));
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (ws.Documents.Contains(d)) ws.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Writes <paramref name="bytes"/> over the tab's file and waits for the tab to take them (Reload when the watcher has not).</summary>
    private static async Task ReplaceOnDisk(ShellSelfTestContext ctx, ClipDocumentViewModel doc, string path, byte[] bytes, bool wantRf2)
    {
        File.WriteAllBytes(path, bytes);
        for (int i = 0; i < 50 && doc.IsRedFactionII != wantRf2; i++) await Task.Delay(100);
        if (doc.IsRedFactionII != wantRf2)
        {
            ctx.Log("  (the file watcher did not reload the tab in 5 s: Reload)");
            doc.ReloadCommand.Execute(null);
        }
        else ctx.Log("  (reloaded by the file watcher)");
        await ctx.SettleAsync();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>The visible Version box of the Clip inspector: its items, selection and whether it is enabled.</summary>
    private static string VersionCombo(Window window)
    {
        var box = Descendants<System.Windows.Controls.ComboBox>(window).FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Version" && b.IsVisible);
        return box is null ? "(no visible Version box)" : $"items [{string.Join(",", box.Items.Cast<object>())}] selected '{box.SelectedItem ?? "(none)"}' enabled {box.IsEnabled}";
    }

    /// <summary>
    /// A Red Faction II clip with no Red Faction II character beside it or in its install: no Red Faction mesh of the
    /// library is put under it (it would play mangled); a notice asks for one, and a mesh picked by hand still plays it.
    /// </summary>
    [SelfTest("rfa.rf2-clip-no-character")]
    public static async Task Rf2ClipNoCharacter(ShellSelfTestContext ctx)
    {
        var ws = RfaModule.Workspace;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-rf2alone-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<DocumentViewModel>();
        try
        {
            var clips = new List<string>();
            string synthetic = Path.Combine(folder, "guard_swing.rfa");
            File.WriteAllBytes(synthetic, SyntheticRf2Clip.SampleSwing());
            clips.Add(synthetic);
            // the clip the review found playing on fp_rmt_det.v3c, copied alone into the temporary folder
            if (LocalPaths.Rf2GameDirectory is { } rf2 && Path.Combine(rf2, "pc_media", "Animations_2", "medm_hrfl_run.rfa") is var real && File.Exists(real))
            {
                string copy = Path.Combine(folder, "medm_hrfl_run.rfa");
                File.Copy(real, copy);
                clips.Add(copy);
            }
            else ctx.Log("  (no Red Faction II install configured: the synthetic clip only)");

            foreach (string path in clips)
            {
                string name = Path.GetFileName(path);
                ctx.Check(ctx.Shell.OpenFile(path), $"{name} opens");
                await ctx.SettleAsync();
                if (ws.ActiveDocument is not ClipDocumentViewModel { IsRedFactionII: true } doc) { ctx.Check(false, $"{name}: a Red Faction II clip tab"); continue; }
                opened.Add(doc);
                for (int i = 0; i < 600 && doc.Scene.Notice?.StartsWith("Looking for", StringComparison.Ordinal) == true; i++) await Task.Delay(100);
                await ctx.SettleAsync();
                ctx.Check(doc.PreviewMesh is null && doc.Scene.MeshName is null, $"{name}: no Red Faction mesh of the library is put under it ({doc.Scene.MeshName ?? "none"})");
                ctx.Check(doc.Scene.Notice?.Contains("No Red Faction II character found for this clip", StringComparison.Ordinal) == true && doc.Scene.NoticeIsWarning,
                    $"{name}: the viewport says so ({doc.Scene.Notice ?? "no notice"})");
                ctx.Log($"  {name}: {doc.PreviewMeshOptions.Count} meshes in the picker");

                // a mesh picked by hand still plays it
                if (doc.PreviewMeshOptions.Count > 0)
                {
                    var pick = doc.PreviewMeshOptions.FirstOrDefault(o => o.Fits) ?? doc.PreviewMeshOptions[0];
                    doc.SelectedPreviewMesh = pick;
                    await WaitForMesh(ctx, doc);
                    ctx.Check(doc.PreviewLibraryMesh?.Name == pick.Mesh.Name && doc.Scene.MeshName == pick.Mesh.Name
                        && doc.Scene.Notice?.Contains("No Red Faction II character", StringComparison.Ordinal) != true,
                        $"{name}: the mesh picked from Preview mesh plays it ({doc.Scene.MeshName}; {doc.Scene.Notice ?? "no notice"})");
                }
                else
                {
                    // no library: a mesh given in memory (what a retarget result previews on)
                    var mesh = V3dReader.Read(LegacyMeshSupport.Convert(SyntheticRf2Mesh.SampleCharacter(), "guard.rfc").Bytes, "guard.v3c");
                    doc.UsePreviewMesh(mesh, "guard.v3c", folder);
                    await ctx.SettleAsync();
                    ctx.Check(doc.Scene.MeshName == "guard.v3c" && doc.Scene.Notice?.Contains("No Red Faction II character", StringComparison.Ordinal) != true,
                        $"{name}: a mesh chosen by hand plays it ({doc.Scene.MeshName}; {doc.Scene.Notice ?? "no notice"})");
                }
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (ws.Documents.Contains(d)) ws.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Waits (up to a minute) until the clip's preview mesh is chosen.</summary>
    private static async Task WaitForMesh(ShellSelfTestContext ctx, ClipDocumentViewModel doc)
    {
        for (int i = 0; i < 600 && doc.PreviewMesh is null; i++) await Task.Delay(100);
        await ctx.SettleAsync();
    }

    /// <summary>Waits (up to 30 s) until the viewport's mesh name passes <paramref name="done"/>.</summary>
    private static async Task WaitForMesh(ShellSelfTestContext ctx, ClipDocumentViewModel doc, Func<string?, bool> done)
    {
        for (int i = 0; i < 300 && !done(doc.Scene.MeshName); i++) await Task.Delay(100);
        await ctx.SettleAsync();
    }

    /// <summary><c>--dialog rfa.rf2-clip-convert</c>: the Convert window for a Red Faction II clip, with its report.</summary>
    [ScreenshotDialog("rfa.rf2-clip-convert")]
    public static Window? ConvertDialog(ShellScreenshotContext ctx)
    {
        var conversion = Rf2AnimationConverter.Convert(SyntheticRf2Clip.SampleSwing(morph: true), "medm_talk.rfa");
        return new LegacyMeshConvertWindow(ctx.Shell.Dialogs, [new LegacyConvertItem("medm_talk.rfa", conversion.OutputName, conversion.Report, IsClip: true)],
            null, Path.Combine(Path.GetTempPath(), "animations"), new LegacyConvertChoice(LegacyConvertTarget.NextToSource, null, false))
        {
            Owner = ctx.MainWindow,
        };
    }
}
