using System.Globalization;
using Cairn.Formats;
using Cairn.Formats.Rf2;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Rf2;

/// <summary>What a Red Faction II packfile group says and where its data was found, kept on its package.</summary>
/// <param name="Toc">The group file as read.</param>
/// <param name="TocPath">The group file's full path.</param>
/// <param name="PackfilePath">Packed groups: the <c>.packfile</c> the entries are read from; loose groups: null.</param>
/// <param name="PackfileStamp">The packfile's length and time when the group was opened (so a change is noticed).</param>
/// <param name="Missing">Loose files the group lists that are not on disk.</param>
/// <param name="SizeDiffers">Loose files whose size on disk differs from the listed size ("name: listed N, on disk M").</param>
/// <param name="CaseDuplicates">Names listed twice in different case (one file on Windows): "a.rfc / A.rfc".</param>
public sealed record Rf2GroupInfo(
    Rf2TocGroup Toc,
    string TocPath,
    string? PackfilePath,
    VppArchiveStamp? PackfileStamp,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> SizeDiffers,
    IReadOnlyList<string> CaseDuplicates)
{
    /// <summary>"packed into All_Levels.packfile" or "loose files".</summary>
    public string ModeText => Toc.IsPacked ? $"packed into {Path.GetFileName(PackfilePath ?? Toc.PackfilePath ?? Toc.GroupName + Rf2TocGroup.PackfileExtension)}" : "loose files";

    /// <summary>The flags value with the bits Cairn knows named ("6 (packed; bit 2 set, meaning unknown)").</summary>
    public string FlagsText
    {
        get
        {
            var parts = new List<string> { Toc.IsPacked ? "packed" : "loose" };
            if ((Toc.Flags & 4) != 0) parts.Add("bit 2 set, meaning unknown");
            uint other = Toc.Flags & ~6u;
            if (other != 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"other bits 0x{other:X}"));
            return string.Create(CultureInfo.InvariantCulture, $"{Toc.Flags} ({string.Join("; ", parts)})");
        }
    }

    /// <summary>The one-line description for the banner above the list.</summary>
    public string Banner
    {
        get
        {
            string count = Toc.Entries.Count == 1 ? "1 file" : $"{Toc.Entries.Count:N0} files";
            string text = $"Red Faction II packfile group {Toc.GroupName} ({count}, {ModeText}). It is read only in Cairn: preview, extract, open and convert its files; nothing can be added, removed, renamed or saved.";
            if (Missing.Count > 0) text += Missing.Count == 1 ? $" 1 listed file is not on disk ({Missing[0]})." : $" {Missing.Count:N0} listed files are not on disk.";
            return text;
        }
    }

    /// <summary>The group's facts for the details pane: label and value pairs.</summary>
    public IReadOnlyList<(string Label, string Value)> Facts(IEnumerable<VppItem> items)
    {
        var c = CultureInfo.CurrentCulture;
        var rows = new List<(string, string)>
        {
            ("Group", Toc.GroupName),
            ("Mode", Toc.IsPacked ? "Packed (one .packfile)" : "Loose files below the game folder"),
            ("Flags", FlagsText),
        };
        if (Toc.IsPacked) rows.Add(("Second value", Toc.Second.ToString(c) + " (meaning unknown)"));
        rows.Add(("Files", Toc.Entries.Count.ToString("N0", c)));
        rows.Add(("Total size", string.Format(c, "{0:N0} bytes", Toc.DataBytes)));
        rows.Add(("Folder", Toc.Folder));
        if (Toc.IsPacked)
        {
            rows.Add(("Stored packfile path", Toc.PackfilePath ?? string.Empty));
            if (PackfilePath is not null) rows.Add(("Packfile", PackfilePath));
        }
        rows.Add(("Group file", TocPath));
        if (Missing.Count > 0) rows.Add(("Missing on disk", string.Join(", ", Missing.Take(8)) + (Missing.Count > 8 ? $" and {Missing.Count - 8:N0} more" : string.Empty)));
        if (SizeDiffers.Count > 0) rows.Add(("Size differs", string.Join(", ", SizeDiffers.Take(4)) + (SizeDiffers.Count > 4 ? $" and {SizeDiffers.Count - 4:N0} more" : string.Empty)));
        if (CaseDuplicates.Count > 0) rows.Add(("Listed twice", string.Join(", ", CaseDuplicates) + " (one file on Windows)"));
        int empty = Toc.Entries.Count(e => e.Size == 0);
        if (empty > 0) rows.Add(("Empty files", empty.ToString("N0", c)));
        foreach (var g in items.GroupBy(i => VppNames.ExtensionOf(i.Name), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            rows.Add(("Type " + (g.Key.Length == 0 ? "(none)" : g.Key), string.Format(c, "{0:N0} ({1:N0} bytes)", g.Count(), g.Sum(i => i.Size))));
        return rows;
    }
}

/// <summary>
/// Opens Red Faction II packfile groups (<c>.toc_group</c>) as read-only packages: a packed group's entries read
/// from its <c>.packfile</c>, a loose group's from the files below the game folder. Nothing here writes.
/// </summary>
public static class Rf2Packfiles
{
    /// <summary>True when the path names a packfile group (by extension: the format has no signature).</summary>
    public static bool IsGroupFile(string? path) => Rf2TocGroup.HasExtension(path);

    /// <summary>
    /// Opens a group file as a read-only package. Packed: the <c>.packfile</c> is looked for beside the group file and
    /// by its stored path below the game folder, and every entry must lie inside it. Loose: each listed file is taken
    /// from its stored path below the game folder; missing ones stay listed (they cannot be read).
    /// </summary>
    /// <exception cref="AssetFormatException">The group cannot be read, or its packfile is missing or does not match.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static VppPackage Open(string tocPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(tocPath);
        string full = Path.GetFullPath(tocPath);
        var stamp = VppArchiveStamp.Of(full);
        var toc = Rf2TocGroup.Open(full);
        var items = ImmutableArray.CreateBuilder<VppItem>(toc.Entries.Count);
        var missing = new List<string>();
        var differs = new List<string>();
        string? packfile = null;
        VppArchiveStamp? packStamp = null;
        if (toc.IsPacked)
        {
            packfile = toc.FindPackfile(full) ?? throw new AssetFormatException(
                $"'{Path.GetFileName(full)}' is a packed Red Faction II packfile group, but its packfile ({toc.PackfilePath}) was not found. Cairn looked in:\n"
                + string.Join("\n", toc.PackfileCandidates(full).Select(p => "• " + p)));
            packStamp = VppArchiveStamp.Of(packfile);
            toc.CheckPackfile(packStamp.Length, Path.GetFileName(packfile));
            for (int i = 0; i < toc.Entries.Count; i++)
            {
                var e = toc.Entries[i];
                if (e.Size > int.MaxValue) throw new AssetFormatException($"'{Path.GetFileName(full)}' lists {e.Name} with {e.Size:N0} bytes, more than Cairn reads from one entry.");
                items.Add(Original(e.Name, new ArchiveSource(packfile, e.Offset, (int)e.Size), i));
            }
        }
        else
        {
            for (int i = 0; i < toc.Entries.Count; i++)
            {
                var e = toc.Entries[i];
                string file = Rf2TocGroup.LooseFilePath(full, e)!;
                var info = new FileInfo(file);
                VppSource source;
                if (!info.Exists)
                {
                    missing.Add(e.Name);
                    source = new MissingFileSource(file, e.Size);
                }
                else
                {
                    if (info.Length != e.Size) differs.Add(string.Create(CultureInfo.CurrentCulture, $"{e.Name}: listed {e.Size:N0} bytes, on disk {info.Length:N0}"));
                    source = new FileSource(info.FullName, info.Length, info.LastWriteTimeUtc);
                }
                items.Add(Original(e.Name, source, i));
            }
        }
        var duplicates = toc.Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => string.Join(" / ", g.Select(e => e.Name).Distinct(StringComparer.Ordinal))).ToList();
        int count = items.Count;
        return new VppPackage(full, items.MoveToImmutable(), stamp)
        {
            OriginalCount = count,
            Rf2 = new Rf2GroupInfo(toc, full, packfile, packStamp, missing, differs, duplicates),
        };
    }

    private static VppItem Original(string name, VppSource source, int index) =>
        new(name, source, VppItemState.Original) { OriginalName = name, OriginalIndex = index };

    /// <summary>Problem codes of RF2 groups (they replace the RF1 packfile rules, which do not apply).</summary>
    public static IReadOnlyDictionary<string, string> Rules { get; } = new Dictionary<string, string>
    {
        ["RF2001"] = "Listed file missing on disk",
        ["RF2002"] = "Name listed twice in different case",
        ["RF2003"] = "File size differs from the listing",
        ["RF2004"] = "Empty file",
        ["RF2005"] = "Packfile changed on disk",
    };

    /// <summary>
    /// The facts worth flagging in an RF2 group: missing loose files and size differences (warnings), names listed
    /// twice and empty files (information). The RF1 rules (name lengths, types the game loads) do not apply.
    /// </summary>
    public static IReadOnlyList<VppProblem> Validate(VppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Rf2 is not { } info) return [];
        var problems = new List<VppProblem>();
        foreach (var item in package.Items)
        {
            if (item.Source is MissingFileSource m)
                problems.Add(new("RF2001", VppSeverity.Warning, $"{item.Name} is listed by the group but is not on disk ({m.FilePath}); it cannot be extracted.", item.Name));
            else if (item.Size == 0)
                problems.Add(new("RF2004", VppSeverity.Info, $"{item.Name} is an empty file (0 bytes).", item.Name));
        }
        foreach (var d in info.SizeDiffers)
            problems.Add(new("RF2003", VppSeverity.Warning, d + " (the file on disk is used).", d[..d.IndexOf(':', StringComparison.Ordinal)]));
        foreach (var d in info.CaseDuplicates)
            problems.Add(new("RF2002", VppSeverity.Info, $"{d} differ only in case: on Windows they are one file, so extracting both into one folder writes it once."));
        return problems;
    }

    /// <summary>
    /// Null when the packfile a packed group reads from still has the length and time it had when the group was
    /// opened; otherwise why its entries cannot be read now.
    /// </summary>
    public static string? StaleProblem(VppPackage package)
    {
        if (package.Rf2 is not { PackfilePath: { } path, PackfileStamp: { } stamp }) return null;
        VppArchiveStamp? now;
        try { now = File.Exists(path) ? VppArchiveStamp.Of(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { now = null; }
        if (now == stamp) return null;
        return (now is null ? $"{Path.GetFileName(path)} is no longer on disk" : $"{Path.GetFileName(path)} changed on disk after the group was opened")
            + ", so its entries cannot be read correctly. Reload the group (or reopen it) first.";
    }
}
