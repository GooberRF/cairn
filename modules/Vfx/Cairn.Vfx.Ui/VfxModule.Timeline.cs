using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Viewport;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Timeline;
using Cairn.Vfx.Ui.Viewport;

namespace Cairn.Vfx.Ui;

/// <summary>Timeline panel, gizmo controller and timeline/gizmo shortcuts.</summary>
public sealed partial class VfxModule
{
    private static readonly ConditionalWeakTable<VfxDocument, VfxTimelinePanel> TimelinePanels = new();

    /// <summary>The cached timeline panel (and gizmo target) of a document.</summary>
    public static VfxTimelinePanel TimelineOf(VfxDocument doc) => TimelinePanels.GetValue(doc, d => new VfxTimelinePanel(d));

    /// <summary>The timeline bottom panel (lowest order: default tab for effects); listed in <c>Panels</c> (VfxModule.Creation.cs).</summary>
    private static readonly PanelContribution TimelinePanelContribution =
        new("vfx.timeline", "Timeline", PanelSide.Bottom, -100, d => d is VfxDocument v ? TimelineOf(v) : null);

    private static bool IsEffect(Cairn.Ui.Documents.IDocument? d) => d is VfxDocument;

    private ShortcutInfo S(string what, Key key, ModifierKeys mods, Action<VfxDocument> run)
        => new("Effect timeline", what, key, mods, new RelayCommand(() => { if (Shell.ActiveDocument is VfxDocument d) run(d); }), IsEffect);

    private IReadOnlyList<ShortcutInfo>? _shortcuts;
    /// <summary>Timeline/gizmo shortcuts, effect documents only, never while typing; listed in <c>Shortcuts</c> (VfxModule.Editing.cs).</summary>
    private IReadOnlyList<ShortcutInfo> TimelineShortcuts => _shortcuts ??=
    [
        S("Move tool", Key.W, ModifierKeys.None, d => SetTool(d, PoseTool.Move)),
        S("Rotate tool", Key.E, ModifierKeys.None, d => SetTool(d, PoseTool.Rotate)),
        S("Scale tool", Key.R, ModifierKeys.None, d => SetTool(d, PoseTool.Scale)),
        S("Select tool (no gizmo)", Key.Q, ModifierKeys.None, d => SetTool(d, PoseTool.Select)),
        .. VertexShortcuts, // VfxModule.Vertex.cs
        S("Insert key at the playhead (selected objects, all channels)", Key.K, ModifierKeys.None, d => TimelineOf(d).InsertKeys(null)),
        S("Previous frame", Key.OemComma, ModifierKeys.None, d => d.SeekFrame(MathF.Max(0, MathF.Round(d.TimelineFrame) - 1))),
        S("Next frame", Key.OemPeriod, ModifierKeys.None, d => d.SeekFrame(MathF.Min(d.EndFrame, MathF.Round(d.TimelineFrame) + 1))),
        S("First frame", Key.Home, ModifierKeys.None, d => d.SeekFrame(0)),
        S("Last frame", Key.End, ModifierKeys.None, d => d.SeekFrame(d.EndFrame)),
        S("Previous key", Key.OemComma, ModifierKeys.Shift, d => TimelineOf(d).JumpKey(-1)),
        S("Next key", Key.OemPeriod, ModifierKeys.Shift, d => TimelineOf(d).JumpKey(+1)),
    ];

    private static VfxGizmoTarget Gizmo(VfxDocument d) => TimelineOf(d).Gizmo;

    private static void SetTool(VfxDocument d, PoseTool tool)
    {
        Gizmo(d).Tool = tool;
        VertexEditing.VfxVertexMode.Of(d).Gizmo.Tool = tool;
        if (d.View is VfxDocumentView v) v.Viewport.Invalidate();
    }

