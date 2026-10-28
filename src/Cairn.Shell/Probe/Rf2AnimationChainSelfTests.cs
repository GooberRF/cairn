#if CAIRN_MODULE_VPP && CAIRN_MODULE_RFA
using System.Windows;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Ui;
using Cairn.Rfa.Ui.Preview;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Views.Dialogs;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>
/// Red Faction II animations in their packfile group, end to end: All_Levels.toc_group opens read only; a clip's entry has
/// its type, Info line and facts, previews playing on a Red Faction II character from the group (converted on the fly),
/// lists a Red Faction clip under Convert to and converts into a folder without a dialog; Open in Cairn gives a read-only
/// clip tab that plays on a character of the group and cannot be saved; the batch converts clips and a character
/// together. Every output goes to a temporary folder; the install is only read.
/// </summary>
public static class Rf2AnimationChainSelfTests
{
    [SelfTest("integration.rf2-animation-chain")]
    public static async Task Rf2AnimationChain(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        string? toc = LocalPaths.Rf2GameDirectory is { } rf2 ? Path.Combine(rf2, "pc_media", "All_Levels.toc_group") : null;
        if (toc is null || !File.Exists(toc)) { ctx.Skip("no Red Faction II install (" + LocalPaths.HowToSet(LocalPaths.Rf2GameDirectoryVariable, "rf2GameDirectory") + ")"); return; }
        var vpp = shell.Modules.OfType<VppModule>().First();
        var rfa = shell.Modules.OfType<RfaModule>().First();
        string folder = Path.Combine(Path.GetTempPath(), "cairn-chain-rf2anim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<IDocument>();
        try
        {
            if (!ctx.Check(shell.OpenFile(toc) && shell.ActiveDocument is VppDocument { IsReadOnly: true }, "All_Levels.toc_group opens read only")) return;
            var doc = (VppDocument)shell.ActiveDocument!;
            opened.Add(doc);
            await ctx.SettleAsync(TimeSpan.FromSeconds(60));
            var clips = doc.Current.Items.Where(i => i.Name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToList();
            ctx.Check(clips.Count >= 60, $"the group lists its animations ({clips.Count})");
            var item = clips.FirstOrDefault(i => i.Name.Equals("medm_talk.rfa", StringComparison.OrdinalIgnoreCase)) ?? clips.FirstOrDefault();
            if (!ctx.Check(item is not null, "an animation to try")) return;

            // the entry: type, Info, facts, Open in Cairn
            ctx.Check(doc.List.AllRows.First(r => r.Name == item!.Name).TypeName == "RF2 animation", "listed as an RF2 animation");
            string info = VppInfo.Summarize(item!.Name, item.Source.Open, item.Size).Text;
            ctx.Check(info.Contains("22 bones", StringComparison.Ordinal), $"Info: {info}");
            ctx.Check(VppFacts.Describe(item).Rows.Any(r => r.Label == "Kind" && r.Value.StartsWith("Red Faction II clip", StringComparison.Ordinal)), "the details say it is a Red Faction II clip");
            doc.SetSelection([item]);
            await ctx.SettleAsync();
            ctx.Check(doc.Commands.CanOpenInCairn, "Open in Cairn is offered");

            // the preview: plays on a Red Faction II character of the group
            var window = new Window { Width = 700, Height = 500, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow, Left = -2000, Top = -2000 };
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            window.Show();
            try
            {
                area.Show(item, [item], doc.Current, immediate: true);
                await area.SettleAsync();
                var preview = (area.Preview.View as Cairn.Previews.ModulePreviewHost)?.Inner as RfaPreview;
                if (ctx.Check(preview is not null, $"{item.Name} previews in the animations module's view ({area.Preview.Kind})"))
                {
                    await preview!.Loading;
                    var wait = await Task.WhenAny(preview.MeshLoading, Task.Delay(120_000));
                    await ctx.SettleAsync();
                    ctx.Check(wait == preview.MeshLoading && preview.MeshName?.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase) == true && preview.Notice is null,
                        $"... playing on a Red Faction II character of the group ({preview.MeshName}; {preview.Notice ?? "no notice"})");
                    ctx.Check(preview.Playback?.IsPlaying == true, "... and playing");
                }
            }
            finally
            {
                area.Dispose();
                window.Close();
            }

            // Convert to: a Red Faction clip, into a folder without a dialog, read back as version 8
            byte[] bytes = item.Source.ReadAll();
            var choice = AssetConversions.TargetsFor(shell.Modules, item.Name, bytes.AsSpan(0, 16)).FirstOrDefault(c => c.Module == rfa && c.Target.Extension == ".rfa");
            if (ctx.Check(choice is not null, "Convert to lists a Red Faction clip"))
            {
                string outFolder = Path.Combine(folder, "entry");
                string? written = await vpp.ConvertEntryAsync(doc, item, choice!, interactive: false, outputFolder: outFolder);
                string path = Path.Combine(outFolder, item.Name);
                ctx.Check(written is not null && File.Exists(path) && RfaReader.ReadFile(path) is { Version: 8, BoneCount: 22 }, $"... written as a version 8 clip ({written})");
            }

            // Open in Cairn: a read-only clip tab on a character of the group
            await doc.Commands.OpenInCairnAsync(item);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is ClipDocumentViewModel { IsRedFactionII: true }, "Open in Cairn opens a Red Faction II clip tab"))
            {
                var tab = (ClipDocumentViewModel)shell.ActiveDocument!;
                opened.Add(tab);
                for (int i = 0; i < 1200 && tab.PreviewMesh is null; i++) await Task.Delay(100);
                await ctx.SettleAsync();
                ctx.Check(tab.Rf2PreviewMeshName?.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase) == true && tab.FittingSkeleton?.Count == 22,
                    $"it plays on a Red Faction II character of the group ({tab.Scene.MeshName}, {tab.Scene.Skeleton.Count} bones)");
                ctx.Check(!((IDocument)tab).CanSave && tab.IsReadOnly, "it cannot be saved or edited");
                ctx.Check(AssetConversions.TargetsFor(shell, tab).Any(c => c.Target.Extension == ".rfa"), "File > Convert to lists a Red Faction clip");
                var tabOut = await RfaModule.Workspace.Legacy.ConvertClipDocumentAsync(tab, new LegacyConvertChoice(LegacyConvertTarget.Folder, Path.Combine(folder, "tab"), false));
                ctx.Check(tabOut is [var p] && RfaReader.ReadFile(p).Version == 8, $"the tab converts too ({string.Join(", ", tabOut ?? [])})");
            }

