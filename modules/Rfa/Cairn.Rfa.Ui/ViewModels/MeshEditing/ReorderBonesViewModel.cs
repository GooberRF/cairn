using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.ViewModels.MeshEditing;

/// <summary>One bone in the reorder list.</summary>
public sealed class ReorderBoneItem(int oldIndex, string name) : ObservableObject
{
    private int _newIndex = oldIndex;

    /// <summary>The bone's index before the reorder.</summary>
    public int OldIndex { get; } = oldIndex;

    public string Name { get; } = name;

    /// <summary>Its index after the reorder.</summary>
    public int NewIndex
    {
        get => _newIndex;
        set
        {
            if (Set(ref _newIndex, value)) RaiseAll(nameof(Label), nameof(Moved), nameof(MovedText));
        }
    }

    public string Label => $"{NewIndex}: {Name}";

    public bool Moved => NewIndex != OldIndex;

    public string MovedText => Moved ? $"was {OldIndex}" : string.Empty;
}

/// <summary>An open clip document previewing the mesh, offered for conforming in the same action.</summary>
public sealed class ConformClipItem : ObservableObject
{
    private bool _conform;

    public ConformClipItem(ClipDocumentViewModel document, int meshBones)
    {
        Document = document;
        CanConform = !document.IsReadOnly && document.Current.BoneCount == meshBones;
        _conform = CanConform;
        Note = document.IsReadOnly ? "read-only"
            : CanConform ? $"{document.Current.BoneCount} bones"
            : $"{document.Current.BoneCount} bones, the mesh has {meshBones}: not conformed";
    }

    public ClipDocumentViewModel Document { get; }

    public string Label => Document.DisplayName;

    public string Note { get; }

    /// <summary>True when the clip has the mesh's bone count (a reorder maps every track).</summary>
    public bool CanConform { get; }

    /// <summary>Conform this clip to the new order in the same action.</summary>
    public bool Conform
    {
        get => _conform && CanConform;
        set => Set(ref _conform, value);
    }

    public string ToolTip => CanConform
        ? $"Re-lay {Document.DisplayName}'s tracks in the new bone order (one undo step in that tab), so it plays exactly as before on the reordered mesh"
        : $"{Document.DisplayName} has {Document.Current.BoneCount} bones; only a clip with the mesh's bone count can follow a reorder";
}

/// <summary>
/// The reorder warning (Mesh › Reorder Bones…, Move Bone Up / Down): the new order (editable), the plain
/// warning that clips address bones by index, and the open clip documents previewing this mesh, each
/// offered to be conformed in the same action with <see cref="ClipEdit.ConformToSkeleton"/> using the
/// permutation <see cref="MeshEdit.ReorderBones"/> returns. The mesh gets one undo step, each conformed
/// clip document one of its own.
/// </summary>
public sealed class ReorderBonesViewModel : ObservableObject
{
    private readonly MeshDocumentViewModel _document;
    private readonly string? _label;
    private ReorderBoneItem? _selected;
    private string? _error;

