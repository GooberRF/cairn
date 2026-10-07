using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cairn.Ui.Mvvm;
using Cairn.Assets;
using Cairn.Formats.Tbl;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>What a row of the Table usage panel stands for.</summary>
public enum TableUsageNodeKind
{
    /// <summary>A heading ("Table lines", or one class's clip list on a mesh).</summary>
    Group,
    /// <summary>A table line naming the clip document (clip document).</summary>
    Usage,
    /// <summary>A mesh the tables play the clip document on (clip document).</summary>
    Mesh,
    /// <summary>A clip one of the mesh document's classes plays (mesh document).</summary>
    Clip,
}

/// <summary>One row of the Table usage tree.</summary>
public sealed class TableUsageNode : ObservableObject
{
    private bool _isExpanded = true;
    private bool _isSelected;

    internal TableUsageNode(TableUsageNodeKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public TableUsageNodeKind Kind { get; }

    /// <summary>The main text, e.g. <c>state "walk"</c> or "ult2_guard.v3c".</summary>
    public string Title { get; }

    /// <summary>Secondary text after the title, e.g. "ult2 · entity.tbl line 1234".</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>A short badge, e.g. "25 bones"; empty for none.</summary>
    public string Badge { get; init; } = string.Empty;

    /// <summary>True when the badge is a problem (bone counts differ, not in the library).</summary>
    public bool IsWarning { get; init; }

    /// <summary>True for the clip document's current preview mesh.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>A Segoe MDL2 glyph for the row.</summary>
    public string Glyph { get; init; } = string.Empty;

    public string ToolTip { get; init; } = string.Empty;

    /// <summary>What a screen reader says for the row.</summary>
    public string AutomationName => string.Join(", ", new[] { Title, Subtitle, Badge }.Where(s => s.Length > 0));

    /// <summary>The table line (usage and clip rows).</summary>
    public ClipUsage? Usage { get; init; }

    /// <summary>The library's copy of the mesh (mesh rows), when it has one.</summary>
    public LibraryMesh? Mesh { get; init; }

    /// <summary>The library's copy of the clip (clip rows), when it has one.</summary>
    public LibraryClip? Clip { get; init; }

    /// <summary>The lines a group stands for (its whole block when copied).</summary>
    public IReadOnlyList<ClipUsage> GroupUsages { get; init; } = [];

    public ObservableCollection<TableUsageNode> Children { get; } = [];

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>The primary action's caption for the context menu ("Preview on this mesh", "Open").</summary>
    public string PrimaryActionHeader => Kind switch
    {
        TableUsageNodeKind.Mesh => "_Preview on this mesh",
        TableUsageNodeKind.Clip => "_Open",
        _ => string.Empty,
    };

    public bool HasPrimaryAction => Kind is TableUsageNodeKind.Mesh or TableUsageNodeKind.Clip;

    public bool HasPreviewAction => Kind == TableUsageNodeKind.Clip;

    public bool CanCopy => Usage is not null || GroupUsages.Count > 0;
}

/// <summary>
/// The Table usage tab of the bottom panel for one document: which table lines name a clip and which
/// meshes would play it, or which clips a mesh's classes play; copies a table line in the stock layout.
/// One per document, made on first use (<see cref="For"/>); it follows the library, the tables and the
/// document, and lets go of the shell when its document closes.
/// </summary>
public sealed class TableUsageViewModel : ObservableObject
{
    private static readonly ConditionalWeakTable<DocumentViewModel, TableUsageViewModel> Instances = new();

    private readonly DocumentViewModel _document;
    private readonly RfaWorkspace _shell;
    private TblSnippetStyle _style = TblSnippetStyle.Entity;
    private bool _styleChosen;
    private string _summary = string.Empty;
    private string _emptyText = string.Empty;
    private string _sourcesText = string.Empty;
    private bool _detached;

