using System.Windows.Input;

namespace Cairn.Rfa.Ui;

public static partial class Shortcuts
{
    /// <summary>Retarget shortcuts (phase 6).</summary>
    private static IEnumerable<ShortcutInfo> RetargetRows() =>
    [
        new("Retarget", "Retarget the active clip onto another skeleton", Key.R, ModifierKeys.Control, m => m.Retarget.RetargetCommand),
        new("Retarget", "Retarget many clips at once", GestureOverride: "Tools menu › Batch Retarget…"),
        new("Retarget", "Retarget a clip from the library", GestureOverride: "Right-click it in the library › Retarget…"),
        new("Retarget", "Change a bone's source in the bone map", GestureOverride: "Alt+Down on its source box, or click it"),
    ];
}
