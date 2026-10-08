using Microsoft.Win32;

namespace Cairn.Assets;

/// <summary>Which probe found the Red Faction install directory.</summary>
public enum GameDirectorySource
{
    /// <summary>Alpine Faction's own "Executable Path" setting.</summary>
    AlpineFaction,
    /// <summary>The legacy Dash Faction "Executable Path" setting.</summary>
    DashFaction,
    /// <summary>The retail installer's Volition registry key.</summary>
    Retail,
    /// <summary>Steam's uninstall entry for Red Faction.</summary>
    Steam,
    /// <summary>A GOG registry entry.</summary>
    Gog,
    /// <summary>One of the usual install paths, found by looking.</summary>
    CommonPath,
}

/// <summary>Where the game was found, and what to tell the user about it.</summary>
/// <param name="Directory">The full path of the install directory.</param>
/// <param name="Source">Which probe answered.</param>
public sealed record GameDirectoryDetection(string Directory, GameDirectorySource Source)
{
    /// <summary>
    /// The phrase the Settings dialog puts after "Found via", e.g. "Alpine Faction's settings".
    /// </summary>
    public string SourceDescription => Source switch
    {
        GameDirectorySource.AlpineFaction => "Alpine Faction's settings",
        GameDirectorySource.DashFaction => "Dash Faction's settings",
        GameDirectorySource.Retail => "the Red Faction installer",
        GameDirectorySource.Steam => "Steam",
        GameDirectorySource.Gog => "GOG",
        _ => "a usual install location",
    };

    /// <summary>The whole sentence the Settings dialog shows after a successful auto-detect.</summary>
    public string Hint => $"Found via {SourceDescription}.";
}

/// <summary>Reads string values out of the Windows registry. Injected so the probe order is testable.</summary>
public interface IRegistryReader
{
    /// <summary>
    /// Returns the string value, or null when the key, the value or the permission is missing.
    /// Implementations must never throw.
    /// </summary>
    /// <param name="hive">The hive to open.</param>
    /// <param name="view">32-bit or 64-bit view of the registry.</param>
    /// <param name="key">The sub-key path.</param>
    /// <param name="value">The value name.</param>
    string? Read(RegistryHive hive, RegistryView view, string key, string value);
}

/// <summary>The real registry. Every failure is swallowed: a locked-down machine reports nothing.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    /// <summary>The single instance; the class holds no state.</summary>
    public static WindowsRegistryReader Instance { get; } = new();

    /// <inheritdoc />
    public string? Read(RegistryHive hive, RegistryView view, string key, string value)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var subKey = baseKey.OpenSubKey(key);
            return subKey?.GetValue(value) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
            or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Best-effort detection of the Red Faction install directory, used to seed the setting on first
/// run and by the Settings dialog's Auto-detect button.
///
/// The first probe is Alpine Faction's own "Executable Path" setting, because a machine that has
/// Alpine Faction installed — which is every machine this app is for — has already told Alpine
/// Faction where the game is, and that answer is right far more often than guesswork is. The
/// legacy Dash Faction setting comes next, then the same registry entries Alpine Faction itself
/// falls back to (<c>common/src/config/GameConfig.cpp</c>, <c>detect_game_path</c>), then the usual
/// install paths. Nothing here throws, and every candidate has to pass
/// <see cref="LooksLikeGameDirectory"/> before it is accepted.
/// </summary>
public static class GameDirectoryLocator
{
    /// <summary>One registry probe: where to look, and what finding it would mean.</summary>
    private sealed record RegistryProbe(
        RegistryHive Hive,
        string Key,
        string Value,
        GameDirectorySource Source,
        bool PreferSixtyFourBitView = false);

