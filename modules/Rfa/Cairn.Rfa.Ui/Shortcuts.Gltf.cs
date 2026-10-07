using System.Windows.Input;

namespace Cairn.Rfa.Ui;

public static partial class Shortcuts
{
    /// <summary>glTF export and import shortcuts (phase 6).</summary>
    private static IEnumerable<ShortcutInfo> GltfRows() =>
    [
        new("glTF", "Export the active mesh or clip to glTF", Key.E, ModifierKeys.Control, m => m.Gltf.ExportCommand),
        new("glTF", "Import animations from a glTF file", Key.I, ModifierKeys.Control, m => m.Gltf.ImportAnimationCommand),
        new("glTF", "Import a mesh from a glTF file", GestureOverride: "File menu › Import Mesh from glTF…"),
        new("glTF", "Import a glTF file (asks: animation or mesh)", GestureOverride: "Drop a .gltf or .glb on the window, or open it"),
    ];
}