    /// <summary>Diagnostics: <c>--timeline expanded</c> opens every key row; <c>--tool move|rotate|scale|select</c> picks the gizmo tool.</summary>
    internal static void ApplyTimelineOptions(VfxDocument doc, IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("timeline", out var tl) && tl.Equals("expanded", StringComparison.OrdinalIgnoreCase)) TimelineOf(doc).Surface.ExpandAll();
        if (options.TryGetValue("tool", out var tool))
            Gizmo(doc).Tool = tool.ToLowerInvariant() switch { "rotate" => PoseTool.Rotate, "scale" => PoseTool.Scale, "select" => PoseTool.Select, _ => PoseTool.Move };
        ApplyVertexOptions(doc, options, Gizmo(doc).Tool); // VfxModule.Vertex.cs
        // --key t|r|s : select the first key of that track of the primary selection (Keys tab screenshots).
        if (options.TryGetValue("key", out var kc) && doc.Current.Sections.ElementAtOrDefault(doc.Selection.Primary) is VfxMesh { Keys: not null } km)
        {
            var ch = kc.ToLowerInvariant() switch { "r" => VfxKeyChannel.Rotation, "s" => VfxKeyChannel.Scale, _ => VfxKeyChannel.Translation };
            if (VfxTimelineEdits.KeyTimes(km, ch).Cast<int?>().FirstOrDefault() is { } t)
                doc.Selection.SelectKeys([new VfxKeyRef(doc.Selection.Primary, ch, t)]);
        }
        if (doc.View is VfxDocumentView v) v.Dispatcher.InvokeAsync(() => AttachGizmo(doc), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Effect menu > Timeline items (called from <c>BuildMenu</c>): toggles, insert/delete keys.</summary>
    private void AddTimelineItems(MenuItem menu)
    {
        VfxGizmoPrefs.Load(new ModuleSettings(Shell.Settings, Id));
        menu.Items.Add(new Separator());
        MenuItem Toggle(string header, string tip, Func<bool> get, Action<bool> set)
        {
            var m = new MenuItem { Header = header, ToolTip = tip, IsCheckable = true, IsChecked = get() };
            m.Click += (_, _) => set(m.IsChecked);
            menu.SubmenuOpened += (_, _) => m.IsChecked = get();
            menu.Items.Add(m); return m;
        }
        Toggle("_Auto-key", AutoKeyTip, () => VfxGizmoPrefs.AutoKey, v => VfxGizmoPrefs.AutoKey = v);
        Toggle("Local a_xes", LocalTip, () => VfxGizmoPrefs.LocalSpace, v => VfxGizmoPrefs.LocalSpace = v);
        Toggle("_Pivot mode", PivotTip, () => VfxGizmoPrefs.PivotMode, v => VfxGizmoPrefs.PivotMode = v);
        var keys = new MenuItem { Header = "_Keys" };
        void Item(string header, string tip, Action<VfxDocument> run, string? gesture = null) =>
            keys.Items.Add(new MenuItem { Header = header, ToolTip = tip, InputGestureText = gesture ?? "", Command = new RelayCommand(() => { if (Active is { } d) run(d); }, () => Active is { IsOlderVersion: false }) });
        Item("_Insert key (all tracks)", "Key translation, rotation and scale of the selected keyframed meshes at the playhead", d => TimelineOf(d).InsertKeys(null), "K");
        Item("Insert _translation key", "Key translation only", d => TimelineOf(d).InsertKeys(VfxKeyChannel.Translation));
        Item("Insert _rotation key", "Key rotation only", d => TimelineOf(d).InsertKeys(VfxKeyChannel.Rotation));
        Item("Insert _scale key", "Key scale only", d => TimelineOf(d).InsertKeys(VfxKeyChannel.Scale));
        Item("_Delete keys at playhead", "Delete the keys of the selected meshes at the playhead (a track keeps at least one key)", d => TimelineOf(d).DeleteKeysAtPlayhead());
        menu.Items.Add(keys);
    }

    internal const string AutoKeyTip = "On: gizmo drags on keyframed meshes set keys at the playhead. Off: they offset every key of the channel.";
    internal const string PivotTip = "Move/rotate gizmo drags on keyframed meshes change the pivot; the keys are compensated so the motion is unchanged";
    internal const string LocalTip = "Gizmo axes follow the object's orientation (off: world axes)";

    private static readonly ConditionalWeakTable<VfxDocumentView, object> TimelineUiAttached = new();

    /// <summary>Once per view: Keys inspector tab (auto-shown when keys get selected), viewport toggle bar, status item.</summary>
    /// <summary>Tag of the viewport toolbar (gizmo toggles; the vertex mode toggle joins it on the same line).</summary>
    internal const string ViewportBarTag = "VfxViewportBar";

    private static void AttachTimelineUi(VfxDocument d)
    {
        if (d.View is not VfxDocumentView v || TimelineUiAttached.TryGetValue(v, out _)) return;
        TimelineUiAttached.Add(v, new object());
        var tab = new TabItem { Header = "Keys", ToolTip = "The keys selected in the timeline", Content = new VfxKeysTab(d), Visibility = Visibility.Collapsed };
        v.Inspectors.Tabs.Add(tab);
        int lastCount = 0;
        d.Selection.Changed += (_, _) =>
        {
            int n = d.Selection.Keys.Count;
            tab.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (n > 0 && lastCount == 0) v.Inspectors.SelectedItem = tab;
            if (n == 0 && v.Inspectors.SelectedItem == tab) v.Inspectors.SelectedIndex = 1;
            lastCount = n;
        };
        var bar = new DockPanel { LastChildFill = false, Tag = ViewportBarTag };
        bar.SetResourceReference(Panel.BackgroundProperty, "App.ChromeBackground");
        System.Windows.Controls.Primitives.ToggleButton Tb(string text, string tip, Func<bool> get, Action<bool> set)
        {
            var t = new System.Windows.Controls.Primitives.ToggleButton { Content = new TextBlock { Text = text, FontSize = 12 }, ToolTip = tip, IsChecked = get(), Height = 24, Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(2, 3, 2, 3) };
            t.SetResourceReference(FrameworkElement.StyleProperty, "ToolToggle");
            System.Windows.Automation.AutomationProperties.SetName(t, text);
            t.Click += (_, _) => set(t.IsChecked == true);
            VfxGizmoPrefs.OnChangedWhileAlive(v, () => t.IsChecked = get());
            DockPanel.SetDock(t, Dock.Left);
            bar.Children.Add(t); return t;
        }
        // Gizmo tools first, with the same icons and style as the animation viewport's tool bar.
        var gizmo = Gizmo(d);
        var tools = new List<(PoseTool Tool, RadioButton Button)>();
        void Tool(PoseTool tool, string name, string tip, string icon)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(icon), Width = 16, Height = 16, Stretch = System.Windows.Media.Stretch.None,
                StrokeThickness = 1.4, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            };
            path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground))
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
            var b = new RadioButton { Content = path, ToolTip = tip, GroupName = "VfxGizmoTool", Height = 24, Margin = new Thickness(tools.Count == 0 ? 4 : 0, 3, 0, 3) };
            b.SetResourceReference(FrameworkElement.StyleProperty, "ToolToggle");
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            b.Click += (_, _) => SetTool(d, tool);
            DockPanel.SetDock(b, Dock.Left);
            bar.Children.Add(b);
            tools.Add((tool, b));
        }
        Tool(PoseTool.Select, "Select tool", "Select tool (Q): click objects to select them, no gizmo", "M4,2.5 L4,13 L6.8,10.4 L8.8,14.2 L10.6,13.3 L8.6,9.6 L12.2,9.4 Z");
        Tool(PoseTool.Rotate, "Rotate tool", "Rotate tool (E): drag the gizmo rings to rotate the selected object", "M13,8 A5,5 0 1 1 10.5,3.7 M10.5,1.5 L10.8,3.9 L8.4,4.4");
        Tool(PoseTool.Move, "Move tool", "Move tool (W): drag the gizmo arrows to move the selected object",
            "M8,1.5 V14.5 M1.5,8 H14.5 M6,3.5 L8,1.5 L10,3.5 M6,12.5 L8,14.5 L10,12.5 M3.5,6 L1.5,8 L3.5,10 M12.5,6 L14.5,8 L12.5,10");
        Tool(PoseTool.Scale, "Scale tool", "Scale tool (R): drag the axis cubes to scale the selected object; the centre scales uniformly, facing quads set width and height",
            "M2.5,8.5 H7.5 V13.5 H2.5 Z M7.5,8.5 L13.5,2.5 M9.5,2.5 H13.5 V6.5");
        void SyncTools() { foreach (var (tool, b) in tools) b.IsChecked = gizmo.Tool == tool; }
        System.ComponentModel.PropertyChangedEventManager.AddHandler(gizmo, (_, _) => SyncTools(), nameof(VfxGizmoTarget.Tool));
        SyncTools();
        var toolSep = new System.Windows.Shapes.Rectangle { Width = 1, Margin = new Thickness(5, 6, 3, 6) };
        toolSep.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "App.Border");
        DockPanel.SetDock(toolSep, Dock.Left);
        bar.Children.Add(toolSep);

        Tb("Auto-key", AutoKeyTip, () => VfxGizmoPrefs.AutoKey, x => VfxGizmoPrefs.AutoKey = x);
        Tb("Local axes", LocalTip, () => VfxGizmoPrefs.LocalSpace, x => VfxGizmoPrefs.LocalSpace = x);
        Tb("Pivot mode", PivotTip, () => VfxGizmoPrefs.PivotMode, x => VfxGizmoPrefs.PivotMode = x);
        if (v.Viewport.Parent is DockPanel centre)
        {
            DockPanel.SetDock(bar, Dock.Top);
            centre.Children.Insert(centre.Children.IndexOf(v.Viewport), bar);
        }
        void Status() { d.GizmoStatus = [new(VfxGizmoPrefs.StatusText, "Gizmo settings (Effect menu, viewport bar)")]; v.Viewport.Invalidate(); }
        VfxGizmoPrefs.OnChangedWhileAlive(v, Status);
        Status();
    }

    /// <summary>Hooks the gizmo controller into a document's viewport (called when an effect becomes active).</summary>
    internal static void AttachGizmo(VfxDocument d)
    {
        var vm = VertexEditing.VfxVertexMode.Of(d);
        AttachTimelineUi(d);
        AttachVertexUi(d); // Vertices tab, viewport toggle, status item (once per view)
        IGizmoTarget want = vm.Enabled ? vm.Gizmo : Gizmo(d);
        if (d.View is VfxDocumentView v && v.Viewport.Gizmos.Controller != want) { v.Viewport.Gizmos.Controller = want; v.Viewport.Invalidate(); }
    }
}

