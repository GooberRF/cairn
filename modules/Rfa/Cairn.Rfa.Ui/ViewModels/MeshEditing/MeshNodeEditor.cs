using System.Globalization;
using System.Numerics;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Ui.ViewModels.MeshEditing;

/// <summary>
/// The editor pane of the Structure tab for one selected node (phase 6). Every change goes through the
/// document's single edit path as one labelled undo step (<see cref="TryApply"/>); a Core refusal (an
/// <see cref="ArgumentException"/> from <c>MeshEdit</c>) becomes <see cref="Error"/>, shown under the
/// editor, and the status-bar message. An editor survives the tree rebuild that follows each edit (the
/// structure keeps it while the same node stays selected, and calls <see cref="Refresh"/>), so a number
/// box keeps its focus through a spinner run. A read-only document (.v3m) shows the same editor disabled.
/// </summary>
public abstract class MeshNodeEditor : ObservableObject
{
    private string? _error;

    protected MeshNodeEditor(MeshDocumentViewModel document, MeshNodeRef node)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Node = node;
    }

    /// <summary>The document edited.</summary>
    public MeshDocumentViewModel Document { get; }

    /// <summary>The node edited.</summary>
    public MeshNodeRef Node { get; }

    /// <summary>The Mesh menu commands (the editor's Add / Remove / Move buttons are the same commands).</summary>
    public MeshCommands Tools => Document.Shell.MeshTools;

    /// <summary>True for a document that may not be edited (a .v3m): every control is shown disabled.</summary>
    public bool IsReadOnly => Document.IsReadOnly;

    public bool IsEditable => !Document.IsReadOnly;

    /// <summary>Why the last edit was refused, or null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The fields <see cref="Refresh"/> re-reads.</summary>
    protected List<IMeshField> Fields { get; } = [];

    protected T Add<T>(T field) where T : IMeshField
    {
        Fields.Add(field);
        return field;
    }

    /// <summary>True when <paramref name="node"/> addresses the same node as this editor.</summary>
    public bool Edits(MeshNodeRef node) =>
        node.Kind == Node.Kind && node.Submesh == Node.Submesh && node.Lod == Node.Lod && node.Index == Node.Index;

    /// <summary>True while the node still exists in <paramref name="mesh"/>.</summary>
    public abstract bool Exists(V3dFile mesh);

    /// <summary>Re-reads every value (after any snapshot change: an edit, undo, redo, reload).</summary>
    public virtual void Refresh()
    {
        if (!Exists(Document.Current)) return;
        foreach (var f in Fields) f.Refresh();
        RaiseAll(nameof(IsReadOnly), nameof(IsEditable));
    }

    public void ClearError() => Error = null;

    /// <summary>
    /// Applies <paramref name="edit"/> as one undo step labelled <paramref name="label"/>. A refusal is
    /// shown under the editor and in the status bar and changes nothing. <paramref name="select"/> is the
    /// node the structure tree selects after the edit (an added or moved node). Returns true when the
    /// document changed.
    /// </summary>
    public bool TryApply(string label, Func<V3dFile, V3dFile> edit, MeshNodeRef? select = null)
    {
        Error = null;
        if (Document.IsReadOnly)
        {
            Error = MeshStructureViewModel.ReadOnlyReason(Document);
            return false;
        }
        if (Document.IsEditing) Document.CommitEdit();
        V3dFile next;
        try
        {
            next = edit(Document.Current);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Report(label, ex);
            return false;
        }
        if (ReferenceEquals(next, Document.Current)) return false;
        if (select is { } s) Document.Structure.SelectAfterRebuild(s);
        return Document.Apply(label, _ => next);
    }

    /// <summary>Updates a coalesced run (a spinner); a refusal is reported and the run's value stays.</summary>
    internal void UpdateCoalesced(Func<V3dFile, V3dFile> edit)
    {
        Document.UpdateEdit(m =>
        {
            try
            {
                var next = edit(m);
                Error = null;
                return next;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                Error = MeshCommands.UserMessage(ex);
                throw;
            }
        });
    }

    private void Report(string label, Exception ex)
    {
        Error = MeshCommands.UserMessage(ex);
        Document.ShowStatus($"{label}: {Error}");
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    /// <summary>"none" then every bone ("3: spine"), optionally leaving one bone out.</summary>
    internal static IReadOnlyList<MeshChoice> BoneChoices(V3dFile mesh, bool withNone, int except = int.MinValue)
    {
        var list = new List<MeshChoice>(mesh.Bones.Length + 1);
        if (withNone) list.Add(new MeshChoice(-1, "none (-1)"));
        for (int i = 0; i < mesh.Bones.Length; i++)
        {
            if (i != except) list.Add(new MeshChoice(i, BoneLabel(mesh, i)));
        }
        return list;
    }

    internal static string BoneLabel(V3dFile mesh, int index) =>
        index < 0 ? "none" : index < mesh.Bones.Length ? $"{index}: {mesh.Bones[index].Name.Text}" : $"{index} (missing)";

    internal static string BoneName(V3dFile mesh, int index) =>
        index < 0 ? "none" : index < mesh.Bones.Length ? mesh.Bones[index].Name.Text : index.ToString(CultureInfo.InvariantCulture);

    internal static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    internal static Vector3 WithComponent(Vector3 v, int axis, float value) => axis switch
    {
        0 => v with { X = value },
        1 => v with { Y = value },
        _ => v with { Z = value },
    };

    /// <summary><paramref name="rotation"/> with one Euler angle (pitch, yaw, roll; degrees) replaced.</summary>
    internal static Quaternion WithEuler(Quaternion rotation, int axis, double degrees)
    {
        var e = Quat.ToEulerDegrees(Quat.Normalize(rotation));
        return Quat.Normalize(Quat.FromEulerDegrees(WithComponent(e, axis, (float)degrees)));
    }

    internal static string Vec(Vector3 v) => string.Format(CultureInfo.CurrentCulture, "({0:0.####}, {1:0.####}, {2:0.####})", v.X, v.Y, v.Z);

    internal static string Quaternion4(Quaternion q) =>
        string.Format(CultureInfo.CurrentCulture, "({0:0.####}, {1:0.####}, {2:0.####}, {3:0.####})", q.X, q.Y, q.Z, q.W);

    internal static readonly string[] Axes = ["X", "Y", "Z"];

    internal static readonly string[] Angles = ["Pitch (X)", "Yaw (Y)", "Roll (Z)"];

    /// <summary>The Euler convention, stated in every rotation tooltip (the Key inspector's).</summary>
    internal const string EulerConvention =
        "Euler angles in degrees: X = pitch, Y = yaw, Z = roll, composed q = Ry(yaw) · Rx(pitch) · Rz(roll) — roll is applied first, yaw last — "
        + "in RF's left-handed frame (+X right, +Y up, +Z forward), as the active rotation (the file stores the conjugate); the Key inspector's "
        + "convention. Pitch is kept in [−90°, 90°]; at ±90° roll folds into yaw.";
}
