using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>A node of the library's Meshes tree.</summary>
public class LibraryNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;
    private Func<IEnumerable<LibraryNode>>? _loadChildren;

    private readonly string _toolTip;

    public LibraryNode(string header, string detail, string glyph, string toolTip, object? item = null)
    {
        Header = header;
        Detail = detail;
        Glyph = glyph;
        _toolTip = toolTip;
        Item = item;
    }

    public string Header { get; }

    /// <summary>The badge text on the right ("24 bones", "state stand").</summary>
    public string Detail { get; }

    public string Glyph { get; }

    /// <summary>The node's facts, plus what a double-click on it would do now (clip and mesh nodes).</summary>
    public string ToolTip => Item is not null && LibraryViewModel.ActivationHint is { } hint ? _toolTip + "\n\n" + hint(Item) : _toolTip;

    /// <summary>The double-click hint depends on the active document: re-read the tooltip.</summary>
    internal void RefreshToolTip()
    {
        if (Item is not null) Raise(nameof(ToolTip));
        foreach (var child in Children) child.RefreshToolTip();
    }

    /// <summary>The <see cref="LibraryClip"/> or <see cref="LibraryMesh"/> the node stands for, or null for a group.</summary>
    public object? Item { get; }

    /// <summary>True for a node whose item is missing (a table clip no searched location has).</summary>
    public bool IsMissing { get; init; }

    public ObservableCollection<LibraryNode> Children { get; } = [];

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value) || !value || _loadChildren is null) return;
            var load = _loadChildren;
            _loadChildren = null;
            Children.Clear();
            foreach (var child in load()) Children.Add(child);
        }
    }

    public string AutomationName => Detail.Length > 0 ? $"{Header}, {Detail}" : Header;

    /// <summary>The context menu's preview caption for this node's item.</summary>
    public string PreviewHeader => Item is LibraryMesh ? "_Preview on this mesh" : "_Preview this clip";

    /// <summary>True for a readable character mesh (the context menu offers New Clip for This Mesh…).</summary>
    public bool IsCharacterMesh => ClipCreation.NewClipCommands.CanCreateFor(Item);

    /// <summary>Gives the node children that are built the first time it is expanded.</summary>
    public void SetLazyChildren(Func<IEnumerable<LibraryNode>> load)
    {
        _loadChildren = load;
        Children.Add(new LibraryNode("Loading…", "", "", ""));
    }
}

/// <summary>One row of the library's Clips list.</summary>
public sealed class LibraryClipRow : ObservableObject
{
    private readonly string _toolTip;

    public LibraryClipRow(LibraryClip clip)
    {
        Clip = clip;
        var f = clip.Facts;
        DurationText = f is null ? "?" : (f.Duration / (double)RfaClip.TicksPerFrame).ToString("0.#", CultureInfo.CurrentCulture) + " f";
        BonesText = f is null ? "?" : f.BoneCount.ToString(CultureInfo.CurrentCulture);
        VersionText = f is null ? "" : "v" + f.Version.ToString(CultureInfo.InvariantCulture);
        HasMorph = f?.HasMorph == true;
        _toolTip = f is null
            ? $"{clip.Name}\n{clip.Location.DisplayLocation}\nCannot be read: {clip.Error}"
            : $"{clip.Name}\n{clip.Location.DisplayLocation}\n{f.BoneCount} bones · {TimeFormat.Duration(f.Duration)} · version {f.Version}"
              + (HasMorph ? $"\nMorph: {f.MorphVertexCount:N0} vertices, {f.MorphKeyframeCount} keyframes" : "");
    }

    public LibraryClip Clip { get; }

    public string Name => Clip.Name;

    public string DurationText { get; }

    public string BonesText { get; }

    public string VersionText { get; }

    public bool HasMorph { get; }

    public bool IsReadable => Clip.IsReadable;

    /// <summary>The clip's facts, plus what a double-click on it would do now.</summary>
    public string ToolTip => LibraryViewModel.ActivationHint is { } hint ? _toolTip + "\n\n" + hint(Clip) : _toolTip;

    /// <summary>The double-click hint depends on the active document: re-read the tooltip.</summary>
    internal void RefreshToolTip() => Raise(nameof(ToolTip));

