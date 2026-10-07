using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Inspectors;

namespace Cairn.Vfx.Ui.Timeline;

/// <summary>
/// Keys inspector tab: the keys selected in the timeline. Time, value, absolute Bezier control points
/// (translation/scale), ease (rotation), TCB (stored, unused by the game). Several keys: shared fields editable,
/// differing ones indeterminate. Every edit is one undo step (spinner drags coalesce).
/// </summary>
public sealed class VfxKeysTab : VfxInspectorPage
{
    public VfxKeysTab(VfxDocument doc) : base(doc) { }

    /// <summary>Show tangents relative to the key value (display only; stored control points stay absolute).</summary>
    public bool Relative { get; set; }

    /// <summary>The selected keys that exist in the current file, primary order = selection order.</summary>
    internal List<VfxKeyRef> Selected() => Doc.Selection.Keys.Where(r => Vec(File, r) is not null || Rot(File, r) is not null).ToList();

    internal static VfxVectorKey? Vec(VfxFile f, VfxKeyRef r) =>
        r.Channel != VfxKeyChannel.Rotation && r.Section < f.Sections.Length && f.Sections[r.Section] is VfxMesh { Keys: { } k }
            ? (r.Channel == VfxKeyChannel.Translation ? k.Translation : k.Scale).FirstOrDefault(x => x.Time == r.Time) : null;

    internal static VfxRotationKey? Rot(VfxFile f, VfxKeyRef r) =>
        r.Channel == VfxKeyChannel.Rotation && r.Section < f.Sections.Length && f.Sections[r.Section] is VfxMesh { Keys: { } k }
            ? k.Rotation.FirstOrDefault(x => x.Time == r.Time) : null;

    private IEnumerable<VfxVectorKey> Vecs() => Selected().Select(r => Vec(File, r)).OfType<VfxVectorKey>();
    private IEnumerable<VfxRotationKey> Rots() => Selected().Select(r => Rot(File, r)).OfType<VfxRotationKey>();

    private VfxFile EachVec(VfxFile f, Func<VfxVectorKey, VfxVectorKey> change)
    {
        foreach (var r in Selected()) if (Vec(f, r) is { } k) f = VfxEdit.SetKey(f, r.Section, r.Channel, change(k));
        return f;
    }

    private VfxFile EachRot(VfxFile f, Func<VfxRotationKey, VfxRotationKey> change)
    {
        foreach (var r in Selected()) if (Rot(f, r) is { } k) f = VfxEdit.SetRotationKey(f, r.Section, change(k));
        return f;
    }

    protected override string StructureKey()
    {
        var s = Selected();
        return $"{s.Count(r => r.Channel != VfxKeyChannel.Rotation) > 0}|{s.Count(r => r.Channel == VfxKeyChannel.Rotation) > 0}|{s.Count > 1}|{Relative}";
    }

    private static float C(Vector3 v, int c) => c == 0 ? v.X : c == 1 ? v.Y : v.Z;
    private static Vector3 With(Vector3 v, int c, float x) => c == 0 ? v with { X = x } : c == 1 ? v with { Y = x } : v with { Z = x };
    private static readonly string[] Axes = ["X", "Y", "Z"];

