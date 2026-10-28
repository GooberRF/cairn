using System.Text;
using System.Windows;
using Cairn.Formats.Imaging;
using Cairn.Formats.Rf2;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ps2;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// Red Faction II packfile groups (<c>Cairn.exe --probe-module --selftest-only vpp.rf2*</c>): synthetic packed and loose
/// groups opened through the shell (read only: nothing can be added, removed, renamed or saved; extraction is
/// byte-exact; Convert to writes into a folder), and the real install's All_Levels group when it is configured
/// (only read: nothing is ever written beside it).
/// </summary>
public static class VppRf2SelfTests
{
    private static string NewFolder() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairn-rf2-test-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private static byte[] Toc(string group, bool packed, IReadOnlyList<(string Name, int Size, int Offset)> files)
    {
        using var ms = new MemoryStream();
        void Text(string s) { ms.Write(Encoding.ASCII.GetBytes(s)); ms.WriteByte(0); }
        void U32(int v) => ms.Write(BitConverter.GetBytes(v));
        Text(group);
        Text($@"pc_media\{group}\");
        U32(packed ? 2 : 0);
        if (packed)
        {
            U32(1);
            Text($@"pc_media\{group}\{group}.packfile");
            U32(files.Count);
            foreach (var (name, size, offset) in files) { Text(name); U32(size); U32(offset); }
        }
        else
        {
            U32(files.Count);
            foreach (var (name, size, _) in files) { Text(name); U32(size); Text($@"pc_media\{group}\{name}"); }
        }
        return ms.ToArray();
    }

    /// <summary>The PEG preview a preview area shows (module previews come wrapped in the shared host), or null.</summary>
    private static PegPreview? PegOf(VppPreviewArea area) => area.Preview.View switch
    {
        PegPreview peg => peg,
        ModulePreviewHost { Inner: PegPreview peg } => peg,
        _ => null,
    };

    [SelfTest("vpp.rf2-synthetic")]
    public static async Task Synthetic(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        string game = NewFolder();
        var opened = new List<IDocument>();
        try
        {
            string media = Directory.CreateDirectory(Path.Combine(game, "pc_media")).FullName;
            // packed: a PEG texture pack, an RF2 animation and an empty .peg, back to back from offset 0
            byte[] peg = SyntheticPeg.Sample();
            byte[] anim = Cairn.Rfa.Formats.Legacy.SyntheticRf2Clip.SampleSwing();
            string packedToc = Path.Combine(media, "Synth_levels.toc_group");
            File.WriteAllBytes(packedToc, Toc("Synth_levels", true, [("level01.peg", peg.Length, 0), ("walk.rfa", anim.Length, peg.Length), ("poop_v3d.peg", 0, peg.Length + anim.Length)]));
            Directory.CreateDirectory(Path.Combine(media, "Synth_levels"));
            File.WriteAllBytes(Path.Combine(media, "Synth_levels", "Synth_levels.packfile"), [.. peg, .. anim]);
            // loose: a table, a file listed twice in different case, and one listed but missing
            byte[] table = Encoding.ASCII.GetBytes("#General\n$Name: \"x\"\n#End\n");
            string looseToc = Path.Combine(media, "Synth_loose.toc_group");
            File.WriteAllBytes(looseToc, Toc("Synth_loose", false, [("weapons.tbl", table.Length, 0), ("military_Grunt.tbl", table.Length, 0), ("military_grunt.tbl", table.Length, 0), ("vssver.scc", 3152, 0)]));
            Directory.CreateDirectory(Path.Combine(media, "Synth_loose"));
            File.WriteAllBytes(Path.Combine(media, "Synth_loose", "weapons.tbl"), table);
            File.WriteAllBytes(Path.Combine(media, "Synth_loose", "military_grunt.tbl"), table);

            // ---- packed, opened like a double-click
            ctx.Check(ctx.Shell.OpenFile(packedToc), "the shell opens a .toc_group");
            if (ctx.Shell.ActiveDocument is not VppDocument doc || doc.Rf2 is null) { ctx.Check(false, "... as a Red Faction II packfile group"); return; }
            opened.Add(doc);
            ctx.Check(doc.Kind.Id == "vpp.rf2" && doc.Kind.Extensions.SequenceEqual([".toc_group"]), "its document kind is the RF2 group kind");
            ctx.Check(doc.IsReadOnly && !((IDocument)doc).CanSave && !doc.IsDirty, "read only: Save and Save As are off");
            ctx.Check(doc.Current.Count == 3 && doc.Current.Items.All(i => i.Source is ArchiveSource), "3 entries read from the .packfile");
            var view = (VppDocumentView)doc.View;
            ctx.Check(view.Rf2BannerText?.Contains("read only", StringComparison.Ordinal) == true, "the banner says it is read only: " + view.Rf2BannerText);
            ctx.Check(view.Ps2BannerText is null, "no PlayStation 2 banner (an RF2 group holds .peg files too)");
            ctx.Check(view.VisibleEditTools.Count == 0, "no Add/Remove/Rename on the toolbar: " + string.Join(", ", view.VisibleEditTools));
            ctx.Check(doc.List.AllRows.First(r => r.Name == "level01.peg").TypeName == "RF2 texture pack", "RF2's names for the types");

            // nothing changes it
            int count = doc.Current.Count;
            ctx.Check(!doc.ApplyEdit("probe", p => Editing.VppEdit.Remove(p, ["level01.peg"])), "an edit is refused");
            ctx.Check(await doc.Commands.AddPathsAsync([looseToc]) is null, "adding files is refused");
            doc.SelectNames(["level01.peg"]);
            doc.Commands.RemoveSelected();
            ctx.Check(doc.Commands.Rename("level01.peg", "other.peg") is not null, "renaming is refused");
            ctx.Check(doc.Current.Count == count && doc.Current.Find("level01.peg") is not null && !doc.IsDirty && !doc.CanUndo, "the group is unchanged");
            ctx.Check(!ctx.Shell.Save(doc), "File > Save does nothing");
            string workCopy = await doc.Work.PrepareAsync(doc.Current.Find("level01.peg")!);
            ctx.Check(module.ArchiveEntryOf(workCopy) is { EntryName: "level01.peg" } && module.ArchiveOf(workCopy) is null,
                "a work copy knows its entry, but other modules are not offered to add files to the group");

            // extraction is byte-exact
            string outFolder = Path.Combine(game, "out");
            var written = await doc.Commands.ExtractAsync(doc.Current.Items, outFolder, Commands.VppOverwritePolicy.Overwrite, quiet: true);
            ctx.Check(written?.Count == 3, $"Extract writes every entry ({written?.Count})");
            ctx.Check(File.ReadAllBytes(Path.Combine(outFolder, "level01.peg")).AsSpan().SequenceEqual(peg)
                && File.ReadAllBytes(Path.Combine(outFolder, "walk.rfa")).AsSpan().SequenceEqual(anim)
                && new FileInfo(Path.Combine(outFolder, "poop_v3d.peg")).Length == 0, "... byte for byte, the empty file empty");

            // the context menu: no Remove/Rename/Replace; Convert to for the .peg writes .tga files into a folder
            var list = view.FileList;
            doc.SelectNames(["level01.peg"]);
            list.RefreshContextMenu();
            var hidden = list.EntryMenu.Items.OfType<System.Windows.Controls.MenuItem>().Where(m => m.Visibility == Visibility.Visible).Select(m => (m.Header as string ?? string.Empty).Replace("_", "", StringComparison.Ordinal)).ToList();
            ctx.Check(!hidden.Contains("Remove") && !hidden.Contains("Rename") && !hidden.Contains("Replace...") && !hidden.Contains("Convert to .tga..."),
                "the entry menu hides what would change the group: " + string.Join(", ", hidden));
            ctx.Check(list.ContextMenuConvertToItem?.Visibility == Visibility.Visible, "the Convert to submenu is there for the .peg");
            var choice = AssetConversions.TargetsFor(ctx.Shell.Modules, "level01.peg").FirstOrDefault(c => ReferenceEquals(c.Converter, module) && c.Target.Extension == ".tga");
            ctx.Check(choice is not null, "the packfile module converts the .peg to .tga");
            if (choice is not null)
            {
                string tgaFolder = Path.Combine(game, "tga");
                string? first = await module.ConvertEntryAsync(doc, doc.Current.Find("level01.peg")!, choice, interactive: false, outputFolder: tgaFolder);
                ctx.Check(first is not null && File.Exists(first) && Directory.GetFiles(tgaFolder).Length >= 5, $"... into a folder ({first}, {(Directory.Exists(tgaFolder) ? Directory.GetFiles(tgaFolder).Length : 0)} files)");
                ctx.Check(doc.Current.Count == count && !doc.IsDirty, "... and the group stays as it was");
            }
            // an RF2 animation opens and converts (to a Red Faction clip) when the animations module is in the build
            doc.SelectNames(["walk.rfa"]);
            list.RefreshContextMenu();
            bool clips = module.CairnOpens("walk.rfa");
            ctx.Check(doc.Commands.CanOpenInCairn == clips && (list.ContextMenuConvertToItem?.Visibility == Visibility.Visible) == clips,
                $"an RF2 animation offers Open in Cairn and Convert to exactly when a module reads it ({clips})");
            ctx.Check(!clips || AssetConversions.TargetsFor(ctx.Shell.Modules, "walk.rfa", anim).Any(c => c.Target.Extension == ".rfa"),
                "... its Convert to lists a Red Faction clip (.rfa)");
            ctx.Check(doc.List.AllRows.First(r => r.Name == "walk.rfa").TypeName == "RF2 animation", "listed as an RF2 animation");

            // previews: the .peg shows its textures; the empty one says it is empty
            var window = new Window { Width = 700, Height = 600, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000, Top = -10000 };
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            window.Show();
            try
            {
                var pegItem = doc.Current.Find("level01.peg")!;
                area.Show(pegItem, [pegItem], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(PegOf(area) is { Pack.Textures.Count: > 0 }, $"the .peg previews its textures ({area.Preview.Kind}, {area.Preview.View?.GetType().Name})");
                var empty = doc.Current.Find("poop_v3d.peg")!;
                area.Show(empty, [empty], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(area.Details.Rows.Any(r => r.Value == "Empty file (0 bytes)"), "the empty .peg's details say it is empty");
                area.Show(null, [], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(area.Details.Rows.Any(r => r.Label == "Mode" && r.Value.StartsWith("Packed", StringComparison.Ordinal)), "with nothing selected, the details show the group's facts");
            }
            finally
            {
                area.Dispose();
                window.Close();
            }

            // ---- loose
            ctx.Check(ctx.Shell.OpenFile(looseToc), "the shell opens a loose group");
            if (ctx.Shell.ActiveDocument is not VppDocument loose || loose.Rf2 is null) { ctx.Check(false, "... as a Red Faction II packfile group"); return; }
            opened.Add(loose);
            ctx.Check(loose.Current.Count == 4 && loose.Rf2.Missing.SequenceEqual(["vssver.scc"]), "4 entries, the missing one listed");
            ctx.Check(loose.Problems.Any(p => p.Code == "RF2001") && loose.Problems.All(p => p.Severity != Validation.VppSeverity.Error), "the missing file is a warning, never an error");
            string looseOut = Path.Combine(game, "loose-out");
            var looseWritten = await loose.Commands.ExtractAsync(loose.Current.Items, looseOut, Commands.VppOverwritePolicy.Overwrite, quiet: true);
            var files = Directory.Exists(looseOut) ? Directory.GetFiles(looseOut).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
            ctx.Check(looseWritten?.Count == 2 && files.Count == 2, "the case twins are written once, the missing file skipped: " + string.Join(", ", files));
            ctx.Check(files.All(f => File.ReadAllBytes(Path.Combine(looseOut, f!)).AsSpan().SequenceEqual(table)), "... byte for byte");
            ctx.Check(loose.StatusMessage.Contains("written once", StringComparison.Ordinal) && loose.StatusMessage.Contains("not on disk", StringComparison.Ordinal), "the status says so: " + loose.StatusMessage);
            ctx.Check(File.Exists(Path.Combine(media, "Synth_loose", "weapons.tbl")) && Directory.GetFiles(Path.Combine(media, "Synth_loose")).Length == 2, "nothing was written beside the group's files");
        }
        finally
        {
            foreach (var d in opened) if (ctx.Shell.Documents.Contains(d)) ctx.Shell.Close(d);
            Work.VppWorkFolder.TryDeleteFolder(game);
        }
    }

    [SelfTest("vpp.rf2-all-levels", Order = 310)]
    public static async Task AllLevels(SelfTestContext ctx)
    {
        string? dir = LocalPaths.Rf2GameDirectory;
        string? toc = dir is null ? null : Path.Combine(dir, "pc_media", "All_Levels.toc_group");
        if (toc is null || !File.Exists(toc)) { ctx.Skip("no Red Faction II install configured (rf2GameDirectory)"); return; }
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        var stamps = Directory.GetFiles(Path.GetDirectoryName(toc)!).ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
        string scratch = NewFolder();
        VppDocument? doc = null;
        try
        {
            // every group of the install opens (read only), with the stock counts
            int total = 0;
            foreach (string group in Directory.GetFiles(Path.GetDirectoryName(toc)!, "*.toc_group"))
            {
                using var d = (VppDocument)module.Rf2Kind.Open(group);
                total += d.Current.Count;
                ctx.Check(d.IsReadOnly && d.Problems.All(p => p.Severity != Validation.VppSeverity.Error), $"{Path.GetFileName(group)}: {d.Current.Count:N0} entries, {d.Rf2!.ModeText}");
            }
            ctx.Check(total == 8682, $"all groups list 8,682 files ({total:N0})");

            ctx.Check(ctx.Shell.OpenFile(toc), "the shell opens All_Levels.toc_group");
            doc = ctx.Shell.ActiveDocument as VppDocument;
            if (doc?.Rf2 is not { } rf2) { ctx.Check(false, "... as a Red Faction II group"); return; }
            ctx.Check(doc.Current.Count == 1215 && rf2.Toc.IsPacked, $"1,215 entries packed into {Path.GetFileName(rf2.PackfilePath)}");
            var levels = doc.List.AllRows.Where(r => r.Extension == ".rfl").ToList();
            ctx.Check(levels.Count == 105 && levels.All(r => r.TypeName == "RF2 level"), $"the list shows the 105 levels ({levels.Count})");

            // byte-exact reads from the packfile: first, last and a spread between
            using (var raw = new FileStream(rf2.PackfilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bool exact = true;
                foreach (int i in new[] { 0, 1, 2, 300, 777, doc.Current.Count - 1 })
                {
                    var entry = rf2.Toc.Entries[i];
                    var expected = new byte[entry.Size];
                    raw.Position = entry.Offset;
                    raw.ReadExactly(expected);
                    exact &= doc.Current.Items[i].Source.ReadAll().AsSpan().SequenceEqual(expected);
                }
                ctx.Check(exact, "entries read byte for byte at their offsets");
            }
            var sample = doc.Current.Items.Where(i => i.Size is > 0 and < (2 << 20)).Take(4).ToList();
            var written = await doc.Commands.ExtractAsync(sample, scratch, Commands.VppOverwritePolicy.Overwrite, quiet: true);
            ctx.Check(written?.Count == sample.Count && sample.All(i => File.ReadAllBytes(Path.Combine(scratch, i.Name)).AsSpan().SequenceEqual(i.Source.ReadAll())), "Extract to a folder is byte-exact");

            // a .peg previews its textures (all of them decode); an .rfl gets the level preview and facts
            var window = new Window { Width = 800, Height = 700, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000, Top = -10000 };
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            window.Show();
            try
            {
                var peg = doc.Current.Items.First(i => i.Extension == ".peg" && i.Size > 0);
                area.Show(peg, [peg], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(PegOf(area) is { Pack.Textures.Count: > 0 }, $"{peg.Name} previews its textures ({area.Preview.Kind})");
                byte[] bytes = peg.Source.ReadAll();
                var pack = PegCodec.Read(bytes, peg.Name);
                bool decodes = pack.Textures.All(t => Enumerable.Range(0, t.FrameCount).All(f => PegCodec.DecodeFrame(bytes, t, f).Pixels.Length > 0));
                ctx.Check(decodes, $"... every texture of it decodes ({pack.Textures.Count})");
                var level = doc.Current.Items.First(i => i.Extension == ".rfl");
                area.Show(level, [level], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(area.Preview.Kind is not AssetPreviewKind.Loading && area.Details.Rows.Any(r => r.Value == "RF2 level"), $"{level.Name}: preview ({area.Preview.Kind}) and details");
                await area.Details.Pending;
                string version = area.Details.Rows.FirstOrDefault(r => r.Label == "Format version")?.Value ?? "";
                ctx.Check(version == "Red Faction II (295)" && area.Details.Warnings.Count == 0 && area.Details.Rows.All(r => !r.Flagged),
                    $"{level.Name}: the details name the format without calling it unsupported ('{version}'; warnings: {string.Join(" | ", area.Details.Warnings)})");
            }
            finally
            {
                area.Dispose();
                window.Close();
            }

            ctx.Check(Directory.GetFiles(Path.GetDirectoryName(toc)!).All(f => stamps.TryGetValue(f, out var t) && t == File.GetLastWriteTimeUtc(f)), "nothing in the RF2 folder was written or added");
        }
        finally
        {
            if (doc is not null && ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc);
            Work.VppWorkFolder.TryDeleteFolder(scratch);
        }
    }
}
