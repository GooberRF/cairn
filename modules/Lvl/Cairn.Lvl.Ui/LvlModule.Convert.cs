using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cairn.Assets;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Ui.Dialogs;
using Cairn.Lvl.Ui.Documents;
using Cairn.Lvl.Ui.Preview;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;

namespace Cairn.Lvl.Ui;

public sealed partial class LvlModule
{
    // ── targets (IAssetConverter) ─────────────────────────────────────────────────────────────────────────────

    private static readonly Lazy<bool> RfgWriter = new(() =>
    {
        // The group-file writer fills a hook in the converters; without it the target reports itself unavailable.
        try
        {
            var probe = LvlConverter.Convert(LvlConvertInput.FromGroupFile(new RfGroupFile { Name = "probe.rfg" }), new LvlConvertOptions { Target = LvlConvertTarget.Rfg });
            return !probe.Report.WithCode(LvlConvertCodes.TargetUnavailable).Any();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    });

    /// <summary>True when this version writes group files (.rfg), so a level can be converted to one.</summary>
    internal static bool RfgAvailable => RfgWriter.Value;

    /// <summary>
    /// glTF (.gltf + .bin), glTF binary (.glb), OBJ and static mesh (.v3m) for levels and group files; a group file
    /// (.rfg) for levels, meshes (.v3m, .v3c), glTF and OBJ files when this version writes them.
    /// </summary>
    public IReadOnlyList<ConversionTarget> TargetsFor(string sourceName)
    {
        if (IsMeshSource(sourceName)) return RfgAvailable ? [MeshRfgTarget(sourceName)] : [];
        // an .xff is a level only when its content says so (models and characters are the meshes module's)
        if (!LvlSource.Handles(sourceName) || LvlSource.IsXffName(sourceName)) return [];
        return LevelTargets(sourceName);
    }

    /// <summary>The targets of <paramref name="sourceName"/> knowing its content: an <c>.xff</c> gets the level targets only when it is a level.</summary>
    public IReadOnlyList<ConversionTarget> TargetsFor(string sourceName, ReadOnlySpan<byte> head) =>
        LvlSource.IsXffName(sourceName) ? LvlSource.IsXffLevel(head, sourceName) ? LevelTargets(sourceName) : [] : TargetsFor(sourceName);

    /// <summary>An <c>.xff</c> needs its whole content to tell a level from a model.</summary>
    public int HeadLengthFor(string sourceName) => LvlSource.IsXffName(sourceName) ? ContentClaims.WholeFile : ContentClaims.DefaultHeadLength;

    private static List<ConversionTarget> LevelTargets(string sourceName)
    {
        var targets = new List<ConversionTarget>
        {
            new(".gltf", "glTF (.gltf + .bin)", "glTF 2.0 with the textures as PNG beside it; brushes, flags, groups and objects are kept in its extras", false),
            new(".glb", "glTF binary (.glb)", "glTF 2.0 in one file, textures inside; brushes, flags, groups and objects are kept in its extras", false),
            new(".obj", "Wavefront OBJ (.obj + .mtl)", "Geometry and textures for other 3D tools (objects and most flags are dropped)", true),
            new(".v3m", "Static mesh (.v3m)", "A Red Faction static mesh of the level's geometry (objects and flags are dropped)", true),
        };
        if ((LvlSource.IsLevelName(sourceName) || LvlSource.IsXffName(sourceName)) && RfgAvailable)
            targets.Add(new(".rfg", "Group file (.rfg)", "The level's brushes and objects as an editor group file (the compiled geometry and the level's settings are dropped)", true));
        return targets;
    }

    public bool CanConvert(string sourceName, string targetExtension) =>
        IsMeshSource(sourceName)
            ? LvlConvertOptions.TargetOf(targetExtension) == LvlConvertTarget.Rfg && RfgAvailable
            : LvlSource.Handles(sourceName) && LvlConvertOptions.TargetOf(targetExtension) is { } target
                && (target != LvlConvertTarget.Rfg || ((LvlSource.IsLevelName(sourceName) || LvlSource.IsXffName(sourceName)) && RfgAvailable));

    /// <summary>
    /// A level tab through its Convert window (starting at the chosen target); a packfile entry the same way, reading the
    /// entry. Without a dialog: the chosen target with the remembered options, into the packfile when the request can
    /// add to it and names no folder, else into <see cref="AssetConversionRequest.OutputFolder"/>. Returns the main file
    /// or entry written.
    /// </summary>
    public async Task<string?> ConvertAsync(AssetConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanConvert(request.FileName, request.TargetExtension)) return null;
        var target = LvlConvertOptions.TargetOf(request.TargetExtension)!.Value;
        LvlConvertChoice? choice = request.Interactive ? null
            : new LvlConvertChoice(Settings.Options with { Target = target },
                request.AddToArchive is not null && request.OutputFolder is null ? LvlOutputPlace.IntoPackfile : LvlOutputPlace.Folder,
                request.OutputFolder ?? DefaultFolder(), Replace: false) { Import = Settings.RfgImport };
        if (IsMeshSource(request.FileName)) return await ConvertMeshAsync(request, choice);
        if (request.Document is LvlDocument doc) return (await ConvertDocumentAsync(doc, choice, target))?.FirstOrDefault();

        var bytes = request.Bytes;
        string name = request.FileName;
        string? archive = request.ArchivePath;
        IAssetSiblings? siblings = request.ReadSibling is { } read ? new LvlEntrySiblings(archive is null ? name : Path.GetFileName(archive), request.ArchiveEntryNames, read) : null;
        string? filePath = siblings is null ? request.FilePath : null;
        string? install = LvlTexturePacks.Rf2InstallOf(archive ?? request.FilePath);
        var under = base.Shell.Assets.ResolverFor(archive is null ? request.FilePath is { } p ? Path.GetDirectoryName(p) : null : Path.GetDirectoryName(archive));
        string? packfile = request.AddToArchive is null ? null : archive is null ? "the packfile" : Path.GetFileName(archive);
        string? folder = archive is null ? request.FilePath is { } loose ? Path.GetDirectoryName(loose) : null : Path.GetDirectoryName(archive);
        string? nextTo = IsGameFolder(folder) ? null : folder;
        if (choice is null)
        {
            if (base.Shell.IsDiagnosticRun) return null;
            choice = AskChoice([name], packfile, nextTo, target, NoNextToReason(folder), LvlSource.IsLevelName(name) && LooksRf2(bytes));
            if (choice is null) return null;
        }
        var add = request.AddToArchive is { } addToArchive ? (Func<string, IReadOnlyList<(string Name, byte[] Bytes)>, bool, IReadOnlyList<string>?>)addToArchive : null;
        var xff = LvlSource.IsXffName(name) ? XffReadOptions(FilesBeside(siblings, under)) : null;
        var job = new LvlJob(name, _ => LvlSource.Read(bytes, name, xff), source => TexturesFor(source, siblings, under, install, filePath))
        {
            Rf2 = rf2 => LvlTexturePacks.Complete(rf2, name, filePath, siblings, install),
        };
        var outcomes = await RunJobsAsync([job], choice, add, request.ArchiveEntryNames, packfile, nextTo, request.Interactive, null);
        return outcomes?.FirstOrDefault()?.Written.FirstOrDefault();
    }

