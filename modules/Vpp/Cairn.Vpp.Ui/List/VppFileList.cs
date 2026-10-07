using System.Text.RegularExpressions;
using Cairn.Ui.Mvvm;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Ui.List;

/// <summary>A column the list can be sorted by (the view only; the packfile order changes through Packfile > Sort by).</summary>
public enum VppListSort { PackfileOrder, Name, Type, Size, State, Info }

/// <summary>One check box of the type filter: a whole category or one extension, with the number of entries.</summary>
public sealed class VppTypeOption(VppFileList owner, string key, string label, bool isCategory, VppFileCategory category) : ObservableObject
{
    private bool _isChecked;
    private int _count;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public bool IsCategory { get; } = isCategory;
    public VppFileCategory Category { get; } = category;
    public int Count { get => _count; internal set { if (Set(ref _count, value)) Raise(nameof(Text)); } }
    public string Text => $"{Label}  ({Count:N0})";
    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) owner.OnOptionToggled(this); }
    }
    internal void SetQuietly(bool value) { if (_isChecked != value) { _isChecked = value; Raise(nameof(IsChecked)); } }
}

/// <summary>A check box of the "Problems" filter: lists only the entries with one problem code, with their number.</summary>
public sealed class VppProblemOption(VppFileList owner, string code, string label) : ObservableObject
{
    private bool _isChecked;
    private int _count;
    public string Code { get; } = code;
    public string Label { get; } = label;
    public int Count { get => _count; internal set { if (Set(ref _count, value)) { Raise(nameof(Text)); Raise(nameof(IsAvailable)); Raise(nameof(HasMatches)); } } }
    /// <summary>True when at least one entry has the problem: the File types panel shows the check box only then.</summary>
    public bool HasMatches => Count > 0;
    public string Text => $"{Label}  ({Count:N0})";
    /// <summary>False when no entry has the problem and the filter is off (nothing to show).</summary>
    public bool IsAvailable => Count > 0 || _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) { Raise(nameof(IsAvailable)); owner.OnProblemOptionToggled(); } }
    }

    /// <summary>Unticks without refiltering (the caller refilters): used when the last matching entry is gone, so the list is not left filtered by a hidden box.</summary>
    internal void UncheckQuietly() { if (_isChecked) { _isChecked = false; Raise(nameof(IsChecked)); Raise(nameof(IsAvailable)); } }
}

/// <summary>
/// The file list's view model: rows for the current snapshot, filtered (name text with wildcards, type check boxes)
/// and sorted (column headers). Rebuilt in one go on every snapshot change, which stays well under a frame for the
/// largest stock packfile (2,568 entries).
/// </summary>
public sealed class VppFileList : ObservableObject
{
    private IReadOnlyList<VppEntryRow> _all = [];
    private IReadOnlyList<VppEntryRow> _visible = [];
    private string _filterText = string.Empty;
    private Func<VppEntryRow, bool> _nameMatch = _ => true;
    private VppListSort _sort = VppListSort.PackfileOrder;
    private bool _descending;
    private bool _syncingOptions;

    /// <summary>Every row of the snapshot in packfile order.</summary>
    public IReadOnlyList<VppEntryRow> AllRows => _all;
    /// <summary>The rows shown, filtered and sorted (the list's ItemsSource).</summary>
    public IReadOnlyList<VppEntryRow> Visible { get => _visible; private set => Set(ref _visible, value); }
    public List<VppTypeOption> TypeOptions { get; } = [];
    public VppListSort Sort => _sort;
    public bool Descending => _descending;
    public bool IsFiltered => _filterText.Length > 0 || TypeOptions.Any(o => !o.IsCategory && o.IsChecked) || LongNameOption.IsChecked;
    /// <summary>"1,204 of 2,568 shown" or empty when nothing is filtered.</summary>
    public string FilterSummary => IsFiltered ? $"{Visible.Count:N0} of {_all.Count:N0} shown" : string.Empty;
    public string TypeFilterLabel
    {
        get
        {
            var picked = TypeOptions.Where(o => !o.IsCategory && o.IsChecked).ToList();
            return picked.Count == 0 ? "All types" : picked.Count <= 2 ? string.Join(", ", picked.Select(p => p.Key)) : $"{picked.Count} types";
        }
    }
    /// <summary>Raised after <see cref="Visible"/> was replaced.</summary>
    public event EventHandler? VisibleChanged;

