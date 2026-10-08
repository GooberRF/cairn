using Cairn.Formats.Imaging;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ps2;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Vpp.Writing;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// PlayStation 2 content (<c>Cairn.exe --selftest</c>): a synthetic .peg opened as a packfile and saved as a PC
/// packfile, a synthetic PS2 packfile's banner and its undoable "Convert to .tga..." (dialog, one PEG at a time), the .peg preview, PNG export,
/// and the real PS2 files when configured (only read: nothing may appear beside them).
/// </summary>
public static class VppPs2SelfTests
{
    private static string NewFolder() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairn-ps2-test-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private static readonly string[] SampleFiles = ["wall.tga", "panel.tga", "sign.tga", "grate.tga", "dots.tga", "fire_00.tga", "fire_01.tga", "fire_02.tga", "fire.atx", "menu_bg.tga"];

    [SelfTest("vpp.peg-document")]
    public static async Task PegDocument(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        string folder = NewFolder();
        VppDocument? doc = null;
        string? heading = null;
        var service = ctx.Shell.Dialogs as DialogService;
        var previous = service?.NonInteractiveChoice;
        if (service is not null) service.NonInteractiveChoice = (h, buttons) => { heading = h; return 0; };
        try
        {
            byte[] peg = SyntheticPeg.Sample();
            string pegPath = Path.Combine(folder, "sample.peg");
            File.WriteAllBytes(pegPath, peg);

            // opened like a double-click: through the shell, by extension
            ctx.Check(ctx.Shell.OpenFile(pegPath), "the shell opens a .peg");
            doc = ctx.Shell.ActiveDocument as VppDocument;
            ctx.Check(doc is { PegSource: not null }, "... as a packfile document converted from the texture pack");
            if (doc is null) return;
            ctx.Check(ctx.Shell.Settings.RecentFiles.Any(r => string.Equals(r, pegPath, StringComparison.OrdinalIgnoreCase)), "the .peg is in the Recent list");
            ctx.Check(doc.DisplayName == "sample.peg" && doc.FilePath is null && !doc.IsDirty, $"tab 'sample.peg', no path (Save is Save As), clean ({doc.DisplayName}, {doc.FilePath})");
            ctx.Check(doc.Kind.Extensions[0] == ".vpp" && doc.Kind.FileFilter.Contains("*.vpp", StringComparison.Ordinal), "Save As offers a .vpp, never the .peg");
            ctx.Check(doc.Current.Items.Select(i => i.Name).SequenceEqual(SampleFiles), "the entries: one .tga per texture, an animation as frames plus .atx: " + string.Join(", ", doc.Current.Items.Select(i => i.Name)));
            ctx.Check(doc.Current.Items.All(i => i.Name.EndsWith(".atx", StringComparison.Ordinal) || TgaCodec.Probe(i.Source.ReadAll(), i.Name).Width > 0), "every .tga reads back");

            var view = (VppDocumentView)doc.View;
            string banner = view.Ps2BannerText ?? string.Empty;
            ctx.Check(banner.StartsWith("PlayStation 2 texture pack. Saving writes a PC packfile (.vpp) with each texture as a .tga.", StringComparison.Ordinal), "banner: " + banner);
            ctx.Check(banner.Contains("The MPEG-2 compressed background becomes 24-bit .tga (no alpha).", StringComparison.Ordinal) && !banner.Contains("Not converted", StringComparison.Ordinal), "... it says the MPEG-2 background is decoded");
            ctx.Check(banner.Contains(".atx (Alpine Faction 1.4.0 or later)", StringComparison.Ordinal), "... and how the animation is stored");
            ctx.Check(view.Ps2BannerButtons.SequenceEqual(["Dismiss"]), "no convert button on a texture pack");

            // Info column and details say where each file came from
            var sign = doc.Current.Find("sign.tga")!;
            ctx.Check(VppInfo.Summarize(sign).Text.Contains("from PS2 8-bit indexed, 32-bit palette", StringComparison.Ordinal), "Info: " + VppInfo.Summarize(sign).Text);
            var background = doc.Current.Find("menu_bg.tga")!;
            ctx.Check(VppInfo.Summarize(background).Text == "640x448, from PS2 MPEG-2 (6 tiles)", "Info of the decoded background: " + VppInfo.Summarize(background).Text);
            var backgroundTga = TgaCodec.Decode(background.Source.ReadAll(), "menu_bg.tga");
            ctx.Check(backgroundTga.Width == 640 && backgroundTga.Height == 448 && background.Source.ReadAll()[16] == 24, "... a 640 x 448 24-bit .tga");

            // Extract works: PNG of a converted texture, and the .tga as it is
            string outFolder = Path.Combine(folder, "out");
            var pngs = await module.ExtractAsPngAsync(doc, [sign, doc.Current.Find("fire_01.tga")!], outFolder);
            ctx.Check(pngs.Count == 2 && pngs.All(p => File.ReadAllBytes(p).AsSpan(0, 4).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' })), "Extract as PNG writes PNG files");
            var tgas = await doc.Commands.ExtractAsync([sign], outFolder, Commands.VppOverwritePolicy.Overwrite, quiet: true);
            ctx.Check(tgas?.Count == 1 && File.ReadAllBytes(tgas[0]).AsSpan().SequenceEqual(sign.Source.ReadAll()), "Extract writes the .tga");

            // Save = Save As a PC packfile beside it; the .peg is untouched
            string vppPath = Path.Combine(folder, "sample.vpp");
            doc.SaveTo(vppPath);
            ctx.Check(doc.FilePath == vppPath && !doc.IsDirty && doc.PegSource is null, "saved as sample.vpp; the document is now that packfile");
            ctx.Check(heading == "Saved sample.vpp", $"the summary dialog came up once ({heading})");
            ctx.Check(view.Ps2BannerText is null, "the texture-pack banner is gone after saving");
            var saved = VppPackage.Open(vppPath);
            ctx.Check(saved.Items.Select(i => i.Name).SequenceEqual(SampleFiles), "the saved packfile holds the expected .tga and .atx entries");
            ctx.Check(saved.Items.All(i => i.Source.ReadAll().AsSpan().SequenceEqual(doc.Current.Find(i.Name)!.Source.ReadAll())), "... with the converted bytes");
            ctx.Check(File.ReadAllBytes(pegPath).AsSpan().SequenceEqual(peg), "the .peg on disk is unchanged");
            string summary = VppDocument.PegSaveSummary(PegConverter.Convert(peg, "sample.peg", decodeMpeg2: false), "sample.vpp");
            ctx.Check(summary.Contains("menu_bg.tga: " + PegConverter.Mpeg2Reason, StringComparison.Ordinal), "with decoding switched off, the summary lists the MPEG-2 background");
            var keyed = PegConverter.Convert(peg, "sample.peg", blackKey: PegBlackKey.Game);
            ctx.Check(keyed.Files.Single(f => f.Name == "menu_bg.tga").Bytes[16] == 32 && PegConverter.BannerFor(keyed).Contains("32-bit .tga with black below 25 transparent", StringComparison.Ordinal),
                "with black as transparent, the background is a 32-bit .tga and the banner says so");

            // numbered MPEG-2 frames (the PS2 main menu's plan-0001...): a .tga each plus one looping .atx; numbered
            // pages (extras01, extras02) stay plain stills
            var menu = (VppDocument)module.PegKind.OpenBytes(SyntheticPeg.Mpeg2Sequence(), "interface-bg-mm.peg", "test data");
            try
            {
                var menuNames = menu.Current.Items.Select(i => i.Name).ToList();
                string[] expected = [.. Enumerable.Range(1, 8).Select(i => $"plan-000{i}.tga"), "extras01.tga", "extras02.tga", "interface-bg-mm.atx"];
                ctx.Check(menuNames.SequenceEqual(expected), "frames, pages and one .atx: " + string.Join(", ", menuNames));
                string atx = System.Text.Encoding.UTF8.GetString(menu.Current.Find("interface-bg-mm.atx")!.Source.ReadAll());
                ctx.Check(atx.Contains("frame_time = 33", StringComparison.Ordinal) && atx.Contains("\"plan-0008.tga\"", StringComparison.Ordinal) && !atx.Contains("extras", StringComparison.Ordinal),
                    "the .atx loops the 8 frames at 30 fps");
                ctx.Check(VppInfo.Summarize(menu.Current.Find("interface-bg-mm.atx")!).Text.Contains("from PS2 MPEG-2 frames, 8 frames at 30 fps", StringComparison.Ordinal),
                    "Info of the .atx: " + VppInfo.Summarize(menu.Current.Find("interface-bg-mm.atx")!).Text);
                string menuBanner = PegConverter.BannerFor(menu.PegSource!);
                ctx.Check(menuBanner.Contains("listed in interface-bg-mm.atx", StringComparison.Ordinal), "the banner names the .atx: " + menuBanner);
            }
            finally
            {
                menu.Dispose();
            }
        }
        finally
        {
            if (service is not null) service.NonInteractiveChoice = previous;
            if (doc is not null && ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc);
            Work.VppWorkFolder.TryDeleteFolder(folder);
        }
    }

    [SelfTest("vpp.ps2-packfile")]
    public static async Task Ps2Packfile(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        string folder = NewFolder();
        VppDocument? doc = null;
        try
        {
            byte[] peg = SyntheticPeg.Sample();
            var package = VppEdit.AddSources(VppPackage.Empty,
            [
                ("readme.txt", new MemorySource("PS2 test"u8.ToArray())),
                ("pack.peg", new MemorySource(peg)),
                ("mesh.rfm", new MemorySource([0x12, 0x87, 0x12, 0x87, 0, 0, 0, 0])),
            ], VppClashPolicy.KeepBoth).Package;
            string path = Path.Combine(folder, "ps2test.vpp");
            VppSaver.Save(package, path, null, CancellationToken.None, VppSaveOptions.Default);

            doc = (VppDocument)module.Kind.Open(path);
            ctx.Shell.AddDocument(doc);
            ctx.Check(doc.IsPs2Packfile && doc.PegEntryCount == 1, "detected as a PlayStation 2 packfile with one .peg");
            var view = (VppDocumentView)doc.View;
            ctx.Check(view.Ps2BannerText == Ps2Packfiles.PegBanner, "banner: " + view.Ps2BannerText);
            ctx.Check(view.Ps2BannerButtons.SequenceEqual(["Dismiss"]), "... with no convert-all button: " + string.Join(", ", view.Ps2BannerButtons));
            ctx.Check(doc.List.TypeOptions.Any(o => o.Label.Contains("PS2 texture pack", StringComparison.Ordinal)) && doc.List.TypeOptions.Any(o => o.Label.Contains("PS2 static mesh", StringComparison.Ordinal)),
                "the type filter names the PS2 types: " + string.Join(", ", doc.List.TypeOptions.Select(o => o.Label)));
            ctx.Check(VppInfo.Summarize(doc.Current.Find("pack.peg")!).Text == "PEG v6: 7 textures (1 animated, 1 MPEG-2 compressed)", "Info of the .peg entry");
            ctx.Check(doc.Problems.Any(p => p.Code == "VPP009" && p.EntryName == "pack.peg" && p.Message.Contains("PlayStation 2", StringComparison.Ordinal)), "the problem list says the .peg is from the PS2 version");

            // the preview pane's view of a .peg entry: its textures, the first shown
            ctx.Check(module.CanPreview("pack.peg") && !module.CanPreview("pack.tga"), "the module previews .peg entries");
            using (var preview = (PegPreview)module.CreatePreview(peg, "pack.peg")!)
            {
                ctx.Check(preview.Pack.Textures.Count == 7 && preview.Selected?.Name == "wall.tga", "the preview lists 7 textures and shows the first");
                await preview.Pending;
                ctx.Check(preview.Image is not null, "... decoded into the image preview");
                preview.Select(5);
                await preview.Pending;
                ctx.Check(preview.Image?.IsPlaying == true, "an animated texture plays");
                preview.Select(6);
                await preview.Pending;
                ctx.Check(preview.Image is not null, "an MPEG-2 background is decoded into the image preview");
            }
            using (var preview = new PegPreview(peg, "pack.peg", decodeMpeg2: false))
            {
                preview.Select(6);
                await preview.Pending;
                ctx.Check(preview.Image is null, "with decoding switched off, an MPEG-2 background shows a note, not an image");
            }

            // "Open in Cairn" on the .peg entry: a work copy (in the work folder) opens as its own texture-pack tab
            await doc.Commands.OpenInCairnAsync(doc.Current.Find("pack.peg")!);
            var opened = ctx.Shell.ActiveDocument as VppDocument;
            ctx.Check(opened is { PegSource: not null, DisplayName: "pack.peg", FilePath: null } && opened.ArchiveOriginText?.Contains("pack.peg in ps2test.vpp", StringComparison.Ordinal) == true,
                $"Open in Cairn opens the .peg entry as a texture pack ({opened?.DisplayName}, {opened?.ArchiveOriginText})");
            ctx.Check(doc.HasWorkFolder && doc.Work.Root.StartsWith(Path.GetFullPath(module.Settings.WorkRoot), StringComparison.OrdinalIgnoreCase), "... from a work copy in the work folder");
            if (opened is not null && !ReferenceEquals(opened, doc)) ctx.Shell.Close(opened);
            ctx.Shell.Activate(doc);

            // "Convert to .tga..." is offered for selected .peg entries only (context menu and Packfile menu)
            doc.SelectNames(["readme.txt"]);
            view.FileList.RefreshContextMenu();
            ctx.Check(!VppModule.HasSelectedPegs(doc) && view.FileList.ContextMenuPegItem?.Visibility == System.Windows.Visibility.Collapsed, "no Convert to .tga... for a .txt entry");
            doc.SelectNames(["pack.peg"]);
            view.FileList.RefreshContextMenu();
            ctx.Check(VppModule.HasSelectedPegs(doc) && view.FileList.ContextMenuPegItem is { IsEnabled: true, Visibility: System.Windows.Visibility.Visible },
                "Convert to .tga... in the context menu of a .peg entry");
            var packfileMenu = module.Menus.Select(m => m.Item).OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(m => m.Name == "PackfileMenu");
            var menuItem = packfileMenu?.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(m => Equals(m.Header, VppModule.ConvertPegsHeader));
            ctx.Check(menuItem?.Command?.CanExecute(null) == true, "... and in the Packfile menu, enabled with a .peg selected");
            doc.SelectNames(["readme.txt"]);
            ctx.Check(menuItem?.Command?.CanExecute(null) == false, "... disabled without one");

            // the dialog: every texture ticked, what each becomes, black as transparent from the settings
            var picks = module.ReadPegs(doc, [doc.Current.Find("pack.peg")!]);
            var window = Dialogs.VppPegConvertWindow.Create(doc, picks, module.Settings);
            try
            {
                ctx.Check(window.Rows.Count == 7 && window.Rows.All(r => r.IsChecked), $"the dialog lists the 7 textures, all ticked ({window.Rows.Count})");
                ctx.Check(window.Rows.Single(r => r.Name == "fire.vbm").Becomes == "fire.atx + 3 .tga frames", "the animation's .atx is named: " + window.Rows.Single(r => r.Name == "fire.vbm").Becomes);
                ctx.Check(window.Rows.Single(r => r.Name == "menu_bg.tga").Kind == "MPEG-2 background", "the MPEG-2 background is marked");
                ctx.Check(window.BlackTransparent == module.Settings.Mpeg2BlackTransparent && window.Threshold == module.Settings.Mpeg2BlackThreshold, "black as transparent defaults from the settings");
                ctx.Check(window.Summary.StartsWith("7 of 7 textures ticked (1 animated, 1 MPEG-2) in 1 PEG texture pack", StringComparison.Ordinal), "summary: " + window.Summary);
                window.Rows.Single(r => r.Name == "panel.tga").IsChecked = false;
                window.BlackTransparent = true;
                window.Threshold = 30;
                ctx.Check(window.Summary.StartsWith("6 of 7", StringComparison.Ordinal), "unticking updates the summary: " + window.Summary);
            }
            finally
            {
                window.Close();
            }
            var choice = window.Current();
            ctx.Check(choice.Entries.Count == 1 && choice.Entries[0].Textures?.Count == 6 && choice.BlackKey?.Threshold == 30, "the choice: 6 textures of pack.peg, black below 30");

            var report = await module.ConvertPegTexturesAsync(doc, choice, showSkipped: false);
            ctx.Check(report is not null && report.Textures == 6 && report.Mpeg2Decoded == 1 && report.NotChosen == 1, $"converted the 6 ticked textures, the MPEG-2 background included ({report?.Summary})");
            var names = doc.Current.Items.Select(i => i.Name).ToList();
            ctx.Check(names.SequenceEqual(["readme.txt", .. SampleFiles.Where(n => n != "panel.tga"), "mesh.rfm"]), "the .peg is replaced in place, panel.tga left out: " + string.Join(", ", names));
            ctx.Check(doc.Current.Find("menu_bg.tga")!.Source.ReadAll()[16] == 32, "black as transparent: the background is a 32-bit .tga");
            ctx.Check(report?.Summary.Contains("1 texture not ticked", StringComparison.Ordinal) == true, "the summary counts the unticked texture");
            ctx.Check(doc.UndoLabel == "Convert pack.peg to .tga" && doc.IsDirty, $"one undo step ({doc.UndoLabel})");
            ctx.Check(view.Ps2BannerText == Ps2Packfiles.Banner, "the banner stays (PS2 meshes remain) without the convert hint: " + view.Ps2BannerText);
            ctx.Check(doc.Notice?.StartsWith(report!.Summary, StringComparison.Ordinal) == true, "the notice summarises the conversion");
            doc.Undo();
            ctx.Check(doc.Current.Items.Select(i => i.Name).SequenceEqual(["readme.txt", "pack.peg", "mesh.rfm"]) && view.Ps2BannerText == Ps2Packfiles.PegBanner, "Undo brings the .peg back (and the convert hint)");
            doc.Redo();
            ctx.Check(doc.Current.Contains("fire.atx") && !doc.Current.Contains("pack.peg"), "Redo converts again");
            doc.Undo();
            report = await module.ConvertPegTexturesAsync(doc, module.AllTextures(doc), showSkipped: false);
            ctx.Check(report is { Textures: 7, NotChosen: 0 } && doc.Current.Items.Select(i => i.Name).SequenceEqual(["readme.txt", .. SampleFiles, "mesh.rfm"]), "every texture, no dialog: " + report?.Summary);

            string pcPath = Path.Combine(folder, "ps2test_pc.vpp");
            doc.SaveTo(pcPath);
            var saved = VppPackage.Open(pcPath);
            ctx.Check(saved.Items.Select(i => i.Name).SequenceEqual(["readme.txt", .. SampleFiles, "mesh.rfm"]), "Save writes a PC packfile with the .tga entries");
            ctx.Check(TgaCodec.Decode(saved.Find("wall.tga")!.Source.ReadAll(), "wall.tga").Width == 16, "... which read back as images");
            ctx.Shell.Close(doc);
            doc = null;

            // one PEG at a time: a texture whose name the packfile already has keeps the larger version
            doc = new VppDocument(module, DuplicatesPackage(), "dups.vpp", null);
            ctx.Shell.AddDocument(doc);
            int Width(string name) => TgaCodec.Decode(doc!.Current.Find(name)!.Source.ReadAll(), name).Width;
            async Task<Ps2ConvertReport?> One(string peg) => await module.ConvertPegTexturesAsync(doc!, module.AllTextures(doc!, [peg]), showSkipped: false);
            report = await One("a.peg");
            ctx.Check(report is { Textures: 2, Animations: 1 } && Width("wall.tga") == 8 && doc.Current.Contains("boom.atx") && doc.Current.Contains("boom_01.tga"), "a.peg: wall.tga 8x8, boom.atx + 2 frames: " + report?.Summary);
            report = await One("c.peg");
            ctx.Check(report is { Textures: 0, IdenticalCopies: 2, AddedFiles.Count: 0 } && !doc.Current.Contains("c.peg"), "c.peg (the same textures): identical copies left out: " + report?.Summary);
            report = await One("d.peg");
            ctx.Check(report is { Textures: 0, DifferentCopies: 1 } && Width("wall.tga") == 8 && report.Conflicts!.Any(c => c.Contains("kept the 8×8 already in the packfile", StringComparison.Ordinal)),
                "d.peg (a smaller wall.tga): the packfile's larger one is kept: " + report?.Summary);
            report = await One("b.peg");
            var bNames = doc.Current.Items.Select(i => i.Name).ToList();
            ctx.Check(report is { Textures: 2 } && Width("wall.tga") == 16 && report.Conflicts!.Count(c => c.Contains("replaced the 8×8 already in the packfile by 16×16 from b.peg", StringComparison.Ordinal)) == 2,
                "b.peg (larger): wall.tga and boom.atx are replaced: " + report?.Summary);
            ctx.Check(bNames.Count(n => VppNames.Comparer.Equals(n, "wall.tga")) == 1 && !bNames.Contains("boom_00.tga") && bNames.Contains("boom1_00.tga")
                && System.Text.Encoding.UTF8.GetString(doc.Current.Find("boom.atx")!.Source.ReadAll()).Contains("boom1_00.tga", StringComparison.Ordinal),
                "... the old frames go with the old .atx: " + string.Join(", ", bNames));
            ctx.Check(report!.ReplacedEntries!.Order(StringComparer.Ordinal).SequenceEqual(["boom.atx", "boom_00.tga", "boom_01.tga", "wall.tga"]), "replaced entries: " + string.Join(", ", report.ReplacedEntries!));
            doc.Undo();
            ctx.Check(Width("wall.tga") == 8 && doc.Current.Contains("b.peg") && doc.Current.Contains("boom_00.tga"), "Undo brings back the smaller versions and b.peg");
        }
        finally
        {
            if (doc is not null && ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc);
            Work.VppWorkFolder.TryDeleteFolder(folder);
        }
    }

    /// <summary>
    /// <c>--dialog vpp.peg-convert</c>: "Convert to .tga..." for the active packfile's selected .peg entries (all of its
    /// .peg entries when none is selected), or for a sample PS2 packfile (two PEG files, one texture name already
    /// present); <c>--peg-black true</c> ticks "Treat black as transparent".
    /// </summary>
    [ScreenshotDialog("vpp.peg-convert")]
    public static async Task<System.Windows.Window?> ConvertDialog(ScreenshotContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        if (ctx.Shell.ActiveDocument is not VppDocument { PegEntryCount: > 0 } doc)
        {
            var wall = new BgraImage(8, 8);
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) wall.Set(x, y, 90, (byte)(y * 30), (byte)(x * 30), 255);
            var package = VppEdit.AddSources(VppPackage.Empty,
            [
                ("readme.txt", new MemorySource("PS2 sample"u8.ToArray())),
                ("pack.peg", new MemorySource(SyntheticPeg.Sample())),
                ("interface-bg-mm.peg", new MemorySource(SyntheticPeg.Mpeg2Sequence())),
                ("wall.tga", new MemorySource(TgaWriter.Write(wall, includeAlpha: true))),
                ("mesh.rfm", new MemorySource([0x12, 0x87, 0x12, 0x87, 0, 0, 0, 0])),
            ], VppClashPolicy.KeepBoth).Package;
            doc = new VppDocument(module, package, "ps2sample.vpp", null);
            ctx.Shell.AddDocument(doc);
            doc.SelectNames(["pack.peg", "interface-bg-mm.peg"]);
            await ctx.SettleAsync();
        }
        var pegs = doc.SelectedItems.Where(i => Ps2Packfiles.IsPeg(i.Name)).ToList();
        if (pegs.Count == 0) pegs = [.. Ps2Packfiles.Pegs(doc.Current)];
        var window = Dialogs.VppPegConvertWindow.Create(doc, module.ReadPegs(doc, pegs), module.Settings);
        window.Owner = ctx.MainWindow;
        if (ctx.Options.TryGetValue("peg-black", out var black) && !black.Equals("false", StringComparison.OrdinalIgnoreCase)) window.BlackTransparent = true;
        window.Show();
        await ctx.SettleAsync();
        return window;
    }

    /// <summary>
    /// A PS2 packfile whose PEG files share texture names: a.peg (wall.tga 8x8, boom.vbm 8x8 2 frames), b.peg (the same
    /// names at 16x16), c.peg (a copy of a.peg) and d.peg (wall.tga 4x4).
    /// </summary>
    internal static VppPackage DuplicatesPackage()
    {
        static uint Rgba(int r, int g, int b) => (uint)(r | g << 8 | b << 16 | 0x80 << 24);
        byte[] Peg(int size, bool anim) => anim
            ? SyntheticPeg.V6(SyntheticPeg.Texture("wall.tga", size, size, 7, 0, 1, 1, (f, m, x, y) => Rgba(x * 8, y * 8, size)),
                SyntheticPeg.Texture("boom.vbm", size, size, 7, 0, 2, 1, (f, m, x, y) => Rgba(f * 100, x * 8, size)))
            : SyntheticPeg.V6(SyntheticPeg.Texture("wall.tga", size, size, 7, 0, 1, 1, (f, m, x, y) => Rgba(x * 8, y * 8, size)));
        byte[] a = Peg(8, true);
        return VppEdit.AddSources(VppPackage.Empty,
        [
            ("a.peg", new MemorySource(a)),
            ("b.peg", new MemorySource(Peg(16, true))),
            ("c.peg", new MemorySource([.. a])),
            ("d.peg", new MemorySource(Peg(4, false))),
            ("mesh.rfm", new MemorySource([0x12, 0x87, 0x12, 0x87, 0, 0, 0, 0])),
        ], VppClashPolicy.KeepBoth).Package;
    }

    /// <summary>Name, length and time of every file under <paramref name="folder"/> (to prove nothing was written there).</summary>
    private static string Snapshot(string folder) => string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Order(StringComparer.OrdinalIgnoreCase).Select(f => { var i = new FileInfo(f); return $"{f}|{i.Length}|{i.LastWriteTimeUtc.Ticks}"; }));

