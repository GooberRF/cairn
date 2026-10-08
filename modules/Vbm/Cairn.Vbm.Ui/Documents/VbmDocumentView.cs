using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Formats.Imaging;
using Cairn.Previews;
using Cairn.Ui.Controls;

namespace Cairn.Vbm.Ui.Documents;

/// <summary>
/// The bitmap document's view: a toolbar (playback, frame stepping, zoom, alpha and checkerboard, mip level), the big
/// preview, the facts on the right (with the editable frame rate and the module's actions) and the frame strip along
/// the bottom. Built in code like the shared previews it borrows from (<see cref="PreviewUi"/>, <see cref="ImageData"/>).
/// </summary>
public sealed class VbmDocumentView : UserControl
{
    private readonly VbmDocument _doc;
    private readonly ScrollViewer _scroll;
    private readonly Border _canvas;
    private readonly Image _image;
    private readonly TextBlock _zoomText, _frameText;
    private readonly Button _play;
    private readonly ToggleButton _alpha, _checker;
    private readonly ComboBox _mip;
    private readonly ListBox _strip;
    private readonly Grid _factsGrid = new();
    private readonly NumericBox _fps;
    private readonly TextBlock _selectionText;
    private readonly ContentControl _center = new() { Focusable = false };
    private readonly FrameworkElement _previewArea;
    private readonly Border _diskBar, _missingBar;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<(VbmFrame Frame, int Level, bool Alpha), BitmapSource> _cache = [];
    private readonly ToggleButton _smooth, _pixels;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly System.Windows.Shapes.Rectangle _insertLine = new() { Width = 3, Visibility = Visibility.Collapsed };
    private List<VbmFrameItem> _items = [];
    private VbmFile? _builtFrom;

    /// <summary>The strip's items (for self-tests: kept when an edit leaves every frame as it was).</summary>
    internal IReadOnlyList<VbmFrameItem> StripItems => _items;
    private VbmFrameItem? _marked;
    private bool _fit = true;
    private double _scale = 1;
    private bool _syncing;
    private VbmScaling _scaling = VbmScaling.Auto;
    private Point _press;
    private bool _dragCandidate;
    private VbmFrameItem? _pendingSingle;

    /// <summary>The data format of frames dragged inside Cairn (a <see cref="VbmFrameDrag"/>).</summary>
    public const string DragFormat = "Cairn.VbmFrames.Drag";