    // ── one level (a tab) ─────────────────────────────────────────────────────────────────────────────────────

    private IArchiveEntryTarget? ArchiveTarget => base.Shell.Modules.OfType<IArchiveEntryTarget>().FirstOrDefault();

    /// <summary>The packfile a document's work copy came from (its name), or null.</summary>
    public string? PackfileOf(LvlDocument doc) => doc.FilePath is { } path ? ArchiveTarget?.ArchiveOf(path) : null;

    /// <summary>"Next to the source": the file's folder, or for a packfile entry the packfile's; null when none, or the game directory.</summary>
    public string? NextToFolder(LvlDocument doc)
    {
        string? folder = SourceFolder(doc);
        return IsGameFolder(folder) ? null : folder;
    }

    private string? SourceFolder(LvlDocument doc)
    {
        if (doc.FilePath is not { } path) return doc.ArchivePath is { } archive ? Path.GetDirectoryName(archive) : null;
        foreach (var provider in base.Shell.Modules.OfType<IWorkCopyProvider>())
        {
            if (!provider.IsWorkCopy(path)) continue;
            return provider.ArchiveEntryOf(path) is { } entry ? Path.GetDirectoryName(entry.ArchivePath) : null;
        }
        return Path.GetDirectoryName(path);
    }

    /// <summary>True when <paramref name="folder"/> is the game directory (or inside it): never a default output folder.</summary>
    internal bool IsGameFolder(string? folder) => GameDirectoryLocator.IsInGameDirectory(folder, base.Shell.Settings.GameDirectory);