    /// <param name="document">The mesh document.</param>
    /// <param name="order">The proposed order (new index → old index); identity when null.</param>
    /// <param name="label">The undo label ("Move bone 'spine' up"); "Reorder bones" when null.</param>
    /// <param name="focusOldIndex">The bone to select in the list (the one moved), or -1.</param>
    public ReorderBonesViewModel(MeshDocumentViewModel document, IReadOnlyList<int>? order, string? label, int focusOldIndex = -1)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _label = label;
        var bones = document.Current.Bones;
        var start = order is { } o && o.Count == bones.Length ? o : Enumerable.Range(0, bones.Length).ToArray();
        for (int i = 0; i < start.Count; i++)
        {
            int old = start[i];
            Bones.Add(new ReorderBoneItem(old, (uint)old < (uint)bones.Length ? bones[old].Name.Text : $"bone {old}") { NewIndex = i });
        }
        _selected = Bones.FirstOrDefault(b => b.OldIndex == focusOldIndex);
        foreach (var clip in ClipsPreviewing(document))
        {
            var item = new ConformClipItem(clip, bones.Length);
            item.PropertyChanged += (_, _) => OnConformChanged();
            Clips.Add(item);
        }
        MoveUpCommand = new RelayCommand(() => Move(-1), () => _selected is { } s && Bones.IndexOf(s) > 0);
        MoveDownCommand = new RelayCommand(() => Move(+1), () => _selected is { } s && Bones.IndexOf(s) < Bones.Count - 1);
        ResetCommand = new RelayCommand(Reset, () => HasChanges);
    }

    /// <summary>The open clip documents whose preview mesh is this document's mesh (by name, or the very snapshot).</summary>
    public static IEnumerable<ClipDocumentViewModel> ClipsPreviewing(MeshDocumentViewModel document) =>
        document.Shell.Documents.OfType<ClipDocumentViewModel>().Where(c =>
            ReferenceEquals(c.PreviewMesh, document.Current)
            || string.Equals(c.Scene.MeshName, document.DisplayName, StringComparison.OrdinalIgnoreCase));

    public MeshDocumentViewModel Document => _document;

    public string Title => "Reorder bones";

    public string Heading => $"Reorder the bones of {_document.DisplayName}";

    public string Warning =>
        "Clips address bones by index, not by name. After a reorder, every clip made for this mesh plays its tracks on the wrong bones "
        + "until it is conformed to the new order. The mesh itself looks and skins the same: parents, vertex weights, collision spheres and "
        + "prop points are remapped.";

    public ObservableCollection<ReorderBoneItem> Bones { get; } = [];

    public ReorderBoneItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<ConformClipItem> Clips { get; } = [];

    public bool HasClips => Clips.Count > 0;

    public string ClipsHint => HasClips
        ? "Ticked clips are re-laid out for the new order in the same action (one undo step in each tab) and preview the reordered mesh, so they play exactly as before."
        : "No open clip previews this mesh. Clips saved on disk for it must be conformed separately (open each, then Clip › Conform to Skeleton… with the saved mesh).";

    /// <summary>The order as it stands: for each new index, the old bone index.</summary>
    public IReadOnlyList<int> NewOrder => [.. Bones.Select(b => b.OldIndex)];

    public bool HasChanges => Bones.Select((b, i) => b.OldIndex != i).Any(x => x);

    /// <summary>"3 bones change index".</summary>
    public string ChangeText
    {
        get
        {
            int moved = Bones.Count(b => b.Moved);
            return moved == 0 ? "The order is unchanged." : $"{moved} bone{(moved == 1 ? "" : "s")} change index.";
        }
    }

    public string ApplyText => Clips.Any(c => c.Conform) ? "Reorder and conform" : "Reorder";

    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The mesh's undo label.</summary>
    public string Label => _label ?? "Reorder bones";

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand ResetCommand { get; }

    private void Move(int delta)
    {
        if (_selected is not { } item) return;
        int from = Bones.IndexOf(item), to = from + delta;
        if (to < 0 || to >= Bones.Count) return;
        Bones.Move(from, to);
        Renumber();
        Selected = item;
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
    }

    private void Reset()
    {
        var sorted = Bones.OrderBy(b => b.OldIndex).ToList();
        Bones.Clear();
        foreach (var b in sorted) Bones.Add(b);
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < Bones.Count; i++) Bones[i].NewIndex = i;
        RaiseAll(nameof(HasChanges), nameof(ChangeText), nameof(NewOrder));
        ResetCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Re-reads the button caption after a clip's box changes.</summary>
    public void OnConformChanged() => Raise(nameof(ApplyText));

    /// <summary>
    /// Reorders the mesh (one undo step) and conforms each ticked clip document (one step each). Returns
    /// false, with <see cref="Error"/> set, when the Core refused or the document changed meanwhile.
    /// </summary>
    public bool Apply()
    {
        Error = null;
        if (_document.IsReadOnly)
        {
            Error = "Static meshes open read-only.";
            return false;
        }
        if (!HasChanges) return true;
        var order = NewOrder;
        BoneReorderResult result;
        try
        {
            result = MeshEdit.ReorderBones(_document.Current, order);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Error = MeshCommands.UserMessage(ex);
            return false;
        }
        // Keep the moved (or selected) bone selected in the structure tree at its new index.
        int focus = _selected?.OldIndex ?? (_document.Structure.Selected?.Node is { Kind: MeshNodeKind.Bone } n ? n.Index : -1);
        if (focus >= 0) _document.Structure.SelectAfterRebuild(new MeshNodeRef(MeshNodeKind.Bone, Index: result.OldToNew[focus]));
        if (!_document.Apply(Label, _ => result.Mesh))
        {
            Error = "The mesh could not be reordered (see the status bar).";
            return false;
        }
        var reordered = _document.Current;
        foreach (var item in Clips.Where(c => c.Conform))
        {
            var clip = item.Document;
            string clipLabel = $"Conform to the new bone order of {_document.DisplayName}";
            if (clip.Apply(clipLabel, c => ConformToReorder(c, result)))
                clip.UsePreviewMesh(reordered, _document.DisplayName, _document.Folder);
        }
        return true;
    }

    /// <summary>
    /// <paramref name="clip"/> (made for the mesh before the reorder) re-laid out for the reordered mesh:
    /// <see cref="ClipEdit.ConformToSkeleton"/> with each bone named by its old index, so the match is
    /// exactly the permutation (duplicate bone names cannot confuse it). Tracks move bit-identically.
    /// </summary>
    public static RfaClip ConformToReorder(RfaClip clip, BoneReorderResult reorder)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(reorder);
        int n = reorder.NewToOld.Length;
        if (clip.BoneCount != n)
            throw new ArgumentException($"The clip has {clip.BoneCount} bones but the mesh has {n}; only a clip with the mesh's bone count can follow a reorder.", nameof(clip));
        static string Tag(int i) => "#" + i.ToString(CultureInfo.InvariantCulture);
        var oldNames = Enumerable.Range(0, n).Select(Tag).ToArray();
        var bones = reorder.Mesh.Bones.Select((b, i) => b with { Name = FixedString.FromText(Tag(reorder.NewToOld[i]), V3dBone.NameSize) }).ToArray();
        var target = Skeleton.FromBones(bones);
        return ClipEdit.ConformToSkeleton(clip, oldNames, target, new ConformOptions { UseCanonicalNames = false }).Clip;
    }
}
