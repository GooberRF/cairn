using System.Windows.Threading;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Details;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Ui.Preview;

/// <summary>
/// Drives the preview and details panes from a selection: changes are debounced so arrowing through the
/// list only loads where the user stops, and both panes load off the UI thread.
/// </summary>
public sealed class VppPreviewController : IDisposable
{
    /// <summary>Delay after the last selection change before loading.</summary>
    public static readonly TimeSpan Debounce = Cairn.Previews.PreviewDebouncer.Default;

    private readonly DispatcherTimer _debounce;
    private (VppItem? Primary, IReadOnlyList<VppItem> Selection, VppPackage? Package, IReadOnlyList<VppProblem>? Problems)? _pending;
    private bool _disposed;

    public VppPreviewController(IShellContext? shell)
    {
        Preview = new VppPreviewPane(shell);
        Details = new VppDetailsPane(shell);
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = Debounce };
        _debounce.Tick += OnDebounce;
    }

    public VppPreviewPane Preview { get; }
    public VppDetailsPane Details { get; }

    /// <summary>Shows a new selection after the debounce delay (or at once with <paramref name="immediate"/>).</summary>
    /// <param name="primary">The entry to preview (the focused or first selected one).</param>
    /// <param name="selection">Every selected entry.</param>
    /// <param name="package">The packfile as it is now (with pending changes).</param>
    /// <param name="problems">The packfile's current problems when the caller has them.</param>
    /// <param name="immediate">Skip the debounce (first show, tests).</param>
    public void Show(VppItem? primary, IReadOnlyList<VppItem> selection, VppPackage? package, IReadOnlyList<VppProblem>? problems = null, bool immediate = false)
    {
        if (_disposed) return;
        _pending = (primary, selection, package, problems);
        _debounce.Stop();
        if (immediate) Flush();
        else _debounce.Start();
    }

    /// <summary>Loads the waiting selection now.</summary>
    public void Flush()
    {
        _debounce.Stop();
        if (_pending is not { } p || _disposed) return;
        _pending = null;
        Preview.Show(p.Primary, p.Selection, p.Package);
        Details.Show(p.Primary, p.Selection, p.Package, p.Problems);
    }

    /// <summary>Waits for the debounce and both panes' loads (for tests).</summary>
    public async Task SettleAsync()
    {
        while (_debounce.IsEnabled) await Task.Delay(20);
        await Task.WhenAll(Preview.Pending, Details.Pending);
    }

    private void OnDebounce(object? sender, EventArgs e) => Flush();

    /// <summary>Stops loads, playback and animation and releases the views.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        _debounce.Tick -= OnDebounce;
        _pending = null;
        Preview.Dispose();
        Details.Dispose();
    }
}
