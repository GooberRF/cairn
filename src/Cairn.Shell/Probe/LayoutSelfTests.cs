using System.IO;
using System.Text.Json;
using System.Windows;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>Self-tests of the main window's layout persistence (in memory only; the settings file is never written).</summary>
public static class LayoutSelfTests
{
    [SelfTest("shell.window-placement")]
    public static void WindowPlacementRoundTrip(SelfTestContext ctx)
    {
        var minimum = new Size(960, 600);
        var defaults = new Size(1500, 900);
        var primary = new Rect(0, 0, 1920, 1040);
        var second = new Rect(1920, 0, 2560, 1400);
        Rect[] workAreas = [primary, second];

        // Maximised on the second monitor: the restore bounds and the flag survive a JSON round trip of the settings.
        var restore = new Rect(2100, 120, 1400, 850);
        var captured = WindowPlacementLogic.Capture(restore, maximized: true);
        if (!ctx.Check(captured is { IsSet: true, Maximized: true }, "capture keeps the maximised flag and marks the placement set")) return;
        var settings = new AppSettings { Window = captured! };
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings.Clone()))!;
        var (bounds, maximized) = WindowPlacementLogic.Resolve(reloaded.Window, workAreas, primary, minimum, defaults);
        ctx.Check(maximized, "maximised flag restored");
        ctx.Check(bounds == restore, $"restore bounds restored (got {bounds})");

        // Normal state, smaller than the minimum: grown to the minimum, position kept.
        var small = WindowPlacementLogic.Capture(new Rect(40, 30, 500, 300), maximized: false)!;
        (bounds, maximized) = WindowPlacementLogic.Resolve(small, workAreas, primary, minimum, defaults);
        ctx.Check(!maximized && bounds == new Rect(40, 30, 960, 600), $"undersized window grown to the minimum in place (got {bounds})");

        // Off-screen (monitor unplugged): reset to the default size centred on the default work area.
        var gone = new WindowPlacement { Left = 5200, Top = 300, Width = 1200, Height = 800, Maximized = true, IsSet = true };
        (bounds, maximized) = WindowPlacementLogic.Resolve(gone, [primary], primary, minimum, defaults);
        ctx.Check(bounds == WindowPlacementLogic.Centred(primary, minimum, defaults), $"off-screen window centred on the primary work area (got {bounds})");
        ctx.Check(primary.Contains(bounds), "centred window lies inside the work area");
        ctx.Check(maximized, "maximised flag kept when the rectangle is reset");

        // Only a sliver of the title bar visible, or the title bar above the screen: not reachable.
        ctx.Check(!WindowPlacementLogic.IsReachable(new Rect(1900, 100, 800, 600), [primary]), "a 20 px sliver is not reachable");
        ctx.Check(!WindowPlacementLogic.IsReachable(new Rect(100, -500, 800, 400), [primary]), "a title bar above the screen is not reachable");
        ctx.Check(WindowPlacementLogic.IsReachable(new Rect(-600, 10, 800, 600), [primary]), "a window partly off the left edge is reachable");

        // Unusable bounds are not captured; an unset placement is centred.
        ctx.Check(WindowPlacementLogic.Capture(Rect.Empty, false) is null, "empty restore bounds are not captured");
        (bounds, maximized) = WindowPlacementLogic.Resolve(new WindowPlacement(), workAreas, primary, minimum, defaults);
        ctx.Check(!maximized && primary.Contains(bounds), "unset placement centred on the default area");
    }

    /// <summary><c>--welcome-recent 1</c>: opens and closes a few probe files so the welcome view shows a recent list.</summary>
    [ScreenshotStep(90)]
    public static void FillRecent(ScreenshotContext ctx)
    {
        if (!ctx.Options.ContainsKey("welcome-recent")) return;
        var shell = (ShellViewModel)ctx.Shell;
        var folder = Path.Combine(Path.GetTempPath(), "Cairn-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "walk cycle notes.cairnprobe", "level ideas.cairnprobe", "probe.cairnprobe" })
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, name);
            shell.OpenFile(path);
        }
        shell.CloseAll();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    private sealed class DiscardDialogs : DialogService
    {
        public override UnsavedChoice AskUnsavedChanges(IReadOnlyList<string> documentNames) => UnsavedChoice.DontSave;
    }

    [SelfTest("shell.welcome-and-panes")]
    public static void WelcomeAndPanes(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        if (!ctx.Check(shell.Kinds.Any(k => k.Id == "probe"), "probe module loaded (run with --probe-module)")) return;
        var window = (MainWindow)shell.MainWindow;
        shell.Dialogs = new DiscardDialogs();
        shell.CloseAll();
        ctx.Check(window.Welcome.IsVisible, "welcome view shown without documents");
        ctx.Check(window.Welcome.NewGroups.Items.OfType<WelcomeGroup>().SelectMany(g => g.Kinds).Any(k => k.Kind.Id == "probe"), "welcome offers a new probe document");
        ctx.Check(window.Welcome.ModuleList.Items.OfType<WelcomeModule>().Any(m => m.Handles.Contains(".cairnprobe")), "welcome lists the probe module with its extension");

        var folder = Path.Combine(Path.GetTempPath(), "Cairn-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "welcome.cairnprobe");
            File.WriteAllText(path, "welcome");
            if (!ctx.Check(shell.OpenFile(path), "probe file opens")) return;
            window.UpdateLayout();
            ctx.Check(!window.Welcome.IsVisible, "welcome view hidden while a document is open");

            // Pane toggles: the bottom pane hides and comes back, and its tab survives.
            var bottomWasVisible = window.BottomPane.Visibility == Visibility.Visible;
            shell.ToggleBottomPaneCommand.Execute(null);
            ctx.Check((window.BottomPane.Visibility == Visibility.Visible) != bottomWasVisible, "bottom pane toggled by its command");
            shell.ToggleBottomPaneCommand.Execute(null);
            ctx.Check((window.BottomPane.Visibility == Visibility.Visible) == bottomWasVisible && window.BottomTabs.Items.Count > 0, "bottom pane toggled back with its tab");
            ctx.Check(shell.ShellShortcuts.Any(s => s.Command == shell.ToggleBottomPaneCommand) && shell.ShellShortcuts.Any(s => s.Command == shell.ToggleLeftPaneCommand),
                "pane toggles are shell shortcuts (listed in Help)");

            shell.CloseAll();
            window.UpdateLayout();
            ctx.Check(window.Welcome.IsVisible, "welcome view back after closing");
            ctx.Check(window.Welcome.RecentList.Items.OfType<WelcomeRecent>().Any(r => string.Equals(r.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)),
                "the closed file is in the welcome view's recent list");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