    /// <summary>
    /// The registry probes, in order. Alpine Faction and Dash Faction store the path of the game
    /// <i>executable</i>; the rest store the directory. Both shapes go through the same
    /// normalisation, so it does not actually matter which one a given machine wrote.
    ///
    /// Every probe is tried in both registry views. Alpine Faction is a 32-bit process, so from
    /// this 64-bit one its HKLM keys live under <c>WOW6432Node</c>; Steam's uninstall entry is
    /// written by a 64-bit installer and lives in the 64-bit view, which is why Alpine Faction
    /// itself asks for <c>KEY_WOW64_64KEY</c> there.
    /// </summary>
    private static readonly RegistryProbe[] RegistryProbes =
    [
        // 1. Alpine Faction's own setting — the one the user's own launcher wrote.
        new(RegistryHive.CurrentUser, @"SOFTWARE\Volition\Red Faction\Alpine Faction",
            "Executable Path", GameDirectorySource.AlpineFaction),

        // 2. The legacy Dash Faction setting, same shape.
        new(RegistryHive.CurrentUser, @"SOFTWARE\Volition\Red Faction\Dash Faction",
            "Executable Path", GameDirectorySource.DashFaction),

        // 3-5. The retail installer.
        new(RegistryHive.LocalMachine, @"SOFTWARE\Volition\Red Faction", "InstallPath",
            GameDirectorySource.Retail),
        new(RegistryHive.LocalMachine, @"SOFTWARE\Volition\Red Faction", "Install Path",
            GameDirectorySource.Retail),
        new(RegistryHive.CurrentUser, @"SOFTWARE\Volition\Red Faction", "InstallPath",
            GameDirectorySource.Retail),

        // 6-7. Steam. 20530 is the id Alpine Faction reads; 20500 is the other Red Faction listing.
        new(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 20530",
            "InstallLocation", GameDirectorySource.Steam, PreferSixtyFourBitView: true),
        new(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 20500",
            "InstallLocation", GameDirectorySource.Steam, PreferSixtyFourBitView: true),

        // 8-10. GOG, including the entry Alpine Faction reads.
        new(RegistryHive.LocalMachine, @"SOFTWARE\Nordic Games\Red Faction", "INSTALL_DIR",
            GameDirectorySource.Gog),
        new(RegistryHive.LocalMachine, @"SOFTWARE\GOG.com\Games\1207658695", "path",
            GameDirectorySource.Gog),
        new(RegistryHive.LocalMachine, @"SOFTWARE\GOG.com\Games\1441704427", "path",
            GameDirectorySource.Gog),
    ];

    private static readonly string[] SteamLibrarySuffixes =
    [
        @"Steam\steamapps\common\Red Faction",
        @"Steam\steamapps\common\RedFaction",
    ];

    /// <summary>
    /// Returns the detected install directory, or null when nothing plausible was found. Never
    /// throws — a locked-down machine simply reports nothing.
    /// </summary>
    public static string? Detect() => DetectDetailed()?.Directory;

    /// <summary>
    /// Returns the detected install directory together with which probe answered, so the Settings
    /// dialog can say where the answer came from. Never throws.
    /// </summary>
    public static GameDirectoryDetection? DetectDetailed() =>
        DetectDetailed(WindowsRegistryReader.Instance, CommonPathCandidates());

    /// <summary>
    /// The testable seam: the same probe order over an injected registry and an injected list of
    /// well-known paths, so a test can assert the ordering without an RF install or a real key.
    /// </summary>
    /// <param name="registry">Where registry values come from.</param>
    /// <param name="commonPaths">The well-known install paths tried after every registry probe.</param>
    internal static GameDirectoryDetection? DetectDetailed(
        IRegistryReader registry, IEnumerable<string> commonPaths)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(commonPaths);

        foreach (var probe in RegistryProbes)
        {
            foreach (string raw in ReadRegistry(registry, probe))
            {
                foreach (string candidate in CandidateDirectories(raw))
                {
                    if (!LooksLikeGameDirectory(candidate)) continue;
                    if (FullPath(candidate) is { } full)
                        return new GameDirectoryDetection(full, probe.Source);
                }
            }
        }

