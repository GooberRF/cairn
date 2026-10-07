using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Numerics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Cairn.Viewport;

/// <summary>A world-space ray (origin and unit direction).</summary>
public readonly record struct WorldRay(Vector3 Origin, Vector3 Direction);

/// <summary>
/// A reusable 3D viewport: a <see cref="Viewport3D"/> driven by an <see cref="OrbitCamera"/>, the
/// <see cref="GridLayer"/> behind the caller's models, 2D <see cref="OverlayLayers"/> that share the
/// camera projection, and a <see cref="GizmoLayer"/> on top. Rendering is on demand: <see cref="Invalidate"/>
/// marks the view dirty and the next <see cref="CompositionTarget.Rendering"/> raises <see cref="Render"/>
/// once, then redraws the layers.
/// </summary>
/// <remarks>
/// Mouse: left-drag orbits, middle-drag or Shift+left-drag pans, the wheel zooms towards the cursor; a
/// left press on a gizmo handle drags the gizmo. Keys: F frames (<see cref="FrameRequested"/>), Home resets,
/// 1 / 3 / 7 front / side / top, 5 toggles perspective, Esc cancels a gizmo drag.
/// </remarks>
public class ViewportSurface : Grid
{
    private enum DragMode { None, Orbit, Pan, Gizmo }

    private readonly Viewport3D _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera _perspective = new() { FieldOfView = OrbitCamera.FieldOfViewDegrees };
    private readonly OrthographicCamera _orthographic = new();
    private readonly Model3DGroup _lights = new();
    private readonly Model3DGroup _models = new();
    private readonly Grid _overlayHost = new() { IsHitTestVisible = false };
    private ViewportDisplaySettingsBase? _display;
    private DragMode _drag;
    private Point _lastMouse, _dragStart;
    private bool _dragMoved, _dirty, _hooked;

