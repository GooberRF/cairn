using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Views;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of the timeline's view-model logic: rows, selection (click / toggle / box / all / summary
/// mark), move and copy drags (snapping, clamping, one coalesced undo step, cancel), scale, delete,
/// cut / copy / paste through the system clipboard (also into another tab, matched by name, and
/// mirrored), key at playhead, start/end handle drags; and the redraw time on the largest stock clip.
/// </summary>
internal static class TimelineSelfTests
{
    [SelfTest("timeline", Order = 10)]
    public static async Task Timeline(SelfTestContext ctx)
    {
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("selftest timeline: needs a clip document");
            return;
        }
        var tl = doc.Timeline;
        var original = doc.Current;
        int undoBefore = doc.History.UndoLabels.Count;
        var names = doc.ClipboardBoneNames;
        ctx.Check(tl.AllRows.Count == original.BoneCount + 1 && tl.AllRows[0].IsSummary,
            $"rows: a summary row and one per bone ({tl.AllRows.Count - 1} bones)");
        ctx.Check(tl.HasHierarchy == (names is not null) && (names is null || tl.AllRows.Skip(1).All(r => r.Name == names[r.Bone])),
            "bone rows carry the fitting preview mesh's names, in hierarchy order");
        int root = tl.AllRows[1].Bone;
        if (tl.AllRows[1].HasChildren)
        {
            tl.ToggleExpanded(root);
            ctx.Check(tl.VisibleRows.Count == 2, $"collapsing the root hides its descendants ({tl.VisibleRows.Count} rows visible)");
            tl.ToggleExpanded(root);
        }
        tl.Filter = "zzz-no-such-bone";
        ctx.Check(tl.VisibleRows.Count == 1, "the filter hides non-matching bones (summary row stays)");
        tl.Filter = string.Empty;

        // Pick a bone with several rotation keys away from the ends.
        int bone = Enumerable.Range(0, original.BoneCount).OrderByDescending(b => original.Bones[b].RotationKeys.Length).First();
        var track = original.Bones[bone];
        tl.SelectBoneKeys([bone]);
        ctx.Check(doc.KeySelection.Count == track.RotationKeys.Length + track.PositionKeys.Length,
            $"selecting a bone's keys selects all {doc.KeySelection.Count} of them (shared with the document)");
        var one = new KeyRef(bone, KeyKind.Rotation, 1);
        tl.Select(KeySelection.Of(one), SelectMode.Toggle);
        ctx.Check(!doc.KeySelection.Contains(one) && doc.KeySelection.Count == track.RotationKeys.Length + track.PositionKeys.Length - 1, "Ctrl-click toggles a key off");
        tl.Select(KeySelection.Of(one), SelectMode.Replace);
        ctx.Check(doc.KeySelection.Count == 1 && doc.KeySelection.Contains(one), "a plain click selects just that key");
        int t1 = track.RotationKeys[1].Time;
        var summary = tl.KeysAtTime(t1);
        tl.Select(summary, SelectMode.Replace);
        ctx.Check(doc.KeySelection.Count == summary.Count && summary.Keys.All(k => k.TimeIn(original) == t1), $"a summary mark selects every key at its time ({summary.Count})");
        var box = tl.KeysIn(tl.VisibleRows.Where(r => r.Bone == bone), track.RotationKeys[0].Time, track.RotationKeys[2].Time);
        ctx.Check(box.Keys.All(k => k.Bone == bone) && box.Keys.Count(k => k.Kind == KeyKind.Rotation) == 3, "a box over one row and three key times selects those keys");
        tl.SelectAll();
        ctx.Check(doc.KeySelection.Count == KeyCount(original), "Ctrl+A selects every key");

        // Move drag: snapped, clamped, live, one undo step.
        var moveSel = KeySelection.Of(new KeyRef(bone, KeyKind.Rotation, 1), new KeyRef(bone, KeyKind.Rotation, 2));
        doc.SetKeySelection(moveSel);
        int grab = track.RotationKeys[1].Time;
        tl.BeginMove(grab, copy: false);
        tl.UpdateMove(60, free: false);
        tl.UpdateMove(100, free: false);
        int expectedDelta = ClipEdit.ClampMoveDelta(original, moveSel, tl.SnapToFrame(grab + 100) - grab);
        ctx.Check(!ReferenceEquals(doc.Current, original) || expectedDelta == 0, "the drag shows live before it is committed");
        tl.EndDrag();
        var expected = ClipEdit.MoveKeys(original, moveSel, expectedDelta, out var expectedMoved);
        ctx.Check(SameKeys(doc.Current, expected), $"a key drag equals ClipEdit.MoveKeys with the snapped, clamped delta ({expectedDelta} ticks)");
        ctx.Check(doc.History.UndoLabels.Count == undoBefore + 1 && doc.UndoLabel == "Move 2 keys", $"the whole drag is one undo step labelled '{doc.UndoLabel}'");
        ctx.Check(doc.KeySelection.Keys.SequenceEqual(expectedMoved.Keys), "the selection follows the moved keys");
        doc.Undo();
        ctx.Check(ReferenceEquals(doc.Current, original), "undo restores the clip");