    private TableUsageViewModel(DocumentViewModel document)
    {
        _document = document;
        _shell = document.Shell;
        _shell.Assets.LibraryChanged += OnAssetsChanged;
        _shell.Assets.UsageChanged += OnAssetsChanged;
        _shell.Assets.ProgressChanged += OnProgressChanged;
        _shell.Documents.CollectionChanged += OnDocumentsChanged;
        _document.PropertyChanged += OnDocumentPropertyChanged;
        if (_document is ClipDocumentViewModel clip) clip.PreviewSkeletonChanged += OnPreviewChanged;

        ActivateCommand = new RelayCommand(p => Activate(p as TableUsageNode ?? Selected), p => (p as TableUsageNode ?? Selected)?.HasPrimaryAction == true);
        PreviewClipCommand = new RelayCommand(p => PreviewClip(p as TableUsageNode ?? Selected), p => (p as TableUsageNode ?? Selected)?.Clip is not null);
        CopyCommand = new RelayCommand(p => Copy(p as TableUsageNode ?? Selected), _ => CanCopyAnything);
        SetStyleCommand = new RelayCommand(p =>
        {
            if (p is TblSnippetStyle s || Enum.TryParse(p as string, out s)) Style = s;
        });
        Refresh();
    }

    /// <summary>The panel's view-model for <paramref name="document"/>, made on first use; null for no document.</summary>
    public static TableUsageViewModel? For(DocumentViewModel? document) =>
        document is null ? null : Instances.GetValue(document, d => new TableUsageViewModel(d));

    /// <summary>The rows: groups with their lines, meshes or clips.</summary>
    public ObservableCollection<TableUsageNode> Roots { get; } = [];

    /// <summary>One line above the tree, e.g. "3 table lines play ult2_walk.rfa, on 7 meshes".</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>The explanation shown instead of the tree when there is nothing to list.</summary>
    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }

    /// <summary>True when <see cref="EmptyText"/> shows instead of the tree.</summary>
    public bool IsEmpty => Roots.Count == 0;

    /// <summary>True when the empty state is about missing tables (it offers Settings).</summary>
    public bool OffersSettings { get; private set; }

    /// <summary>Where the tables came from ("entity.tbl and weapons.tbl from tables.vpp"), and any that failed.</summary>
    public string SourcesText { get => _sourcesText; private set => Set(ref _sourcesText, value); }

    /// <summary>The table layout Copy uses.</summary>
    public TblSnippetStyle Style
    {
        get => _style;
        set
        {
            _styleChosen = true;
            if (Set(ref _style, value)) RaiseAll(nameof(IsEntityStyle), nameof(IsWeaponStyle), nameof(CopyToolTip));
        }
    }

    public bool IsEntityStyle { get => _style == TblSnippetStyle.Entity; set { if (value) Style = TblSnippetStyle.Entity; } }

    public bool IsWeaponStyle { get => _style == TblSnippetStyle.Weapon; set { if (value) Style = TblSnippetStyle.Weapon; } }

    /// <summary>The selected row (the view keeps it in step with the tree).</summary>
    public TableUsageNode? Selected
    {
        get => Roots.SelectMany(Flatten).FirstOrDefault(n => n.IsSelected);
    }

    /// <summary>Double-click / Enter: a mesh row previews the clip on it, a clip row opens the clip.</summary>
    public RelayCommand ActivateCommand { get; }

    /// <summary>A clip row's second action: play it on this mesh document.</summary>
    public RelayCommand PreviewClipCommand { get; }

    /// <summary>Copies the selected line (or the selected group's block) to the clipboard.</summary>
    public RelayCommand CopyCommand { get; }

    public RelayCommand SetStyleCommand { get; }

    /// <summary>The empty state's way to fix missing tables.</summary>
    public RelayCommand SettingsCommand => _shell.SettingsCommand;

    /// <summary>What Copy will copy, for its tooltip.</summary>
    public string CopyToolTip
    {
        get
        {
            string layout = _style == TblSnippetStyle.Entity ? "entity.tbl" : "weapons.tbl";
            return _document is ClipDocumentViewModel
                ? $"Copy the selected line (or every line, or a new +State: line for an unused clip) in {layout}'s layout"
                : $"Copy the selected clip's line (or the selected class's whole list) in {layout}'s layout";
        }
    }

    private bool CanCopyAnything => _document is ClipDocumentViewModel || Roots.SelectMany(Flatten).Any(n => n.CanCopy);

