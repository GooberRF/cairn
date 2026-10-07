using Cairn.Ui.Mvvm;

namespace Cairn.Vpp.Ui.Documents;

/// <summary>
/// A long-running packfile operation (save, extract, add) shown in the document's progress bar and the status bar,
/// with a Cancel button. Progress may be reported from any thread.
/// </summary>
public sealed class VppOperation : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private string _detail = string.Empty;
    private double _fraction;
    private bool _isIndeterminate = true;
    private long _lastRaiseTicks;

    public VppOperation(string title, System.Windows.Threading.Dispatcher dispatcher)
    {
        Title = title;
        _dispatcher = dispatcher;
        CancelCommand = new RelayCommand(Cancel, () => !_cts.IsCancellationRequested);
    }

    public string Title { get; }
    public CancellationToken Token => _cts.Token;
    public RelayCommand CancelCommand { get; }
    public bool IsCancelled => _cts.IsCancellationRequested;
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    /// <summary>"Saving x.vpp: 45 %".</summary>
    public string StatusText => IsIndeterminate ? Title + "..." : $"{Title}: {Fraction * 100:0} %";
    /// <summary>Raised (UI thread, at most 10 times a second) when the progress text changed.</summary>
    public event EventHandler? Progressed;

    public void Cancel()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        Detail = "Cancelling...";
        CancelCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Reports progress from any thread; updates are throttled to keep the UI thread free.</summary>
    public void Report(double? fraction, string detail)
    {
        long now = Environment.TickCount64;
        bool final = fraction is >= 1;
        if (!final && now - Interlocked.Read(ref _lastRaiseTicks) < 100) return;
        Interlocked.Exchange(ref _lastRaiseTicks, now);
        void Apply()
        {
            if (_cts.IsCancellationRequested) return;
            IsIndeterminate = fraction is null;
            Fraction = Math.Clamp(fraction ?? 0, 0, 1);
            Detail = detail;
            Raise(nameof(StatusText));
            Progressed?.Invoke(this, EventArgs.Empty);
        }
        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.BeginInvoke(Apply);
    }
}
