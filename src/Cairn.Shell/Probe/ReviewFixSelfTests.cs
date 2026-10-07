using System.Text;
using System.Windows;
using System.Windows.Controls;
using Cairn.Shell.Probe;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;

namespace Cairn.Shell;

/// <summary>Shell self-tests guarding the document-path fixes (reload, save as, recovery).</summary>
public static class ReviewFixSelfTests
{
    private sealed class ScriptedDialogs : DialogService
    {
        public string? SavePath { get; set; }
        public List<string> Errors { get; } = [];
        public override string? SaveDocument(string? initialFolder, string suggestedName, string extension, string filter) => SavePath;
        public override UnsavedChoice AskUnsavedChanges(IReadOnlyList<string> documentNames) => UnsavedChoice.DontSave;
        public override bool Confirm(string heading, string body, string confirmText) => true;
        public override void ShowError(string heading, string body, string? details = null) => Errors.Add(heading + ": " + body);
    }

    /// <summary>A probe-like text document that reports damaged input and can fail its recovery capture.</summary>
    private sealed class FaultyDocument(IShellContext shell, IDocumentKind kind, string text, string name, string? path)
        : SnapshotDocument<string>(shell, kind, text, name, path)
    {
        public string Text { get => Current; set => Apply("Edit text", _ => value); }
        public override byte[]? CaptureRecovery() => Current == "!throw" ? throw new ArgumentException("capture failed") : base.CaptureRecovery();
        protected override string Parse(byte[] bytes, string name)
        {
            var text = Encoding.UTF8.GetString(bytes);
            return text.StartsWith("!damaged", StringComparison.Ordinal) ? throw new Cairn.Formats.AssetFormatException("damaged probe text") : text;
        }
        protected override byte[] Write(string snapshot) => Encoding.UTF8.GetBytes(snapshot);
        protected override FrameworkElement CreateView() => new TextBlock();
    }