/// <summary>The timeline bottom panel: toolbar (insert key, delete keys, auto-key, local space, tools) over the dope sheet.</summary>
public sealed class VfxTimelinePanel : DockPanel
{
    private readonly VfxDocument _doc;
    public VfxTimelineSurface Surface { get; }
    public VfxGizmoTarget Gizmo { get; }

    public VfxTimelinePanel(VfxDocument doc)
    {
        _doc = doc;
        Gizmo = new VfxGizmoTarget(doc);
        Surface = new VfxTimelineSurface(doc);
        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(4, 2, 4, 2) };
        static TextBlock Glyph(string g, double size = 12) => new() { Text = g, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        T Add<T>(T c, string tip, string name, double gapAfter = 2) where T : FrameworkElement
        {
            c.ToolTip = tip; c.Height = 24; c.Margin = new Thickness(0, 0, gapAfter, 0);
            System.Windows.Automation.AutomationProperties.SetName(c, name);
            DockPanel.SetDock(c, Dock.Left); bar.Children.Add(c); return c;
        }
        void Sep() { var r = new System.Windows.Shapes.Rectangle { Width = 1, Margin = new Thickness(4, 3, 6, 3) }; r.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "App.Border"); DockPanel.SetDock(r, Dock.Left); bar.Children.Add(r); }
        Button Btn(object content, string tip, string name, Action a, double gap = 2)
        {
            var b = Add(new Button { Content = content, Padding = new Thickness(7, 0, 7, 0) }, tip, name, gap);
            b.SetResourceReference(FrameworkElement.StyleProperty, "ToolButton"); b.Click += (_, _) => a(); return b;
        }
        System.Windows.Controls.Primitives.ToggleButton Tog(object content, string tip, string name, bool on, Action<bool> set, double gap = 2)
        {
            var t = Add(new System.Windows.Controls.Primitives.ToggleButton { Content = content, IsChecked = on, Padding = new Thickness(7, 0, 7, 0) }, tip, name, gap);
            t.SetResourceReference(FrameworkElement.StyleProperty, "ToolToggle"); t.Click += (_, _) => set(t.IsChecked == true); return t;
        }
        // The gizmo tools (select / rotate / move / scale) live on the viewport bar (AttachTimelineUi).
        var keyContent = new StackPanel { Orientation = Orientation.Horizontal };
        var diamond = new System.Windows.Shapes.Path { Width = 10, Height = 10, Data = System.Windows.Media.Geometry.Parse("M5,0 L10,5 L5,10 L0,5 Z"), VerticalAlignment = VerticalAlignment.Center };
        diamond.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Timeline.RotationKey");
        keyContent.Children.Add(diamond);
        keyContent.Children.Add(new TextBlock { Text = "Key", FontSize = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Btn(keyContent, "Insert key (K): key every track of the selected keyframed meshes at the playhead", "Insert key", () => InsertKeys(null));
        Btn(Glyph(""), "Delete the selected keys (Delete)", "Delete selected keys", Surface.DeleteSelectedKeys);
        Btn(Glyph("", 10), "Expand all: show the key rows of every object", "Expand all", Surface.ExpandAll, 6);
        Sep();
        var ak = Tog(new TextBlock { Text = "Auto-key", FontSize = 12 }, VfxModule.AutoKeyTip, "Auto-key", VfxGizmoPrefs.AutoKey, v => VfxGizmoPrefs.AutoKey = v);
        var ls = Tog(new TextBlock { Text = "Local axes", FontSize = 12 }, VfxModule.LocalTip, "Local axes", VfxGizmoPrefs.LocalSpace, v => VfxGizmoPrefs.LocalSpace = v);
        VfxGizmoPrefs.OnChangedWhileAlive(this, () => { ak.IsChecked = VfxGizmoPrefs.AutoKey; ls.IsChecked = VfxGizmoPrefs.LocalSpace; });
        var ctx = new ContextMenu();
        foreach (var (h, ch) in new (string, VfxKeyChannel?)[] { ("Insert key (all tracks)", null), ("Insert translation key", VfxKeyChannel.Translation), ("Insert rotation key", VfxKeyChannel.Rotation), ("Insert scale key", VfxKeyChannel.Scale) })
        { var mi = new MenuItem { Header = h, InputGestureText = ch is null ? "K" : "" }; mi.Click += (_, _) => InsertKeys(ch); ctx.Items.Add(mi); }
        ctx.Items.Add(new Separator());
        var del = new MenuItem { Header = "Delete keys at playhead" }; del.Click += (_, _) => DeleteKeysAtPlayhead(); ctx.Items.Add(del);
        Surface.ContextMenu = ctx;
        var hint = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "Shift at drag start: edit every frame of per-frame data. Alt: drag keys between frames." };
        hint.ToolTip = hint.Text;
        hint.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        bar.Children.Add(hint);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(Surface);
        doc.SceneChanged += (_, _) => bar.IsEnabled = !doc.IsOlderVersion;
        bar.IsEnabled = !doc.IsOlderVersion;
        Loaded += (_, _) => VfxModule.AttachGizmo(doc);
    }

