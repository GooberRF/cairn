using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cairn.Previews;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Details;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Ui.Preview;

/// <summary>
/// The preview above the details with a splitter between them, as one element (self-tests and hosts
/// without their own layout; the packfile tab lays the two panes out itself). The split is remembered
/// in the "vpp." settings.
/// </summary>
public sealed class VppPreviewArea : Grid, IDisposable
{
    /// <summary>Settings key (under "vpp.") of the preview's share of the height.</summary>
    public const string SplitKey = "previewSplit";

    private readonly ModuleSettings? _settings;
    private readonly VppPreviewController _controller;
    private readonly RowDefinition _previewRow;
    private readonly RowDefinition _detailsRow;
    private bool _disposed;

    public VppPreviewArea(IShellContext? shell)
    {
        _settings = shell is null ? null : new ModuleSettings(shell.Settings, "vpp");
        _controller = new VppPreviewController(shell);
        AutomationProperties.SetName(Preview, "Preview");

        double split = Math.Clamp(_settings?.Get(SplitKey, 0.55) ?? 0.55, 0.1, 0.9);
        _previewRow = new RowDefinition { Height = new GridLength(split, GridUnitType.Star), MinHeight = 60 };
        _detailsRow = new RowDefinition { Height = new GridLength(1 - split, GridUnitType.Star), MinHeight = 60 };
        RowDefinitions.Add(_previewRow);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(_detailsRow);

        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ToolTip = "Drag to share the space between the preview and the details" };
        splitter.SetResourceReference(StyleProperty, "HorizontalSplitter");
        AutomationProperties.SetName(splitter, "Preview and details splitter");
        splitter.DragCompleted += OnSplitterMoved;
        SetRow(Preview, 0);
        SetRow(splitter, 1);
        SetRow(Details, 2);
        Children.Add(Preview);
        Children.Add(splitter);
        Children.Add(Details);
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        _sizer = new PreviewRowSizer(Preview, _previewRow, splitter);
    }

    private readonly PreviewRowSizer _sizer;

    public VppPreviewPane Preview => _controller.Preview;
    public VppDetailsPane Details => _controller.Details;

    /// <inheritdoc cref="VppPreviewController.Show"/>
    public void Show(VppItem? primary, IReadOnlyList<VppItem> selection, VppPackage? package, IReadOnlyList<VppProblem>? problems = null, bool immediate = false) =>
        _controller.Show(primary, selection, package, problems, immediate);

    /// <inheritdoc cref="VppPreviewController.SettleAsync"/>
    public Task SettleAsync() => _controller.SettleAsync();

    private void OnSplitterMoved(object sender, DragCompletedEventArgs e)
    {
        double total = _previewRow.ActualHeight + _detailsRow.ActualHeight;
        if (total <= 0) return;
        double split = Math.Clamp(_previewRow.ActualHeight / total, 0.1, 0.9);
        _previewRow.Height = new GridLength(split, GridUnitType.Star);
        _detailsRow.Height = new GridLength(1 - split, GridUnitType.Star);
        _settings?.Set(SplitKey, split);
    }

    /// <summary>Stops loads, playback and animation and releases the views.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sizer.Detach();
        _controller.Dispose();
    }
}