    public VbmDocumentView(VbmDocument document)
    {
        _doc = document;
        Focusable = true;
        AutomationProperties.SetName(this, document.DisplayName);
        SetResourceReference(BackgroundProperty, "App.WindowBackground");

        // preview
        _image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        AutomationProperties.SetName(_image, "Bitmap preview");
        _canvas = new Border { Child = _image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _canvas.SetResourceReference(Border.BackgroundProperty, "Preview.CheckerBrush");
        _scroll = new ScrollViewer
        {
            Content = _canvas, Focusable = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _scroll.SetResourceReference(BackgroundProperty, "Preview.Background");
        _scroll.SizeChanged += (_, _) => { if (_fit) ApplyScale(); };
        _scroll.PreviewMouseWheel += OnWheel;
        _previewArea = _scroll;

        // toolbar
        _play = PreviewUi.Button("Play", "Play or pause the animation (Space)", (_, _) => _doc.IsPlaying = !_doc.IsPlaying);
        _play.MinWidth = 48;
        _frameText = PreviewUi.Secondary("");
        _zoomText = PreviewUi.Secondary("");
        _alpha = PreviewUi.Toggle("Alpha", "Show the alpha channel only (white = opaque)", (_, _) => ShowFrame());
        _checker = PreviewUi.Toggle("Checkerboard", "Show a checkerboard behind transparent pixels (off: a plain background)", (_, _) => UpdateBackground());
        _checker.IsChecked = true;
        _mip = new ComboBox { MinWidth = 150, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Mip level to show" };
        AutomationProperties.SetName(_mip, "Mip level");
        _mip.SelectionChanged += (_, _) => { if (!_syncing) ShowFrame(); };
        _smooth = PreviewUi.Toggle("Smooth", "Smooth scaling: blend the pixels when zoomed (until chosen, zoom above 100% shows pixels and 100% or less is smooth)", (_, _) => { });
        _pixels = PreviewUi.Toggle("Pixels", "Pixel scaling: show each pixel as a sharp square when zoomed (until chosen, zoom above 100% shows pixels and 100% or less is smooth)", (_, _) => { });
        _smooth.Click += (_, _) => SetScaling(VbmScaling.Smooth);
        _pixels.Click += (_, _) => SetScaling(VbmScaling.Pixels);
        var toolbar = PreviewUi.Toolbar(
            _play,
            PreviewUi.Button("|<", "First frame", (_, _) => Go(0)),
            PreviewUi.Button("<", "Previous frame (comma)", (_, _) => Step(-1)),
            PreviewUi.Button(">", "Next frame (period)", (_, _) => Step(1)),
            PreviewUi.Button(">|", "Last frame", (_, _) => Go(_doc.Current.FrameCount - 1)),
            _frameText,
            PreviewUi.Divider(),
            PreviewUi.Button("Fit", "Fit the bitmap to the pane", (_, _) => { _fit = true; ApplyScale(); }),
            PreviewUi.Button("100%", "Show the bitmap at its own size (one pixel per screen pixel)", (_, _) => SetScale(1)),
            _zoomText,
            _smooth,
            _pixels,
            PreviewUi.Divider(),
            _alpha,
            _checker,
            PreviewUi.Divider(),
            _mip);

        // facts
        _fps = new NumericBox { Minimum = 0, Maximum = VbmEditing.MaxFps, Step = 1, Decimals = 0, Suffix = " fps", Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(_fps, "Frame rate");
        _fps.ToolTip = "Frames per second the game plays the animation at";
        _fps.InteractionStarted += (_, _) => { if (!_doc.IsReadOnly) _doc.BeginEdit("Change frame rate"); };
        _fps.InteractionEnded += (_, _) => _doc.CommitEdit();
        _fps.ValueChanged += (_, _) => OnFpsChanged();
        _selectionText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        _selectionText.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        var facts = BuildFacts();

        // frame strip
        _strip = new ListBox
        {
            SelectionMode = SelectionMode.Extended, Height = 112, BorderThickness = new Thickness(0, 1, 0, 0),
            Focusable = true,
        };
        _strip.SetResourceReference(ItemsControl.ItemTemplateProperty, "Vbm.FrameTemplate");
        _strip.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "PaneListBoxItem");
        _strip.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        _strip.SetResourceReference(BorderBrushProperty, "App.SubtleBorder");
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        panel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);
        _strip.ItemsPanel = new ItemsPanelTemplate(panel);
        VirtualizingPanel.SetIsVirtualizing(_strip, true);
        VirtualizingPanel.SetVirtualizationMode(_strip, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_strip, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_strip, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_strip, "Frames");
        AutomationProperties.SetHelpText(_strip, "Drag frames to reorder them; drop image files here to add them as frames");
        _strip.SelectionChanged += OnStripSelection;
        _strip.AllowDrop = true;
        _strip.PreviewMouseLeftButtonDown += OnStripMouseDown;
        _strip.PreviewMouseLeftButtonUp += (_, _) => OnStripMouseUp();
        _strip.PreviewMouseMove += OnStripMouseMove;
        _strip.DragEnter += OnStripDragOver;
        _strip.DragOver += OnStripDragOver;
        _strip.DragLeave += (_, _) => HideInsertLine();
        _strip.Drop += OnStripDrop;
        var m = _doc.Module;
        var menu = new ContextMenu();
        foreach (var (header, command, gesture) in new (string?, ICommand?, string?)[]
        {
            ("_Replace Frame...", m.ReplaceFrameCommand, "Ctrl+R"), ("_Add Frames...", m.AddFramesCommand, "Insert"),
            ("_Duplicate", m.DuplicateFramesCommand, "Ctrl+D"), ("Re_move", m.RemoveFramesCommand, "Del"),
            (null, null, null),
            ("_Copy", m.CopyFramesCommand, "Ctrl+C"), ("_Paste", m.PasteFramesCommand, "Ctrl+V"),
            (null, null, null),
            ("Move _Earlier", m.MoveEarlierCommand, "Alt+Left"), ("Move _Later", m.MoveLaterCommand, "Alt+Right"),
            ("E_xport Frames...", m.ExportFramesCommand, "Ctrl+Shift+E"),
        })
        {
            if (header is null) menu.Items.Add(new Separator());
            else menu.Items.Add(new MenuItem { Header = header, Command = command, InputGestureText = gesture });
        }
        _strip.ContextMenu = menu;
        _insertLine.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "App.Accent");
        _overlay.Children.Add(_insertLine);
        var stripHost = new Grid { Children = { _strip, _overlay } };

        // banners
        _diskBar = Banner("This file changed on disk.", ("Reload", "Replace the content with the file on disk (undoable)", () => _doc.Reload()),
            ("Keep mine", "Keep the content shown here", () => _doc.KeepMineCommand.Execute(null)));
        _missingBar = Banner("This file was deleted or renamed on disk. Save to write it again.",
            ("Dismiss", "Hide this message", () => _doc.DismissMissingCommand.Execute(null)));

        var right = new Border { Width = 280, BorderThickness = new Thickness(1, 0, 0, 0), Child = facts };
        right.SetResourceReference(Border.BackgroundProperty, "App.PaneBackground");
        right.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");

        var dock = new DockPanel();
        foreach (var bar in new FrameworkElement[] { _diskBar, _missingBar, toolbar }) { DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar); }
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        DockPanel.SetDock(stripHost, Dock.Bottom);
        dock.Children.Add(stripHost);
        dock.Children.Add(_center);
        Content = dock;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / 15) };
        _timer.Tick += (_, _) => { if (_doc.IsPlaying) _doc.CurrentFrame = (_doc.CurrentFrame + 1) % _doc.Current.FrameCount; };
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        PreviewKeyDown += OnKey;

        _doc.PropertyChanged += OnDocumentChanged;
        _doc.ContentChanged += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>The zoom factor (for tests).</summary>
    public double Scale => _scale;

    /// <summary>The frame strip (for tests).</summary>
    public ListBox Strip => _strip;

    /// <summary>The frame shown in the preview right now (for tests).</summary>
    public int ShownFrame { get; private set; } = -1;

    /// <summary>Shows the alpha channel only (diagnostic option and tests).</summary>
    public void SetAlphaOnly(bool on) => _alpha.IsChecked = on;

    /// <summary>Shows mip <paramref name="level"/> (diagnostic option and tests).</summary>
    public void SetMipLevel(int level) => _mip.SelectedIndex = Math.Clamp(level, 0, Math.Max(0, _mip.Items.Count - 1));

    /// <summary>Zooms to <paramref name="scale"/> (1 = 100 %), or fits with 0.</summary>
    public void SetZoom(double scale)
    {
        if (scale <= 0) { _fit = true; ApplyScale(); }
        else SetScale(scale);
    }

    /// <summary>The scaling chosen with the Smooth / Pixels buttons (Auto until one is clicked).</summary>
    public VbmScaling Scaling => _scaling;

    /// <summary>What the preview uses now: Smooth or Pixels (Auto resolved by the zoom).</summary>
    public VbmScaling EffectiveScaling { get; private set; } = VbmScaling.Smooth;

    /// <summary>Chooses the preview scaling (the buttons, the diagnostic option and tests); Auto follows the zoom again.</summary>
    public void SetScaling(VbmScaling scaling)
    {
        _scaling = scaling;
        ApplyScale();
    }

    /// <summary>The frame the strip marks as the one shown (playing), or -1 (tests).</summary>
    public int MarkedFrame => _marked?.Index ?? -1;

    /// <summary>The frame's strip item (tests).</summary>
    internal VbmFrameItem? ItemAt(int index) => index >= 0 && index < _items.Count ? _items[index] : null;

    // ── Layout pieces ────────────────────────────────────────────────────────────────────────────────────────

    private FrameworkElement BuildFacts()
    {
        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
        stack.Children.Add(Header("BITMAP"));
        _factsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _factsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stack.Children.Add(_factsGrid);

        stack.Children.Add(Header("FRAME RATE"));
        stack.Children.Add(_fps);

        stack.Children.Add(Header("SELECTION"));
        stack.Children.Add(_selectionText);

        stack.Children.Add(Header("ACTIONS"));
        foreach (var (text, tip, command) in new (string, string, ICommand)[]
        {
            ("Convert to ATX...", "Turn this bitmap into one image per frame plus an animated texture (.atx)", _doc.Module.ConvertToAtxCommand),
            ("Export frames...", "Write the frames as TGA or PNG images", _doc.Module.ExportFramesCommand),
            ("Replace frame...", "Replace the selected frame with an image", _doc.Module.ReplaceFrameCommand),
            ("Add frames...", "Add images as new frames after the selection", _doc.Module.AddFramesCommand),
        })
        {
            var link = new Button { Content = text, ToolTip = tip, Command = command, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 3, 0, 3) };
            link.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetName(link, text.TrimEnd('.'));
            stack.Children.Add(link);
        }
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    }

    private static TextBlock Header(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 4) };
        t.SetResourceReference(StyleProperty, "PaneHeaderText");
        return t;
    }

    private static Border Banner(string text, params (string Label, string Tip, Action Act)[] actions)
    {
        var panel = new DockPanel { Margin = new Thickness(10, 4, 6, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, tip, act) in actions)
        {
            var b = new Button { Content = label, ToolTip = tip, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
            b.SetResourceReference(StyleProperty, "PushButton");
            b.Click += (_, _) => act();
            buttons.Children.Add(b);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        panel.Children.Add(buttons);
        var message = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        message.SetResourceReference(TextBlock.ForegroundProperty, "Banner.Foreground");
        panel.Children.Add(message);
        var border = new Border { Child = panel, BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };
        border.SetResourceReference(Border.BackgroundProperty, "Banner.NoticeBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "Banner.NoticeBorder");
        return border;
    }

    // ── Content ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything that depends on the snapshot: the strip, the facts, the mip list, the frame shown.</summary>
    private void Rebuild()
    {
        var file = _doc.Current;
        // decoded frames that are still in the file stay cached (an edit keeps untouched frames' data)
        var live = new HashSet<VbmFrame>(file.Frames, ReferenceEqualityComparer.Instance);
        foreach (var key in _cache.Keys.Where(k => !live.Contains(k.Frame)).ToList()) _cache.Remove(key);

        _syncing = true;
        try
        {
            // An edit that keeps every frame (a frame rate spin, step by step) keeps the strip and its decoded thumbnails.
            var old = _builtFrom;
            bool sameFrames = old is not null && !_doc.IsBroken && old.Width == file.Width && old.Height == file.Height && old.Format == file.Format
                && old.Version == file.Version && old.FrameCount == file.FrameCount && Enumerable.Range(0, file.FrameCount).All(i => ReferenceEquals(old.Frames[i], file.Frames[i]));
            if (!sameFrames)
            {
                _items = [.. Enumerable.Range(0, file.FrameCount).Select(i => new VbmFrameItem(file, i))];
                _strip.ItemsSource = _doc.IsBroken ? null : _items;
            }
            _builtFrom = _doc.IsBroken ? null : file;
            SyncStripSelection();

            int mip = Math.Max(0, _mip.SelectedIndex);
            _mip.Items.Clear();
            for (int l = 0; l < file.MipLevels; l++)
            {
                var (w, h) = file.LevelSize(l);
                _mip.Items.Add(string.Format(CultureInfo.CurrentCulture, "Mip {0}: {1} x {2}", l, w, h));
            }
            _mip.SelectedIndex = Math.Min(mip, file.MipLevels - 1);
            _mip.IsEnabled = file.MipLevels > 1;
            _mip.Visibility = file.MipLevels > 1 ? Visibility.Visible : Visibility.Collapsed;

            _fps.Value = file.Fps;
            _fps.IsReadOnly = _doc.IsReadOnly;
        }
        finally { _syncing = false; }

        FillFacts();
        _center.Content = _doc.IsBroken
            ? PreviewUi.Message("This file cannot be shown.", _doc.Problems.FirstOrDefault()?.Message, null, warning: true)
            : _previewArea;
        _alpha.IsEnabled = file.HasAlpha && !_doc.IsBroken;
        if (!_alpha.IsEnabled) _alpha.IsChecked = false;
        _play.IsEnabled = file.IsAnimated && !_doc.IsBroken;
        UpdateBanners();
        UpdatePlayButton();
        UpdateTimer();
        ShowFrame();
    }

    private void FillFacts()
    {
        _factsGrid.Children.Clear();
        _factsGrid.RowDefinitions.Clear();
        IReadOnlyList<Cairn.Vbm.VbmFact> rows = _doc.IsBroken
            ? [new("File", "Not readable", _doc.Problems.FirstOrDefault()?.Message)]
            : VbmFacts.For(_doc.Current, _doc.FileSize);
        foreach (var fact in rows)
        {
            if (fact.Label == "Frame rate") continue; // edited in its own box below
            int row = _factsGrid.RowDefinitions.Count;
            _factsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = fact.Label, Margin = new Thickness(0, 2, 10, 2), VerticalAlignment = VerticalAlignment.Top };
            label.SetResourceReference(StyleProperty, "FactLabel");
            var value = new TextBox { Text = fact.Value, ToolTip = fact.ToolTip };
            value.SetResourceReference(StyleProperty, "FactValue");
            AutomationProperties.SetName(value, fact.Label);
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            _factsGrid.Children.Add(label);
            _factsGrid.Children.Add(value);
        }
        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        var sel = _doc.SelectedFrames;
        _selectionText.Text = _doc.IsBroken ? "Nothing to select." : sel.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, "Frame {0} of {1}.", sel[0] + 1, _doc.Current.FrameCount)
            : string.Format(CultureInfo.CurrentCulture, "{0} frames selected ({1}).", sel.Count, string.Join(", ", sel.Take(8).Select(i => (i + 1).ToString(CultureInfo.CurrentCulture))) + (sel.Count > 8 ? ", ..." : ""));
    }

    private void UpdateBanners()
    {
        _diskBar.Visibility = _doc.HasExternalChange ? Visibility.Visible : Visibility.Collapsed;
        _missingBar.Visibility = _doc.IsMissingOnDisk ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VbmDocument.CurrentFrame):
                ShowFrame();
                break;
            case nameof(VbmDocument.IsPlaying):
                UpdatePlayButton();
                UpdateTimer();
                break;
            case nameof(VbmDocument.SelectedFrames):
                SyncStripSelection();
                UpdateSelectionText();
                break;
            case nameof(VbmDocument.HasExternalChange) or nameof(VbmDocument.IsMissingOnDisk):
                UpdateBanners();
                break;
            case nameof(VbmDocument.FilePath) or nameof(VbmDocument.FileSize):
                FillFacts();
                break;
        }
    }

    private void UpdatePlayButton() => _play.Content = _doc.IsPlaying ? "Pause" : "Play";

    private void UpdateTimer()
    {
        int fps = _doc.Current.Fps is > 0 and <= 120 ? _doc.Current.Fps : _doc.Current.Fps > 120 ? 120 : 15;
        _timer.Interval = TimeSpan.FromSeconds(1.0 / fps);
        if (_doc.IsPlaying && IsLoaded) _timer.Start();
        else _timer.Stop();
    }

    private void OnFpsChanged()
    {
        if (_syncing || _doc.IsReadOnly) return;
        int fps = (int)Math.Round(_fps.Value);
        if (_doc.History.IsCoalescing) _doc.UpdateEdit(f => VbmEditing.WithFps(f, Math.Clamp(fps, 0, VbmEditing.MaxFps)));
        else _doc.SetFps(fps);
    }

    // ── Strip ────────────────────────────────────────────────────────────────────────────────────────────────

    private void SyncStripSelection()
    {
        if (_strip.ItemsSource is null) return;
        bool was = _syncing;
        _syncing = true;
        try
        {
            var want = _doc.SelectedFrames.Where(i => i < _items.Count).Select(i => _items[i]).ToList();
            if (!want.SequenceEqual(_strip.SelectedItems.Cast<VbmFrameItem>().OrderBy(i => i.Index)))
            {
                _strip.SelectedItems.Clear();
                foreach (var item in want) _strip.SelectedItems.Add(item);
            }
            if (want.Count > 0) _strip.ScrollIntoView(want[0]);
        }
        finally { _syncing = was; }
    }

    private void OnStripSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var picked = _strip.SelectedItems.Cast<VbmFrameItem>().Select(i => i.Index).Order().ToList();
        if (picked.Count == 0) return;
        _doc.IsPlaying = false;
        if (e.AddedItems.Count > 0 && e.AddedItems[^1] is VbmFrameItem added) _doc.CurrentFrame = added.Index;
        else if (!picked.Contains(_doc.CurrentFrame)) _doc.CurrentFrame = picked[0];
        _syncing = true;
        try { _doc.SelectedFrames = picked; }
        finally { _syncing = false; }
        UpdateSelectionText();
    }

    // ── Drag and drop ────────────────────────────────────────────────────────────────────────────────────────

    private static ListBoxItem? ContainerOf(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        return source as ListBoxItem;
    }

    private void OnStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _press = e.GetPosition(_strip);
        var container = ContainerOf(e.OriginalSource as DependencyObject);
        _dragCandidate = container is not null;
        _pendingSingle = null;
        // An extended-selection list collapses a multi-selection to the clicked item on mouse down, so dragging several
        // frames would only ever move one: hold the collapse back until mouse up, when no drag started.
        if (container is { IsSelected: true, DataContext: VbmFrameItem item } && _strip.SelectedItems.Count > 1
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            _pendingSingle = item;
            e.Handled = true;
            if (!_strip.IsKeyboardFocusWithin) _strip.Focus();
        }
    }

    private void OnStripMouseUp()
    {
        _dragCandidate = false;
        if (_pendingSingle is { } item)
        {
            _pendingSingle = null;
            _doc.IsPlaying = false;
            _doc.CurrentFrame = item.Index;
            _doc.SelectedFrames = [item.Index];
        }
    }

    private void OnStripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(_strip);
        if (Math.Abs(now.X - _press.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _press.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = false;
        _pendingSingle = null;
        if (_doc.IsBroken) return;
        _doc.IsPlaying = false;
        var data = new DataObject(DragFormat, new VbmFrameDrag(_doc, [.. _doc.SelectedFrames]));
        try { DragDrop.DoDragDrop(_strip, data, _doc.IsReadOnly ? DragDropEffects.Copy : DragDropEffects.Move | DragDropEffects.Copy); }
        finally { HideInsertLine(); }
    }

    /// <summary>What a drop of <paramref name="data"/> would do here: move frames, copy them in, or nothing.</summary>
    private DragDropEffects EffectFor(IDataObject data)
    {
        if (_doc.IsReadOnly) return DragDropEffects.None;
        if (data.GetDataPresent(DragFormat) && data.GetData(DragFormat) is VbmFrameDrag drag)
            return ReferenceEquals(drag.Source, _doc) ? DragDropEffects.Move : DragDropEffects.Copy;
        return ImageFiles(data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnStripDragOver(object sender, DragEventArgs e)
    {
        e.Effects = EffectFor(e.Data);
        if (e.Effects == DragDropEffects.None) HideInsertLine();
        else ShowInsertLine(InsertionIndex(e.GetPosition(_strip)));
        e.Handled = true;
    }

    private async void OnStripDrop(object sender, DragEventArgs e)
    {
        HideInsertLine();
        if (EffectFor(e.Data) == DragDropEffects.None) return;
        e.Handled = true; // not the main window's "open the dropped files"
        int index = InsertionIndex(e.GetPosition(_strip));
        try { await DropAsync(e.Data, index); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageDecodeException or InvalidOperationException)
        {
            _doc.Module.ShellContext.Dialogs.ShowError("The frames could not be added", ex.Message);
        }
    }

    /// <summary>
    /// A drop of <paramref name="data"/> before frame <paramref name="index"/>: frames dragged in this strip move (one undo
    /// step), frames from another bitmap's strip are copied in, image files are added as frames. Also the self-tests' way in.
    /// </summary>
    internal async Task<bool> DropAsync(IDataObject data, int index)
    {
        if (_doc.IsReadOnly) return false;
        index = Math.Clamp(index, 0, _doc.Current.FrameCount);
        if (data.GetDataPresent(DragFormat) && data.GetData(DragFormat) is VbmFrameDrag drag)
        {
            if (ReferenceEquals(drag.Source, _doc)) return _doc.MoveFramesTo(drag.Indices, index);
            return _doc.Module.InsertFramesFrom(_doc, index, drag.Source.Current, drag.Indices, "Copy frames from " + drag.Source.DisplayName);
        }
        var files = ImageFiles(data);
        return files.Count > 0 && await _doc.Module.InsertImageFilesAsync(_doc, files, index);
    }

    /// <summary>The dropped files frames can come from (images and bitmaps), in order.</summary>
    private static IReadOnlyList<string> ImageFiles(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths ? [.. paths.Where(VbmImages.IsImageFile)] : [];

    /// <summary>Where a drop at <paramref name="point"/> inserts: before the first shown frame whose middle is right of it.</summary>
    private int InsertionIndex(Point point)
    {
        int last = -1;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_strip.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container || !container.IsVisible) continue;
            last = i;
            double middle = container.TranslatePoint(new Point(0, 0), _strip).X + container.ActualWidth / 2;
            if (point.X < middle) return i;
        }
        return last >= 0 ? last + 1 : _items.Count;
    }

    private void ShowInsertLine(int index)
    {
        double x = 0;
        int at = Math.Min(index, _items.Count - 1);
        if (at >= 0 && _strip.ItemContainerGenerator.ContainerFromIndex(at) is ListBoxItem container)
        {
            double left = container.TranslatePoint(new Point(0, 0), _strip).X;
            x = index > at ? left + container.ActualWidth : left;
        }
        _insertLine.Height = Math.Max(0, _strip.ActualHeight - 20);
        Canvas.SetLeft(_insertLine, Math.Clamp(x - 1.5, 0, Math.Max(0, _strip.ActualWidth - 3)));
        Canvas.SetTop(_insertLine, 4);
        _insertLine.Visibility = Visibility.Visible;
    }

    private void HideInsertLine() => _insertLine.Visibility = Visibility.Collapsed;

    // ── Preview ──────────────────────────────────────────────────────────────────────────────────────────────

    private void Go(int frame)
    {
        _doc.IsPlaying = false;
        _doc.CurrentFrame = frame;
        _doc.SelectedFrames = [_doc.CurrentFrame];
    }

    /// <summary>Steps <paramref name="delta"/> frames (wrapping), pausing playback.</summary>
    public void Step(int delta)
    {
        int n = _doc.Current.FrameCount;
        Go(((_doc.CurrentFrame + delta) % n + n) % n);
    }

    private void ShowFrame()
    {
        _frameText.Text = _doc.FrameText + (_doc.Current.IsAnimated ? string.Format(CultureInfo.CurrentCulture, " at {0} fps", _doc.Current.Fps) : "");
        if (_doc.IsBroken) { _image.Source = null; ShownFrame = -1; return; }
        var file = _doc.Current;
        int frame = Math.Clamp(_doc.CurrentFrame, 0, file.FrameCount - 1);
        int level = Math.Clamp(_mip.SelectedIndex, 0, file.MipLevels - 1);
        bool alphaOnly = _alpha.IsChecked == true;
        var key = (file.Frames[frame], level, alphaOnly);
        if (!_cache.TryGetValue(key, out var bitmap))
        {
            try
            {
                bitmap = ImageData.ToBitmap(file.Decode(frame, level), alphaOnly);
                _cache[key] = bitmap;
            }
            catch (ImageDecodeException) { bitmap = null; }
        }
        _image.Source = bitmap;
        ShownFrame = frame;
        MarkCurrent(frame);
        ApplyScale();
    }

    /// <summary>Moves the strip's play mark to <paramref name="frame"/>.</summary>
    private void MarkCurrent(int frame)
    {
        var item = frame >= 0 && frame < _items.Count && _strip.ItemsSource is not null ? _items[frame] : null;
        if (ReferenceEquals(item, _marked)) return;
        if (_marked is not null) _marked.IsCurrent = false;
        _marked = item;
        if (item is not null) item.IsCurrent = true;
    }

    private void UpdateBackground()
    {
        if (_checker.IsChecked == true) _canvas.SetResourceReference(Border.BackgroundProperty, "Preview.CheckerBrush");
        else _canvas.SetResourceReference(Border.BackgroundProperty, "Preview.Background");
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        SetScale(_scale * (e.Delta > 0 ? 1.25 : 1 / 1.25));
        e.Handled = true;
    }

    private void SetScale(double scale)
    {
        _fit = false;
        _scale = Math.Clamp(scale, 0.02, 64);
        ApplyScale();
    }

    private void ApplyScale()
    {
        // the mip level is shown at the size level 0 would have, so stepping through levels shows the loss of detail
        double w = _doc.Current.Width, h = _doc.Current.Height;
        if (_fit)
        {
            double aw = Math.Max(1, _scroll.ActualWidth - 8), ah = Math.Max(1, _scroll.ActualHeight - 8);
            _scale = _scroll.ActualWidth > 0 ? Math.Max(0.01, Math.Min(aw / w, ah / h)) : 1;
        }
        _canvas.Width = Math.Max(1, Math.Round(w * _scale));
        _canvas.Height = Math.Max(1, Math.Round(h * _scale));
        // Auto as the animated textures preview does it: pixels above 100 %, smooth at 100 % and below. The zoom is that
        // of the source pixels, so a small mip level shown at level 0's size counts as magnified.
        double pixel = _image.Source is BitmapSource b && b.PixelWidth > 0 ? _scale * w / b.PixelWidth : _scale;
        EffectiveScaling = _scaling != VbmScaling.Auto ? _scaling : pixel > 1.001 ? VbmScaling.Pixels : VbmScaling.Smooth;
        RenderOptions.SetBitmapScalingMode(_image, EffectiveScaling == VbmScaling.Pixels ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _smooth.IsChecked = EffectiveScaling == VbmScaling.Smooth;
        _pixels.IsChecked = EffectiveScaling == VbmScaling.Pixels;
        _zoomText.Text = string.Format(CultureInfo.CurrentCulture, "{0:0}%{1}", _scale * 100, _fit ? " (fit)" : "");
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (e.Key is Key.Add or Key.OemPlus && Keyboard.Modifiers == ModifierKeys.None) { SetScale(_scale * 1.25); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus && Keyboard.Modifiers == ModifierKeys.None) { SetScale(_scale / 1.25); e.Handled = true; }
    }
}

/// <summary>How the preview scales the bitmap: Auto (pixels above 100 %, smooth otherwise), or as chosen.</summary>
public enum VbmScaling
{
    Auto,
    Smooth,
    Pixels,
}

/// <summary>Frames dragged from a bitmap's strip: the document and the frames (0-based, in order).</summary>
public sealed record VbmFrameDrag(VbmDocument Source, IReadOnlyList<int> Indices);
