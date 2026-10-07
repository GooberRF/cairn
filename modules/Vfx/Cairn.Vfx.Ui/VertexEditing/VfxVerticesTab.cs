using System.Numerics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Ui.Controls;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Inspectors;

namespace Cairn.Vfx.Ui.VertexEditing;

/// <summary>
/// Vertices inspector tab (visible in vertex mode): selection count, centroid in effect and mesh space (typing moves
/// the selection, one undo step per committed field), delta move, gizmo snap, scope, mesh/morph/face/UV operations.
/// </summary>
public sealed class VfxVerticesTab : VfxInspectorPage
{
    private readonly VfxVertexMode _vm;
    private readonly NumericBox[] _delta = new NumericBox[3];

    public VfxVerticesTab(VfxDocument doc) : base(doc)
    {
        _vm = VfxVertexMode.Of(doc);
        _vm.Changed += (_, _) => Refresh();
    }

    protected override bool UsesFrame => true;

    protected override string StructureKey() =>
        $"{_vm.IsActive}|{_vm.Unavailable}|{_vm.Section}|{_vm.Mesh?.IsMorph}|{_vm.Selected.Count > 0}|{_vm.SelectedFaceIndices.Length > 0}|{_vm.Mesh?.MaterialIndices?.Length}";

    protected override void Build()
    {
        if (!_vm.Enabled) { Fact("Vertex mode", () => "Off: press Tab (or Effect > Vertex mode) with a mesh selected."); return; }
        if (_vm.Unavailable is { } why) { Fact("Unavailable", () => why); return; }
        if (_vm.Mesh is not { } mesh) return;
        Header("Selection");
        Fact("Selected", () => $"{_vm.Selected.Count} of {_vm.Mesh?.NumVertices} vertices, {_vm.SelectedFaceIndices.Length} full faces");
        var scope = new ComboBox { Items = { "This frame", "All frames" }, ToolTip = "This frame = the stored frame being edited (the playhead snaps to it); All frames = the same edit on every stored frame (Shift+F)" };
        AutomationProperties.SetName(scope, "Scope");
        scope.SelectionChanged += (_, _) => { if (scope.SelectedIndex >= 0) _vm.AllFrames = scope.SelectedIndex == 1; };
        Watch(() => scope.SelectedIndex = _vm.AllFrames ? 1 : 0);
        Row("Scope", scope, "Which stored frames vertex edits change");
        Fact("Editing", () => _vm.ScopeText);
        if (_vm.Selected.Count > 0)
        {
            Header(_vm.Selected.Count == 1 ? "Vertex position" : "Selection centroid");
            string[] ax = ["X", "Y", "Z"];
            for (int a = 0; a < 3; a++)
            {
                int axis = a;
                Number($"Effect {ax[a]}", "Centroid in effect space at the playhead; typing moves the selection", () => [Get(_vm.Centroid(false), axis)],
                    (f, v) => _vm.MoveCentroidTo(f, axis, v, false), 3, 0.05);
            }
            for (int a = 0; a < 3; a++)
            {
                int axis = a;
                Number($"Mesh {ax[a]}", "Centroid in the mesh's stored space (edit frame); typing moves the selection", () => [Get(_vm.Centroid(true), axis)],
                    (f, v) => _vm.MoveCentroidTo(f, axis, v, true), 3, 0.05);
            }
            Header("Move by");
            for (int a = 0; a < 3; a++)
            {
                var box = new NumericBox { Decimals = 3, Step = 0.05 };
                AutomationProperties.SetName(box, $"Delta {ax[a]}");
                _delta[a] = box;
                Row($"Delta {ax[a]}", box, "Effect-space offset applied by Move by delta");
            }
            Buttons(("Move by delta", "Moves the selection by the delta (effect space), one undo step",
                () => _vm.MoveBy(new Vector3((float)_delta[0].Value, (float)_delta[1].Value, (float)_delta[2].Value))));
        }
        var snap = Box("Gizmo snap", 3, 0.05); snap.Minimum = 0; snap.Value = _vm.Snap;
        snap.ValueChanged += (_, _) => _vm.Snap = (float)snap.Value;
        Row("Gizmo snap", snap, "Move-gizmo drags snap to this grid in effect units (0 = off)");
        Header("Mesh");
        Buttons(
            ("Make morph mesh", "Converts the mesh to morph (stored per-frame positions)", () => _vm.MakeMorph()),
            ("Add morph frame", "Inserts a stored frame after the edited one (at the playhead)", () => _vm.AddMorphFrame()),
            ("Remove morph frame", "Removes the stored frame being edited", () => _vm.RemoveMorphFrame()),
            ("Delete vertices", "Deletes the selected vertices and their faces (Ctrl+Delete)", () => _vm.Delete()),
            ("Merge to centre", "Merges the selected vertices at their centroid (M)", () => _vm.Merge()));
        if (_vm.SelectedFaceIndices.Length == 0) { Fact("Faces", () => "Select all three corners of a face for face and UV edits."); return; }
        Header("Selected faces");
        Buttons(("Flip winding", "Reverses the winding of the fully selected faces", () => _vm.FlipFaces()));
        int slots = Math.Max(1, mesh.MaterialIndices?.Length ?? 1);
        var slotNames = Enumerable.Range(0, slots).Select(i => $"Slot {i}").ToList();
        Choice("Material slot", "Material slot of the fully selected faces", () => slotNames,
            () => _vm.SelectedFaceIndices.Select(i => $"Slot {_vm.Mesh!.Faces[i].MaterialIndex}"),
            (f, v) => Vfx.Editing.VfxEdit.SetFaceMaterial(f, _vm.Section, int.Parse(v[5..]), _vm.SelectedFaceIndices));
        Number("Smoothing group", "Smoothing group of the fully selected faces", () => _vm.SelectedFaceIndices.Select(i => (double)_vm.Mesh!.Faces[i].SmoothingGroup),
            (f, v) => Vfx.Editing.VfxEdit.SetFaceSmoothing(f, _vm.Section, (int)v, _vm.SelectedFaceIndices), 0, 1, 0);
        Header("UVs (selected faces)");
        var uvU = Box("UV value U", 3, 0.1); var uvV = Box("UV value V", 3, 0.1); var deg = Box("UV degrees", 1, 15);
        Row("U", uvU, "Offset / scale / scroll speed U"); Row("V", uvV, "Offset / scale / scroll speed V"); Row("Degrees", deg, "Rotation in degrees");
        Vector2 UV() => new((float)uvU.Value, (float)uvV.Value);
        Buttons(
            ("Offset", "Adds (U, V) to the selected faces' UVs", () => _vm.OffsetUvs(UV())),
            ("Scale", "Scales the selected faces' UVs by (U, V) about their centre", () => _vm.ScaleUvs(UV())),
            ("Rotate", "Rotates the selected faces' UVs about their centre", () => _vm.RotateUvs((float)deg.Value)),
            ("Generate scrolling UVs", "Per-frame UVs scrolling at (U, V) per second (whole mesh)", () => _vm.ScrollUvs(UV())));
    }

    private static NumericBox Box(string name, int decimals, double step)
    {
        var b = new NumericBox { Decimals = decimals, Step = step };
        AutomationProperties.SetName(b, name);
        return b;
    }

    private static double Get(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
