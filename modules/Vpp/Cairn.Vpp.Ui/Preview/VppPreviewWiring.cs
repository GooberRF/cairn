using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cairn.Previews;
using Cairn.Ui.Modules;
using Cairn.Vpp.Ui.Documents;

namespace Cairn.Vpp.Ui.Preview;

/// <summary>
/// Connects a packfile tab to its preview and details panes: fills the view's hosts, follows the
/// document's selection and changes, remembers the split between the two hosts, and releases everything
/// (stopping playback) when the document closes.
/// </summary>
internal sealed class VppPreviewWiring
{
    private readonly VppDocument _doc;
    private readonly VppDocumentView _view;
    private readonly IShellContext _shell;
    private readonly ModuleSettings _settings;
    private readonly GridSplitter? _splitter;
    private readonly PreviewRowSizer? _sizer;
    private VppPreviewController? _controller;

    private VppPreviewWiring(VppDocument doc, VppDocumentView view)
    {
        _doc = doc;
        _view = view;
        _shell = doc.Shell;
        _settings = new ModuleSettings(_shell.Settings, "vpp");
        _controller = new VppPreviewController(_shell);
        _controller.Preview.OpenInCairnRequested += OnOpenInCairn;
        view.PreviewHost.Content = _controller.Preview;
        view.DetailsHost.Content = _controller.Details;

        if (view.PreviewHost.Parent is Grid grid && grid.RowDefinitions.Count == 3)
        {
            double split = Math.Clamp(_settings.Get(VppPreviewArea.SplitKey, 0.55), 0.1, 0.9);
            grid.RowDefinitions[0].Height = new GridLength(split, GridUnitType.Star);
            grid.RowDefinitions[2].Height = new GridLength(1 - split, GridUnitType.Star);
            _splitter = grid.Children.OfType<GridSplitter>().FirstOrDefault();
            if (_splitter is not null) _splitter.DragCompleted += OnSplitterMoved;
            _sizer = new PreviewRowSizer(_controller.Preview, grid.RowDefinitions[0], _splitter);
        }

        doc.SelectionChanged += OnSelectionChanged;
        doc.PropertyChanged += OnDocumentChanged;
        _shell.DocumentsChanged += OnDocumentsChanged;
        Refresh(immediate: true);
    }

    /// <summary>Wires <paramref name="view"/> (called once per view).</summary>
    public static void Attach(VppDocument doc, VppDocumentView view) => _ = new VppPreviewWiring(doc, view);

    private void Refresh(bool immediate = false)
    {
        var selection = _doc.SelectedItems;
        _controller?.Show(selection.Count > 0 ? selection[0] : null, selection, _doc.Current, _doc.Problems, immediate);
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => Refresh();

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The package changed (an edit, undo, reload): the problems are recomputed with it.
        if (e.PropertyName == nameof(VppDocument.Problems)) Refresh();
    }

    private void OnOpenInCairn(object? sender, Model.VppItem item)
    {
        if (_doc.Commands.CanOpenInCairn) _ = _doc.Commands.OpenInCairnAsync();
    }

    private void OnSplitterMoved(object sender, DragCompletedEventArgs e)
    {
        if (_view.PreviewHost.Parent is not Grid grid) return;
        double top = grid.RowDefinitions[0].ActualHeight, bottom = grid.RowDefinitions[2].ActualHeight;
        if (top + bottom <= 0) return;
        _settings.Set(VppPreviewArea.SplitKey, Math.Clamp(top / (top + bottom), 0.1, 0.9));
    }

    private void OnDocumentsChanged(object? sender, EventArgs e)
    {
        if (_shell.Documents.Contains(_doc)) return;
        // The tab closed: stop playback and loads and let the document, view and panes go.
        _shell.DocumentsChanged -= OnDocumentsChanged;
        _doc.SelectionChanged -= OnSelectionChanged;
        _doc.PropertyChanged -= OnDocumentChanged;
        if (_splitter is not null) _splitter.DragCompleted -= OnSplitterMoved;
        _sizer?.Detach();
        if (_controller is not null)
        {
            _controller.Preview.OpenInCairnRequested -= OnOpenInCairn;
            _controller.Dispose();
            _controller = null;
        }
        _view.PreviewHost.Content = null;
        _view.DetailsHost.Content = null;
    }
}
