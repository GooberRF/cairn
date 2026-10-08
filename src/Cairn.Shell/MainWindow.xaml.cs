using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>Main window: menus, toolbar, panes and shortcuts assembled from the shell and the modules.</summary>
public partial class MainWindow : Window
{
    private ShellViewModel _shell = null!;
    private readonly List<(Control Item, Func<Cairn.Ui.Documents.IDocument?, bool> Visible)> _conditional = [];
    private MenuItem _recentMenu = null!;

    public MainWindow()
    {
        InitializeComponent();
        // remember a dragged pane size at once, not only when the window closes
        LeftSplitter.DragCompleted += (_, _) => { if (_shell is not null) SaveLayout(); };
        BottomSplitter.DragCompleted += (_, _) => { if (_shell is not null) SaveLayout(); };
    }

    public void Attach(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;
        Shortcuts = new ShortcutRouter(shell);
        Shortcuts.Attach(this);
        BuildMenu();
        foreach (var t in shell.Modules.SelectMany(m => m.ToolbarItems).OrderBy(t => t.Order))
        {
            ToolbarPanel.Children.Add(t.Element);
            if (t.IsVisibleFor is { } v) _conditional.Add((new ContentControl { Tag = t.Element }, v));
        }
        _moduleToolbarItems = [.. shell.Modules.SelectMany(m => m.ToolbarItems).Select(t => t.Element)];
        RestoreLayout(shell.Settings);
        shell.ActiveDocumentChanged += (_, _) => OnActiveChanged();
        shell.ContributionsInvalidated += (_, _) => RefreshContributions(activated: false);
        BottomTabs.SelectionChanged += OnPaneTabChanged;
        LeftTabs.SelectionChanged += OnPaneTabChanged;
        Drop += OnDrop;
        shell.PaneToggleRequested += (_, key) => { if (key == "left") TogglePane("left", LeftPane); else TogglePane("bottom", BottomPane); };
        DocumentTabs.PreviewMouseUp += OnTabStripMiddleClick;
        Closing += OnClosing;
        SizeChanged += OnWindowSizeChanged;
        OnActiveChanged();
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---- menus ----

    private void BuildMenu()
    {
        var contributions = _shell.Modules.SelectMany(m => m.Menus).OrderBy(c => c.Order).ToList();
        IEnumerable<Control> Slot(MenuSlot slot) => contributions.Where(c => c.Slot == slot).Select(Track);

        var newMenu = Menu("_New", [.. _shell.Kinds.Where(k => k.CanCreateNew).Select(k => Item(k.DisplayName + "…", _shell.NewCommand, null, k)), .. Slot(MenuSlot.FileNew)]);
        newMenu.Visibility = newMenu.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _recentMenu = Menu("Open _Recent", []);
        _recentMenu.SubmenuOpened += (_, _) => FillRecent();
        var import = Menu("_Import", [.. Slot(MenuSlot.FileImport)]);
        var export = Menu("_Export", [.. Slot(MenuSlot.FileExport)]);
        import.Visibility = import.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        export.Visibility = export.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FillRecent();

        MainMenu.Items.Add(Menu("_File",
        [
            newMenu, Item("_Open…", _shell.OpenCommand, "Ctrl+O"), _recentMenu, new Separator(), import, export, new Separator(),
            Item("_Save", _shell.SaveCommand, "Ctrl+S"), Item("Save _As…", _shell.SaveAsCommand, "Ctrl+Shift+S"), Item("Save A_ll", _shell.SaveAllCommand),
            new Separator(), Item("_Close Tab", _shell.CloseTabCommand, "Ctrl+W"), Item("Close All Ta_bs", _shell.CloseAllCommand),
            Item("Reopen Closed _Tab", _shell.ReopenClosedCommand, "Ctrl+Shift+T"), new Separator(), Item("E_xit", new RelayCommand(Close), "Alt+F4"),
        ]));

        var undo = Item("_Undo", _shell.UndoCommand, "Ctrl+Z");
        undo.SetBinding(HeaderedItemsControl.HeaderProperty, nameof(ShellViewModel.UndoHeader));
        var redo = Item("_Redo", _shell.RedoCommand, "Ctrl+Y");
        redo.SetBinding(HeaderedItemsControl.HeaderProperty, nameof(ShellViewModel.RedoHeader));
        var edit = Slot(MenuSlot.Edit).ToList();
        MainMenu.Items.Add(Menu("_Edit", [undo, redo, .. edit.Count > 0 ? [new Separator(), .. edit] : Array.Empty<Control>()]));

        foreach (var top in Slot(MenuSlot.TopLevel)) MainMenu.Items.Add(top);

        var theme = Menu("_Theme", [.. new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark }.Select(t => Item(t.ToString(), new RelayCommand(() => SetTheme(t))))]);
        var leftItem = Item("_Left Pane", _shell.ToggleLeftPaneCommand, "Ctrl+Shift+L");
        var bottomItem = Item("_Bottom Pane", _shell.ToggleBottomPaneCommand, "Ctrl+Shift+M");
        leftItem.IsCheckable = bottomItem.IsCheckable = true;
        leftItem.ToolTip = "Show or hide the left pane (its tabs come from the modules)";
        bottomItem.ToolTip = "Show or hide the bottom pane (its tabs come from the modules)";
        var view = Menu("_View", [leftItem, bottomItem, new Separator(), theme, .. Slot(MenuSlot.View)]);
        // The check marks show the panes' state; it is re-read whenever the menu opens.
        view.SubmenuOpened += (_, _) =>
        {
            leftItem.IsChecked = LeftPane.Visibility == Visibility.Visible;
            bottomItem.IsChecked = BottomPane.Visibility == Visibility.Visible;
            leftItem.IsEnabled = LeftTabs.Items.Count > 0;
            bottomItem.IsEnabled = BottomTabs.Items.Count > 0;
        };
        MainMenu.Items.Add(view);
        MainMenu.Items.Add(Menu("_Tools",
        [
            Item("_Settings…", new RelayCommand(() => _shell.ShowSettings())),
            Item("File _Associations…", new RelayCommand(() => ShellWindows.ShowAssociations(_shell))), .. Slot(MenuSlot.Tools),
        ]));
        // Module topics grouped under the module's name; "F1" is shown beside whichever entry F1 opens for the active document.
        var topicItems = new List<(HelpTopic Topic, MenuItem Item)>();
        var groups = _shell.Modules.Where(m => m.HelpTopics.Count > 0).Select(m => (Control)Menu(m.DisplayName,
            [.. m.HelpTopics.Select(h => { var i = Item(h.Title, new RelayCommand(() => _shell.ShowHelp(h.Id))); topicItems.Add((h, i)); return (Control)i; })])).ToList();
        var shortcuts = Item("_Keyboard Shortcuts", new RelayCommand(() => _shell.ShowHelp("shortcuts")), "F1");
        var help = Menu("_Help",
        [
            .. groups, .. groups.Count > 0 ? [new Separator()] : Array.Empty<Control>(),
            shortcuts, .. Slot(MenuSlot.Help), new Separator(),
            Item("_About Cairn", new RelayCommand(() => ShellWindows.ShowAbout(this))),
        ]);
        help.SubmenuOpened += (_, e) =>
        {
            if (e.OriginalSource != help) return;
            var context = _shell.ContextHelpTopic;
            foreach (var (topic, item) in topicItems) item.InputGestureText = topic == context ? "F1" : string.Empty;
            shortcuts.InputGestureText = context is null ? "F1" : string.Empty;
        };
        MainMenu.Items.Add(help);
    }

