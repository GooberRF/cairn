using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;
using Cairn.Ui.Services;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Ui.List;

/// <summary>
/// The Info column's lines for one packfile document: computed on a worker thread from each entry's header
/// (<see cref="VppInfo"/>), cached by the entry's data source and name, and handed to the rows on the UI thread in
/// batches as they arrive. Rows of a new snapshot take cached lines at once; only entries whose data or name changed
/// (replace, add, rename) are read again. A new request cancels the previous one, and the worker reads the packfile
/// through one shared file handle instead of opening it per entry.
/// </summary>
public sealed class VppInfoCache(Dispatcher dispatcher) : IDisposable
{
    private readonly record struct Key(VppSource Source, string Name);

    private readonly ConcurrentDictionary<Key, VppInfoLine> _lines = new();
    private CancellationTokenSource? _cts;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;

    /// <summary>The time zone level save times are shown in (the local zone when null).</summary>
    public TimeZoneInfo? Zone { get; init; }

    /// <summary>The running fill: completed when every requested line was computed (or the fill was cancelled); the last batch reaches the rows on the UI thread shortly after.</summary>
    public Task Pending => _worker;

    /// <summary>True while lines are still being computed.</summary>
    public bool IsFilling => !_worker.IsCompleted;

    /// <summary>Raised on the UI thread after the last batch of a fill reached its rows.</summary>
    public event EventHandler? Filled;

    /// <summary>Lines computed so far (all documents' snapshots of this packfile), for diagnostics.</summary>
    public int CachedCount => _lines.Count;

    private static Key KeyOf(VppItem item) => new(item.Source, item.Name);

    /// <summary>
    /// Gives every row of a new snapshot its cached line and stops the previous fill; returns the rows still without
    /// one, for <see cref="Fill"/>. Call on the UI thread.
    /// </summary>
    public List<VppEntryRow> ApplyCached(IReadOnlyList<VppEntryRow> rows)
    {
        _cts?.Cancel();
        _cts = null;
        var missing = new List<VppEntryRow>();
        if (_disposed) return missing;
        foreach (var row in rows)
        {
            if (_lines.TryGetValue(KeyOf(row.Item), out var line)) row.Info = line;
            else missing.Add(row);
        }
        Prune(rows);
        return missing;
    }

    /// <summary>Computes the rows' lines in the background, in the order given (the rows on screen first). Call on the UI thread.</summary>
    public void Fill(IReadOnlyList<VppEntryRow> rows)
    {
        if (_disposed || rows.Count == 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var previous = _worker;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<VppEntryRow> work = [.. rows];
        // Below normal priority, so reading headers never competes with the UI thread for a busy CPU.
        var thread = new Thread(() =>
        {
            // VppInfo never throws; anything else must not take the app down from a worker thread: the rows keep an
            // empty Info cell.
            try { FillRows(work, previous, cts.Token); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine($"Info column fill failed: {ex}"); }
            finally { done.TrySetResult(); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Packfile Info column" };
        _worker = done.Task;
        thread.Start();
    }

    // Lines of entries no snapshot shows any more (removed, replaced, the packfile saved to new offsets) are
    // dropped once they outnumber the live ones; undoing back to them reads those few headers again.
    private void Prune(IReadOnlyList<VppEntryRow> rows)
    {
        if (_lines.Count <= rows.Count * 2 + 512) return;
        var live = rows.Select(r => KeyOf(r.Item)).ToHashSet();
        foreach (var key in _lines.Keys)
            if (!live.Contains(key)) _lines.TryRemove(key, out _);
    }

    private void FillRows(List<VppEntryRow> rows, Task previous, CancellationToken ct)
    {
        // One reader at a time: the cancelled fill stops at its next entry (fills never fault: see Fill).
        previous.Wait(CancellationToken.None);
        // Screenshots and self-tests wait for this (the last batch ends it on the UI thread).
        var busy = BusyTracker.Begin("packfile Info column");
        try
        {
            using var archives = new SharedArchives();
            var batch = new List<(VppEntryRow Row, VppInfoLine Line)>();
            var clock = Stopwatch.StartNew();
            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) { busy.Dispose(); return; }
                var key = KeyOf(row.Item);
                if (!_lines.TryGetValue(key, out var line))
                {
                    line = Compute(row.Item, archives);
                    _lines[key] = line;
                }
                batch.Add((row, line));
                if (clock.ElapsedMilliseconds >= 150 || batch.Count >= 400)
                {
                    Post(batch, ct, null);
                    batch = [];
                    clock.Restart();
                }
            }
            Post(batch, ct, busy);
        }
        catch
        {
            busy.Dispose();
            throw;
        }
    }

    private VppInfoLine Compute(VppItem item, SharedArchives archives)
    {
        Func<Stream> open = item.Source is ArchiveSource archive ? () => archives.Open(archive) : item.Source.Open;
        return VppInfo.Summarize(item.Name, open, item.Size, Zone);
    }

    /// <param name="batch">Rows and their lines.</param>
    /// <param name="ct">The fill's cancellation (a cancelled fill's rows are no longer shown).</param>
    /// <param name="last">The fill's busy mark, on its last batch: ended once that batch is applied.</param>
    private void Post(List<(VppEntryRow Row, VppInfoLine Line)> batch, CancellationToken ct, IDisposable? last)
    {
        if (last is null && (ct.IsCancellationRequested || batch.Count == 0)) return;
        dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (ct.IsCancellationRequested || _disposed) return;
                foreach (var (row, line) in batch) row.Info = line;
                if (last is not null) Filled?.Invoke(this, EventArgs.Empty);
            }
            finally { last?.Dispose(); }
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>One read-only handle per packfile, shared by the windows the worker reads entries through.</summary>
    private sealed class SharedArchives : IDisposable
    {
        private readonly Dictionary<string, FileStream> _files = new(StringComparer.OrdinalIgnoreCase);

        public Stream Open(ArchiveSource source)
        {
            if (!_files.TryGetValue(source.ArchivePath, out var file))
            {
                // FileShare.Delete, as ArchiveSource.Open: a save may replace the packfile while this reads it.
                file = new FileStream(source.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
                _files[source.ArchivePath] = file;
            }
            return new WindowStream(file, source.Offset, source.Length, ownsInner: false);
        }

        public void Dispose()
        {
            foreach (var file in _files.Values) file.Dispose();
            _files.Clear();
        }
    }
}
