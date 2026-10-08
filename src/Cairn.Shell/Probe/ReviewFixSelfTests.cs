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
        public string? LastInitialFolder { get; private set; }
        public List<string> Errors { get; } = [];
        public override string? SaveDocument(string? initialFolder, string suggestedName, string extension, string filter)
        {
            LastInitialFolder = initialFolder;
            return SavePath;
        }
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

    [SelfTest("review.saveas-never-game-directory")]
    public static void SaveAsNeverGameDirectory(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, dialogs, _, folder)) return;
        string? oldGame = shell.Settings.GameDirectory, oldLast = shell.Settings.LastSaveFolder;
        string game = Path.Combine(folder, "game"), maps = Path.Combine(game, "user_maps"), outside = Path.Combine(folder, "out");
        Directory.CreateDirectory(maps);
        Directory.CreateDirectory(outside);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        try
        {
            shell.Settings.GameDirectory = game + Path.DirectorySeparatorChar;
            shell.Settings.LastSaveFolder = outside;
            var inGame = Path.Combine(maps, "g.cairnprobe");
            File.WriteAllText(inGame, "G");
            ctx.Check(shell.OpenFile(inGame), "open a file inside the game directory");
            var doc = shell.ActiveDocument;
            dialogs.SavePath = null;
            ctx.Check(doc is not null && !shell.SaveAs(doc), "save as cancelled");
            ctx.Check(string.Equals(dialogs.LastInitialFolder, outside, StringComparison.OrdinalIgnoreCase),
                "a file from inside the game directory: save as starts in the last save folder: " + dialogs.LastInitialFolder);

            shell.Settings.LastSaveFolder = game;
            shell.SaveAs(doc!);
            ctx.Check(string.Equals(dialogs.LastInitialFolder, documents, StringComparison.OrdinalIgnoreCase),
                "last save folder is the game directory too: Documents: " + dialogs.LastInitialFolder);

            shell.Settings.LastSaveFolder = outside;
            dialogs.SavePath = Path.Combine(game, "chosen.cairnprobe");
            ctx.Check(shell.SaveAs(doc!) && File.Exists(dialogs.SavePath), "the user may still choose the game directory");
            ctx.Check(string.Equals(shell.Settings.LastSaveFolder, outside, StringComparison.OrdinalIgnoreCase),
                "saving into the game directory does not become the last save folder: " + shell.Settings.LastSaveFolder);
            shell.CloseAll();

            var own = Path.Combine(outside, "o.cairnprobe");
            File.WriteAllText(own, "O");
            shell.Settings.LastSaveFolder = null;
            ctx.Check(shell.OpenFile(own), "open a file outside the game directory");
            dialogs.SavePath = null;
            shell.SaveAs(shell.ActiveDocument!);
            ctx.Check(string.Equals(dialogs.LastInitialFolder, outside, StringComparison.OrdinalIgnoreCase),
                "a file outside the game directory: save as starts in its own folder: " + dialogs.LastInitialFolder);

            // The production dialog service applies the same rule to every module's save and export dialog.
            var settings = new Cairn.Workspace.AppSettings { GameDirectory = game, LastSaveFolder = null };
            ctx.Check(string.Equals(DialogService.SafeSaveFolder(game, settings), documents, StringComparison.OrdinalIgnoreCase), "game directory itself -> Documents");
            ctx.Check(string.Equals(DialogService.SafeSaveFolder(maps, settings), documents, StringComparison.OrdinalIgnoreCase), "folder inside it -> Documents");
            ctx.Check(DialogService.SafeSaveFolder(null, settings) == documents, "no folder -> Documents (never the dialog's own last folder)");
            ctx.Check(DialogService.SafeSaveFolder(outside, settings) == outside, "an ordinary folder is kept");
            Directory.CreateDirectory(game + "-mods");
            ctx.Check(DialogService.SafeSaveFolder(game + "-mods", settings) == game + "-mods", "a sibling whose name starts like the game directory is kept");
            ctx.Check(DialogService.SafeSaveFolder(Path.Combine(folder, "missing"), settings) == documents, "a folder that does not exist -> Documents");
            ctx.Check(DialogService.SafeSaveFolder(maps, null) == maps, "without settings the folder is unchanged");
        }
        finally
        {
            shell.CloseAll();
            shell.Settings.GameDirectory = oldGame;
            shell.Settings.LastSaveFolder = oldLast;
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [SelfTest("shell.help-topics")]
    public static void HelpTopicsBuild(SelfTestContext ctx)
    {
        var topics = ctx.Shell.Modules.SelectMany(m => m.HelpTopics).ToList();
        ctx.Check(topics.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == topics.Count, $"{topics.Count} help topics, ids unique");
        foreach (var topic in topics)
        {
            try { ctx.Check(topic.Build().Blocks.Count > 0, $"{topic.Id} builds a page"); }
            catch (Exception ex) { ctx.Check(false, $"{topic.Id} threw {ex.GetType().Name}: {ex.Message}"); }
        }
        // Every document type has a page that explains it: sounds and the exporter / PS2 meshes included.
        var extensions = ctx.Shell.Modules.SelectMany(m => m.DocumentKinds).SelectMany(k => k.Extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (extensions.Contains(".vse")) ctx.Check(topics.Any(t => t.Title.Contains(".vse", StringComparison.Ordinal)), "a Sounds help topic");
        if (extensions.Contains(".v3d")) ctx.Check(topics.Any(t => t.Title.Contains("PS2 meshes", StringComparison.Ordinal) && t.Build().Blocks.Count > 0), "an exporter and PS2 meshes help topic");
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
