using System.Text;
using System.Windows.Input;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui;

/// <summary>One keyboard shortcut, as both a binding and a line in the Help window.</summary>
/// <param name="Category">The group it is listed under.</param>
/// <param name="Action">What it does, in plain language.</param>
/// <param name="Key">The key, or <see cref="Key.None"/> for a gesture the window does not bind.</param>
/// <param name="Modifiers">Modifier keys.</param>
/// <param name="Command">The shell command, when the window binds it; null when a control closer to the action handles it.</param>
/// <param name="GestureOverride">Text to show instead of the generated gesture.</param>
public sealed record ShortcutInfo(
    string Category,
    string Action,
    Key Key = Key.None,
    ModifierKeys Modifiers = ModifierKeys.None,
    Func<RfaWorkspace, ICommand>? Command = null,
    string? GestureOverride = null)
{
    /// <summary>The gesture as the user would write it, e.g. "Ctrl+Shift+S".</summary>
    public string Gesture
    {
        get
        {
            if (GestureOverride is not null) return GestureOverride;
            var text = new StringBuilder();
            if (Modifiers.HasFlag(ModifierKeys.Control)) text.Append("Ctrl+");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) text.Append("Alt+");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) text.Append("Shift+");
            text.Append(Key switch
            {
                Key.Delete => "Del",
                Key.None => string.Empty,
                _ => Key.ToString(),
            });
            return text.ToString();
        }
    }
}

