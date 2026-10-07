using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.VertexEditing;

namespace Cairn.Vfx.Ui;

/// <summary>Vertex mode surface: shortcuts, Effect menu entries, viewport toolbar toggle, Vertices tab, status item.</summary>
public sealed partial class VfxModule
{
    private ShortcutInfo VShortcut(string what, Key key, ModifierKeys mods, Action<VfxVertexMode> run)
        => new("Effect vertex mode", what, key, mods,
            new RelayCommand(() => { if (Shell.ActiveDocument is VfxDocument d && VfxVertexMode.Of(d).IsActive) run(VfxVertexMode.Of(d)); }),
            doc => doc is VfxDocument d && VfxVertexMode.Of(d).IsActive); // enabled but unavailable: leave the keys to other bindings

    private IReadOnlyList<ShortcutInfo>? _vertexShortcuts;
    /// <summary>Vertex-mode shortcuts (spread into the timeline list): Tab toggles, Escape leaves; the rest only while the mode is active (on and available for the selection).</summary>
    private IReadOnlyList<ShortcutInfo> VertexShortcuts => _vertexShortcuts ??=
    [
        new("Effect timeline", "Vertex mode on/off (selected mesh)", Key.Tab, ModifierKeys.None,
            new RelayCommand(() => { if (Shell.ActiveDocument is VfxDocument d) ToggleVertex(d); }), d => d is VfxDocument),
        new("Effect vertex mode", "Leave vertex mode", Key.Escape, ModifierKeys.None,
            new RelayCommand(() => { if (Shell.ActiveDocument is VfxDocument d && VfxVertexMode.Of(d).Enabled) ToggleVertex(d); }),
            doc => doc is VfxDocument d && VfxVertexMode.Of(d).Enabled),
        VShortcut("Select all vertices", Key.A, ModifierKeys.Control, m => m.SelectAll()),
        VShortcut("Invert vertex selection", Key.I, ModifierKeys.Control, m => m.Invert()),
        VShortcut("Select connected vertices", Key.L, ModifierKeys.None, m => m.SelectConnected()),
        VShortcut("Grow vertex selection", Key.OemPlus, ModifierKeys.Control, m => m.Grow()),
        VShortcut("Shrink vertex selection", Key.OemMinus, ModifierKeys.Control, m => m.Shrink()),
        VShortcut("Delete selected vertices (and their faces)", Key.Delete, ModifierKeys.Control, m => m.Delete()),
        VShortcut("Merge selected vertices at their centroid", Key.M, ModifierKeys.None, m => m.Merge()),
        VShortcut("Vertex scope: this frame / all frames", Key.F, ModifierKeys.Shift, m => m.AllFrames = !m.AllFrames),
    ];

    /// <summary>Vertex mode on/off; swaps the viewport gizmo controller and shows the Vertices tab.</summary>
    internal static void ToggleVertex(VfxDocument d)
    {
        var m = VfxVertexMode.Of(d);
        m.Enabled = !m.Enabled;
        AttachGizmo(d);
        if (d.View is VfxDocumentView v) v.Viewport.Invalidate();
    }

