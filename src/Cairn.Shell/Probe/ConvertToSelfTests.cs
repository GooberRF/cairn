using System.Windows;
using System.Windows.Controls;
using Cairn.Formats.Imaging;
using Cairn.Shell.Probe;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;

namespace Cairn.Shell;

/// <summary>
/// File › Convert to… and the packfile entry's Convert to submenu: the targets every module registers for a file are
/// listed (grouped by module, lossy ones marked), the menu hides for a file nothing converts, the request carries the
/// tab's unsaved content (and a work copy's packfile), and one conversion per module runs without a dialog. The files
/// are synthetic, written to a temporary folder.
/// </summary>
internal static class ConvertToSelfTests
{
    private sealed class RecordingDialogs : DialogService
    {
        public List<string> Errors { get; } = [];
        public override void ShowError(string heading, string body, string? details = null) => Errors.Add(heading + ": " + body);
        public override bool Confirm(string heading, string body, string confirmText) => true;
    }

    /// <summary>The submenu's items as text: captions in brackets, separators as "-".</summary>
    private static List<string> MenuTexts(ItemsControl menu) =>
        [.. menu.Items.Cast<object>().Select(i => i switch
        {
            Separator => "-",
            MenuItem { IsHitTestVisible: false } caption => "[" + caption.Header + "]",
            MenuItem item => item.Header as string ?? "?",
            _ => "?",
        })];

    private static string Describe(IEnumerable<ConversionChoice> choices) =>
        string.Join(", ", choices.Select(c => $"{c.Module.Id}:{c.Target.Extension}{(c.Target.Lossy ? " lossy" : "")}"));

    /// <summary>The shell's half through the probe module: the submenu, the request, a conversion without a dialog.</summary>
    [SelfTest("shell.convert-to")]
    public static Task ConvertTo(SelfTestContext ctx) => RunAsync(ctx, async c =>
    {
        ctx.Check(AssetConversions.MenuText(new ConversionTarget(".x", "Thing_s (.x)", "", true)) == "Thing__s (.x) (lossy)…", "menu text: access keys escaped, (lossy) and the ellipsis");
        ctx.Check(c.Window.ConvertMenu.Visibility == Visibility.Collapsed || c.Shell.ActiveDocument is not null, "File › Convert to is hidden with no tab");
        await ProbeAsync(c);
    });

    /// <summary>A file of each type the modules convert: its targets, and one conversion per module without a dialog.</summary>
    [SelfTest("shell.convert-to-modules")]
    public static Task ConvertToModules(SelfTestContext ctx) => RunAsync(ctx, ModulesAsync);

    /// <summary>The packfile entry's submenu and its conversions into the packfile.</summary>
    [SelfTest("shell.convert-to-packfile")]
    public static Task ConvertToPackfile(SelfTestContext ctx) => RunAsync(ctx, async c =>
    {
#if CAIRN_MODULE_VPP
        if (c.Shell.Modules.Any(m => m.Id == "vpp")) { await PackfileAsync(c); return; }
#endif
        await Task.CompletedTask;
        ctx.Skip("needs the packfile module");
    });

