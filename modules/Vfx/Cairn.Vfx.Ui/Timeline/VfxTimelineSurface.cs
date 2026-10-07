using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Timeline;

/// <summary>
/// Custom-drawn dope sheet for an effect (drawing/zoom/marquee ported in spirit from RFA's TimelineSurface): ruler with
/// playhead (click/drag scrubs), one row per object with its active-range bar (drag = start, right edge = frame count),
/// T/R/S key rows for keyframed meshes, per-frame tick rows otherwise. Ctrl+wheel zooms, Shift+wheel / middle drag pans,
/// wheel scrolls rows. Keys: click, Ctrl/Shift-click, marquee; drag moves selected keys snapped to frames (Alt = free).
/// Delete, Ctrl+C / Ctrl+V (at the playhead) while focused.
/// </summary>
public sealed partial class VfxTimelineSurface : FrameworkElement
{
    public const double HeaderWidth = 190, RulerHeight = 22, RowHeight = 20;
    private readonly VfxDocument _doc;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Row> _rows = [];
    private double _pxPerFrame = 12, _panFrames = -1, _scrollY;
    private bool _fitted;
    private enum Drag { None, Scrub, Keys, Move, Resize, Marquee, Pan }
    private Drag _drag;
    private Point _down, _now;
    private VfxFile? _base;
    private int _dragSection;
    private float _dragStart, _dragLength;
    private List<VfxKeyRef> _dragKeys = [];
    private int _dragDelta;
    private List<(int, VfxKeyChannel, object)> _clip = [];

    private readonly record struct Row(int Section, VfxKeyChannel? Channel, bool Ticks, string Label, int Depth = 0, int Material = -1, int Curve = 0)
    {
        public bool IsObject => Channel is null && !Ticks && Material < 0;
    }

    /// <summary>Raised when a material curve row is clicked (the material is selected; the module shows the Material tab).</summary>
    public event Action<int>? MaterialRowClicked;

    public VfxTimelineSurface(VfxDocument doc)
    {
        _doc = doc;
        Focusable = true; ClipToBounds = true;
        doc.SceneChanged += (_, _) => { Rebuild(); InvalidateVisual(); };
        doc.FrameChanged += (_, _) => InvalidateVisual();
        doc.Selection.Changed += (_, _) => InvalidateVisual();
        Rebuild();
    }

    /// <summary>Rows currently shown (diagnostics/self-tests): section, channel (null = object row).</summary>
    public IEnumerable<(int Section, VfxKeyChannel? Channel)> Rows => _rows.Select(r => (r.Section, r.Channel));
    public void Expand(int section, bool on = true) { var n = NameAt(section); if (on) _expanded.Add(n); else _expanded.Remove(n); Rebuild(); InvalidateVisual(); }
    public void ExpandAll() { for (int i = 0; i < _doc.Current.Sections.Length; i++) _expanded.Add(NameAt(i)); Rebuild(); InvalidateVisual(); }
    private string NameAt(int i) => VfxSections.NameOf(_doc.Current.Sections[i]);
    private bool IsExpanded(int i) => _expanded.Contains(NameAt(i));

    /// <summary>Rows in outliner tree order (parents before children, depth-first, section order among siblings; objects whose parent is
    /// not in the file are roots); expanded state is kept by object name, so it survives edits and undo for this document.</summary>
    private void Rebuild()
    {
        _rows.Clear();
        var f = _doc.Current;
        var objects = Enumerable.Range(0, f.Sections.Length).Where(i => VfxTimelineEdits.Range(f.Sections[i]) is not null).ToList();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (int i in objects) byName.TryAdd(VfxSections.NameOf(f.Sections[i]), i);
        var children = objects.ToLookup(i => byName.TryGetValue(VfxSections.ParentOf(f.Sections[i]) ?? "", out var p) && p != i ? p : -1);
        var done = new HashSet<int>();
        void Add(int i, int depth)
        {
            if (!done.Add(i)) return;
            var s = f.Sections[i];
            _rows.Add(new(i, null, false, VfxSections.NameOf(s), depth));
            if (IsExpanded(i) && s is VfxMesh m)
            {
                if (m.Keys is not null)
                    foreach (var ch in new[] { VfxKeyChannel.Translation, VfxKeyChannel.Rotation, VfxKeyChannel.Scale }) _rows.Add(new(i, ch, false, ch.ToString(), depth));
                else if (m.Frames.Length > 1) _rows.Add(new(i, null, true, m.IsMorph ? "Morph frames" : "Frame transforms", depth));
            }
            if (IsExpanded(i)) AddMaterialRows(f, i, depth);
            foreach (int c in children[i]) Add(c, depth + 1);
        }
        foreach (int i in children[-1]) Add(i, 0);
        foreach (int i in objects) Add(i, 0); // parent cycles
    }

