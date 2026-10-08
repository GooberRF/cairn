using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Cairn.Vpp.Ui.List;

namespace Cairn.Vpp.Ui.Documents;

/// <summary>
/// The packfile tab: toolbar (add, extract, remove, rename, filter, type filter), message bars (packfile changed on
/// disk, notices, changed work copies, running operation), the file list, and a right-hand area whose
/// <see cref="PreviewHost"/> and <see cref="DetailsHost"/> the preview/details panes fill.
/// </summary>
public sealed class VppDocumentView : Grid
{
    private readonly VppDocument _doc;
    private readonly Border _diskBar, _missingBar, _ps2Bar, _noticeBar, _workBar, _operationBar;
    private readonly TextBlock _ps2Text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _diskText;
    private const string DiskChangedText = "The packfile was changed on disk by another program. Its entries are read from that file, so reload it before saving.";
    private readonly TextBlock _noticeText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _workText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _operationText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Width = 220, Height = 10, Minimum = 0, Maximum = 1, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel;
    private readonly TextBox _filter = new() { Width = 200, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
    private readonly ToggleButton _typeButton = new() { Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
    private readonly TextBlock _filterSummary = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly FrameworkElement _toolbar;
    private VppOperation? _watchedOperation;

    public VppDocumentView(VppDocument doc)
    {
        _doc = doc;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _toolbar = BuildToolbar();
        Children.Add(_toolbar);

        var bars = new StackPanel();
        SetRow(bars, 1);
        _diskBar = Bar(false, _diskText = new TextBlock { Text = DiskChangedText, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
            ("Reload", "Read the packfile again (pending changes are lost)", () => doc.ReloadFromDisk()),
            ("Keep mine", "Hide this message (saving stays blocked until the packfile is reloaded)", () => doc.KeepMineCommand.Execute(null)));
        _missingBar = Bar(true, new TextBlock { Text = "The packfile was deleted or renamed on disk. Use File > Save As to write it somewhere.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
            ("Dismiss", "Hide this message", () => doc.DismissMissingCommand.Execute(null)));
        // PlayStation 2 content: a packfile from the PS2 version, or a .peg texture pack opened as a packfile
        // (PEG texture packs convert per selection: select .peg entries, then Convert to .tga... in the context menu or Packfile menu)
        _ps2Bar = Bar(false, _ps2Text, ("Dismiss", "Hide this message", () => doc.Ps2BannerDismissed = true));
        AutomationProperties.SetName(_ps2Bar, "PlayStation 2 banner");
        _noticeBar = Bar(false, _noticeText, ("Dismiss", "Hide this message", () => doc.Notice = null));
        _workBar = Bar(false, _workText,
            ("Update packfile", "Replace the entries with the edited work copies (one undoable change)", () => doc.Commands.UpdateFromWorkCopies()),
            ("Ignore", "Keep the packfile's entries; the bar appears again if the copies change", doc.Commands.IgnoreWorkChanges));
        _cancel = new Button { Content = "Cancel", MinWidth = 72, Command = null };
        _cancel.SetResourceReference(StyleProperty, "PushButton");
        _cancel.Click += (_, _) => _doc.Operation?.Cancel();
        var opPanel = new DockPanel();
        DockPanel.SetDock(_cancel, Dock.Right);
        DockPanel.SetDock(_progress, Dock.Right);
        opPanel.Children.Add(_cancel);
        opPanel.Children.Add(_progress);
        opPanel.Children.Add(_operationText);
        _operationBar = Bar(false, opPanel);
        foreach (var b in new[] { _diskBar, _missingBar, _ps2Bar, _noticeBar, _workBar, _operationBar }) bars.Children.Add(b);
        Children.Add(bars);

        var body = new Grid();
        SetRow(body, 2);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380), MinWidth = 200 });
        FileList = new VppFileListView(doc);
        body.Children.Add(FileList);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.SetResourceReference(StyleProperty, "VerticalSplitter");
        SetColumn(splitter, 1);
        body.Children.Add(splitter);
        var right = new Grid();
        right.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });
        PreviewHost = new ContentControl { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        DetailsHost = new ContentControl { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(PreviewHost, "Preview");
        AutomationProperties.SetName(DetailsHost, "Details");
        var hsplit = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        hsplit.SetResourceReference(StyleProperty, "HorizontalSplitter");
        SetRow(hsplit, 1);
        SetRow(DetailsHost, 2);
        right.Children.Add(PreviewHost);
        right.Children.Add(hsplit);
        right.Children.Add(DetailsHost);
        SetColumn(right, 2);
        body.Children.Add(right);
        Children.Add(body);

        AllowDrop = true;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        Drop += OnDrop;

        doc.PropertyChanged += OnDocumentChanged;
        doc.List.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(VppFileList.FilterSummary) or nameof(VppFileList.TypeFilterLabel)) UpdateFilterSummary(); };
        UpdateBars();
        UpdateFilterSummary();
        Module = doc.Module;
        doc.Module.OnViewCreated(doc, this);
        // a selection made before the view existed (diagnostic options, SelectNames) reaches the panes now
        if (doc.SelectedItems.Count > 0) doc.NotifySelectionChanged();
    }

    private VppModule Module { get; }
    /// <summary>The file list.</summary>
    public VppFileListView FileList { get; }
    /// <summary>Top of the right-hand area: the preview of the selected entry (filled by the preview pane).</summary>
    public ContentControl PreviewHost { get; }
    /// <summary>Bottom of the right-hand area: facts about the selected entry (filled by the details pane).</summary>
    public ContentControl DetailsHost { get; }
    /// <summary>The filter box (self-tests, focus).</summary>
    public TextBox FilterBox => _filter;
    /// <summary>The work-copy bar (self-tests).</summary>
    public bool IsWorkBarVisible => _workBar.Visibility == Visibility.Visible;
    /// <summary>The PlayStation 2 banner's text when it is shown, else null (self-tests).</summary>
    public string? Ps2BannerText => _ps2Bar.Visibility == Visibility.Visible ? _ps2Text.Text : null;
    /// <summary>The PlayStation 2 banner's buttons (self-tests: only Dismiss, conversion is per selected .peg entry).</summary>
    public IReadOnlyList<string> Ps2BannerButtons => [.. ((DockPanel)_ps2Bar.Child).Children.OfType<Button>().Select(b => b.Content as string ?? string.Empty)];

    private FrameworkElement BuildToolbar()
    {
        var bar = new DockPanel { Margin = new Thickness(6, 4, 6, 4), LastChildFill = false };
        var c = _doc.Commands;
        Button Tool(string text, string tip, Action action)
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(8, 3, 8, 3) };
            b.SetResourceReference(StyleProperty, "ToolButton");
            AutomationProperties.SetName(b, text);
            b.Click += (_, _) => action();
            DockPanel.SetDock(b, Dock.Left);
            bar.Children.Add(b);
            return b;
        }
        void Gap() { var s = new Border { Width = 1, Margin = new Thickness(6, 3, 6, 3) }; s.SetResourceReference(Border.BackgroundProperty, "App.SubtleBorder"); DockPanel.SetDock(s, Dock.Left); bar.Children.Add(s); }
        Tool("Add files...", "Add files to the packfile (or drop them on the list)", () => c.Fire(c.AddFilesAsync));
        Tool("Add folder...", "Add every file of a folder and its sub-folders (flattened)", () => c.Fire(c.AddFolderAsync));
        Gap();
        Tool("Extract...", "Write the selected entries to a folder (Ctrl+E); with nothing selected, every entry", () => c.Fire(_doc.SelectedItems.Count > 0 ? c.ExtractSelectedToAsync : c.ExtractAllToAsync));
        Tool("Remove", "Remove the selected entries (Del; undoable)", c.RemoveSelected);
        Tool("Rename", "Rename the selected entry (F2)", c.BeginRename);
        Gap();

        var filterHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _filter.ToolTip = "Show entries whose name contains this text; * and ? work as wildcards (*.tga, lev??.rfl)";
        AutomationProperties.SetName(_filter, "Filter entries by name");
        _filter.SetResourceReference(Control.BackgroundProperty, "App.PaneBackground");
        _filter.SetResourceReference(Control.ForegroundProperty, "App.Text");
        _filter.SetResourceReference(Control.BorderBrushProperty, "App.Border");
        var placeholder = new TextBlock { Text = "Filter (name, *.tga)", Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _filter.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _doc.List.FilterText = _filter.Text;
        };
        _filter.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && _filter.Text.Length > 0) { _filter.Text = string.Empty; e.Handled = true; } };
        filterHost.Children.Add(_filter);
        filterHost.Children.Add(placeholder);
        DockPanel.SetDock(filterHost, Dock.Left);
        bar.Children.Add(filterHost);

        _typeButton.ToolTip = "Show only some types (check several)";
        AutomationProperties.SetName(_typeButton, "Type filter");
        _typeButton.SetResourceReference(StyleProperty, "ToolToggle");
        var options = new ItemsControl { ItemTemplate = Application.Current?.TryFindResource("Vpp.TypeOptionTemplate") as DataTemplate };
        var all = new Button { Content = "Show all types", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        all.SetResourceReference(StyleProperty, "LinkButton");
        all.Click += (_, _) => _doc.List.ShowOnlyTypes([]);
        var popupBody = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(all, Dock.Top);
        popupBody.Children.Add(all);
        popupBody.Children.Add(new ScrollViewer { Content = options, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var popupBorder = new Border { Child = popupBody, BorderThickness = new Thickness(1), MinWidth = 220 };
        popupBorder.SetResourceReference(Border.BackgroundProperty, "App.PaneBackground");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "App.Border");
        var popup = new Popup { Child = popupBorder, PlacementTarget = _typeButton, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = false };
        popup.SetBinding(Popup.IsOpenProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = _typeButton, Mode = BindingMode.TwoWay });
        popup.Opened += (_, _) => { options.ItemsSource = null; options.ItemsSource = _doc.List.TypeOptions; };
        DockPanel.SetDock(_typeButton, Dock.Left);
        bar.Children.Add(_typeButton);
        bar.Children.Add(popup);
        _filterSummary.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        DockPanel.SetDock(_filterSummary, Dock.Left);
        bar.Children.Add(_filterSummary);
        var border = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BackgroundProperty, "App.ChromeBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        return border;
    }

    private Border Bar(bool error, UIElement content, params (string Text, string Tip, Action Action)[] buttons)
    {
        var panel = new DockPanel { Margin = new Thickness(10, 6, 8, 6) };
        foreach (var (text, tip, action) in buttons.Reverse())
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(6, 0, 0, 0), MinWidth = 72 };
            b.SetResourceReference(StyleProperty, "PushButton");
            b.Click += (_, _) => action();
            DockPanel.SetDock(b, Dock.Right);
            panel.Children.Add(b);
        }
        if (content is TextBlock tb) tb.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        panel.Children.Add(content);
        var border = new Border { Child = panel, BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };
        border.SetResourceReference(Border.BackgroundProperty, error ? "Banner.ErrorBackground" : "Banner.InfoBackground");
        border.SetResourceReference(Border.BorderBrushProperty, error ? "Banner.ErrorBorder" : "Banner.InfoBorder");
        return border;
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VppDocument.HasExternalChange) or nameof(VppDocument.IsMissingOnDisk) or nameof(VppDocument.Notice)
            or nameof(VppDocument.WorkChanges) or nameof(VppDocument.Operation) or nameof(VppDocument.IsBusy)
            or nameof(VppDocument.PegSource) or nameof(VppDocument.IsPs2Packfile) or nameof(VppDocument.PegEntryCount) or nameof(VppDocument.Ps2BannerDismissed))
            UpdateBars();
    }

    private void UpdateBars()
    {
        static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        _diskBar.Visibility = Show(_doc.HasExternalChange);
        _diskText.Text = _doc.NeedsReload
            ? "The packfile was saved, but the saved file could not be read back. Nothing is read from it until it is reloaded."
            : DiskChangedText;
        _missingBar.Visibility = Show(_doc.IsMissingOnDisk);
        _ps2Text.Text = _doc.PegSource is { } peg ? Cairn.Vpp.Ps2.PegConverter.BannerFor(peg) : Cairn.Vpp.Ps2.Ps2Packfiles.BannerFor(_doc.PegEntryCount);
        _ps2Bar.Visibility = Show(!_doc.Ps2BannerDismissed && (_doc.PegSource is not null || _doc.IsPs2Packfile));
        _noticeText.Text = _doc.Notice ?? string.Empty;
        _noticeBar.Visibility = Show(_doc.Notice is not null);
        var changes = _doc.WorkChanges;
        _workText.Text = changes.Count == 0 ? string.Empty
            : (changes.Count == 1 ? "A work copy was changed outside the packfile: " : $"{changes.Count} work copies were changed outside the packfile: ")
              + string.Join(", ", changes.Take(6).Select(c => c.EntryName)) + (changes.Count > 6 ? ", ..." : string.Empty);
        _workBar.Visibility = Show(changes.Count > 0);

        if (!ReferenceEquals(_watchedOperation, _doc.Operation))
        {
            if (_watchedOperation is not null) _watchedOperation.Progressed -= OnProgress;
            _watchedOperation = _doc.Operation;
            if (_watchedOperation is not null) _watchedOperation.Progressed += OnProgress;
        }
        _operationBar.Visibility = Show(_doc.Operation is not null);
        _toolbar.IsEnabled = FileList.IsEnabled = !_doc.IsBusy;
        OnProgress(null, EventArgs.Empty);
    }

    private void OnProgress(object? sender, EventArgs e)
    {
        if (_doc.Operation is not { } op) return;
        _operationText.Text = op.Detail.Length > 0 ? $"{op.Title}: {op.Detail}" : op.Title + "...";
        _progress.IsIndeterminate = op.IsIndeterminate;
        _progress.Value = op.Fraction;
        _cancel.IsEnabled = !op.IsCancelled;
    }

    private void UpdateFilterSummary()
    {
        _typeButton.Content = _doc.List.TypeFilterLabel + "  ▾";
        _filterSummary.Text = _doc.List.FilterSummary;
    }

    // ---- drop in ---------------------------------------------------------------------------------------------

    private bool AcceptsDrop(DragEventArgs e) => !FileList.IsDraggingOut && !_doc.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = AcceptsDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!AcceptsDrop(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        _doc.Commands.Fire(() => _doc.Commands.AddPathsAsync(paths));
    }
}