    /// <summary>Runs <paramref name="body"/> with recorded error dialogs and a temporary folder; closes what it opened.</summary>
    private static async Task RunAsync(SelfTestContext ctx, Func<Context, Task> body)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var window = (MainWindow)ctx.MainWindow;
        var original = shell.Dialogs;
        var dialogs = new RecordingDialogs();
        shell.Dialogs = dialogs;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-convert-to-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<IDocument>();
        try
        {
            await body(new Context(ctx, shell, window, folder, opened, dialogs));
            if (ctx.SkipReason is null)
                ctx.Check(dialogs.Errors.Count == 0, "no error was reported" + (dialogs.Errors.Count > 0 ? ": " + string.Join(" | ", dialogs.Errors) : ""));
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            foreach (var d in shell.Documents.Where(d => d.FilePath?.StartsWith(folder, StringComparison.OrdinalIgnoreCase) == true).ToList()) shell.CloseDiscarding(d);
            shell.Dialogs = original;
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record Context(SelfTestContext Ctx, ShellViewModel Shell, MainWindow Window, string Folder, List<IDocument> Opened, RecordingDialogs Dialogs)
    {
        /// <summary>
        /// Lets the UI and short background work finish. Bounded at a few seconds: with a game directory configured the
        /// libraries keep indexing for minutes, and nothing here waits for them.
        /// </summary>
        public Task SettleAsync() => Ctx.SettleAsync(TimeSpan.FromSeconds(5));

        /// <summary>Writes a file into the temporary folder and opens it as a tab.</summary>
        public async Task<IDocument?> OpenAsync(string name, byte[] bytes)
        {
            string path = Path.Combine(Folder, name);
            File.WriteAllBytes(path, bytes);
            bool ok = Shell.OpenFile(path);
            await SettleAsync();
            if (!Ctx.Check(ok && Shell.ActiveDocument is not null, $"{name} opens")) return null;
            if (!Opened.Contains(Shell.ActiveDocument!)) Opened.Add(Shell.ActiveDocument!);
            return Shell.ActiveDocument;
        }

        /// <summary>Checks the tab in front lists exactly <paramref name="expected"/> ("module:.ext" plus " lossy"), in the menu too.</summary>
        public void ExpectTargets(string what, params string[] expected)
        {
            var choices = Shell.ConversionsForActive;
            var got = choices.Select(c => $"{c.Module.Id}:{c.Target.Extension}{(c.Target.Lossy ? " lossy" : "")}").ToList();
            Ctx.Check(got.SequenceEqual(expected), $"{what}: targets {Describe(choices)} (expected {string.Join(", ", expected)})");
            var menu = Window.ConvertMenu;
            Window.ConvertMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));
            var texts = MenuTexts(menu);
            if (expected.Length == 0)
            {
                Ctx.Check(menu.Visibility == Visibility.Collapsed, $"{what}: File › Convert to is hidden");
                return;
            }
            Ctx.Check(menu.Visibility == Visibility.Visible, $"{what}: File › Convert to is shown");
            var modules = choices.Select(c => c.Module).Distinct().ToList();
            var captions = texts.Where(t => t.StartsWith('[')).ToList();
            Ctx.Check(captions.SequenceEqual(modules.Select(m => "[" + m.DisplayName + "]")), $"{what}: one caption per module, in load order ({string.Join(" ", texts)})");
            Ctx.Check(texts.Count(t => t == "-") == modules.Count - 1, $"{what}: a separator between module groups");
            Ctx.Check(choices.All(c => texts.Contains(AssetConversions.MenuText(c.Target))) && texts.Count(t => t.Contains(AssetConversions.LossyMarker, StringComparison.Ordinal)) == choices.Count(c => c.Target.Lossy),
                $"{what}: each target once, the lossy ones marked");
        }