    private Control Track(MenuContribution c)
    {
        if (c.IsVisibleFor is { } v) _conditional.Add((c.Item, v));
        return c.Item;
    }

    private static MenuItem Menu(string header, IEnumerable<Control> items)
    {
        var menu = new MenuItem { Header = header };
        foreach (var i in items) menu.Items.Add(i);
        return menu;
    }

    private static MenuItem Item(string header, ICommand command, string? gesture = null, object? parameter = null) =>
        new() { Header = header, Command = command, CommandParameter = parameter, InputGestureText = gesture ?? string.Empty };

    private void FillRecent()
    {
        _recentMenu.Items.Clear();
        foreach (var item in _shell.RecentFiles)
        {
            var (name, folder) = ShellViewModel.DescribeRecent(item);
            _recentMenu.Items.Add(Item(Path.Combine(folder, name).Replace("_", "__"), _shell.OpenRecentCommand, null, item));
        }
        _recentMenu.IsEnabled = _recentMenu.Items.Count > 0;
    }

    private void SetTheme(AppTheme theme)
    {
        _shell.Settings.Theme = theme;
        _shell.Theme.Apply(theme);
    }

    // ---- activation: menu visibility, panels, shortcuts ----

    private void OnActiveChanged() => RefreshContributions(activated: true);