    /// <summary>Inserts a key at the playhead on <paramref name="channel"/> (null = all channels) of every selected keyframed mesh; one undo step.</summary>
    public void InsertKeys(VfxKeyChannel? channel)
    {
        if (_doc.IsOlderVersion) return;
        int tick = (int)MathF.Round(_doc.TimelineFrame) * VfxTimelineEdits.TicksPerFrame;
        var sections = _doc.Selection.Sections.Where(i => i < _doc.Current.Sections.Length && _doc.Current.Sections[i] is VfxMesh { Keys: not null }).ToList();
        if (sections.Count == 0) return;
        var chans = channel is { } c ? [c] : new[] { VfxKeyChannel.Translation, VfxKeyChannel.Rotation, VfxKeyChannel.Scale };
        _doc.Apply("Insert key", f => { foreach (int i in sections) foreach (var ch in chans) f = VfxTimelineEdits.InsertKey(f, i, ch, tick); return f; });
    }

    /// <summary>Deletes the keys at the playhead tick on every track of the selected keyframed meshes; one undo step.</summary>
    public void DeleteKeysAtPlayhead()
    {
        if (_doc.IsOlderVersion) return;
        int tick = (int)MathF.Round(_doc.TimelineFrame) * VfxTimelineEdits.TicksPerFrame;
        var f = _doc.Current;
        var refs = _doc.Selection.Sections.Where(i => i < f.Sections.Length && f.Sections[i] is VfxMesh { Keys: not null })
            .SelectMany(i => new[] { VfxKeyChannel.Translation, VfxKeyChannel.Rotation, VfxKeyChannel.Scale }
                .Where(ch => VfxTimelineEdits.KeyTimes((VfxMesh)f.Sections[i], ch).Contains(tick)).Select(ch => (i, ch, tick))).ToList();
        if (refs.Count > 0) _doc.Apply("Delete keys at playhead", g => VfxTimelineEdits.DeleteKeys(g, refs));
    }