    public string FilterText
    {
        get => _filterText;
        set
        {
            value ??= string.Empty;
            if (!Set(ref _filterText, value)) return;
            _nameMatch = BuildMatcher(value.Trim());
            Refilter();
        }
    }

    /// <summary>Substring match without wildcards, else * and ? matched against the whole name; case-insensitive both ways.</summary>
    public static Func<VppEntryRow, bool> BuildMatcher(string text)
    {
        if (text.Length == 0) return _ => true;
        if (text.IndexOfAny(['*', '?']) < 0) return r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
        var regex = new Regex("^" + Regex.Escape(text).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return r => regex.IsMatch(r.Name);
    }

    /// <summary>Takes a new snapshot (and the validator's problems for its rows); filters, sort and type choices are kept.</summary>
    public void Load(VppPackage package, IReadOnlyList<VppProblem>? problems = null)
    {
        var byName = (problems ?? [])
            .Where(p => p.EntryName is not null && p.Severity != VppSeverity.Info)
            .GroupBy(p => p.EntryName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<VppProblem>)g.ToList(), StringComparer.OrdinalIgnoreCase);
        var rows = new VppEntryRow[package.Items.Length];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new VppEntryRow(package.Items[i], i, byName.GetValueOrDefault(package.Items[i].Name));
        _all = rows;
        RebuildOptions();
        LongNameOption.Count = rows.Count(r => r.NameTooLong);
        if (LongNameOption.Count == 0) LongNameOption.UncheckQuietly();
        // cached Info lines at once (before sorting, which may be by Info); the rest in the background below
        var missing = InfoCache?.ApplyCached(rows);
        Refilter();
        if (missing is { Count: > 0 })
        {
            // the rows shown first, top of the shown order first, then the filtered-out ones
            var wanted = missing.ToHashSet();
            var first = _visible.Where(wanted.Contains).ToList();
            var shown = first.ToHashSet();
            InfoCache!.Fill([.. first, .. missing.Where(r => !shown.Contains(r))]);
        }
        Raise(nameof(AllRows));
    }

    private VppInfoCache? _infoCache;

    /// <summary>Fills the rows' Info column (null: the column stays empty). Set once by the document.</summary>
    public VppInfoCache? InfoCache
    {
        get => _infoCache;
        set
        {
            if (_infoCache is { } old) old.Filled -= OnInfoFilled;
            _infoCache = value;
            if (value is not null) value.Filled += OnInfoFilled;
        }
    }

    // Sorted by Info while the lines were still arriving: sort again once they are all in (not per batch, so rows
    // do not jump around while the column fills).
    private void OnInfoFilled(object? sender, EventArgs e)
    {
        if (_sort == VppListSort.Info) Refilter();
    }

    /// <summary>The "Problems" filter of the File types panel: only entries whose names are too long for the game.</summary>
    public VppProblemOption LongNameOption { get; }

    public VppFileList() => LongNameOption = new VppProblemOption(this, VppEntryRow.LongNameCode, $"Names longer than {VppValidator.MaxAssetNameLength} characters");

    internal void OnProblemOptionToggled() => Refilter();

    /// <summary>Sorts the view by <paramref name="sort"/>; the same column again flips the direction.</summary>
    public void SortBy(VppListSort sort, bool? descending = null)
    {
        _descending = descending ?? (sort == _sort && !_descending);
        _sort = sort;
        Raise(nameof(Sort)); Raise(nameof(Descending));
        Refilter();
    }

    /// <summary>Shows only <paramref name="extensions"/> (lower case with the dot); empty shows every type.</summary>
    public void ShowOnlyTypes(IEnumerable<string> extensions)
    {
        var set = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _syncingOptions = true;
        foreach (var o in TypeOptions.Where(o => !o.IsCategory)) o.SetQuietly(set.Contains(o.Key));
        SyncCategoryChecks();
        _syncingOptions = false;
        Refilter();
    }

    internal void OnOptionToggled(VppTypeOption option)
    {
        if (_syncingOptions) return;
        _syncingOptions = true;
        if (option.IsCategory)
            foreach (var o in TypeOptions.Where(o => !o.IsCategory && o.Category == option.Category)) o.SetQuietly(option.IsChecked);
        SyncCategoryChecks();
        _syncingOptions = false;
        Refilter();
    }