    /// <summary>
    /// Re-evaluates contribution visibility and re-queries the panels. On activation the bottom tab is the one last
    /// chosen for the document's kind; on a module's <c>RefreshCommands</c> the current tab is kept while it exists.
    /// </summary>
    private void RefreshContributions(bool activated)
    {
        var doc = _shell.ActiveDocument;
        foreach (var (item, visible) in _conditional)
        {
            var target = item.Tag as UIElement ?? item;
            target.Visibility = visible(doc) ? Visibility.Visible : Visibility.Collapsed;
        }
        var currentLeft = (LeftTabs.SelectedItem as TabItem)?.Tag as string;
        var currentBottom = (BottomTabs.SelectedItem as TabItem)?.Tag as string;
        // On activation: the tab last chosen for this kind; a kind never seen before gets the bottom tab with the
        // lowest Order (null = first), while the left pane (often a library shared by kinds) keeps its current tab,
        // unless it showed the start page's tabs. The start page (no document) has its own remembered left tab. A module
        // refresh that already sees the new document (from its OnActivated) counts as the activation for the left pane.
        var leftSwitched = activated || doc?.Id != _leftFilledFor;
        var leftPreferred = !leftSwitched ? currentLeft
            : doc is null ? _shell.Settings.Get<string>(StartLeftTabKey)
            : _shell.Settings.Get<string>(TabKey("left", doc)) ?? (_leftShowsStart ? null : currentLeft);
        var bottomPreferred = activated && doc != null ? _shell.Settings.Get<string>(TabKey("bottom", doc)) : currentBottom;
        _leftShowsStart = doc is null;
        _leftFilledFor = doc?.Id;
        _fillingPanels = true;
        try
        {
            FillPanels(LeftTabs, LeftPane, PanelSide.Left, "left", leftPreferred);
            FillPanels(BottomTabs, BottomPane, PanelSide.Bottom, "bottom", bottomPreferred);
        }
        finally { _fillingPanels = false; }
        UpdatePaneToggles();
        UpdateNewButton();
    }

    private bool _fillingPanels;
    private bool _leftShowsStart;
    private string? _leftFilledFor; // the id of the document the left pane was last filled for (null = the start page)

    /// <summary>Settings key of the tab last chosen in a pane for a document kind, e.g. <c>shell.bottomTab.vfx</c>.</summary>
    public static string TabKey(string pane, Cairn.Ui.Documents.IDocument doc) => $"shell.{pane}Tab.{doc.Kind.Id}";

    /// <summary>Settings key of the left tab last chosen on the start page (no document open); absent = the lowest Order.</summary>
    public const string StartLeftTabKey = "shell.leftTab.start";

