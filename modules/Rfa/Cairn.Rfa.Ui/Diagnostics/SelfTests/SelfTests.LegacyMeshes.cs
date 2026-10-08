using System.Windows;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Views.Dialogs;
using ShellSelfTestContext = Cairn.Ui.Diagnostics.SelfTestContext;
using ShellScreenshotContext = Cairn.Ui.Diagnostics.ScreenshotContext;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// The meshes Cairn reads and converts but never saves (.v3d, .vcm, .rfm, .rfc): each opens in a read-only tab with
/// the convert banner, a character shows its skeleton, edits are refused, a file that cannot be read opens with the
/// reason in Problems, and Convert writes the .v3m/.v3c. Synthetic files under %TEMP% (and, when configured, one PS2
/// mesh of each kind copied out of the PS2 demo's packfile).
/// </summary>
internal static class LegacyMeshSelfTests
{
    [SelfTest("rfa.legacy-meshes")]
    public static async Task LegacyMeshes(ShellSelfTestContext ctx)
    {
        var ws = RfaModule.Workspace;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-legacy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<DocumentViewModel>();
        try
        {
            string box = Path.Combine(folder, "box.v3d"), guard = Path.Combine(folder, "guard.vcm"), rf2 = Path.Combine(folder, "rf2_thing.rfm");
            File.WriteAllBytes(box, SyntheticExporterMesh.SampleStatic());
            File.WriteAllBytes(guard, SyntheticExporterMesh.SampleCharacter());
            // A Red Faction II mesh header (same magic, version 0x114): refused with a reason.
            File.WriteAllBytes(rf2, [0x12, 0x87, 0x12, 0x87, 0x14, 0x01, 0, 0, .. new byte[56]]);

            // .v3d: a read-only tab with the convert banner
            var v3d = await Open(ctx, box, opened);
            if (ctx.Check(v3d is { IsLegacy: true }, "box.v3d opens as a read-only exporter mesh tab"))
            {
                ctx.Check(v3d!.IsReadOnly && !v3d.ShowsReadOnlyBanner, "read-only, without the .v3m banner");
                ctx.Check(v3d.ConvertBannerText == "Exporter static mesh (.v3d) is read-only in Cairn. Convert to .v3m to use it in the PC game.", $"banner: {v3d.ConvertBannerText}");
                var kind = ((IDocument)v3d).Kind;
                ctx.Check(kind.Id == RfaModule.LegacyMeshKind.Id && kind.Extensions[0] == ".v3m", "the shell sees the legacy mesh kind; Save As offers the converted .v3m");
                ctx.Check(v3d.Current.Submeshes.Single().Lods.Length == 2 && v3d.Kind == DocumentKind.StaticMesh, "the tab shows the converted mesh (one submesh, two LODs)");
                ctx.Check(v3d.Diagnostics.Any(d => d.Code == LegacyMeshSource.NoteCode), "Problems lists what converting approximates");
                ctx.Check(v3d.ConvertCommand?.CanExecute(null) == true, "Convert is offered");
                await ctx.SettleAsync();
                var banner = (v3d.View as FrameworkElement)?.FindName("ConvertBanner") as FrameworkElement;
                ctx.Check(banner?.Visibility == Visibility.Visible, "the convert banner shows in the tab");
                ctx.Check(!v3d.Apply("Rename", m => m with { EndSizeField = 1 }), "edits are refused");

                string outFolder = Path.Combine(folder, "out");
                var first = await ws.Legacy.ConvertDocumentAsync(v3d, new LegacyConvertChoice(LegacyConvertTarget.Folder, outFolder, false));
                ctx.Check(first is [var p] && p.EndsWith("box.v3m", StringComparison.Ordinal) && V3dReader.ReadFile(p).Submeshes.Single().Lods.Length == 2,
                    $"Convert writes box.v3m that reads back ({first?.FirstOrDefault()})");
                var second = await ws.Legacy.ConvertDocumentAsync(v3d, new LegacyConvertChoice(LegacyConvertTarget.Folder, outFolder, false));
                ctx.Check(second is [var q] && q.EndsWith("box (2).v3m", StringComparison.Ordinal), $"a taken name gets a free one ({second?.FirstOrDefault()})");
                var third = await ws.Legacy.ConvertDocumentAsync(v3d, new LegacyConvertChoice(LegacyConvertTarget.NextToSource, folder, true));
                ctx.Check(third is [var r] && File.Exists(Path.Combine(folder, "box.v3m")) && r.EndsWith("box.v3m", StringComparison.Ordinal), "next to the source");
                ctx.Check(File.ReadAllBytes(box).AsSpan().SequenceEqual(SyntheticExporterMesh.SampleStatic()), "the source is left as it was");
                // never over the exporter file it was read from
                bool refused = false;
                try { ((IDocument)v3d).SaveTo(box); }
                catch (InvalidOperationException) { refused = true; }
                ctx.Check(refused && v3d.IsLegacy && File.ReadAllBytes(box).AsSpan().SequenceEqual(SyntheticExporterMesh.SampleStatic()),
                    "Save As over the source .v3d is refused and the source is unchanged");
                ctx.Check(((IDocument)v3d).CanSave, "a readable legacy tab can be saved as .v3m");
                // Save As writes the converted mesh; the tab is that .v3m from then on.
                string saved = Path.Combine(folder, "saved.v3m");
                ((IDocument)v3d).SaveTo(saved);
                ctx.Check(!v3d.IsLegacy && v3d.FilePath == saved && v3d.ConvertBannerText is null && V3dReader.ReadFile(saved).Submeshes.Any(),
                    "Save As writes the .v3m and the tab becomes it");
            }

            // .vcm: a character with its skeleton
            var vcm = await Open(ctx, guard, opened);
            if (ctx.Check(vcm is { IsLegacy: true, Kind: DocumentKind.CharacterMesh }, "guard.vcm opens as a character"))
            {
                ctx.Check(vcm!.HasSkeleton && vcm.Scene.Skeleton.Count == 2, $"its skeleton shows ({vcm.Scene.Skeleton.Count} bones)");
                ctx.Check(vcm.IsReadOnly && vcm.ConvertBannerText?.Contains("Convert to .v3c", StringComparison.Ordinal) == true, $"read-only, converts to .v3c ({vcm.ConvertBannerText})");
                ctx.Check(vcm.Current.Bones[0].Name.Text == "root-bdbn-pelvis", "bone names as the compiler wrote them");
                // "next to the source" is never the game directory (the user picks a folder instead)
                string? game = ws.Settings.GameDirectory;
                try
                {
                    ctx.Check(string.Equals(ws.Legacy.NextToFolder(vcm), folder, StringComparison.OrdinalIgnoreCase),"next to the source: the file's folder");
                    ws.Settings.GameDirectory = folder;
                    ctx.Check(ws.Legacy.NextToFolder(vcm) is null, "next to the source is off when the source is in the game directory");
                }
                finally { ws.Settings.GameDirectory = game; }
            }

            // opened from bytes (a packfile entry), closed, reopened: still the read-only exporter mesh
            var fromBytes = (MeshDocumentViewModel)RfaModule.LegacyMeshKind.OpenBytes(SyntheticExporterMesh.SampleStatic(), "crate.v3d", "crate.v3d in test.vpp");
            ctx.Shell.AddDocument(fromBytes);
            opened.Add(fromBytes);
            await ctx.SettleAsync();
            byte[]? captured = ((IDocument)fromBytes).CaptureRecovery();
            ctx.Check(captured is not null && captured.AsSpan().SequenceEqual(SyntheticExporterMesh.SampleStatic()), "a closed legacy tab is kept as the file it showed");
            if (captured is not null)
            {
                var reopened = (DocumentViewModel)RfaModule.LegacyMeshKind.Restore(new RecoverySnapshot(Guid.NewGuid().ToString("N"), null, fromBytes.DisplayName, DateTime.UtcNow, RfaModule.LegacyMeshKind.Id, captured));
                ctx.Check(reopened is MeshDocumentViewModel { IsLegacy: true, IsReadOnly: true, IsDirty: false } m && m.Current.Submeshes.Any(),
                    $"reopening it gives the read-only exporter mesh tab again ({reopened.GetType().Name}, legacy {(reopened as MeshDocumentViewModel)?.IsLegacy})");
                reopened.Dispose();
            }

            // packfile batch: a PS2 mesh converts from its exporter twin anywhere in the packfile (not only the selection),
            // and two selected entries of one name do not break the batch
            {
                byte[] fakePs2 = [0x12, 0x87, 0x12, 0x87, 0, 0, 0, 0, 1, 0, 0, 0];
                ArchiveBatchEntry E(string name, byte[] bytes) => new(name, bytes.Length, () => bytes);
                var twin = E("box.v3d", SyntheticExporterMesh.SampleStatic());
                var selected = new[] { E("box.rfm", fakePs2), E("crate.v3d", SyntheticExporterMesh.SampleStatic()), E("crate.v3d", SyntheticExporterMesh.SampleStatic()) };
                string batchOut = Path.Combine(folder, "batch");
                string? summary = await ws.Legacy.ConvertBatchAsync(new ArchiveBatchRequest("test.vpp", null, selected)
                {
                    Interactive = false,
                    Options = new LegacyConvertChoice(LegacyConvertTarget.Folder, batchOut, false),
                    AllEntries = [twin, .. selected],
                });
                var last = ws.Legacy.LastBatch;
                ctx.Check(last is not null && last.Converted.Any(c => c.SourceName == "box.rfm" && c.TwinName == "box.v3d"),
                    $"box.rfm converts from box.v3d, which is in the packfile but not selected ({summary})");
                ctx.Check(last is not null && last.Converted.Count(c => c.OutputName == "crate.v3m") == 1 && last.Failed.Any(f => f.StartsWith("crate.v3d", StringComparison.Ordinal)),
                    "two selected entries of one name: one converts, the other is reported, the batch goes on");
            }

            // a file that cannot be read
            var bad = await Open(ctx, rf2, opened);
            if (ctx.Check(bad is { IsLegacy: true }, "a Red Faction II .rfm still opens a tab"))
            {
                ctx.Check(bad!.Diagnostics.Any(d => d.Code == LegacyMeshSource.UnreadableCode && d.Message.Contains("Red Faction II", StringComparison.Ordinal)),
                    "Problems says why: " + bad.Diagnostics.FirstOrDefault()?.Message);
                ctx.Check(bad.ConvertCommand?.CanExecute(null) == false && bad.ConvertBannerText?.Contains("could not be read", StringComparison.Ordinal) == true, "nothing to convert; the banner says so");
                // nothing to save either: an empty .v3m is not a conversion
                ctx.Check(!((IDocument)bad).CanSave && !ctx.Shell.Save(bad) && !ctx.Shell.SaveAs(bad), "an unreadable legacy tab cannot be saved (Save and Save As are off)");
                ctx.Check(bad.Diagnostics.Any(d => d.Code == LegacyMeshSource.UnreadableCode && d.Help.Contains("cannot be saved", StringComparison.Ordinal)),
                    "Problems says it cannot be saved");
                string empty = Path.Combine(folder, "rf2_thing.v3m");
                bool refused = false;
                try { ((IDocument)bad).SaveTo(empty); }
                catch (InvalidOperationException) { refused = true; }
                ctx.Check(refused && !File.Exists(empty) && bad.IsLegacy, "SaveTo is refused and writes nothing");
            }

            // real PS2 meshes (when the PS2 demo is configured)
            if (LocalPaths.Ps2Directory is { } ps2 && File.Exists(Path.Combine(ps2, "RF_PS2.VPP")))
            {
                var archive = VppArchive.Open(Path.Combine(ps2, "RF_PS2.VPP"));
                foreach (string ext in new[] { ".rfm", ".rfc" })
                {
                    if (archive.Entries.FirstOrDefault(e => e.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) is not { } entry) continue;
                    string path = Path.Combine(folder, entry.Name);
                    File.WriteAllBytes(path, archive.ReadEntry(entry));
                    var doc = await Open(ctx, path, opened);
                    ctx.Check(doc is { IsLegacy: true, Legacy.Mesh: not null } && doc.IsReadOnly && doc.Current.Submeshes.Any(),
                        $"{entry.Name} (PS2) opens read-only with its geometry ({doc?.ConvertBannerText})");
                }
            }
            else ctx.Log("  (no PS2 folder configured: the .rfm/.rfc tabs were not tried)");
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

    /// <summary><c>--dialog rfa.legacy-convert</c>: the Convert window for one exporter mesh, with its report.</summary>
    [ScreenshotDialog("rfa.legacy-convert")]
    public static Window? ConvertDialog(ShellScreenshotContext ctx)
    {
        LegacyMeshSupport.EnsureRegistered();
        var conversion = LegacyMeshSupport.Convert(SyntheticExporterMesh.SampleStatic(), "barrel_debris.v3d");
        return new LegacyMeshConvertWindow(ctx.Shell.Dialogs, [new LegacyConvertItem("barrel_debris.v3d", conversion.OutputName, conversion.Report)],
            "RF_PS2.VPP", Path.Combine(Path.GetTempPath(), "meshes"), new LegacyConvertChoice(LegacyConvertTarget.IntoPackfile, null, false))
        {
            Owner = ctx.MainWindow,
        };
    }
}
