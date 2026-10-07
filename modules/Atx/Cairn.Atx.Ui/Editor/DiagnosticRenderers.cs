using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Cairn.Atx.Linting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Cairn.Atx.Ui.Editor;

/// <summary>One squiggle: a span of text and the severity that colours it.</summary>
/// <param name="Offset">Start offset in the document.</param>
/// <param name="Length">Length in characters.</param>
/// <param name="Severity">Error, warning or info.</param>
/// <param name="Diagnostics">The problems reported on this span, for the hover tooltip.</param>
public sealed record SquiggleMarker(
    int Offset, int Length, DiagnosticSeverity Severity, IReadOnlyList<Diagnostic> Diagnostics)
    : ISegment
{
    int ISegment.Offset => Offset;

    int ISegment.Length => Length;

    int ISegment.EndOffset => Offset + Length;
}

/// <summary>
/// Draws the squiggly underlines. This is the design's "TextMarkerService": diagnostics are pushed
/// in after every lint pass and the renderer draws them under whatever text currently occupies
/// those offsets, so nothing has to track document changes.
/// </summary>
public sealed class SquiggleRenderer : IBackgroundRenderer
{
    private IReadOnlyList<SquiggleMarker> _markers = [];

    /// <summary>Brush for error squiggles.</summary>
    public Brush ErrorBrush { get; set; } = Brushes.Red;

    /// <summary>Brush for warning squiggles.</summary>
    public Brush WarningBrush { get; set; } = Brushes.Orange;

    /// <summary>Brush for informational squiggles.</summary>
    public Brush InfoBrush { get; set; } = Brushes.SteelBlue;

    /// <summary>Squiggles sit above the text background but below the caret and selection.</summary>
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>The current markers, ordered by offset.</summary>
    public IReadOnlyList<SquiggleMarker> Markers => _markers;

    /// <summary>Replaces the marker set and asks the view to repaint.</summary>
    public void SetMarkers(TextView? view, IReadOnlyList<SquiggleMarker> markers)
    {
        _markers = markers ?? [];
        view?.InvalidateLayer(Layer);
    }

    /// <summary>The markers covering <paramref name="offset"/>, for the hover tooltip.</summary>
    public IReadOnlyList<SquiggleMarker> MarkersAt(int offset) =>
        [.. _markers.Where(m => offset >= m.Offset && offset <= m.Offset + Math.Max(1, m.Length))];

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(textView);
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_markers.Count == 0 || !textView.VisualLinesValid) return;

        int viewStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        int viewEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;

        foreach (var marker in _markers)
        {
            if (marker.Offset > viewEnd || marker.Offset + marker.Length < viewStart) continue;
            var brush = marker.Severity switch
            {
                DiagnosticSeverity.Error => ErrorBrush,
                DiagnosticSeverity.Warning => WarningBrush,
                _ => InfoBrush,
            };
            var pen = new Pen(brush, 1.0);
            pen.Freeze();

            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
            {
                double width = Math.Max(rect.Width, 4);
                drawingContext.DrawGeometry(null, pen, Squiggle(rect.Left, rect.Bottom - 1, width));
            }
        }
    }

    /// <summary>A zig-zag line 3px wide per tooth, which reads as a squiggle at every DPI.</summary>
    private static Geometry Squiggle(double left, double bottom, double width)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(left, bottom), false, false);
            bool up = false;
            for (double x = left + 3; x < left + width; x += 3)
            {
                context.LineTo(new Point(x, bottom + (up ? 0 : -3)), true, false);
                up = !up;
            }
        }
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>
/// Softly highlights the selected frame's block, which is how the frames list shows the user where
/// they are in the source without moving the caret or taking focus.
/// </summary>
public sealed class BlockHighlightRenderer : IBackgroundRenderer
{
    private ISegment? _segment;

    /// <summary>Fill behind the highlighted block.</summary>
    public Brush Background { get; set; } = Brushes.Transparent;

    /// <summary>Outline of the highlighted block.</summary>
    public Brush BorderBrush { get; set; } = Brushes.Transparent;

    /// <summary>The highlight sits behind the text.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>Sets (or clears, with null) the highlighted span and repaints.</summary>
    public void SetSegment(TextView? view, int offset, int length)
    {
        _segment = length <= 0 ? null : new TextSegment { StartOffset = offset, Length = length };
        view?.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(textView);
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_segment is null || !textView.VisualLinesValid) return;

        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 3 };
        builder.AddSegment(textView, _segment);
        var geometry = builder.CreateGeometry();
        if (geometry is null) return;

        var pen = new Pen(BorderBrush, 1);
        pen.Freeze();
        drawingContext.DrawGeometry(Background, pen, geometry);
    }
}
