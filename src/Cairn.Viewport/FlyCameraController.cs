using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Ui.Modules;

namespace Cairn.Viewport;

/// <summary>
/// A fly camera for a <see cref="ViewportSurface"/>: while <see cref="IsEnabled"/>, dragging with the left or right
/// button turns the view about the eye (mouse look), W A S D move forward, left, back and right, Q and E move down and
/// up, Shift moves faster and Ctrl slower, and the wheel steps forward or back. A click without a drag raises
/// <see cref="Clicked"/> (the surface's own click is not raised while flying). Everything else (F, Home, 1 / 3 / 7, 5)
/// stays with the surface. While flying, the surface claims the movement keys (<see cref="KeyboardClaims"/>), so the
/// shell's shortcuts with the same letters (Ctrl+W closes a tab) give way to the camera.
/// </summary>
/// <remarks>
/// The surface keeps its <see cref="OrbitCamera"/>: flying moves the eye and keeps the orbit target
/// <see cref="LookDistance"/> ahead of it, so the near and far planes (which scale with the orbit distance) suit the
/// scene, and switching back to orbiting turns about the point the camera was looking at. Movement is integrated per
/// rendered frame while a movement key is held, so the speed does not depend on the key repeat rate.
/// </remarks>
public sealed class FlyCameraController : IDisposable
{
    /// <summary>Degrees the view turns per pixel of mouse movement.</summary>
    private const double LookSensitivity = 0.25;

    private readonly ViewportSurface _surface;
    private readonly HashSet<Key> _held = [];
    private bool _enabled, _looking, _moved, _ticking, _disposed;
    private Point _lastMouse, _downAt;
    private long _lastTick;
    private double _lookDistance = 10, _speed = 10;

    /// <param name="surface">The surface whose camera this controller flies.</param>
    public FlyCameraController(ViewportSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        surface.PreviewMouseDown += OnMouseDown;
        surface.PreviewMouseMove += OnMouseMove;
        surface.PreviewMouseUp += OnMouseUp;
        surface.PreviewMouseWheel += OnMouseWheel;
        surface.PreviewKeyDown += OnKeyDown;
        surface.PreviewKeyUp += OnKeyUp;
        surface.LostKeyboardFocus += OnLostFocus;
        surface.LostMouseCapture += OnLostCapture;
        surface.Unloaded += OnUnloaded;
    }

    /// <summary>Raised when <see cref="IsEnabled"/> changes.</summary>
    public event EventHandler? EnabledChanged;

    /// <summary>A left click that did not drag while flying, in surface coordinates.</summary>
    public event EventHandler<Point>? Clicked;

    /// <summary>The camera flown (the surface's).</summary>
    public OrbitCamera Camera => _surface.Camera;

    /// <summary>
    /// True while the mouse and the movement keys fly the camera. Turning it on keeps the eye where it is and moves the
    /// orbit target to <see cref="LookDistance"/> ahead; turning it off leaves the camera as it is.
    /// </summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            // While flying, the movement keys (with Shift or Ctrl too) belong to the camera, not to shell shortcuts.
            KeyboardClaims.SetMovementKeys(_surface, value);
            if (value) Reanchor();
            else Stop();
            EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>How far ahead of the eye the orbit target stays while flying, in metres (also sets the clip planes).</summary>
    public double LookDistance
    {
        get => _lookDistance;
        set
        {
            if (!(value > 0) || !double.IsFinite(value)) return;
            _lookDistance = value;
            if (_enabled) Reanchor();
        }
    }

    /// <summary>Movement speed in metres per second (Shift: four times, Ctrl: a quarter).</summary>
    public double Speed
    {
        get => _speed;
        set { if (value > 0 && double.IsFinite(value)) _speed = value; }
    }

    /// <summary>
    /// Keeps the eye where it is and puts the orbit target <see cref="LookDistance"/> ahead of it (after the camera was
    /// framed or reset while flying).
    /// </summary>
    public void Reanchor()
    {
        var camera = Camera;
        var eye = camera.Eye;
        camera.Distance = _lookDistance;
        camera.Target = eye - camera.Backward * (float)camera.Distance;
        camera.NotifyChanged();
    }

    /// <summary>Turns the view about the eye by <paramref name="yawDegrees"/> (positive turns right) and <paramref name="pitchDegrees"/> (positive looks down).</summary>
    public void Look(double yawDegrees, double pitchDegrees)
    {
        var camera = Camera;
        var eye = camera.Eye;
        camera.Yaw += yawDegrees;
        camera.Pitch += pitchDegrees;
        camera.Target = eye - camera.Backward * (float)camera.Distance;
        camera.NotifyChanged();
    }