        /// <summary>Runs the tab in front's target <paramref name="module"/>:<paramref name="extension"/> without a dialog into a fresh folder.</summary>
        public async Task<(string? Result, string Output)> RunAsync(string module, string extension)
        {
            string output = Path.Combine(Folder, "out-" + module + extension.TrimStart('.'));
            Directory.CreateDirectory(output);
            var choice = Shell.ConversionsForActive.FirstOrDefault(c => c.Module.Id == module && c.Target.Extension == extension);
            if (!Ctx.Check(choice is not null, $"{module} offers {extension}")) return (null, output);
            string? result = await Shell.ConvertActiveAsync(choice!, interactive: false, outputFolder: output);
            await SettleAsync();
            if (Shell.ActiveDocument is { } now && !Opened.Contains(now)) Opened.Add(now);
            return (result, output);
        }
    }

    // ---- the probe module: the shell's half without any real module ----------------------------------------------

    private static async Task ProbeAsync(Context c)
    {
        var (ctx, shell) = (c.Ctx, c.Shell);
        if (shell.Modules.OfType<ProbeModule>().FirstOrDefault() is null) { ctx.Log("probe part skipped (no --probe-module)"); return; }
        if (await c.OpenAsync("notes.cairnprobe", "abc"u8.ToArray()) is not ProbeDocument doc) return;
        c.ExpectTargets("probe document", "probe:.txt", "probe:.shout lossy");
        doc.Text = "unsaved edit";
        var (result, output) = await c.RunAsync("probe", ".shout");
        var request = ProbeModule.LastRequest;
        ctx.Check(result == Path.Combine(output, "notes.shout") && File.Exists(result) && File.ReadAllText(result) == "UNSAVED EDIT", $"the conversion ran without a dialog on the unsaved text ({result})");
        ctx.Check(request is { FileName: "notes.cairnprobe", Interactive: false, ArchivePath: null, AddToArchive: null } && ReferenceEquals(request.Document, doc)
            && request.FilePath == doc.FilePath, "the request names the file, its path and the tab; no packfile");
        var two = shell.Modules.OfType<ProbeTwoModule>().FirstOrDefault();
        if (two?.Kind.CreateNew() is { } other)
        {
            shell.AddDocument(other);
            c.Opened.Add(other);
            c.ExpectTargets("second probe kind (nothing converts it)");
        }
        shell.Activate(doc);
        await c.SettleAsync();
        ctx.Check(c.Window.ConvertMenu.Visibility == Visibility.Visible, "the submenu comes back when a convertible tab is in front again");
    }

    // ---- the real modules: one file of each type, one conversion per module ------------------------------------

    private static async Task ModulesAsync(Context c)
    {
        var (ctx, shell) = (c.Ctx, c.Shell);
        bool Has(string id) => shell.Modules.Any(m => m.Id == id);
#if CAIRN_MODULE_VBM
        if (Has("vbm"))
        {
            var frames = Enumerable.Range(0, 3).Select(i => { var image = new BgraImage(16, 8); image.Pixels.AsSpan().Fill((byte)(40 * i + 60)); return image; }).ToList();
            var vbm = Cairn.Vbm.VbmEditing.Create(frames, VbmPixelFormat.Argb4444, 10, 1).Write();
            if (await c.OpenAsync("fire.vbm", vbm) is not null)
            {
                c.ExpectTargets(".vbm", [.. (Has("atx") ? new[] { "atx:.atx" } : []), "vbm:.tga lossy", "vbm:.png lossy"]);
                var (result, output) = await c.RunAsync("vbm", ".png");
                ctx.Check(result == output && Directory.GetFiles(output, "*.png").Length == 3, $"VBM frames as PNG: {Directory.GetFiles(output).Length} files in {result}");
#if CAIRN_MODULE_ATX
                if (Has("atx"))
                {
                    var (atx, _) = await c.RunAsync("atx", ".atx");
                    ctx.Check(atx is not null && File.Exists(atx) && atx.EndsWith(".atx", StringComparison.OrdinalIgnoreCase), $"VBM to ATX wrote {atx}");
                    ctx.Check(shell.ActiveDocument?.FilePath is { } p && string.Equals(p, atx, StringComparison.OrdinalIgnoreCase), "the .atx opens in a tab");
                    if (shell.ActiveDocument is not null) c.ExpectTargets(".atx (nothing converts it)");
                }
#endif
            }
        }
#endif
#if CAIRN_MODULE_VF
        if (Has("vf") && await c.OpenAsync("sample.vf", Cairn.Vf.Formats.VfWriter.Write(Cairn.Vf.Model.VfSamples.Create(Cairn.Vf.Model.VfPixelFormat.Mono))) is not null)
        {
            c.ExpectTargets(".vf", "vf:.png");
            var (result, output) = await c.RunAsync("vf", ".png");
            ctx.Check(result == Path.Combine(output, "sample.png") && File.Exists(result) && File.Exists(Path.Combine(output, "sample.json")), $"font image sheet: {result}");
        }
#endif
#if CAIRN_MODULE_VFX
        if (Has("vfx") && await c.OpenAsync("flash.vfx", Cairn.Vfx.Formats.VfxWriter.Write(Cairn.Vfx.Ui.Dialogs.VfxCreation.Template("Additive flash"))) is not null)
        {
            c.ExpectTargets(".vfx", "vfx:.gltf", "vfx:.glb");
            var (result, output) = await c.RunAsync("vfx", ".glb");
            ctx.Check(result == Path.Combine(output, "flash.glb") && File.Exists(result) && new FileInfo(result).Length > 0, $"effect as GLB: {result}");
        }
#endif
#if CAIRN_MODULE_RFA
        if (Has("rfa"))
        {
            var v3d = Cairn.Rfa.Formats.Legacy.SyntheticExporterMesh.SampleStatic();
            if (await c.OpenAsync("box.v3d", v3d) is not null)
            {
                c.ExpectTargets(".v3d", "rfa:.v3m lossy", "rfa:.gltf lossy", "rfa:.glb lossy");
                var (result, output) = await c.RunAsync("rfa", ".v3m");
                ctx.Check(result is not null && File.Exists(Path.Combine(output, "box.v3m")), $"exporter mesh to .v3m: {result}");
            }
            var v3m = Cairn.Rfa.Formats.Legacy.LegacyMeshSupport.Convert(v3d, "box.v3d").Bytes;
            if (await c.OpenAsync("crate.v3m", v3m) is not null)
            {
                // meshes also list OBJ, and with the levels module an editor group file
                c.ExpectTargets(".v3m", ["rfa:.gltf", "rfa:.glb", "rfa:.obj lossy", .. Has("lvl") ? new[] { "lvl:.rfg lossy" } : []]);
                var (result, output) = await c.RunAsync("rfa", ".gltf");
                ctx.Check(result == Path.Combine(output, "crate.gltf") && File.Exists(result) && File.Exists(Path.Combine(output, "crate.bin")), $"mesh as glTF: {result}");
            }
            ctx.Check(AssetConversions.TargetsFor(shell.Modules, "walk.rfa").Select(x => x.Target.Extension).SequenceEqual([".gltf", ".glb"])
                && AssetConversions.TargetsFor(shell.Modules, "guard.rfc").Any(x => x.Target.Extension == ".v3c" && x.Target.Lossy), "clips list glTF; PS2 characters list .v3c");
        }
#endif
#if CAIRN_MODULE_SND
        if (Has("snd") && await c.OpenAsync("shot.vse", Cairn.Snd.SyntheticSounds.OneShotVse(400)) is not null)
        {
            c.ExpectTargets(".vse", [.. new[] { "snd:.wav" }, .. Cairn.Formats.Audio.OggVorbisWriter.IsAvailable ? new[] { "snd:.ogg lossy" } : []]);
            var (result, output) = await c.RunAsync("snd", ".wav");
            ctx.Check(result is not null && File.Exists(Path.Combine(output, "shot.wav")), $"sound to WAV: {result}");
            ctx.Check(AssetConversions.TargetsFor(shell.Modules, "music.wav").All(x => x.Target.Extension != ".wav"), "a .wav is not offered .wav");
        }
#endif
#if CAIRN_MODULE_TBL
        if (Has("tbl") && await c.OpenAsync("custom.tbl", "#General\n$Name: \"x\"\n#End\n"u8.ToArray()) is not null)
            c.ExpectTargets(".tbl (nothing converts it)");
#endif
    }

