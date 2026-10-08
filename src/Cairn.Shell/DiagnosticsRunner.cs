using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;

namespace Cairn.Shell;

/// <summary>
/// Runs <c>--screenshot</c>, <c>--selftest</c> and <c>--dialog</c>. Methods are found in the shell
/// assembly and every module's assembly (each scanned once).
/// Order: modules initialised, window shown, command-line files opened (the last one active),
/// <see cref="IModule.ApplyDiagnosticOptions"/> called once, then self-tests, then screenshot steps,
/// then the capture (main window, <c>--dialog</c> window, or <c>--popup</c> menu).
/// Self-test exit code = the number of failed checks; skipped tests do not count.
/// </summary>
public static class DiagnosticsRunner
{
    public static async Task RunAsync(MainWindow window, ShellViewModel shell, CommandLine options)
    {
        var exit = 0;
        try
        {
            if (options.Size is { } size) { window.Width = size.Width; window.Height = size.Height; }
            window.Show();
            var opened = new List<IDocument>();
            foreach (var f in options.Files)
            {
                if (shell.OpenFile(f) && shell.ActiveDocument is { } doc) { if (!opened.Contains(doc)) opened.Add(doc); }
                else Console.WriteLine($"could not open {f}");
            }
            await ScreenshotContext.YieldAsync();
            var opts = (IReadOnlyDictionary<string, string>)options.ModuleOptions;
            foreach (var module in shell.Modules) module.ApplyDiagnosticOptions(opts);

            var registry = new DiagnosticsRegistry(new[] { typeof(App).Assembly }.Concat(shell.Modules.Select(m => m.GetType().Assembly)));
            if (options.Dialog is { } requested && (requested.Equals("list", StringComparison.OrdinalIgnoreCase) || !registry.Dialogs.ContainsKey(requested)))
            {
                if (!requested.Equals("list", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"unknown dialog '{requested}'"); exit = 2; }
                Console.WriteLine("dialogs: " + string.Join(", ", registry.Dialogs.Values.OrderBy(d => d.Name).Select(d => $"{d.Name} ({Owner(d.Assembly)})")));
                Application.Current.Shutdown(exit);
                return;
            }

            if (options.SelfTest) exit = await RunSelfTestsAsync(window, shell, options, opts, registry, opened);

            var png = options.Screenshot ?? (options.Dialog is { } d ? $"dialog-{d}.png" : null);
            if (png is not null)
            {
                var ctx = new ScreenshotContext(window, shell, opts, Console.WriteLine);
                await ctx.SettleAsync();
                foreach (var step in registry.ScreenshotSteps)
                {
                    Console.WriteLine($"step {step.Method.DeclaringType?.Name}.{step.Name} ({step.Order})");
                    if (step.Invoke(ctx) is Task t) await t;
                }
                // Modules report pending work (texture decodes, background loads) through BusyTracker; this waits for it.
                await ctx.SettleAsync();
                if (options.Dialog is { } name) await CaptureDialogAsync(ctx, registry.Dialogs[name], png);
                else if (opts.TryGetValue("popup", out var popup)) await CapturePopupAsync(ctx, popup, png);
                else Capture(window, png);
                Console.WriteLine($"Wrote {png}");
            }
            if (options.Wait) return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            exit = 2;
        }
        Application.Current.Shutdown(exit);
    }

    private static string Owner(Assembly assembly) => assembly == typeof(App).Assembly ? "shell" : assembly.GetName().Name ?? "?";

    private sealed class Tally { public int Tests, Ok, Failed, Skipped; }

    /// <summary>A self-test running longer than this is reported as failed (async tests are abandoned).</summary>
    private static readonly TimeSpan PerTestLimit = TimeSpan.FromSeconds(240);

    /// <summary>
    /// Watches the UI thread's windows from a background thread while one self-test runs: a modal window (the
    /// main window disabled under another visible window of the thread) is logged with its title and closed, so
    /// a prompt nobody can answer never hangs an unattended run.
    /// </summary>
    private sealed class SelfTestWatchdog : IDisposable
    {
        private readonly nint _main;
        private readonly uint _thread;
        private readonly string _test;
        private readonly System.Threading.Timer _timer;
        private readonly List<string> _closed = [];
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public SelfTestWatchdog(Window main, string test)
        {
            _main = new System.Windows.Interop.WindowInteropHelper(main).Handle;
            _thread = Native.GetWindowThreadProcessId(_main, out _);
            _test = test;
            _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        }

