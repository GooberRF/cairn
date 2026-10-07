using System.Windows;
using System.Windows.Media;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Timeline;

/// <summary>Read-only material curve rows: opacity and self-illumination of each material an expanded mesh uses, drawn as
/// value curves (0..1) in effect frames, sampled like the game (<see cref="VfxSampler.Track"/>). Clicking one selects the
/// material so the Material tab edits it.</summary>
public sealed partial class VfxTimelineSurface
{
    /// <summary>Material section indices used by a mesh (its material slots that point at material sections).</summary>
    internal static IEnumerable<int> MaterialSectionsOf(VfxFile f, int section)
    {
        if (f.Sections[section] is not VfxMesh { MaterialIndices: { } slots }) yield break;
        var mats = Enumerable.Range(0, f.Sections.Length).Where(i => f.Sections[i] is VfxMaterial).ToList();
        foreach (int s in slots.Distinct()) if (s >= 0 && s < mats.Count) yield return mats[s];
    }

    private bool HasDetail(VfxFile f, int section) =>
        f.Sections[section] is VfxMesh m && (m.Keys is not null || m.Frames.Length > 1 || MaterialSectionsOf(f, section).Any());

    partial void AddMaterialRows(VfxFile f, int section, int depth)
    {
        foreach (int mi in MaterialSectionsOf(f, section))
        {
            string name = VfxSections.NameOf(f.Sections[mi]);
            if (string.IsNullOrWhiteSpace(name)) name = $"Material {f.Sections.Take(mi).Count(x => x is VfxMaterial) + 1}"; // as the outliner names it
            _rows.Add(new(section, null, false, $"{name}: opacity", depth, mi, 0));
            _rows.Add(new(section, null, false, $"{name}: self-illum", depth, mi, 1));
        }
    }

    private void DrawMaterialCurve(DrawingContext dc, VfxFile f, int material, int curve, double y, Brush stroke)
    {
        if (f.Sections[material] is not VfxMaterial m) return;
        float[] keys = curve == 0 ? (m.Opacity is { } o ? [.. o] : []) : [.. m.SelfIllumination];
        float fps = m.Fps ?? m.LegacyFps ?? 15;
        double top = y + 4, height = RowHeight - 9;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double f0 = Math.Max(0, FrameAt(HeaderWidth)), f1 = FrameAt(ActualWidth > 0 ? ActualWidth : HeaderWidth + 600);
            double step = Math.Max(0.25, 2 / _pxPerFrame);
            bool first = true;
            for (double fr = f0; fr <= f1 + step; fr += step)
            {
                float v = VfxSampler.Track(keys, fps, (float)fr, curve == 0 ? 1 : 0);
                var p = new Point(X(fr), top + (1 - v) * height);
                if (first) { c.BeginFigure(p, false, false); first = false; } else c.LineTo(p, true, false);
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, new Pen(stroke, 1.2), g);
        if (keys.Length > 1)
            for (int k = 0; k < keys.Length; k++)
                dc.DrawRectangle(stroke, null, new Rect(X(k * 15.0 / fps) - 1.5, top + (1 - Math.Clamp(keys[k], 0, 1)) * height - 1.5, 3, 3));
    }
}
