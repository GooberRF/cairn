using System.Windows.Controls;
using Cairn.Ui.Modules;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;

namespace Cairn.Vpp.Ui;

// Other modules' batch converters (IArchiveBatchConverter, e.g. "Convert sounds...") in the Packfile menu and the entry
// list's context menu, and IArchiveEntryTarget: files another module made from a work copy go back into its packfile.
public sealed partial class VppModule : IArchiveEntryTarget
{
    private IEnumerable<IArchiveBatchConverter> BatchConverters =>
        Shell?.Modules.OfType<IArchiveBatchConverter>().Where(c => !ReferenceEquals(c, this)) ?? [];

    /// <summary>Adds a Packfile menu item per batch converter another module offers.</summary>
    private void InitializeBatchConverters()
    {
        if (_menus.FirstOrDefault(m => m.Slot == MenuSlot.TopLevel && m.Item is MenuItem { Name: "PackfileMenu" })?.Item is not MenuItem menu) return;
        foreach (var converter in BatchConverters)
        {
            var item = new MenuItem
            {
                Header = converter.CommandText, ToolTip = converter.CommandToolTip,
                Command = Cmd(d => d.Commands.Fire(() => ConvertWithAsync(d, converter)), d => SelectedFor(d, converter).Count > 0),
            };
            // before the separator that precedes Validate, with the other converters
            menu.Items.Insert(Math.Max(menu.Items.Count - 2, 0), item);
        }
    }

    /// <summary>Context-menu items for the batch converters (enabled when the selection holds an entry they take).</summary>
    internal IReadOnlyList<MenuItem> BatchConverterMenuItems(VppDocument doc, ContextMenu menu)
    {
        var items = new List<MenuItem>();
        foreach (var converter in BatchConverters)
        {
            var item = new MenuItem { Header = converter.CommandText, ToolTip = converter.CommandToolTip };
            item.Click += (_, _) => doc.Commands.Fire(() => ConvertWithAsync(doc, converter));
            menu.Opened += (_, _) => item.IsEnabled = !doc.IsBusy && SelectedFor(doc, converter).Count > 0;
            items.Add(item);
        }
        return items;
    }

    private static List<VppItem> SelectedFor(VppDocument doc, IArchiveBatchConverter converter) =>
        [.. doc.SelectedItems.Where(i => converter.CanConvert(i.Name))];

    /// <summary>
    /// Runs <paramref name="converter"/> on the selected entries it takes: its dialog, then the work with this packfile's
    /// progress bar and Cancel; files it adds become ONE undo step. Returns the converter's summary, or null.
    /// </summary>
    public async Task<string?> ConvertWithAsync(VppDocument doc, IArchiveBatchConverter converter, bool interactive = true, object? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(converter);
        var items = SelectedFor(doc, converter);
        if (doc.IsBusy || items.Count == 0 || !doc.EnsureReadable(items)) return null;
        var request = new ArchiveBatchRequest(doc.DisplayName, doc.Folder, [.. items.Select(i => new ArchiveBatchEntry(i.Name, i.Size, () => i.Source.ReadAll()))])
        {
            Interactive = interactive,
            Options = options,
            RunAsync = (title, work) => doc.RunOperationAsync(title, op =>
                work(new Progress<(double Fraction, string Item)>(p => op.Report(p.Fraction, p.Item)), op.Token)),
            AddFiles = (label, files, replace) => AddToPackfile(doc, label, files, replace) ?? [],
            AllEntries = [.. doc.Current.Items.Select(i => new ArchiveBatchEntry(i.Name, i.Size, () => i.Source.ReadAll()))],
        };
        string? summary = await converter.ConvertAsync(request);
        if (summary is not null) doc.Notice = summary;
        return summary;
    }

    /// <summary>Adds in-memory files to <paramref name="doc"/> as one undo step and selects them; null when refused.</summary>
    private static IReadOnlyList<string>? AddToPackfile(VppDocument doc, string label, IReadOnlyList<(string Name, byte[] Bytes)> files, bool replace)
    {
        if (files.Count == 0) return [];
        VppAddReport? report = null;
        bool changed = doc.ApplyEdit(label, p =>
        {
            var result = VppEdit.AddSources(p, files.Select(f => (f.Name, (VppSource)new MemorySource(f.Bytes))), replace ? VppClashPolicy.Replace : VppClashPolicy.KeepBoth);
            report = result.Report;
            return result.Package;
        });
        if (!changed || report is null) return null;
        var names = report.Added.Concat(report.Replaced).Concat(report.Renamed.Select(r => r.Used)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        doc.SelectNames(names);
        return names;
    }

    /// <inheritdoc/>
    public string? ArchiveOf(string path) => DocumentOfWorkCopy(path)?.DisplayName;

    /// <inheritdoc/>
    public IReadOnlyList<string>? EntryNamesOf(string path) => DocumentOfWorkCopy(path)?.Current.Items.Select(i => i.Name).ToList();

    /// <inheritdoc/>
    public IReadOnlyList<string>? AddFiles(string path, string undoLabel, IReadOnlyList<(string Name, byte[] Bytes)> files, bool replace)
    {
        if (DocumentOfWorkCopy(path) is not { } doc)
        {
            Shell.Dialogs.ShowError("The packfile is not open", "The packfile this file came from is no longer open in Cairn.");
            return null;
        }
        if (doc.IsBusy)
        {
            Shell.Dialogs.ShowError($"{doc.DisplayName} is busy", "Wait for the packfile's current operation to finish, then try again.");
            return null;
        }
        var names = AddToPackfile(doc, undoLabel, files, replace);
        if (names is null) Shell.Dialogs.ShowError($"Could not add to {doc.DisplayName}", "The packfile cannot be changed now (reload it first if it changed on disk).");
        else doc.ShowStatus(undoLabel + " (save the packfile to keep it)");
        return names;
    }

    private VppDocument? DocumentOfWorkCopy(string path)
    {
        if (string.IsNullOrEmpty(path) || Shell is null) return null;
        return Shell.Documents.OfType<VppDocument>().FirstOrDefault(d => d.HasWorkFolder && d.Work.EntryOf(path) is not null);
    }
}