    private static (ShellViewModel Shell, ScriptedDialogs Dialogs, IDocumentKind Kind, string Folder)? Setup(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        if (!ctx.Check(shell.Kinds.Any(k => k.Id == "probe"), "probe module loaded (run with --probe-module)")) return null;
        var dialogs = new ScriptedDialogs();
        shell.Dialogs = dialogs;
        shell.CloseAll();
        var folder = Path.Combine(Path.GetTempPath(), "Cairn-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return (shell, dialogs, shell.Kinds.First(k => k.Id == "probe"), folder);
    }

    [SelfTest("review.reload-dirty-is-undoable")]
    public static void ReloadDirtyIsUndoable(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, _, _, folder)) return;
        var path = Path.Combine(folder, "r.cairnprobe");
        File.WriteAllText(path, "disk 1");
        ctx.Check(shell.OpenFile(path), "open succeeds");
        if (shell.ActiveDocument is not ProbeDocument doc) { ctx.Check(false, "probe document active"); return; }
        doc.Text = "my edit";
        File.WriteAllText(path, "disk 2");
        doc.Reload();
        ctx.Check(doc.Text == "disk 2" && !doc.IsDirty, "reload shows the disk content and is clean");
        ctx.Check(doc.CanUndo && doc.UndoLabel == "Reload from disk", "reload of a dirty document is one undo step: " + doc.UndoLabel);
        doc.Undo();
        ctx.Check(doc.Text == "my edit" && doc.IsDirty, "undo brings the unsaved edit back");
        shell.CloseAll();
    }

    [SelfTest("review.reload-damaged-file")]
    public static void ReloadDamagedFile(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, dialogs, kind, folder)) return;
        var path = Path.Combine(folder, "d.cairnprobe");
        File.WriteAllText(path, "good");
        var doc = new FaultyDocument(shell, kind, "good", "d.cairnprobe", path);
        shell.AddDocument(doc);
        File.WriteAllText(path, "!damaged");
        try { doc.Reload(); }
        catch (Cairn.Formats.AssetFormatException) { ctx.Check(false, "reload of a damaged file must not throw"); }
        ctx.Check(doc.Text == "good", "document keeps its content");
        ctx.Check(doc.StatusMessage.Contains("could not be reloaded", StringComparison.OrdinalIgnoreCase), "error shown in the status: " + doc.StatusMessage);
        ctx.Check(dialogs.Errors.Count == 0, "no modal error");
        shell.CloseAll();
    }

    [SelfTest("review.open-location-access-denied")]
    public static void OpenLocationAccessDenied(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, dialogs, _, folder)) return;
        // an "archive" that is a folder: opening it is refused by the OS with UnauthorizedAccessException
        var location = new Cairn.Assets.AssetLocation("x.cairnprobe", "x.cairnprobe", Cairn.Assets.AssetSourceKind.GameArchive,
            null, folder, new Cairn.Formats.Vpp.VppEntry("x.cairnprobe", 0, 4));
        Exception? raw = null;
        try { location.ReadAllBytes(); } catch (Exception ex) { raw = ex; }
        ctx.Check(raw is UnauthorizedAccessException, "the location throws UnauthorizedAccessException: " + raw?.GetType().Name);
        bool opened = true;
        try { opened = shell.OpenLocation(location); }
        catch (Exception ex) { ctx.Check(false, "OpenLocation threw " + ex.GetType().Name); }
        ctx.Check(!opened && dialogs.Errors.Count == 1 && shell.Documents.Count == 0, "OpenLocation reports the error and opens nothing");
        Directory.Delete(folder, true);
    }

    [SelfTest("review.saveas-onto-open-tab")]
    public static void SaveAsOntoOpenTab(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, dialogs, _, folder)) return;
        var a = Path.Combine(folder, "a.cairnprobe");
        var b = Path.Combine(folder, "b.cairnprobe");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");
        shell.OpenFile(a);
        var docA = shell.ActiveDocument;
        shell.OpenFile(b);
        var docB = shell.ActiveDocument as ProbeDocument;
        if (!ctx.Check(docA is ProbeDocument && docB is not null && docA != docB, "two tabs open")) return;
        dialogs.SavePath = a.ToUpperInvariant();
        ctx.Check(!shell.SaveAs(docB!), "save as onto another tab's file is refused");
        ctx.Check(File.ReadAllText(a) == "A" && docB!.FilePath == b, "the other tab's file is untouched");
        ctx.Check(dialogs.Errors.Count == 1 && dialogs.Errors[0].Contains("a.cairnprobe", StringComparison.OrdinalIgnoreCase), "message names the tab: " + string.Join("; ", dialogs.Errors));
        dialogs.Errors.Clear();
        docB!.Text = "B2";
        dialogs.SavePath = b;
        ctx.Check(shell.SaveAs(docB) && File.ReadAllText(b) == "B2" && dialogs.Errors.Count == 0, "save as onto its own path is a normal save");
        shell.CloseAll();
    }

    [SelfTest("review.recovery-survives-a-failing-document")]
    public static void RecoverySurvivesFailingDocument(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, _, kind, _)) return;
        var bad = new FaultyDocument(shell, kind, "x", "bad.cairnprobe", null);
        shell.AddDocument(bad);
        bad.Text = "!throw";
        var good = new FaultyDocument(shell, kind, "y", "good.cairnprobe", null);
        shell.AddDocument(good);
        good.Text = "fine";
        IReadOnlyList<string> saved = [];
        try { saved = shell.SnapshotRecovery(); }
        catch (ArgumentException) { ctx.Check(false, "one failing document must not abort the snapshot"); }
        finally { bad.Text = "x2"; }
        ctx.Check(saved.Contains("good.cairnprobe"), "the later document is still snapshotted: " + string.Join(", ", saved));
        shell.Recovery.Discard(bad.Id);
        shell.Recovery.Discard(good.Id);
        shell.CloseAll();
    }
}
