using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Cairn.Viewport;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Viewport;

/// <summary>Display settings for the effect preview (not persisted yet).</summary>
public sealed class VfxDisplaySettings() : ViewportDisplaySettingsBase("vfx.viewport");

/// <summary>
/// The effect preview: a <see cref="ViewportSurface"/> with the <see cref="VfxSceneRenderer"/> scene and a
/// 2D overlay (dummy axes + names, light radius rings, emitter boxes, selection box). Click selects.
/// </summary>
public sealed class VfxViewport : ViewportSurface
{
    private readonly VfxDocument _doc;
    private readonly VfxSceneRenderer _renderer;
    private readonly OverlayLayer _overlay;
    private readonly Stopwatch _watch = new();
    private double _lastRender;

    public VfxViewport(VfxDocument doc, TextureService textures)
    {
        _doc = doc;
        Display = new VfxDisplaySettings();
        _renderer = new VfxSceneRenderer(doc, textures);
        Models.Add(_renderer.Root);
        _overlay = new OverlayLayer(this);
        OverlayLayers.Add(_overlay);
        _renderer.TexturesChanged += (_, _) => Invalidate();
        doc.FrameChanged += (_, _) => Invalidate();
        doc.VisibilityChanged += (_, _) => Invalidate();
        doc.Selection.Changed += (_, _) => Invalidate();
        doc.SceneChanged += (_, _) => { _renderer.Rebuild(); Invalidate(); };
        Render += (_, _) => OnRender();
        FrameRequested += (_, _) => FrameAll();
        Clicked += (_, p) => OnClicked(p);
        Loaded += (_, _) => { if (!Camera.HasBeenFramed) FrameAll(); };
        _watch.Start();
        System.Windows.Automation.AutomationProperties.SetName(this, "Effect preview");
        ToolTip = null;
    }

    public VfxSceneRenderer Renderer => _renderer;

    private void OnRender()
    {
        double start = _watch.Elapsed.TotalMilliseconds;
        _renderer.Update(Camera);
        _overlay.InvalidateVisual();
        double end = _watch.Elapsed.TotalMilliseconds;
        Stats.Add(end - start, start - _lastRender);
        _lastRender = start;
    }

    /// <summary>Frames all geometry over a few sampled frames (meshes, then dummies/lights/emitters).</summary>
    public void FrameAll()
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        float keep = (float)_doc.Playback.Time;
        foreach (float f in new[] { 0f, _doc.EndFrame * 0.25f, _doc.EndFrame * 0.5f, _doc.EndFrame * 0.75f })
        {
            _doc.SeekFrame(f);
            _renderer.Update(Camera);
            if (_renderer.TryGetBounds(-1, out var a, out var b)) { min = Vector3.Min(min, a); max = Vector3.Max(max, b); }
        }
        _doc.Playback.Seek(keep);
        var s = _doc.Sampler;
        for (int i = 0; i < s.Dummies.Count; i++) { var p = s.SampleDummy(i, 0).Position; min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        if (min.X > max.X) { min = new(-1); max = new(1); }
        var pad = Vector3.Max(max - min, new Vector3(0.2f)) * 0.05f;
        Camera.FrameBox(min - pad, max + pad, ActualWidth > 0 ? ActualWidth / Math.Max(1, ActualHeight) : 1.6);
        Camera.HasBeenFramed = true;
        Camera.NotifyChanged();
        Invalidate();
    }

    // ---- vertex mode: pick, marquee (Alt+drag; Ctrl toggles, Shift adds) ----
    private Rect? _marquee;
    private Point _marqueeStart;

    /// <summary>Screen positions of the vertex-mode mesh's vertices at the shown frame (same frame as the renderer).</summary>
    internal List<(double X, double Y, double Depth, bool Visible)> ScreenVertices()
    {
        var vm = VertexEditing.VfxVertexMode.Of(_doc);
        var list = new List<(double, double, double, bool)>();
        if (!vm.IsActive || _doc.HiddenSections.Contains(vm.Section)) return list;
        foreach (var w in VertexEditing.VfxVertexEdits.EffectPositions(_doc.Sampler, vm.Section, _doc.State.Frame))
            list.Add(Project(w, out var sp, out var depth) ? (sp.X, sp.Y, depth, true) : (0, 0, 0, false));
        return list;
    }