    // ── Building ─────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the rows from the current library, tables and document.</summary>
    public void Refresh()
    {
        if (_detached) return;
        var selectedKey = Selected is { } s ? Key(s) : null;
        var expanded = Roots.Where(r => !r.IsExpanded).Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Roots.Clear();
        OffersSettings = false;
        var assets = _shell.Assets;
        var usage = assets.Usage;

        SourcesText = DescribeSources(usage);
        string? tablesProblem = TablesProblem(usage);
        if (tablesProblem is not null)
        {
            Summary = string.Empty;
            EmptyText = tablesProblem;
        }
        else if (_document is ClipDocumentViewModel clip)
        {
            BuildForClip(clip, usage, assets.Snapshot);
        }
        else if (_document is MeshDocumentViewModel mesh)
        {
            BuildForMesh(mesh, usage, assets.Snapshot);
        }

        foreach (var node in Roots.SelectMany(Flatten))
        {
            string key = Key(node);
            if (expanded.Contains(key)) node.IsExpanded = false;
            if (selectedKey is not null && string.Equals(key, selectedKey, StringComparison.OrdinalIgnoreCase)) node.IsSelected = true;
        }
        if (!_styleChosen)
        {
            var first = Roots.SelectMany(Flatten).FirstOrDefault(n => n.Usage is not null)?.Usage;
            _style = first?.Table == ClipUsageIndex.WeaponsTable ? TblSnippetStyle.Weapon : TblSnippetStyle.Entity;
        }
        RaiseAll(nameof(IsEmpty), nameof(OffersSettings), nameof(IsEntityStyle), nameof(IsWeaponStyle), nameof(CopyToolTip));
        CopyCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Why the tables cannot be shown at all, or null when they can.</summary>
    private string? TablesProblem(ClipUsageIndex usage)
    {
        var assets = _shell.Assets;
        if (!assets.HasSources)
        {
            OffersSettings = true;
            return "The game's tables are not available: no Red Faction folder or search folder is set. "
                + "Set the game directory in Tools › Settings, and this panel lists the entity.tbl and weapons.tbl lines that use each clip.";
        }
        if (usage.TableSources.Count == 0 && usage.TableErrors.Count == 0)
        {
            if (assets.IsLoading || ReferenceEquals(usage, ClipUsageIndex.Empty) && assets.Snapshot.Clips.Length == 0)
                return "Reading the game's tables…";
            OffersSettings = true;
            return "No entity.tbl or weapons.tbl was found in the game directory or the search folders, so nothing says which classes play which clips. "
                + "Check the game directory in Tools › Settings.";
        }
        if (usage.Usages.Length == 0 && usage.TableErrors.Count > 0)
        {
            OffersSettings = true;
            return "The game's tables could not be read: " + string.Join(" ", usage.TableErrors.Values);
        }
        return null;
    }

    private static string DescribeSources(ClipUsageIndex usage)
    {
        if (usage.TableSources.Count == 0 && usage.TableErrors.Count == 0) return string.Empty;
        var byPlace = usage.TableSources
            .GroupBy(p => p.Value.DisplayLocation, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{string.Join(", ", g.Select(p => p.Key))} from {g.Key}");
        string text = "Tables: " + string.Join("; ", byPlace);
        if (usage.TableErrors.Count > 0) text += ". Not readable: " + string.Join(" ", usage.TableErrors.Values);
        return text;
    }

    private void BuildForClip(ClipDocumentViewModel clip, ClipUsageIndex usage, LibrarySnapshot library)
    {
        string name = clip.DisplayName;
        int bones = clip.Current.BoneCount;
        var uses = usage.UsagesOf(name);
        if (uses.Count == 0)
        {
            Summary = string.Empty;
            EmptyText = UnusedClipText(name);
            return;
        }

        var lines = new TableUsageNode(TableUsageNodeKind.Group, $"Table lines ({uses.Count})")
        {
            Glyph = "",
            GroupUsages = uses,
            ToolTip = "Every line of the game's tables that names this clip (the clip is known by its base name, globally)",
        };
        foreach (var u in uses)
        {
            lines.Children.Add(new TableUsageNode(TableUsageNodeKind.Usage, $"{KindWord(u.Kind)} \"{u.SlotName}\"")
            {
                Glyph = u.Kind == ClipUsageKind.State ? "" : "",
                Subtitle = $"{u.ClassName}{(u.WeaponBlock is null ? "" : $" · weapon {u.WeaponBlock}")} · {u.Table} line {u.Line}",
                Badge = u.Sound is { Length: > 0 } sound ? sound : string.Empty,
                Usage = u,
                ToolTip = $"{u.Table}, line {u.Line}: class {u.ClassName}{(u.WeaponBlock is null ? "" : $", +Weapon Specific: \"{u.WeaponBlock}\"")} "
                    + $"plays {u.Clip.Original} as the {KindWord(u.Kind)} \"{u.SlotName}\""
                    + (u.Kind == ClipUsageKind.Action ? " (a one-shot layer over the states)" : " (a looping base animation)")
                    + (u.Sound is { Length: > 0 } s2 ? $", with the sound {s2}" : "") + ".",
            });
        }
        Roots.Add(lines);

        var meshNames = usage.MeshesForClip(name);
        var meshes = new TableUsageNode(TableUsageNodeKind.Group, $"Meshes that play it ({meshNames.Count})")
        {
            Glyph = "",
            ToolTip = "The character meshes the tables play this clip on. Double-click one to preview the clip on it.",
        };
        string key = ClipUsageIndex.ClipKey(name);
        string? current = clip.Scene.MeshName;
        int mismatched = 0;
        foreach (string meshName in meshNames)
        {
            var routes = usage.MeshClipLists
                .Where(l => string.Equals(l.MeshName, meshName, StringComparison.OrdinalIgnoreCase)
                    && l.Clips.Any(c => string.Equals(c.ClipBaseName, key, StringComparison.OrdinalIgnoreCase)))
                .Select(l => l.Character is { } character ? $"{character} ({l.Table})" : $"{l.ClassName} ({l.Table})")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var found = library.FindMesh(meshName);
            bool differs = found is { HasSkeleton: true } && found.BoneCount != bones;
            if (differs) mismatched++;
            bool isCurrent = current is not null && string.Equals(current, meshName, StringComparison.OrdinalIgnoreCase);
            meshes.Children.Add(new TableUsageNode(TableUsageNodeKind.Mesh, meshName)
            {
                Glyph = "",
                Subtitle = (isCurrent ? "preview mesh · " : "") + string.Join(", ", routes.Take(3)) + (routes.Count > 3 ? $" +{routes.Count - 3}" : ""),
                Badge = found is null ? "not in the library" : found.HasSkeleton ? $"{found.BoneCount} bones" : "no bones",
                IsWarning = found is null || differs,
                IsCurrent = isCurrent,
                Mesh = found,
                ToolTip = found is null
                    ? $"{meshName} is named by the tables but the library has no such file, so it cannot be previewed."
                    : (differs
                        ? $"{meshName} has {found.BoneCount} bones but this clip has {bones}: the game matches bones by index and would play it wrong there. "
                        : $"{meshName} ({found.BoneCount} bones, {found.Location.DisplayLocation}). ")
                      + "Double-click (or Enter) to preview the clip on it." + (routes.Count > 0 ? " Played through: " + string.Join(", ", routes) + "." : ""),
            });
        }
        if (meshes.Children.Count > 0) Roots.Add(meshes);

        string tables = string.Join(" and ", uses.Select(u => u.Table).Distinct(StringComparer.OrdinalIgnoreCase));
        Summary = $"{name}: {uses.Count} {Plural(uses.Count, "line", "lines")} in {tables}, on {meshNames.Count} {Plural(meshNames.Count, "mesh", "meshes")}"
            + (mismatched > 0 ? $" ({mismatched} with a different bone count)" : "");
        EmptyText = string.Empty;
    }