        public IReadOnlyList<string> ClosedWindows { get { lock (_closed) return [.. _closed]; } }

        /// <summary>True once the test ran past <see cref="PerTestLimit"/>; its nested pumps are being ended.</summary>
        public bool TimedOut { get; private set; }

        private void Tick()
        {
            if (_clock.Elapsed >= PerTestLimit)
            {
                // A synchronous test cannot be abandoned: end its nested dispatcher frames so it returns.
                if (!TimedOut) Console.WriteLine($"WATCHDOG {_test}: over the {PerTestLimit.TotalSeconds:0} s limit, ending its nested pumps");
                TimedOut = true;
                SelfTestPump.AbortAll();
            }
            if (_main == 0 || Native.IsWindowEnabled(_main)) return; // no modal loop is running
            Native.EnumThreadWindows(_thread, (hwnd, _) =>
            {
                if (hwnd == _main || !Native.IsWindowVisible(hwnd) || !Native.IsWindowEnabled(hwnd)) return true;
                var text = new System.Text.StringBuilder(256);
                Native.GetWindowText(hwnd, text, text.Capacity);
                string title = text.Length == 0 ? "(untitled)" : text.ToString();
                lock (_closed) _closed.Add(title);
                Console.WriteLine($"WATCHDOG {_test}: closing modal window \"{title}\"");
                Native.PostMessage(hwnd, 0x0010 /* WM_CLOSE */, 0, 0);
                return true;
            }, 0);
        }

        public void Dispose() => _timer.Dispose();
    }

