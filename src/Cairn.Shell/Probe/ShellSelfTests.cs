using System.Windows;
using System.Windows.Input;
using Cairn.Shell.Probe;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;

namespace Cairn.Shell;

/// <summary>Shell self-tests: the module contract end to end through the probe module.</summary>
public static class ShellSelfTests
{
    private sealed class ScriptedDialogs : DialogService
    {
        public string? SavePath { get; set; }
        public UnsavedChoice Unsaved { get; set; } = UnsavedChoice.DontSave;
        public bool ConfirmAnswer { get; set; } = true;
        public List<string> Errors { get; } = [];
        public override string? SaveDocument(string? initialFolder, string suggestedName, string extension, string filter) => SavePath;
        public override UnsavedChoice AskUnsavedChanges(IReadOnlyList<string> documentNames) => Unsaved;
        public override bool Confirm(string heading, string body, string confirmText) => ConfirmAnswer;
        public override void ShowError(string heading, string body, string? details = null) => Errors.Add(heading + ": " + body);
    }

    private static (ShellViewModel Shell, ScriptedDialogs Dialogs, string Folder)? Setup(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        if (!ctx.Check(shell.Kinds.Any(k => k.Id == "probe"), "probe module loaded (run with --probe-module)")) return null;
        var dialogs = new ScriptedDialogs();
        shell.Dialogs = dialogs;
        shell.CloseAll();
        var folder = Path.Combine(Path.GetTempPath(), "Cairn-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return (shell, dialogs, folder);
    }

    [SelfTest("shell.open-edit-save")]
    public static void OpenEditSave(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, dialogs, folder)) return;
        var path = Path.Combine(folder, "a.cairnprobe");
        File.WriteAllText(path, "hello");
        ctx.Check(shell.OpenFile(path), "open succeeds");
        var doc = shell.ActiveDocument as ProbeDocument;
        if (!ctx.Check(doc is { Text: "hello" }, "active document is the probe text")) return;
        ctx.Check(shell.OpenFile(path) && shell.Documents.Count == 1, "second open of the same path activates the tab");
        doc!.Text = "hello world";
        ctx.Check(doc.IsDirty && doc.TabHeader.EndsWith(" *", StringComparison.Ordinal), "edit marks dirty");
        ctx.Check(shell.UndoCommand.CanExecute(null), "undo enabled");
        shell.UndoCommand.Execute(null);
        ctx.Check(doc.Text == "hello" && !doc.IsDirty, "undo restores the saved text");
        shell.RedoCommand.Execute(null);
        ctx.Check(doc.Text == "hello world", "redo reapplies");
        ctx.Check(shell.Save(doc) && File.ReadAllText(path) == "hello world" && !doc.IsDirty, "save writes and clears dirty");
        dialogs.SavePath = Path.Combine(folder, "b.cairnprobe");
        ctx.Check(shell.SaveAs(doc) && File.Exists(dialogs.SavePath) && doc.FilePath == dialogs.SavePath, "save as writes the new path");
        ctx.Check(shell.RecentFiles.Contains(dialogs.SavePath), "recent files updated");
        ctx.Check(dialogs.Errors.Count == 0, "no errors reported: " + string.Join("; ", dialogs.Errors));
        shell.CloseAll();
    }

    [SelfTest("shell.close-prompt-reopen")]
    public static void ClosePromptReopen(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, dialogs, _)) return;
        var kind = shell.Kinds.First(k => k.Id == "probe");
        var doc = (ProbeDocument)kind.CreateNew()!;
        shell.AddDocument(doc);
        doc.Text = "unsaved";
        dialogs.Unsaved = UnsavedChoice.Cancel;
        ctx.Check(!shell.Close(doc) && shell.Documents.Contains(doc), "cancel keeps the dirty tab");
        dialogs.Unsaved = UnsavedChoice.DontSave;
        ctx.Check(shell.Close(doc) && shell.Documents.Count == 0, "don't save closes it");
        ctx.Check(shell.ReopenClosedCommand.CanExecute(null), "reopen enabled");
        shell.ReopenClosedCommand.Execute(null);
        ctx.Check(shell.ActiveDocument is ProbeDocument { Text: "unsaved", IsDirty: true }, "reopened tab keeps the unsaved text");
        shell.CloseAll();
    }

    [SelfTest("shell.recovery")]
    public static void Recovery(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, _, _)) return;
        var kind = shell.Kinds.First(k => k.Id == "probe");
        var keep = (ProbeDocument)kind.CreateNew()!;
        var drop = (ProbeDocument)kind.CreateNew()!;
        shell.AddDocument(keep);
        shell.AddDocument(drop);
        keep.Text = "crash me";
        drop.Text = "throw me away";
        ctx.Check(shell.SnapshotRecovery().Count == 2 && shell.Recovery.List().Count == 2, "dirty documents snapshotted");
        var keepId = keep.Id;
        var dropId = drop.Id;
        // Simulate the next session: forget the tabs without discarding the snapshots, then offer them.
        void Forget()
        {
            shell.OpenDocuments.Clear();
            shell.ActiveDocument = null;
        }
        Forget();
        IReadOnlyList<Cairn.Workspace.RecoverySnapshot>? offered = null;
        try
        {
            shell.ChooseRecovery = list => { offered = list; return null; };
            ctx.Check(shell.OfferRecovery() == 0 && shell.Recovery.List().Count == 2, "not now keeps every snapshot");
            ctx.Check(offered is { Count: 2 }, "both snapshots offered");
            shell.ChooseRecovery = list => [.. list.Where(s => s.Id == keepId)];
            ctx.Check(shell.OfferRecovery() == 1, "one snapshot restored");
            ctx.Check(shell.ActiveDocument is ProbeDocument { Text: "crash me", IsDirty: true }, "restored text is dirty");
            ctx.Check(shell.Recovery.List().All(s => s.Id != keepId), "restored snapshot removed from the store");
            ctx.Check(shell.Recovery.List().All(s => s.Id != dropId), "discarded snapshot removed from the store");
        }
        finally
        {
            shell.ChooseRecovery = null;
        }
        shell.CloseAll();
    }
    [SelfTest("shell.menus-shortcuts")]
    public static void MenusAndShortcuts(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, _, _)) return;
        var window = (MainWindow)ctx.MainWindow;
        var menu = window.MainMenu.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(m => m.Name == "ProbeMenu");
        const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
        ctx.Check(menu is { Visibility: Visibility.Collapsed }, "probe menu hidden without a document");
        ctx.Check(!window.Shortcuts.TryExecute(Key.F, ModifierKeys.None, window), "scoped probe shortcut does not fire without a document");
        var doc = (ProbeDocument)shell.Kinds.First(k => k.Id == "probe").CreateNew()!;
        shell.AddDocument(doc);
        ctx.Check(menu is { Visibility: Visibility.Visible }, "probe menu shown for a probe document");
        ctx.Check(window.BottomPane.Visibility == Visibility.Visible, "bottom panel shown");
        ctx.Check(window.Shortcuts.TryExecute(Key.P, CtrlShift, window) && doc.Text == "!", "shortcut runs the module command");
        ctx.Check(shell.StatusItems.Count == 1, "status items come from the document");
        doc.Text = string.Empty;
        shell.CloseAll();
    }

    [SelfTest("shell.open-archive-entry")]
    public static void OpenArchiveEntry(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, dialogs, folder)) return;
        // A one-entry RF1 VPP (version 1): header block, directory block, data block.
        var data = System.Text.Encoding.UTF8.GetBytes("from the archive");
        const int Block = Cairn.Formats.Vpp.VppArchive.BlockSize;
        var vpp = new byte[Block * 3];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vpp, Cairn.Formats.Vpp.VppArchive.Signature);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vpp.AsSpan(4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vpp.AsSpan(8), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(vpp.AsSpan(12), (uint)vpp.Length);
        System.Text.Encoding.ASCII.GetBytes("entry.cairnprobe").CopyTo(vpp, Block);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(vpp.AsSpan(Block + Cairn.Formats.Vpp.VppArchive.NameBytes), data.Length);
        data.CopyTo(vpp, Block * 2);
        var vppPath = Path.Combine(folder, "probe.vpp");
        File.WriteAllBytes(vppPath, vpp);
        var entry = Cairn.Formats.Vpp.VppArchive.Open(vppPath).Entries.FirstOrDefault();
        if (!ctx.Check(entry is { Name: "entry.cairnprobe" }, "temp archive written and indexed")) return;

        var location = new Cairn.Assets.AssetLocation("entry.cairnprobe", "entry.cairnprobe", Cairn.Assets.AssetSourceKind.SearchFolderArchive, null, vppPath, entry);
        ctx.Check(shell.OpenLocation(location), "OpenLocation opens an archive entry");
        var doc = shell.ActiveDocument as ProbeDocument;
        ctx.Check(doc is { Text: "from the archive", FilePath: null, IsDirty: false, IsFromArchive: true }, "opened through OpenBytes with an archive origin, not as a new document");
        ctx.Check(doc?.TabToolTip?.Contains("probe.vpp", StringComparison.Ordinal) == true, "tab tooltip names the archive");
        var target = Path.Combine(folder, "saved.cairnprobe");
        dialogs.SavePath = target;
        shell.SaveCommand.Execute(null);
        ctx.Check(File.Exists(target) && doc?.FilePath == target && doc.IsFromArchive == false, "Save on an archive document becomes Save As");
        shell.CloseAll();
    }

    [SelfTest("shell.shortcut-routing")]
    public static void ShortcutRouting(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, _, _)) return;
        var window = (MainWindow)ctx.MainWindow;
        var router = window.Shortcuts;
        const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
        var two = shell.Modules.OfType<ProbeTwoModule>().FirstOrDefault();
        if (!ctx.Check(two != null, "second probe module loaded")) return;

        // A real PreviewKeyDown through the window (modifier-less keys only: Keyboard.Modifiers is the live state).
        bool Press(UIElement target, Key key)
        {
            if (PresentationSource.FromVisual(window) is not { } source) return router.TryExecute(key, ModifierKeys.None, target);
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(e);
            return e.Handled;
        }

        var doc = (ProbeDocument)shell.Kinds.First(k => k.Id == "probe").CreateNew()!;
        shell.AddDocument(doc);
        window.UpdateLayout();
        ctx.Check(Press(window, Key.Space) && doc.Text == "_", "Space (no modifiers) fires with the document focused, and is marked handled");
        ctx.Check(Press(window, Key.F) && doc.Text == "_F", "a letter (no modifiers) fires with the document focused");
        ctx.Check(!Press(window, Key.G) && doc.Text == "_F", "an unbound key is let through");

        var box = (System.Windows.Controls.TextBox)doc.View;
        var boxConnected = PresentationSource.FromVisual(box) != null;
        ctx.Check(!(boxConnected ? Press(box, Key.Space) : router.TryExecute(Key.Space, ModifierKeys.None, box)) && doc.Text == "_F",
            "Space does not fire while a text box has focus");
        ctx.Check(!router.TryExecute(Key.F, ModifierKeys.None, box) && doc.Text == "_F", "a letter does not fire while a text box has focus");
        ctx.Check(ShortcutRouter.IsInTextInput(new System.Windows.Controls.PasswordBox()) && !ShortcutRouter.IsInTextInput(new System.Windows.Controls.Button()),
            "text input detection: password box yes, button no");
        ctx.Check(router.TryExecute(Key.P, CtrlShift, box) && doc.Text == "_F!", "a Ctrl+Shift gesture still fires in a text box");
        ctx.Check(!ShortcutRouter.YieldsToTextInput(new("t", "t", Key.Space, ModifierKeys.None, shell.SaveCommand, AllowInTextInput: true))
            && ShortcutRouter.YieldsToTextInput(new("t", "t", Key.C, ModifierKeys.Control, shell.SaveCommand)), "AllowInTextInput overrides; Ctrl+C yields");

        // The owner module of the active document wins a shared gesture; scoped shortcuts skip other kinds.
        var other = new ProbeDocument(shell, two!.Kind, string.Empty, "Other.cairnprobe2", null);
        other.MarkAsNew();
        shell.AddDocument(other);
        ctx.Check(shell.ActiveDocument == other, "second-kind document active");
        ctx.Check(!Press(window, Key.Space) && other.Text == string.Empty && doc.Text == "_F!", "probe's scoped Space does not fire for another kind's document");
        ctx.Check(router.TryExecute(Key.P, CtrlShift, window) && other.Text == "?", "shared gesture runs the active document's module (probe2)");
        ctx.Check(router.TryExecute(Key.L, CtrlShift, window) && other.Text == "?<", "module gesture wins over the shell's for its own document");
        shell.ActiveDocument = doc;
        ctx.Check(router.TryExecute(Key.P, CtrlShift, window) && doc.Text == "_F!!" && other.Text == "?<", "shared gesture runs the probe module for a probe document");
        ctx.Check(router.Candidates().FirstOrDefault(s => s.Key == Key.L && s.Modifiers == CtrlShift)?.Command == shell.ToggleLeftPaneCommand,
            "the shell's gesture applies again for a probe document");
        shell.CloseAll();
    }

    [SelfTest("shell.reopen-open-file-every-route")]
    public static void ReopenEveryRoute(SelfTestContext ctx)
    {
        if (Setup(ctx) is not var (shell, dialogs, folder)) return;
        var first = Path.Combine(folder, "first.cairnprobe");
        var second = Path.Combine(folder, "second.cairnprobe");
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");
        if (!ctx.Check(shell.OpenFile(first) && shell.ActiveDocument is { } a && shell.OpenFile(second) && shell.ActiveDocument is { } b && a != b,
            "two probe files open")) return;
        var target = shell.Documents.First(d => string.Equals(d.FilePath, first, StringComparison.OrdinalIgnoreCase));
        var other = shell.Documents.First(d => d != target);
        var routes = new (string Name, Action Run)[]
        {
            ("OpenFile", () => shell.OpenFile(first)),
            ("OpenFile (other spelling)", () => shell.OpenFile(Path.Combine(folder, ".", "FIRST.cairnprobe"))),
            ("forwarded / drag-drop (OpenFiles)", () => shell.OpenFiles([first])),
            ("recent files", () => shell.OpenRecentCommand.Execute(first)),
            ("loose AssetLocation", () => shell.OpenLocation(new Cairn.Assets.AssetLocation("first.cairnprobe", "first.cairnprobe", default, first, null, null))),
        };
        foreach (var (name, run) in routes)
        {
            shell.Activate(other);
            shell.ShowStatus(string.Empty);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            run();
            ctx.Check(clock.ElapsedMilliseconds < 2000 && shell.Documents.Count == 2 && shell.ActiveDocument == target
                && shell.StatusText.Contains("already open", StringComparison.Ordinal) && dialogs.Errors.Count == 0,
                $"{name}: returns at once ({clock.ElapsedMilliseconds} ms), no second tab ({shell.Documents.Count}), existing tab active, status \"{shell.StatusText}\"");
        }
        shell.CloseAll();
    }

    [ScreenshotStep(100)]
    public static void OpenProbe(ScreenshotContext ctx)
    {
        if (!ctx.Options.ContainsKey("probe-document")) return;
        var shell = (ShellViewModel)ctx.Shell;
        if (shell.Kinds.FirstOrDefault(k => k.Id == "probe")?.CreateNew() is ProbeDocument doc) { shell.AddDocument(doc); doc.Text = "Probe document\nSecond line"; }
    }
}