        doc.SetKeySelection(moveSel);
        tl.BeginMove(grab, copy: false);
        tl.UpdateMove(37, free: true);
        tl.CancelDrag();
        ctx.Check(ReferenceEquals(doc.Current, original) && doc.History.UndoLabels.Count == undoBefore, "Esc cancels a drag without an undo step");

        // Copy drag (Ctrl): originals stay, copies land at the new time.
        var copySel = KeySelection.Of(new KeyRef(bone, KeyKind.Rotation, 1));
        doc.SetKeySelection(copySel);
        int lastKey = track.RotationKeys[^1].Time;
        int target = lastKey + 2 * RfaClip.TicksPerFrame;
        tl.BeginMove(grab, copy: true);
        tl.UpdateMove(target - grab, free: true);
        tl.EndDrag();
        var copied = doc.Current.Bones[bone].RotationKeys;
        ctx.Check(copied.Length == track.RotationKeys.Length + 1 && copied.Any(k => k.Time == grab) && copied.Any(k => k.Time == target)
            && doc.UndoLabel == "Copy 1 key", $"Ctrl+drag copies the key (now {copied.Length} keys; '{doc.UndoLabel}')");
        doc.Undo();

        // Scale about the playhead.
        var scaleSel = KeySelection.Bones(original, [bone], positions: false);
        doc.SetKeySelection(scaleSel);
        doc.Playback.Seek(original.StartTime);
        tl.ScaleSelected(0.5);
        ctx.Check(SameKeys(doc.Current, ClipEdit.ScaleKeys(original, scaleSel, original.StartTime, 0.5)), $"scale keys equals ClipEdit.ScaleKeys about the playhead ('{doc.UndoLabel}')");
        doc.Undo();

        // Delete.
        doc.SetKeySelection(moveSel);
        tl.DeleteSelected();
        ctx.Check(SameKeys(doc.Current, ClipEdit.DeleteKeys(original, moveSel)) && doc.UndoLabel == "Delete 2 keys" && doc.KeySelection.IsEmpty,
            $"Delete removes the selected keys ('{doc.UndoLabel}')");
        doc.Undo();

        // Copy / paste through the system clipboard at the playhead.
        var pasteSel = KeySelection.Bones(original, [bone]);
        doc.SetKeySelection(pasteSel);
        bool copiedOk = tl.Copy();
        string? text = null;
        if (KeyClipboardStore.LastPutFailed)
        {
            // Another program held the system clipboard open: the copy is kept in-process and still pastes.
            ctx.Log("selftest note: the system clipboard was held by another program; Ctrl+C kept the keys in-process (checked by the pastes below)");
            ctx.Check(copiedOk && KeyClipboardStore.Get()?.KeyCount == pasteSel.Count, "Ctrl+C keeps the keys when the system clipboard is busy");
        }
        else
        {
            for (int attempt = 0; attempt < 3 && text is null; attempt++)
            {
                try { text = Clipboard.GetText(); }
                catch (System.Runtime.InteropServices.ExternalException) { await Task.Delay(100); }
            }
            ctx.Check(copiedOk && (text?.Contains(KeyClipboard.FormatName, StringComparison.Ordinal) ?? false), "Ctrl+C puts KeyClipboard JSON on the system clipboard");
        }
        int pasteAt = original.StartTime + RfaClip.TicksPerFrame * 3;
        doc.Playback.Seek(pasteAt);
        var expectedPaste = KeyClipboard.Copy(original, pasteSel, names).Paste(original, pasteAt, names);
        tl.Paste();
        ctx.Check(SameKeys(doc.Current, expectedPaste.Clip) && doc.KeySelection.Keys.SequenceEqual(expectedPaste.Pasted.Keys),
            $"Ctrl+V pastes at the playhead, matched by name, and selects the pasted keys ('{doc.UndoLabel}')");
        doc.Undo();
        doc.SetKeySelection(pasteSel);
        tl.Cut();
        ctx.Check(SameKeys(doc.Current, ClipEdit.DeleteKeys(original, pasteSel)) && doc.UndoLabel?.StartsWith("Cut", StringComparison.Ordinal) == true
            && KeyClipboardStore.Get()?.KeyCount == pasteSel.Count, "Ctrl+X copies and deletes in one undo step");
        doc.Undo();

