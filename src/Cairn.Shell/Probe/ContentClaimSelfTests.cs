using System.Text;
using System.Windows;
using Cairn.Assets;
using Cairn.Previews;
using Cairn.Shell.Probe;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;

namespace Cairn.Shell;

/// <summary>
/// Content claims (<see cref="ContentClaims"/>): two probe modules take the extension <c>.cairnclaim</c>, the probe
/// module the files that start "A:" and the second probe module those that start "B:". Every path that picks a kind,
/// a preview, a conversion or a batch by name must pick by content: File › Open (the path the command line, drag and
/// drop, Recent and work copies take), an archive entry (<see cref="IShellContext.OpenLocation"/>), the preview pane
/// and its Open in Cairn, File › Convert to…, and the packfile's Convert to submenu and batch commands.
/// </summary>
internal static class ContentClaimSelfTests
{
    private static readonly byte[] TextA = Encoding.UTF8.GetBytes("A: the first module's claim probe #A");
    private static readonly byte[] TextB = Encoding.UTF8.GetBytes("B: the second module's claim probe #B");
    private static readonly byte[] TextC = Encoding.UTF8.GetBytes("C: nobody's claim probe");

    private sealed class RecordingDialogs : DialogService
    {
        public List<string> Errors { get; } = [];
        public override void ShowError(string heading, string body, string? details = null) => Errors.Add(heading + ": " + body);
    }

