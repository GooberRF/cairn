using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Assets;

namespace Cairn.Rfa.Ui.Viewport;

/// <summary>Frame-time figures for the report and the diagnostics switch.</summary>
public sealed class ViewportStats
{
    private readonly double[] _update = new double[240];
    private readonly double[] _interval = new double[240];
    private int _count;
    private int _next;

    /// <summary>Vertices drawn.</summary>
    public int Vertices { get; internal set; }

    /// <summary>Bytes allocated inside the skinning and vertex-write path since the last reset.</summary>
    public long SkinAllocatedBytes { get; internal set; }

    /// <summary>Bytes allocated inside pose sampling (ClipSampler + FK) since the last reset.</summary>
    public long PoseAllocatedBytes { get; internal set; }

    internal void Add(double updateMs, double intervalMs)
    {
        _update[_next] = updateMs;
        _interval[_next] = intervalMs;
        _next = (_next + 1) % _update.Length;
        _count = Math.Min(_count + 1, _update.Length);
    }

    /// <summary>Frames recorded (up to 240).</summary>
    public int Frames => _count;

    /// <summary>Mean ms per frame spent on pose sampling, skinning, vertex upload and overlay.</summary>
    public double MeanUpdateMs => _count == 0 ? 0 : _update.Take(_count).Average();

    /// <summary>Worst update in the window.</summary>
    public double MaxUpdateMs => _count == 0 ? 0 : _update.Take(_count).Max();

    /// <summary>Mean interval between rendered frames (1000 / fps).</summary>
    public double MeanIntervalMs => _count < 2 ? 0 : _interval.Take(_count).Where(i => i > 0).DefaultIfEmpty(0).Average();

    public void Reset()
    {
        _count = 0;
        _next = 0;
        SkinAllocatedBytes = 0;
        PoseAllocatedBytes = 0;
    }
}

/// <summary>
/// The 3D viewport used by both document kinds: a <see cref="Viewport3D"/> with a
/// <c>MatrixCamera</c> fed by <see cref="OrbitCamera"/>, the grid behind it and the overlay
/// above it (both projecting with the same matrices), camera navigation, bone picking, and a render
/// loop on <see cref="CompositionTarget.Rendering"/> that runs only while something changed.
/// </summary>
/// <remarks>
/// Its DataContext is an <see cref="IViewportHost"/> (a <see cref="DocumentViewModel"/>, or a dialog's
/// <see cref="ViewportPreviewHost"/>); it renders the host's <see cref="IViewportHost.Scene"/>.
/// Mouse: left-drag orbits, middle-drag or Shift+left-drag pans, the wheel zooms towards the cursor,
/// a click picks a bone (Ctrl+click toggles it in the selection; in a mesh tab a click also picks a
/// collision sphere or prop point, and clicking again cycles through what is under it). Keys (when focused): F frames,
/// 1/3/7 (or the numpad) front/side/top, 5 toggles perspective, Space plays, Left/Right step.
/// Library entries dropped on it become the document's preview partner.
/// </remarks>
public partial class ViewportControl : UserControl
{
    /// <summary>Every live viewport (the screenshot switch flushes them before capturing).</summary>
    private static readonly List<WeakReference<ViewportControl>> Live = [];

    private readonly MeshRenderer _renderer = new();
    private SceneViewModel? _scene;
    private IViewportHost? _host;
    private DocumentViewModel? _document;
    private bool _meshDirty;
    private bool _poseDirty;
    private bool _cameraDirty;
    // The automatic first framing: the scene, the viewport size it was made for and the camera it produced.
    // The shell creates document views lazily, so the first mesh build can run before the viewport has its
    // final size; until the user moves the camera, a size change frames the mesh again for the new size.
    private (SceneViewModel Scene, Size Size, Vector3 Target, double Distance, double Yaw, double Pitch)? _autoFrame;
    private bool _overlayDirty;
    private bool _subscribed;
    private TimeSpan _lastRenderingTime;
    private long _lastFrameTicks;
    private Point _dragStart;
    private Point _lastMouse;
    private DragMode _drag;
    private bool _dragMoved;

    private enum DragMode { None, Orbit, Pan, Gizmo }