        // Paste mirrored (needs names): copy a left-side bone, paste onto its partner.
        if (names is not null)
        {
            var pairs = BonePairs.Detect(names);
            int left = Enumerable.Range(0, original.BoneCount).FirstOrDefault(b => pairs.IsPaired(b) && original.Bones[b].RotationKeys.Length > 1, -1);
            if (left >= 0)
            {
                doc.SetKeySelection(KeySelection.Bones(original, [left], positions: false));
                tl.Copy();
                doc.Playback.Seek(original.StartTime);
                tl.PasteMirrored();
                int partner = pairs.PartnerOf(left);
                ctx.Check(doc.KeySelection.Keys.All(k => k.Bone == partner) && !doc.KeySelection.IsEmpty,
                    $"paste mirrored lands on the partner bone ({names[left]} → {names[partner]})");
                doc.Undo();
            }
        }

        // Paste into another tab (matched by bone name).
        string? otherPath = FindSibling(doc, "ult2_run.rfa") ?? FindSibling(doc, "ult2_stand.rfa");
        if (otherPath is not null)
        {
            doc.SetKeySelection(pasteSel);
            tl.Copy();
            var other = ctx.Model.OpenFile(otherPath) as ClipDocumentViewModel;
            await ctx.SettleAsync();
            if (other is not null)
            {
                var before = other.Current;
                other.Playback.Seek(before.StartTime);
                bool pasted = other.Timeline.Paste();
                ctx.Check(pasted && other.KeySelection.Keys.All(k => k.Bone == bone || other.ClipboardBoneNames is null) && !other.KeySelection.IsEmpty,
                    $"keys copied in one tab paste into another ({other.DisplayName}), matched by bone name");
                while (other.CanUndo) other.Undo();
                ctx.Model.CloseDocument(other);
                ctx.Model.ActiveDocument = doc;
                await ctx.SettleAsync();
            }
        }

        // K: key the selected bones at the playhead, motion unchanged.
        int keyTime = (track.RotationKeys[1].Time + track.RotationKeys[2].Time) / 2;
        if (keyTime != track.RotationKeys[1].Time)
        {
            doc.Selection.Select(bone);
            doc.Playback.Seek(keyTime);
            var sampledBefore = ClipEdit.SampleRotation(original, bone, keyTime);
            tl.KeySelectedBonesAtPlayhead();
            var keyed = doc.Current.Bones[bone].RotationKeys;
            float angle = Cairn.Formats.Maths.Quat.AngleDegrees(sampledBefore, ClipEdit.SampleRotation(doc.Current, bone, keyTime));
            ctx.Check(keyed.Any(k => k.Time == keyTime) && angle < 0.05f && doc.KeySelection.Keys.Any(k => k.Bone == bone),
                $"K adds a key at the playhead without changing the motion ({angle:0.000}°, '{doc.UndoLabel}')");
            doc.Undo();
        }

        // Start/end handle drag.
        tl.BeginRangeDrag(start: false);
        tl.UpdateRangeDrag(original.EndTime + 2 * RfaClip.TicksPerFrame + 30, free: false);
        tl.EndDrag();
        ctx.Check(doc.Current.EndTime == original.EndTime + 2 * RfaClip.TicksPerFrame && doc.UndoLabel == "Set end time",
            $"dragging the end handle edits the header in one step (end {doc.Current.EndTime})");
        doc.Undo();

