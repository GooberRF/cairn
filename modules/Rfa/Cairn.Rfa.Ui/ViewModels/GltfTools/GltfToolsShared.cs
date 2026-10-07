using System.Globalization;
using System.Windows.Threading;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Formats;
using Cairn.Formats.Gltf;

namespace Cairn.Rfa.Ui.ViewModels.GltfTools;

/// <summary>
/// A debounced, cancellable recompute that runs off the UI thread (the glTF dialogs' previews): every
/// <see cref="Schedule"/> restarts a short timer; when it fires, <c>prepare</c> captures the parameters
/// on the UI thread and returns the work, which runs on the thread pool; only the newest result is
/// delivered (on the UI thread). Refusals (<see cref="ArgumentException"/>, <see cref="AssetFormatException"/>…)
/// arrive through <c>failed</c> as plain messages. Tracked by <see cref="BusyTracker"/> from the moment
/// it is scheduled, so diagnostic runs wait for the preview.
/// </summary>
internal sealed class BackgroundRecompute<T> where T : class
{
    private readonly DispatcherTimer _timer;
    private readonly string _what;
    private readonly Func<Func<CancellationToken, T>> _prepare;
    private readonly Action<T> _succeeded;
    private readonly Action<string> _failed;
    private CancellationTokenSource? _cts;
    private int _generation;
    private IDisposable? _busy;
    private bool _stopped;
    private bool _pending;

    public BackgroundRecompute(Dispatcher dispatcher, string what, Func<Func<CancellationToken, T>> prepare, Action<T> succeeded, Action<string> failed, int delayMs = 180)
    {
        _what = what;
        _prepare = prepare;
        _succeeded = succeeded;
        _failed = failed;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Start();
        };
    }

    /// <summary>True while a recompute is scheduled or running.</summary>
    public bool IsPending => _pending;

    /// <summary>Raised on the UI thread when <see cref="IsPending"/> changes.</summary>
    public event EventHandler? PendingChanged;

    /// <summary>Recomputes after the debounce delay (restarting it).</summary>
    public void Schedule()
    {
        if (_stopped) return;
        _busy ??= BusyTracker.Begin(_what);
        SetPending(true);
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Recomputes now, skipping the debounce.</summary>
    public void RunNow()
    {
        if (_stopped) return;
        _busy ??= BusyTracker.Begin(_what);
        _timer.Stop();
        Start();
    }

    /// <summary>Waits (without blocking the UI thread) until nothing is scheduled or running.</summary>
    public async Task<bool> SettleAsync(int timeoutMs = 120_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_pending && !_stopped)
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(15).ConfigureAwait(true);
        }
        return true;
    }

    /// <summary>Cancels everything for good (the dialog closed).</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _timer.Stop();
        _cts?.Cancel();
        SetPending(false);
        _busy?.Dispose();
        _busy = null;
    }

    private async void Start()
    {
        if (_stopped) return;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        int generation = ++_generation;
        SetPending(true);

        Func<CancellationToken, T> work;
        try
        {
            work = _prepare();
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            Finish(generation, null, GltfText.UserMessage(ex));
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write(_what, ex);
            Finish(generation, null, GltfText.Unexpected(ex));
            return;
        }

        try
        {
            var token = cts.Token;
            var result = await Task.Run(() => work(token), token).ConfigureAwait(true);
            Finish(generation, result, null);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer recompute (which reports for itself), or stopped.
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            Finish(generation, null, GltfText.UserMessage(ex));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write(_what, ex);
            Finish(generation, null, GltfText.Unexpected(ex));
        }
    }

    private void Finish(int generation, T? result, string? error)
    {
        if (generation != _generation || _stopped) return;
        // A newer change is waiting for its debounce: this result is already stale.
        if (_timer.IsEnabled) return;
        SetPending(false);
        _busy?.Dispose();
        _busy = null;
        if (error is not null) _failed(error);
        else _succeeded(result!);
    }

    private void SetPending(bool value)
    {
        if (_pending == value) return;
        _pending = value;
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Text helpers shared by the glTF dialogs.</summary>
internal static class GltfText
{
    /// <summary>The file filter for glTF pickers.</summary>
    public const string OpenFilter = "glTF 2.0 (*.gltf;*.glb)|*.gltf;*.glb|All files (*.*)|*.*";

    /// <summary>True for an exception that is a refusal or a bad input, not a bug.</summary>
    public static bool IsRefusal(Exception ex)
    {
        if (ex is OperationCanceledException || !RfaWorkspace.IsReadFailure(ex)) return false;
        // A file the Core turns down for a reason it did not foresee (a reader bug) is still a refusal to
        // report in plain words, not a crash; it is logged so it can be fixed.
        if (ex is not (ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
            or AssetFormatException or ArithmeticException or NotSupportedException))
            Cairn.Ui.Services.ErrorLog.Write("glTF", ex);
        return true;
    }

    /// <summary>An exception's message without the " (Parameter 'x')" suffix.</summary>
    public static string UserMessage(Exception ex)
    {
        string message = ex.Message;
        if (ex is ArgumentException { ParamName: { } p })
        {
            int at = message.LastIndexOf($" (Parameter '{p}')", StringComparison.Ordinal);
            if (at > 0) message = message[..at];
        }
        return message;
    }

    /// <summary>What to say about a bug (the details go to the error log).</summary>
    public static string Unexpected(Exception ex) =>
        $"Something failed unexpectedly ({ex.GetType().Name}: {ex.Message}). Details were written to the error log.";

    /// <summary>"3,000" in the current culture.</summary>
    public static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>"1 bone" / "3 bones".</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        $"{N(n)} {(n == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>A name usable as a file name (invalid characters become '_').</summary>
    public static string SafeFileName(string name, string fallback = "imported")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string result = new string(chars).Trim('.', ' ');
        return result.Length == 0 ? fallback : result;
    }

    /// <summary>Reads a glTF file off the UI thread (tracked, so diagnostic runs wait for it).</summary>
    public static async Task<GltfDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var busy = BusyTracker.Begin("read " + Path.GetFileName(path));
        return await Task.Run(() => GltfReader.ReadFile(path), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Frames a dialog preview's mesh once layout has settled (a mesh set before the viewport has its final size frames too far or too near).</summary>
    public static void FrameSoon(ViewportPreviewHost host) =>
        host.Shell.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (host.Scene.Mesh is not null) host.Scene.RequestFrame(false);
        }));

    /// <summary>The time span an animation's samplers cover, in seconds (from the input accessors' min/max).</summary>
    public static double AnimationSeconds(GltfDocument doc, GltfAnimation animation)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var sampler in animation.Samplers)
        {
            if (sampler.Input < 0 || sampler.Input >= doc.Accessors.Count) continue;
            var accessor = doc.Accessors[sampler.Input];
            if (accessor.Min is { Length: > 0 } lo) min = Math.Min(min, lo[0]);
            if (accessor.Max is { Length: > 0 } hi) max = Math.Max(max, hi[0]);
        }
        return max >= min && max != double.MinValue ? max - (min == double.MaxValue ? 0 : min) : 0;
    }
}

/// <summary>A pickable row with a check box (an animation, a clip, a LOD).</summary>
public abstract class CheckRow : ObservableObject
{
    private bool _isChecked;

    /// <summary>Raised when <see cref="IsChecked"/> changes.</summary>
    public event EventHandler? CheckedChanged;

    /// <summary>Whether the row is included.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!Set(ref _isChecked, value)) return;
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The text shown.</summary>
    public abstract string Label { get; }
}