    private void OnPaneTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingPanels || sender is not TabControl tabs || !ReferenceEquals(e.OriginalSource, tabs)) return;
        if ((tabs.SelectedItem as TabItem)?.Tag is not string id) return;
        if (_shell.ActiveDocument is { } doc) _shell.Settings.Set(TabKey(tabs == LeftTabs ? "left" : "bottom", doc), id);
        else if (tabs == LeftTabs) _shell.Settings.Set(StartLeftTabKey, id);
    }

    /// <summary>
    /// Brings a pane's tabs in line with the panels that have content now: existing tabs are kept (their content
    /// replaced only when the panel returns a different object), new ones inserted in order, vanished ones removed.
    /// </summary>
    private void FillPanels(TabControl tabs, Border pane, PanelSide side, string key, string? selected)
    {
        var existing = tabs.Items.OfType<TabItem>().ToDictionary(t => (string)t.Tag);
        var wanted = new List<TabItem>();
        var doc = _shell.ActiveDocument;
        // The left pane belongs to the document in front: only the panels of the module that owns it (an animated
        // texture has none, so the pane hides; a packfile shows its file types, an effect the effects library).
        // With no document open every module's left panels show, for browsing.
        var owner = side == PanelSide.Left && doc is not null
            ? _shell.Modules.FirstOrDefault(m => m.DocumentKinds.Any(k => k.Id == doc.Kind.Id))
            : null;
        IReadOnlyList<Cairn.Ui.Modules.IModule> modules = owner is not null ? [owner] : _shell.Modules;
        var candidates = modules.SelectMany(m => m.Panels).Where(p => p.Side == side).OrderBy(p => p.Order)
            .Select(p => (Panel: p, Content: p.Content(doc))).Where(x => x.Content is not null).ToList();
        // An exclusive panel with content for this document replaces the others in its pane.
        if (candidates.Any(x => x.Panel.Exclusive)) candidates = candidates.Where(x => x.Panel.Exclusive).ToList();
        foreach (var (p, found) in candidates)
        {
            var content = found!;
            if (existing.TryGetValue(p.Id, out var tab)) { if (!ReferenceEquals(tab.Content, content)) tab.Content = content; }
            else tab = new TabItem { Header = p.Title, Content = content, Tag = p.Id, ToolTip = p.Title };
            System.Windows.Automation.AutomationProperties.SetName(tab, p.Title);
            wanted.Add(tab);
        }
        if (!tabs.Items.OfType<TabItem>().SequenceEqual(wanted))
        {
            tabs.Items.Clear();
            foreach (var t in wanted) tabs.Items.Add(t);
        }
        tabs.SelectedItem = wanted.FirstOrDefault(t => (string)t.Tag == selected) ?? wanted.FirstOrDefault();
        var show = tabs.Items.Count > 0 && _shell.Settings.Panels.GetValueOrDefault(key, true);
        pane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // A pane that stays open keeps the size it has (the user may have dragged it this session); only a pane that
        // opens gets the remembered size.
        if (side == PanelSide.Left)
        {
            bool wasShown = LeftColumn.ActualWidth > 0 && LeftColumn.Width.Value > 0;
            if (!show) LeftColumn.Width = new GridLength(0);
            else if (!wasShown) LeftColumn.Width = new GridLength(LeftWidth());
            LeftColumn.MinWidth = show ? 160 : 0;
            LeftSplitter.Visibility = pane.Visibility;
        }
        else
        {
            bool wasShown = BottomRow.ActualHeight > 0 && BottomRow.Height.Value > 0;
            if (!show) BottomRow.Height = new GridLength(0);
            else if (!wasShown) BottomRow.Height = new GridLength(BottomHeight());
            BottomRow.MinHeight = show ? 80 : 0;
            BottomSplitter.Visibility = pane.Visibility;
        }
    }

    /// <summary>Default left pane width (RFA Workbench used 300, ATX Workbench 280).</summary>
    internal const double DefaultLeftWidth = 280;

    /// <summary>Default bottom pane height before the user drags the splitter.</summary>
    internal const double DefaultBottomHeight = 200;

    /// <summary>
    /// The bottom pane's height: the saved one, else the default capped at about a quarter of the window height
    /// (RFA Workbench's 200) so a small window keeps a usable document area; never so tall that the document row (min 200) is squeezed.
    /// </summary>
    private double BottomHeight()
    {
        var windowHeight = ActualHeight > 0 ? ActualHeight : Height;
        var height = _shell.Settings.Layout.TryGetValue("bottomHeight", out var saved) ? saved : Math.Min(DefaultBottomHeight, Math.Round(windowHeight * 0.25));
        var room = BodyGrid.ActualHeight > 0 ? BodyGrid.ActualHeight - 200 - (DocumentTabs.ActualHeight + 6) : height;
        return Math.Max(80, Math.Min(height, room));
    }

    /// <summary>The left pane's width: the saved one, else the default capped at about a quarter of the window width.</summary>
    private double LeftWidth()
    {
        if (_shell.Settings.Layout.TryGetValue("leftWidth", out var saved)) return saved;
        var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        return Math.Max(200, Math.Min(DefaultLeftWidth, Math.Round(windowWidth * 0.23)));
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_shell is null) return;
        if (BottomPane.Visibility == Visibility.Visible && e.HeightChanged) BottomRow.Height = new GridLength(BottomHeight());
        if (LeftPane.Visibility == Visibility.Visible && e.WidthChanged && !_shell.Settings.Layout.ContainsKey("leftWidth")) LeftColumn.Width = new GridLength(LeftWidth());
    }

    // ---- New split button ----

    /// <summary>The kind the toolbar's New button creates: the active document module's first creatable kind, else the first one.</summary>
    internal Cairn.Ui.Modules.IDocumentKind? DefaultNewKind()
    {
        var kindId = _shell.ActiveDocument?.Kind.Id;
        var owner = kindId is null ? null : _shell.Modules.FirstOrDefault(m => m.DocumentKinds.Any(k => k.Id == kindId));
        return owner?.DocumentKinds.FirstOrDefault(k => k.CanCreateNew) ?? _shell.Kinds.FirstOrDefault(k => k.CanCreateNew);
    }

    private void UpdateNewButton()
    {
        var kind = DefaultNewKind();
        NewSplit.Visibility = kind is null ? Visibility.Collapsed : Visibility.Visible;
        NewButton.ToolTip = kind is null ? null : $"New {kind.DisplayName.ToLowerInvariant()}… (click the arrow for other kinds)";
        NewMenuButton.Visibility = _shell.Kinds.Count(k => k.CanCreateNew) > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNewDefault(object sender, RoutedEventArgs e)
    {
        if (DefaultNewKind() is { } kind) _shell.NewCommand.Execute(kind);
    }

    private void OnNewMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = NewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var preferred = DefaultNewKind();
        foreach (var kind in _shell.Kinds.Where(k => k.CanCreateNew))
        {
            var item = new MenuItem { Header = kind.DisplayName + "…", Command = _shell.NewCommand, CommandParameter = kind };
            if (kind == preferred) item.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private List<UIElement> _moduleToolbarItems = [];

    /// <summary>Toolbar pane toggles: shown only for panes that have tabs, checked while the pane is visible.</summary>
    private void UpdatePaneToggles()
    {
        LeftPaneToggle.Visibility = LeftTabs.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BottomPaneToggle.Visibility = BottomTabs.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LeftPaneToggle.IsChecked = LeftPane.Visibility == Visibility.Visible;
        BottomPaneToggle.IsChecked = BottomPane.Visibility == Visibility.Visible;
        var anyToggle = LeftPaneToggle.Visibility == Visibility.Visible || BottomPaneToggle.Visibility == Visibility.Visible;
        PaneToggleSeparator.Visibility = anyToggle ? Visibility.Visible : Visibility.Collapsed;
        ModuleToolbarSeparator.Visibility = _moduleToolbarItems.Any(e => e.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLeftPaneToggle(object sender, RoutedEventArgs e) => TogglePane("left", LeftPane);

    private void OnBottomPaneToggle(object sender, RoutedEventArgs e) => TogglePane("bottom", BottomPane);

    /// <summary>Middle-click on a document tab closes it, as in a browser.</summary>
    private void OnTabStripMiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != DocumentTabs; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is TabItem { DataContext: Cairn.Ui.Documents.IDocument doc } && _shell.CloseTabCommand.CanExecute(doc))
            {
                _shell.CloseTabCommand.Execute(doc);
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>
    /// <see cref="Cairn.Ui.Modules.IShellContext.ShowPanel"/>: selects the tab <paramref name="panelId"/> in whichever pane
    /// holds it, shows that pane if it was hidden, and remembers the choice for the active document's kind.
    /// </summary>
    internal bool ShowPanel(string panelId)
    {
        foreach (var (tabs, pane, key) in new[] { (LeftTabs, LeftPane, "left"), (BottomTabs, BottomPane, "bottom") })
        {
            if (tabs.Items.OfType<TabItem>().FirstOrDefault(t => (string)t.Tag == panelId) is not { } tab) continue;
            if (_shell.ActiveDocument is { } doc) _shell.Settings.Set(TabKey(key, doc), panelId);
            else if (key == "left") _shell.Settings.Set(StartLeftTabKey, panelId);
            if (pane.Visibility != Visibility.Visible)
            {
                SaveLayout();
                _shell.Settings.Panels[key] = true;
                RefreshContributions(activated: false);
            }
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().FirstOrDefault(t => (string)t.Tag == panelId) ?? tab;
            return true;
        }
        return false;
    }

    private void TogglePane(string key, Border pane)
    {
        SaveLayout();
        _shell.Settings.Panels[key] = pane.Visibility != Visibility.Visible;
        OnActiveChanged();
    }

    /// <summary>Routes this window's key presses to the shell's and the modules' shortcuts.</summary>
    public ShortcutRouter Shortcuts { get; private set; } = null!;

    // ---- layout persistence, drag-drop, closing ----

    private void RestoreLayout(AppSettings settings)
    {
        var w = settings.Window;
        if (!w.IsSet) return;
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var (bounds, maximized) = WindowPlacementLogic.Resolve(w, [virtualScreen], SystemParameters.WorkArea, new Size(MinWidth, MinHeight), new Size(Width, Height));
        Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (maximized) WindowState = WindowState.Maximized;
    }

    private void SaveLayout()
    {
        var s = _shell.Settings;
        // A minimised window squeezes the panes to their minimum sizes: keep the sizes saved before that.
        if (WindowState != WindowState.Minimized)
        {
            if (LeftPane.Visibility == Visibility.Visible && LeftColumn.ActualWidth > 0) s.Layout["leftWidth"] = LeftColumn.ActualWidth;
            if (BottomPane.Visibility == Visibility.Visible && BottomRow.ActualHeight > 0) s.Layout["bottomHeight"] = BottomRow.ActualHeight;
        }
        // RestoreBounds holds the normal rectangle while maximised, so un-maximising next run lands where it was.
        if (WindowPlacementLogic.Capture(RestoreBounds, WindowState == WindowState.Maximized) is { } placement) s.Window = placement;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _shell.OpenFiles(files);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_shell.IsDiagnosticRun && !_shell.CloseAll()) { e.Cancel = true; return; }
        _shell.StopRecoveryTimer();
        SaveLayout();
        ((App)Application.Current).SaveSettings();
    }
}

/// <summary>Window placement as pure functions over rectangles (device-independent units), testable without a window.</summary>
public static class WindowPlacementLogic
{
    /// <summary>How much of the title bar must lie on a work area for a saved rectangle to count as reachable.</summary>
    private const double GripWidth = 120, GripHeight = 24, TitleBarHeight = 32;

    /// <summary>The placement to persist from a window's restore bounds; null when the bounds are unusable.</summary>
    public static WindowPlacement? Capture(Rect restoreBounds, bool maximized) =>
        restoreBounds.IsEmpty || !IsFinite(restoreBounds) || restoreBounds.Width <= 0 || restoreBounds.Height <= 0
            ? null
            : new WindowPlacement { Left = restoreBounds.Left, Top = restoreBounds.Top, Width = restoreBounds.Width, Height = restoreBounds.Height, Maximized = maximized, IsSet = true };

    /// <summary>
    /// The rectangle and maximised flag to apply. The saved rectangle is kept (grown to the minimum size) while a work
    /// area holds a grip of its title bar; otherwise (a monitor was unplugged, the resolution shrank) the window gets its
    /// default size centred on <paramref name="defaultArea"/>. The maximised flag survives either way.
    /// </summary>
    public static (Rect Bounds, bool Maximized) Resolve(WindowPlacement saved, IReadOnlyList<Rect> workAreas, Rect defaultArea, Size minimum, Size defaultSize)
    {
        if (!saved.IsSet) return (Centred(defaultArea, minimum, defaultSize), false);
        var bounds = new Rect(saved.Left, saved.Top, Math.Max(minimum.Width, saved.Width), Math.Max(minimum.Height, saved.Height));
        return (IsFinite(bounds) && IsReachable(bounds, workAreas) ? bounds : Centred(defaultArea, minimum, defaultSize), saved.Maximized);
    }

    /// <summary>True when the title bar strip of <paramref name="bounds"/> overlaps some work area by at least a grip.</summary>
    public static bool IsReachable(Rect bounds, IReadOnlyList<Rect> workAreas)
    {
        var titleBar = new Rect(bounds.Left, bounds.Top, bounds.Width, Math.Min(bounds.Height, TitleBarHeight));
        foreach (var area in workAreas)
        {
            var overlap = Rect.Intersect(titleBar, area);
            if (!overlap.IsEmpty && overlap.Width >= Math.Min(GripWidth, titleBar.Width) && overlap.Height >= Math.Min(GripHeight, titleBar.Height)) return true;
        }
        return false;
    }

    /// <summary>The default size (shrunk to fit the area, never below the minimum) centred on the area.</summary>
    public static Rect Centred(Rect area, Size minimum, Size defaultSize)
    {
        var width = Math.Max(minimum.Width, Math.Min(defaultSize.Width, area.Width - 40));
        var height = Math.Max(minimum.Height, Math.Min(defaultSize.Height, area.Height - 40));
        return new Rect(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
    }

    private static bool IsFinite(Rect r) => double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height);
}