    protected override void OnMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        var vm = VertexEditing.VfxVertexMode.Of(_doc);
        if (vm.IsActive && e.ChangedButton == System.Windows.Input.MouseButton.Left && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0)
        {
            Focus();
            _marqueeStart = e.GetPosition(this);
            _marquee = new Rect(_marqueeStart, _marqueeStart);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_marquee is not null) { _marquee = new Rect(_marqueeStart, e.GetPosition(this)); Invalidate(); return; }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_marquee is { } r)
        {
            _marquee = null;
            ReleaseMouseCapture();
            var mods = System.Windows.Input.Keyboard.Modifiers;
            VertexEditing.VfxVertexMode.Of(_doc).Select(VertexEditing.VfxVertexEdits.InRect(ScreenVertices(), r.Left, r.Top, r.Right, r.Bottom).ToList(),
                add: (mods & System.Windows.Input.ModifierKeys.Shift) != 0, toggle: (mods & System.Windows.Input.ModifierKeys.Control) != 0);
            Invalidate();
            e.Handled = true;
            return;
        }
        base.OnMouseUp(e);
    }

    private void OnClicked(Point p)
    {
        var vmode = VertexEditing.VfxVertexMode.Of(_doc);
        if (vmode.IsActive)
        {
            int v = VertexEditing.VfxVertexEdits.Pick(ScreenVertices(), p.X, p.Y);
            var mods = System.Windows.Input.Keyboard.Modifiers;
            bool ctrl = (mods & System.Windows.Input.ModifierKeys.Control) != 0, shift = (mods & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (v >= 0) vmode.Select([v], add: shift, toggle: ctrl);
            else if (!ctrl && !shift) vmode.Select([]);
            return;
        }
        var ray = RayAt(p);
        int best = _renderer.Pick(ray, out _);
        if (best < 0)
        {
            // fall back to the nearest dummy/light marker on screen
            double bestPx = 12;
            var s = _doc.Sampler; var f = _doc.Sampler.File;
            for (int i = 0; i < s.Dummies.Count; i++)
                if (Project(s.SampleDummy(i, _doc.State.Frame).Position, out var sp, out _) && (sp - p).Length < bestPx) { bestPx = (sp - p).Length; best = VfxSections.SectionIndex<VfxDummy>(f, i); }
            for (int i = 0; i < s.Lights.Count; i++)
                if (Project(s.SampleLight(i, _doc.State.Frame).Position, out var sp, out _) && (sp - p).Length < bestPx) { bestPx = (sp - p).Length; best = VfxSections.SectionIndex<VfxLight>(f, i); }
        }
        _doc.Selection.Select(best, (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0);
    }

    private sealed class OverlayLayer : FrameworkElement
    {
        private readonly VfxViewport _v;
        public OverlayLayer(VfxViewport v) { _v = v; IsHitTestVisible = false; }

        protected override void OnRender(DrawingContext dc)
        {
            var doc = _v._doc; var s = doc.Sampler; float frame = doc.State.Frame;
            var accent = TryFindResource("App.Accent") as Brush ?? Brushes.Orange;
            var text = TryFindResource("App.Text") as Brush ?? Brushes.White;
            var accentPen = new Pen(accent, 1.5);
            var face = new Typeface("Segoe UI");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 0; i < s.Dummies.Count; i++)
            {
                int section = VfxSections.SectionIndex<VfxDummy>(s.File, i);
                if (doc.HiddenSections.Contains(section)) continue;
                var d = s.SampleDummy(i, frame);
                if (!_v.Project(d.Position, out var o, out _)) continue;
                float len = 0.15f * (float)Math.Max(0.1, _v.Camera.Distance * 0.15);
                (Vector3 axis, Color c)[] axes = [(Vector3.UnitX, Colors.IndianRed), (Vector3.UnitY, Colors.LimeGreen), (Vector3.UnitZ, Colors.DodgerBlue)];
                foreach (var (axis, c) in axes)
                    if (_v.Project(d.Position + Vector3.Transform(axis, d.Orientation) * len, out var e, out _)) dc.DrawLine(new Pen(new SolidColorBrush(c), doc.Selection.Contains(section) ? 2.5 : 1.2), o, e);
                dc.DrawText(new FormattedText(s.Dummies[i].Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 11, text, dpi), o + new System.Windows.Vector(4, 2));
            }
            for (int i = 0; i < s.Lights.Count; i++)
            {
                int section = VfxSections.SectionIndex<VfxLight>(s.File, i);
                if (doc.HiddenSections.Contains(section)) continue;
                var l = s.SampleLight(i, frame);
                if (!_v.Project(l.Position, out var o, out _)) continue;
                var col = new SolidColorBrush(Color.FromRgb(Byte(l.Color.X), Byte(l.Color.Y), Byte(l.Color.Z)));
                double r = _v.Project(l.Position + _v.Camera.Right * l.Radius, out var e, out _) ? (e - o).Length : 0;
                dc.DrawEllipse(null, new Pen(col, doc.Selection.Contains(section) ? 2 : 1) { DashStyle = DashStyles.Dash }, o, r, r);
                dc.DrawEllipse(col, null, o, 3, 3);
            }
            for (int i = 0; i < s.ParticleSystems.Count; i++)
            {
                int section = VfxSections.SectionIndex<VfxParticleSystem>(s.File, i);
                if (doc.HiddenSections.Contains(section)) continue;
                var em = s.SampleEmitter(i, frame - s.ParticleSystems[i].StartTime);
                if (_v.Project(em.Position, out var o, out _))
                    dc.DrawRectangle(null, new Pen(doc.Selection.Contains(section) ? accent : text, 1) { DashStyle = DashStyles.Dot }, new Rect(o.X - 5, o.Y - 5, 10, 10));
            }
            var vm = VertexEditing.VfxVertexMode.Of(doc);
            if (!vm.Enabled || !vm.IsActive)
            {
                foreach (int section in doc.Selection.Sections)
                    if (_v._renderer.TryGetBounds(section, out var a, out var b)) DrawBox(dc, accentPen, a, b);
            }
            if (vm.Enabled) DrawVertices(dc, vm, accent, text, face, dpi);
            if (_v._marquee is { } mr) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 80, 160, 255)), new Pen(accent, 1) { DashStyle = DashStyles.Dash }, mr);
        }

        /// <summary>Vertex mode: faint wireframe + vertex points (selected in the accent colour, with a dark outline for contrast).</summary>
        private void DrawVertices(DrawingContext dc, VertexEditing.VfxVertexMode vm, Brush accent, Brush text, Typeface face, double dpi)
        {
            if (vm.Unavailable is { } why)
            {
                dc.DrawText(new FormattedText(why, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 12, text, dpi), new Point(10, 10));
                return;
            }
            if (vm.Mesh is not { } m) return;
            var pts = _v.ScreenVertices();
            if (pts.Count == 0) return;
            var wire = new Pen(new SolidColorBrush(Color.FromArgb(70, 200, 220, 255)), 1); wire.Freeze();
            foreach (var f in m.Faces)
            {
                if ((uint)f.V0 >= pts.Count || (uint)f.V1 >= pts.Count || (uint)f.V2 >= pts.Count) continue;
                var a = pts[f.V0]; var b = pts[f.V1]; var c = pts[f.V2];
                if (!a.Visible || !b.Visible || !c.Visible) continue;
                dc.DrawLine(wire, new Point(a.X, a.Y), new Point(b.X, b.Y));
                dc.DrawLine(wire, new Point(b.X, b.Y), new Point(c.X, c.Y));
                dc.DrawLine(wire, new Point(c.X, c.Y), new Point(a.X, a.Y));
            }
            var outline = new Pen(Brushes.Black, 1); outline.Freeze();
            var plain = new SolidColorBrush(Color.FromRgb(230, 235, 245)); plain.Freeze();
            for (int i = 0; i < pts.Count; i++)
            {
                if (!pts[i].Visible) continue;
                bool sel = vm.Selected.Contains(i);
                dc.DrawEllipse(sel ? accent : plain, outline, new Point(pts[i].X, pts[i].Y), sel ? 4 : 2.5, sel ? 4 : 2.5);
            }
        }

        private static byte Byte(float v) => (byte)Math.Clamp(v <= 1 ? v * 255 : v, 0, 255);

        private void DrawBox(DrawingContext dc, Pen pen, Vector3 a, Vector3 b)
        {
            Span<Point> p = stackalloc Point[8];
            for (int i = 0; i < 8; i++)
                if (!_v.Project(new Vector3((i & 1) != 0 ? b.X : a.X, (i & 2) != 0 ? b.Y : a.Y, (i & 4) != 0 ? b.Z : a.Z), out p[i], out _)) return;
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) dc.DrawLine(pen, p[i], p[i | bit]);
        }
    }
}
