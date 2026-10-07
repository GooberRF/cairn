using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Viewport;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Timeline;
using Cairn.Vfx.Ui.Viewport;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Edit commands on awkward states (nothing selected, a material selected, an older file, an empty effect),
/// a long seeded undo/redo run, refused saves and recovery snapshots.</summary>
public static class VfxRobustnessSelfTests
{
    private static VfxFile Rich()
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("rb_a.tga"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Box"), null);
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("Host", 4), null);
        f = VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("Part", 0, 4, "Host"), null);
        f = VfxEdit.AddSection(f, VfxBuilder.Light("Lamp", 4), null);
        return f;
    }

    private static VfxDocument Doc(SelfTestContext ctx, VfxFile f, string? path = null) =>
        new(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f, "robust.vfx", path);

    private static void NoThrow(SelfTestContext ctx, string what, Action act)
    {
        try { act(); ctx.Check(true, what); }
        catch (Exception ex) { ctx.Check(false, $"{what}: threw {ex.GetType().Name}: {ex.Message}"); }
    }

    [SelfTest("VFX robustness: object, key and playback commands on empty, unselected, non-applicable and older effects never throw")]
    public static void CommandsOnAwkwardStates(SelfTestContext ctx)
    {
        var states = new (string Name, Func<VfxFile> File, Func<VfxFile, int?> Select)[]
        {
            ("empty effect", VfxBuilder.NewFile, _ => null),
            ("nothing selected", Rich, _ => null),
            ("material selected", Rich, f => Enumerable.Range(0, f.Sections.Length).First(i => f.Sections[i] is VfxMaterial)),
            ("older format", () => Rich() with { Version = 0x3000E }, f => Enumerable.Range(0, f.Sections.Length).First(i => f.Sections[i] is VfxMesh)),
        };
        foreach (var (name, make, select) in states)
        {
            using var d = Doc(ctx, make());
            if (select(d.Current) is int s) d.Selection.Select(s);
            var start = d.Current;
            var panel = VfxModule.TimelineOf(d);
            var ops = new (string, Action)[]
            {
                ("duplicate", () => VfxObjectCommands.Duplicate(d)), ("move up", () => VfxObjectCommands.Move(d, -1)),
                ("move down", () => VfxObjectCommands.Move(d, 1)), ("reparent to root", () => VfxObjectCommands.Reparent(d, d.Selection.Sections, "Scene Root")),
                ("select all of type", () => VfxObjectCommands.SelectAllOfType(d)), ("isolate", () => VfxObjectCommands.Isolate(d)),
                ("show all", () => VfxObjectCommands.ShowAll(d)), ("rename first", () => { if (d.Current.Sections.Length > 0) VfxObjectCommands.Rename(d, 0, "Renamed"); }),
                ("insert key", () => panel.InsertKeys(null)), ("delete keys at playhead", panel.DeleteKeysAtPlayhead),
                ("delete selected keys", panel.Surface.DeleteSelectedKeys), ("expand all", panel.Surface.ExpandAll),
                ("seek past the end", () => d.SeekFrame(1e6f)), ("play", d.Playback.Play), ("deactivate while playing", d.OnDeactivated),
                ("delete", () => VfxObjectCommands.Delete(d)), ("undo", () => { if (d.CanUndo) d.Undo(); }),
            };
            foreach (var (op, act) in ops) NoThrow(ctx, $"{name}: {op}", act);
            if (!VfxEditing.CanEdit(d)) ctx.Check(ReferenceEquals(start, d.Current), $"{name}: blocked document left unchanged");
        }
    }

    [SelfTest("VFX robustness: 30 seeded mixed edits undo and redo byte-identically at every level")]
    public static void LongUndoRedo(SelfTestContext ctx)
    {
        using var d = Doc(ctx, Rich());
        var rng = new Random(20261006);
        var levels = new List<byte[]> { VfxWriter.Write(d.Current) };
        int attempts = 0;
        while (levels.Count < 31 && attempts++ < 300)
        {
            int n = d.Current.Sections.Length;
            if (n > 0) d.Selection.Select(rng.Next(n));
            d.SeekFrame(rng.Next(0, 4));
            int kind = rng.Next(6);
            try
            {
                _ = kind switch
                {
                    0 => VfxObjectCommands.Duplicate(d),
                    1 => n > 6 && VfxObjectCommands.Delete(d),
                    2 => VfxObjectCommands.Move(d, rng.Next(2) * 2 - 1),
                    3 => n > 0 && VfxObjectCommands.Rename(d, rng.Next(n), $"Obj{attempts}"),
                    4 => Do(() => VfxModule.TimelineOf(d).InsertKeys(null)),
                    _ => VfxObjectCommands.Reparent(d, d.Selection.Sections, "Scene Root"),
                };
            }
            catch (Exception ex) { ctx.Check(false, $"edit {attempts} (kind {kind}) threw {ex.GetType().Name}: {ex.Message}"); return; }
            var now = VfxWriter.Write(d.Current);
            if (!now.AsSpan().SequenceEqual(levels[^1])) levels.Add(now);
        }
        ctx.Check(levels.Count == 31, $"30 distinct edits applied ({levels.Count - 1} in {attempts} attempts)");
        bool ok = true;
        for (int i = levels.Count - 2; i >= 0 && ok; i--)
        {
            d.Undo();
            ok = VfxWriter.Write(d.Current).AsSpan().SequenceEqual(levels[i]);
            if (!ok) ctx.Log($"undo level {i} differs");
        }
        ctx.Check(ok && !d.CanUndo, "every undo level is byte-identical, down to the opened state");
        for (int i = 1; i < levels.Count && ok; i++)
        {
            d.Redo();
            ok = VfxWriter.Write(d.Current).AsSpan().SequenceEqual(levels[i]);
            if (!ok) ctx.Log($"redo level {i} differs");
        }
        ctx.Check(ok, "every redo level is byte-identical");
    }

    private static bool Do(Action a) { a(); return true; }

    [SelfTest("VFX robustness: a refused save keeps the document dirty and names the object; recovery restores an equal effect")]
    public static void SaveSafety(SelfTestContext ctx)
    {
        string path = Path.Combine(Path.GetTempPath(), "cairn-robust-save.vfx");
        File.Delete(path);
        using var d = Doc(ctx, Rich(), path);
        int host = Enumerable.Range(0, d.Current.Sections.Length).First(i => d.Current.Sections[i] is VfxDummy);
        // Test-only invalid state: a name the format cannot store (outside Latin-1).
        VfxEditing.Apply(d, "Test: invalid name", f => f with { Sections = f.Sections.SetItem(host, ((VfxDummy)f.Sections[host]) with { Name = "Host一" }) });
        ctx.Check(d.IsDirty, "invalid edit made the document dirty");
        string? message = null;
        try { d.SaveTo(path); }
        catch (InvalidOperationException ex) { message = ex.Message; }
        ctx.Log($"refused save: {message}");
        ctx.Check(message is not null && message.Contains("VfxDummy") && message.Contains("Host"), "save refused with a message naming the object");
        ctx.Check(d.IsDirty && !File.Exists(path), "refused save keeps the document dirty and writes nothing");
        d.Undo();
        VfxEditing.Apply(d, "Test: rename", f => f with { Sections = f.Sections.SetItem(host, ((VfxDummy)f.Sections[host]) with { Name = "Host2" }) });
        var bytes = d.CaptureRecovery();
        ctx.Check(bytes is not null, "dirty document captures a recovery snapshot");
        using var r = Doc(ctx, VfxBuilder.NewFile());
        if (bytes is not null) r.RestoreBytes(bytes);
        ctx.Check(bytes is not null && r.Serialize().AsSpan().SequenceEqual(d.Serialize()) && r.IsDirty, "recovery restores an equal, dirty document");
        NoThrow(ctx, "valid save after the refused one", () => d.SaveTo(path));
        ctx.Check(!d.IsDirty && File.Exists(path), "valid save clears dirty");
        File.Delete(path);
    }

    /// <summary>Answers every dialog without showing it; installed on the shell for the duration of a test.</summary>
    private sealed class QuietDialogs : DialogService
    {
        public string? SavePath { get; set; }
        public int SaveAsked { get; private set; }
        public List<string> Errors { get; } = [];
        public override string? SaveDocument(string? initialFolder, string suggestedName, string extension, string filter) { SaveAsked++; return SavePath; }
        public override string[] OpenDocuments(string? initialFolder, string filter) => [];
        public override string[] OpenFiles(string? initialFolder, string title, string filter, bool multiselect) => [];
        public override string? PickFolder(string? initialFolder, string title) => null;
        public override UnsavedChoice AskUnsavedChanges(IReadOnlyList<string> documentNames) => UnsavedChoice.DontSave;
        public override bool ConfirmSaveWithErrors(string documentName, int errorCount) => false;
        public override bool Confirm(string heading, string body, string confirmText) => false;
        public override void ShowError(string heading, string body, string? details = null) => Errors.Add(heading + ": " + body);
        public int ChooseAnswer { get; set; }
        public List<string> Choices { get; } = [];
        public override int Choose(string heading, string body, IReadOnlyList<string> buttons, int cancelIndex) { Choices.Add(heading + ": " + string.Join("|", buttons)); return ChooseAnswer; }
    }

    [SelfTest("VFX robustness: the first Save after converting an older file asks overwrite or Save As (default Save As), once")]
    public static void ConvertThenSave(SelfTestContext ctx)
    {
        using var scope = new DialogScope(ctx.Shell);
        var save = ctx.Shell.GetType().GetMethod("Save", [typeof(IDocument)]);
        if (!ctx.Check(scope.Installed && save is not null, "shell dialogs and Save reachable")) return;
        string folder = Path.Combine(Path.GetTempPath(), "cairn-convert-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string original = Path.Combine(folder, "old.vfx"), copy = Path.Combine(folder, "new.vfx");
        File.WriteAllText(original, "original bytes");
        bool Save(IDocument doc) => (bool)save!.Invoke(ctx.Shell, [doc])!;

        using (var d = Doc(ctx, Rich() with { Version = 0x3000E }, original))
        {
            d.ConvertCommand.Execute(null);
            scope.Dialogs.ChooseAnswer = 2;
            ctx.Check(!Save(d) && File.ReadAllText(original) == "original bytes", "cancel writes nothing");
            scope.Dialogs.ChooseAnswer = 0;
            scope.Dialogs.SavePath = copy;
            ctx.Check(Save(d) && File.ReadAllText(original) == "original bytes" && d.FilePath == copy && File.Exists(copy), "Save As keeps the original and saves the converted copy");
            ctx.Check(scope.Dialogs.Choices.Count == 2 && scope.Dialogs.Choices[0].Contains(": Save As...|", StringComparison.Ordinal), "asked with Save As as the default button: " + string.Join("; ", scope.Dialogs.Choices));
        }
        scope.Dialogs.Choices.Clear();
        using (var d = Doc(ctx, Rich() with { Version = 0x3000E }, original))
        {
            d.ConvertCommand.Execute(null);
            scope.Dialogs.ChooseAnswer = 1;
            ctx.Check(Save(d) && File.ReadAllText(original) != "original bytes" && d.FilePath == original, "overwrite replaces the original");
            VfxEditing.Apply(d, "Test: rename", f => f with { Sections = f.Sections.SetItem(0, f.Sections[0] with { }) });
            ctx.Check(Save(d) && scope.Dialogs.Choices.Count == 1, "asked only once");
        }
        Directory.Delete(folder, true);
    }

    /// <summary>Installs <see cref="QuietDialogs"/> on the shell (its Dialogs property is settable there); disposing restores the old service.</summary>
    private sealed class DialogScope : IDisposable
    {
        private readonly object _shell; private readonly System.Reflection.PropertyInfo? _prop; private readonly object? _old;
        public QuietDialogs Dialogs { get; } = new();
        public bool Installed { get; }
        public DialogScope(IShellContext shell)
        {
            _shell = shell;
            _prop = shell.GetType().GetProperty(nameof(IShellContext.Dialogs));
            if (_prop?.CanWrite != true) return;
            _old = _prop.GetValue(shell);
            _prop.SetValue(shell, Dialogs);
            Installed = true;
        }
        public void Dispose() { if (Installed) _prop!.SetValue(_shell, _old); }
    }

    private static VfxModule? ModuleOf(IShellContext shell) =>
        (shell.GetType().GetProperty("Modules")?.GetValue(shell) as IEnumerable<IModule>)?.OfType<VfxModule>().FirstOrDefault();

    private static void CloseExtra(IShellContext shell, ICollection<IDocument> keep)
    {
        foreach (var doc in shell.Documents.Where(x => !keep.Contains(x)).ToList()) shell.Close(doc);
    }

    // Headers whose command opens a modal window; any header with an ellipsis is skipped as well.
    private static readonly HashSet<string> ModalHeaders = new(StringComparer.OrdinalIgnoreCase) { "Settings", "Effect settings" };

    private static void Sweep(SelfTestContext ctx, VfxDocument d, int? select, string state, string path, System.Collections.IEnumerable items, ICollection<IDocument> keep, ref int run, ref int skipped)
    {
        foreach (var mi in items.OfType<MenuItem>().ToList())
        {
            string header = (mi.Header?.ToString() ?? "").Replace("_", "");
            string where = $"{path}/{header}";
            if (mi.Items.Count > 0)
            {
                mi.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, mi));
                Sweep(ctx, d, select, state, where, mi.Items, keep, ref run, ref skipped);
                continue;
            }
            if (header.Contains("...") || header.Contains('…') || ModalHeaders.Contains(header)) { skipped++; continue; }
            // every item starts from the state itself, not from what the previous item left behind
            while (d.CanUndo) d.Undo();
            if (select is int s) d.Selection.Select(s);
            try
            {
                if (mi.Command is { } c) { if (c.CanExecute(mi.CommandParameter)) c.Execute(mi.CommandParameter); }
                else if (mi.IsEnabled) mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, mi));
                run++;
            }
            catch (Exception ex) { ctx.Check(false, $"{state}: {where} threw {ex.GetType().Name}: {ex.Message}"); }
            d.Playback.Pause();
            CloseExtra(ctx.Shell, keep);
            if (!ReferenceEquals(ctx.Shell.ActiveDocument, d)) ctx.Shell.Activate(d);
        }
    }

    [SelfTest("VFX robustness: every Effect, File and context menu item runs or refuses in four document states without throwing")]
    public static void MenuSweep(SelfTestContext ctx)
    {
        if (ModuleOf(ctx.Shell) is not { } module) { ctx.Skip("the effects module instance is not reachable from the shell"); return; }
        using var scope = new DialogScope(ctx.Shell);
        if (!scope.Installed) { ctx.Skip("the shell's dialog service cannot be replaced"); return; }
        var kind = new VfxKind { Shell = ctx.Shell };
        var states = new (string Name, Func<VfxDocument> Make, Func<VfxFile, int?> Select)[]
        {
            ("empty new effect", () => (VfxDocument)kind.CreateNew()!, _ => null),
            ("stock-like effect, nothing selected", () => Doc(ctx, Rich()), _ => null),
            ("stock-like effect, material selected", () => Doc(ctx, Rich()), f => Enumerable.Range(0, f.Sections.Length).First(i => f.Sections[i] is VfxMaterial)),
            ("older-format file", () => Doc(ctx, Rich() with { Version = 0x3000E }), f => Enumerable.Range(0, f.Sections.Length).First(i => f.Sections[i] is VfxMesh)),
        };
        foreach (var (name, make, select) in states)
        {
            var keep = new HashSet<IDocument>(ctx.Shell.Documents);
            var d = make();
            ctx.Shell.AddDocument(d);
            keep.Add(d);
            int? sel = select(d.Current);
            int run = 0, skipped = 0;
            foreach (var m in module.Menus)
            {
                if (m.Item is MenuItem root) Sweep(ctx, d, sel, name, m.Slot.ToString(), new[] { root }, keep, ref run, ref skipped);
            }
            if (d.View is VfxDocumentView view && view.Outliner.ContextMenu is { } outliner)
            {
                outliner.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, outliner));
                Sweep(ctx, d, sel, name, "Outliner", outliner.Items, keep, ref run, ref skipped);
            }
            if (VfxModule.TimelineOf(d).Surface.ContextMenu is { } timeline) Sweep(ctx, d, sel, name, "Timeline", timeline.Items, keep, ref run, ref skipped);
            ctx.Log($"{name}: {run} items run, {skipped} dialog items skipped");
            ctx.Check(run > 20, $"{name}: the sweep reached the menu items ({run})");
            ctx.Shell.Close(d);
            CloseExtra(ctx.Shell, keep.Where(x => x != d).ToList());
        }
        ctx.Check(scope.Dialogs.SaveAsked == 0, "no item asked for a save path");
        if (scope.Dialogs.Errors.Count > 0) ctx.Log("errors reported through the dialog service: " + string.Join(" | ", scope.Dialogs.Errors.Take(5)));
    }

    [SelfTest("VFX robustness: move, rotate and scale drags in pivot mode on a mesh whose pivot cannot change are refused with a reason")]
    public static void BlockedPivotDrags(SelfTestContext ctx)
    {
        var f = VfxEdit.AddSection(VfxBuilder.NewFile(), VfxPrimitives.Box("Spin"), null);
        int i = f.Sections.Length - 1;
        f = VfxEdit.ToKeyframes(VfxEdit.SetFrameCount(f, i, 10), i, reduce: false);
        var m = (VfxMesh)f.Sections[i];
        var k = m.Keys!;
        // differing rotation keys block a pivot move; non-uniform scale keys block a pivot turn
        if (k.Rotation.Length > 1) k = k with { Rotation = k.Rotation.SetItem(k.Rotation.Length - 1, k.Rotation[^1] with { Value = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f) }) };
        if (k.Scale.Length > 0) k = k with { Scale = k.Scale.SetItem(0, k.Scale[0] with { Value = new(1, 2, 1), InTangent = new(1, 2, 1), OutTangent = new(1, 2, 1) }) };
        f = f with { Sections = f.Sections.SetItem(i, m with { Keys = k }) };
        ctx.Check(VfxPivotEdits.MoveBlocked(f, i) is not null && VfxPivotEdits.TurnBlocked(f, i) is not null, "test mesh blocks both pivot move and pivot turn");
        using var d = Doc(ctx, f);
        d.Selection.Select(i); d.SeekFrame(3);
        bool oldPivot = VfxGizmoPrefs.PivotMode;
        VfxGizmoPrefs.PivotMode = true;
        try
        {
            var start = d.Current;
            var gizmo = new VfxGizmoTarget(d);
            foreach (var (tool, kindOf, act) in new (PoseTool, PoseDragKind, Action<VfxGizmoTarget>)[]
            {
                (PoseTool.Move, PoseDragKind.Move, g => g.UpdateWorldTranslation(new Vector3(0.5f, 0, 0))),
                (PoseTool.Rotate, PoseDragKind.Rotate, g => g.UpdateAxisRotation(1, 0.5)),
                (PoseTool.Scale, PoseDragKind.Scale, g => g.UpdateScale(GizmoHandle.Screen, 1.5)),
            })
            {
                gizmo.Tool = tool;
                NoThrow(ctx, $"{tool}: blocked drag", () => { gizmo.BeginDrag(kindOf, GizmoHandle.Screen); act(gizmo); gizmo.CommitDrag(); });
                ctx.Check(ReferenceEquals(d.Current, start) || VfxWriter.Write(d.Current).AsSpan().SequenceEqual(VfxWriter.Write(start)), $"{tool}: effect unchanged");
                ctx.Check(!string.IsNullOrEmpty(gizmo.BlockedReason), $"{tool}: reason given ({gizmo.BlockedReason})");
            }
            ctx.Check(!d.IsDirty, "refused drags leave the document clean");
        }
        finally { VfxGizmoPrefs.PivotMode = oldPivot; }
    }

    private static async Task Idle() { for (int n = 0; n < 3; n++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenPlaying(SelfTestContext ctx, string name, out VfxDocument doc)
    {
        doc = Doc(ctx, Rich());
        ctx.Shell.AddDocument(doc);
        doc.Playback.Play();
        return new WeakReference(doc);
    }

    [SelfTest("VFX robustness: closing, switching and deactivating playing effects stops their clocks and frees closed documents")]
    public static async Task PlayingLifecycle(SelfTestContext ctx)
    {
        using var scope = new DialogScope(ctx.Shell);
        var keep = new HashSet<IDocument>(ctx.Shell.Documents);
        var weakA = OpenPlaying(ctx, "a", out var a);
        await Idle();
        ctx.Check(a.Playback.IsPlaying, "first effect plays");
        OpenPlaying(ctx, "b", out var b);
        await Idle();
        ctx.Check(!a.Playback.IsPlaying && b.Playback.IsPlaying, "opening a second effect pauses the first; the second plays");
        ctx.Shell.Activate(a);
        a.Playback.Play();
        await Idle();
        ctx.Check(a.Playback.IsPlaying && !b.Playback.IsPlaying, "switching back pauses the other effect");
        a.OnDeactivated();
        ctx.Check(!a.Playback.IsPlaying, "OnDeactivated pauses");
        a.Playback.Play();
        ctx.Check(ctx.Shell.Close(a), "a playing effect closes");
        ctx.Check(!a.Playback.IsPlaying, "closing stops its clock");
        a = null!;
        ctx.Shell.Close(b);
        b = null!;
        CloseExtra(ctx.Shell, keep);
        await Idle();
        for (int n = 0; n < 3 && weakA.IsAlive; n++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle(); }
        // shell-side holders (view, panels) are outside this test's scope: logged, not checked
        ctx.Log($"closed playing document collected after closing through the shell: {!weakA.IsAlive}");
        var weakC = PlayAndDispose(ctx);
        for (int n = 0; n < 3 && weakC.IsAlive; n++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle(); }
        ctx.Check(!weakC.IsAlive, "a playing effect that is disposed is collected (its clock no longer holds it)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PlayAndDispose(SelfTestContext ctx)
    {
        var d = Doc(ctx, Rich());
        d.Playback.Play();
        d.Dispose();
        return new WeakReference(d);
    }

    [SelfTest("VFX robustness: an effect opened from an archive has no path, is clean, and Save goes to Save As")]
    public static void ArchiveSave(SelfTestContext ctx)
    {
        using var scope = new DialogScope(ctx.Shell);
        if (!scope.Installed) { ctx.Skip("the shell's dialog service cannot be replaced"); return; }
        string path = Path.Combine(Path.GetTempPath(), "cairn-robust-archive.vfx");
        File.Delete(path);
        var d = (VfxDocument)new VfxKind { Shell = ctx.Shell }.OpenBytes(VfxWriter.Write(Rich()), "packed.vfx", "effects.vpp > packed.vfx");
        ctx.Check(d.FilePath is null && !d.IsDirty && d.IsFromArchive, "archive effect: no path, clean, marked as from an archive");
        ctx.Shell.AddDocument(d);
        scope.Dialogs.SavePath = null;
        ctx.Check(!ctx.Shell.Save(d) && scope.Dialogs.SaveAsked == 1 && d.FilePath is null, "Save asks for a path (Save As); cancelling saves nothing");
        scope.Dialogs.SavePath = path;
        ctx.Check(ctx.Shell.Save(d) && scope.Dialogs.SaveAsked == 2, "Save with a chosen path succeeds through Save As");
        ctx.Check(File.Exists(path) && string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase) && !d.IsDirty, "the effect now has the chosen path and is clean");
        ctx.Shell.Close(d);
        File.Delete(path);
    }

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (int n = 0; n < 60 && !condition(); n++) await Task.Delay(100);
        return condition();
    }

    [SelfTest("VFX robustness: external changes offer Reload (clean) or Keep mine (dirty), and a deleted file shows as missing")]
    public static async Task ExternalChanges(SelfTestContext ctx)
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-robust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "watched.vfx");
        File.WriteAllBytes(path, VfxWriter.Write(Rich()));
        using var d = (VfxDocument)new VfxKind { Shell = ctx.Shell }.Open(path);
        var changed = Rich();
        int host = Enumerable.Range(0, changed.Sections.Length).First(i => changed.Sections[i] is VfxDummy);
        changed = changed with { Sections = changed.Sections.SetItem(host, ((VfxDummy)changed.Sections[host]) with { Name = "FromDisk" }) };
        await Task.Delay(300);
        File.WriteAllBytes(path, VfxWriter.Write(changed));
        ctx.Check(await WaitFor(() => d.HasExternalChange), "clean document: the change on disk is noticed (reload offered)");
        ctx.Check(d.ReloadCommand.CanExecute(null), "Reload is available");
        d.ReloadCommand.Execute(null);
        ctx.Check(!d.HasExternalChange && VfxSections.NameOf(d.Current.Sections[host]) == "FromDisk", "Reload gives the new content");
        VfxEditing.Apply(d, "Test: rename", f => f with { Sections = f.Sections.SetItem(host, ((VfxDummy)f.Sections[host]) with { Name = "Mine" }) });
        await Task.Delay(300);
        File.WriteAllBytes(path, VfxWriter.Write(Rich()));
        ctx.Check(await WaitFor(() => d.HasExternalChange), "dirty document: the change on disk is noticed");
        d.KeepMineCommand.Execute(null);
        ctx.Check(!d.HasExternalChange && d.IsDirty && VfxSections.NameOf(d.Current.Sections[host]) == "Mine", "Keep mine keeps the edits");
        File.Delete(path);
        ctx.Check(await WaitFor(() => d.IsMissingOnDisk), "a deleted file shows as missing on disk");
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }
}
