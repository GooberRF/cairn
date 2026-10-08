using System.Windows;
using Cairn.Shell.Dialogs;
using Cairn.Ui.Diagnostics;
using Cairn.Workspace;

namespace Cairn.Shell.Probe;

/// <summary>
/// <c>--dialog</c> captures for the shell's own dialogs. Each opens non-modally over the main
/// window with fabricated content; nothing is written to the recovery store or the crash log.
/// </summary>
public static class DialogScreenshots
{
    [ScreenshotDialog("about")]
    public static Window About(ScreenshotContext ctx) => ShowOver(ctx, AboutDialog.CreateForCapture(ctx.MainWindow));

    [ScreenshotDialog("help")]
    public static Window Help(ScreenshotContext ctx)
    {
        // --help-topic <id>: that module topic instead of the shortcut table.
        var topic = ctx.Options.TryGetValue("help-topic", out var id) && !string.IsNullOrWhiteSpace(id) ? id : HelpWindow.ShortcutsId;
        var window = HelpWindow.Show((ShellViewModel)ctx.Shell, topic);
        window.Owner ??= ctx.MainWindow;
        return window;
    }

    [ScreenshotDialog("recovery")]
    public static Window Recovery(ScreenshotContext ctx)
    {
        // A made-up path off the system drive, so the capture shows no user name.
        var games = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty, "Games");
        var now = DateTime.UtcNow;
        IReadOnlyList<RecoverySnapshot> snapshots =
        [
            new("capture-1", Path.Combine(games, "Red Faction", "anims", "miner_walk.rfa"), "miner_walk.rfa", now.AddMinutes(-3), "probe", []),
            new("capture-2", null, "Untitled 2", now.AddMinutes(-4), "probe", []),
            // The running exe is certainly newer than this, so the row shows its disk-is-newer warning.
            new("capture-3", Environment.ProcessPath, Path.GetFileName(Environment.ProcessPath) ?? "Cairn.exe", now.AddYears(-5), "probe", []),
        ];
        return ShowOver(ctx, RecoveryDialog.CreateForCapture(ctx.MainWindow, snapshots));
    }

    [ScreenshotDialog("crash")]
    public static Window Crash(ScreenshotContext ctx)
    {
        string details;
        try
        {
            throw new InvalidOperationException("Fabricated failure for the crash dialog capture.");
        }
        catch (InvalidOperationException ex)
        {
            details = ex.ToString();
        }
        return ShowOver(ctx, CrashDialog.CreateForCapture(ctx.MainWindow,
            "Cairn hit a problem it did not expect. Your unsaved documents have been copied to the recovery folder, so nothing is lost.",
            ["miner_walk.rfa", "Untitled 2"], details));
    }

    private static Window ShowOver(ScreenshotContext ctx, Window window)
    {
        window.Owner ??= ctx.MainWindow;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Show();
        return window;
    }
}
