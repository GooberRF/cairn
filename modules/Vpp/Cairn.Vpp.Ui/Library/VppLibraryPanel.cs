using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Cairn.Assets;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;
using Cairn.Vpp.Ui.List;

namespace Cairn.Vpp.Ui.Library;

/// <summary>A folder of packfiles in the Packfiles panel: its heading, the folder itself, and its place in the list.</summary>
/// <param name="Label">The heading ("Red Faction", "user_maps\multi", "mods\name", a search folder's name).</param>
/// <param name="Folder">The full path of the folder (the heading's tooltip).</param>
/// <param name="Order">Groups are listed in this order.</param>
public sealed record VppLibraryGroup(string Label, string Folder, int Order)
{
    public override string ToString() => Label;
}

/// <summary>One packfile listed in the Packfiles panel.</summary>
/// <param name="Path">Full path.</param>
/// <param name="Name">File name.</param>
/// <param name="Size">Size in bytes (-1 when it could not be read).</param>
/// <param name="Group">The folder it was found in.</param>
public sealed record VppLibraryEntry(string Path, string Name, long Size, VppLibraryGroup Group)
{
    public string SizeText => Size < 0 ? string.Empty : VppEntryRow.FormatSize(Size);
    public string AutomationName => SizeText.Length > 0 ? $"{Name}, {SizeText}" : Name;
}

/// <summary>A folder's node in the Packfiles tree: the heading (expandable like the Animations library's families) and its packfiles.</summary>
public sealed class VppLibraryGroupNode : ObservableObject
{
    private readonly Action<VppLibraryGroupNode>? _expandedChanged;
    private bool _isExpanded;

    /// <param name="group">The folder.</param>
    /// <param name="children">Its packfiles (as filtered).</param>
    /// <param name="expanded">The initial state.</param>
    /// <param name="expandedChanged">Called when the user expands or collapses it.</param>
    public VppLibraryGroupNode(VppLibraryGroup group, IReadOnlyList<VppLibraryEntry> children, bool expanded, Action<VppLibraryGroupNode>? expandedChanged)
    {
        Group = group;
        Children = children;
        _isExpanded = expanded;
        _expandedChanged = expandedChanged;
    }

    public VppLibraryGroup Group { get; }
    public string Label => Group.Label;
    public string Folder => Group.Folder;
    public IReadOnlyList<VppLibraryEntry> Children { get; }
    /// <summary>The badge: how many of its packfiles are shown.</summary>
    public string CountText => Children.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
    public string AutomationName => $"{Label}, {(Children.Count == 1 ? "1 packfile" : $"{Children.Count:N0} packfiles")}";

    /// <summary>Bound to the tree item: the arrow, a double-click on the heading, or Left/Right toggle it.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) _expandedChanged?.Invoke(this); }
    }
}

