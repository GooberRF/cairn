using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Text;
using Cairn.Ui.Services;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using ParsedTable = Cairn.Tbl.Model.TblDocument;

namespace Cairn.Tbl.Ui.Editor;

/// <summary>
/// The table colours of the active theme: <c>Tbl.Dark.*</c> / <c>Tbl.Light.*</c> colours from the module's
/// resources (Themes/TblResources.xaml), one brush per <see cref="TblTextClass"/>.
/// </summary>
public sealed class TblPalette
{
    private readonly Dictionary<TblTextClass, Brush> _brushes = [];

    /// <summary>Reads the palette for <paramref name="theme"/>'s current (light or dark) theme.</summary>
    public TblPalette(ThemeService? theme)
    {
        string prefix = theme?.IsDark != false ? "Tbl.Dark." : "Tbl.Light.";
        foreach (var cls in Enum.GetValues<TblTextClass>())
        {
            var fallback = theme?.Brush("Editor.Foreground", Brushes.Gray) ?? Brushes.Gray;
            var brush = Application.Current?.TryFindResource(prefix + cls) switch
            {
                Color c => Frozen(new SolidColorBrush(c)),
                Brush b => b,
                _ => fallback,
            };
            _brushes[cls] = brush;
        }
        IsDark = theme?.IsDark != false;
    }

    /// <summary>True for the dark palette.</summary>
    public bool IsDark { get; }

    /// <summary>The brush for a token class.</summary>
    public Brush this[TblTextClass cls] => _brushes.TryGetValue(cls, out var b) ? b : Brushes.Gray;

    private static Brush Frozen(SolidColorBrush brush) { brush.Freeze(); return brush; }
}

/// <summary>Colours the text by the token classes of the latest <see cref="Documents.TblModel"/>.</summary>
public sealed class TblColorizer : DocumentColorizingTransformer
{
    private static readonly TextDecorationCollection Dotted = BuildDotted();
    private ImmutableArray<TblClassifiedSpan> _classes = [];

    /// <summary>The colours in use.</summary>
    public TblPalette Palette { get; set; } = new(null);

    /// <summary>Replaces the classified spans (redraw the view afterwards).</summary>
    public void SetClasses(ImmutableArray<TblClassifiedSpan> classes) => _classes = classes;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_classes.IsDefaultOrEmpty) return;
        int start = line.Offset, end = line.EndOffset;
        int i = FirstAtOrAfter(start);
        for (; i < _classes.Length; i++)
        {
            var cs = _classes[i];
            if (cs.Span.Start >= end) break;
            int s = Math.Max(cs.Span.Start, start), e = Math.Min(cs.Span.End, end);
            if (e <= s || cs.Class == TblTextClass.Text) continue;
            var brush = Palette[cs.Class];
            var cls = cs.Class;
            ChangeLinePart(s, e, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
                if (cls is TblTextClass.SectionHeader or TblTextClass.FieldName)
                {
                    var tf = element.TextRunProperties.Typeface;
                    element.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, tf.Style,
                        cls == TblTextClass.SectionHeader ? FontWeights.Bold : FontWeights.SemiBold, tf.Stretch));
                }
                else if (cls == TblTextClass.Comment)
                {
                    var tf = element.TextRunProperties.Typeface;
                    element.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, FontStyles.Italic, tf.Weight, tf.Stretch));
                }
                else if (cls == TblTextClass.FileName)
                {
                    element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                }
                else if (cls == TblTextClass.RefName)
                {
                    element.TextRunProperties.SetTextDecorations(Dotted);
                }
            });
        }
    }

    private int FirstAtOrAfter(int offset)
    {
        int lo = 0, hi = _classes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_classes[mid].Span.End <= offset) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static TextDecorationCollection BuildDotted()
    {
        // 1 on, 2 off: clearly dotted beside the solid underline of file names.
        var pen = new Pen(Brushes.Gray, 1) { DashStyle = new DashStyle([1.0, 2.0], 0) };
        pen.Freeze();
        var decoration = new TextDecoration(TextDecorationLocation.Underline, pen, 1, TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel);
        var collection = new TextDecorationCollection { decoration };
        collection.Freeze();
        return collection;
    }
}