    private void BuildForMesh(MeshDocumentViewModel mesh, ClipUsageIndex usage, LibrarySnapshot library)
    {
        string name = mesh.DisplayName;
        if (!mesh.HasSkeleton)
        {
            Summary = string.Empty;
            EmptyText = $"{name} has no bones, so it plays no clips: the tables give clips only to character meshes (.v3c).";
            return;
        }
        int bones = mesh.Scene.Skeleton.Count;
        var lists = usage.ClipListsForMesh(name);
        if (lists.Count == 0)
        {
            Summary = string.Empty;
            EmptyText = $"No table gives {name} any clips — the game plays clips on a mesh only through the entity classes (entity.tbl, pc_multi.tbl) "
                + "and weapons (weapons.tbl, fpgun.tbl) that use it. Preview clips still play on it here.";
            return;
        }
        int total = 0, mismatched = 0;
        foreach (var list in lists)
        {
            var group = new TableUsageNode(TableUsageNodeKind.Group,
                list.Character is { } character ? $"{character} — {list.ClassName}" : list.ClassName)
            {
                Glyph = list.Table == ClipUsageIndex.EntityTable || list.Table == ClipUsageIndex.MultiTable ? "" : "",
                Subtitle = $"{list.Relation} · {list.Table} · {list.Clips.Length} {Plural(list.Clips.Length, "clip", "clips")}",
                GroupUsages = list.Clips,
                ToolTip = $"{list.Table}: {list.Relation}. Copy table line on this row copies the whole list.",
            };
            foreach (var u in list.Clips)
            {
                var found = library.FindClip(u.ClipBaseName);
                bool differs = found?.BoneCount is { } b && b != bones;
                if (differs) mismatched++;
                total++;
                group.Children.Add(new TableUsageNode(TableUsageNodeKind.Clip, u.DiskName)
                {
                    Glyph = u.Kind == ClipUsageKind.State ? "" : "",
                    Subtitle = $"{KindWord(u.Kind)} \"{u.SlotName}\"{(u.WeaponBlock is null ? "" : $" · weapon {u.WeaponBlock}")} · line {u.Line}",
                    Badge = found is null ? "not in the library" : found.BoneCount is { } n ? $"{n} bones" : "unreadable",
                    IsWarning = found is null || differs,
                    Usage = u,
                    Clip = found,
                    ToolTip = found is null
                        ? $"{u.Table} line {u.Line} names {u.Clip.Original}, but no searched location has {u.DiskName}: the game would not find it."
                        : (differs ? $"{u.DiskName} has {found.BoneCount} bones but {name} has {bones}: it would play wrong on this mesh. " : $"{u.DiskName} ({found.Location.DisplayLocation}). ")
                          + "Double-click (or Enter) to open it; the context menu can also preview it on this mesh.",
                });
            }
            Roots.Add(group);
        }
        Summary = $"{name}: {lists.Count} {Plural(lists.Count, "class gives", "classes give")} it {total} table {Plural(total, "line", "lines")}"
            + (mismatched > 0 ? $" ({mismatched} naming a clip with another bone count)" : "");
        EmptyText = string.Empty;
    }

