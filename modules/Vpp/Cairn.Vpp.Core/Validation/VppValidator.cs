using Cairn.Formats.Vpp;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Validation;

/// <summary>How serious a packfile problem is. Errors block saving.</summary>
public enum VppSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One problem with a packfile or one of its entries.</summary>
/// <param name="Code">Stable rule code ("VPP005").</param>
/// <param name="Severity">Errors block saving.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
/// <param name="EntryName">The entry concerned, or null for the whole packfile.</param>
public sealed record VppProblem(string Code, VppSeverity Severity, string Message, string? EntryName = null)
{
    public override string ToString() => $"{Code} {Severity}: {Message}";
}

/// <summary>
/// Checks a package against the format's limits and the game's loading rules (Alpine Faction is the
/// baseline: limits only stock RF has are not checked). <see cref="Validate"/> is pure (no file access) and cheap enough to run after every edit;
/// <see cref="CheckSources"/> touches the disk and runs before a save.
/// </summary>
public static class VppValidator
{
    /// <summary>Codes and titles of every rule, for help pages and tests.</summary>
    public static IReadOnlyDictionary<string, string> Rules { get; } = new Dictionary<string, string>
    {
        ["VPP001"] = "Name is empty",
        ["VPP002"] = "Name too long",
        ["VPP003"] = "Name has characters that cannot be stored",
        ["VPP004"] = "Name contains a folder separator or control character",
        ["VPP005"] = "Duplicate name",
        ["VPP006"] = "Entry larger than 1.5 GB",
        ["VPP007"] = "Packfile too large for the format",
        ["VPP008"] = "Entry starts beyond 2 GB",
        ["VPP009"] = "Type the game does not load",
        ["VPP010"] = "Added file changed or missing",
        ["VPP011"] = "Entry data missing from the packfile",
        ["VPP012"] = "Packfile changed on disk",
        ["VPP013"] = "Packfile is empty",
        ["VPP014"] = "Name cannot be used as a Windows file name",
        ["VPP015"] = "Name has no extension",
        ["VPP016"] = "Too many entries for Alpine Faction",
        ["VPP017"] = "Empty entry",
        ["VPP020"] = "Name is not plain ASCII",
        ["VPP021"] = "Animation name with extra dots",
        ["VPP023"] = "Upper-case .OGG extension",
        ["VPP024"] = "Packfile name longer than 31 characters",
        ["VPP025"] = "Packfile path longer than 127 characters",
        ["VPP026"] = "Header size field differs from the file length",
        ["VPP027"] = "Name longer than 31 characters",
    };

    /// <summary>
    /// Longest name (extension included) the game's texture, sound and font managers keep: they copy the name
    /// into a 32-byte slot without a bound, so a longer name is cut off or garbled and the file is not found.
    /// </summary>
    public const int MaxAssetNameLength = 31;