/// <summary>One squiggle: a span and the diagnostics reported on it.</summary>
public sealed record TblSquiggle(int Offset, int Length, TblSeverity Severity, IReadOnlyList<TblDiagnostic> Diagnostics) : ISegment
{
    int ISegment.Offset => Offset;
    int ISegment.Length => Length;
    int ISegment.EndOffset => Offset + Length;
}

/// <summary>Draws the diagnostics' squiggly underlines (error, warning, information colours).</summary>
public sealed class TblSquiggleRenderer : IBackgroundRenderer
{
    private IReadOnlyList<TblSquiggle> _markers = [];

    public Brush ErrorBrush { get; set; } = Brushes.Red;
    public Brush WarningBrush { get; set; } = Brushes.Orange;
    public Brush InfoBrush { get; set; } = Brushes.SteelBlue;
    public KnownLayer Layer => KnownLayer.Selection;
    public IReadOnlyList<TblSquiggle> Markers => _markers;

    /// <summary>Builds the markers from diagnostics, clamped to a text of <paramref name="length"/> characters.</summary>
    public void SetDiagnostics(TextView? view, IEnumerable<TblDiagnostic> diagnostics, int length)
    {
        _markers = [.. diagnostics
            .GroupBy(d => (d.Span.Start, d.Span.Length))
            .Select(g =>
            {
                int start = Math.Clamp(g.Key.Start, 0, length);
                int span = Math.Clamp(g.Key.Length, 0, length - start);
                if (span == 0) span = Math.Min(1, length - start);
                return new TblSquiggle(start, span, g.Min(d => d.Severity), [.. g]);
            })
            .Where(m => m.Length > 0)];
        view?.InvalidateLayer(Layer);
    }

    /// <summary>The markers at <paramref name="offset"/>.</summary>
    public IReadOnlyList<TblSquiggle> MarkersAt(int offset) =>
        [.. _markers.Where(m => offset >= m.Offset && offset <= m.Offset + Math.Max(1, m.Length))];

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_markers.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
        int viewStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        int viewEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        int docLength = textView.Document?.TextLength ?? 0;
        foreach (var marker in _markers)
        {
            if (marker.Offset > viewEnd || marker.Offset + marker.Length < viewStart || marker.Offset + marker.Length > docLength) continue;
            var brush = marker.Severity switch
            {
                TblSeverity.Error => ErrorBrush,
                TblSeverity.Warning => WarningBrush,
                _ => InfoBrush,
            };
            var pen = new Pen(brush, 1.0);
            pen.Freeze();
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
            {
                drawingContext.DrawGeometry(null, pen, Squiggle(rect.Left, rect.Bottom - 1, Math.Max(rect.Width, 4)));
            }
        }
    }

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

/// <summary>Folding regions: sections, entries and block comments.</summary>
public static class TblFolding
{
    /// <summary>The fold regions of <paramref name="parsed"/> within a text of <paramref name="document"/>'s length.</summary>
    public static List<NewFolding> Build(ParsedTable parsed, TextDocument document)
    {
        var foldings = new List<NewFolding>();
        int length = document.TextLength;
        void Add(TextSpan span, string name)
        {
            int start = Math.Clamp(span.Start, 0, length);
            int end = Math.Clamp(span.End, 0, length);
            // Trailing line breaks belong to the next region visually.
            while (end > start && (document.GetCharAt(end - 1) is '\r' or '\n' or ' ' or '\t')) end--;
            if (end <= start) return;
            var first = document.GetLineByOffset(start);
            if (first.LineNumber == document.GetLineByOffset(end).LineNumber) return;
            foldings.Add(new NewFolding(first.EndOffset, end) { Name = name });
        }
        foreach (var section in parsed.Sections)
        {
            if (section.HasHeader) Add(section.Span, " … ");
            foreach (var entry in section.Entries) Add(entry.Span, " … ");
        }
        foreach (var comment in parsed.Comments)
        {
            if (comment.Kind == TblLexKind.BlockComment) Add(comment.Span, " … */");
        }
        foldings.Sort((a, b) => a.StartOffset != b.StartOffset ? a.StartOffset.CompareTo(b.StartOffset) : b.EndOffset.CompareTo(a.EndOffset));
        return foldings;
    }
}
