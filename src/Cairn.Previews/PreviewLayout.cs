using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Cairn.Previews;

/// <summary>
/// Runs the last posted action once nothing new was posted for <see cref="Delay"/>, so moving through a list (or
/// a caret through text) only loads a preview where the user stops. UI thread only.
/// </summary>
public sealed class PreviewDebouncer : IDisposable
{
    /// <summary>The delay the packfile list uses.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherTimer _timer;
    private Action? _pending;
    private bool _disposed;

    public PreviewDebouncer(TimeSpan delay)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        _timer.Tick += OnTick;
    }

    /// <summary>The delay after the last post.</summary>
    public TimeSpan Delay => _timer.Interval;

    /// <summary>True while an action waits for the delay.</summary>
    public bool IsPending => _timer.IsEnabled;

    /// <summary>Replaces the waiting action and restarts the delay.</summary>
    public void Post(Action action)
    {
        if (_disposed) return;
        _pending = action;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Runs the waiting action now.</summary>
    public void Flush()
    {
        _timer.Stop();
        if (_pending is not { } action || _disposed) return;
        _pending = null;
        action();
    }

    /// <summary>Drops the waiting action.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _pending = null;
    }

    private void OnTick(object? sender, EventArgs e) => Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _pending = null;
    }
}

/// <summary>
/// Shrinks the preview's row to its content (one line of text) while the preview is compact (levels),
/// hiding the splitter, and restores the row afterwards.
/// </summary>
public sealed class PreviewRowSizer
{
    private readonly AssetPreviewPane _pane;
    private readonly RowDefinition _row;
    private readonly UIElement? _splitter;
    private GridLength _savedHeight;
    private double _savedMin;
    private bool _compact;

    public PreviewRowSizer(AssetPreviewPane pane, RowDefinition row, UIElement? splitter)
    {
        _pane = pane;
        _row = row;
        _splitter = splitter;
        pane.KindChanged += OnKindChanged;
    }

    /// <summary>Stops following the pane.</summary>
    public void Detach() => _pane.KindChanged -= OnKindChanged;

    private void OnKindChanged(object? sender, EventArgs e)
    {
        bool compact = _pane.IsCompact;
        if (compact == _compact) return;
        _compact = compact;
        if (compact)
        {
            _savedHeight = _row.Height;
            _savedMin = _row.MinHeight;
            _row.MinHeight = 0;
            _row.Height = GridLength.Auto;
        }
        else
        {
            _row.Height = _savedHeight;
            _row.MinHeight = _savedMin;
        }
        if (_splitter is not null) _splitter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
}