    private void SyncCategoryChecks()
    {
        foreach (var c in TypeOptions.Where(o => o.IsCategory))
            c.SetQuietly(TypeOptions.Where(o => !o.IsCategory && o.Category == c.Category).All(o => o.IsChecked));
    }

    private void RebuildOptions()
    {
        var checkedKeys = TypeOptions.Where(o => !o.IsCategory && o.IsChecked).Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byExt = _all.GroupBy(r => r.Extension.Length == 0 ? "(none)" : r.Extension.ToLowerInvariant())
            .Select(g => (Ext: g.Key, Rows: g.ToList())).ToList();
        var options = new List<VppTypeOption>();
        foreach (var cat in byExt.GroupBy(e => e.Rows[0].Category).OrderBy(c => c.Key.ToString(), StringComparer.Ordinal))
        {
            options.Add(new VppTypeOption(this, "category:" + cat.Key, CategoryName(cat.Key), true, cat.Key) { Count = cat.Sum(e => e.Rows.Count) });
            foreach (var e in cat.OrderBy(e => e.Ext, StringComparer.Ordinal))
            {
                var option = new VppTypeOption(this, e.Ext, $"{e.Ext}  {e.Rows[0].TypeName}", false, cat.Key) { Count = e.Rows.Count };
                option.SetQuietly(checkedKeys.Contains(e.Ext));
                options.Add(option);
            }
        }
        TypeOptions.Clear();
        TypeOptions.AddRange(options);
        _syncingOptions = true;
        SyncCategoryChecks();
        _syncingOptions = false;
        Raise(nameof(TypeOptions));
    }

    private static string CategoryName(VppFileCategory category) => category switch
    {
        VppFileCategory.Image => "Images",
        VppFileCategory.Audio => "Sounds",
        VppFileCategory.Mesh => "Meshes",
        VppFileCategory.Animation => "Animations",
        VppFileCategory.Effect => "Effects",
        VppFileCategory.Level => "Levels",
        VppFileCategory.Table => "Tables",
        _ => category.ToString(),
    };

    private void Refilter()
    {
        var types = TypeOptions.Where(o => !o.IsCategory && o.IsChecked).Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool longOnly = LongNameOption.IsChecked;
        IEnumerable<VppEntryRow> rows = _all.Where(r => _nameMatch(r) && (types.Count == 0 || types.Contains(r.Extension.Length == 0 ? "(none)" : r.Extension))
            && (!longOnly || r.NameTooLong));
        rows = _sort switch
        {
            VppListSort.Name => Order(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
            VppListSort.Type => Order(rows, r => r.TypeName + "\0" + r.Name, StringComparer.OrdinalIgnoreCase),
            VppListSort.Size => Order(rows, r => r.Size, Comparer<long>.Default),
            VppListSort.State => Order(rows, r => r.StateText.Length == 0 ? "￿" : r.StateText, StringComparer.Ordinal),
            // numbers in the text compare by value ("8 frames" before "10 frames"); empty lines last
            VppListSort.Info => Order(rows, r => r.InfoText.Length == 0 ? "￿" : InfoSortKey(r.InfoText), Cairn.Formats.Text.NaturalStringComparer.Instance),
            _ => _descending ? rows.Reverse() : rows,
        };
        Visible = rows.ToList();
        Raise(nameof(IsFiltered)); Raise(nameof(FilterSummary)); Raise(nameof(TypeFilterLabel));
        VisibleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Info text without thousands separators, so "1,234 triangles" compares as 1234.</summary>
    private static string InfoSortKey(string text)
    {
        if (!text.Contains(',', StringComparison.Ordinal)) return text;
        var b = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ',' && i > 0 && i + 1 < text.Length && char.IsAsciiDigit(text[i - 1]) && char.IsAsciiDigit(text[i + 1])) continue;
            b.Append(text[i]);
        }
        return b.ToString();
    }

    // stable: ties keep packfile order in both directions
    private IEnumerable<VppEntryRow> Order<TKey>(IEnumerable<VppEntryRow> rows, Func<VppEntryRow, TKey> key, IComparer<TKey> comparer) =>
        _descending ? rows.OrderByDescending(key, comparer).ThenBy(r => r.Index) : rows.OrderBy(key, comparer).ThenBy(r => r.Index);
}