            // the batch: clips and a character together, into a folder
            var batch = clips.Take(3).Concat(doc.Current.Items.Where(i => i.Name.Equals("burke.rfc", StringComparison.OrdinalIgnoreCase))).ToList();
            shell.ActiveDocument = doc;
            doc.SetSelection(batch);
            await ctx.SettleAsync();
            string batchOut = Path.Combine(folder, "batch");
            string? summary = await vpp.ConvertWithAsync(doc, rfa, interactive: false, options: new LegacyConvertChoice(LegacyConvertTarget.Folder, batchOut, false));
            var last = RfaModule.Workspace.Legacy.LastBatch;
            ctx.Check(last is { Clips.Count: 3 } && last.Converted.Count == batch.Count - 3 && last.Clips.All(c => RfaReader.ReadFile(Path.Combine(batchOut, c.OutputName)).Version == 8),
                $"Convert meshes and animations... converts the clips and the character ({summary})");
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A loose packfile group file listing <paramref name="files"/> in <c>pc_media\&lt;group&gt;\</c>.</summary>
    private static byte[] LooseToc(string group, IReadOnlyList<(string Name, int Size)> files)
    {
        using var ms = new MemoryStream();
        void Text(string s) { ms.Write(System.Text.Encoding.ASCII.GetBytes(s)); ms.WriteByte(0); }
        void U32(int v) => ms.Write(BitConverter.GetBytes(v));
        Text(group);
        Text($@"pc_media\{group}\");
        U32(0);
        U32(files.Count);
        foreach (var (name, size) in files) { Text(name); U32(size); Text($@"pc_media\{group}\{name}"); }
        return ms.ToArray();
    }

    private static int VersionOf(byte[] bytes) => bytes.Length >= 8 ? BitConverter.ToInt32(bytes, 4) : -1;

    private static string Versions(VppDocument doc) => string.Join(", ", doc.Current.Items.Select(i => $"{i.Name} v{VersionOf(i.Source.ReadAll())}"));

    /// <summary>
    /// Converting a Red Faction II clip never replaces a Red Faction II clip, even with Replace on: not its loose file in
    /// a Red Faction II group (the tab, whose own file is a work copy, and the batch), not another one in the folder, and
    /// not its entry in a .vpp (the tab and the batch "into the packfile"); the output takes a free name instead. A Red
    /// Faction clip in the way is still replaced. Synthetic files under %TEMP%.
    /// </summary>
    [SelfTest("integration.rf2-clip-sources-kept")]
    public static async Task Rf2ClipSourcesKept(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var vpp = shell.Modules.OfType<VppModule>().First();
        var rfa = shell.Modules.OfType<RfaModule>().First();
        string root = Path.Combine(Path.GetTempPath(), "cairn-rf2keep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var opened = new List<IDocument>();
        try
        {
            byte[] clip = Cairn.Rfa.Formats.Legacy.SyntheticRf2Clip.SampleSwing();

            // ── a loose Red Faction II group: the clips are files beside the group file ──
            string media = Directory.CreateDirectory(Path.Combine(root, "game", "pc_media")).FullName;
            string toc = Path.Combine(media, "Synth_anims.toc_group");
            File.WriteAllBytes(toc, LooseToc("Synth_anims", [("walk.rfa", clip.Length), ("run.rfa", clip.Length)]));
            string groupDir = Directory.CreateDirectory(Path.Combine(media, "Synth_anims")).FullName;
            string walk = Path.Combine(groupDir, "walk.rfa"), run = Path.Combine(groupDir, "run.rfa");
            File.WriteAllBytes(walk, clip);
            File.WriteAllBytes(run, clip);
            if (!ctx.Check(shell.OpenFile(toc) && shell.ActiveDocument is VppDocument { IsReadOnly: true }, "the loose group opens read only")) return;
            var group = (VppDocument)shell.ActiveDocument!;
            opened.Add(group);
            await ctx.SettleAsync();

            // the tab (Open in Cairn), converted into the clip's own folder with Replace on
            await group.Commands.OpenInCairnAsync(group.Current.Find("walk.rfa")!);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is ClipDocumentViewModel { IsRedFactionII: true }, "Open in Cairn gives a Red Faction II clip tab"))
            {
                var tab = (ClipDocumentViewModel)shell.ActiveDocument!;
                opened.Add(tab);
                ctx.Check(!string.Equals(tab.FilePath, walk, StringComparison.OrdinalIgnoreCase), "the tab's file is a work copy, not the loose file");
                var written = await RfaModule.Workspace.Legacy.ConvertClipDocumentAsync(tab, new LegacyConvertChoice(LegacyConvertTarget.Folder, groupDir, Replace: true));
                ctx.Check(VersionOf(File.ReadAllBytes(walk)) == 9 && File.ReadAllBytes(walk).AsSpan().SequenceEqual(clip), $"tab: the loose Red Faction II file is untouched (version {VersionOf(File.ReadAllBytes(walk))})");
                ctx.Check(written is [var w] && Path.GetFileName(w) == "walk (2).rfa" && VersionOf(File.ReadAllBytes(w)) == 8,
                    $"tab: the clip is written under a free name ({string.Join(", ", (written ?? []).Select(Path.GetFileName))})");
            }

            // the batch, same folder, Replace on
            shell.ActiveDocument = group;
            group.SetSelection([group.Current.Find("run.rfa")!]);
            await ctx.SettleAsync();
            string? summary = await vpp.ConvertWithAsync(group, rfa, interactive: false, options: new LegacyConvertChoice(LegacyConvertTarget.Folder, groupDir, Replace: true));
            ctx.Check(VersionOf(File.ReadAllBytes(run)) == 9 && File.ReadAllBytes(run).AsSpan().SequenceEqual(clip), $"batch: the loose Red Faction II file is untouched (version {VersionOf(File.ReadAllBytes(run))}; {summary})");
            string run2 = Path.Combine(groupDir, "run (2).rfa");
            ctx.Check(File.Exists(run2) && VersionOf(File.ReadAllBytes(run2)) == 8, "batch: the clip is written under a free name (run (2).rfa)");

            // another folder: a Red Faction II clip there is kept, a Red Faction clip is replaced
            string other = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
            byte[] oldRf1 = RfaWriter.Write(RfaReader.Read(clip, "x.rfa") with { Version = 7 });
            File.WriteAllBytes(Path.Combine(other, "walk.rfa"), clip);
            File.WriteAllBytes(Path.Combine(other, "run.rfa"), oldRf1);
            group.SetSelection([group.Current.Find("walk.rfa")!, group.Current.Find("run.rfa")!]);
            await ctx.SettleAsync();
            summary = await vpp.ConvertWithAsync(group, rfa, interactive: false, options: new LegacyConvertChoice(LegacyConvertTarget.Folder, other, Replace: true));
            ctx.Check(VersionOf(File.ReadAllBytes(Path.Combine(other, "walk.rfa"))) == 9 && File.Exists(Path.Combine(other, "walk (2).rfa")),
                $"batch: a Red Faction II clip already in the folder is kept; the output takes walk (2).rfa ({summary})");
            ctx.Check(VersionOf(File.ReadAllBytes(Path.Combine(other, "run.rfa"))) == 8 && !File.Exists(Path.Combine(other, "run (2).rfa")),
                "batch: a Red Faction clip in the way is replaced, as asked");

            // ── a writable .vpp holding Red Faction II clips: "into the packfile" with Replace on ──
            var package = Cairn.Vpp.Editing.VppEdit.AddSources(VppPackage.Empty,
                [("walk.rfa", (VppSource)new MemorySource(clip)), ("run.rfa", new MemorySource(clip))], Cairn.Vpp.Editing.VppClashPolicy.KeepBoth).Package;
            string vppPath = Path.Combine(root, "mod.vpp");
            Cairn.Vpp.Writing.VppSaver.Save(package, vppPath, null, CancellationToken.None, Cairn.Vpp.Writing.VppSaveOptions.Default);
            if (!ctx.Check(shell.OpenFile(vppPath) && shell.ActiveDocument is VppDocument { IsReadOnly: false }, "the .vpp opens writable")) return;
            var mod = (VppDocument)shell.ActiveDocument!;
            opened.Add(mod);
            await ctx.SettleAsync();

            mod.SetSelection([mod.Current.Find("run.rfa")!]);
            await ctx.SettleAsync();
            summary = await vpp.ConvertWithAsync(mod, rfa, interactive: false, options: new LegacyConvertChoice(LegacyConvertTarget.IntoPackfile, null, Replace: true));
            ctx.Check(mod.Current.Find("run.rfa") is { } r && VersionOf(r.Source.ReadAll()) == 9 && mod.Current.Find("run (2).rfa") is { } r2 && VersionOf(r2.Source.ReadAll()) == 8,
                $"batch into the packfile: run.rfa is kept and the clip is added as run (2).rfa ({Versions(mod)}; {summary})");

            await mod.Commands.OpenInCairnAsync(mod.Current.Find("walk.rfa")!);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is ClipDocumentViewModel { IsRedFactionII: true }, "Open in Cairn gives a Red Faction II clip tab"))
            {
                var tab = (ClipDocumentViewModel)shell.ActiveDocument!;
                opened.Add(tab);
                var written = await RfaModule.Workspace.Legacy.ConvertClipDocumentAsync(tab, new LegacyConvertChoice(LegacyConvertTarget.IntoPackfile, null, Replace: true));
                ctx.Check(mod.Current.Find("walk.rfa") is { } w && VersionOf(w.Source.ReadAll()) == 9 && written is ["walk (2).rfa"]
                    && mod.Current.Find("walk (2).rfa") is { } w2 && VersionOf(w2.Source.ReadAll()) == 8,
                    $"tab into the packfile: walk.rfa is kept and the clip is added as walk (2).rfa ({string.Join(", ", written ?? [])}; {Versions(mod)})");
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
#endif
