using System.Windows.Controls;
using Cairn.Shell.Probe;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;

namespace Cairn.Shell;

/// <summary>Panel freshness: tabs follow the active document and a module's <c>RefreshCommands</c>.</summary>
public static class PanelSelfTests
{
    private sealed class DiscardDialogs : DialogService
    {
        public override UnsavedChoice AskUnsavedChanges(IReadOnlyList<string> documentNames) => UnsavedChoice.DontSave;
    }

    [SelfTest("shell.panels-refresh")]
    public static void PanelsRefresh(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var kind = shell.Kinds.FirstOrDefault(k => k.Id == "probe");
        if (!ctx.Check(kind != null, "probe module loaded (run with --probe-module)")) return;
        var window = (MainWindow)shell.MainWindow;
        shell.Dialogs = new DiscardDialogs();
        shell.CloseAll();
        var doc = kind!.CreateNew() as ProbeDocument;
        if (!ctx.Check(doc != null, "probe document created")) return;
        doc!.Text = "abc";
        shell.AddDocument(doc);
        bool HasProbeTab() => window.BottomTabs.Items.OfType<TabItem>().Any(t => (string)t.Tag == "probe.length");
        ctx.Check(HasProbeTab(), "panel shown for a document with text");
        doc.Text = "#nopanel";
        shell.RefreshCommands();
        ctx.Check(!HasProbeTab(), "RefreshCommands removes the panel that now returns null");
        doc.Text = "x";
        shell.RefreshCommands();
        ctx.Check(HasProbeTab(), "RefreshCommands brings the panel back");
        shell.CloseAll();
    }

    [SelfTest("shell.panels-default-tab")]
    public static void PanelsDefaultTab(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var kind = shell.Kinds.FirstOrDefault(k => k.Id == "probe");
        var two = shell.Modules.OfType<ProbeTwoModule>().FirstOrDefault();
        if (!ctx.Check(kind != null && two != null, "probe modules loaded (run with --probe-module)")) return;
        var window = (MainWindow)shell.MainWindow;
        shell.Dialogs = new DiscardDialogs();
        shell.CloseAll();
        var doc = (ProbeDocument)kind!.CreateNew()!;
        var key = MainWindow.TabKey("bottom", doc);
        var saved = shell.Settings.Get<string>(key);
        shell.Settings.Set<string>(key, null);
        string? Selected() => (window.BottomTabs.SelectedItem as TabItem)?.Tag as string;
        shell.AddDocument(doc);
        ctx.Check(window.BottomTabs.Items.OfType<TabItem>().Select(t => (string)t.Tag).SequenceEqual(["probe.length", "probe.lines"]), "bottom tabs sorted by Order");
        ctx.Check(Selected() == "probe.length", "first activation of a kind selects the lowest-Order bottom tab");
        window.BottomTabs.SelectedItem = window.BottomTabs.Items.OfType<TabItem>().First(t => (string)t.Tag == "probe.lines");
        ctx.Check(shell.Settings.Get<string>(key) == "probe.lines", "the user's choice is stored under shell.bottomTab.<kind>");
        var other = new ProbeDocument(shell, two!.Kind, "x", "Other.cairnprobe2", null);
        shell.AddDocument(other);
        ctx.Check(Selected() == "probe.length", "another kind gets its own default");
        shell.ActiveDocument = doc;
        ctx.Check(Selected() == "probe.lines", "returning to the kind restores its last bottom tab");
        shell.CloseAll();
        shell.Settings.Set(key, saved);
        shell.Settings.Set<string>(MainWindow.TabKey("bottom", other), null);
    }
}
