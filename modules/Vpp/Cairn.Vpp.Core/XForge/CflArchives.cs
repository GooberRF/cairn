using System.Globalization;
using System.Security.Cryptography;
using Cairn.Formats;
using Cairn.Formats.XForge;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.XForge;

/// <summary>
/// An entry of an X-Forge CFL archive on disk: its data is read from the archive and unpacked each time it is opened.
/// </summary>
/// <param name="ArchivePath">Full path of the archive.</param>
/// <param name="Archive">The archive's directory as read.</param>
/// <param name="Entry">The entry.</param>
/// <param name="Problem">Why the entry cannot be read (its compression is not supported, or it is corrupt), or null.</param>
public sealed record CflEntrySource(string ArchivePath, CflArchive Archive, CflEntry Entry, string? Problem) : VppSource
{
    /// <summary>True when the open did not check the entry (the archive would unpack too much); it is checked when read.</summary>
    public bool NotChecked { get; init; }

    public override long Size => Entry.UnpackedSize;

    public override string Describe() => $"'{Path.GetFileName(ArchivePath)}' at offset {Entry.Offset:N0} ({Entry.CompressionText})";

    /// <exception cref="IOException">The archive cannot be read, or the entry cannot be unpacked (the message says why).</exception>
    public override Stream Open()
    {
        if (Problem is not null) throw new IOException($"'{Entry.Name}' in '{Path.GetFileName(ArchivePath)}' cannot be read: {Problem}.");
        using var stream = new FileStream(ArchivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        try
        {
            return Archive.OpenEntry(stream, Entry);
        }
        catch (AssetFormatException ex)
        {
            // the archive changed on disk since it was opened (the open checked every entry)
            throw new IOException(ex.Message, ex);
        }
    }
}

/// <summary>What an X-Forge CFL archive says, kept on its package.</summary>
/// <param name="Archive">The directory as read.</param>
/// <param name="ArchivePath">The archive's full path.</param>
/// <param name="Problems">Why entries cannot be read, by index in the directory (compression not supported, or corrupt).</param>
/// <param name="ListedTwice">Names listed more than once (ignoring case).</param>
public sealed record CflArchiveInfo(CflArchive Archive, string ArchivePath, IReadOnlyDictionary<int, string> Problems, IReadOnlyList<CflNameListedTwice> ListedTwice)
{
    /// <summary>
    /// The entries the open did not check, by index in the directory: checking them would have unpacked more than
    /// <see cref="CheckBudget"/> bytes in all. Empty for an ordinary archive.
    /// </summary>
    public IReadOnlyList<int> NotChecked { get; init; } = [];

    /// <summary>The most the open unpacked to check the entries (see <see cref="CflArchives.CheckBudgetFor"/>).</summary>
    public long CheckBudget { get; init; }

    /// <summary>Entries stored as they are.</summary>
    public int StoredCount => Archive.Entries.Count(e => e.Compression == CflArchive.Stored);

    /// <summary>Entries packed with zlib.</summary>
    public int ZlibCount => Archive.Entries.Count(e => e.Compression == CflArchive.Zlib);

    /// <summary>Entries with a compression Cairn does not read (a CFL3 plug-in's).</summary>
    public int UnsupportedCount => Archive.Entries.Count(e => !e.IsSupported);

    /// <summary>Entries whose data does not unpack to their listed size (the unsupported ones are not counted).</summary>
    public int CorruptCount => Problems.Count(p => Archive.Entries[p.Key].IsSupported);

    /// <summary>The stored entry an item of this archive's package came from, or null.</summary>
    public CflEntry? EntryOf(VppItem item) =>
        item.OriginalIndex >= 0 && item.OriginalIndex < Archive.Entries.Count ? Archive.Entries[item.OriginalIndex] : null;

    /// <summary>The one-line description for the banner above the list.</summary>
    public string Banner
    {
        get
        {
            int count = Archive.Entries.Count;
            string text = $"X-Forge CFL3 archive {Path.GetFileName(ArchivePath)} (Red Faction for N-Gage, {(count == 1 ? "1 file" : $"{count:N0} files")}). It is read only in Cairn: preview, extract, open and convert its files; nothing can be added, removed, renamed or saved.";
            if (Problems.Count > 0) text += Problems.Count == 1 ? " 1 file cannot be read (see Problems)." : $" {Problems.Count:N0} files cannot be read (see Problems).";
            if (NotChecked.Count > 0) text += NotChecked.Count == 1 ? " 1 file was not checked (see Problems)." : $" {NotChecked.Count:N0} files were not checked (see Problems).";
            return text;
        }
    }

