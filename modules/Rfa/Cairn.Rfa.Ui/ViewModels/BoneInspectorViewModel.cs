using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Docs;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>A weight preset button: a value, or a set of bones to apply the current weight to.</summary>
/// <param name="Label">Caption.</param>
/// <param name="ToolTip">What it does.</param>
/// <param name="Run">The action.</param>
public sealed record BoneActionViewModel(string Label, string ToolTip, RelayCommand Run);

/// <summary>
/// The inspector's Bone tab (DESIGN.md "Inspector / Bone"): the bone selection's name, index and
/// parent, its weight in this clip (indeterminate when the selected bones disagree) with value presets,
/// "apply to children" and the upper/lower/all-body presets (<see cref="WeightPreset"/>), the key
/// counts, the bone's offset in this clip against the mesh's rest offset, and the key-at-playhead /
/// delete-all-keys / select-all-keys buttons. Every edit is one undo step through the document.
/// </summary>
public sealed class BoneInspectorViewModel : ObservableObject
{
    private readonly ClipDocumentViewModel _doc;
    private string _title = "No bone selected";
    private string _indexText = string.Empty;
    private string _parentText = string.Empty;
    private string _keysText = string.Empty;
    private bool _includeChildren;

    internal BoneInspectorViewModel(ClipDocumentViewModel document)
    {
        _doc = document;
        Weight = new SelectionFieldViewModel(document, "Weight", "rfa.bone.weight",
            "The bone's weight in this clip, 0–10. As an action, 10 replaces the states on the bone, 5 shares it half and half, 0 leaves it to the states. "
            + "With 'Apply to children' the value also goes to every bone below the selected ones.",
            c => Bones(c).Select(b => (double)c.Bones[b].Weight).ToList(),
            (c, v) => SetWeight(c, (float)v),
            () => WeightLabel(), () => 2, () => string.Empty, () => 1, 0, 10);

        foreach (float value in new[] { 0f, 5f, 10f })
        {
            float v = value;
            ValuePresets.Add(new BoneActionViewModel(v.ToString("0", CultureInfo.CurrentCulture),
                $"Set the selected bones' weight to {v:0} ({(v >= 10 ? "fully replaces the states as an action" : v <= 0 ? "ignored by the clip" : "shares the bone with the states")})",
                new RelayCommand(() => _doc.Apply(WeightLabel(v), c => SetWeight(c, v)), () => HasSelection && !_doc.IsReadOnly)));
        }
        BodyPresets.Add(new BoneActionViewModel("Upper body", "Give every upper-body bone (the spine and everything above it) the weight shown above",
            new RelayCommand(() => ApplyPreset("upper body", s => WeightPreset.UpperBody(s)), () => _doc.FittingSkeleton is not null && !_doc.IsReadOnly)));
        BodyPresets.Add(new BoneActionViewModel("Lower body", "Give every lower-body bone (the root, pelvis and legs) the weight shown above",
            new RelayCommand(() => ApplyPreset("lower body", s => WeightPreset.LowerBody(s)), () => _doc.FittingSkeleton is not null && !_doc.IsReadOnly)));
        BodyPresets.Add(new BoneActionViewModel("All bones", "Give every bone of the clip the weight shown above",
            new RelayCommand(() => ApplyPreset("all bones", _ => WeightPreset.All(_doc.Current.BoneCount)), () => !_doc.IsReadOnly)));

        KeyAtPlayheadCommand = new RelayCommand(() => _doc.Timeline.KeySelectedBonesAtPlayhead(), () => HasSelection && !_doc.IsReadOnly);
        DeleteAllKeysCommand = new RelayCommand(() =>
        {
            var bones = Bones(_doc.Current).ToList();
            var keys = KeySelection.Bones(_doc.Current, bones);
            if (keys.IsEmpty) return;
            string label = bones.Count == 1 ? $"Delete all keys of {_doc.BoneDisplayName(bones[0])}" : $"Delete all keys of {bones.Count} bones";
            _doc.ApplyAndSelect(label, c => (ClipEdit.DeleteKeys(c, KeySelection.Bones(c, bones)), KeySelection.Empty));
        }, () => HasSelection && !_doc.IsReadOnly);
        SelectAllKeysCommand = new RelayCommand(() => _doc.Timeline.SelectBoneKeys(Bones(_doc.Current)), () => HasSelection);

        _doc.Selection.Changed += (_, _) => Refresh();
        _doc.PreviewSkeletonChanged += (_, _) => Refresh();
        Refresh();
    }

    public SelectionFieldViewModel Weight { get; }

    public ObservableCollection<BoneActionViewModel> ValuePresets { get; } = [];

    public ObservableCollection<BoneActionViewModel> BodyPresets { get; } = [];

    /// <summary>Weight edits also go to every bone below the selected ones.</summary>
    public bool IncludeChildren
    {
        get => _includeChildren;
        set => Set(ref _includeChildren, value);
    }

    public bool CanIncludeChildren => _doc.FittingSkeleton is not null;

    public RelayCommand KeyAtPlayheadCommand { get; }

    public RelayCommand DeleteAllKeysCommand { get; }

    public RelayCommand SelectAllKeysCommand { get; }

    public bool HasSelection { get; private set; }

    public string Title { get => _title; private set => Set(ref _title, value); }

    public string IndexText { get => _indexText; private set => Set(ref _indexText, value); }

    public string ParentText { get => _parentText; private set => Set(ref _parentText, value); }

    public string KeysText { get => _keysText; private set => Set(ref _keysText, value); }

    /// <summary>Clip offset vs mesh rest offset rows.</summary>
    public ObservableCollection<FactRow> Offsets { get; } = [];

