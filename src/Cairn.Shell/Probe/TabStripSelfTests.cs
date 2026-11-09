using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Services;

namespace Cairn.Shell;

/// <summary>
/// The document tab strip with more tabs than fit: the scrolling row (arrows, the tab in front kept in sight), the
/// rows layout, the list of open tabs, the tab menu's close commands and the tab shortcuts. Probe documents only.
/// </summary>
public static class TabStripSelfTests
{
    private sealed class DiscardDialogs : DialogService
    {
        public override UnsavedChoice AskUnsavedChanges(IReadOnlyList<string> documentNames) => UnsavedChoice.DontSave;
    }

    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static bool InSight(MainWindow window, IDocument doc)
    {
        var scroller = window.TabScroller!;
        if (window.DocumentTabs.ItemContainerGenerator.ContainerFromItem(doc) is not TabItem tab) return false;
        var left = tab.TransformToAncestor(scroller).Transform(new Point(0, 0)).X;
        return left >= -0.5 && left + tab.ActualWidth <= scroller.ActualWidth + 0.5;
    }

    /// <summary><c>--tab-rows</c>: the screenshot shows the document tabs on several rows.</summary>
    [ScreenshotStep(95)]
    public static void TabRows(ScreenshotContext ctx)
    {
        if (ctx.Options.ContainsKey("tab-rows")) ((ShellViewModel)ctx.Shell).DocumentTabRows = true;
    }

    [SelfTest("shell.document-tabs")]
    public static async Task DocumentTabs(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        if (!ctx.Check(shell.Kinds.FirstOrDefault(k => k.Id == "probe" && k.CanCreateNew) is not null, "probe module loaded (run with --probe-module)")) return;
        var kind = shell.Kinds.First(k => k.Id == "probe" && k.CanCreateNew);
        var window = (MainWindow)shell.MainWindow;
        var dialogs = shell.Dialogs;
        bool rows = shell.DocumentTabRows;
        shell.Dialogs = new DiscardDialogs();
        try
        {
            shell.CloseAll();
            shell.DocumentTabRows = false;
            for (int i = 0; i < 30; i++) shell.NewCommand.Execute(kind);
            var docs = shell.OpenDocuments.ToList();
            await SettleAsync(window);
            var scroller = window.TabScroller;
            if (!ctx.Check(scroller is not null && window.TabsLeft is not null && window.TabsRight is not null, "the strip has its scroller and arrows")) return;
            ctx.Check(scroller!.ScrollableWidth > 0, $"30 tabs do not fit one row ({scroller.ExtentWidth:N0} of {scroller.ViewportWidth:N0} px)");
            ctx.Check(window.TabsLeft!.IsVisible && window.TabsRight!.IsVisible, "the arrows show while tabs are out of sight");
            ctx.Check(InSight(window, docs[^1]), "the last tab opened (in front) is scrolled into sight");
            ctx.Check(!window.TabsRight!.IsEnabled && window.TabsLeft.IsEnabled, "at the right end only the left arrow is on");

            shell.ActiveDocument = docs[0];
            await SettleAsync(window);
            ctx.Check(InSight(window, docs[0]) && scroller.HorizontalOffset < 0.5, "bringing the first tab to the front scrolls back to it");
            ctx.Check(!window.TabsLeft.IsEnabled && window.TabsRight.IsEnabled, "at the left end only the right arrow is on");
            double before = scroller.HorizontalOffset;
            window.ScrollTabs(1);
            await SettleAsync(window);
            ctx.Check(scroller.HorizontalOffset > before, $"the right arrow scrolls the strip ({before:N0} -> {scroller.HorizontalOffset:N0} px)");
            window.ScrollTabs(-1);
            await SettleAsync(window);
            ctx.Check(scroller.HorizontalOffset < 0.5, "the left arrow scrolls back");

            window.Shortcuts.TryExecute(Key.PageUp, ModifierKeys.Control, null);
            await SettleAsync(window);
            ctx.Check(shell.ActiveDocument == docs[^1] && InSight(window, docs[^1]), "Ctrl+PageUp from the first tab goes to the last, in sight");
            window.Shortcuts.TryExecute(Key.PageDown, ModifierKeys.Control, null);
            ctx.Check(shell.ActiveDocument == docs[0], "Ctrl+PageDown goes on to the first again");

            var list = window.TabListMenu();
            var items = list.Items.OfType<MenuItem>().ToList();
            ctx.Check(items.Count == docs.Count && items.Count(i => i.IsChecked) == 1 && items[0].IsChecked, "the tab list names every open tab, the one in front ticked");
            items[17].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await SettleAsync(window);
            ctx.Check(shell.ActiveDocument == docs[17] && InSight(window, docs[17]), "choosing a tab in the list brings it to the front, in sight");

            double oneRow = window.DocumentTabs.ActualHeight;
            shell.DocumentTabRows = true;
            await SettleAsync(window);
            ctx.Check(!window.TabsLeft.IsVisible && !window.TabsRight.IsVisible && scroller.ScrollableWidth < 0.5, "several rows: no arrows, nothing to scroll");
            ctx.Check(window.DocumentTabs.ActualHeight > oneRow * 1.5, $"several rows: the strip grows ({oneRow:N0} -> {window.DocumentTabs.ActualHeight:N0} px)");
            ctx.Check(docs.All(d => InSight(window, d)), "several rows: every tab is in sight");
            shell.DocumentTabRows = false;
            await SettleAsync(window);
            ctx.Check(Math.Abs(window.DocumentTabs.ActualHeight - oneRow) < 0.5 && InSight(window, docs[17]), "back to one row, with the tab in front in sight");

            var menu = window.TabMenu(docs[9]).Items.OfType<MenuItem>().ToList();
            ctx.Check(menu.Select(m => m.Header as string).SequenceEqual(["_Close", "Close _Others", "Close Tabs to the _Right", "Close _All"]), "a tab's menu offers the close commands");
            menu[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ctx.Check(shell.OpenDocuments.SequenceEqual(docs.Take(10)) && shell.ActiveDocument == docs[9], "Close tabs to the right keeps the first ten, the clicked one in front");
            ctx.Check(!window.TabMenu(docs[9]).Items.OfType<MenuItem>().ElementAt(2).IsEnabled, "... and is off for the last tab");
            window.TabMenu(docs[3]).Items.OfType<MenuItem>().ElementAt(1).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ctx.Check(shell.OpenDocuments.SequenceEqual([docs[3]]) && shell.ActiveDocument == docs[3], "Close others leaves only that tab, in front");
            await SettleAsync(window);
            ctx.Check(!window.TabsLeft.IsVisible && !window.TabsRight.IsVisible, "one tab: no arrows");
        }
        finally
        {
            shell.CloseAll();
            shell.DocumentTabRows = rows;
            shell.Dialogs = dialogs;
        }
    }
}