    private string NoNextToReason(string? sourceFolder) => IsGameFolder(sourceFolder) ? "that is the game directory" : "it has no folder";

    /// <summary>Where outputs go when nothing else says: the remembered folder, else Documents (never the game directory).</summary>
    private string DefaultFolder() =>
        Settings.Folder is { Length: > 0 } f && !IsGameFolder(f) ? f : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>"Convert..." on a tab: the window, then the conversion and its report.</summary>
    internal void ConvertDocument(LvlDocument doc) => _ = ConvertDocumentAsync(doc, null, null);

    /// <summary>
    /// Converts <paramref name="doc"/> with <paramref name="choice"/>, or with what the Convert window settles on when null
    /// (starting at <paramref name="start"/>). Returns the files or entries written (the main one first), or null when
    /// cancelled or failed.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ConvertDocumentAsync(LvlDocument doc, LvlConvertChoice? choice = null, LvlConvertTarget? start = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        await doc.Loading;
        if (doc.Source is not { } source)
        {
            base.Shell.Dialogs.ShowError($"Could not convert {doc.DisplayName}", doc.LoadError ?? "The file could not be read.");
            return null;
        }
        string? packfile = PackfileOf(doc), nextTo = NextToFolder(doc);
        if (choice is null)
        {
            if (base.Shell.IsDiagnosticRun) return null;
            choice = AskChoice([doc.DisplayName], packfile, nextTo, start, NoNextToReason(SourceFolder(doc)), source.Level?.IsRf2 == true);
            if (choice is null) return null;
        }
        Func<string, IReadOnlyList<(string Name, byte[] Bytes)>, bool, IReadOnlyList<string>?>? add = null;
        IReadOnlyList<string> names = [];
        if (packfile is not null && doc.FilePath is { } path && ArchiveTarget is { } target)
        {
            add = (label, files, replace) => target.AddFiles(path, label, files, replace);
            names = target.EntryNamesOf(path) ?? [];
        }
        var resolver = ResolverFor(doc, source);
        var siblings = source.Level?.IsRf2 == true ? AssetSiblings.For(base.Shell, doc.FilePath) : null;
        string? filePath = siblings is null ? doc.FilePath : null, install = Rf2InstallOf(doc);
        var job = new LvlJob(doc.DisplayName, _ => source, _ => resolver)
        {
            Rf2 = rf2 => LvlTexturePacks.Complete(rf2, doc.DisplayName, filePath, siblings, install),
        };
        var outcomes = await RunJobsAsync([job], choice, add, names, packfile, nextTo, interactive: !base.Shell.IsDiagnosticRun, null);
        var outcome = outcomes?.FirstOrDefault();
        if (outcome is { Succeeded: true }) doc.ShowStatus(choice.Place == LvlOutputPlace.IntoPackfile
            ? $"Added {outcome.Written[0]} to {packfile} (undo in the packfile's tab; save the packfile to keep it)"
            : $"Wrote {Path.GetFileName(outcome.Written[0])} to {Path.GetDirectoryName(outcome.Written[0])}");
        return outcome?.Succeeded == true ? outcome.Written : null;
    }

    private LvlConvertChoice? AskChoice(IReadOnlyList<string> names, string? packfile, string? nextTo, LvlConvertTarget? start, string noNextToReason, bool anyRf2)
    {
        var window = new LvlConvertWindow(base.Shell.Dialogs, names, packfile, nextTo, Settings, start, RfgAvailable, anyRf2, noNextToReason) { Owner = base.Shell.MainWindow };
        if (window.ShowDialog() != true || window.Result is not { } chosen) return null;
        Remember(chosen, meshes: names.All(IsMeshSource));
        return chosen;
    }

    /// <summary>
    /// The Red Faction II install a tab's level belongs to (see <see cref="LvlTexturePacks.Rf2InstallOf"/>): a work
    /// copy's packfile group, else its file or the packfile it came from. UI thread.
    /// </summary>
    internal string? Rf2InstallOf(LvlDocument doc)
    {
        string? path = doc.FilePath ?? doc.ArchivePath;
        if (doc.FilePath is { } file)
        {
            foreach (var provider in base.Shell.Modules.OfType<IWorkCopyProvider>())
            {
                if (!provider.IsWorkCopy(file)) continue;
                path = provider.ArchiveEntryOf(file)?.ArchivePath;
                break;
            }
        }
        return LvlTexturePacks.Rf2InstallOf(path);
    }

    private static bool LooksRf2(byte[] bytes) => bytes.Length >= 8 && BitConverter.ToInt32(bytes, 4) == Reading.RflLevelReader.Rf2Version;

    /// <summary>
    /// Remembers the choice for next time, except Replace (asked for each conversion); a conversion of meshes keeps the
    /// levels' options as they were.
    /// </summary>
    private void Remember(LvlConvertChoice choice, bool meshes)
    {
        if (base.Shell.IsDiagnosticRun) return;
        if (meshes) Settings.RfgImport = choice.Import;
        else Settings.Options = choice.Options;
        Settings.Place = choice.Place;
        if (choice.Place == LvlOutputPlace.Folder && choice.Folder is { } folder) Settings.Folder = folder;
    }

    // ── a batch from a packfile (IArchiveBatchConverter) ──────────────────────────────────────────────────────

    public string CommandText => "Convert _levels...";
    public string CommandToolTip => "Convert the selected levels and group files (.rfl, .rfg, N-Gage .xff levels) to glTF, OBJ or static meshes, or levels to group files: into the packfile as new entries (one undo step) or into a folder";

    public bool CanConvert(string entryName) => LvlSource.Handles(entryName);

    /// <summary>The selected levels with one set of options (the window, or <see cref="ArchiveBatchRequest.Options"/> when not interactive).</summary>
    public async Task<string?> ConvertAsync(ArchiveBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entries = request.Entries.Where(e => CanConvert(e.Name)).ToList();
        if (entries.Count == 0) return null;
        string? packfile = request.AddFiles is null ? null : request.ArchiveName;
        string? nextTo = IsGameFolder(request.ArchiveFolder) ? null : request.ArchiveFolder;
        var choice = request.Options as LvlConvertChoice;
        // the levels of a Red Faction II packfile group are all RF2 levels (no need to read them to know)
        bool rf2Group = request.ArchiveName.EndsWith(Cairn.Formats.Rf2.Rf2TocGroup.Extension, StringComparison.OrdinalIgnoreCase);
        if (choice is null && request.Interactive && !base.Shell.IsDiagnosticRun)
        {
            choice = AskChoice([.. entries.Select(e => e.Name)], packfile, nextTo, null, NoNextToReason(request.ArchiveFolder), rf2Group);
            if (choice is null) return null;
        }
        choice ??= new LvlConvertChoice(Settings.Options, packfile is not null ? LvlOutputPlace.IntoPackfile : LvlOutputPlace.Folder, nextTo ?? DefaultFolder(), Replace: false);
        // textures come from the packfile's own entries first
        var siblings = new LvlEntrySiblings(request.ArchiveName, [.. request.AllEntries.Select(e => e.Name)],
            name => request.AllEntries.FirstOrDefault(e => e.Name.Equals(Path.GetFileName(name), StringComparison.OrdinalIgnoreCase)) is { } entry ? SafeRead(entry) : null);
        var under = base.Shell.Assets.ResolverFor(request.ArchiveFolder);
        string? install = request.ArchiveFolder is { } archiveFolder ? LvlTexturePacks.Rf2InstallOf(Path.Combine(archiveFolder, request.ArchiveName)) : null;
        var xff = XffReadOptions(FilesBeside(siblings, under));
        var jobs = entries.Select(e => new LvlJob(e.Name, _ => LvlSource.Read(e.Read(), e.Name, LvlSource.IsXffName(e.Name) ? xff : null), source => TexturesFor(source, siblings, under, install))
        {
            Rf2 = rf2 => LvlTexturePacks.Complete(rf2, e.Name, null, siblings, install),
        }).ToList();
        var add = request.AddFiles is { } addFiles ? (label, files, replace) => addFiles(label, files, replace) : (Func<string, IReadOnlyList<(string Name, byte[] Bytes)>, bool, IReadOnlyList<string>?>?)null;
        var outcomes = await RunJobsAsync(jobs, choice, add, [.. request.AllEntries.Select(e => e.Name)], packfile, nextTo, request.Interactive, request.RunAsync);
        return outcomes is null ? null : Summary(outcomes, choice);
    }

    private static byte[]? SafeRead(ArchiveBatchEntry entry)
    {
        try { return entry.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException) { return null; }
    }

    /// <summary>The last conversion's outcomes, one per source with its report (self-tests, the shell's among them).</summary>
    public IReadOnlyList<LvlConvertOutcome>? LastOutcomes { get; private set; }

    // ── the conversion itself ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>One source to convert: its name, how to read it (worker thread) and where its textures are found.</summary>
    private sealed record LvlJob(string Name, Func<CancellationToken, LvlSource> Load, Func<LvlSource, AssetResolver?> Resolver)
    {
        /// <summary>Completes a Red Faction II level's options with what lies beside it (its texture packs, its install's clutter table). Worker thread.</summary>
        public Func<LvlRf2Options, LvlRf2Options>? Rf2 { get; init; }

        /// <summary>A source that is not a level or group file (a mesh, glTF or OBJ file): converted by this instead of <see cref="Load"/>. Worker thread.</summary>
        public Func<RfgImportOptions, CancellationToken, LvlConversion>? Direct { get; init; }
    }

    /// <summary>
    /// A packfile entry's (or loose file's) textures: the packfile's other entries first (a Red Faction II level's .peg
    /// packs among them, then its install's shared packs), then <paramref name="under"/>.
    /// </summary>
    private static AssetResolver? TexturesFor(LvlSource source, IAssetSiblings? siblings, AssetResolver under, string? rf2Install = null, string? filePath = null) =>
        source.Level?.IsRf2 == true ? LvlTexturePacks.ResolverFor(source, filePath, siblings, under, rf2Install) : siblings.Layer(under);

    /// <summary>
    /// Converts every job with <paramref name="choice"/> and writes the outputs: into the folder (a taken main name gets a
    /// free one unless Replace is ticked and confirmed), or through <paramref name="add"/> into the packfile as one undo
    /// step. Shows the report when interactive. Null when cancelled.
    /// </summary>
    private async Task<IReadOnlyList<LvlConvertOutcome>?> RunJobsAsync(IReadOnlyList<LvlJob> jobs, LvlConvertChoice choice,
        Func<string, IReadOnlyList<(string Name, byte[] Bytes)>, bool, IReadOnlyList<string>?>? add, IReadOnlyList<string> entryNames,
        string? packfile, string? nextTo, bool interactive, Func<string, Func<IProgress<(double Fraction, string Item)>, CancellationToken, Task>, Task<bool>>? run)
    {
        bool intoPackfile = choice.Place == LvlOutputPlace.IntoPackfile && add is not null;
        string folder = choice.Place == LvlOutputPlace.NextToSource ? nextTo ?? choice.Folder ?? DefaultFolder() : choice.Folder ?? DefaultFolder();
        bool ask = interactive && !base.Shell.IsDiagnosticRun;
        string ext = LvlConvertOptions.ExtensionOf(choice.Options.Target);
        var mains = jobs.Select(j => Path.GetFileNameWithoutExtension(j.Name) + ext).ToList();

        // Taken main names: replaced only when Replace is ticked and confirmed; else each output gets a free name.
        var existing = intoPackfile ? new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase) : null;
        bool Taken(string main) => existing?.Contains(main) ?? SafeExists(Path.Combine(folder, main));
        var taken = mains.Where(Taken).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool replace = false;
        if (taken.Count > 0 && choice.Replace)
        {
            if (!ask) replace = true;
            else
            {
                string place = intoPackfile ? packfile ?? "the packfile" : folder;
                string list = string.Join("\n", taken.Take(8).Select(n => "• " + n)) + (taken.Count > 8 ? $"\n... and {taken.Count - 8:N0} more" : "");
                int answer = base.Shell.Dialogs.Choose($"{taken.Count:N0} output name{(taken.Count == 1 ? " is" : "s are")} taken",
                    $"{place} already has:\n{list}\n\nReplace them with the converted files, or keep both (the new files get free names)?",
                    ["Replace them", "Keep both", "Cancel"], 2);
                if (answer is not (0 or 1)) return null;
                replace = answer == 0;
            }
        }

        var outcomes = new LvlConvertOutcome[jobs.Count];
        var packfileFiles = new List<(string Name, byte[] Bytes)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = choice.Options;
        Task Work(IProgress<(double Fraction, string Item)> progress, CancellationToken ct) => Task.Run(() =>
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var job = jobs[i];
                progress.Report(((double)i / jobs.Count, job.Name));
                string main = mains[i];
                if (!intoPackfile && Taken(main) && !replace) main = FreeName(folder, main, usedNames);
                else if (intoPackfile && !replace && Taken(main)) main = FreeEntryName(existing!, main, usedNames);
                usedNames.Add(main);
                LvlConversion conversion;
                try
                {
                    if (job.Direct is { } direct) conversion = direct(choice.Import, ct);
                    else
                    {
                        var source = job.Load(ct);
                        var o = options with { TextureResolver = job.Resolver(source) };
                        if (source.Level?.IsRf2 == true && job.Rf2 is { } complete) o = o with { Rf2 = complete(o.Rf2) };
                        conversion = LvlConverter.Convert(source.Input, o, main, ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    outcomes[i] = new LvlConvertOutcome(job.Name, null, [], ex.Message);
                    continue;
                }
                if (!conversion.HasOutput)
                {
                    outcomes[i] = new LvlConvertOutcome(job.Name, conversion.Report, [],
                        conversion.Report.Issues.FirstOrDefault(x => x.Severity == LvlIssueSeverity.Error)?.Message ?? "The conversion produced nothing to write.");
                    continue;
                }
                try
                {
                    if (intoPackfile)
                    {
                        // written to a private folder first, then added to the packfile with the others as one step
                        string temp = Path.Combine(Path.GetTempPath(), "cairn-lvl-" + Guid.NewGuid().ToString("N")[..10]);
                        try
                        {
                            var files = conversion.WriteTo(Path.Combine(temp, main));
                            var names = new List<string>();
                            foreach (var file in files)
                            {
                                string entry = Path.GetFileName(file);
                                // textures shared by several levels are added once
                                if (packfileFiles.Any(f => f.Name.Equals(entry, StringComparison.OrdinalIgnoreCase))) continue;
                                packfileFiles.Add((entry, File.ReadAllBytes(file)));
                                names.Add(entry);
                            }
                            outcomes[i] = new LvlConvertOutcome(job.Name, conversion.Report, names, null);
                        }
                        finally { TryDelete(temp); }
                    }
                    else
                    {
                        Directory.CreateDirectory(folder);
                        var files = conversion.WriteTo(Path.Combine(folder, main));
                        outcomes[i] = new LvlConvertOutcome(job.Name, conversion.Report, files, null);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    outcomes[i] = new LvlConvertOutcome(job.Name, conversion.Report, [], ex.Message);
                }
            }
            progress.Report((1, "done"));
        }, ct);

        bool finished;
        using (BusyTracker.Begin("level conversion"))
        {
            if (run is not null) finished = await run("Converting levels", Work);
            else finished = await RunWithProgressAsync(jobs.Count == 1 ? "Converting " + jobs[0].Name : "Converting levels", Work, ask);
        }
        if (!finished) return null;

        var result = outcomes.Select((o, i) => o ?? new LvlConvertOutcome(jobs[i].Name, null, [], "Not converted.")).ToList();
        if (intoPackfile && add is not null && packfileFiles.Count > 0)
        {
            var ok = result.Where(o => o.Succeeded).ToList();
            string target = LvlConvertOptions.ExtensionOf(options.Target).TrimStart('.').ToUpperInvariant();
            string label = ok.Count == 1 ? $"Convert {ok[0].SourceName} to {target}" : $"Convert {ok.Count:N0} levels to {target}";
            var added = add(label, packfileFiles, replace);
            if (added is null)
                result = [.. result.Select(o => o.Succeeded ? o with { Written = [], Error = "The files could not be added to the packfile." } : o)];
            else
            {
                // the names the packfile used (a taken one gets a free name unless replacing)
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < Math.Min(added.Count, packfileFiles.Count); i++) map[packfileFiles[i].Name] = added[i];
                result = [.. result.Select(o => o with { Written = [.. o.Written.Select(n => map.GetValueOrDefault(n, n))] })];
            }
        }
        LastOutcomes = result;
        if (ask)
        {
            var window = new LvlReportWindow(Summary(result, choice), result) { Owner = base.Shell.MainWindow };
            window.ShowDialog();
        }
        else if (result.FirstOrDefault(o => o.Error is not null) is { } failed && jobs.Count == 1 && interactive)
        {
            base.Shell.Dialogs.ShowError($"Could not convert {failed.SourceName}", failed.Error!);
        }
        return result;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with a small progress window (Cancel stops it) when <paramref name="show"/>, else
    /// quietly. False when cancelled.
    /// </summary>
    private async Task<bool> RunWithProgressAsync(string title, Func<IProgress<(double Fraction, string Item)>, CancellationToken, Task> work, bool show)
    {
        using var cts = new CancellationTokenSource();
        LvlProgressWindow? window = null;
        if (show)
        {
            window = new LvlProgressWindow(title, cts) { Owner = base.Shell.MainWindow };
            window.Show();
        }
        try
        {
            await work(window?.Progress ?? new Progress<(double, string)>(), cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { window?.Finish(); }
    }

    private static string Summary(IReadOnlyList<LvlConvertOutcome> outcomes, LvlConvertChoice choice)
    {
        int ok = outcomes.Count(o => o.Succeeded);
        string target = LvlConvertOptions.ExtensionOf(choice.Options.Target);
        int files = outcomes.Sum(o => o.Written.Count);
        string where = choice.Place == LvlOutputPlace.IntoPackfile ? "added to the packfile (Undo removes them)" : "written";
        return outcomes.Count == 1
            ? ok == 1 ? string.Format(CultureInfo.CurrentCulture, "Converted {0} to {1}: {2:N0} file{3} {4}.", outcomes[0].SourceName, target, files, files == 1 ? "" : "s", where)
                : $"{outcomes[0].SourceName} was not converted."
            : string.Format(CultureInfo.CurrentCulture, "Converted {0:N0} of {1:N0} levels and group files to {2}: {3:N0} files {4}.", ok, outcomes.Count, target, files, where);
    }

    private static string FreeName(string folder, string name, HashSet<string> used)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string candidate = name;
        for (int n = 2; SafeExists(Path.Combine(folder, candidate)) || used.Contains(candidate); n++) candidate = $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){ext}";
        return candidate;
    }

    private static string FreeEntryName(HashSet<string> existing, string name, HashSet<string> used)
    {
        // packfile names have no spaces in stock data; keep them short and plain
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string candidate = name;
        for (int n = 2; existing.Contains(candidate) || used.Contains(candidate); n++) candidate = $"{stem}_{n.ToString(CultureInfo.InvariantCulture)}{ext}";
        return candidate;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The other entries of a packfile, read through a converter request's or batch's callbacks.</summary>
internal sealed class LvlEntrySiblings(string label, IReadOnlyList<string> names, Func<string, byte[]?> read) : IAssetSiblings
{
    private readonly HashSet<string> _names = new(names.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);

    public string Label { get; } = label;
    public IReadOnlyList<string> Names { get; } = names;
    public bool Contains(string name) => !string.IsNullOrEmpty(name) && _names.Contains(Path.GetFileName(name));

    public byte[]? Read(string name)
    {
        if (!Contains(name)) return null;
        try { return read(Path.GetFileName(name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }
}

/// <summary>A small window with a progress bar and Cancel while a conversion runs.</summary>
internal sealed class LvlProgressWindow : Window
{
    private readonly ProgressBar _bar = new() { Height = 14, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _item = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private bool _finished;

    public LvlProgressWindow(string title, CancellationTokenSource cts)
    {
        Title = title;
        Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");
        _item.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        System.Windows.Automation.AutomationProperties.SetName(_bar, "Progress");
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), ToolTip = "Stop converting (nothing is written for the level being converted)" };
        cancel.SetResourceReference(StyleProperty, "DialogButton");
        cancel.Click += (_, _) => { cts.Cancel(); cancel.IsEnabled = false; };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(_item);
        panel.Children.Add(_bar);
        panel.Children.Add(cancel);
        Content = panel;
        _item.Text = title + "…";
        Progress = new Progress<(double Fraction, string Item)>(p =>
        {
            _bar.Value = Math.Clamp(p.Fraction, 0, 1);
            _item.Text = p.Item == "done" ? "Writing…" : p.Item;
        });
        Closing += (_, e) => { if (!_finished) { cts.Cancel(); e.Cancel = true; } };
    }

    /// <summary>Reports progress (on the UI thread).</summary>
    public IProgress<(double Fraction, string Item)> Progress { get; }

    /// <summary>Closes the window once the work has ended.</summary>
    public void Finish()
    {
        _finished = true;
        Close();
    }
}