    protected override void Build()
    {
        var sel = Selected();
        if (sel.Count == 0) { Header("No keys selected"); Fact("Hint", () => "Click a key diamond in the timeline (expand a keyframed mesh's row)."); return; }
        bool vec = sel.Any(r => r.Channel != VfxKeyChannel.Rotation), rot = sel.Any(r => r.Channel == VfxKeyChannel.Rotation);
        Header(sel.Count == 1 ? "Key" : $"{sel.Count} keys");
        Fact("Selected", () =>
        {
            var s = Selected();
            return s.Count == 1 ? $"{VfxSections.NameOf(File.Sections[s[0].Section])}: {s[0].Channel}" : $"{s.Count} keys on {s.Select(r => r.Section).Distinct().Count()} objects";
        });
        const int T = VfxTimelineEdits.TicksPerFrame;
        Number("Time (frames)", "Key time in effect frames (15 per second; 320 ticks each). Several keys: use the timeline to move them.",
            () => Selected().Select(r => r.Time / (double)T), (f, v) => Retime(f, (int)Math.Round(v * T)), decimals: 3, step: 1, min: 0);
        Number("Time (ticks)", "Key time in ticks (4800 per second)", () => Selected().Select(r => (double)r.Time), (f, v) => Retime(f, (int)Math.Round(v)), decimals: 0, step: 32, min: 0);
        if (vec)
        {
            Header("Value");
            for (int c = 0; c < 3; c++) { int cc = c; Number("Value " + Axes[c], "Key value (translation in metres; scale factor)", () => Vecs().Select(k => (double)C(k.Value, cc)), (f, v) => EachVec(f, k => k with { Value = With(k.Value, cc, F(v)) }), decimals: 3, step: 0.1); }
            Header(Relative ? "Tangents (relative to the value)" : "Tangents (absolute control points)");
            var rel = new CheckBox { Content = "Show relative to the key value", IsChecked = Relative, ToolTip = "Display only: the file stores absolute Bezier control points" };
            rel.Click += (_, _) => { Relative = rel.IsChecked == true; Refresh(); };
            Body.Children.Add(rel);
            foreach (var (name, isIn) in new[] { ("In", true), ("Out", false) })
                for (int c = 0; c < 3; c++)
                {
                    int cc = c; bool i = isIn;
                    Number($"{name} {Axes[c]}", $"{name}-tangent control point. The game draws the curve from a key's value through its out-point and the next key's in-point (cubic Bezier).",
                        () => Vecs().Select(k => (double)(C(i ? k.InTangent : k.OutTangent, cc) - (Relative ? C(k.Value, cc) : 0))),
                        (f, v) => EachVec(f, k =>
                        {
                            float a = F(v) + (Relative ? C(k.Value, cc) : 0);
                            return i ? k with { InTangent = With(k.InTangent, cc, a) } : k with { OutTangent = With(k.OutTangent, cc, a) };
                        }), decimals: 3, step: 0.1);
                }
            Buttons(("Flat", "Control points = key value (no overshoot; a channel of flat keys moves along straight lines with ease in/out)", () => { VfxEditing.Apply(Doc, "Flat tangents", f => Flat(f)); }),
                    ("Auto", "Smooth (Catmull-Rom) control points from the neighbouring keys; end keys flat", () => { VfxEditing.Apply(Doc, "Auto tangents", f => Auto(f)); }));
        }
        if (rot)
        {
            Header("Rotation");
            string[] eul = ["Yaw (Y)", "Pitch (X)", "Roll (Z)"];
            for (int c = 0; c < 3; c++)
            {
                int cc = c;
                Number(eul[c], "Rotation key as Euler degrees (same convention as the pivot fields)",
                    () => Rots().Select(k => Math.Round(C(VfxObjectTab.ToEuler(k.Value), cc), 3)),
                    (f, v) => EachRot(f, k => k with { Value = VfxObjectTab.FromEuler(With(VfxObjectTab.ToEuler(k.Value), cc, F(v))) }), decimals: 2, step: 5, suffix: " deg");
            }
            Fact("Quaternion", () => Rots().Select(k => $"{k.Value.X:0.####}, {k.Value.Y:0.####}, {k.Value.Z:0.####}, {k.Value.W:0.####}").Distinct().ToList() is { Count: 1 } q ? q[0] : "(differs)");
            Number("Ease in", "0..1: slows the approach to this key (the game eases the fraction before the slerp)", () => Rots().Select(k => (double)k.EaseIn), (f, v) => EachRot(f, k => k with { EaseIn = F(v) }), decimals: 2, step: 0.1, min: 0, max: 1);
            Number("Ease out", "0..1: slows the departure from this key", () => Rots().Select(k => (double)k.EaseOut), (f, v) => EachRot(f, k => k with { EaseOut = F(v) }), decimals: 2, step: 0.1, min: 0, max: 1);
            int before = Body.Children.Count;
            Number("Tension", "Stored TCB tension (not used by the game)", () => Rots().Select(k => (double)k.Tension), (f, v) => EachRot(f, k => k with { Tension = F(v) }), step: 0.1);
            Number("Continuity", "Stored TCB continuity (not used by the game)", () => Rots().Select(k => (double)k.Continuity), (f, v) => EachRot(f, k => k with { Continuity = F(v) }), step: 0.1);
            Number("Bias", "Stored TCB bias (not used by the game)", () => Rots().Select(k => (double)k.Bias), (f, v) => EachRot(f, k => k with { Bias = F(v) }), step: 0.1);
            var inner = new StackPanel();
            var note = new TextBlock { Text = "The game does not use tension, continuity or bias; they are kept for round trips.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            inner.Children.Add(note);
            while (Body.Children.Count > before) { var e = Body.Children[before]; Body.Children.RemoveAt(before); inner.Children.Add(e); }
            var exp = new Expander { Header = "Advanced", Content = inner, Margin = new Thickness(0, 6, 0, 0) };
            exp.SetResourceReference(Control.ForegroundProperty, "App.Text");
            Body.Children.Add(exp);
        }
        Header("Navigate");
        Buttons(("Previous key", "Select the previous key of the same track and move the playhead to it", () => Step(-1)),
                ("Next key", "Select the next key of the same track and move the playhead to it", () => Step(+1)),
                ("Delete", "Delete the selected keys (a track keeps at least one key)", () => VfxModule.TimelineOf(Doc).Surface.DeleteSelectedKeys()));
    }

    private VfxFile Retime(VfxFile f, int t)
    {
        var s = Selected();
        if (s.Count != 1) throw new ArgumentException("Several keys selected: drag them in the timeline to move them.");
        var r = s[0];
        if (t == r.Time) return f;
        var g = VfxEdit.MoveKey(f, r.Section, r.Channel, r.Time, t);
        Dispatcher.BeginInvoke(() => Doc.Selection.SelectKeys([r with { Time = t }]));
        return g;
    }

    internal VfxFile Flat(VfxFile f) => EachVec(f, k => k with { InTangent = k.Value, OutTangent = k.Value });

    internal VfxFile Auto(VfxFile f)
    {
        foreach (var r in Selected())
        {
            if (r.Channel == VfxKeyChannel.Rotation || Vec(f, r) is not { } k) continue;
            var auto = Vec(VfxEdit.AutoTangents(f, r.Section, r.Channel), r)!;
            f = VfxEdit.SetKey(f, r.Section, r.Channel, k with { InTangent = auto.InTangent, OutTangent = auto.OutTangent });
        }
        return f;
    }

    /// <summary>Moves the selection to the previous/next key of the primary key's track and seeks the playhead there.</summary>
    internal void Step(int dir)
    {
        var s = Selected();
        if (s.Count == 0 || File.Sections[s[0].Section] is not VfxMesh m) return;
        var times = VfxTimelineEdits.KeyTimes(m, s[0].Channel).OrderBy(t => t).ToList();
        int at = times.IndexOf(s[0].Time) + dir;
        if (at < 0 || at >= times.Count) return;
        Doc.Selection.SelectKeys([s[0] with { Time = times[at] }]);
        Doc.SeekFrame(times[at] / (float)VfxTimelineEdits.TicksPerFrame);
    }
}
