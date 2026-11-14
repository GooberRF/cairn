using System.Globalization;
using Cairn.Previews;
using Cairn.Ui.Modules;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Ui.Details;

/// <summary>
/// The details of the selected entry: entry-level rows (name, type, size, offset in the packfile, state,
/// source of added files), this entry's problems, and the facts from <see cref="VppFacts"/> grouped by
/// section, shown by the shared <see cref="AssetDetailsPane"/>.
/// </summary>
public sealed class VppDetailsPane : AssetDetailsPane
{
    public VppDetailsPane(IShellContext? shell) : base(shell) => BusyLabel = "vpp preview details";

    /// <summary>Shows the details of <paramref name="primary"/> (callers debounce).</summary>
    /// <param name="primary">The entry, or null.</param>
    /// <param name="selection">Every selected entry.</param>
    /// <param name="package">The packfile (offsets, problems, "in this packfile" checks).</param>
    /// <param name="problems">The packfile's current problems when the caller has them; computed when null.</param>
    public void Show(VppItem? primary, IReadOnlyList<VppItem> selection, VppPackage? package, IReadOnlyList<VppProblem>? problems = null)
    {
        if (IsDisposed) return;
        if (selection.Count > 1)
        {
            ShowDetails(Summary(selection, problems));
            return;
        }
        if (primary is null)
        {
            // a Red Faction II group shows what it says about itself until a file is selected
            if (package?.Rf2 is { } rf2) ShowDetails(GroupDetails(rf2, package));
            else if (package?.Cache is { } cache) ShowDetails(CacheDetails(cache, package));
            else if (package?.Cfl is { } cfl) ShowDetails(CflDetails(cfl, package));
            else ShowMessage("Select a file to see its details.");
            return;
        }
        var resolver = Shell?.Assets.Resolver;
        Show(ct => Build(primary, package, problems, resolver, ct));
    }

