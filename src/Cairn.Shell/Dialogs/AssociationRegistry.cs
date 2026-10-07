using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Microsoft.Win32;

namespace Cairn.Shell.Dialogs;

/// <summary>Where one extension stands: opens with this Cairn, another Cairn, another program, or nothing per user.</summary>
public enum AssociationState { NotRegistered, Cairn, OtherCairn, OtherApp }

/// <summary>An extension's <see cref="AssociationState"/>, plus the other program's ProgID (and its type name) when known.</summary>
public sealed record AssociationStatus(AssociationState State, string? OtherProgId = null, string? OtherName = null);

/// <summary>Where Windows takes an extension's program from.</summary>
public enum OwnerSource
{
    /// <summary>No program is registered.</summary>
    None,
    /// <summary>A default the user picked in Windows' own chooser (protected: apps cannot change it).</summary>
    UserChoice,
    /// <summary>The per-user default (<c>HKCU\Software\Classes</c>).</summary>
    UserClasses,
    /// <summary>The machine-wide default (<c>HKLM\Software\Classes</c>).</summary>
    MachineClasses,
}

/// <summary>The program that opens an extension now, as Windows resolves it.</summary>
/// <param name="ProgId">The ProgID in effect, or null when none.</param>
/// <param name="Name">Friendly program name ("Notepad++"), "Cairn", or "None".</param>
/// <param name="Source">Where the ProgID came from.</param>
/// <param name="IsCairn">True when it is this copy of Cairn.</param>
public sealed record AssociationOwner(string? ProgId, string Name, OwnerSource Source, bool IsCairn);

/// <summary>
/// Queries, registers and removes Cairn's per-user associations through an <see cref="IAssociationStore"/>
/// (the real registry, or the self-tests' in-memory fake).
///
/// Registration writes the ProgID (<c>Cairn.&lt;kindId&gt;</c>: type name, icon, <c>"exe" "%1"</c> command),
/// lists it in the extension's <c>OpenWithProgids</c> (so Cairn is always in "Open with") and, unless the
/// caller says otherwise, makes it the extension's per-user default, parking the previous default on the
/// ProgID. A default the user picked in Windows' chooser (<c>UserChoice</c>) is never written: Windows
/// protects it. Removal is per extension and only touches values that point at our ProgID, and a ProgID
/// key goes once no extension still uses it.
/// </summary>
public sealed class AssociationRegistry
{
    /// <summary>The per-user (and machine) Classes root, relative to the hive.</summary>
    public const string ClassesPath = @"Software\Classes";
    /// <summary>Where Explorer keeps per-user choices (<c>&lt;ext&gt;\UserChoice</c>), relative to HKCU.</summary>
    public const string FileExtsPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
    private const string PreviousProgIdValue = "PreviousProgId";

    /// <summary>The ProgIDs RFA Workbench and ATX Workbench registered, with their extensions.</summary>
    public static readonly IReadOnlyList<(string Extension, string ProgId)> OldAppProgIds =
    [
        (".rfa", "RFAWorkbench.rfa"),
        (".v3c", "RFAWorkbench.v3c"),
        (".atx", "ATXWorkbench.atx"),
    ];

    private readonly IAssociationStore _store;
    private readonly string _executable;

    /// <param name="store">Registry access.</param>
    /// <param name="kinds">Every extension Cairn knows, so a shared ProgID is only removed when unused.</param>
    /// <param name="executable">The executable registrations point at.</param>
    public AssociationRegistry(IAssociationStore store, IReadOnlyList<FileAssociation.Kind> kinds, string executable)
    {
        _store = store;
        Kinds = kinds;
        _executable = executable;
    }

    public IReadOnlyList<FileAssociation.Kind> Kinds { get; }

    /// <summary>One registration per extension the modules open (the first kind claiming an extension wins).</summary>
    public static IReadOnlyList<FileAssociation.Kind> KindsOf(IEnumerable<IDocumentKind> kinds) =>
        [.. kinds.SelectMany(k => k.Extensions.Select(e => FileAssociation.Kind.For(k.Id, e, k.AssociationDescription)))
            .DistinctBy(k => k.Extension)];

    private static string Classes(string path) => $@"{ClassesPath}\{path}";

    /// <summary>A Classes string as Windows merges them: the user's value, else the machine's.</summary>
    private string? ClassString(string path, string? name = null) =>
        _store.GetString(RegistryHive.CurrentUser, Classes(path), name) is { Length: > 0 } user ? user
            : _store.GetString(RegistryHive.LocalMachine, Classes(path), name);

