using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Hosting, capture and leak checks for the read-only preview self-tests.</summary>
internal static class PreviewTestKit
{
    /// <summary>Where the captures go: <c>--preview-shots &lt;folder&gt;</c>, else a temp folder.</summary>
    public static string ShotsFolder(SelfTestContext ctx)
    {
        string dir = ctx.Options.TryGetValue("preview-shots", out var d) && !string.IsNullOrWhiteSpace(d) ? d : Path.Combine(Path.GetTempPath(), "cairn-preview-shots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>An off-screen, never-activated window to host a preview in (the caller closes it).</summary>
    public static Window Host(int width = 640, int height = 420)
    {
        var w = new Window { Width = width, Height = height, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Title = "Preview self-test" };
        w.SetResourceReference(Control.BackgroundProperty, "App.WindowBackground");
        w.Show();
        return w;
    }

    public static async Task IdleAsync(int times = 2)
    {
        for (int i = 0; i < times; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Waits for preview loads and texture decodes to finish (not other background work), then a few rendered frames.</summary>
    public static async Task SettleAsync(TimeSpan? limit = null)
    {
        var watch = Stopwatch.StartNew();
        var max = limit ?? TimeSpan.FromSeconds(15);
        // twice: the first rendered frames start the texture loads
        for (int pass = 0; pass < 2; pass++)
        {
            await Task.Delay(200);
            await IdleAsync();
            while (watch.Elapsed < max && BusyTracker.Describe().Any(d => d.Contains("preview", StringComparison.OrdinalIgnoreCase) || d.StartsWith("texture", StringComparison.OrdinalIgnoreCase)))
                await Task.Delay(30);
        }
        await Task.Delay(300);
        await IdleAsync();
    }

    /// <summary>Renders <paramref name="element"/> to a PNG; returns the number of distinct colours (1 = blank).</summary>
    public static int SavePng(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        int w = (int)Math.Max(1, element.ActualWidth), h = (int)Math.Max(1, element.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var pixels = new int[w * h];
        rtb.CopyPixels(pixels, w * 4, 0);
        return pixels.Distinct().Count();
    }

    /// <summary>True when the object behind <paramref name="weak"/> is collected after a few idle passes and full GCs.</summary>
    public static async Task<bool> CollectedAsync(WeakReference weak)
    {
        for (int i = 0; i < 6 && weak.IsAlive; i++)
        {
            await IdleAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (weak.IsAlive) await Task.Delay(50);
        }
        return !weak.IsAlive;
    }

    /// <summary>Managed memory after a full collection, in MB.</summary>
    public static double ManagedMegabytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true) / (1024.0 * 1024.0);
    }

    /// <summary>Two corrupt inputs: random bytes, and <paramref name="bytes"/> cut to its first 40 %.</summary>
    public static (string What, byte[] Bytes)[] Corrupt(byte[] bytes)
    {
        var garbage = new byte[700];
        new Random(12345).NextBytes(garbage);
        return [("random bytes", garbage), ("truncated", bytes.Take(bytes.Length * 2 / 5).ToArray())];
    }
}
