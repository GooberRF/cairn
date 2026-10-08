using System.Windows;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;

namespace Cairn.Shell.Dialogs;

/// <summary>One extension on the File associations page: who opens it now, and whether Cairn should.</summary>
public sealed class AssociationRow : ObservableObject
{
    private bool _openWithCairn;

    public AssociationRow(FileAssociation.Kind kind, string group, string description)
    {
        Kind = kind;
        Group = group;
        Description = description;
    }

    public FileAssociation.Kind Kind { get; }
    public string Extension => Kind.Extension;
    /// <summary>The module's name; the page groups rows by it.</summary>
    public string Group { get; }
    /// <summary>The document kind's (or importer's) name.</summary>
    public string Description { get; }
    /// <summary>Who opens the extension now.</summary>
    public AssociationOwner Owner { get; private set; } = new(null, "None", OwnerSource.None, false);
    /// <summary>True when this copy of Cairn was registered for the extension when the page was (re)loaded.</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>The check box: applying registers or removes Cairn when this differs from <see cref="IsRegistered"/>.</summary>
    public bool OpenWithCairn
    {
        get => _openWithCairn;
        set { if (Set(ref _openWithCairn, value)) RaiseAll(nameof(IsChanged), nameof(CanChooseDefault), nameof(Note), nameof(HasNote)); }
    }

    public bool IsChanged => OpenWithCairn != IsRegistered;

    /// <summary>True when the user picked another program as the default in Windows: Cairn must not override it.</summary>
    public bool IsProtected => Owner.Source == OwnerSource.UserChoice && !Owner.IsCairn;

    /// <summary>True when "Choose default..." is offered: Cairn is (to be) registered but a protected default wins.</summary>
    public bool CanChooseDefault => IsProtected && OpenWithCairn;

    public string OwnerText => Owner.Name;

    public string OwnerTip => Owner.Source switch
    {
        OwnerSource.UserChoice => $"{Owner.Name}: a default you picked in Windows ({Owner.ProgId})",
        OwnerSource.UserClasses => $"{Owner.Name}: the default for your Windows account ({Owner.ProgId})",
        OwnerSource.MachineClasses => $"{Owner.Name}: the default for every account on this computer ({Owner.ProgId})",
        _ => $"No program is registered for {Extension} files",
    };

    public string CheckTip => AssociationsModel.IsGeneralFormat(Extension)
        ? $"Open {Extension} files with this copy of Cairn (a general format: Select all leaves it unticked)"
        : $"Open {Extension} files with this copy of Cairn";

    public string ChooseTip => $"Open Windows' chooser to pick the default program for {Extension} files";

    /// <summary>Why the row will not simply switch to Cairn, and what to do about it.</summary>
    public string? Note => !CanChooseDefault ? null : IsRegistered
        ? $"Cairn is in \"Open with\". {Owner.Name} stays the default because you chose it in Windows."
        : $"You chose {Owner.Name} in Windows, so Cairn will only be added to \"Open with\".";

    public bool HasNote => Note is not null;

    /// <summary>Takes the state just read; a pending (changed) check box is kept when <paramref name="keepPending"/>.</summary>
    internal void Update(AssociationOwner owner, bool registered, bool keepPending)
    {
        bool pending = keepPending && IsChanged;
        Owner = owner;
        IsRegistered = registered;
        if (!pending) _openWithCairn = registered;
        RaiseAll(nameof(Owner), nameof(OwnerText), nameof(OwnerTip), nameof(IsRegistered), nameof(OpenWithCairn), nameof(IsChanged),
            nameof(IsProtected), nameof(CanChooseDefault), nameof(Note), nameof(HasNote));
    }
}

/// <summary>
/// The File associations settings page: one row per extension the modules open (plus importer extensions
/// that are game formats), grouped by module. Nothing is written until <see cref="Apply"/> (the dialog's
/// OK), and then only for rows whose check box changed. The two buttons that act at once ("Choose default"
/// and removing the old apps' associations) are explicit, and the latter asks first.
/// </summary>
public sealed class AssociationsModel : ObservableObject
{
    /// <summary>The page title (and the id <see cref="Cairn.Ui.Modules.IShellContext.ShowSettings"/> takes).</summary>
    public const string PageTitle = "File associations";