    /// <summary>The per-user state of <paramref name="kind"/>'s extension default.</summary>
    public AssociationStatus Query(FileAssociation.Kind kind)
    {
        var current = _store.GetString(RegistryHive.CurrentUser, Classes(kind.Extension));
        if (string.IsNullOrEmpty(current)) return new(AssociationState.NotRegistered);
        if (string.Equals(current, kind.ProgId, StringComparison.OrdinalIgnoreCase))
            return PointsHere(kind.ProgId) ? new(AssociationState.Cairn) : new(AssociationState.OtherCairn);
        return new(AssociationState.OtherApp, current, _store.GetString(RegistryHive.CurrentUser, Classes(current)));
    }

    /// <summary>True when this copy of Cairn is registered for the extension: our ProgID runs this executable and the extension uses or lists it.</summary>
    public bool IsRegistered(FileAssociation.Kind kind) => PointsHere(kind.ProgId) && Uses(kind.Extension, kind.ProgId);

    private bool PointsHere(string progId) =>
        _store.GetString(RegistryHive.CurrentUser, Classes($@"{progId}\shell\open\command")) is { } line
        && line.Contains(_executable, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ProgID of a default the user picked in Windows' chooser, or null.</summary>
    public string? UserChoice(string extension) =>
        _store.GetString(RegistryHive.CurrentUser, $@"{FileExtsPath}\{extension}\UserChoice", "ProgId") is { Length: > 0 } progId ? progId : null;

    /// <summary>Who opens <paramref name="kind"/>'s extension now: the user's choice, else the per-user default, else the machine's.</summary>
    public AssociationOwner Owner(FileAssociation.Kind kind)
    {
        if (UserChoice(kind.Extension) is { } chosen) return Describe(chosen, OwnerSource.UserChoice);
        if (_store.GetString(RegistryHive.CurrentUser, Classes(kind.Extension)) is { Length: > 0 } user) return Describe(user, OwnerSource.UserClasses);
        if (_store.GetString(RegistryHive.LocalMachine, Classes(kind.Extension)) is { Length: > 0 } machine) return Describe(machine, OwnerSource.MachineClasses);
        return new(null, "None", OwnerSource.None, false);
    }

    private AssociationOwner Describe(string progId, OwnerSource source)
    {
        if (progId.StartsWith("Cairn.", StringComparison.OrdinalIgnoreCase))
        {
            bool here = PointsHere(progId);
            return new(progId, here ? "Cairn" : "Cairn (another copy)", source, here);
        }
        return new(progId, AppName(progId), source, false);
    }

    /// <summary>A program's friendly name for <paramref name="progId"/>: the app's own name where Windows keeps one, else its executable's description, else the type name, else the ProgID.</summary>
    private string AppName(string progId)
    {
        if (ClassString(progId, "FriendlyAppName") is { } friendly) return _store.ResolveIndirect(friendly);
        if (ClassString($@"{progId}\Application", "ApplicationName") is { } packaged) return _store.ResolveIndirect(packaged);
        if (ExecutableOf(ClassString($@"{progId}\shell\open\command")) is { } exe)
            return _store.DescribeExecutable(exe) ?? Path.GetFileNameWithoutExtension(exe);
        if (progId.StartsWith(@"Applications\", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(progId[@"Applications\".Length..]);
        if (ClassString(progId) is { } typeName) return _store.ResolveIndirect(typeName);
        return progId;
    }

    /// <summary>The program path at the start of a command line (quoted, or up to ".exe"), with environment variables expanded.</summary>
    internal static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        string exe;
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            exe = end > 1 ? command[1..end] : command[1..];
        }
        else
        {
            int dot = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = dot > 0 ? command[..(dot + 4)] : command.Split(' ')[0];
        }
        return exe.Length == 0 ? null : Environment.ExpandEnvironmentVariables(exe);
    }

    /// <summary>
    /// Points <paramref name="kinds"/> at the executable. With <paramref name="setDefault"/> false only the
    /// ProgID and the <c>OpenWithProgids</c> entry are written (for extensions whose default the user
    /// picked in Windows). Returns null on success, or what went wrong.
    /// </summary>
    public string? Register(IEnumerable<FileAssociation.Kind> kinds, bool setDefault = true) => Guard(() =>
    {
        foreach (var kind in kinds)
        {
            var previous = setDefault ? _store.GetString(RegistryHive.CurrentUser, Classes(kind.Extension)) : null;
            if (string.Equals(previous, kind.ProgId, StringComparison.OrdinalIgnoreCase)) previous = null;

            var progId = Classes(kind.ProgId);
            _store.SetString(progId, null, kind.FriendlyName);
            _store.SetString(progId, "FriendlyTypeName", kind.FriendlyName);
            if (previous is { Length: > 0 }) _store.SetString(progId, PreviousProgIdValue, previous);
            _store.SetString($@"{progId}\DefaultIcon", null, $"\"{_executable}\",0");
            _store.SetString($@"{progId}\shell\open\command", null, $"\"{_executable}\" \"%1\"");

            if (setDefault) _store.SetString(Classes(kind.Extension), null, kind.ProgId);
            _store.SetEmptyValue(Classes($@"{kind.Extension}\OpenWithProgids"), kind.ProgId);
        }
    });

    /// <summary>
    /// Undoes <see cref="Register"/> for <paramref name="kinds"/>. An extension that now points elsewhere
    /// keeps its default; the previous handler is put back only when the ProgID serves one extension,
    /// because <c>PreviousProgId</c> is stored once per ProgID.
    /// </summary>
    public string? Remove(IEnumerable<FileAssociation.Kind> kinds) => Guard(() =>
    {
        foreach (var kind in kinds)
        {
            bool single = Kinds.Count(k => k.ProgId == kind.ProgId) <= 1;
            var previous = single ? _store.GetString(RegistryHive.CurrentUser, Classes(kind.ProgId), PreviousProgIdValue) : null;
            Unhook(kind.Extension, kind.ProgId, previous);
            if (!Kinds.Any(k => k.ProgId == kind.ProgId && k.Extension != kind.Extension && Uses(k.Extension, k.ProgId)))
                _store.DeleteTree(Classes(kind.ProgId));
        }
    });

    /// <summary>True when any old RFA/ATX Workbench ProgID or extension default is still registered.</summary>
    public bool HasOldApps() =>
        OldAppProgIds.Any(o => _store.KeyExists(RegistryHive.CurrentUser, Classes(o.ProgId)) || Uses(o.Extension, o.ProgId));

    /// <summary>Removes only the old apps' ProgIDs, and extension defaults that point at them.</summary>
    public string? RemoveOldApps() => Guard(() =>
    {
        foreach (var (extension, progId) in OldAppProgIds)
        {
            var previous = _store.GetString(RegistryHive.CurrentUser, Classes(progId), PreviousProgIdValue);
            if (OldAppProgIds.Any(o => string.Equals(o.ProgId, previous, StringComparison.OrdinalIgnoreCase))) previous = null;
            Unhook(extension, progId, previous);
            _store.DeleteTree(Classes(progId));
        }
    });

    /// <summary>True when <paramref name="extension"/> opens with, or lists, <paramref name="progId"/> (per user).</summary>
    private bool Uses(string extension, string progId)
    {
        if (string.Equals(_store.GetString(RegistryHive.CurrentUser, Classes(extension)), progId, StringComparison.OrdinalIgnoreCase)) return true;
        return _store.GetValueNames(RegistryHive.CurrentUser, Classes($@"{extension}\OpenWithProgids")).Contains(progId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Clears <paramref name="progId"/> from <paramref name="extension"/>, restoring <paramref name="previous"/>, and drops keys left empty.</summary>
    private void Unhook(string extension, string progId, string? previous)
    {
        var key = Classes(extension);
        if (!_store.KeyExists(RegistryHive.CurrentUser, key)) return;
        if (string.Equals(_store.GetString(RegistryHive.CurrentUser, key), progId, StringComparison.OrdinalIgnoreCase))
        {
            if (previous is { Length: > 0 }) _store.SetString(key, null, previous);
            else _store.DeleteValue(key, null);
        }
        var openWith = $@"{key}\OpenWithProgids";
        if (_store.KeyExists(RegistryHive.CurrentUser, openWith))
        {
            _store.DeleteValue(openWith, progId);
            if (_store.IsEmptyKey(openWith)) _store.DeleteTree(openWith);
        }
        if (_store.IsEmptyKey(key)) _store.DeleteTree(key);
    }

    private string? Guard(Action change)
    {
        try
        {
            change();
            _store.NotifyChanged();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return ex.Message;
        }
    }
}
