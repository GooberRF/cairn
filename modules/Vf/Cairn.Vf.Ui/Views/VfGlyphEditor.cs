using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Vf.Rendering;
using Cairn.Vf.Ui.Documents;

namespace Cairn.Vf.Ui.Views;

/// <summary>
/// The selected glyph enlarged, as a small pixel editor: the left button paints <see cref="PaintValue"/> (or clears
/// when <see cref="Eraser"/> is on), the right button clears. A stroke (button down to up) is one undo step. Pixel
/// grid lines show from 6× zoom.
/// </summary>
public sealed class VfGlyphEditor : FrameworkElement
{
    private readonly VfDocument _doc;
    private ImageSource? _image;
    private int _glyph = -1, _zoom = 1, _width, _height;
    private bool _painting, _erasing;

    public VfGlyphEditor(VfDocument doc)
    {
        _doc = doc;
        Focusable = false;
        Cursor = Cursors.Pen;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SnapsToDevicePixels = true;
        ToolTip = "Left button: paint the value chosen below. Right button: clear. Each stroke is one undo step.";
    }

    /// <summary>The raw value the left button paints.</summary>
    public int PaintValue { get; set; } = 14;

    /// <summary>True: the left button clears too.</summary>
    public bool Eraser { get; set; }

    /// <summary>The zoom in use.</summary>
    public int Zoom => _zoom;

    /// <summary>Shows glyph <paramref name="index"/> at <paramref name="zoom"/>.</summary>
    public void Show(int index, int zoom)
    {
        var f = _doc.Current;
        _glyph = index;
        _zoom = Math.Max(1, zoom);
        _width = index >= 0 && index < f.GlyphCount ? f.Glyphs[index].Width : 0;
        _height = Math.Max(0, f.Height);
        try { _image = _width > 0 && _height > 0 ? VfImages.ToSource(VfRender.Glyph(f, index)) : null; }
        catch (VfImageTooLargeException) { _image = null; }
        Width = Math.Max(1, _width) * _zoom;
        Height = Math.Max(1, _height) * _zoom;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, Math.Max(1, _width) * _zoom, Math.Max(1, _height) * _zoom);
        dc.DrawRectangle(TryFindResource(VfImages.BrushKey(_doc.Backdrop)) as Brush ?? Brushes.Black, null, rect);
        if (_image is not null) dc.DrawImage(_image, new Rect(0, 0, _width * _zoom, _height * _zoom));
        if (_zoom >= 6 && _width > 0)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)), 1);
            pen.Freeze();
            for (int x = 1; x < _width; x++) dc.DrawLine(pen, new Point(x * _zoom + 0.5, 0), new Point(x * _zoom + 0.5, rect.Height));
            for (int y = 1; y < _height; y++) dc.DrawLine(pen, new Point(0, y * _zoom + 0.5), new Point(rect.Width, y * _zoom + 0.5));
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right) || _width <= 0) return;
        _erasing = e.ChangedButton == MouseButton.Right || Eraser;
        _painting = true;
        CaptureMouse();
        _doc.BeginStroke();
        PaintAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_painting) PaintAt(e.GetPosition(this));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        EndStroke();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndStroke();
    }

    private void EndStroke()
    {
        if (!_painting) return;
        _painting = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _doc.EndStroke();
    }

    private void PaintAt(Point p)
    {
        int x = (int)Math.Floor(p.X / _zoom), y = (int)Math.Floor(p.Y / _zoom);
        _doc.PaintPixel(_glyph, x, y, _erasing ? 0 : PaintValue);
    }
}