    [SelfTest("vpp.ps2-real-files")]
    public static async Task RealFiles(SelfTestContext ctx)
    {
        if (LocalPaths.Ps2Directory is not { } dir) { ctx.Skip("no PlayStation 2 folder: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory")); return; }
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        string before = Snapshot(dir);
        var docs = new List<VppDocument>();
        try
        {
            foreach (string peg in Directory.EnumerateFiles(dir, "*.peg"))
            {
                var doc = (VppDocument)module.PegKind.Open(peg);
                docs.Add(doc);
                ctx.Check(doc.PegSource is { Error: null } && doc.Current.Count > 0 && doc.FilePath is null, $"{Path.GetFileName(peg)}: {doc.PegSource?.ConvertedTextures} textures in {doc.Current.Count} files");
            }
            // the smallest packfile with several .peg entries (else with one)
            string? vpp = Directory.EnumerateFiles(dir, "*.vpp").Select(p => (Path: p, Pegs: Ps2Packfiles.Pegs(VppPackage.Open(p)).Count)).Where(p => p.Pegs > 0)
                .OrderBy(p => p.Pegs > 1 ? 0 : 1).ThenBy(p => new FileInfo(p.Path).Length).Select(p => p.Path).FirstOrDefault();
            if (vpp is not null)
            {
                var doc = (VppDocument)module.Kind.Open(vpp);
                docs.Add(doc);
                ctx.Check(doc.IsPs2Packfile && doc.PegEntryCount > 0, $"{Path.GetFileName(vpp)} is a PlayStation 2 packfile with {doc.PegEntryCount} .peg");
                int pegs = doc.PegEntryCount;
                // one PEG first (as "Convert to .tga..." on one entry), then the rest in one go
                string first = Ps2Packfiles.Pegs(doc.Current)[0].Name;
                var one = await module.ConvertPegTexturesAsync(doc, module.AllTextures(doc, [first]), showSkipped: false);
                ctx.Check(one is { Unreadable.Count: 0, ReplacedPegs.Count: 1 } && doc.PegEntryCount == pegs - 1 && !doc.Current.Contains(first), $"{first} alone: {one?.Summary}");
                if (pegs > 1)
                {
                    var report = await module.ConvertPegTexturesAsync(doc, module.AllTextures(doc), showSkipped: false);
                    ctx.Check(report is { Unreadable.Count: 0 } && doc.PegEntryCount == 0, $"{Path.GetFileName(vpp)}, the other {pegs - 1}: {report?.Summary}");
                }
                var duplicates = doc.Current.Items.GroupBy(i => i.Name, Editing.VppNames.Comparer).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                ctx.Check(duplicates.Count == 0, "no name twice after converting one at a time: " + string.Join(", ", duplicates.Take(5)));
                doc.Undo();
                if (pegs > 1) doc.Undo();
                ctx.Check(doc.PegEntryCount == pegs && !doc.IsDirty, "undone: the packfile is as it was on disk");
                ctx.Check(!doc.HasWorkFolder || doc.Work.Root.StartsWith(Path.GetFullPath(module.Settings.WorkRoot), StringComparison.OrdinalIgnoreCase), "work copies go to the work folder, not beside the file");
            }
            ctx.Check(Snapshot(dir) == before, "nothing was written in the PlayStation 2 folder");
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }
}
