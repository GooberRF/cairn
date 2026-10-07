#if CAIRN_MODULE_VPP
using System.Text;
using System.Windows.Controls;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Work;
using Cairn.Vpp.Writing;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>
/// The Recent list and packfile entries: work copies are recorded as "packfile › entry" and reopen through the packfile
/// module; paths in the work area (recorded by older versions) are hidden and dropped; a vanished packfile is reported
/// and removed. Also the start page's own left-tab memory.
/// </summary>
public static class RecentEntrySelfTests
{
    private sealed class DiscardDialogs : DialogService
    {
        public override UnsavedChoice AskUnsavedChanges(IReadOnlyList<string> documentNames) => UnsavedChoice.DontSave;
    }

    [SelfTest("shell.recent-entries")]
    public static async Task RecentEntries(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        if (shell.Modules.OfType<IWorkCopyProvider>().FirstOrDefault(p => p.CanOpenArchive("x.vpp")) is not { } provider) { ctx.Skip("needs the packfile module"); return; }
        if (!shell.Kinds.Any(k => k.Extensions.Contains(".tbl"))) { ctx.Skip("needs the table module"); return; }
        var dialogs = shell.Dialogs;
        var collecting = new DiscardDialogs { CollectErrors = [] };
        shell.Dialogs = collecting;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-recent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var injected = new List<string>();
        try
        {
            shell.CloseAll();
            // a work-copy path an older version recorded: hidden at once, dropped from the settings when the list is saved
            string legacy = Path.Combine(VppWorkRoot.Default, "0123456789abcdef", "legacy_info.tbl");
            shell.InjectRecentForTest(legacy);
            injected.Add(legacy);
            ctx.Check(!shell.RecentFiles.Contains(legacy, StringComparer.OrdinalIgnoreCase), "a work-area path left by an older version is not listed");
            shell.SaveRecent();
            ctx.Check(!shell.Settings.RecentFiles.Contains(legacy, StringComparer.OrdinalIgnoreCase), "... and is dropped from the settings when the list is saved");

            // an entry reference reopens through the packfile module
            var package = VppEdit.AddSources(VppPackage.Empty, [("pack_info.tbl", (VppSource)new MemorySource(Encoding.ASCII.GetBytes("#General\r\n$Name: \"pack\"\r\n#End\r\n")))], VppClashPolicy.KeepBoth).Package;
            string vpp = Path.Combine(folder, "pack.vpp");
            VppSaver.Save(package, vpp, null, CancellationToken.None, VppSaveOptions.Default);
            string item = RecentFilesList.EntryReference(vpp, "pack_info.tbl");
            shell.InjectRecentForTest(item);
            injected.Add(item);
            ctx.Check(shell.RecentFiles.FirstOrDefault() == item, "an entry reference is listed");
            var (name, dim) = ShellViewModel.DescribeRecent(item);
            ctx.Check(name == "pack.vpp › pack_info.tbl" && dim == folder, $"it reads \"packfile › entry\" with the packfile's folder dimmed ({name} | {dim})");
            shell.OpenRecentCommand.Execute(item);
            IDocument? table = null;
            for (int i = 0; i < 100 && table is null; i++)
            {
                await Task.Delay(100);
                table = shell.Documents.FirstOrDefault(d => d.FilePath is { } p && p.EndsWith(".tbl", StringComparison.OrdinalIgnoreCase));
            }
            ctx.Check(shell.Documents.Any(d => string.Equals(d.FilePath, vpp, StringComparison.OrdinalIgnoreCase)), "reopening opens the packfile");
            ctx.Check(table?.FilePath is { } copy && provider.IsWorkCopy(copy) && provider.ArchiveEntryOf(copy)?.EntryName == "pack_info.tbl",
                $"... and the entry from a work copy, as Open in Cairn does ({table?.FilePath})");
            ctx.Check(shell.RecentFiles.FirstOrDefault() == item && !shell.RecentFiles.Any(provider.IsWorkCopy), "the Recent list keeps the entry reference, never the work copy");
            ctx.Check(collecting.CollectErrors!.Count == 0, $"no errors ({string.Join(" | ", collecting.CollectErrors)})");
            shell.CloseAll();

            // the packfile is gone: told, and removed from the list as a missing file is
            string gone = RecentFilesList.EntryReference(Path.Combine(folder, "gone.vpp"), "gone_info.tbl");
            shell.InjectRecentForTest(gone);
            injected.Add(gone);
            shell.OpenRecentCommand.Execute(gone);
            ctx.Check(collecting.CollectErrors.Any(e => e.Contains("gone.vpp", StringComparison.OrdinalIgnoreCase)), "a vanished packfile is reported");
            ctx.Check(!shell.RecentFiles.Contains(gone) && !shell.Settings.RecentFiles.Contains(gone), "... and removed from the Recent list");

            // the entry is gone from a packfile that is still there: reported and removed too
            string missing = RecentFilesList.EntryReference(vpp, "no_such.tbl");
            shell.InjectRecentForTest(missing);
            injected.Add(missing);
            shell.OpenRecentCommand.Execute(missing);
            ctx.Check(collecting.CollectErrors.Any(e => e.Contains("no_such.tbl", StringComparison.Ordinal)) && !shell.RecentFiles.Contains(missing), "a vanished entry is reported and removed");
        }
        finally
        {
            shell.CloseAll();
            foreach (var i in injected) shell.ForgetRecentForTest(i);
            shell.Dialogs = dialogs;
            VppWorkFolder.TryDeleteFolder(folder);
        }
    }

    [SelfTest("shell.start-tab-memory")]
    public static async Task StartTabMemory(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var window = (MainWindow)shell.MainWindow;
        var kind = shell.Kinds.FirstOrDefault(k => k.Id == "probe");
        if (!ctx.Check(kind != null, "probe module loaded (run with --probe-module)")) return;
        var dialogs = shell.Dialogs;
        shell.Dialogs = new DiscardDialogs();
        var saved = shell.Settings.Get<string>(MainWindow.StartLeftTabKey);
        string? Selected() => (window.LeftTabs.SelectedItem as TabItem)?.Tag as string;
        try
        {
            shell.Settings.Set<string>(MainWindow.StartLeftTabKey, null);
            shell.CloseAll();
            shell.AddDocument(kind!.CreateNew()!);
            shell.CloseAll();
            await ScreenshotContext.YieldAsync();
            var ids = window.LeftTabs.Items.OfType<TabItem>().Select(t => (string)t.Tag).ToList();
            ctx.Check(ids.Count > 0 && Selected() == ids[0], $"with nothing remembered the start page selects its first (lowest Order) left tab ({string.Join(", ", ids)})");
            if (ids.Count < 2) { ctx.Log("  only one start-page tab in this build: the memory check is skipped"); return; }
            window.LeftTabs.SelectedItem = window.LeftTabs.Items.OfType<TabItem>().First(t => (string)t.Tag == ids[1]);
            ctx.Check(shell.Settings.Get<string>(MainWindow.StartLeftTabKey) == ids[1], $"choosing a start-page tab stores it under {MainWindow.StartLeftTabKey}");
            shell.AddDocument(kind.CreateNew()!);
            await ScreenshotContext.YieldAsync();
            shell.CloseAll();
            await ScreenshotContext.YieldAsync();
            ctx.Check(Selected() == ids[1], $"closing the last document returns to the remembered start tab ({Selected()})");
        }
        finally
        {
            shell.CloseAll();
            shell.Settings.Set(MainWindow.StartLeftTabKey, saved);
            shell.Dialogs = dialogs;
        }
    }
}
#endif
