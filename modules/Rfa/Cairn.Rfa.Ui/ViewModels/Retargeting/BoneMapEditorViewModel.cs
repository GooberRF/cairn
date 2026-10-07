using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Ui.ViewModels.Retargeting;

/// <summary>An entry of a bone map row's source picker: a source bone, or "none".</summary>
/// <param name="Index">Source bone index, -1 for none.</param>
/// <param name="Label">"3: ult2-bdbn-spine03", or "(none — holds a still pose)".</param>
public sealed record BoneSourceOption(int Index, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A problem of the map as the editor lists it.</summary>
/// <param name="IsError">True when the retarget (or import) cannot run until it is fixed.</param>
/// <param name="Message">What is wrong and how to fix it.</param>
/// <param name="TargetIndex">The target bone concerned, or -1.</param>
public sealed record BoneMapProblemRow(bool IsError, string Message, int TargetIndex)
{
    /// <summary>Segoe MDL2 glyph: error or warning.</summary>
    public string Glyph => IsError ? "\uE783" : "\uE7BA";

    /// <summary>The severity brush key.</summary>
    public string BrushKey => IsError ? "Severity.Error" : "Severity.Warning";
}

/// <summary>One target bone of the bone map table.</summary>
public sealed class BoneMapRowViewModel : ObservableObject
{
    private readonly BoneMapEditorViewModel _owner;
    private BoneSourceOption _selected;
    private BoneMapEntry _entry;
    private bool _isExtra;
    private string? _problem;

    internal BoneMapRowViewModel(BoneMapEditorViewModel owner, BoneMapEntry entry, int depth, IReadOnlyList<BoneSourceOption> options, bool isExtra)
    {
        _owner = owner;
        _entry = entry;
        Depth = depth;
        Options = options;
        _isExtra = isExtra;
        _selected = options.FirstOrDefault(o => o.Index == entry.SourceIndex) ?? options[0];
    }

    /// <summary>The target bone's index.</summary>
    public int TargetIndex => _entry.TargetIndex;

    /// <summary>The target bone's name.</summary>
    public string TargetName => _entry.TargetName;

    /// <summary>"3" — the bone's index (clips address bones by it).</summary>
    public string IndexText => TargetIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>Depth in the target hierarchy (the name is indented by it).</summary>
    public int Depth { get; }

    /// <summary>Left indent of the name, in DIPs.</summary>
    public System.Windows.Thickness Indent => new(Depth * 12, 0, 0, 0);

    /// <summary>"none" then every source bone.</summary>
    public IReadOnlyList<BoneSourceOption> Options { get; }

    /// <summary>The source bone that drives this target bone (setting it edits the map).</summary>
    public BoneSourceOption Selected
    {
        get => _selected;
        set
        {
            if (value is null || ReferenceEquals(value, _selected)) return;
            _selected = value;
            Raise();
            _owner.SetSource(TargetIndex, value.Index);
        }
    }

    /// <summary>How the bone will be driven.</summary>
    public BoneMapStatus Status => _entry.Status;

    /// <summary>The chip text: mapped, unmapped, extra, reparented, cross-branch, error.</summary>
    public string StatusText => _entry.Status switch
    {
        BoneMapStatus.Mapped => "mapped",
        BoneMapStatus.Reparented => "reparented",
        BoneMapStatus.CrossBranch => "cross-branch",
        BoneMapStatus.Unmapped => _isExtra ? "extra" : "unmapped",
        BoneMapStatus.ParentUnmapped => "parent unmapped",
        BoneMapStatus.RootFromNonRoot => "root mismatch",
        _ => _entry.Status.ToString(),
    };

    /// <summary>The chip's tooltip: what the status means.</summary>
    public string StatusToolTip => _entry.Status switch
    {
        BoneMapStatus.Mapped => "Follows its source bone one for one: key times and eases are kept.",
        BoneMapStatus.Reparented => "Hangs off a different parent than its source bone: its track is resampled from the source chain (eases kept only where keys coincide).",
        BoneMapStatus.CrossBranch => "Its parent follows a source bone on another branch: evaluated in model space (eases lost). Check this pairing.",
        BoneMapStatus.Unmapped => _isExtra
            ? "An extra bone the source has no counterpart for: it holds a still pose (the reference clip's, or the bind) and rides on its parent."
            : "Not mapped, although the automatic map would pair it: it holds a still pose. Pick a source bone, or Auto-map.",
        BoneMapStatus.ParentUnmapped => "Cannot run: this bone has a source but its parent has none. Map the parent, or set this bone to none.",
        BoneMapStatus.RootFromNonRoot => "Cannot run: the target root must follow the source's root bone.",
        _ => string.Empty,
    };

    /// <summary>The chip's theme brush (background).</summary>
    public string StatusBrushKey => _entry.Status switch
    {
        BoneMapStatus.Mapped => "Severity.InfoSoft",
        BoneMapStatus.Unmapped => "App.AccentSoft",
        BoneMapStatus.Reparented or BoneMapStatus.CrossBranch => "Severity.WarningSoft",
        _ => "Severity.ErrorSoft",
    };

    /// <summary>"exact name", "canonical", "stock table", "similar words", "by hand", or empty.</summary>
    public string MatchText => _entry.Match switch
    {
        BoneMatchKind.ExactName => "exact name",
        BoneMatchKind.CanonicalName => "canonical name",
        BoneMatchKind.BuiltInTable => "stock table",
        BoneMatchKind.Fuzzy => "similar words",
        BoneMatchKind.Manual => "by hand",
        _ => string.Empty,
    };

    /// <summary>The map problem that names this bone, or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set
        {
            if (!Set(ref _problem, value)) return;
            Raise(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem is not null;

    /// <summary>"Source bone for ult2-bdbn-head".</summary>
    public string PickerName => $"Source bone for {TargetName}";

    internal void Update(BoneMapEntry entry, bool isExtra, string? problem)
    {
        _entry = entry;
        _isExtra = isExtra;
        var option = Options.FirstOrDefault(o => o.Index == entry.SourceIndex) ?? Options[0];
        if (!ReferenceEquals(option, _selected))
        {
            _selected = option;
            Raise(nameof(Selected));
        }
        Problem = problem;
        RaiseAll(nameof(Status), nameof(StatusText), nameof(StatusToolTip), nameof(StatusBrushKey), nameof(MatchText));
    }
}

/// <summary>
/// The editable bone map table shared by the retarget dialogs and the glTF animation import: one row per
/// target bone in hierarchy order with its source picker ("none" included) and a status chip, the map's
/// problems from <see cref="BoneMap.Validate"/>, Auto-map and Clear. Every edit goes through
/// <see cref="BoneMap.With(int, int)"/> (statuses recomputed) and raises <see cref="MapChanged"/>.
/// </summary>
public sealed class BoneMapEditorViewModel : ObservableObject
{
    private BoneMap? _map;
    private BoneMap? _auto;
    private Func<BoneMap>? _autoMap;
    private string _summary = "No skeletons yet.";
    private bool _loading;

    public BoneMapEditorViewModel()
    {
        AutoMapCommand = new RelayCommand(AutoMap, () => _autoMap is not null);
        ClearCommand = new RelayCommand(Clear, () => _map is not null);
    }

    /// <summary>The map being edited, or null before <see cref="Load"/>.</summary>
    public BoneMap? Map => _map;

    /// <summary>Rows in target hierarchy order.</summary>
    public ObservableCollection<BoneMapRowViewModel> Rows { get; } = [];

    /// <summary>The map's problems, errors first.</summary>
    public ObservableCollection<BoneMapProblemRow> Problems { get; } = [];

    /// <summary>True when some problem stops the retarget.</summary>
    public bool HasErrors => Problems.Any(p => p.IsError);

    /// <summary>True when there is anything in <see cref="Problems"/>.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>"22 mapped · 1 reparented · 2 extra · 3 source bones unused".</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>The source bones no target bone follows (their motion is dropped), as one line, or empty.</summary>
    public string UnusedText => _map is { UnusedSourceBones.Length: > 0 } m
        ? "Not used (their motion is dropped): " + string.Join(", ", m.UnusedSourceBones.Select(b => b.Name))
        : string.Empty;

    /// <summary>The header of the first column ("Target bone").</summary>
    public string TargetHeader { get; set; } = "Target bone";

    /// <summary>The header of the picker column ("Source bone", "glTF node").</summary>
    public string SourceHeader { get; set; } = "Source bone";

    public RelayCommand AutoMapCommand { get; }

    public RelayCommand ClearCommand { get; }

    /// <summary>Raised after every change of <see cref="Map"/> (an edit, Auto-map, Clear or Load).</summary>
    public event EventHandler? MapChanged;

    /// <summary>
    /// Shows <paramref name="map"/>. <paramref name="autoMap"/> (the automatic map for the same skeletons)
    /// backs the Auto-map button and tells "unmapped" (the automatic map would pair it) from "extra" apart.
    /// </summary>
    public void Load(BoneMap map, Func<BoneMap>? autoMap)
    {
        ArgumentNullException.ThrowIfNull(map);
        _autoMap = autoMap;
        _auto = null;
        try { _auto = autoMap?.Invoke(); }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { _auto = null; }
        _loading = true;
        try
        {
            Rows.Clear();
            var options = new List<BoneSourceOption> { new(-1, "(none — holds a still pose)") };
            for (int i = 0; i < map.SourceBones.Length; i++) options.Add(new BoneSourceOption(i, $"{i}: {map.SourceBones[i].Name}"));
            foreach (var (index, depth) in HierarchyOrder(map.TargetBones))
                Rows.Add(new BoneMapRowViewModel(this, map.Entries[index], depth, options, IsExtra(index)));
        }
        finally { _loading = false; }
        Apply(map);
        AutoMapCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Clears the table (no skeletons).</summary>
    public void Reset(string summary)
    {
        _map = null;
        _auto = null;
        _autoMap = null;
        Rows.Clear();
        Problems.Clear();
        Summary = summary;
        RaiseAll(nameof(Map), nameof(HasErrors), nameof(HasProblems), nameof(UnusedText));
        AutoMapCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Drives <paramref name="targetIndex"/> by <paramref name="sourceIndex"/> (-1: none).</summary>
    public void SetSource(int targetIndex, int sourceIndex)
    {
        if (_loading || _map is null) return;
        if (_map.SourceOf(targetIndex) == sourceIndex) return;
        Apply(_map.With(targetIndex, sourceIndex));
    }

    /// <summary>Replaces the whole map (a loaded profile's map, Auto-map).</summary>
    public void SetMap(BoneMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (_map is null || map.TargetBones.Length != Rows.Count) Load(map, _autoMap);
        else Apply(map);
    }

    private void AutoMap()
    {
        if (_autoMap is null) return;
        try
        {
            _auto = _autoMap();
            SetMap(_auto);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Summary = "The automatic map could not be made: " + ex.Message;
        }
    }

    private void Clear()
    {
        if (_map is null) return;
        Apply(BoneMap.Create(_map.SourceBones, _map.TargetBones, [.. Enumerable.Repeat(-1, _map.TargetBones.Length)]));
    }

    private bool IsExtra(int target) => _auto is null || _auto.SourceOf(target) < 0;

    private void Apply(BoneMap map)
    {
        _map = map;
        var problems = map.Validate();
        var byBone = problems.Where(p => p.TargetIndex >= 0).GroupBy(p => p.TargetIndex).ToDictionary(g => g.Key, g => g.First().Message);
        _loading = true;
        try
        {
            foreach (var row in Rows)
                row.Update(map.Entries[row.TargetIndex], IsExtra(row.TargetIndex), byBone.TryGetValue(row.TargetIndex, out var m) ? m : null);
        }
        finally { _loading = false; }
        Problems.Clear();
        foreach (var p in problems.OrderBy(p => p.Severity == BoneMapSeverity.Error ? 0 : 1))
            Problems.Add(new BoneMapProblemRow(p.Severity == BoneMapSeverity.Error, p.Message, p.TargetIndex));

        int mapped = map.Count(BoneMapStatus.Mapped), reparented = map.Count(BoneMapStatus.Reparented) + map.Count(BoneMapStatus.CrossBranch);
        int unmapped = map.Count(BoneMapStatus.Unmapped);
        int errors = map.Count(BoneMapStatus.ParentUnmapped) + map.Count(BoneMapStatus.RootFromNonRoot);
        var parts = new List<string> { $"{mapped} mapped" };
        if (reparented > 0) parts.Add($"{reparented} reparented");
        if (unmapped > 0) parts.Add($"{unmapped} without a source");
        if (errors > 0) parts.Add($"{errors} unsupported");
        if (map.UnusedSourceBones.Length > 0) parts.Add($"{map.UnusedSourceBones.Length} source bone{(map.UnusedSourceBones.Length == 1 ? "" : "s")} unused");
        Summary = string.Join(" · ", parts);
        RaiseAll(nameof(Map), nameof(HasErrors), nameof(HasProblems), nameof(UnusedText));
        ClearCommand.RaiseCanExecuteChanged();
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Target bones depth-first from each root, children in index order (cycles cut).</summary>
    private static IEnumerable<(int Index, int Depth)> HierarchyOrder(IReadOnlyList<BoneMapBone> bones)
    {
        int n = bones.Count;
        var parents = Cairn.Rfa.Animation.ForwardKinematics.EffectiveParents([.. bones.Select(b => b.Parent)]);
        var children = new List<int>[n];
        for (int i = 0; i < n; i++) children[i] = [];
        for (int i = 0; i < n; i++)
        {
            if (parents[i] >= 0) children[parents[i]].Add(i);
        }
        var seen = new bool[n];
        var order = new List<(int, int)>(n);
        void Visit(int i, int depth)
        {
            if (seen[i]) return;
            seen[i] = true;
            order.Add((i, depth));
            foreach (int c in children[i]) Visit(c, depth + 1);
        }
        for (int i = 0; i < n; i++)
        {
            if (parents[i] < 0) Visit(i, 0);
        }
        for (int i = 0; i < n; i++) Visit(i, 0);
        return order;
    }
}