/// <summary>
/// Every keyboard shortcut in one table: the shell builds its key bindings from the entries that name
/// a command, and Help › Keyboard Shortcuts lists the whole table, so the two cannot drift apart.
/// Phases 5 and 6 add their rows here.
/// </summary>
public static partial class Shortcuts
{
    public static IReadOnlyList<ShortcutInfo> All { get; } =
    [
        // ── File ─────────────────────────────────────────────────────────────
        new("File", "New clip (a starting pose for a character mesh)", Key.N, ModifierKeys.Control, m => m.NewClips.NewClipCommand),
        new("File", "New clip for the mesh in front, or a library character", GestureOverride: "Mesh menu, or right-click the character in the library"),
        new("File", "Open clips or meshes", Key.O, ModifierKeys.Control, m => m.OpenCommand),
        new("File", "Save", Key.S, ModifierKeys.Control, m => m.SaveCommand),
        new("File", "Save as", Key.S, ModifierKeys.Control | ModifierKeys.Shift, m => m.SaveAsCommand),
        new("File", "Save every open file", Key.S, ModifierKeys.Control | ModifierKeys.Alt, m => m.SaveAllCommand),
        new("File", "Close the current tab", Key.W, ModifierKeys.Control, m => m.CloseTabCommand),
        new("File", "Reopen the last closed tab", Key.T, ModifierKeys.Control | ModifierKeys.Shift, m => m.ReopenClosedTabCommand),
        new("File", "Close a tab with the mouse", GestureOverride: "Middle-click the tab"),
        new("File", "Next / previous tab", GestureOverride: "Ctrl+Tab / Ctrl+Shift+Tab"),
        new("File", "Quit", GestureOverride: "Alt+F4"),

        // ── Edit ─────────────────────────────────────────────────────────────
        new("Edit", "Undo (the menu names the step)", Key.Z, ModifierKeys.Control, m => m.UndoCommand),
        new("Edit", "Redo", Key.Y, ModifierKeys.Control, m => m.RedoCommand),
        new("Edit", "Redo (alternative)", Key.Z, ModifierKeys.Control | ModifierKeys.Shift, m => m.RedoCommand),
        new("Edit", "Step a number box", GestureOverride: "Up / Down or the wheel (Shift: ×10)"),
        new("Edit", "Commit a number box / put the old value back", GestureOverride: "Enter / Esc (number box focused)"),

        // ── Library ──────────────────────────────────────────────────────────
        new("Library", "Preview on the document in front (a clip on a mesh, a mesh under a clip)", GestureOverride: "Double-click or Enter (see Settings › Library)"),
        new("Library", "Open in a new tab", GestureOverride: "Ctrl+double-click, Ctrl+Enter or middle-click"),
        new("Library", "Preview by dragging", GestureOverride: "Drag an entry onto the viewport"),
        new("Library", "Clear the filter", GestureOverride: "Esc (filter box focused)"),

        // ── Viewport ─────────────────────────────────────────────────────────
        new("Viewport", "Orbit", GestureOverride: "Left-drag"),
        new("Viewport", "Pan", GestureOverride: "Middle-drag or Shift+left-drag"),
        new("Viewport", "Zoom towards the cursor", GestureOverride: "Mouse wheel"),
        new("Viewport", "Select a bone / add or remove one (its editor comes forward)", GestureOverride: "Click a joint / Ctrl+click"),
        new("Viewport", "Select a collision sphere or prop point (mesh tabs, when shown; its editor comes forward)", GestureOverride: "Click its outline or inside / its diamond"),
        new("Viewport", "Pick the next of several overlapping things (mesh tabs)", GestureOverride: "Click the same spot again"),
        new("Viewport", "Clear the bone selection", GestureOverride: "Esc (viewport focused)"),
        new("Viewport", "Frame the selection, or everything", Key.F, ModifierKeys.Control | ModifierKeys.Shift, m => m.FrameCommand,
            GestureOverride: "F (viewport focused) or Ctrl+Shift+F"),
        new("Viewport", "Front / side / top view", GestureOverride: "1 / 3 / 7 or the numpad (viewport focused)"),
        new("Viewport", "Perspective or orthographic", GestureOverride: "5 (viewport focused)"),

        // ── Gizmos: pose editing (clip tabs) and mesh editing (mesh tabs) ────
        new("Gizmos", "Select tool (no gizmo; clicks select)", GestureOverride: "Q (viewport focused)"),
        new("Gizmos", "Rotate tool (clip: bones; mesh: a bone's bind pose or a prop point)", GestureOverride: "E (viewport focused)"),
        new("Gizmos", "Move tool (clip: root, animated positions, IK on a hand or foot; mesh: a bone's bind pose, a collision sphere or a prop point)", GestureOverride: "W (viewport focused)"),
        new("Gizmos", "Rotate or move with the gizmo (one undo step per drag)", GestureOverride: "Left-drag a ring, arrow or the inside"),
        new("Gizmos", "Change a collision sphere's radius (mesh tabs, move tool)", GestureOverride: "Drag its outline or the round grip"),
        new("Gizmos", "Snap to 5° (rotate) or 1 cm (move, radius)", GestureOverride: "Hold Ctrl while dragging"),
        new("Gizmos", "Cancel the drag in progress", GestureOverride: "Esc while dragging"),

        // ── Playback ─────────────────────────────────────────────────────────
        new("Playback", "Play or pause", GestureOverride: "Space (viewport or time bar focused)"),
        new("Playback", "Play or pause from anywhere", Key.P, ModifierKeys.Control | ModifierKeys.Shift, m => m.PlayPauseCommand),
        new("Playback", "Step one frame", GestureOverride: "Left / Right (viewport or time bar focused)"),
        new("Playback", "Step ten frames", GestureOverride: "Shift+Left / Shift+Right (time bar focused)"),
        new("Playback", "First / last frame", GestureOverride: "Home / End (viewport or time bar focused)"),
        new("Playback", "Scrub without snapping to frames", GestureOverride: "Alt+drag on the time bar"),

        // ── Timeline (phase 5) ───────────────────────────────────────────────
        new("Timeline", "Select a key / toggle / add", GestureOverride: "Click / Ctrl+click / Shift+click a key"),
        new("Timeline", "Box-select keys", GestureOverride: "Drag on empty space (Ctrl or Shift: add)"),
        new("Timeline", "Select every key at a time", GestureOverride: "Click a mark in the 'All keys' row"),
        new("Timeline", "Move the selected keys (snaps to frames)", GestureOverride: "Drag a selected key (Alt: free)"),
        new("Timeline", "Copy the selected keys by dragging", GestureOverride: "Ctrl+drag a selected key"),
        new("Timeline", "Scale the selected keys about the playhead", GestureOverride: "Drag a grip on the ruler, or Alt+drag the first/last key"),
        new("Timeline", "Delete the selected keys", GestureOverride: "Del or Backspace (timeline focused)"),
        new("Timeline", "Clear the key selection, or cancel a drag", GestureOverride: "Esc (timeline focused)"),
        new("Timeline", "Play or pause / step a frame", GestureOverride: "Space / Left / Right (timeline focused)"),
        new("Timeline", "Zoom in / out", GestureOverride: "Ctrl+Plus / Ctrl+Minus (timeline focused)"),
        new("Timeline", "Copy / cut / paste keys (paste at the playhead)", GestureOverride: "Ctrl+C / Ctrl+X / Ctrl+V (timeline focused)"),
        new("Timeline", "Paste keys mirrored left/right", GestureOverride: "Ctrl+Shift+V (timeline focused)"),
        new("Timeline", "Key the selected bones at the playhead", GestureOverride: "K (timeline focused)"),
        new("Timeline", "Select every key", GestureOverride: "Ctrl+A (timeline focused)"),
        new("Timeline", "Fit the clip into view", GestureOverride: "Home (timeline focused)"),
        new("Timeline", "Zoom about the cursor / pan", GestureOverride: "Ctrl+wheel / Shift+wheel or middle-drag"),
        new("Timeline", "Seek / scrub", GestureOverride: "Click or drag the ruler (Alt: between frames)"),
        new("Timeline", "Change the clip's start or end", GestureOverride: "Drag the triangles on the ruler"),
        new("Timeline", "Open a key in the Key inspector", GestureOverride: "Double-click the key"),
        new("Timeline", "Select bones / expand a branch", GestureOverride: "Click a row name (Ctrl, Shift) / click its arrow"),
        new("Timeline", "Select every key of a bone", GestureOverride: "Double-click its row name"),
        new("Timeline", "Previous / next bone", GestureOverride: "Up / Down (timeline focused)"),

        // ── Problems and Table usage (bottom panel) ──────────────────────────
        new("Problems", "Select what a problem points at", GestureOverride: "Click its row, or Up / Down / Enter (Problems focused)"),
        new("Table usage", "Preview the clip on a mesh row / open a clip row", GestureOverride: "Double-click or Enter (Table usage focused)"),
        new("Table usage", "Copy the selected row's table line (on a list row, the whole list)", GestureOverride: "Ctrl+C (Table usage focused)"),

        // ── Clip tools (phase 5): each opens its dialog for the active clip ────
        new("Clip tools", "Trim / crop to a range", Key.T, ModifierKeys.Control | ModifierKeys.Alt, m => m.ClipTools.TrimCommand),
        new("Clip tools", "Shift in time", Key.H, ModifierKeys.Control | ModifierKeys.Alt, m => m.ClipTools.ShiftCommand),
        new("Clip tools", "Retime (scale the duration)", Key.R, ModifierKeys.Control | ModifierKeys.Alt, m => m.ClipTools.RetimeCommand),
        new("Clip tools", "Reverse", Key.V, ModifierKeys.Control | ModifierKeys.Alt, m => m.ClipTools.ReverseCommand),
        new("Clip tools", "Resample (bake)", Key.B, ModifierKeys.Control | ModifierKeys.Alt, m => m.ClipTools.ResampleCommand),
        new("Clip tools", "Reduce keys", Key.D, ModifierKeys.Control | ModifierKeys.Alt, m => m.ClipTools.ReduceCommand),
        new("Clip tools", "Offset the selected bones", Key.F, ModifierKeys.Control | ModifierKeys.Alt, m => m.ClipTools.OffsetCommand),
        new("Clip tools", "Compare with another clip", Key.G, ModifierKeys.Control | ModifierKeys.Alt, m => m.ClipTools.CompareCommand),
        new("Clip tools", "Recompute start/end", GestureOverride: "Clip menu"),
        new("Clip tools", "Make loopable", GestureOverride: "Clip menu"),
        new("Clip tools", "Mirror left/right", GestureOverride: "Clip menu"),
        new("Clip tools", "Remove or scale root motion", GestureOverride: "Clip menu"),
        new("Clip tools", "Set bone lengths", GestureOverride: "Clip menu"),
        new("Clip tools", "Conform to another skeleton", GestureOverride: "Clip menu"),
        new("Clip tools", "Set bone weights", GestureOverride: "Clip menu"),
        new("Clip tools", "Normalise (repair structure)", GestureOverride: "Clip menu"),
        new("Clip tools", "Play / scrub the preview while a tool is open", GestureOverride: "The dialog's play button and time bar"),

        // ── View ─────────────────────────────────────────────────────────────
        new("View", "Show or hide the library", Key.L, ModifierKeys.Control | ModifierKeys.Shift, m => m.ToggleLibraryCommand),
        new("View", "Show or hide the inspector", Key.I, ModifierKeys.Control | ModifierKeys.Shift, m => m.ToggleInspectorCommand),
        new("View", "Show or hide the bottom panel (Timeline, Problems, Table usage)", Key.M, ModifierKeys.Control | ModifierKeys.Shift, m => m.ToggleBottomCommand),
        new("View", "Show the Problems tab", GestureOverride: "Click the error / warning counts in the status bar"),

        // ── Help ─────────────────────────────────────────────────────────────
        new("Help", "RFA & V3C format reference", Key.F1, ModifierKeys.None, m => m.FormatReferenceCommand),
        new("Help", "Keyboard shortcuts", GestureOverride: "Help menu"),

        // ── Phase 6: retarget, mesh editing, glTF (each in its own Shortcuts.*.cs) ──
        .. RetargetRows(),
        .. MeshRows(),
        .. GltfRows(),
    ];

    /// <summary>The categories in the order the Help window lists them.</summary>
    public static IReadOnlyList<string> Categories { get; } = [.. All.Select(s => s.Category).Distinct()];

    /// <summary>The shortcuts in one category, in table order.</summary>
    public static IReadOnlyList<ShortcutInfo> InCategory(string category) =>
        [.. All.Where(s => string.Equals(s.Category, category, StringComparison.Ordinal))];

    /// <summary>Adds a key binding for every entry that names both a key and a shell command.</summary>
    public static void Install(InputBindingCollection bindings, RfaWorkspace model)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(model);
        foreach (var shortcut in All)
        {
            if (shortcut.Key == Key.None || shortcut.Command is null) continue;
            bindings.Add(new KeyBinding(shortcut.Command(model), shortcut.Key, shortcut.Modifiers));
        }
    }
}