    public ViewportSurface()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "Viewport.Background");
        AutomationProperties.SetName(this, "3D viewport");
        ToolTip = "Left-drag: orbit · Middle or Shift+left-drag: pan · Wheel: zoom · F: frame · Home: reset";
        ToolTipService.SetInitialShowDelay(this, 1500);
        _viewport.Children.Add(new ModelVisual3D { Content = _lights });
        _viewport.Children.Add(new ModelVisual3D { Content = _models });
        Grid.Camera = Camera;
        Gizmos.Camera = Camera;
        Children.Add(Grid);
        Children.Add(_viewport);
        Children.Add(_overlayHost);
        Children.Add(Gizmos);
        Models.CollectionChanged += OnModelsChanged;
        OverlayLayers.CollectionChanged += OnOverlaysChanged;
        Camera.Changed += (_, _) => Invalidate();
        UpdateLights();
        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
    }

    /// <summary>The camera; changing it and calling <see cref="OrbitCamera.NotifyChanged"/> redraws.</summary>
    public OrbitCamera Camera { get; } = new();

    /// <summary>The ground grid (behind the 3D scene).</summary>
    public GridLayer Grid { get; } = new();

    /// <summary>The gizmo layer (on top); set <see cref="GizmoLayer.Controller"/> to show a gizmo.</summary>
    public GizmoLayer Gizmos { get; } = new();

    /// <summary>The caller's models, drawn in order.</summary>
    public ObservableCollection<Model3D> Models { get; } = [];

    /// <summary>2D layers over the 3D scene, bottom to top (each draws with <see cref="CreateProjector"/>).</summary>
    public ObservableCollection<FrameworkElement> OverlayLayers { get; } = [];

    /// <summary>Frame timings; <see cref="Render"/> handlers may fill the counters.</summary>
    public ViewportStats Stats { get; } = new();

    /// <summary>Raised once per dirty frame, before the layers redraw: update models here.</summary>
    public event EventHandler? Render;

    /// <summary>F pressed: the caller frames what it shows (e.g. <see cref="OrbitCamera.FrameBox"/>), or ignores it.</summary>
    public event EventHandler? FrameRequested;

    /// <summary>Home pressed: the caller resets its view; unhandled, the camera returns to the default orbit.</summary>
    public event EventHandler? ResetRequested;

    /// <summary>A left click that did not drag (and missed the gizmo), in surface coordinates.</summary>
    public event EventHandler<Point>? Clicked;

    /// <summary>The display toggles followed (grid, background, projection, fullbright); null = defaults.</summary>
    public ViewportDisplaySettingsBase? Display
    {
        get => _display;
        set
        {
            if (ReferenceEquals(_display, value)) return;
            if (_display is not null) _display.Changed -= OnDisplayChanged;
            _display = value;
            if (_display is not null) _display.Changed += OnDisplayChanged;
            OnDisplayChanged(this, EventArgs.Empty);
        }
    }

    /// <summary>Marks the view dirty: the next rendering tick raises <see cref="Render"/> and redraws.</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>The projection the 2D layers use for the current size.</summary>
    public Projector CreateProjector() => new(Camera, RenderSize);

    /// <summary>Projects a world point to surface coordinates; false when behind the camera.</summary>
    public bool Project(Vector3 world, out Point screen, out double depth) =>
        Camera.Project(world, RenderSize, Camera.ViewProjection(RenderSize), out screen, out depth);

    /// <summary>The world ray under a surface point (for picking).</summary>
    public WorldRay RayAt(Point screen)
    {
        var vp = Camera.ViewProjection(RenderSize);
        if (!vp.HasInverse || RenderSize.Width < 1 || RenderSize.Height < 1) return new WorldRay(Camera.Eye, Camera.Forward);
        vp.Invert();
        double x = screen.X / RenderSize.Width * 2 - 1, y = 1 - screen.Y / RenderSize.Height * 2;
        var a = Unproject(vp, x, y, 0);
        var b = Unproject(vp, x, y, 1);
        var dir = b - a;
        return dir.LengthSquared() > 0 ? new WorldRay(a, Vector3.Normalize(dir)) : new WorldRay(Camera.Eye, Camera.Forward);
    }

    private static Vector3 Unproject(Matrix3D inverse, double x, double y, double z)
    {
        var p = inverse.Transform(new Point4D(x, y, z, 1));
        return Math.Abs(p.W) < 1e-12 ? Vector3.Zero : new Vector3((float)(p.X / p.W), (float)(p.Y / p.W), (float)(p.Z / p.W));
    }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
        _dirty = on;
    }

    private long _lastTick;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_dirty) return;
        _dirty = false;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Render?.Invoke(this, EventArgs.Empty);
        UpdateCamera();
        Grid.InvalidateVisual();
        foreach (var layer in OverlayLayers) layer.InvalidateVisual();
        Gizmos.InvalidateVisual();
        long end = System.Diagnostics.Stopwatch.GetTimestamp();
        double interval = _lastTick == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(_lastTick, end).TotalMilliseconds;
        _lastTick = end;
        Stats.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, interval);
    }

    private void UpdateCamera()
    {
        Camera.IsPerspective = _display?.Perspective ?? true;
        var eye = Camera.Eye;
        var position = new Point3D(eye.X, eye.Y, eye.Z);
        var look = new Vector3D(Camera.Forward.X, Camera.Forward.Y, Camera.Forward.Z);
        var up = new Vector3D(Camera.Up.X, Camera.Up.Y, Camera.Up.Z);
        // The Viewport3D cameras use the same view and projection matrices the 2D layers project with.
        var view = new MatrixCamera(Camera.ViewMatrix, Camera.ProjectionMatrix(RenderSize.Width / Math.Max(1, RenderSize.Height)));
        _perspective.Position = _orthographic.Position = position;
        _perspective.LookDirection = _orthographic.LookDirection = look;
        _perspective.UpDirection = _orthographic.UpDirection = up;
        _viewport.Camera = view;
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        Grid.ShowGrid = _display?.ShowGrid ?? true;
        if (_display is not null && ViewportDisplaySettingsBase.BackgroundColor(_display.Background) is { } colour)
            Background = new SolidColorBrush(colour);
        else
            SetResourceReference(BackgroundProperty, "Viewport.Background");
        UpdateLights();
        Invalidate();
    }

    private void UpdateLights()
    {
        _lights.Children.Clear();
        if (_display?.FullBright == true)
        {
            _lights.Children.Add(new AmbientLight(Colors.White));
            return;
        }
        _lights.Children.Add(new AmbientLight(Color.FromRgb(0x60, 0x60, 0x60)));
        _lights.Children.Add(new DirectionalLight(Color.FromRgb(0xA0, 0xA0, 0xA0), new Vector3D(-0.4, -0.8, -0.45)));
        _lights.Children.Add(new DirectionalLight(Color.FromRgb(0x40, 0x40, 0x40), new Vector3D(0.5, 0.2, 0.6)));
    }

    private void OnModelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _models.Children.Clear();
        foreach (var model in Models) _models.Children.Add(model);
        Invalidate();
    }

    private void OnOverlaysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _overlayHost.Children.Clear();
        foreach (var layer in OverlayLayers) _overlayHost.Children.Add(layer);
        Invalidate();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Invalidate();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _dragStart = _lastMouse = e.GetPosition(this);
        _dragMoved = false;
        if (e.ChangedButton == MouseButton.Left && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && Gizmos.TryBeginDrag(_dragStart))
            _drag = DragMode.Gizmo;
        else if (e.ChangedButton == MouseButton.Left)
            _drag = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? DragMode.Pan : DragMode.Orbit;
        else if (e.ChangedButton == MouseButton.Middle)
            _drag = DragMode.Pan;
        else
            return;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_drag == DragMode.Gizmo)
        {
            Gizmos.Drag(p, Keyboard.Modifiers);
            Invalidate();
            return;
        }
        if (_drag == DragMode.None)
        {
            if (Gizmos.UpdateHover(p)) Invalidate();
            return;
        }
        var delta = p - _lastMouse;
        _lastMouse = p;
        if ((p - _dragStart).Length > 3) _dragMoved = true;
        if (!_dragMoved) return;
        if (_drag == DragMode.Orbit)
        {
            Camera.Yaw -= delta.X * 0.4;
            Camera.Pitch += delta.Y * 0.4;
        }
        else
        {
            double scale = Camera.UnitsPerPixel(RenderSize);
            Camera.Target += Camera.Right * (float)(-delta.X * scale) + Camera.Up * (float)(delta.Y * scale);
        }
        Camera.NotifyChanged();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragMode.None) return;
        if (_drag == DragMode.Gizmo)
        {
            if (e.ChangedButton == MouseButton.Left) Gizmos.EndDrag();
        }
        else if (!_dragMoved && e.ChangedButton == MouseButton.Left)
        {
            Clicked?.Invoke(this, e.GetPosition(this));
        }
        _drag = DragMode.None;
        ReleaseMouseCapture();
        Invalidate();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Gizmos.ClearHover();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double factor = Math.Pow(0.85, e.Delta / 120.0);
        // Zoom towards the point under the cursor: move the target along with the distance.
        var focus = Camera.PointOnFocalPlane(e.GetPosition(this), RenderSize);
        Camera.Target = focus + (Camera.Target - focus) * (float)factor;
        Camera.Distance *= factor;
        Camera.NotifyChanged();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.F: FrameRequested?.Invoke(this, EventArgs.Empty); break;
            case Key.Home:
                if (ResetRequested is { } reset) reset(this, EventArgs.Empty);
                else
                {
                    Camera.Target = new Vector3(0, 1, 0);
                    Camera.Distance = 4;
                    Camera.SetView(0, 15);
                }
                break;
            case Key.D1 or Key.NumPad1: Camera.SetView(0, 0); break;
            case Key.D3 or Key.NumPad3: Camera.SetView(90, 0); break;
            case Key.D7 or Key.NumPad7: Camera.SetView(0, 89.5); break;
            case Key.D5 or Key.NumPad5:
                if (_display is not null) _display.Perspective = !_display.Perspective;
                else Camera.IsPerspective = !Camera.IsPerspective;
                Invalidate();
                break;
            case Key.Escape when _drag == DragMode.Gizmo:
                Gizmos.CancelDrag();
                _drag = DragMode.None;
                ReleaseMouseCapture();
                Invalidate();
                break;
            default: return;
        }
        e.Handled = true;
    }
}