    [SelfTest("shell.content-claims")]
    public static async Task ContentClaimsChoose(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var one = shell.Modules.OfType<ProbeModule>().FirstOrDefault();
        var two = shell.Modules.OfType<ProbeTwoModule>().FirstOrDefault();
        if (one is null || two is null)
        {
            ctx.Skip("needs --probe-module");
            return;
        }
        var original = shell.Dialogs;
        var dialogs = new RecordingDialogs();
        shell.Dialogs = dialogs;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-claims-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<IDocument>();
        try
        {
            Rules(ctx, shell);
            await OpenFileAsync(ctx, shell, folder, opened);
            await ConvertToAsync(ctx, shell, folder, opened);
            await PreviewAsync(ctx, shell);
#if CAIRN_MODULE_VPP
            if (shell.Modules.Any(m => m.Id == "vpp")) await PackfileAsync(ctx, shell, folder, opened);
            else ctx.Log("  packfile part skipped (no packfile module)");
#endif
            ctx.Check(dialogs.Errors.Count == 0, "no error was reported" + (dialogs.Errors.Count > 0 ? ": " + string.Join(" | ", dialogs.Errors) : ""));
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            shell.Dialogs = original;
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The choice itself: one candidate is never asked, ties and "not mine", a claim that throws.</summary>
    private static void Rules(SelfTestContext ctx, ShellViewModel shell)
    {
        var claimKinds = ContentClaims.KindsFor(shell.Kinds, "x" + ProbeClaims.Extension);
        ctx.Check(claimKinds.Select(k => k.Id).SequenceEqual(["probe.claim", "probe2.claim"]), $"two kinds take .cairnclaim, in module order ({string.Join(", ", claimKinds.Select(k => k.Id))})");
        bool asked = false;
        var probe = ContentClaims.KindFor(shell.Kinds, "notes.cairnprobe", () => { asked = true; return []; });
        ctx.Check(probe?.Id == "probe" && !asked, "a single candidate is used without reading the file");
        ctx.Check(ContentClaims.KindFor(shell.Kinds, "a.cairnclaim", TextA)?.Id == "probe.claim" && ContentClaims.KindFor(shell.Kinds, "b.CAIRNCLAIM", TextB)?.Id == "probe2.claim",
            "the content picks the kind (the extension ignoring case)");
        ctx.Check(ContentClaims.KindFor(shell.Kinds, "c.cairnclaim", TextC)?.Id == "probe.claim", "nobody's content: the first by name (it reports what it cannot read)");
        ctx.Check(ContentClaims.KindFor(shell.Kinds, "c.cairnclaim", () => null)?.Id == "probe.claim", "an unreadable file: the first by name");
        ctx.Check(ContentClaims.KindFor(shell.Kinds, "x.nothing", TextA) is null, "no kind for an unknown extension");
        int[] claims = [ContentClaims.ByName, ContentClaims.Recognised, ContentClaims.Recognised];
        ctx.Check(ContentClaims.Pick(new[] { 0, 1, 2 }, i => claims[i]) == 1, "the highest claim wins, the first on a tie");
        ctx.Check(ContentClaims.Pick(new[] { 0, 1 }, i => i == 0 ? throw new InvalidOperationException("broken") : ContentClaims.ByName) == 1, "a claim that throws counts as not mine");
        ctx.Check(ContentClaims.Pick(new[] { 0, 1 }, _ => ContentClaims.NotMine) == 0, "every claim not mine: the first");
        ctx.Check(ContentClaims.HeadLengthFor(shell.Modules, "a.cairnclaim") == ContentClaims.MaxBytes
            && ContentClaims.HeadLengthFor(shell.Modules, "notes.cairnprobe") == ContentClaims.DefaultHeadLength, "the converters ask for the whole file only for their shared type");
    }

    /// <summary>File › Open (also the command line, drag and drop, Recent and work copies): the content picks the kind.</summary>
    private static async Task OpenFileAsync(SelfTestContext ctx, ShellViewModel shell, string folder, List<IDocument> opened)
    {
        foreach (var (name, bytes, kind) in new[] { ("a.cairnclaim", TextA, "probe.claim"), ("b.cairnclaim", TextB, "probe2.claim"), ("c.cairnclaim", TextC, "probe.claim") })
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            bool ok = shell.OpenFile(path);
            await ctx.SettleAsync(TimeSpan.FromSeconds(5));
            if (shell.ActiveDocument is { } doc && !opened.Contains(doc)) opened.Add(doc);
            ctx.Check(ok && shell.ActiveDocument?.Kind.Id == kind, $"File › Open {name}: {shell.ActiveDocument?.Kind.Id} (expected {kind})");
        }
        // the old single-kind path still opens a probe document
        string probe = Path.Combine(folder, "plain.cairnprobe");
        File.WriteAllText(probe, "plain");
        ctx.Check(shell.OpenFile(probe) && shell.ActiveDocument?.Kind.Id == "probe", "a type one kind takes opens as before");
        if (shell.ActiveDocument is { } p && !opened.Contains(p)) opened.Add(p);
    }

    /// <summary>File › Convert to…: each module lists its target only for its own content (the tab gives its whole text).</summary>
    private static async Task ConvertToAsync(SelfTestContext ctx, ShellViewModel shell, string folder, List<IDocument> opened)
    {
        foreach (var (name, expected) in new[] { ("a.cairnclaim", "probe:.claima"), ("b.cairnclaim", "probe2:.claimb") })
        {
            var tab = opened.FirstOrDefault(d => d.FilePath is { } p && Path.GetFileName(p) == name);
            if (!ctx.Check(tab is not null, $"{name} is open")) continue;
            shell.Activate(tab!);
            await ctx.SettleAsync(TimeSpan.FromSeconds(5));
            var got = shell.ConversionsForActive.Select(c => $"{c.Module.Id}:{c.Target.Extension}").ToList();
            ctx.Check(got.SequenceEqual([expected]), $"Convert to for {name}: {string.Join(", ", got)} (expected {expected})");
        }
        ctx.Check(AssetConversions.TargetsFor(shell.Modules, "a.cairnclaim", TextA.AsSpan(0, ContentClaims.DefaultHeadLength)).Count == 0,
            "with only the first 16 bytes no converter claims the file (the whole file must reach them)");
        // a conversion runs on the content it was listed for
        var a = opened.First(d => d.FilePath is { } p && Path.GetFileName(p) == "a.cairnclaim");
        shell.Activate(a);
        var choice = shell.ConversionsForActive.FirstOrDefault();
        if (ctx.Check(choice is not null, "a.cairnclaim has a target"))
        {
            string? result = await shell.ConvertActiveAsync(choice!, interactive: false, outputFolder: folder);
            ctx.Check(result == Path.Combine(folder, "a.claima") && File.Exists(result), $"the conversion ran ({result})");
        }
    }

    /// <summary>The preview pane: the content picks the provider.</summary>
    private static async Task PreviewAsync(SelfTestContext ctx, ShellViewModel shell)
    {
        using var pane = new AssetPreviewPane(shell);
        foreach (var (name, bytes, module) in new[] { ("a.cairnclaim", TextA, "probe"), ("b.cairnclaim", TextB, "probe2"), ("c.cairnclaim", TextC, "probe") })
        {
            ProbeClaims.LastPreviewBy = null;
            pane.Show(AssetPreviewSource.FromBytes(name, bytes));
            await pane.Pending;
            ctx.Check(pane.Kind == AssetPreviewKind.Module && ProbeClaims.LastPreviewBy == module, $"preview of {name}: {pane.Kind} by {ProbeClaims.LastPreviewBy} (expected {module})");
        }
        await Task.CompletedTask;
    }

#if CAIRN_MODULE_VPP
    /// <summary>A packfile holding A's and B's files: OpenLocation, Open in Cairn, the Convert to submenu and the batch commands.</summary>
    private static async Task PackfileAsync(SelfTestContext ctx, ShellViewModel shell, string folder, List<IDocument> opened)
    {
        var vpp = shell.Modules.OfType<Cairn.Vpp.Ui.VppModule>().First();
        var package = Cairn.Vpp.Model.VppPackage.Empty;
        foreach (var (name, bytes) in new[] { ("a.cairnclaim", TextA), ("b.cairnclaim", TextB), ("notes.cairnprobe", "probe"u8.ToArray()) })
            package = Cairn.Vpp.Editing.VppEdit.AddBytes(package, name, bytes, Cairn.Vpp.Editing.VppClashPolicy.Replace).Package;
        string packPath = Path.Combine(folder, "claims.vpp");
        Cairn.Vpp.Writing.VppSaver.Save(package, packPath, null, CancellationToken.None, Cairn.Vpp.Writing.VppSaveOptions.Default);

        // an archive entry by location (what the libraries and table references open)
        var archive = Cairn.Formats.Vpp.VppArchive.Open(packPath);
        foreach (var (name, kind) in new[] { ("a.cairnclaim", "probe.claim"), ("b.cairnclaim", "probe2.claim") })
        {
            var entry = archive.Entries.First(e => e.Name == name);
            var location = new AssetLocation(name, name, AssetSourceKind.SearchFolderArchive, null, packPath, entry);
            bool ok = shell.OpenLocation(location);
            await ctx.SettleAsync(TimeSpan.FromSeconds(5));
            if (shell.ActiveDocument is { } d && !opened.Contains(d)) opened.Add(d);
            ctx.Check(ok && shell.ActiveDocument?.Kind.Id == kind, $"OpenLocation {name} in the packfile: {shell.ActiveDocument?.Kind.Id} (expected {kind})");
        }

        if (!ctx.Check(shell.OpenFile(packPath) && shell.ActiveDocument is Cairn.Vpp.Ui.Documents.VppDocument, "the packfile opens")) return;
        var doc = (Cairn.Vpp.Ui.Documents.VppDocument)shell.ActiveDocument!;
        opened.Add(doc);
        await ctx.SettleAsync(TimeSpan.FromSeconds(5));

        // the entry's Convert to submenu reads as much as the converters ask for
        if (doc.View is Cairn.Vpp.Ui.Documents.VppDocumentView view && view.FileList.ContextMenuConvertToItem is { } submenu)
        {
            foreach (var (name, expected) in new[] { ("a.cairnclaim", "Claim A text (.claima)…"), ("b.cairnclaim", "Claim B text (.claimb)…") })
            {
                doc.SelectNames([name]);
                await ctx.SettleAsync(TimeSpan.FromSeconds(5));
                vpp.FillConvertToMenu(doc, submenu);
                var texts = submenu.Items.OfType<System.Windows.Controls.MenuItem>().Where(i => i.IsHitTestVisible).Select(i => i.Header as string).ToList();
                ctx.Check(submenu.Visibility == Visibility.Visible && texts.SequenceEqual([expected]), $"entry {name}: Convert to lists {string.Join(", ", texts)} (expected {expected})");
            }
        }
        else ctx.Check(false, "the entry list has a Convert to submenu");

        // the batch commands: each converter gets only its own entries
        doc.SelectNames(["a.cairnclaim", "b.cairnclaim"]);
        await ctx.SettleAsync(TimeSpan.FromSeconds(5));
        foreach (var (converter, id, expected) in new (IArchiveBatchConverter, string, string)[] { (shell.Modules.OfType<ProbeModule>().First(), "probe", "a.cairnclaim"), (shell.Modules.OfType<ProbeTwoModule>().First(), "probe2", "b.cairnclaim") })
        {
            ProbeClaims.LastBatch = null;
            string? summary = await vpp.ConvertWithAsync(doc, converter, interactive: false);
            ctx.Check(summary is not null && ProbeClaims.LastBatch is { } batch && batch.ModuleId == id && batch.Entries.SequenceEqual([expected]),
                $"batch {id}: {(ProbeClaims.LastBatch is { } b ? string.Join(", ", b.Entries) : "not run")} (expected {expected})");
        }

        // Open in Cairn: the work copy opens through File › Open, so the content picks the kind
        var item = doc.Current.Find("b.cairnclaim")!;
        await doc.Commands.OpenInCairnAsync(item);
        await ctx.SettleAsync(TimeSpan.FromSeconds(5));
        if (shell.ActiveDocument is { } work && !opened.Contains(work)) opened.Insert(0, work);
        ctx.Check(shell.ActiveDocument?.Kind.Id == "probe2.claim", $"Open in Cairn of b.cairnclaim: {shell.ActiveDocument?.Kind.Id} (expected probe2.claim)");
    }
#endif
}