    public string AutomationName => $"{Name}, {DurationText}, {BonesText} bones, {VersionText}{(HasMorph ? ", morph" : "")}";
}

/// <summary>
/// The Library panel (left, global; DESIGN.md "Library"): every clip and mesh the resolver can see,
/// built by <see cref="AssetLibrary"/> off the UI thread. The Meshes tab groups characters by skeleton
/// family (static meshes apart) and lists under each character the clips the tables give it; the Clips
/// tab is a virtualised list with a debounced, off-thread filter and a "compatible with the active
/// mesh" toggle.
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly RfaWorkspace _shell;
    private readonly DispatcherTimer _filterDebounce;
    private string _filter = string.Empty;
    private bool _compatibleOnly;
    private int _selectedTab;
    private IReadOnlyList<LibraryClipRow> _allClipRows = [];
    private IReadOnlyList<LibraryClipRow> _clipRows = [];
    private int _filterGeneration;
    private LibraryClipRow? _selectedClipRow;
    private LibraryNode? _selectedNode;

    public LibraryViewModel(RfaWorkspace shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _filterDebounce = new DispatcherTimer(DispatcherPriority.Background, shell.Dispatcher) { Interval = TimeSpan.FromMilliseconds(220) };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            ApplyFilter();
        };
        _selectedTab = shell.Settings.Get("rfa.libraryTab", 0);

        ActivationHint = item => DescribeActivation(item);
        OpenCommand = new RelayCommand(p => Open(p ?? SelectedItem), p => (p ?? SelectedItem) is LibraryClip or LibraryMesh);
        ActivateCommand = new RelayCommand(p => Activate(p ?? SelectedItem, newTab: false), p => (p ?? SelectedItem) is LibraryClip or LibraryMesh);
        PreviewCommand = new RelayCommand(p => Preview(p ?? SelectedItem), p => CanPreview(p ?? SelectedItem));
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p ?? SelectedItem), p => (p ?? SelectedItem) is LibraryClip or LibraryMesh);
        ExtractCommand = new RelayCommand(p => Extract(p ?? SelectedItem), p => (p ?? SelectedItem) is LibraryClip or LibraryMesh);
        CopyNameCommand = new RelayCommand(p => CopyName(p ?? SelectedItem), p => (p ?? SelectedItem) is LibraryClip or LibraryMesh);
        RetargetCommand = new RelayCommand(p => { if ((p ?? SelectedItem) is LibraryClip clip) _shell.Retarget.RetargetLibraryClip(clip); },
            p => (p ?? SelectedItem) is LibraryClip { IsReadable: true });
        NewClipCommand = new RelayCommand(p => { if ((p ?? SelectedItem) is LibraryMesh mesh) _shell.NewClips.ShowForLibraryMesh(mesh); },
            p => ClipCreation.NewClipCommands.CanCreateFor(p ?? SelectedItem));
        RefreshCommand = new RelayCommand(() => _shell.Assets.Refresh(), () => !_shell.Assets.IsLoading && _shell.Assets.HasSources);
        SetGameDirectoryCommand = new RelayCommand(_shell.PickGameDirectory);
        AddSearchFolderCommand = new RelayCommand(_shell.PickSearchFolder);
        ClearFilterCommand = new RelayCommand(() => Filter = string.Empty, () => _filter.Length > 0);

        _shell.Assets.LibraryChanged += (_, _) => Rebuild();
        _shell.Assets.UsageChanged += (_, _) => Rebuild();
        _shell.Assets.ProgressChanged += (_, _) =>
        {
            RaiseAll(nameof(IsLoading), nameof(ProgressText), nameof(ProgressFraction), nameof(HasProgressFraction),
                nameof(IsEmptyNoSources), nameof(IsEmptyNothingFound), nameof(HasContent));
            RefreshCommand.RaiseCanExecuteChanged();
        };
    }

    public ObservableCollection<LibraryNode> MeshRoots { get; } = [];

    /// <summary>The clips shown (after the filter and the compatibility toggle).</summary>
    public IReadOnlyList<LibraryClipRow> ClipRows
    {
        get => _clipRows;
        private set
        {
            _clipRows = value;
            Raise();
            Raise(nameof(ClipCountText));
        }
    }

    /// <summary>"1,234 clips" / "12 of 1,234 clips".</summary>
    public string ClipCountText => _clipRows.Count == _allClipRows.Count
        ? $"{_allClipRows.Count:N0} clips"
        : $"{_clipRows.Count:N0} of {_allClipRows.Count:N0} clips";

    /// <summary>The filter text (substring or wildcard), applied after a short pause in typing.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value ?? string.Empty)) return;
            ClearFilterCommand.RaiseCanExecuteChanged();
            _filterDebounce.Stop();
            _filterDebounce.Start();
        }
    }

    /// <summary>Show only clips whose bone count matches the active document's mesh.</summary>
    public bool CompatibleOnly
    {
        get => _compatibleOnly;
        set
        {
            if (!Set(ref _compatibleOnly, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>The bone count "compatible" means, from the active document (0 = none).</summary>
    public int ActiveBoneCount => _shell.ActiveDocument?.Scene.Skeleton.Count ?? 0;

    public string CompatibleToolTip => ActiveBoneCount > 0
        ? $"Show only clips with {ActiveBoneCount} bones (the active mesh's count)"
        : "Show only clips that fit the active document's mesh (open a mesh or a clip with a preview mesh first)";

    /// <summary>0 = Meshes, 1 = Clips (remembered).</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            _shell.Settings.Set("rfa.libraryTab", value);
        }
    }

    public LibraryClipRow? SelectedClipRow
    {
        get => _selectedClipRow;
        set
        {
            if (!Set(ref _selectedClipRow, value)) return;
            RaiseCommands();
        }
    }

    public LibraryNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value)) return;
            RaiseCommands();
        }
    }

    /// <summary>The item the commands act on when no parameter is given.</summary>
    public object? SelectedItem => _selectedTab == 1 ? _selectedClipRow?.Clip : _selectedNode?.Item;

    public bool IsLoading => _shell.Assets.IsLoading;

    public string ProgressText => _shell.Assets.ProgressText;

    public double ProgressFraction => _shell.Assets.ProgressFraction ?? 0;

    public bool HasProgressFraction => _shell.Assets.IsLoading && _shell.Assets.ProgressFraction is not null;

    /// <summary>Nowhere to look yet: show "Set game directory…" and "Add search folder…".</summary>
    public bool IsEmptyNoSources => !_shell.Assets.HasSources && !_shell.Assets.IsLoading;

    /// <summary>Looked, found nothing.</summary>
    public bool IsEmptyNothingFound => _shell.Assets.HasSources && !_shell.Assets.IsLoading
        && _shell.Assets.Snapshot.Clips.Length == 0 && _shell.Assets.Snapshot.Meshes.Length == 0;

    public bool HasContent => !IsEmptyNoSources && !IsEmptyNothingFound;

    /// <summary>Open in new tab (context menu, Ctrl+double-click, Ctrl+Enter, middle-click).</summary>
    public RelayCommand OpenCommand { get; }

    /// <summary>What double-click and Enter do (<see cref="Activate"/>).</summary>
    public RelayCommand ActivateCommand { get; }

    public RelayCommand PreviewCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ExtractCommand { get; }
    public RelayCommand CopyNameCommand { get; }

    /// <summary>Retarget… (a clip): opens the retarget dialog on it.</summary>
    public RelayCommand RetargetCommand { get; }

    /// <summary>New Clip for This Mesh… (a character mesh): opens File › New Clip… on it.</summary>
    public RelayCommand NewClipCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SetGameDirectoryCommand { get; }
    public RelayCommand AddSearchFolderCommand { get; }
    public RelayCommand ClearFilterCommand { get; }

    /// <summary>"Preview on this mesh" / "Preview this clip" depending on the item and the active tab.</summary>
    public string PreviewHeader(object? item) => item is LibraryMesh ? "Preview on this mesh" : "Preview this clip";

    /// <summary>Called when the active document changes (compatibility and preview targets change).</summary>
    public void OnActiveDocumentChanged()
    {
        RaiseAll(nameof(ActiveBoneCount), nameof(CompatibleToolTip), nameof(DoubleClickHint));
        RaiseCommands();
        RefreshToolTips();
        if (_compatibleOnly) ApplyFilter();
    }

    /// <summary>Called after Settings change the double-click choice.</summary>
    public void OnDoubleClickSettingChanged()
    {
        Raise(nameof(DoubleClickHint));
        RefreshToolTips();
    }

    private void RefreshToolTips()
    {
        foreach (var row in _allClipRows) row.RefreshToolTip();
        foreach (var node in MeshRoots) node.RefreshToolTip();
    }

    // ── Double-click: preview on the current document, or open ──────────────

    /// <summary>The settings key of the double-click choice: "preview" (default) or "newTab".</summary>
    public const string DoubleClickSettingKey = "rfa.libraryDoubleClick";

    /// <summary>The double-click hint for a clip or mesh, used by every row's tooltip (set by the live library).</summary>
    internal static Func<object, string>? ActivationHint { get; private set; }

    /// <summary>True when double-click previews on the active document (the default); false: always a new tab.</summary>
    public bool DoubleClickPreviews =>
        !string.Equals(_shell.Settings.Get<string>(DoubleClickSettingKey), "newTab", StringComparison.OrdinalIgnoreCase);

    private enum ActivationKind { NewTab, PlayOnMesh, PreviewOnMesh, NewTabOnSameMesh }

    /// <summary>What double-click on <paramref name="item"/> does now, and why when it falls back to a new tab.</summary>
    private (ActivationKind Kind, string? Reason) Plan(object item)
    {
        if (!DoubleClickPreviews) return (ActivationKind.NewTab, null);
        switch (_shell.ActiveDocument, item)
        {
            case (MeshDocumentViewModel { HasSkeleton: true } mesh, LibraryClip { IsReadable: true } clip):
                return clip.BoneCount == mesh.Scene.Skeleton.Count
                    ? (ActivationKind.PlayOnMesh, null)
                    : (ActivationKind.NewTab, $"{clip.Name} has {clip.BoneCount} bones and {mesh.DisplayName} has {mesh.Scene.Skeleton.Count}");
            case (ClipDocumentViewModel clipDoc, LibraryMesh { HasSkeleton: true } libraryMesh):
                return libraryMesh.BoneCount == clipDoc.Current.BoneCount
                    ? (ActivationKind.PreviewOnMesh, null)
                    : (ActivationKind.NewTab, $"{libraryMesh.Name} has {libraryMesh.BoneCount} bones and {clipDoc.DisplayName} has {clipDoc.Current.BoneCount}");
            case (ClipDocumentViewModel { PreviewMesh: not null }, LibraryClip):
                return (ActivationKind.NewTabOnSameMesh, null);
            default:
                return (ActivationKind.NewTab, null);
        }
    }

    /// <summary>"Double-click: play on ult2_guard.v3c · Ctrl+double-click: open in a new tab".</summary>
    private string DescribeActivation(object item)
    {
        const string newTab = "Ctrl+double-click or middle-click: open in a new tab";
        var (kind, reason) = Plan(item);
        var active = _shell.ActiveDocument;
        return kind switch
        {
            ActivationKind.PlayOnMesh => $"Double-click: play it on {active?.DisplayName}\n{newTab}",
            ActivationKind.PreviewOnMesh => $"Double-click: preview {active?.DisplayName} on this mesh\n{newTab}",
            ActivationKind.NewTabOnSameMesh when active is ClipDocumentViewModel c =>
                $"Double-click: open in a new tab, previewed on {c.Scene.MeshName} when its bone count fits",
            _ when reason is not null => $"Double-click: open in a new tab ({reason})",
            _ => "Double-click: open in a new tab",
        };
    }

    /// <summary>The library's hint line: what double-click does with the document now in front.</summary>
    public string DoubleClickHint
    {
        get
        {
            if (!DoubleClickPreviews)
                return "Double-click opens in a new tab (Tools › Settings › Library). Drag onto the viewport, or right-click › Preview, to preview instead.";
            return _shell.ActiveDocument switch
            {
                MeshDocumentViewModel { HasSkeleton: true } mesh =>
                    $"Double-click a clip to play it on {mesh.DisplayName}. Ctrl+double-click or middle-click opens a new tab.",
                ClipDocumentViewModel clip =>
                    $"Double-click a mesh to preview {clip.DisplayName} on it; a clip opens in a new tab on the same mesh. Ctrl+double-click always opens a new tab.",
                _ => "Double-click opens a clip or mesh in a new tab.",
            };
        }
    }

    /// <summary>
    /// Double-click / Enter: with a mesh in front, a clip that fits plays on it; with a clip in front, a mesh
    /// that fits becomes its preview mesh and another clip opens in a new tab on the same mesh; otherwise
    /// (or with <paramref name="newTab"/>, Ctrl, or the "always open" setting) the item opens in a new tab.
    /// A mismatched bone count opens a new tab and says why in the status bar.
    /// </summary>
    public void Activate(object? item, bool newTab)
    {
        if (item is not (LibraryClip or LibraryMesh)) return;
        if (newTab)
        {
            Open(item);
            return;
        }
        var (kind, reason) = Plan(item);
        var active = _shell.ActiveDocument;
        switch (kind)
        {
            case ActivationKind.PlayOnMesh when active is MeshDocumentViewModel mesh && item is LibraryClip clip:
                mesh.UsePreviewClip(clip, play: true);
                _shell.ShowShellStatus($"Playing {clip.Name} on {mesh.DisplayName}. Ctrl+double-click opens it in a new tab.");
                break;
            case ActivationKind.PreviewOnMesh when active is ClipDocumentViewModel clipDoc && item is LibraryMesh libraryMesh:
                clipDoc.UsePreviewMesh(libraryMesh);
                _shell.ShowShellStatus($"Previewing {clipDoc.DisplayName} on {libraryMesh.Name}. Ctrl+double-click opens the mesh in a new tab.");
                break;
            case ActivationKind.NewTabOnSameMesh when active is ClipDocumentViewModel source && item is LibraryClip:
                OpenOnSameMesh(item, source);
                break;
            default:
                Open(item);
                if (reason is not null) _shell.ShowShellStatus($"Opened in a new tab: {reason}, so it cannot preview there.");
                break;
        }
    }

    /// <summary>Opens a clip in a new tab previewed on <paramref name="source"/>'s mesh when the bone counts match.</summary>
    private void OpenOnSameMesh(object item, ClipDocumentViewModel source)
    {
        var mesh = source.PreviewMesh;
        string? meshName = source.PreviewLibraryMesh?.Name ?? source.Scene.MeshName;
        string? textureFolder = source.PreviewLibraryMesh?.Location.FilePath is { } p ? Path.GetDirectoryName(p) : source.Folder;
        Open(item, document =>
        {
            if (document is ClipDocumentViewModel opened && mesh is not null && !string.IsNullOrWhiteSpace(meshName)
                && mesh.Bones.Length == opened.Current.BoneCount)
            {
                opened.UsePreviewMesh(mesh, meshName, textureFolder);
            }
        });
    }

    private void RaiseCommands()
    {
        OpenCommand.RaiseCanExecuteChanged();
        ActivateCommand.RaiseCanExecuteChanged();
        PreviewCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        ExtractCommand.RaiseCanExecuteChanged();
        CopyNameCommand.RaiseCanExecuteChanged();
    }

    // ── Building ─────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        var snapshot = _shell.Assets.Snapshot;
        _allClipRows = [.. snapshot.Clips.Select(c => new LibraryClipRow(c))];
        ApplyFilter();
        RaiseAll(nameof(IsEmptyNoSources), nameof(IsEmptyNothingFound), nameof(HasContent));
    }

    private void ApplyFilter()
    {
        int generation = ++_filterGeneration;
        string filter = _filter;
        int bones = _compatibleOnly ? ActiveBoneCount : 0;
        bool compatibleOnly = _compatibleOnly;
        var rows = _allClipRows;
        var snapshot = _shell.Assets.Snapshot;
        var usage = _shell.Assets.Usage;
        var busy = BusyTracker.Begin("library filter");
        _ = Task.Run(() =>
        {
            var clips = rows.Where(r => LibrarySnapshot.Matches(r.Name, filter)
                && (!compatibleOnly || (bones > 0 && r.Clip.BoneCount == bones))).ToList();
            var meshes = BuildMeshTree(snapshot, usage, filter);
            return (clips, meshes);
        }).ContinueWith(task => _shell.Dispatcher.BeginInvoke(new Action(() =>
        {
            busy.Dispose();
            if (generation != _filterGeneration || !task.IsCompletedSuccessfully) return;
            ClipRows = task.Result.clips;
            MeshRoots.Clear();
            foreach (var node in task.Result.meshes) MeshRoots.Add(node);
        })), TaskScheduler.Default);
    }

    /// <summary>Builds the Meshes tree (off the UI thread; nodes are plain objects until bound).</summary>
    private static List<LibraryNode> BuildMeshTree(LibrarySnapshot snapshot, ClipUsageIndex usage, string filter)
    {
        var roots = new List<LibraryNode>();
        bool filtering = !string.IsNullOrWhiteSpace(filter);
        foreach (var family in snapshot.Families.OrderByDescending(f => f.Members.Length).ThenBy(f => f.Members[0], StringComparer.OrdinalIgnoreCase))
        {
            var members = family.Members.Where(m => LibrarySnapshot.Matches(m, filter)).Select(snapshot.FindMesh).OfType<LibraryMesh>().ToList();
            if (members.Count == 0) continue;
            string title = FamilyTitle(family);
            var node = new LibraryNode(title, Bones(family.BoneCount),"",
                family.Members.Length == 1
                    ? $"No other mesh shares this skeleton ({family.BoneCount} bones)."
                    : $"{family.Members.Length} meshes share this skeleton ({family.BoneCount} bones); any clip made for one plays on all of them.")
            {
                IsExpanded = filtering,
            };
            foreach (var mesh in members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)) node.Children.Add(MeshNode(mesh, usage, snapshot));
            roots.Add(node);
        }

        var statics = snapshot.StaticMeshes.Where(m => LibrarySnapshot.Matches(m.Name, filter)).ToList();
        if (statics.Count > 0)
        {
            var group = new LibraryNode("Static meshes", $"{statics.Count:N0}", "",
                "Meshes without bones (.v3m, and any mesh that could not be read). They open read-only.")
            {
                IsExpanded = filtering && statics.Count <= 200,
            };
            foreach (var mesh in statics) group.Children.Add(MeshNode(mesh, usage, snapshot));
            roots.Add(group);
        }
        return roots;
    }

    private static string Bones(int count) => count == 1 ? "1 bone" : $"{count} bones";

    private static string FamilyTitle(Cairn.Rfa.Animation.SkeletonFamily family)
    {
        // Name a family after its shortest member, which is usually the base character.
        string shortest = family.Members.OrderBy(m => m.Length).ThenBy(m => m, StringComparer.OrdinalIgnoreCase).First();
        string stem = Path.GetFileNameWithoutExtension(shortest);
        return family.Members.Length == 1 ? stem : $"{stem} family ({family.Members.Length})";
    }

    private static LibraryNode MeshNode(LibraryMesh mesh, ClipUsageIndex usage, LibrarySnapshot snapshot)
    {
        string detail = mesh.IsReadable ? (mesh.HasSkeleton ? Bones(mesh.BoneCount) : "static") : "unreadable";
        string tip = $"{mesh.Name}\n{mesh.Location.DisplayLocation}" + (mesh.Error is { } e ? $"\nCannot be read: {e}" : "");
        var node = new LibraryNode(mesh.Name, detail, mesh.HasSkeleton ? "" : "", tip, mesh) { IsMissing = !mesh.IsReadable };
        if (mesh.HasSkeleton && usage.ClipListsForMesh(mesh.Name).Count > 0)
        {
            node.SetLazyChildren(() => TableClips(mesh, usage, snapshot));
        }
        return node;
    }

    private static IEnumerable<LibraryNode> TableClips(LibraryMesh mesh, ClipUsageIndex usage, LibrarySnapshot snapshot)
    {
        foreach (var list in usage.ClipListsForMesh(mesh.Name))
        {
            var group = new LibraryNode($"{list.ClassName} ({list.Table})", $"{list.Clips.Length} clips", "",
                $"{list.Relation}: the clips {list.Table} gives {list.ClassName}");
            foreach (var u in list.Clips)
            {
                var clip = snapshot.FindClip(u.ClipBaseName);
                string slot = (u.Kind == ClipUsageKind.State ? "state " : "action ") + u.SlotName
                    + (u.WeaponBlock is { } w ? $" ({w})" : "");
                group.Children.Add(new LibraryNode(u.DiskName, slot, "",
                    clip is null ? $"{u.DiskName} is named by {list.Table} line {u.Line} but no searched location has it." : $"{clip.Name}\n{clip.Location.DisplayLocation}\n{u.Describe()}",
                    clip) { IsMissing = clip is null });
            }
            yield return group;
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private void Open(object? item, Action<DocumentViewModel>? opened = null)
    {
        switch (item)
        {
            case LibraryClip clip: _shell.OpenLocation(clip.Location, clip.Name, opened); break;
            case LibraryMesh mesh: _shell.OpenLocation(mesh.Location, mesh.Name, opened); break;
        }
    }

    private bool CanPreview(object? item) => item switch
    {
        LibraryMesh mesh => mesh.HasSkeleton && _shell.ActiveDocument is ClipDocumentViewModel,
        LibraryClip clip => clip.IsReadable && _shell.ActiveDocument is MeshDocumentViewModel { HasSkeleton: true },
        _ => false,
    };

    private void Preview(object? item)
    {
        switch (item)
        {
            case LibraryMesh mesh when _shell.ActiveDocument is ClipDocumentViewModel clipDocument:
                clipDocument.UsePreviewMesh(mesh);
                break;
            case LibraryClip clip when _shell.ActiveDocument is MeshDocumentViewModel meshDocument:
                meshDocument.UsePreviewClip(clip);
                break;
        }
    }

    private static AssetLocation? LocationOf(object? item) => item switch
    {
        LibraryClip c => c.Location,
        LibraryMesh m => m.Location,
        _ => null,
    };

    private void OpenFolder(object? item)
    {
        if (LocationOf(item) is not { } location) return;
        string? path = location.FilePath ?? location.ArchivePath;
        if (path is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            _shell.Dialogs.ShowError("The folder could not be opened.", path, ex.Message);
        }
    }

    private void Extract(object? item)
    {
        if (LocationOf(item) is not { } location) return;
        // An archive entry's name is untrusted: only its file name part is used, so a hostile .vpp entry such
        // as "..\..\x.rfa" cannot write outside the chosen folder.
        string name = Path.GetFileName(location.ResolvedName.Replace('/', '\\'));
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _shell.Dialogs.ShowError("That entry cannot be extracted.", $"'{location.ResolvedName}' is not a usable file name.");
            return;
        }
        string? folder = _shell.Dialogs.PickFolder(_shell.DefaultOutputFolder(), $"Extract {name} to…");
        if (folder is null) return;
        string target = Path.Combine(folder, name);
        if (File.Exists(target) && !_shell.Dialogs.Confirm($"'{name}' is already in that folder.", "Replace it with the copy from the library?", "_Replace"))
            return;
        var busy = BusyTracker.Begin("extract " + name);
        _ = Task.Run(() =>
        {
            byte[] bytes = location.ReadAllBytes();
            Cairn.Workspace.AtomicFile.WriteAllBytes(target, bytes);
        }).ContinueWith(task => _shell.Dispatcher.BeginInvoke(new Action(() =>
        {
            busy.Dispose();
            if (task.IsCompletedSuccessfully)
            {
                if (!_shell.IsGameDirectory(folder)) _shell.Settings.LastSaveFolder = folder;
                _shell.ShowShellStatus($"Extracted {name} to {folder}.");
            }
            else
            {
                _shell.Dialogs.ShowError($"{name} could not be extracted.", target, task.Exception?.InnerException?.Message);
            }
        })), TaskScheduler.Default);
    }

    private void CopyName(object? item)
    {
        string? name = item switch { LibraryClip c => c.Name, LibraryMesh m => m.Name, _ => null };
        if (name is null) return;
        try
        {
            Clipboard.SetText(name);
            _shell.ShowShellStatus($"Copied '{name}'.");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ErrorLog.Write("clipboard", ex);
        }
    }
}