#if CAIRN_MODULE_VPP
    /// <summary>The packfile entry submenu: shown for one entry some module converts; the packfile's own DDS and another module's conversion into the packfile (one undo step); a work copy's request names the packfile.</summary>
    private static async Task PackfileAsync(Context c)
    {
        var (ctx, shell) = (c.Ctx, c.Shell);
        var vpp = shell.Modules.OfType<Cairn.Vpp.Ui.VppModule>().First();
        var image = new BgraImage(16, 16);
        image.Pixels.AsSpan().Fill(200);
        var files = new List<(string, byte[])> { ("tex.png", PngEncoder.Encode(image)), ("readme.txt", "not convertible"u8.ToArray()) };
#if CAIRN_MODULE_SND
        files.Add(("shot.vse", Cairn.Snd.SyntheticSounds.OneShotVse(300)));
#endif
        var package = Cairn.Vpp.Model.VppPackage.Empty;
        foreach (var (name, bytes) in files) package = Cairn.Vpp.Editing.VppEdit.AddBytes(package, name, bytes, Cairn.Vpp.Editing.VppClashPolicy.Replace).Package;
        string packPath = Path.Combine(c.Folder, "convert.vpp");
        Cairn.Vpp.Writing.VppSaver.Save(package, packPath, null, CancellationToken.None, Cairn.Vpp.Writing.VppSaveOptions.Default);
        if (!ctx.Check(shell.OpenFile(packPath) && shell.ActiveDocument is Cairn.Vpp.Ui.Documents.VppDocument, "the packfile opens")) return;
        var doc = (Cairn.Vpp.Ui.Documents.VppDocument)shell.ActiveDocument!;
        c.Opened.Add(doc);
        await c.SettleAsync();
        c.ExpectTargets(".vpp (nothing converts a packfile)");
        if (doc.View is not Cairn.Vpp.Ui.Documents.VppDocumentView view || view.FileList.ContextMenuConvertToItem is not { } submenu)
        {
            ctx.Check(false, "the entry list has a Convert to submenu");
            return;
        }
        async Task<List<string>> Select(params string[] names)
        {
            doc.SelectNames(names);
            await c.SettleAsync();
            vpp.FillConvertToMenu(doc, submenu);
            return submenu.Visibility == Visibility.Visible ? MenuTexts(submenu) : [];
        }
        var tex = await Select("tex.png");
        ctx.Check(tex.SequenceEqual(["[" + vpp.DisplayName + "]", "DDS texture (.dds) (lossy)…"]), $"tex.png: {string.Join(" ", tex)}");
        ctx.Check((await Select("readme.txt")).Count == 0, "a text entry: no Convert to");
        ctx.Check((await Select("tex.png", "readme.txt")).Count == 0, "two entries: no Convert to (batches keep their own commands)");
        ctx.Check(AssetConversions.TargetsFor(shell.Modules, "skins.peg").Any(x => x.Module == vpp && x.Target.Extension == ".tga"), "a .peg lists .tga");

        var texItem = doc.Current.Find("tex.png")!;
        var dds = AssetConversions.TargetsFor(shell.Modules, "tex.png").First(x => x.Target.Extension == ".dds");
        string output = Path.Combine(c.Folder, "out-vppdds");
        string? toFolder = await vpp.ConvertEntryAsync(doc, texItem, dds, interactive: false, outputFolder: output);
        ctx.Check(toFolder == Path.Combine(output, "tex.dds") && File.Exists(toFolder) && doc.Current.Find("tex.dds") is null, $"DDS into a folder: {toFolder}");
        string? intoPack = await vpp.ConvertEntryAsync(doc, texItem, dds, interactive: false);
        ctx.Check(intoPack == "tex.dds" && doc.Current.Find("tex.dds") is not null && doc.Current.Find("tex.png") is not null && doc.UndoLabel?.Contains("DDS", StringComparison.Ordinal) == true,
            $"DDS into the packfile beside the image, one undo step ({doc.UndoLabel})");
#if CAIRN_MODULE_SND
        if (shell.Modules.Any(m => m.Id == "snd"))
        {
            var shot = doc.Current.Find("shot.vse")!;
            var wav = AssetConversions.TargetsFor(shell.Modules, "shot.vse").First(x => x.Target.Extension == ".wav");
            ctx.Check((await Select("shot.vse")).Contains("[Sounds]"), "shot.vse: the sounds module's group");
            string? added = await vpp.ConvertEntryAsync(doc, shot, wav, interactive: false);
            ctx.Check(added == "shot.wav" && doc.Current.Find("shot.wav") is not null && doc.UndoLabel == "Convert shot.vse to WAV", $"another module's conversion adds into the packfile as one undo step ({added}, {doc.UndoLabel})");

            // a work copy (Open in Cairn): the request names the packfile, not the temporary file, and can add to it
            await doc.Commands.OpenInCairnAsync(shot);
            await c.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is Cairn.Snd.Ui.Documents.SndDocument, "Open in Cairn opens the sound"))
            {
                var tab = shell.ActiveDocument!;
                c.Opened.Add(tab);
                var request = AssetConversions.RequestFor(shell, tab, ".wav", interactive: false);
                ctx.Check(request.FileName == "shot.vse" && request.FilePath is null && string.Equals(request.ArchivePath, packPath, StringComparison.OrdinalIgnoreCase)
                    && request.AddToArchive is not null && request.ArchiveEntryNames.Contains("tex.png") && request.ReadSibling?.Invoke("tex.png") is { Length: > 0 },
                    $"work copy request: {request.FileName}, archive {request.ArchivePath}, {request.ArchiveEntryNames.Count} names");
            }
        }
#endif
    }
#endif
}
