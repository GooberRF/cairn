#if CAIRN_MODULE_LVL
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Cairn.Lvl.Ui.Diagnostics;
using Cairn.Lvl.Ui.Documents;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;

namespace Cairn.Shell;

/// <summary>
/// A level tab's fly camera against the shell's shortcuts: while flying, the movement keys with Shift or Ctrl (faster,
/// slower) move the camera instead of running Ctrl+W (close the tab), Ctrl+Shift+E (convert) or Ctrl+A; when the
/// camera orbits again, the shortcuts are back.
/// </summary>
public static class FlyKeysSelfTests
{
    [SelfTest("integration.fly-keys")]
    public static async Task FlyKeys(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var window = (MainWindow)ctx.MainWindow;
        var router = window.Shortcuts;
        string folder = Path.Combine(Path.GetTempPath(), $"cairn-fly-keys-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(folder);
        LvlDocument? doc = null;
        try
        {
            string path = Path.Combine(folder, "fly.rfl");
            File.WriteAllBytes(path, LvlSyntheticLevel.Bytes());
            if (!ctx.Check(shell.OpenFile(path) && shell.ActiveDocument is LvlDocument, "a synthetic level opens in a tab")) return;
            doc = (LvlDocument)shell.ActiveDocument!;
            await doc.Loading;
            await ctx.SettleAsync();
            if (!ctx.Check(doc.View is LvlDocumentView, "the level view is built")) return;
            var scene = ((LvlDocumentView)doc.View).Scene;
            await scene.Building;
            await ctx.SettleAsync();
            var surface = scene.Surface;
            int windows = Application.Current.Windows.Count;

            const ModifierKeys Ctrl = ModifierKeys.Control, CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
            ctx.Check(router.Candidates().Any(s => s.Key == Key.W && s.Modifiers == Ctrl && (s.AppliesTo?.Invoke(doc) ?? true)),
                "Ctrl+W is a shortcut for the level tab (Close tab)");
            ctx.Check(!KeyboardClaims.GetMovementKeys(surface), "orbiting: the surface claims no keys");

            scene.FlyMode = true;
            ctx.Check(KeyboardClaims.GetMovementKeys(surface), "flying: the surface claims the movement keys");
            foreach (var (key, modifiers) in new[] { (Key.W, Ctrl), (Key.E, CtrlShift), (Key.A, Ctrl), (Key.S, Ctrl), (Key.D, ModifierKeys.Shift) })
            {
                bool ran = router.Route(window, key, modifiers, surface, out string? reason);
                ctx.Check(!ran && reason is not null && shell.Documents.Contains(doc) && shell.ActiveDocument == doc && Application.Current.Windows.Count == windows,
                    $"{modifiers}+{key} while flying runs no shortcut ({reason ?? "ran"})");
            }
            ctx.Check(!KeyboardClaims.ClaimsMovementKey(surface, Key.W, ModifierKeys.Alt) && !KeyboardClaims.ClaimsMovementKey(surface, Key.F, Ctrl),
                "Alt gestures and other keys are not claimed");

            // A real key press, routed from the window down to the surface, reaches the camera.
            if (PresentationSource.FromVisual(window) is { } source)
            {
                var eye = surface.Camera.Eye;
                var forward = surface.Camera.Forward;
                var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.W) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                surface.RaiseEvent(down);
                await Task.Delay(400);
                surface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.W) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
                var moved = surface.Camera.Eye - eye;
                ctx.Check(down.Handled && moved.Length() > 1e-3f && Vector3.Dot(Vector3.Normalize(moved), forward) > 0.99f,
                    $"holding W moves the eye forward ({moved.Length():0.###} m)");
                ctx.Check(shell.Documents.Contains(doc), "the level tab is still open");
            }

            scene.FlyMode = false;
            ctx.Check(!KeyboardClaims.GetMovementKeys(surface), "orbiting again: the claim is gone");
            ctx.Check(router.Route(window, Key.W, Ctrl, surface, out _) && !shell.Documents.Contains(doc), "Ctrl+W closes the tab again when not flying");
        }
        finally
        {
            if (doc is not null && shell.Documents.Contains(doc)) shell.Close(doc);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
#endif
