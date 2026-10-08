using System.Globalization;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Ui.Views.Dialogs;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// Converting the meshes Cairn only reads (.v3d, .vcm, .rfm, .rfc) to the PC formats: one open tab ("Convert..." on
/// its banner or the File > Export menu) or a batch of packfile entries (the packfile's "Convert meshes..."). The
/// files go next to the source, into a folder, or into the packfile as new entries in one undo step; a report says
/// what was approximated.
/// </summary>
public sealed class LegacyMeshTools(RfaWorkspace workspace)
{
    private const string TargetKey = "legacyConvert.target", FolderKey = "legacyConvert.folder", ReplaceKey = "legacyConvert.replace";

    private IShellContext? Host => workspace.Host;

    private ModuleSettings Settings => new(workspace.Settings, "rfa");

    /// <summary>The choice the Convert window starts from (the last one used).</summary>
    public LegacyConvertChoice Defaults => new(
        Enum.TryParse<LegacyConvertTarget>(Settings.Get<string>(TargetKey), out var target) ? target : LegacyConvertTarget.NextToSource,
        Settings.Get<string>(FolderKey),
        Settings.Get<bool>(ReplaceKey));

    private void Remember(LegacyConvertChoice choice)
    {
        if (workspace.IsDiagnosticRun) return;
        Settings.Set(TargetKey, choice.Target.ToString());
        Settings.Set(ReplaceKey, choice.Replace);
        if (choice.Target == LegacyConvertTarget.Folder && choice.Folder is { } folder) Settings.Set(FolderKey, folder);
    }

    private IArchiveEntryTarget? ArchiveTarget => Host?.Modules.OfType<IArchiveEntryTarget>().FirstOrDefault();

    /// <summary>The packfile a tab's work copy came from (its name), or null.</summary>
    public string? PackfileOf(MeshDocumentViewModel doc) => doc.FilePath is { } path ? ArchiveTarget?.ArchiveOf(path) : null;

    /// <summary>
    /// The folder "next to the source" means: the file's own, or for a packfile entry the packfile's; null when none,
    /// or when that is the game directory (a loose mesh there changes what the game loads; the user picks a folder).
    /// </summary>
    public string? NextToFolder(MeshDocumentViewModel doc)
    {
        string? folder = SourceFolder(doc);
        return folder is not null && workspace.IsGameDirectory(folder) ? null : folder;
    }

    /// <summary>Why <see cref="NextToFolder"/> is null, for the Convert window.</summary>
    private string NoNextToReason(string? sourceFolder) =>
        sourceFolder is not null && workspace.IsGameDirectory(sourceFolder) ? "that is the game directory" : "it has no folder";

    private string? SourceFolder(MeshDocumentViewModel doc)
    {
        if (doc.FilePath is not { } path) return null;
        foreach (var provider in Host?.Modules.OfType<IWorkCopyProvider>() ?? [])
        {
            if (!provider.IsWorkCopy(path)) continue;
            return provider.ArchiveEntryOf(path) is { } entry ? Path.GetDirectoryName(entry.ArchivePath) : null;
        }
        return Path.GetDirectoryName(path);
    }

    /// <summary>
    /// Reads a file beside the tab's source by name (the rest of its packfile for a packfile entry, else its folder),
    /// so a PS2 mesh converts from its exporter twin when that is there.
    /// </summary>
    public Func<string, byte[]?> SiblingReader(MeshDocumentViewModel doc)
    {
        string? path = doc.FilePath;
        var siblings = path is null ? null : Host?.Modules.OfType<IAssetSiblingsProvider>().Select(p => p.SiblingsFor(path)).FirstOrDefault(s => s is not null);
        return name =>
        {
            if (siblings is not null) return siblings.Read(name);
            if (path is null || Path.GetDirectoryName(path) is not { } folder) return null;
            string candidate = Path.Combine(folder, name);
            return File.Exists(candidate) ? File.ReadAllBytes(candidate) : null;
        };
    }

