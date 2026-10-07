using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Commands;

/// <summary>
/// Shared edit plumbing for the effects module. Every edit goes through <see cref="Apply"/> (one undo step with a
/// label) or <see cref="Begin"/>/<see cref="Update"/>/<see cref="Commit"/> (a drag that coalesces into one step).
/// Edits are refused, with <see cref="OlderVersionReason"/> on the status bar, while the file is an older format.
/// Core exceptions (<see cref="InvalidOperationException"/>/<see cref="ArgumentException"/>) become status messages.
/// </summary>
public static class VfxEditing
{
    public const string OlderVersionReason = "This effect uses an older format version. Use Effect > Convert to current format to edit it.";

    public static bool CanEdit(VfxDocument doc) => !doc.IsOlderVersion && !doc.IsReadOnly;

    /// <summary>Why editing is disabled, or null when it is allowed (use as the tooltip of disabled controls).</summary>
    public static string? DisabledReason(VfxDocument doc) => doc.IsOlderVersion ? OlderVersionReason : doc.IsReadOnly ? "The document is read-only." : null;

    /// <summary>One undoable edit. Returns false when refused, failed or unchanged.</summary>
    public static bool Apply(VfxDocument doc, string label, Func<VfxFile, VfxFile> edit)
    {
        if (DisabledReason(doc) is { } why) { doc.ShowStatus(why); return false; }
        try { return doc.Apply(label, edit); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { doc.ShowStatus($"{label}: {e.Message}"); return false; }
    }

    /// <summary>One undoable edit applied to each section in <paramref name="sections"/> in turn.</summary>
    public static bool ApplyEach(VfxDocument doc, string label, IEnumerable<int> sections, Func<VfxFile, int, VfxFile> edit)
    {
        var list = sections.ToList();
        return list.Count > 0 && Apply(doc, label, f => list.Aggregate(f, edit));
    }

    /// <summary>Starts a coalesced edit (drag, spinner); <see cref="Update"/> replaces its result, <see cref="Commit"/> ends it.</summary>
    public static bool Begin(VfxDocument doc, string label)
    {
        if (!CanEdit(doc)) return false;
        doc.CommitEdit();
        doc.BeginEdit(label);
        return true;
    }

    public static void Update(VfxDocument doc, Func<VfxFile, VfxFile> edit)
    {
        try { doc.UpdateEdit(edit); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { doc.ShowStatus(e.Message); }
    }

    public static void Commit(VfxDocument doc) => doc.CommitEdit();

    /// <summary>Selected section indices whose section is a <typeparamref name="T"/>, primary first.</summary>
    public static List<int> SelectedOf<T>(VfxDocument doc) where T : VfxSection
    {
        var secs = doc.Current.Sections;
        var list = doc.Selection.Sections.Where(i => i >= 0 && i < secs.Length && secs[i] is T).ToList();
        if (list.Remove(doc.Selection.Primary)) list.Insert(0, doc.Selection.Primary);
        return list;
    }

    /// <summary>Selected object sections (not materials), in file order.</summary>
    public static List<int> SelectedObjects(VfxDocument doc) =>
        [.. doc.Selection.Sections.Where(i => i >= 0 && i < doc.Current.Sections.Length && VfxTransplant.IsObject(doc.Current.Sections[i])).Order()];

    /// <summary>The current timeline frame as an index into an object's frame list (clamped).</summary>
    public static int FrameIndex(VfxDocument doc, int frameCount) => Math.Clamp((int)Math.Floor(doc.TimelineFrame), 0, Math.Max(0, frameCount - 1));
}