    private static class Native
    {
        public delegate bool EnumWindowsProc(nint hwnd, nint lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowEnabled(nint hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    }

    private static async Task<int> RunSelfTestsAsync(MainWindow window, ShellViewModel shell, CommandLine options,
        IReadOnlyDictionary<string, string> opts, DiagnosticsRegistry registry, List<IDocument> opened)
    {
        var shellAssembly = typeof(App).Assembly;
        var moduleOrder = shell.Modules.Select(m => m.GetType().Assembly).Distinct().ToList();
        // Module tests first (in module order), the shell's probe tests last: those replace and close documents.
        var tests = registry.SelfTests
            .Where(t => options.SelfTestOnly is null || t.Name.Equals(options.SelfTestOnly, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Assembly == shellAssembly ? int.MaxValue : moduleOrder.IndexOf(t.Assembly))
            .ThenBy(t => t.Order)
            .ToList();
        var tallies = new Dictionary<string, Tally>();
        var failures = 0;
        foreach (var test in tests)
        {
            var owner = Owner(test.Assembly);
            if (!tallies.TryGetValue(owner, out var tally)) tallies[owner] = tally = new Tally();
            tally.Tests++;
            string? skip = null;
            if (test.Assembly == shellAssembly && !options.ProbeModule) skip = "needs --probe-module";
            else if (!await ActivateFor(shell, test, opened)) skip = $"needs a {test.Requires} document on the command line";
            if (skip is not null)
            {
                tally.Skipped++;
                Console.WriteLine($"SKIP {test.Name}: {skip}");
                continue;
            }

            var ctx = new SelfTestContext(window, shell, opts, Console.WriteLine) { RootPathFinder = o => LeakFinder.FindPath(o) };
            Console.WriteLine($"---- {test.Name} [{owner}]" + (shell.ActiveDocument is { } active ? $" on {active.DisplayName}" : string.Empty));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var watchdog = new SelfTestWatchdog(window, test.Name))
            {
                try
                {
                    var result = test.Invoke(ctx);
                    if (result is Task t && await Task.WhenAny(t, Task.Delay(PerTestLimit)) == t) await t;
                    else if (result is Task) ctx.Check(false, $"{test.Name} did not finish within {PerTestLimit.TotalSeconds:0} s (abandoned; the run continues)");
                    else if (watchdog.TimedOut || clock.Elapsed >= PerTestLimit)
                        ctx.Check(false, $"{test.Name} ran {clock.Elapsed.TotalSeconds:0} s, over the {PerTestLimit.TotalSeconds:0} s limit{(watchdog.TimedOut ? " (its nested pumps were ended by the watchdog)" : "")}");
                }
                catch (Exception ex) { ctx.Check(false, $"threw {(ex as TargetInvocationException)?.InnerException ?? ex}"); }
                foreach (var title in watchdog.ClosedWindows)
                    ctx.Check(false, $"a modal window \"{title}\" appeared during {test.Name} and was closed by the watchdog");
            }
            if (clock.Elapsed >= PerTestLimit && ctx.Failures.Count == 0)
                ctx.Check(false, $"{test.Name} ran {clock.Elapsed.TotalSeconds:0} s, over the {PerTestLimit.TotalSeconds:0} s limit");
            failures += ctx.Failures.Count;
            tally.Ok += ctx.Passed;
            tally.Failed += ctx.Failures.Count;
            if (ctx.SkipReason is { } reason && ctx.Failures.Count == 0) { tally.Skipped++; Console.WriteLine($"SKIP {test.Name}: {reason}"); continue; }
            Console.WriteLine($"{(ctx.Failures.Count == 0 ? "PASS" : "FAIL")} {test.Name}");
            foreach (var f in ctx.Failures) Console.WriteLine("    " + f);
        }
        foreach (var (owner, t) in tallies)
            Console.WriteLine($"[{owner}] {t.Tests} tests, {t.Ok} checks ok, {t.Failed} failed, {t.Skipped} skipped");
        Console.WriteLine(failures == 0 ? "All self-tests passed." : $"{failures} check(s) failed.");
        return failures;
    }

    /// <summary>
    /// Before a test: re-activates the command-line document it needs (its <c>Requires</c>, else the first one
    /// of its module's kinds), reopening it from disk when an earlier test closed it. False when a requirement is unmet.
    /// </summary>
    private static async Task<bool> ActivateFor(ShellViewModel shell, DiagnosticMethod test, List<IDocument> opened)
    {
        Func<IDocument, bool> wanted;
        if (test.Requires is { } req)
            wanted = d => d.Kind.Id.Equals(req, StringComparison.OrdinalIgnoreCase)
                || d.Kind.Extensions.Contains(req, StringComparer.OrdinalIgnoreCase)
                || (d.FilePath is { } p && Path.GetExtension(p).Equals(req, StringComparison.OrdinalIgnoreCase));
        else if (shell.Modules.FirstOrDefault(m => m.GetType().Assembly == test.Assembly) is { } module && test.Assembly != typeof(App).Assembly)
            wanted = d => module.DocumentKinds.Contains(d.Kind);
        else return true;

        for (var i = 0; i < opened.Count; i++)
        {
            var doc = opened[i];
            if (!wanted(doc)) continue;
            if (!shell.Documents.Contains(doc))
            {
                if (doc.FilePath is not { } path || !shell.OpenFile(path) || shell.ActiveDocument is not { } again) continue;
                opened[i] = doc = again;
            }
            shell.Activate(doc);
            await ScreenshotContext.YieldAsync();
            return true;
        }
        return test.Requires is null;
    }

    /// <summary>As RFA's runner: show the <c>[ScreenshotDialog]</c> window non-modally over the main window, settle, capture it, close it.</summary>
    private static async Task CaptureDialogAsync(ScreenshotContext ctx, DiagnosticMethod method, string png)
    {
        var result = method.Invoke(ctx);
        if (result is Task task) { await task; result = task.GetType().GetProperty("Result")?.GetValue(task); }
        if (result is not Window dialog) throw new InvalidOperationException($"dialog '{method.Name}' returned no window");
        // A large or SizeToContent window (or one whose content arrives late) can still be unrendered after a fixed delay,
        // which captured as a blank image: wait for its first ContentRendered, then for busy work, layout and an idle dispatcher.
        var rendered = new TaskCompletionSource();
        dialog.ContentRendered += (_, _) => rendered.TrySetResult();
        if (dialog != ctx.MainWindow)
        {
            dialog.Owner ??= ctx.MainWindow;
            dialog.ShowActivated = false;
            if (!dialog.IsVisible)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                dialog.Show();
            }
            else rendered.TrySetResult();
        }
        else rendered.TrySetResult();
        await Task.WhenAny(rendered.Task, Task.Delay(10000));
        await Task.Delay(400);
        await ctx.SettleAsync();
        for (var pass = 0; pass < 3; pass++)
        {
            dialog.UpdateLayout();
            await dialog.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await ctx.SettleAsync();
        }
        await ScreenshotContext.YieldAsync();
        Capture(dialog, png);
        if (ctx.Options.ContainsKey("with-window"))
            Capture(ctx.MainWindow, Path.Combine(Path.GetDirectoryName(png) ?? ".", Path.GetFileNameWithoutExtension(png) + "_window.png"));
        if (dialog != ctx.MainWindow) dialog.Close();
    }