    /// <summary>Diagnostics: <c>--vertex all | 0,3,7</c> (vertex mode on the primary selection; optional <c>--vertex-scope all</c>).</summary>
    internal static void ApplyVertexOptions(VfxDocument doc, IReadOnlyDictionary<string, string> options, Cairn.Viewport.PoseTool tool)
    {
        if (!options.TryGetValue("vertex", out var vx)) return;
        var vm = VfxVertexMode.Of(doc);
        vm.Enabled = true;
        vm.AllFrames = options.TryGetValue("vertex-scope", out var sc) && sc.Equals("all", StringComparison.OrdinalIgnoreCase);
        if (vx.Equals("all", StringComparison.OrdinalIgnoreCase)) vm.SelectAll();
        else vm.Select(vx.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => int.TryParse(t, out int i) ? i : -1).Where(i => i >= 0 && i < (vm.Mesh?.NumVertices ?? 0)).ToList());
        vm.Gizmo.Tool = tool == Cairn.Viewport.PoseTool.Select ? Cairn.Viewport.PoseTool.Move : tool;
    }

    /// <summary>Effect menu > Vertex mode entries.</summary>
    private void AddVertexItems(MenuItem menu)
    {
        var root = new MenuItem { Header = "_Vertex mode" };
        MenuItem Add(string header, string gesture, string tip, Action<VfxDocument> run, bool needsMode = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, ToolTip = tip };
            mi.Click += (_, _) => { if (Shell.ActiveDocument is VfxDocument d) run(d); };
            root.SubmenuOpened += (_, _) =>
            {
                var d = Shell.ActiveDocument as VfxDocument;
                var vm = d is null ? null : VfxVertexMode.Of(d);
                mi.IsEnabled = d is not null && (!needsMode || vm!.IsActive);
                if (mi.Header is "Vertex _mode") mi.IsChecked = vm?.Enabled == true;
                ToolTipService.SetShowOnDisabled(mi, true);
                mi.ToolTip = vm is { Enabled: true, Unavailable: { } why } ? why : tip;
            };
            root.Items.Add(mi);
            return mi;
        }
        Add("Vertex _mode", "Tab", "Edit the vertices of the selected mesh", ToggleVertex, false).IsCheckable = false;
        root.Items.Add(new Separator());
        Add("Select _all", "Ctrl+A", "Select every vertex", d => VfxVertexMode.Of(d).SelectAll());
        Add("_Invert selection", "Ctrl+I", "Invert the vertex selection", d => VfxVertexMode.Of(d).Invert());
        Add("Select _connected", "L", "Add every vertex connected to the selection", d => VfxVertexMode.Of(d).SelectConnected());
        Add("_Grow selection", "Ctrl+Plus", "Add the neighbours of the selection", d => VfxVertexMode.Of(d).Grow());
        Add("_Shrink selection", "Ctrl+Minus", "Drop the border of the selection", d => VfxVertexMode.Of(d).Shrink());
        root.Items.Add(new Separator());
        Add("_Delete vertices", "Ctrl+Delete", "Delete the selected vertices and their faces", d => VfxVertexMode.Of(d).Delete());
        Add("_Merge to centre", "M", "Merge the selected vertices at their centroid", d => VfxVertexMode.Of(d).Merge());
        menu.Items.Add(new Separator());
        menu.Items.Add(root);
    }

    private static readonly ConditionalWeakTable<VfxDocumentView, object> VertexUiAttached = new();

    /// <summary>Once per view: Vertices inspector tab (shown in vertex mode), viewport toolbar toggle, status item.</summary>
    internal static void AttachVertexUi(VfxDocument d)
    {
        if (d.View is not VfxDocumentView v || VertexUiAttached.TryGetValue(v, out _)) return;
        VertexUiAttached.Add(v, new object());
        var vm = VfxVertexMode.Of(d);
        var tab = new TabItem { Header = "Vertices", ToolTip = "Vertex mode: selection, positions, faces and UVs", Content = new VfxVerticesTab(d), Visibility = Visibility.Collapsed };
        v.Inspectors.Tabs.Add(tab);

        var toggle = new ToggleButton { Content = new TextBlock { Text = "Vertex mode", FontSize = 12 }, ToolTip = "Edit the vertices of the selected mesh (Tab; Esc leaves)", Height = 24, Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(2, 3, 2, 3) };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToolToggle");
        AutomationProperties.SetName(toggle, "Vertex mode");
        toggle.Click += (_, _) => { if (toggle.IsChecked != vm.Enabled) ToggleVertex(d); toggle.IsChecked = vm.Enabled; };
        var hint = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        hint.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = hint });
        var sep = new System.Windows.Shapes.Rectangle { Width = 1, Margin = new Thickness(4, 6, 4, 6) };
        sep.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "App.Border");
        DockPanel.SetDock(sep, Dock.Left);
        DockPanel.SetDock(toggle, Dock.Left);
        if (v.Viewport.Parent is DockPanel centre)
        {
            // Same line as the gizmo toggles when that bar exists (one compact viewport toolbar).
            if (centre.Children.OfType<DockPanel>().FirstOrDefault(p => Equals(p.Tag, ViewportBarTag)) is { } shared)
            {
                shared.LastChildFill = true;
                shared.Children.Add(sep); shared.Children.Add(toggle); shared.Children.Add(hint);
            }
            else
            {
                var bar = new DockPanel { LastChildFill = true };
                bar.SetResourceReference(Panel.BackgroundProperty, "App.ChromeBackground");
                bar.Children.Add(toggle); bar.Children.Add(hint);
                DockPanel.SetDock(bar, Dock.Top);
                centre.Children.Insert(centre.Children.IndexOf(v.Viewport), bar);
            }
        }

        void Sync()
        {
            toggle.IsChecked = vm.Enabled;
            hint.Text = vm.Enabled ? vm.StatusText : VfxVertexEdits.Unavailable(d.Current, d.Selection.Primary) is { } why ? why : "Tab: edit the vertices of the selected mesh";
            var wasVisible = tab.Visibility == Visibility.Visible;
            tab.Visibility = vm.Enabled ? Visibility.Visible : Visibility.Collapsed;
            if (vm.Enabled && !wasVisible) v.Inspectors.SelectedItem = tab;
            if (!vm.Enabled && v.Inspectors.SelectedItem == tab) v.Inspectors.SelectedIndex = 1;
            d.VertexStatus = vm.Enabled ? [new(vm.StatusText, "Vertex mode (Tab toggles, Esc leaves)")] : [];
        }
        vm.Changed += (_, _) => Sync();
        d.Selection.Changed += (_, _) => { Sync(); if (vm.Enabled) v.Dispatcher.InvokeAsync(() => v.Inspectors.SelectedItem = tab, System.Windows.Threading.DispatcherPriority.Background); };
        d.FrameChanged += (_, _) => { if (vm.Enabled) Sync(); };
        Sync();
    }
}
