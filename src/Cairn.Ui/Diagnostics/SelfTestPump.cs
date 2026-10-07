using System.Windows.Threading;

namespace Cairn.Ui.Diagnostics;

/// <summary>
/// Synchronous pumping for self-tests that cannot await: runs the UI dispatcher in a nested frame for a while.
/// The normal exit is a Background-priority operation queued after <c>ms</c>, so layout, render, bindings and
/// Loaded work queued before it run first (as the old DispatcherTimer(Background) pumps did). Background work and
/// WM_TIMER are both deferred by Windows/WPF while input is reported pending, so the frame is also ended at
/// Send priority after a hard cap: a stalled queue can no longer hold a test (and the whole run) for minutes.
/// <see cref="AbortAll"/> ends every running pump; the self-test runner calls it when a test passes its limit.
/// </summary>
public static class SelfTestPump
{
    /// <summary>How long after its interval a pump waits for its Background exit before ending anyway.</summary>
    public static TimeSpan HardCapExtra { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly List<DispatcherFrame> Running = [];

    /// <summary>Pumps for <paramref name="ms"/>; false when the hard cap or <see cref="AbortAll"/> ended it.</summary>
    public static bool Pump(int ms)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        bool normal = false;
        lock (Running) Running.Add(frame);
        using var soft = new System.Threading.Timer(_ => dispatcher.InvokeAsync(() => { normal = true; frame.Continue = false; }, DispatcherPriority.Background),
            null, Math.Max(0, ms), Timeout.Infinite);
        using var hard = new System.Threading.Timer(_ => End(frame), null, Math.Max(0, ms) + (int)HardCapExtra.TotalMilliseconds, Timeout.Infinite);
        try { Dispatcher.PushFrame(frame); }
        finally { lock (Running) Running.Remove(frame); }
        if (!normal)
            Console.WriteLine($"PUMP: a {ms} ms pump ended without its Background exit (queue stalled for {HardCapExtra.TotalSeconds:0} s, or aborted)");
        return normal;
    }

    /// <summary>Ends every pump running now (from any thread).</summary>
    public static void AbortAll()
    {
        lock (Running) foreach (var frame in Running) End(frame);
    }

    private static void End(DispatcherFrame frame) =>
        frame.Dispatcher.InvokeAsync(() => frame.Continue = false, DispatcherPriority.Send);
}