    /// <summary>
    /// <c>--popup &lt;header&gt;</c>: opens the main menu's top-level item with that header (access key ignored),
    /// <c>--popup &lt;header&gt;/&lt;sub&gt;</c> one of its submenus, or <c>--popup context</c> the first visible element's
    /// context menu (<c>--popup context:&lt;automation name&gt;</c>: that element's), and captures the popup.
    /// </summary>
    private static async Task CapturePopupAsync(ScreenshotContext ctx, string what, string png)
    {
        static string Plain(object? header) => (header as string ?? string.Empty).Replace("_", string.Empty);
        FrameworkElement? target = null;
        Action close = () => { };
        // "context:<automation name>" picks the element of that name (e.g. "context:Packfile entries")
        string? contextOf = what.StartsWith("context:", StringComparison.OrdinalIgnoreCase) ? what["context:".Length..] : null;
        if (what.Equals("context", StringComparison.OrdinalIgnoreCase) || contextOf is not null)
        {
            if (Find<FrameworkElement>(ctx.MainWindow, e => e.IsVisible && e.ContextMenu is not null
                && (contextOf is null || string.Equals(System.Windows.Automation.AutomationProperties.GetName(e), contextOf, StringComparison.OrdinalIgnoreCase))) is { ContextMenu: { } menu } owner)
            {
                menu.PlacementTarget = owner;
                menu.IsOpen = true;
                await ctx.SettleAsync();
                target = menu;
                close = () => menu.IsOpen = false;
            }
        }
        else
        {
            var path = what.Split('/');
            ItemsControl? parent = Find<Menu>(ctx.MainWindow, _ => true);
            var opened = new List<MenuItem>();
            foreach (var part in path)
            {
                if (parent?.Items.OfType<MenuItem>().FirstOrDefault(m => Plain(m.Header).Equals(part, StringComparison.OrdinalIgnoreCase)) is not { } item) { target = null; break; }
                item.IsSubmenuOpen = true;
                opened.Insert(0, item);
                await ctx.SettleAsync();
                target = item.Template?.FindName("PART_Popup", item) is Popup { Child: FrameworkElement child } ? child : null;
                parent = item;
            }
            close = () => { foreach (var item in opened) item.IsSubmenuOpen = false; };
        }
        if (target is null) { close(); throw new InvalidOperationException($"popup '{what}' not found"); }
        target.UpdateLayout();
        CaptureElement(target, png, ctx.MainWindow.Background);
        close();
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && match(hit)) return hit;
            if (Find(child, match) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Renders the window (or the topmost owned window) to a PNG.</summary>
    public static void Capture(Window window, string path)
    {
        var target = window.IsVisible && window.Owner is not null ? window
            : Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.IsVisible && w.Owner == window) ?? window;
        // The window's template root covers the whole client area (Content alone loses its margin).
        var content = VisualTreeHelper.GetChildrenCount(target) > 0 && VisualTreeHelper.GetChild(target, 0) is FrameworkElement root ? root : (FrameworkElement)target.Content;
        CaptureElement(content, path, target.Background);
    }

    /// <summary>Renders <paramref name="content"/> over <paramref name="background"/> to a PNG.</summary>
    public static void CaptureElement(FrameworkElement content, string path, Brush? background)
    {
        var dpi = VisualTreeHelper.GetDpi(content);
        // An absolute viewbox maps the element 1:1; the default bounding-box mapping stretched or cropped content that overhangs.
        var area = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.Width * dpi.DpiScaleX), (int)Math.Ceiling(area.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, area);
            dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area, Stretch = Stretch.Fill }, null, area);
        }
        bitmap.Render(visual);
        // A VisualBrush over a large tree that has not been brush-realized yet can render nothing (a fully transparent
        // capture, seen with RFA's batch-retarget window): render again, then fall back to rendering the element itself.
        for (var attempt = 0; attempt < 2 && IsBlank(bitmap); attempt++)
        {
            bitmap.Clear();
            if (attempt == 0) bitmap.Render(visual);
            else
            {
                var back = new DrawingVisual();
                using (var dc = back.RenderOpen()) dc.DrawRectangle(background, null, area);
                bitmap.Render(back);
                bitmap.Render(content);
            }
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>True when every pixel is fully transparent (nothing was drawn).</summary>
    private static bool IsBlank(RenderTargetBitmap bitmap)
    {
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return true;
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) return false;
        return true;
    }
}