        foreach (string candidate in commonPaths)
        {
            if (!LooksLikeGameDirectory(candidate)) continue;
            if (FullPath(candidate) is { } full)
                return new GameDirectoryDetection(full, GameDirectorySource.CommonPath);
        }
        return null;
    }

    /// <summary>
    /// True when the folder contains the files an RF install always has. Exposed so the Settings
    /// dialog can warn about a wrong folder before the user saves it.
    /// </summary>
    public static bool LooksLikeGameDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            if (!Directory.Exists(directory)) return false;
            return File.Exists(Path.Combine(directory, "tables.vpp"))
                || File.Exists(Path.Combine(directory, "RF.exe"))
                || File.Exists(Path.Combine(directory, "rf.exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="folder"/> is <paramref name="gameDirectory"/> or inside it (no output defaults to writing
    /// there: a loose file in the game's folders changes what the game loads). False when either is unset.
    /// </summary>
    public static bool IsInGameDirectory(string? folder, string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(gameDirectory)) return false;
        try
        {
            string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            string g = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
            return f.Equals(g, StringComparison.OrdinalIgnoreCase) || f.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns whatever a registry value holds into the directories worth testing. The value may be
    /// quoted, padded, full of environment variables, and may name either the game executable or
    /// the folder it lives in — launchers have written all of those. A path that is a directory is
    /// tried as one first; otherwise its parent is tried, and then the value itself, so a folder
    /// that merely happens not to exist yet is not silently turned into its parent.
    /// </summary>
    /// <param name="raw">The value exactly as the registry held it.</param>
    internal static IEnumerable<string> CandidateDirectories(string? raw)
    {
        string value = Clean(raw);
        if (value.Length == 0) yield break;

        bool isDirectory;
        try { isDirectory = Directory.Exists(value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            isDirectory = false;
        }

        if (isDirectory) yield return value;

        string? parent = null;
        try { parent = Path.GetDirectoryName(value); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { }
        if (!string.IsNullOrWhiteSpace(parent)) yield return parent!;

        if (!isDirectory) yield return value;
    }

    /// <summary>Trims whitespace and surrounding quotes, then expands environment variables.</summary>
    private static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string value = raw.Trim().Trim('"').Trim();
        if (value.Length == 0) return string.Empty;
        try { value = Environment.ExpandEnvironmentVariables(value); }
        catch (ArgumentException) { /* a stray % is not worth failing over */ }
        return value.Trim().Trim('"').Trim();
    }

    private static string? FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads one probe in both registry views, the more likely one first.</summary>
    private static IEnumerable<string> ReadRegistry(IRegistryReader registry, RegistryProbe probe)
    {
        var views = probe.PreferSixtyFourBitView
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
            : [RegistryView.Registry32, RegistryView.Registry64];
        foreach (var view in views)
        {
            string? value;
            try { value = registry.Read(probe.Hive, view, probe.Key, probe.Value); }
            catch (Exception ex) when (ex is System.Security.SecurityException
                or UnauthorizedAccessException or IOException)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(value)) yield return value!;
        }
    }

    /// <summary>The usual install locations, tried once every registry probe has come up empty.</summary>
    private static IEnumerable<string> CommonPathCandidates()
    {
        foreach (string root in ProgramFileRoots())
        {
            foreach (string suffix in SteamLibrarySuffixes)
                yield return Path.Combine(root, suffix);
            yield return Path.Combine(root, "GOG Galaxy", "Games", "Red Faction");
            yield return Path.Combine(root, "GOG.com", "Red Faction");
            yield return Path.Combine(root, "Red Faction");
            yield return Path.Combine(root, "THQ", "Red Faction");
        }
    }

    private static IEnumerable<string> ProgramFileRoots()
    {
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.ProgramFiles,
        })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
        foreach (var drive in SafeDrives())
        {
            yield return Path.Combine(drive, "Games");
            yield return drive;
        }
    }

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var drive in drives)
        {
            bool ready;
            try { ready = drive.DriveType == DriveType.Fixed && drive.IsReady; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (ready) yield return drive.RootDirectory.FullName;
        }
    }
}