    /// <summary>Extensions whose loaders keep the name in a 32-byte slot (textures, sounds, bitmap fonts).</summary>
    public static IReadOnlySet<string> ShortNameExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".tga", ".vbm", ".dds", ".png", ".jpg", ".jpeg", ".atx", ".wav", ".ogg", ".vf",
    };

    /// <summary>True when the game loads files of this name's type through a 32-byte name slot and the name does not fit.</summary>
    public static bool IsNameTooLongForGame(string name) =>
        name.Length > MaxAssetNameLength && ShortNameExtensions.Contains(VppNames.ExtensionOf(name)) && !IsExternalMipOfShortName(name);

    // "base-mip1.tga" is an external mip level that the engine opens as a file next to the bitmap "base.tga"; it never
    // takes a name slot itself, so only the base name has to fit (stock maps1.vpp has "mtl_contrl_panel03_drty-mip2.tga").
    private static bool IsExternalMipOfShortName(string name)
    {
        string ext = VppNames.ExtensionOf(name);
        string stem = name[..^ext.Length];
        int dash = stem.LastIndexOf("-mip", StringComparison.OrdinalIgnoreCase);
        if (dash <= 0 || dash + 4 >= stem.Length || !stem[(dash + 4)..].All(char.IsAsciiDigit)) return false;
        return dash + ext.Length <= MaxAssetNameLength;
    }

    /// <summary>Most entries Alpine Faction accepts in one packfile (it rejects the whole packfile beyond this).</summary>
    public const int MaxEntries = 0x100000;

    /// <summary>Largest entry Alpine Faction accepts (it rejects the whole packfile beyond this).</summary>
    public const long MaxEntryBytes = 0x60000000;

    /// <summary>The game seeks with a signed 32-bit offset: every entry must start below this.</summary>
    public const long MaxEntryStart = 1L << 31;

    /// <summary>Longest packfile file name the game loads.</summary>
    public const int MaxPackfileNameLength = 31;

    /// <summary>Longest full packfile path the game loads.</summary>
    public const int MaxPackfilePathLength = 127;

    private static readonly char[] WindowsInvalid = ['<', '>', '"', '|', '?', '*'];

    /// <summary>Checks names, sizes and limits (and the package's own path). Does not read the disk.</summary>
    public static IReadOnlyList<VppProblem> Validate(VppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var problems = new List<VppProblem>();
        var seen = new Dictionary<string, string>(VppNames.Comparer);
        long offset = VppArchive.BlockSize + VppPackage.AlignUp((long)package.Items.Length * VppArchive.EntryBytes);
        bool reportedStart = false;
        foreach (var item in package.Items)
        {
            CheckEntry(package, item, seen, problems);
            if (!reportedStart && item.Size > 0 && offset >= MaxEntryStart)
            {
                problems.Add(new("VPP008", VppSeverity.Error,
                    $"'{item.Name}' (and every entry after it) would start {offset:N0} bytes into the packfile; the game cannot reach data beyond 2 GB.", item.Name));
                reportedStart = true;
            }
            offset += VppPackage.AlignUp(item.Size);
        }

        int count = package.Items.Length;
        if (count == 0) problems.Add(new("VPP013", VppSeverity.Warning, "The packfile has no entries."));
        if (count > MaxEntries)
        {
            problems.Add(new("VPP016", VppSeverity.Error, $"The packfile has {count:N0} entries; Alpine Faction rejects packfiles with more than {MaxEntries:N0}."));
        }
        long total = package.ArchiveBytes;
        if (total > uint.MaxValue)
        {
            problems.Add(new("VPP007", VppSeverity.Error, $"The packfile would be {total:N0} bytes; the format records sizes in 32 bits (at most {uint.MaxValue:N0})."));
        }
        if (package.HeaderArchiveSize is { } field && package.Stamp is { } stamp && field != stamp.Length)
        {
            problems.Add(new("VPP026", VppSeverity.Info, $"The header records {field:N0} bytes but the file has {stamp.Length:N0}; the game ignores the field and saving rewrites it."));
        }
        if (package.Path is { } path) problems.AddRange(ValidateTargetPath(path));
        return problems;
    }

    private static void CheckEntry(VppPackage package, VppItem item, Dictionary<string, string> seen, List<VppProblem> problems)
    {
        string name = item.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add(new("VPP001", VppSeverity.Error, "An entry has an empty name.", name));
            return;
        }
        if (name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(c => c < ' ' || c == '\u007F'))
        {
            problems.Add(new("VPP004", VppSeverity.Error, $"'{name}' contains a folder separator or a control character; the game looks files up by bare name and can never find it.", name));
        }
        if (!VppNames.IsLatin1(name))
        {
            problems.Add(new("VPP003", VppSeverity.Error, $"'{name}' contains characters that cannot be stored (names are Latin-1).", name));
        }
        else if (!VppNames.IsAscii(name))
        {
            problems.Add(new("VPP020", VppSeverity.Warning, $"'{name}' is not plain ASCII: the game ignores case for A-Z only, and other tools or code pages may mangle it.", name));
        }
        if (VppNames.ByteLength(name) > VppNames.MaxNameBytes)
        {
            problems.Add(new("VPP002", VppSeverity.Error, $"'{name}' is {VppNames.ByteLength(name)} characters long; at most {VppNames.MaxNameBytes} fit.", name));
        }
        else if (IsNameTooLongForGame(name))
        {
            problems.Add(new("VPP027", VppSeverity.Warning, $"'{name}' is {name.Length} characters long; the game keeps texture, sound and font names in 31 characters, so a longer name is cut off and the file will not be found (or loads wrong). Rename it to 31 characters or fewer.", name));
        }
        if (seen.TryGetValue(name, out string? first))
        {
            problems.Add(new("VPP005", VppSeverity.Error, $"'{name}' appears more than once (the game ignores case: '{first}'); only the last one would be used.", name));
        }
        else
        {
            seen.Add(name, name);
        }
        if (item.Size > MaxEntryBytes)
        {
            problems.Add(new("VPP006", VppSeverity.Error, $"'{name}' is {item.Size:N0} bytes; Alpine Faction rejects packfiles with entries over 1.5 GB.", name));
        }
        else if (item.Size == 0)
        {
            problems.Add(new("VPP017", VppSeverity.Warning, $"'{name}' is empty (0 bytes); legal, but usually a mistake.", name));
        }
        if (name.IndexOfAny(WindowsInvalid) >= 0 || name.EndsWith('.') || name.EndsWith(' ') || name != name.TrimStart())
        {
            problems.Add(new("VPP014", VppSeverity.Warning, $"'{name}' cannot be extracted under this name on Windows.", name));
        }

        string ext = VppNames.ExtensionOf(name);
        if (ext.Length == 0)
        {
            problems.Add(new("VPP015", VppSeverity.Warning, $"'{name}' has no extension; nothing in the game asks for it.", name));
        }
        else
        {
            var type = VppFileTypes.Find(name);
            if (type is null || !type.GameLoads)
            {
                problems.Add(new("VPP009", VppSeverity.Warning, $"'{name}': the game does not load {ext} files.", name));
            }
            if (ext == ".rfa" && name.IndexOf('.') < name.Length - 4)
            {
                problems.Add(new("VPP021", VppSeverity.Warning, $"'{name}': the game cuts animation names at the first dot and looks for '{name[..name.IndexOf('.')]}.rfa', so this entry can never load.", name));
            }
            if (ext == ".ogg" && !name.EndsWith(".ogg", StringComparison.Ordinal))
            {
                problems.Add(new("VPP023", VppSeverity.Warning, $"'{name}': Alpine Faction only treats a sound as Ogg Vorbis when its extension is lower-case '.ogg'.", name));
            }
        }

        if (item.Source is ArchiveSource archive && package.Stamp is { } stamp
            && string.Equals(archive.ArchivePath, package.Path, StringComparison.OrdinalIgnoreCase)
            && archive.Offset + archive.Length > stamp.Length)
        {
            problems.Add(new("VPP011", VppSeverity.Error, $"'{name}' lies beyond the end of the packfile on disk (it is truncated); remove or replace it.", name));
        }
    }

    /// <summary>Checks a packfile's own path against the game's limits (for Save As).</summary>
    public static IReadOnlyList<VppProblem> ValidateTargetPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var problems = new List<VppProblem>();
        string fileName = Path.GetFileName(path);
        if (fileName.Length > MaxPackfileNameLength)
        {
            problems.Add(new("VPP024", VppSeverity.Warning, $"The packfile name '{fileName}' is {fileName.Length} characters long; the game does not load packfiles whose name exceeds {MaxPackfileNameLength}."));
        }
        if (path.Length > MaxPackfilePathLength)
        {
            problems.Add(new("VPP025", VppSeverity.Info, $"The full path is {path.Length} characters long; if this is inside the game folder, the game does not load packfiles whose path exceeds {MaxPackfilePathLength}."));
        }
        return problems;
    }

    /// <summary>
    /// Checks the disk: added files that changed or vanished since they were added, and the packfile
    /// itself having changed since it was opened (its entries' offsets would then be wrong).
    /// </summary>
    public static IReadOnlyList<VppProblem> CheckSources(VppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var problems = new List<VppProblem>();
        foreach (var item in package.Items)
        {
            if (item.Source is FileSource file && file.CheckUnchanged() is { } why)
            {
                problems.Add(new("VPP010", VppSeverity.Error, $"'{item.Name}' was added from '{file.FilePath}', but {why}. Add it again or remove it.", item.Name));
            }
        }
        if (package.Path is { } path && package.Stamp is { } stamp
            && package.Items.Any(i => i.Source is ArchiveSource a && string.Equals(a.ArchivePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            var now = File.Exists(path) ? VppArchiveStamp.Of(path) : null;
            if (now is null)
            {
                problems.Add(new("VPP012", VppSeverity.Error, $"'{Path.GetFileName(path)}' no longer exists on disk, so its entries cannot be read."));
            }
            else if (now != stamp)
            {
                var affected = package.Items.Where(i => i.Source is ArchiveSource a && string.Equals(a.ArchivePath, path, StringComparison.OrdinalIgnoreCase)).Select(i => i.Name).ToList();
                problems.Add(new("VPP012", VppSeverity.Error, $"'{Path.GetFileName(path)}' changed on disk after it was opened, so {affected.Count:N0} entr{(affected.Count == 1 ? "y" : "ies")} stored in it ({string.Join(", ", affected.Take(5))}{(affected.Count > 5 ? ", ..." : "")}) can no longer be read correctly; reopen it before saving (Save As works once no entry is read from it)."));
            }
        }
        return problems;
    }

    /// <summary>True when any problem is an error.</summary>
    public static bool HasErrors(IEnumerable<VppProblem> problems) => problems.Any(p => p.Severity == VppSeverity.Error);
}
