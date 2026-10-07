using System.Windows.Input;

namespace Cairn.Rfa.Ui;

public static partial class Shortcuts
{
    /// <summary>The Mesh menu's editing shortcuts (phase 6).</summary>
    private static IEnumerable<ShortcutInfo> MeshRows() =>
    [
        new("Mesh editing", "Rename the selected bone, sphere, prop point or submesh; retype a material's (or LOD texture entry's) texture name", Key.F2, ModifierKeys.None, m => m.MeshTools.RenameSelectedCommand),
        new("Mesh editing", "Commit a typed name / put the stored one back", GestureOverride: "Enter / Esc (in the name box)"),
        new("Mesh editing", "Remove the selected collision sphere or prop point", GestureOverride: "Del (structure tree focused)"),
        new("Mesh editing", "Move the selected bone up / down in the index order", GestureOverride: "Alt+Up / Alt+Down (structure tree focused)"),
        new("Mesh editing", "Reorder every bone (warns, offers to conform open clips)", GestureOverride: "Mesh menu › Reorder Bones…"),
        new("Mesh editing", "Add a collision sphere or prop point", GestureOverride: "Mesh menu"),
        new("Mesh editing", "Pick a material's texture from the game's files", GestureOverride: "Browse… in the material editor"),
    ];
}