    public ViewportControl()
    {
        InitializeComponent();
        SceneVisual.Content = _renderer.Root;
        DataContextChanged += (_, _) => Attach(DataContext as IViewportHost);
        SizeChanged += (_, _) =>
        {
            ReframeForNewSize();
            MarkCamera();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) MarkAll(); };
        Loaded += (_, _) =>
        {
            if (RfaUi.Theme is { } theme) theme.ThemeChanged += OnThemeChanged;
            MarkAll();
        };
        Unloaded += (_, _) =>
        {
            if (RfaUi.Theme is { } theme) theme.ThemeChanged -= OnThemeChanged;
            Unsubscribe();
        };
        Root.MouseDown += OnMouseDown;
        Root.MouseMove += OnMouseMove;
        Root.MouseUp += OnMouseUp;
        Root.MouseWheel += OnMouseWheel;
        Root.MouseLeave += (_, _) =>
        {
            if (_drag != DragMode.Gizmo) Gizmos.ClearHover();
            ClearPickHover();
        };
        DragOver += OnDragOver;
        Drop += OnDrop;
        lock (Live) Live.Add(new WeakReference<ViewportControl>(this));
        GridLayer.Camera = null;
        InitPoseEditing();
    }

    /// <summary>Frame-time figures since the last <see cref="ViewportStats.Reset"/>.</summary>
    public ViewportStats Stats { get; } = new();

    /// <summary>Applies any pending update to every visible viewport now (before a screenshot).</summary>
    public static void FlushAll()
    {
        lock (Live)
        {
            foreach (var weak in Live.ToList())
            {
                if (!weak.TryGetTarget(out var control))
                {
                    Live.Remove(weak);
                    continue;
                }
                if (control.IsVisible) control.UpdateNow();
            }
        }
    }

    /// <summary>The visible viewports (diagnostics).</summary>
    public static IReadOnlyList<ViewportControl> Visible()
    {
        lock (Live)
        {
            return [.. Live.Select(w => w.TryGetTarget(out var c) ? c : null).OfType<ViewportControl>().Where(c => c.IsVisible)];
        }
    }

    /// <summary>The frame-time figures of the measured update path.</summary>
    public int VertexCount => _renderer.VertexCount;

    private void Attach(IViewportHost? host)
    {
        if (_scene is not null)
        {
            _scene.MeshChanged -= OnMeshChanged;
            _scene.PoseChanged -= OnPoseChanged;
            _scene.OverlayChanged -= OnOverlayChanged;
            _scene.FrameRequested -= OnFrameRequested;
            _scene.PropertyChanged -= OnScenePropertyChanged;
            _scene.Camera.Changed -= OnCameraChanged;
            WeakEventManager<ViewModels.ViewportDisplaySettings, EventArgs>.RemoveHandler(_scene.Display, nameof(ViewModels.ViewportDisplaySettings.Changed), OnDisplayChanged);
        }
        _host = host;
        _document = host as DocumentViewModel;
        var document = _document;
        _scene = host?.Scene;
        Overlay.Scene = _scene;
        Overlay.Camera = _scene?.Camera;
        GridLayer.Camera = _scene?.Camera;
        if (_scene is not null)
        {
            _scene.MeshChanged += OnMeshChanged;
            _scene.PoseChanged += OnPoseChanged;
            _scene.OverlayChanged += OnOverlayChanged;
            _scene.FrameRequested += OnFrameRequested;
            _scene.PropertyChanged += OnScenePropertyChanged;
            _scene.Camera.Changed += OnCameraChanged;
            // weak: the display settings belong to the workspace, which outlives every document and view
            WeakEventManager<ViewModels.ViewportDisplaySettings, EventArgs>.AddHandler(_scene.Display, nameof(ViewModels.ViewportDisplaySettings.Changed), OnDisplayChanged);
            _scene.Camera.IsPerspective = _scene.Display.Perspective;
        }
        AttachPoseEditing(document);
        UpdateNotice();
        MarkAll();
    }

    // ── Change tracking and the render loop ──────────────────────────────────

    private void OnMeshChanged(object? sender, EventArgs e)
    {
        _meshDirty = true;
        Request();
    }

    private void OnPoseChanged(object? sender, EventArgs e)
    {
        _poseDirty = true;
        Request();
    }

    private void OnOverlayChanged(object? sender, EventArgs e) => MarkOverlay();

    private void OnCameraChanged(object? sender, EventArgs e) => MarkCamera();

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (_scene is null) return;
        if (_scene.Camera.IsPerspective != _scene.Display.Perspective)
        {
            _scene.Camera.IsPerspective = _scene.Display.Perspective;
            _cameraDirty = true;
        }
        // Mesh mode and lighting change the models; the rest only the 2D layers.
        _meshDirty = true;
        UpdateBackground();
        UpdateNotice();
        MarkAll();
    }

    private void OnScenePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SceneViewModel.Notice) or nameof(SceneViewModel.NoticeIsWarning)) UpdateNotice();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Overlay.ResetResources();
        Gizmos.ResetResources();
        GridLayer.ResetPens();
        _renderer.ClearTextureCache();
        UpdateBackground();
        MarkAll();
    }

    private void MarkAll()
    {
        _meshDirty = true;
        _cameraDirty = true;
        _overlayDirty = true;
        Request();
    }

    private void MarkCamera()
    {
        _cameraDirty = true;
        Request();
    }

    private void MarkOverlay()
    {
        _overlayDirty = true;
        Request();
    }

    private void Request()
    {
        if (_subscribed || !IsLoaded) return;
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // WPF can raise Rendering more than once per frame; do the work once.
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = args.RenderingTime;
        }
        if (!IsVisible)
        {
            Unsubscribe();
            return;
        }
        UpdateNow();
        if (!_meshDirty && !_poseDirty && !_cameraDirty && !_overlayDirty && _host?.Playback.IsPlaying != true) Unsubscribe();
    }

    /// <summary>Applies every pending change: geometry, skinning, camera, overlays.</summary>
    private void UpdateNow()
    {
        if (_scene is not { } scene)
        {
            _meshDirty = _poseDirty = _cameraDirty = _overlayDirty = false;
            return;
        }
        long start = Stopwatch.GetTimestamp();
        bool measured = _poseDirty && !_meshDirty;
        if (_meshDirty)
        {
            _meshDirty = false;
            _poseDirty = false;
            _renderer.Build(scene, this);
            Stats.Vertices = _renderer.VertexCount;
            if (!scene.Camera.HasBeenFramed && scene.Mesh is not null)
            {
                FrameAll(scene);
                var c = scene.Camera;
                _autoFrame = (scene, new Size(View.ActualWidth, View.ActualHeight), c.Target, c.Distance, c.Yaw, c.Pitch);
            }
            _overlayDirty = true;
            _cameraDirty = true;
            UpdateNotice();
        }
        else if (_poseDirty)
        {
            _poseDirty = false;
            long before = GC.GetAllocatedBytesForCurrentThread();
            _renderer.UpdatePose(scene);
            Stats.SkinAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            _overlayDirty = true;
        }
        if (_cameraDirty)
        {
            _cameraDirty = false;
            var size = new Size(Math.Max(1, View.ActualWidth), Math.Max(1, View.ActualHeight));
            Camera.ViewMatrix = scene.Camera.ViewMatrix;
            Camera.ProjectionMatrix = scene.Camera.ProjectionMatrix(size.Width / size.Height);
            _renderer.UpdateLighting(scene.Camera, scene.Display.FullBright);
            GridLayer.ShowGrid = scene.Display.ShowGrid;
            GridLayer.InvalidateVisual();
            _overlayDirty = true;
        }
        if (_overlayDirty)
        {
            _overlayDirty = false;
            GridLayer.ShowGrid = scene.Display.ShowGrid;
            GridLayer.InvalidateVisual();
            Overlay.InvalidateVisual();
            Gizmos.InvalidateVisual();
            BindBadge.Visibility = scene.Pose is not null && scene.ShowingBindPose && scene.Clip is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        if (measured)
        {
            long now = Stopwatch.GetTimestamp();
            // Pose sampling (done when the time changed) + skinning and vertex upload here + the overlay's last render.
            double updateMs = (now - start) * 1000.0 / Stopwatch.Frequency + scene.LastPoseMs + Overlay.LastRenderMs;
            double intervalMs = _lastFrameTicks == 0 ? 0 : (now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            _lastFrameTicks = now;
            Stats.Add(updateMs, intervalMs);
        }
    }

    private void UpdateBackground()
    {
        if (_scene is null) return;
        Brush? brush = _scene.Display.Background switch
        {
            ViewportBackground.Black => Brushes.Black,
            ViewportBackground.DarkGrey => new SolidColorBrush(Color.FromRgb(0x30, 0x31, 0x36)),
            ViewportBackground.MidGrey => new SolidColorBrush(Color.FromRgb(0x6E, 0x70, 0x76)),
            ViewportBackground.LightGrey => new SolidColorBrush(Color.FromRgb(0xC8, 0xCA, 0xCE)),
            ViewportBackground.White => Brushes.White,
            _ => null,
        };
        if (brush is null) Root.SetResourceReference(Panel.BackgroundProperty, "Viewport.Background");
        else
        {
            if (brush.CanFreeze) brush.Freeze();
            Root.Background = brush;
        }
    }

    private void UpdateNotice()
    {
        UpdateBackground();
        var scene = _scene;
        string? text = scene?.Notice;
        if (text is null && scene is not null && scene.Mesh is null && _document is not null) text = null;
        if (text is null)
        {
            NoticeBox.Visibility = Visibility.Collapsed;
            return;
        }
        NoticeText.Text = text;
        bool warning = scene!.NoticeIsWarning;
        NoticeGlyph.Text = warning ? "" : "";
        NoticeGlyph.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Severity.Warning" : "Severity.Info");
        NoticeBox.Visibility = Visibility.Visible;
    }

    // ── Framing ──────────────────────────────────────────────────────────────

    private void OnFrameRequested(object? sender, bool selection)
    {
        if (_scene is null) return;
        if (_meshDirty) UpdateNow();
        if (selection && _scene.Selection.Count > 0) FrameSelection(_scene);
        else FrameAll(_scene);
    }

    /// <summary>Frames the automatically framed mesh again when the viewport got a new size before the user moved the camera.</summary>
    private void ReframeForNewSize()
    {
        if (_autoFrame is not { } f) return;
        var c = f.Scene.Camera;
        if (!ReferenceEquals(f.Scene, _scene) || c.Target != f.Target || c.Distance != f.Distance || c.Yaw != f.Yaw || c.Pitch != f.Pitch)
        {
            _autoFrame = null;
            return;
        }
        var size = new Size(View.ActualWidth, View.ActualHeight);
        if (size.Width <= 1 || size.Height <= 1 || size == f.Size) return;
        FrameAll(f.Scene);
        _autoFrame = (f.Scene, size, c.Target, c.Distance, c.Yaw, c.Pitch);
    }

    private void FrameAll(SceneViewModel scene)
    {
        // Everything that may be on screen: the skinned mesh, the mesh in its bind pose (a mesh built before
        // its pose arrives is briefly skinned with identity bones, which shrank tall meshes in the glTF import
        // previews), and the skeleton's joints.
        (Vector3 Min, Vector3 Max)? bounds = null;
        void Add((Vector3 Min, Vector3 Max)? b)
        {
            if (b is not { } v) return;
            bounds = bounds is { } a ? (Vector3.Min(a.Min, v.Min), Vector3.Max(a.Max, v.Max)) : v;
        }
        Add(_renderer.Bounds());
        Add(_renderer.BindBounds());
        if (scene.Pose is { } pose && pose.World.Length > 0)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var w in pose.World)
            {
                if (!float.IsFinite(w.Position.X) || !float.IsFinite(w.Position.Y) || !float.IsFinite(w.Position.Z)) continue;
                min = Vector3.Min(min, w.Position);
                max = Vector3.Max(max, w.Position);
            }
            if (min.X <= max.X) Add((min, max));
        }
        if (bounds is not { } b) return;
        // The whole box on screen, in both fields of view, and clear of the toolbar along the top (in a short
        // dialog preview the toolbar is a quarter of the half-height).
        double height = View.ActualHeight, width = View.ActualWidth;
        double aspect = height > 1 && width > 1 ? width / height : 1.6;
        double toolbar = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight + Toolbar.Margin.Top + 8 : 40;
        double fill = height > 1 ? Math.Clamp((height / 2 - toolbar) / (height / 2), 0.45, 0.86) : 0.86;
        scene.Camera.FrameBox(b.Min, b.Max, aspect, fill);
    }

    private void FrameSelection(SceneViewModel scene)
    {
        if (scene.Pose is not { } pose) return;
        var points = scene.Selection.Bones.Where(i => i < pose.World.Length).Select(i => pose.World[i].Position).ToList();
        // Include the selected bones' children so a single joint frames its whole bone.
        var parents = scene.Skeleton.EffectiveParents;
        for (int i = 0; i < pose.World.Length; i++)
        {
            if (parents[i] >= 0 && scene.Selection.Contains(parents[i])) points.Add(pose.World[i].Position);
        }
        if (points.Count == 0) return;
        var min = points.Aggregate(Vector3.Min);
        var max = points.Aggregate(Vector3.Max);
        float radius = Math.Max(0.15f, (max - min).Length() * 0.5f);
        scene.Camera.Frame((min + max) * 0.5f, radius);
    }

    private void OnFrameClick(object sender, RoutedEventArgs e)
    {
        if (_scene is null) return;
        _scene.RequestFrame(_scene.Selection.Count > 0);
        Focus();
    }

    // ── View menu ────────────────────────────────────────────────────────────

    private void OnViewMenuClick(object sender, RoutedEventArgs e)
    {
        if (_scene is null) return;
        var display = _scene.Display;
        var menu = new ContextMenu { PlacementTarget = ViewMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Radio(string header, bool isChecked, Action click, string tip)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked, ToolTip = tip };
            item.Click += (_, _) => click();
            return item;
        }
        menu.Items.Add(Radio("_Textured", display.MeshMode == MeshDisplayMode.Textured, () => display.MeshMode = MeshDisplayMode.Textured, "Draw the mesh with its textures"));
        menu.Items.Add(Radio("_Flat", display.MeshMode == MeshDisplayMode.Flat, () => display.MeshMode = MeshDisplayMode.Flat, "Draw the mesh in one neutral colour"));
        menu.Items.Add(Radio("_Hidden", display.MeshMode == MeshDisplayMode.Off, () => display.MeshMode = MeshDisplayMode.Off, "Do not draw the mesh (skeleton only)"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Radio("Full _bright", display.FullBright, () => display.FullBright = !display.FullBright, "Unlit: the textures' own colours, without the headlight's shading"));
        var background = new MenuItem { Header = "B_ackground" };
        foreach (var (value, label) in ViewportDisplaySettings.BackgroundChoices)
        {
            var v = value;
            background.Items.Add(Radio(label, display.Background == v, () => display.Background = v, "Viewport background colour"));
        }
        menu.Items.Add(background);
        menu.Items.Add(GhostMenuItem(display));
        menu.Items.Add(new Separator());
        var front = new MenuItem { Header = "F_ront view", InputGestureText = "1", ToolTip = "Look at the front of the model (from +Z)" };
        front.Click += (_, _) => _scene.Camera.SetView(0, 0);
        var side = new MenuItem { Header = "_Side view", InputGestureText = "3", ToolTip = "Look at the model's side (from +X)" };
        side.Click += (_, _) => _scene.Camera.SetView(90, 0);
        var top = new MenuItem { Header = "T_op view", InputGestureText = "7", ToolTip = "Look straight down" };
        top.Click += (_, _) => _scene.Camera.SetView(0, 89.5);
        menu.Items.Add(front);
        menu.Items.Add(side);
        menu.Items.Add(top);
        menu.IsOpen = true;
    }

    // ── Mouse ────────────────────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_scene is null) return;
        if (e.OriginalSource is DependencyObject d && IsInToolbar(d)) return;
        Focus();
        _dragStart = _lastMouse = e.GetPosition(Root);
        _dragMoved = false;
        if (e.ChangedButton == MouseButton.Left && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && TryBeginGizmoDrag(_dragStart))
        {
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left)
            _drag = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? DragMode.Pan : DragMode.Orbit;
        else if (e.ChangedButton == MouseButton.Middle)
            _drag = DragMode.Pan;
        else
            return;
        Root.CaptureMouse();
        e.Handled = true;
    }

    private bool IsInToolbar(DependencyObject d)
    {
        for (var p = d; p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (ReferenceEquals(p, Toolbar)) return true;
            if (ReferenceEquals(p, Root)) return false;
        }
        return false;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_scene is null) return;
        var p = e.GetPosition(Root);
        if (_drag == DragMode.Gizmo)
        {
            Gizmos.Drag(p, Keyboard.Modifiers);
            return;
        }
        if (_drag == DragMode.None)
        {
            if (UpdateGizmoHover(p)) return;
            if (_document is MeshDocumentViewModel)
            {
                UpdateMeshPickHover(p);
                return;
            }
            int hover = Overlay.Pick(p);
            if (hover != Overlay.HoverBone)
            {
                Overlay.HoverBone = hover;
                Root.Cursor = hover >= 0 ? Cursors.Hand : null;
                MarkOverlay();
            }
            return;
        }
        var delta = p - _lastMouse;
        _lastMouse = p;
        if ((p - _dragStart).Length > 3) _dragMoved = true;
        if (!_dragMoved) return;
        var camera = _scene.Camera;
        if (_drag == DragMode.Orbit)
        {
            camera.Yaw -= delta.X * 0.4;
            camera.Pitch += delta.Y * 0.4;
        }
        else
        {
            double scale = camera.UnitsPerPixel(new Size(Root.ActualWidth, Root.ActualHeight));
            camera.Target += camera.Right * (float)(-delta.X * scale) + camera.Up * (float)(delta.Y * scale);
        }
        camera.NotifyChanged();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        if (_drag == DragMode.Gizmo)
        {
            if (e.ChangedButton == MouseButton.Left) EndGizmoDrag();
            e.Handled = true;
            return;
        }
        bool click = !_dragMoved && e.ChangedButton == MouseButton.Left;
        _drag = DragMode.None;
        Root.ReleaseMouseCapture();
        if (click && _scene is not null && _document is MeshDocumentViewModel)
        {
            // Mesh tabs: joints, collision spheres and prop points (click again to cycle through overlapping ones).
            ClickInMesh(e.GetPosition(Root), Keyboard.Modifiers);
        }
        else if (click && _scene is not null)
        {
            int bone = Overlay.Pick(e.GetPosition(Root));
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _scene.Selection.Toggle(bone);
            else _scene.Selection.Select(bone);
            // The document brings up the picked bone's editor (structure node, Bone inspector).
            if (bone >= 0 && _scene.Selection.Contains(bone)) _scene.NotifyBonePicked(bone);
        }
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scene is null) return;
        var camera = _scene.Camera;
        var size = new Size(Root.ActualWidth, Root.ActualHeight);
        double factor = Math.Pow(0.85, e.Delta / 120.0);
        // Zoom towards the point under the cursor: move the target along with the distance.
        var focus = camera.PointOnFocalPlane(e.GetPosition(Root), size);
        camera.Target = focus + (camera.Target - focus) * (float)factor;
        camera.Distance *= factor;
        camera.NotifyChanged();
        e.Handled = true;
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // A visible frame when focus arrived from the keyboard (Tab); a click into the viewport needs none.
        if (ReferenceEquals(e.NewFocus, this) && InputManager.Current.MostRecentInputDevice is KeyboardDevice)
            FocusFrame.Visibility = Visibility.Visible;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        FocusFrame.Visibility = Visibility.Collapsed;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_scene is null || e.Handled) return;
        // Typing into a box inside the viewport (the layer-edit options popup) is text, not camera keys.
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (HandlePoseKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var camera = _scene.Camera;
        switch (e.Key)
        {
            case Key.Space:
                _host?.Playback.TogglePlay();
                break;
            case Key.F:
                _scene.RequestFrame(_scene.Selection.Count > 0);
                break;
            case Key.D1 or Key.NumPad1:
                camera.SetView(0, 0);
                break;
            case Key.D3 or Key.NumPad3:
                camera.SetView(90, 0);
                break;
            case Key.D7 or Key.NumPad7:
                camera.SetView(0, 89.5);
                break;
            case Key.D5 or Key.NumPad5:
                _scene.Display.Perspective = !_scene.Display.Perspective;
                break;
            case Key.Left:
                _host?.Playback.StepBackCommand.Execute(null);
                break;
            case Key.Right:
                _host?.Playback.StepForwardCommand.Execute(null);
                break;
            case Key.Home:
                _host?.Playback.FirstCommand.Execute(null);
                break;
            case Key.End:
                _host?.Playback.LastCommand.Execute(null);
                break;
            case Key.Escape:
                _scene.Selection.Clear();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ── Drag and drop from the library ───────────────────────────────────────

    /// <summary>The drag format the library uses for its entries.</summary>
    public const string LibraryDragFormat = "RfaWorkbench.LibraryItem";

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var item = e.Data.GetData(LibraryDragFormat);
        bool ok = (item is LibraryMesh { HasSkeleton: true } && _document is ClipDocumentViewModel)
            || (item is LibraryClip { IsReadable: true } && _document is MeshDocumentViewModel { HasSkeleton: true });
        if (item is not null)
        {
            e.Effects = ok ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        switch (e.Data.GetData(LibraryDragFormat))
        {
            case LibraryMesh mesh when _document is ClipDocumentViewModel clipDocument && mesh.HasSkeleton:
                clipDocument.UsePreviewMesh(mesh);
                e.Handled = true;
                break;
            case LibraryClip clip when _document is MeshDocumentViewModel meshDocument:
                meshDocument.UsePreviewClip(clip);
                e.Handled = true;
                break;
        }
    }
}