    /// <summary>Moves the playhead to the previous/next key time of the selected objects (all keys when nothing is selected).</summary>
    public void JumpKey(int dir)
    {
        var f = _doc.Current; float now = MathF.Round(_doc.TimelineFrame * VfxTimelineEdits.TicksPerFrame);
        var sel = _doc.Selection.IsEmpty ? Enumerable.Range(0, f.Sections.Length) : _doc.Selection.Sections;
        var times = sel.Where(i => i < f.Sections.Length).Select(i => f.Sections[i]).OfType<VfxMesh>()
            .SelectMany(m => new[] { VfxKeyChannel.Translation, VfxKeyChannel.Rotation, VfxKeyChannel.Scale }.SelectMany(ch => VfxTimelineEdits.KeyTimes(m, ch))).Distinct().ToList();
        var next = dir > 0 ? times.Where(t => t > now).DefaultIfEmpty(-1).Min() : times.Where(t => t < now).DefaultIfEmpty(-1).Max();
        if (next >= 0) _doc.SeekFrame(next / (float)VfxTimelineEdits.TicksPerFrame);
    }
}

/// <summary>
/// Gizmo toggles shared by every effect: the single source of truth for the Effect menu, the viewport bar, the timeline
/// toolbar, the status item and <see cref="VfxGizmoTarget"/>. Persisted as settings "vfx.autoKey" / "vfx.localAxes".
/// </summary>
public static class VfxGizmoPrefs
{
    private static ModuleSettings? _store;
    private static bool _auto = true, _local;
    public static event EventHandler? Changed;

