using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Commands;

/// <summary>
/// Outliner / Effect > Edit operations on the selection. Each is one undo step through <see cref="VfxEditing.Apply"/>;
/// the selection follows the result (duplicates selected, deleted cleared, moved kept).
/// </summary>
public static class VfxObjectCommands
{
    public static bool Rename(VfxDocument doc, int index, string name) =>
        name.Length > 0 && VfxEditing.Apply(doc, "Rename", f => VfxEdit.Rename(f, index, name));

    public static bool Duplicate(VfxDocument doc)
    {
        var sel = VfxEditing.SelectedObjects(doc);
        if (sel.Count == 0) { doc.ShowStatus("Select objects to duplicate."); return false; }
        VfxTransplantResult? result = null;
        if (!VfxEditing.Apply(doc, sel.Count == 1 ? "Duplicate object" : $"Duplicate {sel.Count} objects", f => (result = VfxTransplant.Duplicate(f, sel, 0)).File)) return false;
        bool first = true;
        foreach (var s in result!.Sections) { doc.Selection.Select(s.TargetIndex, add: !first); first = false; }
        return true;
    }

    public static bool Delete(VfxDocument doc)
    {
        var sel = doc.Selection.Sections.Where(i => i < doc.Current.Sections.Length).OrderDescending().ToList();
        if (sel.Count == 0) return false;
        int next = NextSelectionAfterDelete(doc.Current.Sections, sel);
        // removing from the highest index keeps the lower indices valid; materials in use refuse (reassign on the Material tab)
        if (!VfxEditing.Apply(doc, sel.Count == 1 ? "Delete" : $"Delete {sel.Count} sections", f => sel.Aggregate(f, (a, i) => VfxEdit.RemoveSection(a, i)))) return false;
        doc.Selection.Select(next);
        return true;
    }

    /// <summary>
    /// The index (after the deletion) to select once <paramref name="deleted"/> are removed: the next surviving
    /// sibling (same parent) of the first deleted section, else the previous one, else -1 (nothing).
    /// </summary>
    internal static int NextSelectionAfterDelete(IReadOnlyList<VfxSection> sections, IReadOnlyCollection<int> deleted)
    {
        int first = deleted.Min();
        string? parent = VfxEdit.ParentOf(sections[first]);
        bool Sibling(int i) => !deleted.Contains(i) && VfxEdit.ParentOf(sections[i]) == parent;
        int pick = Enumerable.Range(first + 1, Math.Max(0, sections.Count - first - 1)).FirstOrDefault(Sibling, -1);
        if (pick < 0) pick = Enumerable.Range(0, first).LastOrDefault(Sibling, -1);
        return pick < 0 ? -1 : pick - deleted.Count(d => d < pick);
    }

    public static bool Reparent(VfxDocument doc, IEnumerable<int> sections, string parent) =>
        VfxEditing.ApplyEach(doc, $"Parent to {parent}", sections.Where(i => i < doc.Current.Sections.Length && VfxTransplant.IsObject(doc.Current.Sections[i])), (f, i) => VfxEdit.Reparent(f, i, parent));

    /// <summary>Moves the primary section one place up (-1) or down (+1) in file order.</summary>
    public static bool Move(VfxDocument doc, int delta)
    {
        int from = doc.Selection.Primary, to = from + delta;
        if (from < 0 || to < 0 || to >= doc.Current.Sections.Length) return false;
        if (!VfxEditing.Apply(doc, delta < 0 ? "Move up" : "Move down", f => VfxEdit.MoveSection(f, from, to))) return false;
        doc.Selection.Select(to);
        return true;
    }

    public static void SelectAllOfType(VfxDocument doc)
    {
        if (doc.SelectedSection is not { } p) return;
        var t = p.GetType(); bool first = true;
        for (int i = 0; i < doc.Current.Sections.Length; i++)
            if (doc.Current.Sections[i].GetType() == t) { doc.Selection.Select(i, add: !first); first = false; }
    }

    /// <summary>Hides every object outside the selection in the preview (view state, not an edit).</summary>
    public static void Isolate(VfxDocument doc)
    {
        for (int i = 0; i < doc.Current.Sections.Length; i++)
            if (VfxTransplant.IsObject(doc.Current.Sections[i])) doc.SetHidden(i, !doc.Selection.Contains(i));
    }

    public static void ShowAll(VfxDocument doc)
    {
        foreach (var i in doc.HiddenSections.ToList()) doc.SetHidden(i, false);
    }

    /// <summary>Parent choices for the "Parent" submenu: Scene Root and every named object.</summary>
    public static IReadOnlyList<string> ParentNames(VfxFile f) =>
        ["Scene Root", .. f.Sections.Where(VfxTransplant.IsObject).Select(VfxSections.NameOf).Where(n => n.Length > 0).Distinct()];
}