    /// <summary>
    /// Moves the eye (and the target with it) by <paramref name="forward"/>, <paramref name="right"/> and
    /// <paramref name="up"/> metres: forward along the view, right along the screen, up along the world's Y axis.
    /// </summary>
    public void Move(double forward, double right, double up)
    {
        var camera = Camera;
        var delta = camera.Forward * (float)forward + camera.Right * (float)right + Vector3.UnitY * (float)up;
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z) || delta == Vector3.Zero) return;
        camera.Target += delta;
        camera.NotifyChanged();
    }

    /// <summary>Puts the eye at <paramref name="eye"/> looking along <paramref name="yaw"/> and <paramref name="pitch"/> (degrees, as <see cref="OrbitCamera"/>).</summary>
    public void PlaceEye(Vector3 eye, double yaw, double pitch)
    {
        var camera = Camera;
        camera.Yaw = yaw;
        camera.Pitch = pitch;
        camera.Distance = _lookDistance;
        camera.Target = eye - camera.Backward * (float)camera.Distance;
        camera.NotifyChanged();
    }

    private static bool IsMovementKey(Key key) => key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_enabled || e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        // Shift+left pans as in orbit mode; the surface handles it.
        if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        _surface.Focus();
        _looking = true;
        _moved = false;
        _downAt = _lastMouse = e.GetPosition(_surface);
        _surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_looking) return;
        var p = e.GetPosition(_surface);
        var delta = p - _lastMouse;
        _lastMouse = p;
        if ((p - _downAt).Length > 3) _moved = true;
        if (!_moved) return;
        Look(delta.X * LookSensitivity, delta.Y * LookSensitivity);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_looking || e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        _looking = false;
        bool click = !_moved && e.ChangedButton == MouseButton.Left;
        _surface.ReleaseMouseCapture();
        e.Handled = true;
        if (click) Clicked?.Invoke(this, e.GetPosition(_surface));
    }

    private void OnLostCapture(object sender, MouseEventArgs e) => _looking = false;

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_enabled) return;
        // One notch moves a third of a second's travel.
        Move(e.Delta / 120.0 * CurrentSpeed() / 3, 0, 0);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_enabled || !IsMovementKey(e.Key) || (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;
        _held.Add(e.Key);
        StartTicking();
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsMovementKey(e.Key)) return;
        _held.Remove(e.Key);
        if (_held.Count == 0) StopTicking();
        if (_enabled) e.Handled = true;
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Stop();

    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();

    private double CurrentSpeed()
    {
        var modifiers = Keyboard.Modifiers;
        double speed = _speed;
        if (modifiers.HasFlag(ModifierKeys.Shift)) speed *= 4;
        if (modifiers.HasFlag(ModifierKeys.Control)) speed /= 4;
        return speed;
    }

    private void StartTicking()
    {
        if (_ticking || _disposed) return;
        _ticking = true;
        _lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void Stop()
    {
        _held.Clear();
        StopTicking();
        if (_looking)
        {
            _looking = false;
            if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        // A long gap (the window was busy) moves no further than a tenth of a second would.
        double dt = Math.Min(0.1, System.Diagnostics.Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds);
        _lastTick = now;
        if (!_enabled || _held.Count == 0 || dt <= 0) return;
        double step = CurrentSpeed() * dt;
        double forward = (_held.Contains(Key.W) ? 1 : 0) - (_held.Contains(Key.S) ? 1 : 0);
        double right = (_held.Contains(Key.D) ? 1 : 0) - (_held.Contains(Key.A) ? 1 : 0);
        double up = (_held.Contains(Key.E) ? 1 : 0) - (_held.Contains(Key.Q) ? 1 : 0);
        Move(forward * step, right * step, up * step);
    }

    /// <summary>Stops flying and detaches from the surface.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        if (_enabled) KeyboardClaims.SetMovementKeys(_surface, false);
        _surface.PreviewMouseDown -= OnMouseDown;
        _surface.PreviewMouseMove -= OnMouseMove;
        _surface.PreviewMouseUp -= OnMouseUp;
        _surface.PreviewMouseWheel -= OnMouseWheel;
        _surface.PreviewKeyDown -= OnKeyDown;
        _surface.PreviewKeyUp -= OnKeyUp;
        _surface.LostKeyboardFocus -= OnLostFocus;
        _surface.LostMouseCapture -= OnLostCapture;
        _surface.Unloaded -= OnUnloaded;
    }
}
