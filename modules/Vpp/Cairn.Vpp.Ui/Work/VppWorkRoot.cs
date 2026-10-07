namespace Cairn.Vpp.Ui.Work;

/// <summary>
/// Where work copies live, and the only folders Cairn ever deletes there. The root is Cairn's own folder
/// (<c>%LOCALAPPDATA%\Cairn\work</c>, or a "Cairn work" folder INSIDE the folder the user chose, never the chosen
/// folder itself) and carries a marker written when Cairn created it. Each work folder carries its own marker;
/// clean-up deletes only marked children of a marked root, never follows links or junctions, never touches
/// anything else.
/// </summary>
public static class VppWorkRoot
{
    /// <summary>Name of Cairn's folder inside a user-chosen location.</summary>
    public const string FolderName = "Cairn work";
    private const string RootMarker = ".cairn-work-root";
    private const string FolderMarker = ".cairn-work";

    /// <summary>The default root.</summary>
    public static string Default => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cairn", "work");

    /// <summary>The root for a setting: the default when empty or not acceptable (<see cref="Validate"/>), else "&lt;chosen&gt;\Cairn work".</summary>
    public static string Resolve(string? setting) =>
        string.IsNullOrWhiteSpace(setting) || Validate(setting) is not null ? Default : Path.Combine(Path.GetFullPath(setting.Trim()), FolderName);

    /// <summary>Why <paramref name="setting"/> cannot be the location for work copies, or null when it can (empty = default).</summary>
    public static string? Validate(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return null;
        string text = setting.Trim();
        if (!Path.IsPathFullyQualified(text)) return "Enter a full path such as D:\\Temp (a relative path depends on where Cairn was started).";
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "This is not a valid folder path."; }
        if (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
            return "Choose a folder, not a whole drive.";
        foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.System, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData })
        {
            string s = Environment.GetFolderPath(special);
            if (s.Length > 0 && (string.Equals(full, s, StringComparison.OrdinalIgnoreCase)
                || (special != Environment.SpecialFolder.UserProfile && full.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                return "Choose a folder of your own, not a Windows or program folder.";
        }
        if (File.Exists(Path.Combine(full, "RF.exe")) || File.Exists(Path.Combine(full, "PureFaction.exe")) || File.Exists(Path.Combine(full, "AlpineFaction.exe")))
            return "Choose a folder outside the game folder.";
        return null;
    }

    /// <summary>Creates a new work folder <paramref name="name"/> under <paramref name="root"/> (and the root, marked, when new).</summary>
    public static string CreateFolder(string root, string name)
    {
        bool isDefault = string.Equals(Path.GetFullPath(root), Default, StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(root) || (isDefault && !File.Exists(Path.Combine(root, RootMarker)))) // the default lies in Cairn's own data folder
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, RootMarker), "Created by Cairn for work copies of packfile entries. Cairn deletes only its own sub-folders here.");
        }
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        string marker = Path.Combine(folder, FolderMarker);
        if (!File.Exists(marker)) File.WriteAllText(marker, string.Empty);
        return folder;
    }

    /// <summary>True when <paramref name="folder"/> is a work folder Cairn created (marked, not a link) under a marked root.</summary>
    public static bool IsOwnFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) && File.Exists(Path.Combine(info.FullName, FolderMarker))
                && info.Parent is { } parent && !parent.Attributes.HasFlag(FileAttributes.ReparsePoint) && File.Exists(Path.Combine(parent.FullName, RootMarker));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Deletes <paramref name="folder"/> only when it is one of Cairn's own work folders; false when not deleted.</summary>
    public static bool TryDeleteOwnFolder(string folder) => IsOwnFolder(folder) && VppWorkFolder.TryDeleteFolder(folder);

    /// <summary>Work folders of earlier sessions (crash, locked files) older than two days: only Cairn's own, marked ones.</summary>
    public static int RemoveStale(string root)
    {
        int removed = 0;
        try
        {
            if (!File.Exists(Path.Combine(root, RootMarker))) return 0;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-2) && TryDeleteOwnFolder(dir)) removed++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}