    /// <summary>The rows for one entry (pool thread).</summary>
    internal static AssetDetails Build(VppItem item, VppPackage? package, IReadOnlyList<VppProblem>? problems, Cairn.Assets.AssetResolver? resolver, CancellationToken ct)
    {
        var sheet = VppFacts.Describe(item, new VppFactsContext(package, resolver));
        ct.ThrowIfCancellationRequested();
        problems ??= package is null ? [] : VppValidator.Validate(package);
        var mine = problems.Where(p => p.EntryName is not null && string.Equals(p.EntryName, item.Name, StringComparison.OrdinalIgnoreCase)).ToList();

        const string entry = "Entry";
        var rows = new List<AssetDetailRow> { new(entry, "Name", item.Name) };
        if (item.IsRenamed) rows.Add(new(entry, "Original name", item.OriginalName!));
        if (package?.Cache is { } cache && cache.EntryOf(item) is { } stored)
        {
            if (stored.Name != item.Name) rows.Add(new(entry, "Stored name", stored.Name + " (the data's type is added to the listed name)"));
            rows.Add(new(entry, "Tag", string.Format(CultureInfo.InvariantCulture, "0x{0:X16} (the game's own value for what the data was made from)", stored.Tag)));
            rows.Add(new(entry, "CRC", string.Format(CultureInfo.InvariantCulture, "0x{0:X8} ({1})", stored.Crc,
                cache.Corrupt.Contains(item.OriginalIndex) ? "does not match the data: the entry is corrupt" : "matches the data")));
        }
        foreach (var label in new[] { "Type", "Size", "Game" })
            if (sheet[label] is { } value) rows.Add(new(entry, label, value));
        switch (item.Source)
        {
            case Cairn.Vpp.XForge.CflEntrySource c:
                var cv = CultureInfo.CurrentCulture;
                rows.Add(new(entry, "Compression", c.Problem is not null && c.Entry.IsSupported ? c.Entry.CompressionText + " (corrupt: " + c.Problem + ")" : c.Entry.CompressionText,
                    Flagged: c.Problem is not null));
                rows.Add(new(entry, "Packed size", string.Format(cv, "{0:N0} bytes", c.Entry.PackedSize)));
                rows.Add(new(entry, "Unpacked size", string.Format(cv, "{0:N0} bytes", c.Entry.UnpackedSize)));
                rows.Add(new(entry, "Offset", string.Format(cv, "{0:N0} (0x{0:X})", c.Entry.Offset)));
                break;
            case ArchiveSource a when package?.Path is { } path && string.Equals(Path.GetFullPath(a.ArchivePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase):
                rows.Add(new(entry, "Offset", string.Format(CultureInfo.CurrentCulture, "{0:N0} (0x{0:X})", a.Offset)));
                break;
            case ArchiveSource a when package?.Rf2?.PackfilePath is { } packfile && string.Equals(a.ArchivePath, packfile, StringComparison.OrdinalIgnoreCase):
                rows.Add(new(entry, "Offset", string.Format(CultureInfo.CurrentCulture, "{0:N0} (0x{0:X}) in {1}", a.Offset, Path.GetFileName(packfile))));
                break;
            case ArchiveSource a:
                rows.Add(new(entry, "Source", string.Format(CultureInfo.CurrentCulture, "{0}, offset {1:N0}", a.ArchivePath, a.Offset)));
                break;
            case FileSource f:
                rows.Add(new(entry, "Source", f.FilePath));
                break;
            case MissingFileSource m:
                rows.Add(new(entry, "Source", m.FilePath + " (not on disk)", Flagged: true));
                break;
            case MemorySource:
                rows.Add(new(entry, "Source", "in memory (not saved yet)"));
                break;
        }
        if (package is not { IsReadOnly: true }) rows.Add(new(entry, "State", item.State switch
        {
            VppItemState.Added => "Added (not saved yet)",
            VppItemState.Replaced => "Replaced (not saved yet)",
            VppItemState.Renamed => "Renamed (not saved yet)",
            _ => item.IsRenamed ? "Renamed (not saved yet)" : "Unchanged",
        }));
        if (mine.Count > 0)
        {
            rows.Add(new(entry, "Problems", string.Join(Environment.NewLine, mine.Select(p => $"{p.Code} {p.Severity}: {p.Message}")),
                Flagged: mine.Any(p => p.Severity != VppSeverity.Info)));
        }

        rows.AddRange(FactRows(item.Name, sheet));
        var warnings = sheet.Warnings.ToList();
        warnings.AddRange(mine.Where(p => p.Severity != VppSeverity.Info).Select(p => p.Message));
        return new AssetDetails(item.Name, rows, warnings);
    }

    /// <summary>The type-specific rows of a fact sheet (everything but type, size and game), grouped by section.</summary>
    internal static List<AssetDetailRow> FactRows(string name, VppFactSheet sheet)
    {
        var rows = new List<AssetDetailRow>();
        string factsSection = sheet["Type"] is not null ? VppFileTypes.Describe(name).Category switch
        {
            VppFileCategory.Level => "Level",
            VppFileCategory.Other => "Contents",
            var c => c.ToString(),
        } : "Contents";
        var warnings = sheet.Warnings;
        foreach (var row in sheet.Rows)
        {
            if (row.Label is "Type" or "Size" or "Game") continue;
            string section = factsSection, label = row.Label;
            int colon = row.Label.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
            {
                section = row.Label[..colon];
                label = row.Label[(colon + 2)..];
            }
            bool flagged = row.Value.EndsWith("; missing", StringComparison.Ordinal) || row.Value.EndsWith("but missing", StringComparison.Ordinal)
                || (label == "Missing" && row.Value != "0") || row.Label == "Error" || row.Label == "Content"
                || warnings.Contains(row.Value);
            rows.Add(new AssetDetailRow(section, label, row.Value, flagged));
        }
        return rows;
    }

    /// <summary>A Red Faction II group's own facts (mode, flags raw, counts, types, where its data is).</summary>
    internal static AssetDetails GroupDetails(Cairn.Vpp.Rf2.Rf2GroupInfo rf2, VppPackage package)
    {
        var rows = new List<AssetDetailRow>();
        foreach (var (label, value) in rf2.Facts(package.Items))
        {
            bool type = label.StartsWith("Type ", StringComparison.Ordinal);
            rows.Add(new(type ? "Types" : "Packfile group", type ? label[5..] : label, value, Flagged: label == "Missing on disk" || label == "Size differs"));
        }
        return new AssetDetails(Path.GetFileName(rf2.TocPath), rows, []);
    }

    /// <summary>An Alpine Faction cache pack's own facts (format, write id, counts, types).</summary>
    internal static AssetDetails CacheDetails(Cairn.Vpp.Alpine.CachePackInfo cache, VppPackage package)
    {
        var rows = new List<AssetDetailRow>();
        foreach (var (label, value) in cache.Facts(package.Items))
        {
            bool type = label.StartsWith("Type ", StringComparison.Ordinal);
            rows.Add(new(type ? "Types" : "Cache pack", type ? label[5..] : label, value));
        }
        return new AssetDetails(Path.GetFileName(cache.PackPath), rows, []);
    }

    /// <summary>An X-Forge CFL archive's own facts (format, counts of each compression, sizes, types).</summary>
    internal static AssetDetails CflDetails(Cairn.Vpp.XForge.CflArchiveInfo cfl, VppPackage package)
    {
        var rows = new List<AssetDetailRow>();
        foreach (var (label, value) in cfl.Facts(package.Items))
        {
            bool type = label.StartsWith("Type ", StringComparison.Ordinal);
            rows.Add(new(type ? "Types" : "CFL archive", type ? label[5..] : label, value, Flagged: label is "Unsupported" or "Corrupt"));
        }
        return new AssetDetails(Path.GetFileName(cfl.ArchivePath), rows, []);
    }

    private static AssetDetails Summary(IReadOnlyList<VppItem> selection, IReadOnlyList<VppProblem>? problems)
    {
        var names = selection.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        problems ??= [];
        int withProblems = problems.Where(p => p.EntryName is not null && names.Contains(p.EntryName)).Select(p => p.EntryName!).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        const string s = "Selection";
        long total = selection.Sum(i => i.Size);
        var rows = new List<AssetDetailRow>
        {
            new(s, "Files", selection.Count.ToString("N0", CultureInfo.CurrentCulture)),
            new(s, "Total size", string.Format(CultureInfo.CurrentCulture, "{0} ({1:N0} bytes)", PreviewUi.Size(total), total)),
            new(s, "Changed", selection.Count(i => i.State != VppItemState.Original || i.IsRenamed).ToString("N0", CultureInfo.CurrentCulture)),
        };
        if (withProblems > 0) rows.Add(new(s, "With problems", withProblems.ToString("N0", CultureInfo.CurrentCulture), Flagged: true));
        foreach (var g in selection.GroupBy(i => VppFileTypes.Describe(i.Name).DisplayName).OrderByDescending(g => g.Count()))
            rows.Add(new("Types", g.Key, string.Format(CultureInfo.CurrentCulture, "{0:N0} ({1})", g.Count(), PreviewUi.Size(g.Sum(i => i.Size)))));
        return new AssetDetails(string.Format(CultureInfo.CurrentCulture, "{0:N0} files selected", selection.Count), rows, []);
    }
}
