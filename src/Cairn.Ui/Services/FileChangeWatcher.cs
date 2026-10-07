using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Cairn.Ui.Services;

/// <summary>
/// A debounced <see cref="FileSystemWatcher"/> that raises its event on the UI thread. Editors and
/// exporters write files in bursts (temp file, rename, attribute change), so a raw watcher would
/// fire several times for one save; everything here collapses into a single notification.
/// </summary>
public sealed class FileChangeWatcher : IDisposable
{
    /// <summary>How long to wait before trying to re-establish a watcher that failed.</summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
    ];

    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _retry;
    private FileSystemWatcher? _watcher;
    private string _filter = "*.*";
    private int _retryAttempt;
    private bool _disposed;

    /// <param name="dispatcher">The UI dispatcher the event is raised on.</param>
    /// <param name="debounceMs">How long to wait for the burst to settle.</param>
    public FileChangeWatcher(Dispatcher dispatcher, int debounceMs = 300)
    {
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(debounceMs, 20, 5000)),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
        };

        // A watcher whose buffer overflows, or whose folder goes away, is dead for good: the OS
        // stops delivering to it and nothing ever restarts it. Without this, one burst of activity
        // silently ends external-change detection for the rest of the session.
        _retry = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _retry.Tick += (_, _) => Reestablish();
    }

    /// <summary>Raised once per burst of file-system activity, on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// How long notifications stay suppressed after a suspension is lifted. The OS delivers watcher
    /// events on its own thread well after the write returns, so a flag that flips back the instant
    /// the save finishes would still let the app's own save through as an "external change".
    /// </summary>
    private static readonly TimeSpan SuspendGrace = TimeSpan.FromMilliseconds(750);

    private bool _suspended;
    private DateTime _suspendedUntilUtc = DateTime.MinValue;

    /// <summary>True while notifications are suppressed, e.g. during the app's own save.</summary>
    public bool IsSuspended
    {
        get => _suspended || DateTime.UtcNow < _suspendedUntilUtc;
        set
        {
            if (value)
            {
                _suspended = true;
            }
            else if (_suspended)
            {
                _suspended = false;
                _suspendedUntilUtc = DateTime.UtcNow + SuspendGrace;
            }
        }
    }

    /// <summary>The folder currently being watched, or null.</summary>
    public string? Folder { get; private set; }

    /// <summary>
    /// Points the watcher at a folder. Pass null to stop watching. A folder that cannot be watched
    /// (a removable drive, a UNC path with no permission) simply yields no events.
    /// </summary>
    /// <param name="folder">Folder to watch.</param>
    /// <param name="filter">File filter, e.g. a single file name or <c>*.*</c>.</param>
    public void Watch(string? folder, string filter = "*.*")
    {
        Stop();
        Folder = folder;
        _filter = string.IsNullOrEmpty(filter) ? "*.*" : filter;
        _retryAttempt = 0;
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!TryStart()) ScheduleRetry();
    }

    private bool TryStart()
    {
        if (_disposed || string.IsNullOrWhiteSpace(Folder)) return false;
        try
        {
            if (!Directory.Exists(Folder)) return false;
            var watcher = new FileSystemWatcher(Folder, _filter)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                // The default 8 KB buffer overflows on a folder an exporter is writing a hundred
                // images into, and an overflow is what kills the watcher.
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += OnEvent;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _watcher = null;
            return false;
        }
    }

    /// <summary>
    /// The watcher died — buffer overflow, or the folder was removed or went offline. Tear it down
    /// and try again with a backoff, and tell the owner once, because whatever we missed during the
    /// gap has to be re-checked.
    /// </summary>
    private void OnError(object sender, ErrorEventArgs e)
    {
        if (_disposed) return;
        Post(() =>
        {
            if (_disposed) return;
            StopWatcher();
            _retryAttempt = 0;
            ScheduleRetry();
            if (!IsSuspended) Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void ScheduleRetry()
    {
        if (_disposed || string.IsNullOrWhiteSpace(Folder)) return;
        _retry.Interval = RetryDelays[Math.Min(_retryAttempt, RetryDelays.Length - 1)];
        _retry.Stop();
        _retry.Start();
    }

    private void Reestablish()
    {
        _retry.Stop();
        if (_disposed || _watcher is not null || string.IsNullOrWhiteSpace(Folder)) return;
        if (TryStart())
        {
            _retryAttempt = 0;
            // The folder came back; whatever happened while it was gone has to be re-read.
            if (!IsSuspended) Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        _retryAttempt++;
        ScheduleRetry();
    }

    /// <summary>Stops watching without disposing the object.</summary>
    public void Stop()
    {
        _debounce.Stop();
        _retry.Stop();
        StopWatcher();
    }

    private void StopWatcher()
    {
        if (_watcher is null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _watcher = null;
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed || IsSuspended) return;
        // Restarting the timer on every event is what collapses a burst into one notification.
        Post(() =>
        {
            if (_disposed || IsSuspended) return;
            _debounce.Stop();
            _debounce.Start();
        });
    }

    /// <summary>
    /// Hops to the UI thread, quietly doing nothing once the dispatcher is shutting down: watcher
    /// notifications arrive on an OS thread and can land after the app has begun to close, where
    /// BeginInvoke throws.
    /// </summary>
    private void Post(Action action)
    {
        var dispatcher = _debounce.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        try { dispatcher.BeginInvoke(action); }
        catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