    public string IndexToolTip => FormatDocs.Find("rfa.bone.index")?.Tooltip ?? "Bone index";

    public string KeysToolTip =>
        (FormatDocs.Find("rfa.bone.num_rot_keys")?.Tooltip ?? string.Empty) + "\n\n" + (FormatDocs.Find("rfa.bone.num_pos_keys")?.Tooltip ?? string.Empty);

    public string OffsetToolTip =>
        "The bone's offset from its parent: in this clip (its first position key) and in the preview mesh's bind (rest) pose. "
        + "A clip that stores other lengths than the mesh stretches the skeleton when it plays (the game uses the clip's).";

    public bool IsReadOnly => _doc.IsReadOnly;

    /// <summary>The selected bones the clip has, in selection order.</summary>
    private IEnumerable<int> Bones(RfaClip clip) => _doc.Selection.Bones.Where(b => (uint)b < (uint)clip.BoneCount);

    /// <summary>Re-reads everything.</summary>
    public void Refresh()
    {
        var clip = _doc.Current;
        var bones = Bones(clip).ToList();
        HasSelection = bones.Count > 0;
        var skeleton = _doc.FittingSkeleton;
        Offsets.Clear();
        if (bones.Count == 0)
        {
            Title = "No bone selected";
            IndexText = ParentText = KeysText = string.Empty;
        }
        else if (bones.Count == 1)
        {
            int b = bones[0];
            Title = _doc.BoneDisplayName(b);
            IndexText = b.ToString(CultureInfo.CurrentCulture);
            int parent = skeleton?.EffectiveParents[b] ?? -2;
            ParentText = parent == -2 ? "unknown (no fitting preview mesh)" : parent < 0 ? "none (root)" : $"{_doc.BoneDisplayName(parent)} ({parent})";
            var track = clip.Bones[b];
            KeysText = $"{track.RotationKeys.Length:N0} rotation · {track.PositionKeys.Length:N0} position";
            var clipOffset = track.PositionKeys.Length > 0 ? track.PositionKeys[0].Position : (Vector3?)null;
            Offsets.Add(new FactRow("In this clip", clipOffset is { } co ? Describe(co) : "no position keys (the bone collapses onto its parent)", OffsetToolTip));
            if (skeleton is not null)
            {
                var rest = skeleton.RestLocal[b].Position;
                Offsets.Add(new FactRow("Mesh rest", Describe(rest), OffsetToolTip));
                if (clipOffset is { } c2)
                {
                    float diff = c2.Length() - rest.Length();
                    Offsets.Add(new FactRow("Length difference", string.Format(CultureInfo.CurrentCulture, "{0:+0.####;-0.####;0} m ({1:+0.#;-0.#;0} %)",
                        diff, rest.Length() > 1e-6f ? diff / rest.Length() * 100 : 0), OffsetToolTip));
                }
            }
            else
            {
                Offsets.Add(new FactRow("Mesh rest", "— (needs a preview mesh that fits the clip)", OffsetToolTip));
            }
        }
        else
        {
            Title = $"{bones.Count} bones";
            IndexText = string.Join(", ", bones.Take(12)) + (bones.Count > 12 ? ", …" : string.Empty);
            ParentText = "—";
            int rot = bones.Sum(b => clip.Bones[b].RotationKeys.Length), pos = bones.Sum(b => clip.Bones[b].PositionKeys.Length);
            KeysText = $"{rot:N0} rotation · {pos:N0} position (together)";
        }
        Weight.Refresh();
        RaiseAll(nameof(HasSelection), nameof(CanIncludeChildren), nameof(IsReadOnly));
        foreach (var p in ValuePresets.Concat(BodyPresets)) p.Run.RaiseCanExecuteChanged();
        KeyAtPlayheadCommand.RaiseCanExecuteChanged();
        DeleteAllKeysCommand.RaiseCanExecuteChanged();
        SelectAllKeysCommand.RaiseCanExecuteChanged();
    }

    private static string Describe(Vector3 v) =>
        string.Format(CultureInfo.CurrentCulture, "({0:0.####}, {1:0.####}, {2:0.####}) · {3:0.####} m", v.X, v.Y, v.Z, v.Length());

    private string WeightLabel(float? value = null)
    {
        var bones = Bones(_doc.Current).ToList();
        string who = bones.Count == 1 ? _doc.BoneDisplayName(bones[0]) : $"{bones.Count} bones";
        if (_includeChildren && CanIncludeChildren) who += " and children";
        return value is { } v ? $"Set weight of {who} to {v.ToString("0.##", CultureInfo.CurrentCulture)}" : $"Set weight of {who}";
    }

    private RfaClip SetWeight(RfaClip clip, float value)
    {
        var bones = Bones(clip).ToList();
        if (bones.Count == 0) return clip;
        if (_includeChildren && _doc.FittingSkeleton is { } skeleton)
        {
            var result = clip;
            foreach (int b in bones) result = ClipEdit.SetBoneWeightsForSubtree(result, skeleton.EffectiveParents, b, value);
            return result;
        }
        return ClipEdit.SetBoneWeights(clip, bones, value);
    }

    private void ApplyPreset(string name, Func<Cairn.Rfa.Animation.Skeleton, IEnumerable<int>> bones)
    {
        float value = (float)Weight.Value;
        var skeleton = _doc.FittingSkeleton;
        if (skeleton is null && name != "all bones") return;
        var set = bones(skeleton!).ToList();
        _doc.Apply($"Set weight of {name} to {value.ToString("0.##", CultureInfo.CurrentCulture)}", c => ClipEdit.SetBoneWeights(c, set, value));
    }
}