        doc.SetKeySelection(KeySelection.Empty);
        while (doc.History.UndoLabels.Count > undoBefore && doc.CanUndo) doc.Undo();
        ctx.Check(ReferenceEquals(doc.Current, original), "the timeline test leaves the clip as it found it");
    }

    [SelfTest("timeline-perf", Order = 11)]
    public static async Task TimelinePerformance(SelfTestContext ctx)
    {
        var start = ctx.Model.ActiveDocument;
        string? path = start?.FilePath is { } p ? Path.Combine(Path.GetDirectoryName(p)!, "CS5_PARK_Shot01_22.rfa") : null;
        if (path is null || !File.Exists(path))
        {
            ctx.Log("selftest timeline-perf: CS5_PARK_Shot01_22.rfa is not beside the open clip; skipped");
            return;
        }
        var doc = ctx.Model.OpenFile(path) as ClipDocumentViewModel;
        await ctx.SettleAsync();
        if (doc is null) return;
        ctx.Model.IsBottomVisible = true;
        ctx.Model.SelectedBottomTab = ctx.Model.BottomTabs.First(t => t.Id == "timeline");
        await ctx.YieldAsync();
        ctx.Window.UpdateLayout();
        var view = FindVisible<TimelineView>(ctx.Window);
        if (view is null)
        {
            ctx.Check(false, "the Timeline view is on screen for the big clip");
        }
        else
        {
            var surface = view.TimelineSurface;
            // Measure with every bone row on screen (a tall bottom panel), not just the few the default height shows.
            double oldHeight = view.Height;
            view.Height = TimelineSurfaceRowsHeight(doc.Current.BoneCount + 1) + 40;
            ctx.Window.UpdateLayout();
            surface.Fit();
            for (int i = 0; i < 3; i++) surface.RedrawAll(); // warm-up (JIT), not counted
            var times = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                surface.RedrawAll();
                times.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            int keys = KeyCount(doc.Current);
            // Selecting everything makes every mark a selected mark (no merging of selected keys).
            doc.Timeline.SelectAll();
            var selTimes = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                surface.RedrawAll();
                selTimes.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            double median = Median(times), selMedian = Median(selTimes);
            ctx.Log(string.Format(CultureInfo.InvariantCulture,
                "timing: timeline redraw on {0} ({1:N0} keys, {2} bones, {3}×{4} px): median {5:0.00} ms, best {6:0.00} ms, max {7:0.00} ms, {8:N0} marks drawn; all keys selected: median {9:0.00} ms, best {10:0.00} ms (a 60 Hz frame is 16.7 ms; a busy machine adds noise)",
                doc.DisplayName, keys, doc.Current.BoneCount, (int)surface.ActualWidth, (int)surface.ActualHeight, median, times.Min(), times.Max(), surface.LastMarksDrawn,
                selMedian, selTimes.Min()));
            // A timing is reported, not judged, unless it is grossly off (the median, so one stall cannot fail it).
            ctx.Check(median < 60 && selMedian < 60, $"the timeline redraw of the largest stock clip is not grossly slow (median {median:0.0} / {selMedian:0.0} ms, limit 60 ms)");
            doc.SetKeySelection(KeySelection.Empty);
            view.Height = oldHeight;
        }
        ctx.Model.CloseDocument(doc);
        if (start is not null) ctx.Model.ActiveDocument = start;
        await ctx.SettleAsync();
    }

    private static double TimelineSurfaceRowsHeight(int rows) => Controls.TimelineSurface.RulerHeight + rows * Controls.TimelineSurface.RowHeight;

    internal static int KeyCount(RfaClip clip) => clip.Bones.Sum(b => b.RotationKeys.Length + b.PositionKeys.Length);

    /// <summary>The median of a list of timings (robust against one stall on a busy machine).</summary>
    internal static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.Order().ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static string? FindSibling(DocumentViewModel doc, string name)
    {
        if (doc.FilePath is not { } p) return null;
        string candidate = Path.Combine(Path.GetDirectoryName(p)!, name);
        return File.Exists(candidate) && !string.Equals(candidate, p, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    /// <summary>True when both clips hold the same keys and header (by value).</summary>
    internal static bool SameKeys(RfaClip a, RfaClip b)
    {
        if (a.BoneCount != b.BoneCount || a.StartTime != b.StartTime || a.EndTime != b.EndTime) return false;
        for (int i = 0; i < a.BoneCount; i++)
        {
            var x = a.Bones[i];
            var y = b.Bones[i];
            if (x.Weight != y.Weight || !x.RotationKeys.SequenceEqual(y.RotationKeys) || !x.PositionKeys.SequenceEqual(y.PositionKeys)) return false;
        }
        return true;
    }

    internal static T? FindVisible<T>(DependencyObject root) where T : FrameworkElement
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.IsVisible) return t;
            if (FindVisible<T>(child) is { } hit) return hit;
        }
        return null;
    }
}