    /// <summary>The empty state for a clip no table names.</summary>
    internal static string UnusedClipText(string name) =>
        $"No table plays {name} — the game only plays clips that a table names (an entity.tbl or weapons.tbl "
        + "+State: or +Action: line), so as it stands this clip is never used. Copy table line gives you a line to add to a class.";

    private static string KindWord(ClipUsageKind kind) => kind == ClipUsageKind.State ? "state" : "action";

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;

    private static string Key(TableUsageNode node) => $"{node.Kind}|{node.Title}|{node.Subtitle}";

    private static IEnumerable<TableUsageNode> Flatten(TableUsageNode node) => node.Children.SelectMany(Flatten).Prepend(node);

    // ── Actions ──────────────────────────────────────────────────────────────

    /// <summary>The primary action on a row: preview the clip on a mesh, or open a clip.</summary>
    public void Activate(TableUsageNode? node)
    {
        if (node is null) return;
        switch (node.Kind)
        {
            case TableUsageNodeKind.Mesh when _document is ClipDocumentViewModel clip:
                if (node.Mesh is { HasSkeleton: true } mesh)
                {
                    clip.UsePreviewMesh(mesh);
                    clip.ShowStatus($"Previewing {clip.DisplayName} on {mesh.Name}.");
                }
                else
                {
                    clip.ShowStatus($"{node.Title} is not in the library, so it cannot be previewed.");
                }
                break;
            case TableUsageNodeKind.Clip:
                if (node.Clip is { } found) _shell.OpenLocation(found.Location, found.Name);
                else _document.ShowStatus($"{node.Title} is not in the library, so it cannot be opened.");
                break;
        }
    }