    /// <summary>Importer extensions that are general formats other programs own: not offered.</summary>
    private static readonly HashSet<string> GeneralFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gltf", ".glb", ".obj", ".fbx", ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".wav", ".ogg", ".mp3", ".txt",
        ".aif", ".aiff", ".aifc",
    };

    /// <summary>True for a general format other programs usually own (.wav, .ogg...): listed when a kind opens it, but "Select all" leaves it.</summary>
    public static bool IsGeneralFormat(string extension) => GeneralFormats.Contains(extension);

    private readonly IAssociationStore _store;
    private readonly IDialogService _dialogs;
    private readonly Action<string>? _status;
    private readonly AssociationRegistry? _registry;
    private bool _hasOldApps;
    private string? _problem;

    public AssociationsModel(IAssociationStore store, IEnumerable<IModule> modules, IDialogService dialogs, Action<string>? status = null)
    {
        _store = store;
        _dialogs = dialogs;
        _status = status;
        Rows = RowsFor(modules);
        if (store.ExecutablePath is { } exe) _registry = new AssociationRegistry(store, [.. Rows.Select(r => r.Kind)], exe);
        else _problem = "Cairn could not work out where it is running from, so it cannot register file types.";

        // general formats (.wav, .ogg) are left as they are: they usually belong to a media player
        SelectAllCommand = new RelayCommand(() => { foreach (var row in Rows) if (!IsGeneralFormat(row.Extension)) row.OpenWithCairn = true; }, () => IsAvailable);
        SelectNoneCommand = new RelayCommand(() => { foreach (var row in Rows) row.OpenWithCairn = false; }, () => IsAvailable);
        ChooseDefaultCommand = new RelayCommand(p => { if (p is AssociationRow row) ChooseDefault(row); }, _ => IsAvailable);
        RemoveOldAppsCommand = new RelayCommand(RemoveOldApps, () => IsAvailable && _hasOldApps);
    }

    public IReadOnlyList<AssociationRow> Rows { get; }
    public bool IsAvailable => _registry is not null;
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand ChooseDefaultCommand { get; }
    public RelayCommand RemoveOldAppsCommand { get; }
    /// <summary>The window the Windows chooser is modal over (set by the view).</summary>
    public Func<Window?>? Owner { get; set; }

    public bool HasOldApps
    {
        get => _hasOldApps;
        private set { if (Set(ref _hasOldApps, value)) RemoveOldAppsCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>Why the page cannot work, or null.</summary>
    public string? Problem
    {
        get => _problem;
        private set { if (Set(ref _problem, value)) Raise(nameof(HasProblem)); }
    }

    public bool HasProblem => _problem is not null;

    public IEnumerable<AssociationRow> ChangedRows => Rows.Where(r => r.IsChanged);

    /// <summary>
    /// The rows for <paramref name="modules"/>, in module order: every extension of every document kind
    /// (the first kind claiming an extension wins), then importer extensions no kind opens, except
    /// general formats (glTF, images, audio) that belong to other programs.
    /// </summary>
    public static IReadOnlyList<AssociationRow> RowsFor(IEnumerable<IModule> modules)
    {
        var list = modules.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kindExtensions = list.SelectMany(m => m.DocumentKinds).SelectMany(k => k.Extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<AssociationRow>();
        foreach (var module in list)
        {
            foreach (var kind in module.DocumentKinds)
                foreach (var extension in kind.Extensions)
                    if (seen.Add(extension))
                        rows.Add(new(FileAssociation.Kind.For(kind.Id, extension, kind.AssociationDescription), module.DisplayName, kind.DisplayName));
            foreach (var importer in module.Importers)
                foreach (var extension in importer.Extensions)
                    if (!kindExtensions.Contains(extension) && !GeneralFormats.Contains(extension) && seen.Add(extension))
                        rows.Add(new(FileAssociation.Kind.For("import" + extension.ToLowerInvariant(), extension, FilterLabel(importer)),
                            module.DisplayName, importer.DisplayName));
        }
        return rows;
    }

    /// <summary>"Volition bitmaps (*.vbm)|*.vbm" -> "Volition bitmaps"; the importer's name when the filter has no label.</summary>
    private static string FilterLabel(IFileImporter importer)
    {
        var label = importer.FileFilter.Split('|')[0];
        int paren = label.IndexOf('(');
        label = (paren > 0 ? label[..paren] : label).Trim();
        return label.Length > 0 ? label : importer.DisplayName;
    }

    /// <summary>Re-reads every row (owner, registration). Pending check boxes survive.</summary>
    public void Refresh(bool keepPending = true)
    {
        if (_registry is null) return;
        try
        {
            foreach (var row in Rows) row.Update(_registry.Owner(row.Kind), _registry.IsRegistered(row.Kind), keepPending);
            HasOldApps = _registry.HasOldApps();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Problem = "Could not read the file associations: " + ex.Message;
        }
    }

    /// <summary>
    /// Registers or removes Cairn for the changed rows only. A row whose default the user picked in Windows
    /// gets the ProgID and the "Open with" entry but not the default. Returns null, or what went wrong.
    /// </summary>
    public string? Apply()
    {
        if (_registry is null) return null;
        var changed = ChangedRows.ToList();
        if (changed.Count == 0) return null;
        var errors = new List<string>();
        void Run(IReadOnlyList<FileAssociation.Kind> kinds, Func<IReadOnlyList<FileAssociation.Kind>, string?> change)
        {
            if (kinds.Count > 0 && change(kinds) is { } error) errors.Add(error);
        }
        Run([.. changed.Where(r => r.OpenWithCairn && !r.IsProtected).Select(r => r.Kind)], k => _registry.Register(k));
        Run([.. changed.Where(r => r.OpenWithCairn && r.IsProtected).Select(r => r.Kind)], k => _registry.Register(k, setDefault: false));
        Run([.. changed.Where(r => !r.OpenWithCairn).Select(r => r.Kind)], _registry.Remove);
        Refresh(keepPending: false);
        return errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Distinct());
    }

    /// <summary>The dialog's OK: <see cref="Apply"/>, reporting the outcome.</summary>
    public void Commit()
    {
        int count = ChangedRows.Count();
        if (Apply() is { } error) _dialogs.ShowError("Could not change file associations", error);
        else if (count > 0) _status?.Invoke(count == 1 ? "File association updated" : $"{count} file associations updated");
    }

    /// <summary>
    /// "Choose default...": makes sure Cairn is in the extension's "Open with" list (so the chooser offers it),
    /// then opens Windows' chooser, then re-reads the rows.
    /// </summary>
    public void ChooseDefault(AssociationRow row)
    {
        if (_registry is null) return;
        if (!_registry.IsRegistered(row.Kind) && _registry.Register([row.Kind], setDefault: false) is { } error)
        {
            _dialogs.ShowError("Could not change file associations", error);
            return;
        }
        if (!_store.ShowDefaultChooser(Owner?.Invoke(), row.Extension))
            _dialogs.ShowError("Could not open Windows' chooser", $"Choose the default program for {row.Extension} files in Windows Settings > Apps > Default apps.");
        Refresh();
    }

    private void RemoveOldApps()
    {
        if (_registry is null) return;
        var list = string.Join("\n", AssociationRegistry.OldAppProgIds.Select(o => $"{o.ProgId} ({o.Extension})"));
        if (!_dialogs.Confirm("Remove old associations",
                $"Remove the file associations RFA Workbench and ATX Workbench made for your Windows account?\n{list}\n\nExtensions that now open with another program are left alone.",
                "Remove")) return;
        if (_registry.RemoveOldApps() is { } error) _dialogs.ShowError("Could not change file associations", error);
        else _status?.Invoke("Old RFA/ATX Workbench associations removed");
        Refresh();
    }
}

/// <summary>The shell's "File associations" settings page (shown after General).</summary>
public sealed class AssociationsPage(AssociationsModel model) : ISettingsPage
{
    private AssociationsPageView? _view;

    public AssociationsModel Model { get; } = model;
    public string Title => AssociationsModel.PageTitle;
    public FrameworkElement View => _view ??= new AssociationsPageView(Model);
    public void Load() => Model.Refresh(keepPending: false);
    public void Commit() => Model.Commit();
}