    public static bool AutoKey { get => _auto; set => Set(ref _auto, value, "autoKey"); }
    public static bool LocalSpace { get => _local; set => Set(ref _local, value, "localAxes"); }
    /// <summary>Gizmo drags on keyframed meshes move/rotate the pivot, compensating the keys (<see cref="Timeline.VfxPivotEdits"/>).</summary>
    public static bool PivotMode { get => _pivot; set => Set(ref _pivot, value, "pivotMode"); }
    private static bool _pivot;

    /// <summary>Status bar text, e.g. "Auto-key on, Local axes".</summary>
    public static string StatusText => $"Auto-key {(AutoKey ? "on" : "off")}, {(LocalSpace ? "Local" : "World")} axes{(PivotMode ? ", pivot mode" : "")}";

    private static void Set(ref bool field, bool value, string key)
    {
        if (field == value) return;
        field = value;
        _store?.Set(key, value);
        Changed?.Invoke(null, EventArgs.Empty);
        WeakOwners.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in WeakOwners.ToList())
            if (w.TryGetTarget(out var owner) && WeakHandlers.TryGetValue(owner, out var handlers)) foreach (var h in handlers.ToList()) h();
    }

    private static readonly List<WeakReference<object>> WeakOwners = [];
    private static readonly ConditionalWeakTable<object, List<Action>> WeakHandlers = new();

    /// <summary>Calls <paramref name="onChanged"/> after every change for as long as <paramref name="owner"/> lives, without keeping
    /// it alive (per-document views and panels subscribe here, so a closed effect can be collected).</summary>
    public static void OnChangedWhileAlive(object owner, Action onChanged)
    {
        if (!WeakHandlers.TryGetValue(owner, out var handlers)) { handlers = []; WeakHandlers.Add(owner, handlers); WeakOwners.Add(new(owner)); }
        handlers.Add(onChanged);
    }

    /// <summary>The module's settings store (null before the module loads, e.g. in isolated tests); also holds the pane widths.</summary>
    internal static ModuleSettings? Store => _store;

    internal static void Load(ModuleSettings store)
    {
        _store = store;
        _auto = store.Get("autoKey", true);
        _local = store.Get("localAxes", false);
        _pivot = store.Get("pivotMode", false);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