    /// <summary>The archive's facts for the details pane: label and value pairs.</summary>
    public IReadOnlyList<(string Label, string Value)> Facts(IEnumerable<VppItem> items)
    {
        var c = CultureInfo.CurrentCulture;
        long packed = Archive.Entries.Sum(e => (long)e.PackedSize), unpacked = Archive.Entries.Sum(e => (long)e.UnpackedSize);
        var rows = new List<(string, string)>
        {
            ("Format", "X-Forge CFL3 (Red Faction for N-Gage)"),
            ("Entries", Archive.Entries.Count.ToString("N0", c)),
            ("Stored", StoredCount.ToString("N0", c)),
            ("zlib", ZlibCount.ToString("N0", c)),
        };
        if (UnsupportedCount > 0) rows.Add(("Unsupported", string.Format(c, "{0:N0} (a compression Cairn does not read)", UnsupportedCount)));
        if (CorruptCount > 0) rows.Add(("Corrupt", string.Format(c, "{0:N0} (data that does not unpack to its listed size)", CorruptCount)));
        if (NotChecked.Count > 0) rows.Add(("Not checked", string.Format(c, "{0:N0} (checking every entry would unpack more than {1:N0} bytes)", NotChecked.Count, CheckBudget)));
        rows.Add(("Packed size", string.Format(c, "{0:N0} bytes", packed)));
        rows.Add(("Unpacked size", string.Format(c, "{0:N0} bytes", unpacked)));
        rows.Add(("Directory", string.Format(c, "{0}, {1:N0} bytes packed, {2:N0} unpacked, at offset {3:N0}",
            CflArchive.CompressionName(Archive.DirectoryCompression), Archive.DirectoryPackedSize, Archive.DirectoryUnpackedSize, Archive.DirectoryOffset)));
        if (Archive.Start > 0) rows.Add(("Starts at", string.Format(c, "offset {0:N0} (appended to other data; {1:N0} bytes long)", Archive.Start, Archive.Length)));
        if (ListedTwice.Count > 0) rows.Add(("Listed twice", string.Join(", ", ListedTwice.Take(4).Select(t => t.Text)) + (ListedTwice.Count > 4 ? string.Format(c, " and {0:N0} more", ListedTwice.Count - 4) : string.Empty)));
        rows.Add(("Archive", ArchivePath));
        foreach (var g in items.GroupBy(i => VppNames.ExtensionOf(i.Name), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            rows.Add(("Type " + (g.Key.Length == 0 ? "(none)" : g.Key), string.Format(c, "{0:N0} ({1:N0} bytes)", g.Count(), g.Sum(i => i.Size))));
        return rows;
    }
}

/// <summary>A name a CFL archive lists more than once (ignoring case).</summary>
/// <param name="Names">The spellings, "a.pcx" or "a.pcx / A.pcx".</param>
/// <param name="Name">The first listed spelling.</param>
/// <param name="Times">How many times it is listed.</param>
/// <param name="SameData">True when every copy unpacks to the same bytes.</param>
public sealed record CflNameListedTwice(string Names, string Name, int Times, bool SameData)
{
    /// <summary>True when a copy was not checked when the archive opened, so whether they hold the same data is not known.</summary>
    public bool NotChecked { get; init; }

    /// <summary>"a.pcx (2 times, same data)".</summary>
    public string Text => string.Format(CultureInfo.CurrentCulture, "{0} ({1:N0} times, {2})", Names, Times, SameData ? "same data" : NotChecked ? "not checked" : "different data");
}

/// <summary>
/// Opens X-Forge CFL3 archives (<c>.cfl</c>, Red Faction for N-Gage's <c>redfaction.cfl</c>) as read-only packages
/// whose entries are unpacked from the archive as they are read. Nothing here writes.
/// </summary>
public static class CflArchives
{
    /// <summary>True when the path names a CFL archive by extension (the reader checks the header and footer).</summary>
    public static bool IsCflArchive(string? path) => CflArchive.HasExtension(path);

    /// <summary>How much the open may unpack to check the entries, as a multiple of the file's size (the stock archive unpacks to about twice its size).</summary>
    public const int CheckBudgetRatio = 64;

    /// <summary>The least the open may unpack to check the entries, however small the file (64 MiB).</summary>
    public const long MinCheckBudget = 64L << 20;

    /// <summary>The most the open may unpack to check the entries, however large the file (1 GiB).</summary>
    public const long MaxCheckBudget = 1L << 30;

    /// <summary>The largest file read into memory in one go to open it (a larger one is read in place, in file order).</summary>
    private const long MaxInMemoryBytes = 256L << 20;

    /// <summary>
    /// The most the open unpacks to check the entries of a file of <paramref name="fileLength"/> bytes:
    /// <see cref="CheckBudgetRatio"/> times its size, between <see cref="MinCheckBudget"/> and <see cref="MaxCheckBudget"/>.
    /// </summary>
    public static long CheckBudgetFor(long fileLength) => Math.Clamp(fileLength * CheckBudgetRatio, MinCheckBudget, MaxCheckBudget);

    /// <summary>
    /// Opens an archive as a read-only package, one entry per directory record (a name listed twice is listed twice).
    /// Every entry is unpacked once to check it (a data block that several records name, once); those that cannot be
    /// read stay listed, with the reason. The checks stop at <see cref="CheckBudgetFor"/> bytes unpacked in all: the
    /// entries left are listed as not checked and are checked when read.
    /// </summary>
    /// <exception cref="AssetFormatException">The file is not a CFL3 archive, or is damaged.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static VppPackage Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        var stamp = VppArchiveStamp.Of(full);
        using var stream = OpenToCheck(full);
        var archive = CflArchive.Read(stream, Path.GetFileName(full));
        var entries = archive.Entries;
        var groups = entries.Select((e, i) => (e, i)).GroupBy(x => x.e.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();

        // Each data block is checked once (records may share one, and a shared block is not unpacked again), in file
        // order, until the budget is spent. The copies of a name listed twice are hashed as they are checked, so they
        // are compared without being read again or held.
        var hashed = groups.SelectMany(g => g).Select(x => BlockOf(x.e)).ToHashSet();
        var checks = new Dictionary<(long, uint, uint), (string? Problem, byte[]? Hash)>();
        var problems = new Dictionary<int, string>();
        var hashes = new byte[]?[entries.Count];
        var notChecked = new List<int>();
        long budget = CheckBudgetFor(stream.Length), spent = 0;
        bool over = false;
        foreach (int i in Enumerable.Range(0, entries.Count).OrderBy(i => entries[i].Offset))
        {
            var e = entries[i];
            var block = BlockOf(e);
            if (!checks.TryGetValue(block, out var check))
            {
                over = over || e.MaxUnpackBytes > budget - spent;
                if (over) { notChecked.Add(i); continue; }
                spent += e.MaxUnpackBytes;
                using var hash = hashed.Contains(block) ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                string? problem = archive.Check(stream, e, hash);
                checks[block] = check = (problem, problem is null ? hash?.GetHashAndReset() : null);
            }
            if (check.Problem is not null) problems[i] = check.Problem;
            hashes[i] = check.Hash;
        }
        notChecked.Sort();

        var skipped = notChecked.ToHashSet();
        var items = ImmutableArray.CreateBuilder<VppItem>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var source = new CflEntrySource(full, archive, e, problems.GetValueOrDefault(i)) { NotChecked = skipped.Contains(i) };
            items.Add(new VppItem(e.Name, source, VppItemState.Original) { OriginalName = e.Name, OriginalIndex = i });
        }
        var twice = new List<CflNameListedTwice>();
        foreach (var g in groups)
        {
            // the same data when every copy was checked, reads, and hashes the same
            byte[]? first = hashes[g.First().i];
            bool same = first is not null && g.All(x => hashes[x.i] is { } h && h.AsSpan().SequenceEqual(first));
            twice.Add(new CflNameListedTwice(string.Join(" / ", g.Select(x => x.e.Name).Distinct(StringComparer.Ordinal)), g.First().e.Name, g.Count(), same)
            {
                NotChecked = g.Any(x => skipped.Contains(x.i)),
            });
        }
        int count = items.Count;
        return new VppPackage(full, items.MoveToImmutable(), stamp)
        {
            OriginalCount = count,
            Cfl = new CflArchiveInfo(archive, full, problems, twice) { NotChecked = notChecked, CheckBudget = budget },
        };
    }

    /// <summary>A data block as an entry reads it: where it is, and the compression and size it is unpacked with.</summary>
    private static (long, uint, uint) BlockOf(CflEntry e) => (e.Offset, e.Compression, e.UnpackedSize);

    /// <summary>
    /// The file to check: read into memory in one sequential read (the open visits every entry, and one read per
    /// entry costs a disk seek each when the file is not cached), or, when larger than <see cref="MaxInMemoryBytes"/>,
    /// the file itself with a small buffer (the entries are then visited in file order).
    /// </summary>
    private static Stream OpenToCheck(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (file.Length > MaxInMemoryBytes) return file;
        using (file)
        {
            var bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            return new MemoryStream(bytes, writable: false);
        }
    }

    /// <summary>Problem codes of CFL archives (they replace the RF1 packfile rules, which do not apply).</summary>
    public static IReadOnlyDictionary<string, string> Rules { get; } = new Dictionary<string, string>
    {
        ["CFL001"] = "Compression not supported",
        ["CFL002"] = "Corrupt entry",
        ["CFL003"] = "Name listed twice",
        ["CFL004"] = "Empty entry",
        ["CFL005"] = "Entries not checked",
    };

    /// <summary>
    /// The facts worth flagging in a CFL archive: entries with a compression Cairn does not read and entries whose data
    /// does not unpack to their listed size (warnings: they cannot be extracted), names listed twice (a warning when the
    /// copies differ, since which one the game reads is not known; else information), empty entries (information) and
    /// entries the open did not check (a warning: the archive lists more data than the check budget). The RF1 rules
    /// (name lengths, types the game loads) do not apply.
    /// </summary>
    public static IReadOnlyList<VppProblem> Validate(VppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Cfl is not { } info) return [];
        var problems = new List<VppProblem>();
        foreach (var item in package.Items)
        {
            if (info.EntryOf(item) is not { } entry) continue;
            if (!entry.IsSupported)
                problems.Add(new("CFL001", VppSeverity.Warning, $"{item.Name} uses compression {entry.Compression}, which is not supported (a CFL3 plug-in's compressor or cipher); it cannot be read.", item.Name));
            else if (info.Problems.TryGetValue(item.OriginalIndex, out var why))
                problems.Add(new("CFL002", VppSeverity.Warning, $"{item.Name} is corrupt: {why}; it cannot be read.", item.Name));
            else if (item.Size == 0)
                problems.Add(new("CFL004", VppSeverity.Info, $"{item.Name} is an empty entry (0 bytes).", item.Name));
        }
        foreach (var d in info.ListedTwice)
        {
            problems.Add(new("CFL003", d.SameData ? VppSeverity.Info : VppSeverity.Warning, d.SameData
                ? $"{d.Text}: Cairn reads the first listed."
                : $"{d.Text}: which copy the game reads is not known; Cairn reads the first listed, and extracting both into one folder keeps both.", d.Name));
        }
        if (info.NotChecked.Count > 0)
        {
            int n = info.NotChecked.Count;
            string first = info.Archive.Entries[info.NotChecked[0]].Name;
            problems.Add(new("CFL005", VppSeverity.Warning, string.Format(CultureInfo.CurrentCulture,
                "{0} not checked when the archive opened ({1}{2}): checking every entry would unpack more than {3:N0} bytes, the most Cairn unpacks to check an archive of this size ({4} times its size, at least 64 MiB, at most 1 GiB). An archive that lists this much is usually damaged, or made to exhaust memory. Each such entry is unpacked and checked only when it is read.",
                n == 1 ? "1 entry was" : $"{n:N0} entries were", first, n > 1 ? " and others" : string.Empty, info.CheckBudget, CheckBudgetRatio), first));
        }
        return problems;
    }
}