    /// <summary>The conversion of a tab's mesh (its twin's for a PS2 mesh when one is beside it).</summary>
    /// <exception cref="AssetFormatException">It cannot be converted; the message says why.</exception>
    public LegacyMeshConversion Convert(MeshDocumentViewModel doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Legacy is not { } legacy) throw new AssetFormatException($"{doc.DisplayName} is not an exporter or PS2 mesh.");
        return LegacyMeshSupport.Convert(legacy.Bytes, doc.DisplayName, SiblingReader(doc));
    }

    /// <summary>"Convert..." on a tab: the window, then the conversion.</summary>
    public void ConvertDocument(MeshDocumentViewModel doc) => _ = ConvertDocumentAsync(doc);

    /// <summary>
    /// Converts <paramref name="doc"/>'s mesh with <paramref name="choice"/>, or with what the Convert window settles on
    /// when null (self-tests pass a choice). Returns the names or paths written, or null when cancelled or failed.
    /// </summary>
    public Task<IReadOnlyList<string>?> ConvertDocumentAsync(MeshDocumentViewModel doc, LegacyConvertChoice? choice = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        LegacyMeshConversion conversion;
        try
        {
            conversion = Convert(doc);
        }
        catch (AssetFormatException ex)
        {
            workspace.Dialogs.ShowError($"Could not convert {doc.DisplayName}", ex.Message);
            return Task.FromResult<IReadOnlyList<string>?>(null);
        }
        string? packfile = PackfileOf(doc), nextTo = NextToFolder(doc);
        if (choice is null)
        {
            if (workspace.IsDiagnosticRun || Host is null) return Task.FromResult<IReadOnlyList<string>?>(null);
            var window = new LegacyMeshConvertWindow(workspace.Dialogs, [new LegacyConvertItem(doc.DisplayName, conversion.OutputName, conversion.Report)], packfile, nextTo, Defaults,
                NoNextToReason(SourceFolder(doc)))
            {
                Owner = Host.MainWindow,
            };
            if (window.ShowDialog() != true || window.Result is not { } chosen) return Task.FromResult<IReadOnlyList<string>?>(null);
            Remember(chosen);
            choice = chosen;
        }

        IReadOnlyList<string>? written;
        if (choice.Target == LegacyConvertTarget.IntoPackfile && doc.FilePath is { } path && ArchiveTarget is { } target)
        {
            written = target.AddFiles(path, $"Convert {doc.DisplayName} to {Path.GetExtension(conversion.OutputName)}", [(conversion.OutputName, conversion.Bytes)], choice.Replace);
            if (written is { Count: > 0 }) doc.ShowStatus($"Added {written[0]} to {packfile} (undo in the packfile's tab; save the packfile to keep it)");
        }
        else
        {
            if ((choice.Folder ?? nextTo) is not { Length: > 0 } folder)
            {
                workspace.Dialogs.ShowError($"Could not convert {doc.DisplayName}",
                    $"Next to the source is not available ({NoNextToReason(SourceFolder(doc))}). Convert again and choose a folder.");
                return Task.FromResult<IReadOnlyList<string>?>(null);
            }
            written = WriteFiles(folder, [conversion], choice.Replace, out var errors);
            if (errors.Count > 0) workspace.Dialogs.ShowError($"Could not write {conversion.OutputName}", string.Join(Environment.NewLine, errors));
            else if (written.Count > 0) doc.ShowStatus($"Wrote {Path.GetFileName(written[0])} to {Path.GetDirectoryName(written[0])}");
        }
        return Task.FromResult(written);
    }

    /// <summary>Writes the converted files into <paramref name="folder"/> (a free name for each taken one unless replacing).</summary>
    internal static IReadOnlyList<string> WriteFiles(string folder, IReadOnlyList<LegacyMeshConversion> conversions, bool replace, out List<string> errors)
    {
        errors = [];
        var written = new List<string>();
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errors.Add($"{folder}: {ex.Message}");
            return written;
        }
        foreach (var c in conversions)
        {
            string path = Path.Combine(folder, c.OutputName);
            if (!replace)
            {
                string stem = Path.GetFileNameWithoutExtension(c.OutputName), ext = Path.GetExtension(c.OutputName);
                for (int n = 2; File.Exists(path) || written.Contains(path, StringComparer.OrdinalIgnoreCase); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
            }
            try
            {
                AtomicFile.WriteAllBytes(path, c.Bytes);
                written.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{c.OutputName}: {ex.Message}");
            }
        }
        return written;
    }

    // ── a batch of packfile entries ──────────────────────────────────────────

    /// <summary>The result of converting a batch (the summary line, the conversions and why the others failed).</summary>
    /// <param name="Summary">One line.</param>
    /// <param name="Converted">The conversions written.</param>
    /// <param name="Failed">"name: why" for each mesh that could not be converted.</param>
    public sealed record BatchResult(string Summary, IReadOnlyList<LegacyMeshConversion> Converted, IReadOnlyList<string> Failed);

    /// <summary>The last batch's result (self-tests).</summary>
    internal BatchResult? LastBatch { get; private set; }

    /// <summary>
    /// The packfile's "Convert meshes...": the selected exporter and PS2 meshes, with a PS2 mesh converting from its
    /// exporter twin when the packfile holds it, selected or not (a selected twin's own entry makes the same file once).
    /// </summary>
    public async Task<string?> ConvertBatchAsync(ArchiveBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entries = request.Entries.Where(e => LegacyMeshSupport.IsLegacyName(e.Name)).ToList();
        if (entries.Count == 0) return null;
        string? packfile = request.AddFiles is null ? null : request.ArchiveName;
        // never the game directory by default: a loose mesh there changes what the game loads
        string? nextTo = request.ArchiveFolder is { } archiveFolder && !workspace.IsGameDirectory(archiveFolder) ? archiveFolder : null;
        var choice = request.Options as LegacyConvertChoice;
        if (choice is null && request.Interactive && !workspace.IsDiagnosticRun && Host is not null)
        {
            var items = entries.Select(e => new LegacyConvertItem(e.Name, Path.ChangeExtension(e.Name, OutputExtensionByName(e.Name)), null)).ToList();
            var window = new LegacyMeshConvertWindow(workspace.Dialogs, items, packfile, nextTo, Defaults, NoNextToReason(request.ArchiveFolder)) { Owner = Host.MainWindow };
            if (window.ShowDialog() != true || window.Result is not { } chosen) return null;
            Remember(chosen);
            choice = chosen;
        }
        var defaults = Defaults;
        choice ??= new LegacyConvertChoice(packfile is not null ? LegacyConvertTarget.IntoPackfile : LegacyConvertTarget.Folder,
            nextTo ?? workspace.DefaultOutputFolder(defaults.Folder), defaults.Replace);

        var converted = new List<LegacyMeshConversion>();
        var failed = new List<string>();
        Task Work(IProgress<(double Fraction, string Item)> progress, CancellationToken ct) => Task.Run(() =>
        {
            // A PS2 mesh's exporter twin is read from the whole packfile, selected or not (the first entry of a name).
            var byName = new Dictionary<string, ArchiveBatchEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in request.AllEntries.Concat(entries)) byName.TryAdd(e.Name, e);
            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Exporter files first: a PS2 mesh whose twin is selected too is made once, from the twin.
            foreach (var (entry, i) in entries.OrderBy(e => e.Name.EndsWith(".rfm", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase)).Select((e, i) => (e, i)))
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(((double)i / entries.Count, entry.Name));
                try
                {
                    byte[] bytes = entry.Read();
                    var conversion = LegacyMeshSupport.Convert(bytes, entry.Name, name => byName.TryGetValue(name, out var twin) ? twin.Read() : null);
                    if (!outputs.Add(conversion.OutputName))
                    {
                        if (conversion.TwinName is null) failed.Add($"{entry.Name}: another selected mesh makes {conversion.OutputName} too; left out");
                        continue;
                    }
                    converted.Add(conversion);
                }
                catch (Exception ex) when (ex is AssetFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    failed.Add($"{entry.Name}: {ex.Message}");
                }
            }
            progress.Report((1, "done"));
        }, ct);
        bool finished;
        if (request.RunAsync is { } run) finished = await run("Converting meshes", Work);
        else { await Work(new Progress<(double, string)>(), CancellationToken.None); finished = true; }
        if (!finished) return null;

        string where;
        var errors = new List<string>();
        if (choice.Target == LegacyConvertTarget.IntoPackfile && request.AddFiles is { } add)
        {
            string label = converted.Count == 1 ? $"Convert {converted[0].SourceName} to {Path.GetExtension(converted[0].OutputName)}" : $"Convert {converted.Count:N0} meshes";
            var names = converted.Count == 0 ? [] : add(label, [.. converted.Select(c => (c.OutputName, c.Bytes))], choice.Replace);
            where = string.Format(CultureInfo.CurrentCulture, " ({0:N0} added to {1}; Undo removes them)", names.Count, request.ArchiveName);
        }
        else
        {
            string folder = choice.Folder ?? nextTo ?? workspace.DefaultOutputFolder(defaults.Folder);
            var written = WriteFiles(folder, converted, choice.Replace, out errors);
            where = string.Format(CultureInfo.CurrentCulture, " ({0:N0} written to {1})", written.Count, folder);
        }
        int approximated = converted.Count(c => !c.IsLossless);
        string summary = string.Format(CultureInfo.CurrentCulture, "Converted {0:N0} of {1:N0} meshes", converted.Count, entries.Count)
            + (approximated > 0 ? string.Format(CultureInfo.CurrentCulture, ", {0:N0} with notes", approximated) : "")
            + where + ".";
        LastBatch = new BatchResult(summary, converted, [.. failed, .. errors]);
        if (request.Interactive && !workspace.IsDiagnosticRun) ShowReport(summary, converted, [.. failed, .. errors]);
        return summary;
    }

    private static string OutputExtensionByName(string name) =>
        name.EndsWith(".vcm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase) ? ".v3c" : ".v3m";

    /// <summary>The batch report: failures first, then each remark with the meshes it concerns.</summary>
    private void ShowReport(string summary, IReadOnlyList<LegacyMeshConversion> converted, IReadOnlyList<string> failed)
    {
        var lines = failed.Select(f => "• " + f).ToList();
        var notes = converted.SelectMany(c => c.Report.Select(n => (Key: n.Replace(Path.GetFileName(c.OutputName), "the mesh", StringComparison.Ordinal), c.SourceName)))
            .GroupBy(n => n.Key).Select(g => g.Count() == 1 ? $"• {g.First().SourceName}: {g.Key}" : $"• {g.Key} ({g.Count():N0} meshes)").ToList();
        string text = summary + (lines.Count > 0 ? "\n\nNot converted:\n" + string.Join("\n", lines.Take(10)) + (lines.Count > 10 ? $"\n... and {lines.Count - 10:N0} more" : "") : "")
            + (notes.Count > 0 ? "\n\nWhat the conversion changed:\n" + string.Join("\n", notes.Take(14)) + (notes.Count > 14 ? $"\n... and {notes.Count - 14:N0} more" : "") : "");
        workspace.Dialogs.Choose("Meshes converted", text, ["OK"], 0);
    }
}