    /// <summary>Plays a clip row's clip on the mesh document.</summary>
    public void PreviewClip(TableUsageNode? node)
    {
        if (node?.Clip is not { } found || _document is not MeshDocumentViewModel mesh) return;
        mesh.UsePreviewClip(found);
        mesh.ShowStatus($"Previewing {found.Name} on {mesh.DisplayName}.");
    }

    /// <summary>
    /// The text Copy puts on the clipboard for <paramref name="node"/> (or the current selection): a
    /// line's own <see cref="TblSnippet.Line"/>, a group's <see cref="TblSnippet.Block"/>; for a clip
    /// document without a selection, every line naming it, or a new <c>+State:</c> line when none does.
    /// Null when there is nothing to copy.
    /// </summary>
    public string? BuildCopyText(TableUsageNode? node)
    {
        try
        {
            if (node?.Usage is { } u) return TblSnippet.Line(u.Kind, u.SlotName, ClipNameFor(u), u.Sound, _style);
            if (node is { GroupUsages.Count: > 0 } group) return TblSnippet.Block(group.GroupUsages.Select(ToEntry), _style);
            if (_document is ClipDocumentViewModel clip)
            {
                var uses = _shell.Assets.Usage.UsagesOf(clip.DisplayName);
                if (uses.Count > 0) return TblSnippet.Block(uses.Select(ToEntry), _style);
                string slot = ClipUsageIndex.ClipKey(clip.DisplayName);
                return TblSnippet.Line(ClipUsageKind.State, slot, clip.DisplayName, null, _style);
            }
            return null;
        }
        catch (ArgumentException ex)
        {
            _document.ShowStatus("That line cannot be written to a table: " + ex.Message);
            return null;
        }

        TblSnippetEntry ToEntry(ClipUsage u) => new(u.Kind, u.SlotName, ClipNameFor(u), u.Sound, u.WeaponBlock);
    }

    // The clip document's own name wins for its lines (a renamed copy copies its new name).
    private string ClipNameFor(ClipUsage u) =>
        _document is ClipDocumentViewModel clip ? clip.DisplayName : u.Clip.Original;

    private void Copy(TableUsageNode? node)
    {
        string? text = BuildCopyText(node);
        if (text is null) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            _document.ShowStatus("The clipboard is busy (another program has it open); try again. " + ex.Message);
            return;
        }
        int lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        _document.ShowStatus($"Copied {lines} {Plural(lines, "table line", "table lines")} ({(_style == TblSnippetStyle.Entity ? "entity.tbl" : "weapons.tbl")} layout) to the clipboard.");
    }

    // ── Following the shell ──────────────────────────────────────────────────

    private void OnAssetsChanged(object? sender, EventArgs e) => Refresh();

    private void OnProgressChanged(object? sender, EventArgs e)
    {
        // Only the "reading the tables" empty state depends on progress.
        if (Roots.Count == 0) Refresh();
    }

    private void OnPreviewChanged(object? sender, EventArgs e) => Refresh();

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.DisplayName) or nameof(MeshDocumentViewModel.HasSkeleton)) Refresh();
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_shell.Documents.Contains(_document)) return;
        _detached = true;
        _shell.Assets.LibraryChanged -= OnAssetsChanged;
        _shell.Assets.UsageChanged -= OnAssetsChanged;
        _shell.Assets.ProgressChanged -= OnProgressChanged;
        _shell.Documents.CollectionChanged -= OnDocumentsChanged;
        _document.PropertyChanged -= OnDocumentPropertyChanged;
        if (_document is ClipDocumentViewModel clip) clip.PreviewSkeletonChanged -= OnPreviewChanged;
        Instances.Remove(_document);
    }
}
