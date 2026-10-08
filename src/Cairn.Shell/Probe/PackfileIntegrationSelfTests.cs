#if CAIRN_MODULE_VPP && CAIRN_MODULE_ATX && CAIRN_MODULE_RFA && CAIRN_MODULE_VFX
using System.Diagnostics;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Vpp.Writing;

namespace Cairn.Shell;

/// <summary>
/// Packfiles together with the other modules (all four loaded): every type previews in the packfile tab, "Open in Cairn"
/// opens a .v3c, .rfa, .vfx and .atx entry in its own module from the work copy, and saving that tab offers to update the
/// packfile, whose saved file then holds the tab's bytes. The packfile is generated from the repository's samples.
/// </summary>
public static class PackfileIntegrationSelfTests
{
    private static readonly (string Sample, string Module)[] Entries =
    [
        (@"rfa\sample_figure.v3c", "rfa"), (@"rfa\sample_figure_walk.rfa", "rfa"), (@"rfa\sample_figure.tga", ""),
        (@"vfx\Additive_flash.vfx", "vfx"), (@"atx\hazard_strip.atx", "atx"), (@"atx\hazard_strip_00.tga", ""),
    ];

    /// <summary>The repository's samples folder, found upwards from the executable; null when this is not a source tree.</summary>
    internal static string? SamplesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "samples");
            if (File.Exists(Path.Combine(candidate, "rfa", "sample_figure.v3c"))) return candidate;
        }
        return null;
    }

    [SelfTest("integration.packfile-open-in-cairn")]
    public static async Task OpenInCairn(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        string? samples = SamplesFolder();
        if (samples is null) { ctx.Skip("the repository's samples folder was not found"); return; }
        string folder = Path.Combine(Path.GetTempPath(), "cairn-int-vpp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<IDocument>();
        try
        {
            var package = VppEdit.AddFiles(VppPackage.Empty, [.. Entries.Select(e => Path.Combine(samples, e.Sample))], VppClashPolicy.Replace).Package;
            string packPath = Path.Combine(folder, "together.vpp");
            VppSaver.Save(package, packPath, null, CancellationToken.None, VppSaveOptions.Default);
            if (!ctx.Check(shell.OpenFile(packPath) && shell.ActiveDocument is VppDocument, "the packfile opens in a tab")) return;
            var doc = (VppDocument)shell.ActiveDocument!;
            opened.Add(doc);
            await ctx.SettleAsync(TimeSpan.FromSeconds(30));
            var view = (VppDocumentView)doc.View;

            // every entry previews in the tab; the meshes, clips, effects and animated textures through their modules
            foreach (var item in doc.Current.Items)
            {
                doc.SetSelection([item]);
                await Task.Delay(400);
                var pane = view.PreviewHost.Content as VppPreviewPane;
                if (pane is not null) await pane.Pending;
                await ctx.SettleAsync(TimeSpan.FromSeconds(20));
                var expected = item.Extension == ".tga" ? Cairn.Previews.AssetPreviewKind.Image : Cairn.Previews.AssetPreviewKind.Module;
                ctx.Check(pane?.Kind == expected, $"{item.Name} previews as {expected} ({pane?.Kind})");
            }

            foreach (var (sample, moduleId) in Entries.Where(e => e.Module.Length > 0))
            {
                string name = Path.GetFileName(sample);
                shell.Activate(doc);
                doc.SelectNames([name]);
                if (!ctx.Check(doc.Commands.CanOpenInCairn, $"{name}: Open in Cairn is available")) continue;
                int before = shell.Documents.Count;
                await doc.Commands.OpenInCairnAsync();
                await ctx.SettleAsync(TimeSpan.FromSeconds(30));
                var tab = shell.ActiveDocument;
                string? copy = doc.Work.PathOf(name);
                var owner = tab is null ? null : shell.Modules.FirstOrDefault(m => m.DocumentKinds.Any(k => k.Id == tab.Kind.Id));
                if (!ctx.Check(tab is not null && tab != doc && shell.Documents.Count == before + 1, $"{name}: opens in a new tab")) continue;
                opened.Add(tab!);
                ctx.Check(owner?.Id == moduleId, $"{name}: in the {moduleId} module ({owner?.Id})");
                ctx.Check(copy is not null && string.Equals(tab!.FilePath, copy, StringComparison.OrdinalIgnoreCase), $"{name}: from the packfile's work copy ({tab!.FilePath})");

                if (!ctx.Check(Edit(tab) && tab.IsDirty, $"{name}: edited in its tab")) continue;
                ctx.Check(shell.Save(tab) && !tab.IsDirty, $"{name}: the tab saves (to the work copy)");
                byte[] saved = File.ReadAllBytes(copy!);
                var sw = Stopwatch.StartNew();
                while (doc.WorkChanges.Count == 0 && sw.ElapsedMilliseconds < 10_000) await Task.Delay(100);
                await Task.Delay(100);
                ctx.Check(doc.WorkChanges.Any(c => string.Equals(c.EntryName, name, StringComparison.OrdinalIgnoreCase)), $"{name}: the packfile notices the saved work copy ({sw.ElapsedMilliseconds} ms)");
                ctx.Check(view.IsWorkBarVisible, $"{name}: the \"Update packfile\" bar is shown");
                ctx.Check(doc.Commands.UpdateFromWorkCopies() && doc.IsDirty, $"{name}: Update packfile replaces the entry ({doc.UndoLabel})");
                ctx.Check(doc.Current.Find(name)!.Source.ReadAll().AsSpan().SequenceEqual(saved), $"{name}: the entry now holds the tab's saved bytes");
            }

            string savedPack = Path.Combine(folder, "together-saved.vpp");
            doc.SaveTo(savedPack);
            var reopened = VppPackage.Open(savedPack);
            foreach (var (sample, _) in Entries.Where(e => e.Module.Length > 0))
            {
                string name = Path.GetFileName(sample);
                string? copy = doc.Work.PathOf(name);
                bool same = copy is not null && reopened.Find(name)?.Source.ReadAll().AsSpan().SequenceEqual(File.ReadAllBytes(copy)) == true;
                ctx.Check(same, $"{name}: the saved packfile holds the edited bytes");
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The shell with packfiles in it: the File associations page has a row for every extension of every module's kinds
    /// (".vpp" included), F1 on a packfile opens the packfile help topic, and Settings shows the "Packfiles" page.
    /// </summary>
    [SelfTest("integration.packfile-shell")]
    public static async Task PackfileInShell(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var store = new FakeAssociationStore();
        var model = new Dialogs.AssociationsModel(store, shell.Modules, shell.Dialogs);
        model.Refresh(keepPending: false);
        var rows = model.Rows.Select(r => r.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = shell.Modules.SelectMany(m => m.DocumentKinds).SelectMany(k => k.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = expected.Where(e => !rows.Contains(e)).ToList();
        ctx.Check(missing.Count == 0, $"File associations lists every kind's extension ({expected.Count}: {string.Join(" ", expected)}){(missing.Count > 0 ? "; missing " + string.Join(" ", missing) : "")}");
        ctx.Check(model.Rows.Any(r => r.Extension == ".vpp" && r.Group == "Packfiles"), "... including .vpp, under Packfiles");
        ctx.Check(model.Rows.Any(r => r.Extension == ".peg" && r.Group == "Packfiles"), "... and .peg (PS2 texture packs open in the packfile view)");
        // Each extension sits under the module whose kind opens it (.vbm under Volition bitmaps, not ATX's VBM importer).
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in shell.Modules)
            foreach (var e in m.DocumentKinds.SelectMany(k => k.Extensions)) owner.TryAdd(e, m.DisplayName);
        var misplaced = model.Rows.Where(r => owner.TryGetValue(r.Extension, out var g) && g != r.Group).Select(r => $"{r.Extension} under {r.Group}").ToList();
        ctx.Check(misplaced.Count == 0, $"... each under the module that opens it{(misplaced.Count > 0 ? ": " + string.Join(", ", misplaced) : "")}");
        ctx.Check(store.Writes == 0, "... and loading the page writes nothing");

        var settings = new Dialogs.SettingsViewModel(shell, store);
        var titles = settings.Pages.Select(p => p.Title).ToList();
        ctx.Check(titles.Contains("Packfiles"), $"Settings shows the Packfiles page ({string.Join(", ", titles)})");

        string? samples = SamplesFolder();
        if (samples is null) { ctx.Skip("the repository's samples folder was not found"); return; }
        string folder = Path.Combine(Path.GetTempPath(), "cairn-int-vpp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        IDocument? opened = null;
        try
        {
            string packPath = Path.Combine(folder, "help.vpp");
            VppSaver.Save(VppEdit.AddFiles(VppPackage.Empty, [Path.Combine(samples, @"vfx\Additive_flash.vfx")], VppClashPolicy.Replace).Package,
                packPath, null, CancellationToken.None, VppSaveOptions.Default);
            if (!ctx.Check(shell.OpenFile(packPath) && shell.ActiveDocument is VppDocument, "a packfile is the active tab")) return;
            opened = shell.ActiveDocument;
            await ctx.SettleAsync(TimeSpan.FromSeconds(20));
            ctx.Check(shell.ContextHelpTopic?.Id == "vpp.format", $"F1's topic for a packfile is the packfile help ({shell.ContextHelpTopic?.Id})");
            var applying = shell.AllShortcuts.Where(s => s.Key == System.Windows.Input.Key.F1 && s.Modifiers == System.Windows.Input.ModifierKeys.None && (s.AppliesTo?.Invoke(opened) ?? true)).ToList();
            // the shell's rows are rebuilt per call, so they are matched by gesture and description
            bool IsShell(ShortcutInfo s) => shell.ShellShortcuts.Any(x => x.Key == s.Key && x.Modifiers == s.Modifiers && x.Description == s.Description);
            ctx.Log("F1 rows applying to a packfile: " + string.Join(", ", applying.Select(s => $"'{s.Description}' ({(IsShell(s) ? "shell" : shell.Modules.FirstOrDefault(m => m.Shortcuts.Contains(s))?.DisplayName ?? "?")})")));
            var f1 = applying.FirstOrDefault(IsShell);
            ctx.Check(f1 is not null && !applying.Any(s => shell.Modules.Any(m => m.Id == "vpp" && m.Shortcuts.Contains(s))), "F1 on a packfile is the shell's context help (the packfile module does not take it)");
            f1?.Command.Execute(null);
            await ctx.SettleAsync(TimeSpan.FromSeconds(10));
            var help = Dialogs.HelpWindow.Current;
            ctx.Check(help?.Topics.SelectedItem is Cairn.Ui.Modules.HelpTopic { Id: "vpp.format" }, $"F1 opens the help window on the packfile topic ({help?.Title})");
            help?.Close();
        }
        finally
        {
            if (opened is not null && shell.Documents.Contains(opened)) shell.CloseDiscarding(opened);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The entry types the stock and user_maps scans never reached (.mp3 .gltf .log .mvf .psd .v3d, MP3-in-WAVE, IMA
    /// ADPCM WAVE): the first of each found anywhere in the game folder (mods and client_mods too) is copied into a
    /// generated packfile; selecting each in the tab shows a preview and details without an error.
    /// </summary>
    [SelfTest("integration.packfile-uncovered-previews")]
    public static async Task UncoveredPreviews(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        string? game = shell.Settings.GameDirectory;
        if (string.IsNullOrEmpty(game) || !Directory.Exists(game)) { ctx.Skip("no game directory"); return; }
        var wanted = new Dictionary<string, (string Name, byte[] Bytes)?>
        {
            [".mp3"] = null, [".gltf"] = null, [".log"] = null, [".mvf"] = null, [".psd"] = null, [".v3d"] = null, ["wav/mp3"] = null, ["wav/ima"] = null,
        };
        var sw = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            foreach (string path in Directory.EnumerateFiles(game, "*.vpp", SearchOption.AllDirectories))
            {
                if (wanted.Values.All(v => v is not null)) break;
                Cairn.Formats.Vpp.VppArchive archive;
                try { archive = Cairn.Formats.Vpp.VppArchive.Open(path); }
                catch (Exception ex) when (ex is Cairn.Formats.AssetFormatException or IOException or UnauthorizedAccessException) { continue; }
                foreach (var entry in archive.Entries)
                {
                    if (entry.Size > 4 << 20) continue;
                    string key = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (key == ".wav" && entry.Size > 40 && (wanted["wav/mp3"] is null || wanted["wav/ima"] is null))
                    {
                        byte[] head = new byte[22];
                        using (var s = archive.OpenEntry(entry)) s.ReadExactly(head);
                        key = BitConverter.ToUInt16(head, 20) switch { 0x55 => "wav/mp3", 0x11 => "wav/ima", _ => key };
                    }
                    if (wanted.TryGetValue(key, out var have) && have is null && (key != ".log" || entry.Size > 0))
                        wanted[key] = (entry.Name, archive.ReadEntry(entry));
                }
            }
        });
        ctx.Log($"scan: {sw.ElapsedMilliseconds:N0} ms; found {string.Join(", ", wanted.Where(w => w.Value is not null).Select(w => $"{w.Key} {w.Value!.Value.Name}"))}");
        if (wanted.Values.All(v => v is null)) { ctx.Skip("none of these types is in the game folder"); return; }

        string folder = Path.Combine(Path.GetTempPath(), "cairn-int-vpp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        IDocument? opened = null;
        try
        {
            var package = VppPackage.Empty;
            foreach (var (_, value) in wanted)
                if (value is { } v) package = VppEdit.AddBytes(package, v.Name, v.Bytes, VppClashPolicy.KeepBoth).Package;
            string packPath = Path.Combine(folder, "uncovered.vpp");
            VppSaver.Save(package, packPath, null, CancellationToken.None, VppSaveOptions.Default);
            if (!ctx.Check(shell.OpenFile(packPath) && shell.ActiveDocument is VppDocument, "the packfile opens")) return;
            var doc = (VppDocument)shell.ActiveDocument!;
            opened = doc;
            var view = (VppDocumentView)doc.View;
            int errorsBefore = (shell.Dialogs as Cairn.Ui.Services.DialogService)?.CollectErrors?.Count ?? 0;
            foreach (var item in doc.Current.Items)
            {
                doc.SetSelection([item]);
                await Task.Delay(400);
                var pane = view.PreviewHost.Content as VppPreviewPane;
                var details = view.DetailsHost.Content as Cairn.Vpp.Ui.Details.VppDetailsPane;
                if (pane is not null) await pane.Pending;
                if (details is not null) await details.Pending;
                await ctx.SettleAsync(TimeSpan.FromSeconds(20));
                var rows = details?.Rows ?? [];
                string? error = rows.FirstOrDefault(r => r.Label == "Error")?.Value;
                ctx.Log($"  {item.Name}: preview {pane?.Kind}; {rows.Count} detail rows: {string.Join("; ", rows.Where(r => r.Section != "Entry").Take(6).Select(r => $"{r.Label}={r.Value}"))}");
                ctx.Check(pane is not null && pane.Kind is not (Cairn.Previews.AssetPreviewKind.Empty or Cairn.Previews.AssetPreviewKind.Loading), $"{item.Name}: a preview ({pane?.Kind})");
                ctx.Check(rows.Count > 3 && error is null, $"{item.Name}: details without an error{(error is null ? "" : ": " + error)}");
            }
            int errorsAfter = (shell.Dialogs as Cairn.Ui.Services.DialogService)?.CollectErrors?.Count ?? 0;
            ctx.Check(errorsAfter == errorsBefore, "no error dialog");
        }
        finally
        {
            if (opened is not null && shell.Documents.Contains(opened)) shell.CloseDiscarding(opened);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>One real edit through each module's own edit path (the document becomes dirty).</summary>
    private static bool Edit(IDocument tab)
    {
        switch (tab)
        {
            case Cairn.Vfx.Ui.Documents.VfxDocument vfx:
                return vfx.Apply("Rename (test)", f =>
                {
                    for (int i = 0; i < f.Sections.Length; i++)
                    {
                        try { return Cairn.Vfx.Editing.VfxEdit.Rename(f, i, "cairn_edit"); }
                        catch (ArgumentException) { }
                    }
                    return f;
                });
            case Cairn.Rfa.Ui.ViewModels.ClipDocumentViewModel clip:
                return clip.Apply("Retime (test)", c => Cairn.Rfa.Editing.ClipEdit.Retime(c, 2.0));
            case Cairn.Rfa.Ui.ViewModels.MeshDocumentViewModel mesh:
                return mesh.Apply("Rename submesh (test)", m => Cairn.Rfa.Editing.MeshEdit.RenameSubmesh(m, 0, "cairn_edit"));
            case Cairn.Atx.Ui.ViewModels.DocumentViewModel atx:
                atx.ReplaceAll(atx.TextForSave + "\r\n# edited in a Cairn self-test\r\n", markSaved: false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Choosing Discard in the packfile's partial-recovery prompt deletes that snapshot quietly: no "Could not recover"
    /// error, and the question does not come back at the next start.
    /// </summary>
    [SelfTest("integration.packfile-recovery-discard")]
    public static void RecoveryDiscard(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        // an own non-interactive service: earlier shell tests may leave one installed that shows real windows
        var service = new DialogService { CollectErrors = [] };
        var previousDialogs = shell.Dialogs;
        shell.Dialogs = service;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-int-vpp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "d.vpp");
            VppSaver.Save(VppEdit.AddBytes(VppPackage.Empty, "big.tga", new byte[1000], VppClashPolicy.Replace).Package, path, null, CancellationToken.None, VppSaveOptions.Default);
            // the replacement is larger than the inline cap and has no side file: only a partial recovery is possible
            var pkg = VppEdit.Replace(VppPackage.Open(path), "big.tga", new MemorySource(new byte[2 << 20]));
            string id = Guid.NewGuid().ToString("N");
            ctx.Check(shell.Recovery.Save(id, path, "d.vpp", "vpp", VppRecoveryManifest.Capture(pkg).ToJson()), "snapshot written");
            int errorsBefore = service.CollectErrors?.Count ?? 0;
            int docsBefore = shell.Documents.Count;
            string? asked = null;
            service.NonInteractiveChoice = (text, _) => { asked = text; return 1; }; // Discard
            shell.ChooseRecovery = list => [.. list.Where(s => s.Id == id)];
            int restored = shell.OfferRecovery();
            ctx.Check(asked?.Contains("partly recovered") == true, "the partial-recovery prompt was shown");
            ctx.Check(restored == 0 && shell.Documents.Count == docsBefore, "nothing restored");
            // the prompt itself is logged as "(no window) <heading> -> <button>"; anything else would be an error dialog
            var shown = service.CollectErrors?.Skip(errorsBefore).Where(e => !e.StartsWith("(no window)", StringComparison.Ordinal)).ToList() ?? [];
            ctx.Check(shown.Count == 0, $"no error dialog ({string.Join(" | ", shown)})");
            ctx.Check(shell.Recovery.List().All(s => s.Id != id), "the discarded snapshot is deleted (not offered again)");
        }
        finally
        {
            shell.ChooseRecovery = null;
            shell.Dialogs = previousDialogs;
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
#endif