    /// <summary>Read-only material curve rows (opacity / self-illumination) under an expanded object; filled in by the material-row code.</summary>
    partial void AddMaterialRows(VfxFile f, int section, int depth);

    // ---- coordinates ----
    private double X(double frame) => HeaderWidth + (frame - _panFrames) * _pxPerFrame;
    private double FrameAt(double x) => (x - HeaderWidth) / _pxPerFrame + _panFrames;
    private double RowY(int row) => RulerHeight + row * RowHeight - _scrollY;
    private int RowAt(double y) => y < RulerHeight ? -1 : (int)Math.Floor((y - RulerHeight + _scrollY) / RowHeight);

    private static Brush B(FrameworkElement e, string key, Color fallback) => e.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private static readonly Brush TBrush = Frozen(Color.FromRgb(0xE8, 0x9A, 0x3C)), RBrush = Frozen(Color.FromRgb(0x4C, 0x9A, 0xE8)), SBrush = Frozen(Color.FromRgb(0x5C, 0xC0, 0x6A));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (!_fitted && w > HeaderWidth + 40) { _fitted = true; _pxPerFrame = Math.Clamp((w - HeaderWidth - 20) / Math.Max(4, _doc.EndFrame + 2), 2, 60); }
        var text = B(this, "App.Text", Colors.White); var sec = B(this, "App.SecondaryText", Colors.Gray);
        var border = new Pen(B(this, "App.Border", Colors.DimGray), 1); var accent = B(this, "App.Accent", Colors.OrangeRed);
        var bg = B(this, "App.PaneBackground", Color.FromRgb(30, 30, 30)); var panel = B(this, "App.ChromeBackground", Color.FromRgb(40, 40, 40));
        var selRow = new SolidColorBrush(Color.FromArgb(50, 0x4C, 0x9A, 0xE8));
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText T(string s, Brush b, double size = 11) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, dpi);
        int end = Math.Max(1, _doc.EndFrame);
        int step = _pxPerFrame >= 8 ? 1 : _pxPerFrame >= 3 ? 5 : 10, label = Math.Max(step, (int)Math.Ceiling(40 / _pxPerFrame / 5) * 5);
        // frame grid + ruler
        dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, 0, Math.Max(0, w - HeaderWidth), h)));
        dc.DrawRectangle(panel, null, new Rect(HeaderWidth, 0, w, RulerHeight));
        for (int fr = Math.Max(0, (int)FrameAt(HeaderWidth) / step * step); X(fr) < w; fr += step)
        {
            double x = Math.Round(X(fr)) + 0.5; bool major = fr % label == 0;
            dc.DrawLine(major ? border : new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1), new Point(x, major ? 0 : RulerHeight - 6), new Point(x, h));
            if (major) dc.DrawText(T(fr.ToString(CultureInfo.InvariantCulture), sec, 10), new Point(x + 2, 3));
        }
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)), null, new Rect(X(end), RulerHeight, Math.Max(0, w - X(end)), h));
        dc.Pop();
        // rows
        var f = _doc.Current; var sel = _doc.Selection;
        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, w, Math.Max(0, h - RulerHeight))));
        for (int r = 0; r < _rows.Count; r++)
        {
            double y = RowY(r); if (y > h || y + RowHeight < RulerHeight) continue;
            var row = _rows[r]; var s = f.Sections[row.Section];
            if (row.IsObject && sel.Contains(row.Section)) dc.DrawRectangle(selRow, null, new Rect(0, y, w, RowHeight));
            dc.DrawLine(border, new Point(0, y + RowHeight - 0.5), new Point(w, y + RowHeight - 0.5));
            // header
            double indent = row.Depth * 10 + (row.IsObject ? 4 : 22);
            if (row.IsObject && HasDetail(f, row.Section))
                dc.DrawText(T(IsExpanded(row.Section) ? "▾" : "▸", sec), new Point(indent, y + 2));
            var nameText = T(row.Label, row.IsObject ? text : sec); nameText.MaxTextWidth = Math.Max(10, HeaderWidth - indent - 52); nameText.MaxLineCount = 1; nameText.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(nameText, new Point(indent + 12, y + 3));
            if (row.IsObject && VfxTimelineEdits.KindOf(s) is VfxAnimKind.Static or VfxAnimKind.PerFrame)
                dc.DrawText(T("→key", accent, 10), new Point(HeaderWidth - 34, y + 4));
            dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, y, Math.Max(0, w - HeaderWidth), RowHeight)));
            if (row.Channel is { } ch && s is VfxMesh km)
            {
                var brush = ch switch { VfxKeyChannel.Translation => TBrush, VfxKeyChannel.Rotation => RBrush, _ => SBrush };
                foreach (int t in VfxTimelineEdits.KeyTimes(km, ch))
                {
                    var key = new VfxKeyRef(row.Section, ch, t);
                    int shown = t + (_drag == Drag.Keys && _dragKeys.Contains(key) ? _dragDelta : 0);
                    Diamond(dc, X(shown / 320.0), y + RowHeight / 2, sel.ContainsKey(key) ? accent : brush, border);
                }
            }
            else if (row.Ticks && s is VfxMesh tm && VfxTimelineEdits.Range(s) is { } tr)
            {
                double per = 15.0 / VfxTimelineEdits.Fps(tm);
                for (int k = 0; k < tm.Frames.Length; k++) { double x = X(tr.Start + k * per); dc.DrawLine(new Pen(sec, 1), new Point(x, y + 5), new Point(x, y + RowHeight - 5)); }
            }
            else if (row.Material >= 0) DrawMaterialCurve(dc, f, row.Material, row.Curve, y, row.Curve == 0 ? RBrush : TBrush);
            else if (VfxTimelineEdits.Range(s) is { } rg)
            {
                var (st, len) = _drag is Drag.Move or Drag.Resize && _dragSection == row.Section ? (_dragStart, _dragLength) : rg;
                var bar = new Rect(X(st), y + 4, Math.Max(3, len * _pxPerFrame), RowHeight - 8);
                var fill = s switch { VfxMesh => TBrush, VfxParticleSystem => SBrush, _ => RBrush };
                dc.DrawRoundedRectangle(fill, sel.Contains(row.Section) ? new Pen(text, 1) : null, bar, 3, 3);
                dc.DrawRectangle(text, null, new Rect(bar.Right - 3, bar.Top + 2, 2, bar.Height - 4));
            }
            dc.Pop();
        }
        dc.Pop();
        dc.DrawLine(border, new Point(HeaderWidth - 0.5, 0), new Point(HeaderWidth - 0.5, h));
        dc.DrawLine(border, new Point(0, RulerHeight - 0.5), new Point(w, RulerHeight - 0.5));
        if (_doc.IsOlderVersion) dc.DrawText(T("Older format: convert (Effect menu) to edit", sec, 10), new Point(4, 4));
        // playhead
        double px = X(_doc.TimelineFrame);
        if (px >= HeaderWidth) { dc.DrawLine(new Pen(accent, 1.5), new Point(px, 0), new Point(px, h)); dc.DrawRectangle(accent, null, new Rect(px - 4, 0, 8, 8)); }
        if (_drag == Drag.Marquee) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0x4C, 0x9A, 0xE8)), new Pen(RBrush, 1), new Rect(_down, _now));
    }

    private static void Diamond(DrawingContext dc, double x, double y, Brush fill, Pen outline)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(new Point(x, y - 5), true, true); c.PolyLineTo([new Point(x + 5, y), new Point(x, y + 5), new Point(x - 5, y)], true, false); }
        g.Freeze(); dc.DrawGeometry(fill, outline, g);
    }

    // ---- input ----
    private IEnumerable<VfxKeyRef> KeysIn(Rect rect)
    {
        for (int r = 0; r < _rows.Count; r++)
        {
            if (_rows[r].Channel is not { } ch || _doc.Current.Sections[_rows[r].Section] is not VfxMesh m) continue;
            double y = RowY(r) + RowHeight / 2;
            foreach (int t in VfxTimelineEdits.KeyTimes(m, ch))
                if (rect.Contains(new Point(X(t / 320.0), y))) yield return new VfxKeyRef(_rows[r].Section, ch, t);
        }
    }

    // ---- hit-testing API (self-tests drive the same paths as the mouse) ----
    /// <summary>Where a key's diamond is drawn (null when its row is not shown).</summary>
    internal Point? KeyPoint(VfxKeyRef k)
    {
        int r = _rows.FindIndex(x => x.Section == k.Section && x.Channel == k.Channel);
        return r < 0 ? null : new Point(X(k.Time / 320.0), RowY(r) + RowHeight / 2);
    }
    /// <summary>A point on an object's range bar at <paramref name="frame"/> (null when the object has no row).</summary>
    internal Point? ObjectRowPoint(int section, double frame)
    {
        int r = _rows.FindIndex(x => x.Section == section && x.IsObject);
        return r < 0 ? null : new Point(X(frame), RowY(r) + RowHeight / 2);
    }
    internal double PixelsPerFrame => _pxPerFrame;

    protected override void OnMouseDown(MouseButtonEventArgs e) { Focus(); PointerDown(e.GetPosition(this), Keyboard.Modifiers, e.ChangedButton); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag != Drag.None) PointerMove(e.GetPosition(this), Keyboard.Modifiers); }
    protected override void OnMouseUp(MouseButtonEventArgs e) => PointerUp();

    /// <summary>Mouse-down at <paramref name="p"/> (surface coordinates): scrub, key click/drag, range move/resize, marquee.</summary>
    internal void PointerDown(Point p, ModifierKeys mods, MouseButton button = MouseButton.Left)
    {
        _down = _now = p;
        if (button == MouseButton.Middle) { _drag = Drag.Pan; CaptureMouse(); return; }
        if (button != MouseButton.Left) return;
        bool add = (mods & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (_down.Y < RulerHeight && _down.X > HeaderWidth) { _drag = Drag.Scrub; Scrub(); CaptureMouse(); return; }
        int r = RowAt(_down.Y); if (r < 0 || r >= _rows.Count) { StartMarquee(add); return; }
        var row = _rows[r]; var s = _doc.Current.Sections[row.Section];
        if (row.Material >= 0) { _doc.Selection.Select(row.Material); MaterialRowClicked?.Invoke(row.Material); return; }
        if (_down.X < HeaderWidth)
        {
            if (row.IsObject)
            {
                if (_down.X < row.Depth * 10 + 18) { Expand(row.Section, !IsExpanded(row.Section)); return; }
                if (_down.X > HeaderWidth - 36 && VfxTimelineEdits.KindOf(s) is VfxAnimKind.Static or VfxAnimKind.PerFrame && !_doc.IsOlderVersion)
                { int i = row.Section; _doc.Apply("Convert to keyframes", f => VfxEdit.ToKeyframes(f, i)); return; }
            }
            _doc.Selection.Select(row.Section, (mods & ModifierKeys.Control) != 0);
            return;
        }
        if (row.Channel is { } ch)
        {
            var hit = KeysIn(new Rect(_down.X - 6, RowY(r), 12, RowHeight)).FirstOrDefault(k => k.Section == row.Section && k.Channel == ch);
            if (hit != default)
            {
                if ((mods & ModifierKeys.Control) != 0) { _doc.Selection.SelectKeys([hit], add: true); return; }
                if (!_doc.Selection.ContainsKey(hit)) _doc.Selection.SelectKeys([hit], add);
                if (_doc.IsOlderVersion) return;
                _drag = Drag.Keys; _dragKeys = [.. _doc.Selection.Keys]; _dragDelta = 0; _base = _doc.Current; _doc.BeginEdit("Move keys"); CaptureMouse(); return;
            }
        }
        else if (!row.Ticks && VfxTimelineEdits.Range(s) is { } rg && !_doc.IsOlderVersion && s is VfxMesh or VfxParticleSystem or VfxDummy or VfxLight or VfxSpacewarp)
        {
            double x0 = X(rg.Start), x1 = X(rg.Start + rg.Length);
            if (_down.X >= x0 - 2 && _down.X <= x1 + 4)
            {
                _doc.Selection.Select(row.Section, (mods & ModifierKeys.Control) != 0);
                bool resize = _down.X >= x1 - 6;
                if (!resize && s is not (VfxMesh or VfxParticleSystem)) return; // dummies/lights/warps start at 0
                _drag = resize ? Drag.Resize : Drag.Move; _dragSection = row.Section; (_dragStart, _dragLength) = rg; _base = _doc.Current;
                _doc.BeginEdit(resize ? "Change frame count" : "Move start time"); CaptureMouse(); return;
            }
        }
        StartMarquee(add);
    }

    private void StartMarquee(bool add) { if (!add) _doc.Selection.ClearKeys(); _drag = Drag.Marquee; CaptureMouse(); }
    private void Scrub() => _doc.SeekFrame((float)Math.Max(0, Math.Round(FrameAt(_now.X))));

    internal void PointerMove(Point p, ModifierKeys mods)
    {
        if (_drag == Drag.None) return;
        double dx = p.X - _now.X; _now = p;
        double frames = (_now.X - _down.X) / _pxPerFrame; bool free = (mods & ModifierKeys.Alt) != 0;
        switch (_drag)
        {
            case Drag.Scrub: Scrub(); break;
            case Drag.Pan: _panFrames -= dx / _pxPerFrame; break;
            case Drag.Keys when _base is { } b:
                _dragDelta = free ? (int)Math.Round(frames * 320) : (int)Math.Round(frames) * 320;
                var keys = _dragKeys.Select(k => (k.Section, k.Channel, k.Time)).ToList(); int d = _dragDelta;
                _doc.UpdateEdit(_ => VfxTimelineEdits.MoveKeys(b, keys, d)); break;
            case Drag.Move when _base is { } b:
                var rg = VfxTimelineEdits.Range(b.Sections[_dragSection])!.Value;
                _dragStart = (float)Math.Max(0, free ? rg.Start + frames : Math.Round(rg.Start + frames)); float st = _dragStart; int i = _dragSection;
                _doc.UpdateEdit(_ => VfxTimelineEdits.MoveRange(b, i, st)); break;
            case Drag.Resize when _base is { } b:
                var rr = VfxTimelineEdits.Range(b.Sections[_dragSection])!.Value;
                _dragLength = (float)Math.Max(1, Math.Round(rr.Length + frames)); float len = _dragLength; int j = _dragSection;
                _doc.UpdateEdit(_ => VfxTimelineEdits.ResizeRange(b, j, len)); break;
        }
        InvalidateVisual();
    }

    internal void PointerUp()
    {
        if (_drag == Drag.Marquee) _doc.Selection.SelectKeys(KeysIn(new Rect(_down, _now)), add: true);
        if (_drag == Drag.Keys)
        {
            _doc.CommitEdit();
            int d = _dragDelta; _doc.Selection.SelectKeys(_dragKeys.Select(k => k with { Time = Math.Max(0, k.Time + d) }));
        }
        else if (_drag is Drag.Move or Drag.Resize) _doc.CommitEdit();
        _drag = Drag.None; _base = null; _dragDelta = 0; ReleaseMouseCapture(); InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && CancelDrag()) { e.Handled = true; return; }
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Delete) { DeleteSelectedKeys(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C) { _clip = [.. VfxTimelineEdits.CopyKeys(_doc.Current, _doc.Selection.Keys.Select(k => (k.Section, k.Channel, k.Time)))]; e.Handled = true; }
        else if (ctrl && e.Key == Key.V && _clip.Count > 0 && !_doc.IsOlderVersion)
        { var clip = _clip; int tick = (int)Math.Round(_doc.TimelineFrame) * 320; _doc.Apply("Paste keys", f => VfxTimelineEdits.PasteKeys(f, clip, tick)); e.Handled = true; }
    }

    /// <summary>Escape during a key/range drag: restores the effect as it was at drag start.</summary>
    internal bool CancelDrag()
    {
        if (_drag is not (Drag.Keys or Drag.Move or Drag.Resize)) return false;
        _doc.CancelEdit(); _drag = Drag.None; _base = null; _dragDelta = 0; ReleaseMouseCapture(); InvalidateVisual(); return true;
    }

    /// <summary>Deletes the selected keys (a channel keeps its last key) as one undo step.</summary>
    public void DeleteSelectedKeys()
    {
        if (_doc.IsOlderVersion || _doc.Selection.Keys.Count == 0) return;
        var keys = _doc.Selection.Keys.Select(k => (k.Section, k.Channel, k.Time)).ToList();
        _doc.Selection.ClearKeys();
        _doc.Apply("Delete keys", f => VfxTimelineEdits.DeleteKeys(f, keys));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this); var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0) { double at = FrameAt(p.X); _pxPerFrame = Math.Clamp(_pxPerFrame * (e.Delta > 0 ? 1.2 : 1 / 1.2), 1, 80); _panFrames = at - (p.X - HeaderWidth) / _pxPerFrame; }
        else if ((mods & ModifierKeys.Shift) != 0) _panFrames -= e.Delta / 120.0 * 40 / _pxPerFrame;
        else _scrollY = Math.Clamp(_scrollY - e.Delta / 120.0 * RowHeight * 2, 0, Math.Max(0, _rows.Count * RowHeight - ActualHeight + RulerHeight + RowHeight));
        e.Handled = true; InvalidateVisual();
    }
}
