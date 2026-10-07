using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of the shell (phase 4): edit, undo, coalescing, refused edits, close and reopen, and
/// (for a file under %TEMP%) save and external changes. Runs last, because it closes and reopens the tab.
/// </summary>
internal static class ShellSelfTests
{
    [SelfTest("shell", Order = 1000)]
    public static async Task Shell(SelfTestContext ctx)
    {
        var model = ctx.Model;
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("selftest shell: needs a clip document");
            return;
        }
        var original = doc.Current;
        ctx.Check(!doc.IsDirty && !doc.CanUndo, "opens clean with nothing to undo");
        doc.Apply("Set ramp in", c => ClipEdit.SetHeader(c, new ClipHeaderChange { RampIn = 320 }));
        ctx.Check(doc.Current.RampIn == 320 && doc.IsDirty, "Apply changes the snapshot and dirties the tab");
        ctx.Check(model.UndoHeader == "_Undo Set ramp in", $"Edit menu names the step ('{model.UndoHeader}')");
        ctx.Check(doc.TabHeader.EndsWith('*'), $"tab shows the dirty marker ('{doc.TabHeader}')");
        doc.Inspector.RampOut.Value = 4;
        ctx.Check(doc.Current.RampOut == TimeFormat.FromUnit(4, model.TimeUnit), $"inspector row edits through Apply (ramp out {doc.Current.RampOut})");
        int before = doc.History.UndoLabels.Count;
        doc.Inspector.End.BeginInteraction();
        doc.Inspector.End.Value = doc.Inspector.End.Value + 1;
        doc.Inspector.End.Value = doc.Inspector.End.Value + 1;
        doc.Inspector.End.Value = doc.Inspector.End.Value + 1;
        doc.Inspector.End.EndInteraction();
        ctx.Check(doc.History.UndoLabels.Count == before + 1, $"a stepping gesture is one undo step ({doc.History.UndoLabels.Count - before} added)");
        ctx.Check(doc.Current.EndTime == original.EndTime + 3 * TimeFormat.FromUnit(1, model.TimeUnit), $"coalesced value lands (end {doc.Current.EndTime})");
        bool refused = !doc.Apply("Bad edit", c => ClipEdit.SetHeader(c, new ClipHeaderChange { RampIn = -5 }));
        ctx.Check(refused && doc.StatusMessage is { Length: > 0 }, $"a refused Core edit leaves the clip alone and says why ('{doc.StatusMessage}')");
        while (doc.CanUndo) doc.Undo();
        ctx.Check(!doc.IsDirty && ReferenceEquals(doc.Current, original), "undoing every step returns to the opened snapshot (clean)");
        doc.Redo();
        ctx.Check(doc.Current.RampIn == 320 && model.RedoHeader.Contains("ramp out", StringComparison.OrdinalIgnoreCase), $"redo re-applies; next redo is '{model.RedoHeader}'");
        doc.Undo();
        string name = doc.DisplayName;
        model.CloseDocument(doc);
        ctx.Check(!model.Documents.Contains(doc), "a clean tab closes without a prompt");
        // Cairn: the shell owns closed-tab history (ShellViewModel.ReopenClosedCommand, File > Reopen Closed Tab).
        (ctx.Shell.GetType().GetProperty("ReopenClosedCommand")?.GetValue(ctx.Shell) as System.Windows.Input.ICommand)?.Execute(null);
        await ctx.SettleAsync();
        ctx.Check(model.ActiveDocument?.DisplayName == name, "Reopen Closed Tab brings it back");
        // Save and external change, only for a file under %TEMP% (the self-test never writes anywhere else).
        if (model.ActiveDocument is ClipDocumentViewModel again && again.FilePath is { } path
            && Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            again.Apply("Set ramp in", c => ClipEdit.SetHeader(c, new ClipHeaderChange { RampIn = 480 }));
            bool saved = model.Save(again);
            var reread = RfaReader.ReadFile(path);
            ctx.Check(saved && !again.IsDirty && reread.RampIn == 480, "Save writes the file atomically and clears the dirty marker");
            ctx.Check(!File.Exists(path + ".tmp") && Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*").Length == 1, "no temporary file is left beside it");
            await Task.Delay(1500);
            byte[] changed = RfaWriter.Write(ClipEdit.SetHeader(reread, new ClipHeaderChange { RampOut = 960 }));
            File.WriteAllBytes(path, changed);
            await Task.Delay(1500);
            await ctx.SettleAsync();
            ctx.Check(again.Current.RampOut == 960 && !again.IsDirty, $"a clean tab reloads a file changed on disk (ramp out {again.Current.RampOut})");
            again.Apply("Set ramp in", c => ClipEdit.SetHeader(c, new ClipHeaderChange { RampIn = 160 }));
            File.WriteAllBytes(path, RfaWriter.Write(ClipEdit.SetHeader(reread, new ClipHeaderChange { RampOut = 320 })));
            await Task.Delay(1500);
            await ctx.SettleAsync();
            ctx.Check(again.HasExternalChange && again.Current.RampIn == 160, "a dirty tab keeps its edit and shows the changed-on-disk bar");
            again.KeepMineCommand.Execute(null);
            ctx.Check(!again.HasExternalChange && again.IsDirty, "Keep mine hides the bar and keeps the unsaved edit");
            while (again.CanUndo) again.Undo();
        }
    }
}
