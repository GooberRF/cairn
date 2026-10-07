using System.Globalization;
using System.Text.Json;
using System.Windows;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.PoseEditing;
using Cairn.Rfa.Ui.Views;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Workspace;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Phase 6 carried-over fixes: the persisted, app-wide pose switches (checked through
/// <see cref="AppSettings.Get{T}"/>, never by writing the settings file), the key clipboard through the
/// real Windows clipboard (or a logged reason why it is unavailable), signed ease bytes in the Key
/// inspector (shown as stored, never rewritten by showing them, other keys bit-identical after an ease
/// edit) and the timeline's pinned summary row. Screenshot switches: <c>--timeline-scroll &lt;rows&gt;</c>
/// and <c>--negative-ease &lt;in&gt;,&lt;out&gt;</c> (sets rotation key 1 of the selected bone's eases to
/// those signed bytes as a live edit, selects it and shows the Key inspector).
/// </summary>
internal static class Phase6FixesSelfTests
{
    [SelfTest("fixes", Order = 450)]
    public static async Task Fixes(SelfTestContext ctx)
    {
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("selftest fixes: needs a clip document");
            return;
        }
        var original = doc.Current;
        int undoBefore = doc.History.UndoLabels.Count;
        var selectionBefore = doc.KeySelection;
        var bonesBefore = doc.Selection.Bones.ToArray();
        var settingsFile = new FileInfo(Path.Combine(SettingsStore.DefaultDirectory, "settings.json"));
        DateTime? fileTime = settingsFile.Exists ? settingsFile.LastWriteTimeUtc : null;

        await PoseSettings(ctx, doc);
        await ClipboardRoundTrip(ctx, doc);
        await NegativeEases(ctx, doc);
        await PinnedSummaryRow(ctx, doc);

        // A diagnostic run never writes the settings file (SaveSettingsSoon would have fired after 1 s).
        await Task.Delay(1300);
        settingsFile.Refresh();
        DateTime? fileTimeAfter = settingsFile.Exists ? settingsFile.LastWriteTimeUtc : null;
        ctx.Check(ctx.Model.IsDiagnosticRun && fileTime == fileTimeAfter, "the settings file was not written by the pose-switch changes (diagnostic run)");

        while (doc.History.UndoLabels.Count > undoBefore && doc.CanUndo) doc.Undo();
        doc.SetKeySelection(selectionBefore);
        doc.Selection.Set(bonesBefore);
        ctx.Check(ReferenceEquals(doc.Current, original), "the fixes test leaves the clip as it found it");
    }

    // ── 4. Persisted pose switches ──────────────────────────────────────────

    private static async Task PoseSettings(SelfTestContext ctx, ClipDocumentViewModel doc)
    {
        var settings = ctx.Model.Settings;
        var ctl = PoseEditController.For(doc);
        var saved = (ctl.Tool, ctl.Space, ctl.IkEnabled, ctl.AutoKey);
        settings.Values.TryGetValue(PoseEditSettings.SettingsKey, out var rawPose);
        bool hadPose = settings.Values.ContainsKey(PoseEditSettings.SettingsKey);
        settings.Values.TryGetValue(ViewportDisplaySettings.SettingsKey, out var rawViewport);
        bool hadViewport = settings.Values.ContainsKey(ViewportDisplaySettings.SettingsKey);
        try
        {
            ctl.Tool = PoseTool.Rotate;
            ctl.Space = OffsetSpace.Parent;
            ctl.IkEnabled = false;
            ctl.AutoKey = !saved.AutoKey;
            var stored = PoseEditSettings.Read(settings);
            ctx.Check(stored == (PoseTool.Rotate, OffsetSpace.Parent, false),
                $"tool, space and IK go to the settings values at once ({stored?.ToString() ?? "nothing stored"})");
            var display = new ViewportDisplaySettings();
            display.Load(settings);
            ctx.Check(display.AutoKey == !saved.AutoKey, "auto-key goes to the settings values (viewport display settings)");
            var reloaded = new PoseEditSettings();
            reloaded.Load(settings);
            ctx.Check(reloaded.Tool == PoseTool.Rotate && reloaded.Space == OffsetSpace.Parent && !reloaded.IkEnabled,
                "the stored pose switches read back (what the next session starts with)");

            string? otherPath = doc.FilePath is { } p ? Path.Combine(Path.GetDirectoryName(p)!, "ult2_run.rfa") : null;
            if (otherPath is not null && File.Exists(otherPath) && !string.Equals(otherPath, doc.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var other = ctx.Model.OpenFile(otherPath) as ClipDocumentViewModel;
                await ctx.SettleAsync();
                if (other is not null)
                {
                    var otherCtl = PoseEditController.For(other);
                    ctx.Check(otherCtl.Tool == PoseTool.Rotate && otherCtl.Space == OffsetSpace.Parent && !otherCtl.IkEnabled && otherCtl.AutoKey == !saved.AutoKey,
                        $"a newly opened document starts with the persisted switches ({other.DisplayName}: {otherCtl.Tool}, {otherCtl.Space}, IK {otherCtl.IkEnabled})");
                    otherCtl.Tool = PoseTool.Move;
                    otherCtl.Space = OffsetSpace.Model;
                    otherCtl.IkEnabled = true;
                    ctx.Check(ctl.Tool == PoseTool.Move && ctl.Space == OffsetSpace.Model && ctl.IkEnabled
                        && PoseEditSettings.Read(settings) == (PoseTool.Move, OffsetSpace.Model, true),
                        "a change in one document applies to every open document and to the settings (app-wide)");
                    ctx.Model.CloseDocument(other);
                    ctx.Model.ActiveDocument = doc;
                    await ctx.SettleAsync();
                }
            }
            else ctx.Log("selftest fixes: ult2_run.rfa is not beside the clip; the new-document check was skipped");
        }
        finally
        {
            ctl.Tool = saved.Tool;
            ctl.Space = saved.Space;
            ctl.IkEnabled = saved.IkEnabled;
            ctl.AutoKey = saved.AutoKey;
            // Leave the in-memory settings exactly as they were (they are never saved in this run anyway).
            if (hadPose) settings.Values[PoseEditSettings.SettingsKey] = rawPose; else settings.Values.Remove(PoseEditSettings.SettingsKey);
            if (hadViewport) settings.Values[ViewportDisplaySettings.SettingsKey] = rawViewport; else settings.Values.Remove(ViewportDisplaySettings.SettingsKey);
        }
    }

    // ── 5. System clipboard ─────────────────────────────────────────────────

    private static Task ClipboardRoundTrip(SelfTestContext ctx, ClipDocumentViewModel doc)
    {
        var clip = doc.Current;
        var tl = doc.Timeline;
        int bone = Enumerable.Range(0, clip.BoneCount).OrderByDescending(b => clip.Bones[b].RotationKeys.Length).First();
        var sel = KeySelection.Bones(clip, [bone]);
        doc.SetKeySelection(sel);
        bool copied = tl.Copy();
        var expectedJson = KeyClipboard.Copy(clip, sel, doc.ClipboardBoneNames).ToJson();
        if (KeyClipboardStore.LastPutFailed)
        {
            var ex = SystemClipboard.LastError;
            ctx.Log($"selftest note: clipboard unavailable — the Windows clipboard could not be opened for writing: "
                + (ex is null ? "no exception recorded" : $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}")
                + "; the keys were kept in-process (the paste below uses that copy)");
            ctx.Check(copied, "Ctrl+C keeps the keys in-process when the system clipboard cannot be opened");
        }
        else
        {
            bool read = SystemClipboard.TryGetText(out string? text);
            bool contains = false;
            try { contains = Clipboard.ContainsData(DataFormats.UnicodeText); }
            catch (System.Runtime.InteropServices.ExternalException) { }
            ctx.Check(read && contains && text == expectedJson, "the system clipboard holds exactly the copied KeyClipboard JSON (read back with GetDataObject)");
            KeyClipboard? parsed = null;
            try { parsed = text is null ? null : KeyClipboard.FromJson(text); }
            catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException) { }
            ctx.Check(parsed?.KeyCount == sel.Count, $"the clipboard text parses as {sel.Count} keys");
        }

        // Paste into the same clip at another time (from the system clipboard when it worked).
        int at = clip.StartTime + RfaClip.TicksPerFrame * 5;
        doc.Playback.Seek(at);
        var expected = KeyClipboard.FromJson(expectedJson).Paste(clip, at, doc.ClipboardBoneNames);
        int steps = doc.History.UndoLabels.Count;
        bool pasted = tl.Paste();
        ctx.Check(pasted && TimelineSelfTests.SameKeys(doc.Current, expected.Clip) && doc.KeySelection.Keys.SequenceEqual(expected.Pasted.Keys)
            && doc.History.UndoLabels.Count == steps + 1,
            $"pasting it at another time equals KeyClipboard.Paste ('{doc.UndoLabel}', {expected.Pasted.Count} keys)");
        doc.Undo();
        ctx.Check(ReferenceEquals(doc.Current, clip), "undo removes the pasted keys");
        return Task.CompletedTask;
    }

    // ── 3. Signed ease bytes ────────────────────────────────────────────────

    private static async Task NegativeEases(SelfTestContext ctx, ClipDocumentViewModel doc)
    {
        var clip = doc.Current;
        var inspector = doc.KeyInspector;
        var bones = Enumerable.Range(0, clip.BoneCount).Where(b => clip.Bones[b].RotationKeys.Length >= 4).ToList();
        if (bones.Count == 0)
        {
            ctx.Log("selftest fixes: no bone with 4 rotation keys; ease checks skipped");
            return;
        }
        int bone = bones[0];
        var negKey = new KeyRef(bone, KeyKind.Rotation, 1);
        var otherKey = new KeyRef(bone, KeyKind.Rotation, 2);
        // The test clip has no negative eases (no stock clip does): store some, as a non-stock file could.
        var withNegative = ClipEdit.SetEases(clip, KeySelection.Of(negKey), -64, -128);
        doc.Apply("Selftest: negative eases", _ => withNegative);
        var stored = doc.Current;
        int steps = doc.History.UndoLabels.Count;

        // Show it in the Key inspector, on screen, and check that showing it changed nothing.
        ctx.Model.IsInspectorVisible = true;
        doc.SelectedInspectorTab = doc.InspectorTabs.First(t => t.Id == "key");
        doc.Selection.Select(bone);
        doc.SetKeySelection(KeySelection.Of(negKey));
        await ctx.YieldAsync();
        ctx.Window.UpdateLayout();
        await ctx.YieldAsync();
        bool onScreen = TimelineSelfTests.FindVisible<KeyInspectorView>(ctx.Window) is not null;
        ctx.Check(ReferenceEquals(doc.Current, stored) && doc.History.UndoLabels.Count == steps,
            $"selecting a key with negative eases changes nothing (Key inspector {(onScreen ? "on screen" : "not on screen")}; no edit, no undo step)");
        double expectIn = -64 * 100.0 / 127, expectOut = -128 * 100.0 / 127;
        ctx.Check(Math.Abs(inspector.EaseIn.Value - expectIn) < 1e-6 && Math.Abs(inspector.EaseOut.Value - expectOut) < 1e-6,
            $"the ease boxes show the stored signed bytes ({inspector.EaseIn.Value:0.##} %, {inspector.EaseOut.Value:0.##} %)");
        ctx.Check(inspector.EaseInByteText == string.Format(CultureInfo.CurrentCulture, "byte {0}", -64)
            && inspector.EaseOutByteText == string.Format(CultureInfo.CurrentCulture, "byte {0}", -128),
            $"the byte readouts show the true values ('{inspector.EaseInByteText}', '{inspector.EaseOutByteText}')");
        ctx.Check(inspector.EaseIn.SliderValue == 0 && inspector.EaseOut.SliderValue == 0, "the sliders sit at 0 without rewriting the bytes");
        ctx.Check(inspector.HasNegativeEase && inspector.CurveInB < 0 && inspector.CurveOutA < 0,
            $"the negative bytes are explained and drawn signed ('{inspector.NegativeEaseNote}')");
        // The engine's curve with a negative ease-out jumps right after the key (ca_ease reads signed bytes).
        float jump = Cairn.Rfa.Animation.ClipSampler.Ease(1e-5f, -128 / 127f, 0f);
        ctx.Check(Math.Abs(jump - 128 / 127f / (2 + 128 / 127f)) < 1e-3, $"ca_ease with ease-out −128 jumps {jump:P1} of the segment at once (|a| / (2 + |a|))");

        // Editing another key's ease leaves every other key bit-identical (the negative bytes included).
        doc.SetKeySelection(KeySelection.Of(otherKey));
        inspector.EaseIn.Value = 25;
        var edited = doc.Current;
        bool othersIdentical = true;
        for (int b = 0; b < stored.BoneCount; b++)
        {
            var before = stored.Bones[b].RotationKeys;
            var after = edited.Bones[b].RotationKeys;
            if (before.Length != after.Length) { othersIdentical = false; break; }
            for (int i = 0; i < before.Length; i++)
            {
                if (b == bone && i == otherKey.Index) continue;
                if (before[i] != after[i]) othersIdentical = false;
            }
            if (!stored.Bones[b].PositionKeys.SequenceEqual(edited.Bones[b].PositionKeys)) othersIdentical = false;
        }
        var e = edited.Bones[bone].RotationKeys[otherKey.Index];
        var s = stored.Bones[bone].RotationKeys[otherKey.Index];
        ctx.Check(othersIdentical && e == s with { EaseIn = (sbyte)Math.Round(25 * 127 / 100.0) }
            && edited.Bones[bone].RotationKeys[negKey.Index] is { EaseIn: -64, EaseOut: -128 },
            $"an ease edit on one key changes only that key's byte; every other key (the negative one too) stays bit-identical ('{doc.UndoLabel}')");
        // And the file bytes: the edited clip and the stored one differ in exactly one byte.
        byte[] a = RfaWriter.Write(stored), z = RfaWriter.Write(edited);
        int diff = a.Length == z.Length ? a.Zip(z).Count(p => p.First != p.Second) : -1;
        ctx.Check(diff == 1, $"written out, the ease edit differs from the original in exactly one byte ({diff})");
        doc.Undo();
        doc.Undo();
        ctx.Check(ReferenceEquals(doc.Current, clip), "undo restores the clip after the ease checks");
    }

    // ── 1. Pinned summary row ───────────────────────────────────────────────

    private static async Task PinnedSummaryRow(SelfTestContext ctx, ClipDocumentViewModel doc)
    {
        ctx.Model.IsBottomVisible = true;
        ctx.Model.SelectedBottomTab = ctx.Model.BottomTabs.First(t => t.Id == "timeline");
        await ctx.YieldAsync();
        ctx.Window.UpdateLayout();
        var view = TimelineSelfTests.FindVisible<TimelineView>(ctx.Window);
        if (view is null)
        {
            ctx.Check(false, "the Timeline view is on screen");
            return;
        }
        var surface = view.TimelineSurface;
        var rows = doc.Timeline.VisibleRows;
        double oldHeight = view.Height;
        // Short enough that the bone rows must scroll.
        view.Height = Controls.TimelineSurface.RulerHeight + 6 * Controls.TimelineSurface.RowHeight + 40;
        ctx.Window.UpdateLayout();
        surface.SetVerticalOffset(1e6);
        double mid = Controls.TimelineSurface.RowHeight / 2;
        int top = surface.RowIndexAt(Controls.TimelineSurface.RulerHeight + mid);
        int firstScrolled = surface.RowIndexAt(surface.ScrollTop + mid);
        int lastShown = surface.RowIndexAt(surface.ActualHeight - 1);
        ctx.Check(surface.VerticalOffset > 0 && surface.PinnedRows == 1 && top == 0 && rows[0].IsSummary
            && Math.Abs(surface.RowTopOf(0) - Controls.TimelineSurface.RulerHeight) < 1e-9,
            $"scrolled to the bottom ({surface.VerticalOffset:0} DIPs), the summary row is still pinned under the ruler");
        ctx.Check(firstScrolled > 1 && (lastShown == rows.Count - 1 || surface.RowIndexAt(surface.RowTopOf(rows.Count - 1) + mid) == rows.Count - 1),
            $"the bone rows scroll under it (row {firstScrolled} first below it, the last row {rows.Count - 1} reachable)");
        surface.BringRowIntoView(1);
        ctx.Check(surface.VerticalOffset == 0 && surface.RowIndexAt(surface.ScrollTop + mid) == 1, "bringing the first bone row into view scrolls back to the top");
        view.Height = oldHeight;
        ctx.Window.UpdateLayout();
    }

    // ── Screenshot switches ─────────────────────────────────────────────────

    [ScreenshotStep(60)]
    public static async Task Screenshot(ScreenshotContext ctx)
    {
        string? scroll = ctx.Extra("timeline-scroll"), eases = ctx.Extra("negative-ease");
        if (scroll is null && eases is null) return;
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("--timeline-scroll / --negative-ease need a clip document");
            return;
        }
        if (eases is not null)
        {
            var parts = eases.Split(',');
            int bone = doc.Selection.Active >= 0 ? doc.Selection.Active : 0;
            if (parts.Length == 2 && sbyte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte ein)
                && sbyte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte eout)
                && bone < doc.Current.BoneCount && doc.Current.Bones[bone].RotationKeys.Length > 2)
            {
                var key = new KeyRef(bone, KeyKind.Rotation, 1);
                doc.Apply("Diagnostic eases", c => ClipEdit.SetEases(c, KeySelection.Of(key), ein, eout));
                doc.SetKeySelection(KeySelection.Of(key));
                ctx.Model.IsInspectorVisible = true;
                doc.SelectedInspectorTab = doc.InspectorTabs.First(t => t.Id == "key");
                ctx.Log($"negative-ease: {doc.BoneDisplayName(bone)} rotation key 1 eases {ein}, {eout}; note '{doc.KeyInspector.NegativeEaseNote}'");
            }
            else ctx.Log($"--negative-ease '{eases}' expects <in>,<out> signed bytes and a selected bone with 3+ rotation keys");
        }
        if (scroll is not null && double.TryParse(scroll, NumberStyles.Float, CultureInfo.InvariantCulture, out double rowsDown))
        {
            ctx.Model.IsBottomVisible = true;
            ctx.Model.SelectedBottomTab = ctx.Model.BottomTabs.First(t => t.Id == "timeline");
            await ctx.SettleAsync();
            ctx.Window.UpdateLayout();
            if (TimelineSelfTests.FindVisible<TimelineView>(ctx.Window) is { } view)
            {
                view.TimelineSurface.SetVerticalOffset(rowsDown * Controls.TimelineSurface.RowHeight);
                ctx.Log($"timeline scrolled to {view.TimelineSurface.VerticalOffset:0} DIPs (summary row pinned: {view.TimelineSurface.PinnedRows == 1})");
            }
            else ctx.Log("--timeline-scroll: the Timeline view is not on screen");
        }
        await ctx.SettleAsync();
    }
}