/// <summary>
/// Left "Packfiles" panel: every .vpp in the game folder, its user_maps folders, each mod folder and the user's search
/// folders, grouped by folder in a tree whose headings collapse like the Animations library's skeleton families,
/// listed off the UI thread, with a filter box. Opening one opens it in a packfile tab.
/// </summary>
public sealed class VppLibraryPanel : DockPanel
{
    private readonly IShellContext _shell;
    private readonly Func<AssetResolver> _resolver;
    private readonly bool _followsSettings;
    private readonly TextBox _filter = new() { Margin = new Thickness(4, 4, 2, 4), ToolTip = "Filter by file name or folder" };
    private readonly TreeView _list = new();
    // folders the user collapsed (kept across refreshes and filter changes for this session, as the library's families)
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 4), TextWrapping = TextWrapping.Wrap };
    private int _generation;

    /// <summary>Every packfile found by the last listing, in display order.</summary>
    public IReadOnlyList<VppLibraryEntry> Entries { get; private set; } = [];
    /// <summary>The packfiles the filter lets through, in display order.</summary>
    public IReadOnlyList<VppLibraryEntry> Shown { get; private set; } = [];
    /// <summary>The tree's folder nodes as shown now.</summary>
    public IReadOnlyList<VppLibraryGroupNode> Groups { get; private set; } = [];
    /// <summary>The tree (self-tests reach its items through it).</summary>
    public TreeView Tree => _list;
    /// <summary>The filter text (as typed in the box).</summary>
    public string Filter { get => _filter.Text; set => _filter.Text = value ?? string.Empty; }
    /// <summary>The footer text.</summary>
    public string StatusText => _status.Text;
    /// <summary>Raised on the UI thread after each listing has been applied.</summary>
    public event EventHandler? Listed;

    /// <param name="shell">The shell (opening files, dialogs, the game-data settings).</param>
    /// <param name="resolver">
    /// Where to look; null = the shell's asset host, re-listed whenever the game folder or search folders change.
    /// A given resolver (self-tests) is listed once and on Refresh only.
    /// </param>
    public VppLibraryPanel(IShellContext shell, Func<AssetResolver>? resolver = null)
    {
        _shell = shell;
        _resolver = resolver ?? (() => shell.Assets.Resolver);
        _followsSettings = resolver is null;
        AutomationProperties.SetName(this, "Packfiles");
        AutomationProperties.SetName(_filter, "Filter packfiles");
        AutomationProperties.SetName(_list, "Packfiles");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");

        // The Animations library's tree: the shared PaneTreeViewItem style gives the same arrow (TreeExpanderToggle) and
        // the same behaviour (click the arrow or double-click a heading to toggle; Left/Right collapse and expand).
        var baseStyle = Application.Current?.TryFindResource("PaneTreeViewItem") as Style;
        _list.ItemTemplate = BuildGroupTemplate(ContainerStyle(baseStyle, nameof(VppLibraryEntry.Path), expands: false));
        _list.ItemContainerStyle = ContainerStyle(baseStyle, nameof(VppLibraryGroupNode.Folder), expands: true);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.BorderThickness = new Thickness(0);
        _list.Padding = new Thickness(0, 2, 0, 2);
        if (Application.Current?.TryFindResource("PaneScrollBar") is Style scrollBar)
            _list.Resources.Add(typeof(ScrollBar), new Style(typeof(ScrollBar), scrollBar));
        _list.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        _list.MouseDoubleClick += (_, e) =>
        {
            // a double-click on a packfile opens it (on a heading the tree toggles it)
            if (ItemOf(e.OriginalSource) is { DataContext: VppLibraryEntry entry }) { Open(entry); e.Handled = true; }
        };
        // a right-click selects the row under the pointer first, so the menu acts on it
        _list.PreviewMouseRightButtonDown += (_, e) => { if (ItemOf(e.OriginalSource) is { } item) item.IsSelected = true; };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && Selected is { } s) { Open(s); e.Handled = true; } };
        var hasSelection = new Func<bool>(() => Selected is not null);
        _list.ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "_Open", Command = new RelayCommand(OpenSelected, hasSelection), ToolTip = "Open the packfile in a Cairn tab" },
                new MenuItem { Header = "Show in _Explorer", Command = new RelayCommand(ShowInExplorer, hasSelection), ToolTip = "Open its folder in Explorer with the file selected" },
                new MenuItem { Header = "_Copy path", Command = new RelayCommand(CopyPath, hasSelection), ToolTip = "Copy the packfile's full path" },
            },
        };

        var placeholder = new TextBlock { Text = "Filter by name or folder", IsHitTestVisible = false, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _filter.TextChanged += (_, _) => { placeholder.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; ApplyFilter(); };
        var refresh = new Button
        {
            Content = "", // Segoe MDL2 "Refresh"
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            ToolTip = "List the packfiles again",
            Margin = new Thickness(0, 4, 4, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        refresh.SetResourceReference(StyleProperty, "ToolButton");
        AutomationProperties.SetName(refresh, "Refresh the packfile list");
        refresh.Click += (_, _) => Reload();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_filter); top.Children.Add(placeholder); top.Children.Add(refresh);
        Grid.SetColumn(refresh, 1);
        SetDock(top, Dock.Top); SetDock(_status, Dock.Bottom);
        Children.Add(top); Children.Add(_status); Children.Add(_list);

        // a new game folder or search folders (Settings) re-list; so does the asset host's own refresh
        if (_followsSettings) shell.Assets.Changed += (_, _) => Dispatcher.InvokeAsync(Reload);
        Reload();
    }

    private VppLibraryEntry? Selected => _list.SelectedItem as VppLibraryEntry;

    private TreeViewItem? ItemOf(object source)
    {
        for (var d = source as DependencyObject; d is not null && d != _list; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TreeViewItem item) return item;
        return null;
    }

    /// <summary>The tree item style (the shared PaneTreeViewItem) with the tooltip, name and, for headings, IsExpanded bound.</summary>
    private static Style ContainerStyle(Style? basedOn, string toolTipPath, bool expands)
    {
        var style = basedOn is null ? new Style(typeof(TreeViewItem)) : new Style(typeof(TreeViewItem), basedOn);
        if (expands) style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(VppLibraryGroupNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(ToolTipProperty, new Binding(toolTipPath)));
        style.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding("AutomationName")));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(1)));
        return style;
    }

    /// <summary>One line as the Animations library draws its nodes: a glyph column, the text, then a detail.</summary>
    private static FrameworkElementFactory Line(char glyph, string textPath, string detailPath, bool badge)
    {
        var panel = new FrameworkElementFactory(typeof(DockPanel));
        panel.SetValue(BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, glyph.ToString());
        icon.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.FontSizeProperty, 11.0);
        icon.SetValue(WidthProperty, 18.0);
        icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        panel.AppendChild(icon);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding(detailPath));
        detail.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        if (badge)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetResourceReference(StyleProperty, "BadgeBorder");
            border.SetValue(MarginProperty, new Thickness(8, 0, 2, 0));
            border.SetValue(DockPanel.DockProperty, Dock.Right);
            detail.SetResourceReference(StyleProperty, "BadgeText");
            border.AppendChild(detail);
            panel.AppendChild(border);
        }
        else
        {
            detail.SetValue(TextBlock.FontSizeProperty, 11.0);
            detail.SetValue(MarginProperty, new Thickness(8, 0, 2, 0));
            detail.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            detail.SetValue(DockPanel.DockProperty, Dock.Right);
            panel.AppendChild(detail);
        }
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(textPath));
        text.SetValue(TextBlock.FontSizeProperty, 12.0);
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        panel.AppendChild(text);
        return panel;
    }

    /// <summary>A heading (folder glyph, label, count badge) whose children are the packfile rows (package glyph, name, size).</summary>
    private static HierarchicalDataTemplate BuildGroupTemplate(Style entryStyle) => new(typeof(VppLibraryGroupNode))
    {
        ItemsSource = new Binding(nameof(VppLibraryGroupNode.Children)),
        VisualTree = Line((char)0xE8B7 /* Folder */, nameof(VppLibraryGroupNode.Label), nameof(VppLibraryGroupNode.CountText), badge: true),
        ItemTemplate = new DataTemplate(typeof(VppLibraryEntry)) { VisualTree = Line((char)0xE7B8 /* Package */, nameof(VppLibraryEntry.Name), nameof(VppLibraryEntry.SizeText), badge: false) },
        ItemContainerStyle = entryStyle,
    };

    /// <summary>Lists the packfiles again (the Refresh button).</summary>
    public void Reload() => _ = ReloadAsync();

    /// <summary>Lists the packfiles off the UI thread and shows them; a newer listing supersedes this one.</summary>
    public async Task ReloadAsync()
    {
        int generation = ++_generation;
        _status.Text = "Listing packfiles...";
        var resolver = _resolver();
        IReadOnlyList<VppLibraryEntry> entries;
        // screenshots wait for the listing
        using var busy = BusyTracker.Begin("packfile listing");
        try { entries = await Task.Run(() => Scan(resolver)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (generation == _generation) _status.Text = "Could not list packfiles: " + ex.Message;
            return;
        }
        if (generation != _generation) return;
        Entries = entries;
        ApplyFilter();
        Listed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Every packfile <paramref name="resolver"/> can see, grouped: the game folder, its user_maps folders, each folder of
    /// the game's <c>mods</c> folder that holds packfiles (its top level only), then the search folders. A file listed
    /// by an earlier group is not listed again. Within a group, by name. Any thread.
    /// </summary>
    public static IReadOnlyList<VppLibraryEntry> Scan(AssetResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var sources = resolver.DescribeArchiveSources();
        var groups = new List<(VppLibraryGroup Group, IEnumerable<string> Paths)>();
        int order = 0;
        foreach (var s in sources.Where(s => s.Kind == AssetSourceKind.GameArchive))
            groups.Add((new VppLibraryGroup("Red Faction", s.Folder, order++), s.ArchivePaths));
        foreach (var s in sources.Where(s => s.Kind == AssetSourceKind.GameFolderArchive))
            groups.Add((new VppLibraryGroup(s.Label, s.Folder, order++), s.ArchivePaths));
        if (resolver.Options.GameDirectory is { Length: > 0 } game)
        {
            foreach (var mod in ListFolders(Path.Combine(game, "mods")))
            {
                var files = ListPackfiles(mod);
                if (files.Count > 0) groups.Add((new VppLibraryGroup(Path.Combine("mods", Path.GetFileName(mod)), mod, order++), files));
            }
        }
        foreach (var s in sources.Where(s => s.Kind == AssetSourceKind.SearchFolderArchive))
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(s.Folder));
            groups.Add((new VppLibraryGroup(name.Length > 0 ? name : s.Folder, s.Folder, order++), s.ArchivePaths));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VppLibraryEntry>();
        foreach (var (group, paths) in groups)
        {
            var inGroup = new List<VppLibraryEntry>();
            foreach (string path in paths)
            {
                string full;
                try { full = Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (!seen.Add(full)) continue;
                long size;
                try { size = new FileInfo(full).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { size = -1; }
                inGroup.Add(new VppLibraryEntry(full, Path.GetFileName(full), size, group));
            }
            result.AddRange(inGroup.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
        }
        return result;
    }

    private static IReadOnlyList<string> ListFolders(string folder)
    {
        try { return Directory.Exists(folder) ? [.. Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase)] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static IReadOnlyList<string> ListPackfiles(string folder)
    {
        // "*.vpp" alone would also match longer extensions such as ".vppx" (Windows' short-name rule)
        try { return [.. Directory.EnumerateFiles(folder, "*.vpp").Where(f => f.EndsWith(".vpp", StringComparison.OrdinalIgnoreCase))]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        Shown = f.Length == 0 ? Entries
            : [.. Entries.Where(e => e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Group.Label.Contains(f, StringComparison.OrdinalIgnoreCase))];
        // Every heading starts expanded and keeps the state the user gave it; while filtering, the matching headings
        // all show expanded (and toggling one then is not remembered).
        bool filtering = f.Length > 0;
        foreach (var g in Entries.Select(e => e.Group).Distinct().Where(g => _pendingCollapse.Remove(g.Label))) _collapsed.Add(g.Folder);
        Groups = [.. Shown.GroupBy(e => e.Group).Select(g => new VppLibraryGroupNode(g.Key, [.. g],
            expanded: filtering || !_collapsed.Contains(g.Key.Folder), filtering ? null : RememberExpanded))];
        _list.ItemsSource = Groups;
        int folders = Entries.Select(e => e.Group).Distinct().Count();
        _status.Text = Entries.Count == 0
            ? (_followsSettings && !_shell.Assets.HasSources ? "No packfiles found: set the game folder in Settings." : "No packfiles found.")
            : f.Length == 0 ? $"{Count(Entries.Count)} in {folders} folder{(folders == 1 ? "" : "s")}"
            : $"{Shown.Count:N0} of {Count(Entries.Count)}";
    }

    /// <summary>Diagnostics: collapses the headings labelled <paramref name="labels"/> now or, before the listing arrives, once it does.</summary>
    public void Collapse(IEnumerable<string> labels)
    {
        _pendingCollapse.UnionWith(labels);
        if (Entries.Count > 0) ApplyFilter();
    }

    private readonly HashSet<string> _pendingCollapse = new(StringComparer.OrdinalIgnoreCase);

    private void RememberExpanded(VppLibraryGroupNode node)
    {
        if (node.IsExpanded) _collapsed.Remove(node.Folder); else _collapsed.Add(node.Folder);
    }

    /// <summary>True when the user collapsed the heading of <paramref name="folder"/> (kept across refreshes and filters).</summary>
    public bool IsCollapsed(string folder) => _collapsed.Contains(folder);

    private static string Count(int n) => n == 1 ? "1 packfile" : $"{n:N0} packfiles";

    private void OpenSelected() { if (Selected is { } s) Open(s); }

    /// <summary>Opens <paramref name="entry"/> in a packfile tab (an already open one is brought forward).</summary>
    public bool Open(VppLibraryEntry entry) => _shell.OpenFile(entry.Path);

    private void ShowInExplorer()
    {
        if (Selected is not { } s) return;
        if (!File.Exists(s.Path)) { _shell.Dialogs.ShowError("File not found", s.Path); return; }
        if (_shell.IsDiagnosticRun) { _shell.ShowStatus($"Would show {s.Path} in Explorer"); return; }
        try { using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{s.Path}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { _shell.Dialogs.ShowError("Could not open Explorer", ex.Message); }
    }

    private void CopyPath()
    {
        if (Selected is not { } s) return;
        _shell.ShowStatus(SystemClipboard.TrySetText(s.Path) ? $"Copied {s.Path}" : "The clipboard is busy: " + SystemClipboard.LastError?.Message);
    }
}
