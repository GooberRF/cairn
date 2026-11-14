using System.Collections.Immutable;
using System.Globalization;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Ui.Views.Dialogs;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// Converting the meshes Cairn only reads (.v3d, .vcm, .rfm, .rfc) to the PC formats, and Red Faction II clips (.rfa
/// version 9) to Red Faction ones: one open tab ("Convert..." on its banner, File > Export or File > Convert to) or a
/// batch of packfile entries (the packfile's "Convert meshes and animations..."). The files go next to the source, into
/// a folder, or into the packfile as new entries in one undo step; a report says what was approximated.
/// </summary>
public sealed class LegacyMeshTools(RfaWorkspace workspace)
{
    private const string TargetKey = "legacyConvert.target", FolderKey = "legacyConvert.folder", ReplaceKey = "legacyConvert.replace",
        Rf2TexturesKey = "legacyConvert.rf2Textures", XffScaleKey = "legacyConvert.xffScale", XffTexturesKey = "legacyConvert.xffTextures";

    private IShellContext? Host => workspace.Host;

    private ModuleSettings Settings => new(workspace.Settings, "rfa");

    /// <summary>The choice the Convert window starts from (the last one used; Red Faction II texture names translated unless chosen otherwise).</summary>
    public LegacyConvertChoice Defaults => new(
        Enum.TryParse<LegacyConvertTarget>(Settings.Get<string>(TargetKey), out var target) ? target : LegacyConvertTarget.NextToSource,
        Settings.Get<string>(FolderKey),
        Settings.Get<bool>(ReplaceKey))
    {
        Rf2Textures = Enum.TryParse<Rf2TextureMode>(Settings.Get<string>(Rf2TexturesKey), out var mode) ? mode : Rf2TextureMode.Translate,
        XffScale = float.TryParse(Settings.Get<string>(XffScaleKey), NumberStyles.Float, CultureInfo.InvariantCulture, out float scale) && Cairn.Formats.XForge.XffSpace.IsValidScale(scale)
            ? scale : Cairn.Formats.XForge.XffSpace.RfScale,
        XffWriteTextures = Settings.Get(XffTexturesKey, true),
    };

    private void Remember(LegacyConvertChoice choice)
    {
        if (workspace.IsDiagnosticRun) return;
        Settings.Set(TargetKey, choice.Target.ToString());
        Settings.Set(ReplaceKey, choice.Replace);
        Settings.Set(Rf2TexturesKey, choice.Rf2Textures.ToString());
        Settings.Set(XffScaleKey, choice.XffScale.ToString("R", CultureInfo.InvariantCulture));
        Settings.Set(XffTexturesKey, choice.XffWriteTextures);
        if (choice.Target == LegacyConvertTarget.Folder && choice.Folder is { } folder) Settings.Set(FolderKey, folder);
    }

    private IArchiveEntryTarget? ArchiveTarget => Host?.Modules.OfType<IArchiveEntryTarget>().FirstOrDefault();

    /// <summary>The packfile a tab's work copy came from (its name), or null.</summary>
    public string? PackfileOf(DocumentViewModel doc) => doc.FilePath is { } path ? ArchiveTarget?.ArchiveOf(path) : null;

    /// <summary>
    /// The folder "next to the source" means: the file's own, or for a packfile entry the packfile's; null when none,
    /// or when that is the game directory (a loose mesh there changes what the game loads; the user picks a folder).
    /// </summary>
    public string? NextToFolder(DocumentViewModel doc)
    {
        string? folder = SourceFolder(doc);
        return folder is not null && workspace.IsGameDirectory(folder) ? null : folder;
    }

    /// <summary>Why <see cref="NextToFolder"/> is null, for the Convert window.</summary>
    private string NoNextToReason(string? sourceFolder) =>
        sourceFolder is not null && workspace.IsGameDirectory(sourceFolder) ? "that is the game directory" : "it has no folder";

    private string? SourceFolder(DocumentViewModel doc)
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

    /// <summary>
    /// The conversion of a tab's mesh (its twin's for a PS2 mesh when one is beside it; a Red Faction II mesh's texture
    /// names translated, or kept with its textures from the .peg packs beside it; an N-Gage scene at the chosen scale, with
    /// its textures from beside it or not), as <paramref name="choice"/> says (the last choice when null).
    /// </summary>
    /// <exception cref="AssetFormatException">It cannot be converted; the message says why.</exception>
    public LegacyMeshConversion Convert(MeshDocumentViewModel doc, LegacyConvertChoice? choice = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Legacy is not { } legacy) throw new AssetFormatException($"{doc.DisplayName} is not an exporter or PS2 mesh.");
        // read only for what needs pixels: kept textures, or a character's atlas
        var options = (choice ?? Defaults).Options(LegacyMeshTextures.PacksForTab(legacy, doc.DisplayName, doc.FilePath, doc.FilePath is null ? null : AssetSiblings.For(Host, doc.FilePath)));
        return LegacyMeshSupport.Convert(legacy.Bytes, doc.DisplayName, SiblingReader(doc), options);
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
        bool rf2 = doc.Legacy?.Mesh?.IsRedFactionII == true;
        var xff = doc.Legacy?.Mesh?.Xff;
        try
        {
            conversion = Convert(doc, choice ?? Defaults);
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
            var defaults = Defaults;
            // a Red Faction II mesh: the report for each texture choice, so the window shows the one ticked
            IReadOnlyList<string> translated = conversion.Report, kept = conversion.Report;
            if (rf2)
            {
                var other = Convert(doc, defaults with { Rf2Textures = defaults.Rf2Textures == Rf2TextureMode.Translate ? Rf2TextureMode.KeepOriginals : Rf2TextureMode.Translate });
                (translated, kept) = defaults.Rf2Textures == Rf2TextureMode.Translate ? (conversion.Report, other.Report) : (other.Report, conversion.Report);
            }
            var item = new LegacyConvertItem(doc.DisplayName, conversion.OutputName, translated, IsRedFactionII: rf2, KeepTexturesReport: rf2 ? kept : null,
                IsXff: xff is not null, ClipName: conversion.Clip?.OutputName);
            var window = new LegacyMeshConvertWindow(workspace.Dialogs, [item], packfile, nextTo, defaults, NoNextToReason(SourceFolder(doc)))
            {
                Owner = Host.MainWindow,
            };
            if (window.ShowDialog() != true || window.Result is not { } chosen) return Task.FromResult<IReadOnlyList<string>?>(null);
            Remember(chosen);
            choice = chosen;
            if ((rf2 && chosen.Rf2Textures != defaults.Rf2Textures) || (xff is not null && (chosen.XffScale != defaults.XffScale || chosen.XffWriteTextures != defaults.XffWriteTextures)))
            {
                try { conversion = Convert(doc, chosen); }
                catch (AssetFormatException ex)
                {
                    workspace.Dialogs.ShowError($"Could not convert {doc.DisplayName}", ex.Message);
                    return Task.FromResult<IReadOnlyList<string>?>(null);
                }
            }
        }

        IReadOnlyList<string>? written;
        if (choice.Target == LegacyConvertTarget.IntoPackfile && doc.FilePath is { } path && ArchiveTarget is { } target)
        {
            written = target.AddFiles(path, $"Convert {doc.DisplayName} to {Path.GetExtension(conversion.OutputName)}", conversion.AllFiles, choice.Replace);
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

    // ── a Red Faction II clip's tab ──────────────────────────────────────────

    /// <summary>
    /// Converts a Red Faction II clip's tab to a Red Faction clip with <paramref name="choice"/>, or with what the Convert
    /// window settles on when null (self-tests pass a choice). The clip is never written over the file the tab was read
    /// from. Returns the names or paths written, or null when cancelled or failed.
    /// </summary>
    public Task<IReadOnlyList<string>?> ConvertClipDocumentAsync(ClipDocumentViewModel doc, LegacyConvertChoice? choice = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        Rf2ClipConversion conversion;
        try
        {
            conversion = Rf2AnimationConverter.Convert(doc.SavedSnapshot, doc.DisplayName);
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
            var item = new LegacyConvertItem(doc.DisplayName, conversion.OutputName, conversion.Report, IsClip: true);
            var window = new LegacyMeshConvertWindow(workspace.Dialogs, [item], packfile, nextTo, Defaults, NoNextToReason(SourceFolder(doc)))
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
            // never over the entry the clip was read from, or another Red Faction II clip: both are kept
            string[] sources = WorkCopyEntryOf(path) is { } entry ? [doc.DisplayName, entry.EntryName] : [doc.DisplayName];
            var siblings = AssetSiblings.For(Host, path);
            var files = Rf2ClipOutputs.PackfileNames([(conversion.OutputName, conversion.Bytes)], sources, target.EntryNamesOf(path) ?? [], name => siblings?.Read(name));
            written = target.AddFiles(path, $"Convert {doc.DisplayName} to a Red Faction clip", files, choice.Replace);
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
            // never over the file the tab shows, nor (for a work copy of a loose Red Faction II group's entry) the loose file
            written = WriteFiles(folder, [], choice.Replace, out var errors, [conversion], SourceFilesOf(doc));
            if (errors.Count > 0) workspace.Dialogs.ShowError($"Could not write {conversion.OutputName}", string.Join(Environment.NewLine, errors));
            else if (written.Count > 0) doc.ShowStatus($"Wrote {Path.GetFileName(written[0])} to {Path.GetDirectoryName(written[0])}");
        }
        return Task.FromResult(written);
    }

    /// <summary>The packfile entry a work copy at <paramref name="path"/> was extracted from, or null (not a work copy, or unknown).</summary>
    private (string ArchivePath, string EntryName)? WorkCopyEntryOf(string path)
    {
        foreach (var provider in Host?.Modules.OfType<IWorkCopyProvider>() ?? [])
            if (provider.IsWorkCopy(path)) return provider.ArchiveEntryOf(path);
        return null;
    }

    /// <summary>
    /// The files a tab's conversion must never be written over: the tab's own file and, for a work copy of an entry of a
    /// loose Red Faction II group, the loose file the entry is.
    /// </summary>
    private IReadOnlyList<string> SourceFilesOf(DocumentViewModel doc)
    {
        if (doc.FilePath is not { } path) return [];
        return WorkCopyEntryOf(path) is { } entry ? [path, .. Rf2ClipOutputs.LooseFiles(entry.ArchivePath, [entry.EntryName])] : [path];
    }

    /// <summary>
    /// Writes the converted meshes and clips into <paramref name="folder"/> (a free name for each taken one unless
    /// replacing; never over one of <paramref name="protectedPaths"/>, the files being converted, or over a Red Faction II
    /// clip), then the textures written beside the meshes (a Red Faction II mesh with its texture names kept): those keep
    /// their names, since the mesh refers to them, so a taken one is left as it is unless replacing. Returns the meshes'
    /// and clips' paths, then the textures'.
    /// </summary>
    internal static IReadOnlyList<string> WriteFiles(string folder, IReadOnlyList<LegacyMeshConversion> conversions, bool replace, out List<string> errors,
        IReadOnlyList<Rf2ClipConversion>? clips = null, IReadOnlyCollection<string>? protectedPaths = null)
    {
        // an N-Gage character's clip goes right after its mesh
        var meshes = WriteMeshes(folder, [.. conversions.SelectMany(c => c.Clip is { } clip ? new[] { (c.OutputName, c.Bytes), (clip.OutputName, clip.Bytes) } : [(c.OutputName, c.Bytes)]),
            .. (clips ?? []).Select(c => (c.OutputName, c.Bytes))], replace, out errors, protectedPaths ?? []);
        if (errors.Count > 0 && meshes.Count == 0) return meshes;
        var written = new List<string>(meshes);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in conversions.SelectMany(c => c.Textures))
        {
            if (!seen.Add(name)) continue;
            string path = Path.Combine(folder, name);
            if (!replace && File.Exists(path)) continue;
            try
            {
                AtomicFile.WriteAllBytes(path, bytes);
                written.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
        return written;
    }

    /// <summary>
    /// Writes files a tab's mesh refers to into its packfile, or beside it (asking before replacing one). Returns false,
    /// after saying why, when they were not written.
    /// </summary>
    internal bool WriteBeside(MeshDocumentViewModel doc, IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        if (doc.FilePath is { } path && PackfileOf(doc) is not null && ArchiveTarget is { } target)
            return target.AddFiles(path, $"Add {files[0].Name}", files, replace: true) is not null;
        if (NextToFolder(doc) is not { Length: > 0 } folder)
        {
            workspace.Dialogs.ShowError($"Could not write {files[0].Name}", $"There is no folder to write it to ({NoNextToReason(SourceFolder(doc))}). Save the mesh in another folder first.");
            return false;
        }
        var taken = files.Where(f => File.Exists(Path.Combine(folder, f.Name))).Select(f => f.Name).ToList();
        if (taken.Count > 0 && !workspace.Dialogs.Confirm($"Replace {string.Join(", ", taken)}?", $"{folder} already holds {string.Join(", ", taken)}.", "Replace")) return false;
        try
        {
            foreach (var (name, bytes) in files) AtomicFile.WriteAllBytes(Path.Combine(folder, name), bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            workspace.Dialogs.ShowError($"Could not write {files[0].Name}", ex.Message);
            return false;
        }
    }

    /// <summary>The textures of <paramref name="conversions"/>, once per name (for adding to a packfile with the meshes).</summary>
    private static IEnumerable<(string Name, byte[] Bytes)> DistinctTextures(IEnumerable<LegacyMeshConversion> conversions)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return conversions.SelectMany(c => c.Textures).Where(t => seen.Add(t.Name));
    }

    private static List<string> WriteMeshes(string folder, IReadOnlyList<(string OutputName, byte[] Bytes)> outputs, bool replace, out List<string> errors, IReadOnlyCollection<string> protectedPaths)
    {
        errors = [];
        var written = new List<string>();
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errors.Add($"{folder}: {ex.Message}");
            return written;
        }
        var keep = Rf2ClipOutputs.PathSet(protectedPaths);
        bool Taken(string path) => File.Exists(path) || written.Contains(path, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in outputs)
        {
            // a converted clip keeps its .rfa name: a file being converted, or any Red Faction II clip, is never replaced
            string path = Rf2ClipOutputs.FolderPath(folder, name, replace, Taken, p => Rf2ClipOutputs.MustKeepFile(p, keep));
            try
            {
                AtomicFile.WriteAllBytes(path, bytes);
                written.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
        return written;
    }

    // ── a batch of packfile entries ──────────────────────────────────────────

    /// <summary>The result of converting a batch (the summary line, the conversions and why the others failed).</summary>
    /// <param name="Summary">One line.</param>
    /// <param name="Converted">The meshes' conversions written.</param>
    /// <param name="Failed">"name: why" for each mesh or clip that could not be converted.</param>
    public sealed record BatchResult(string Summary, IReadOnlyList<LegacyMeshConversion> Converted, IReadOnlyList<string> Failed)
    {
        /// <summary>The Red Faction II clips' conversions written.</summary>
        public IReadOnlyList<Rf2ClipConversion> Clips { get; init; } = [];
    }

    /// <summary>The last batch's result (self-tests, the shell's among them).</summary>
    public BatchResult? LastBatch { get; private set; }

    /// <summary>
    /// The packfile's "Convert meshes and animations...": the selected exporter, PS2 and Red Faction II meshes, with a PS2
    /// mesh converting from its exporter twin when the packfile holds it, selected or not (a selected twin's own entry
    /// makes the same file once), and the selected Red Faction II clips (other .rfa entries are Red Faction clips already
    /// and are left alone).
    /// </summary>
    public async Task<string?> ConvertBatchAsync(ArchiveBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var meshEntries = request.Entries.Where(e => LegacyMeshSupport.IsLegacyName(e.Name)).ToList();
        // the clips by content: only Red Faction II's (version 9) convert
        var clipEntries = await Task.Run(() => request.Entries.Where(e => e.Name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) && IsRf2Clip(e)).ToList());
        var entries = meshEntries.Concat(clipEntries).ToList();
        if (entries.Count == 0)
        {
            if (request.Interactive && !workspace.IsDiagnosticRun && request.Entries.Count > 0)
                workspace.Dialogs.ShowError("Nothing to convert", "None of the selected entries is a mesh Cairn converts or a Red Faction II animation; Red Faction clips need no conversion.");
            return null;
        }
        string? packfile = request.AddFiles is null ? null : request.ArchiveName;
        // never the game directory by default: a loose mesh there changes what the game loads
        string? nextTo = request.ArchiveFolder is { } archiveFolder && !workspace.IsGameDirectory(archiveFolder) ? archiveFolder : null;
        var choice = request.Options as LegacyConvertChoice;
        if (choice is null && request.Interactive && !workspace.IsDiagnosticRun && Host is not null)
        {
            // the texture choice shows when a Red Faction II mesh is among them (by content: PS2 meshes share the extensions)
            var rf2 = await Task.Run(() => meshEntries.Where(e => e.Name.EndsWith(".rfm", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase))
                .Where(e => { try { return LegacyMeshSupport.IsRedFactionII(e.Read()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; } })
                .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            // an N-Gage scene is a model or a character by its content (a character also makes its clip)
            var xffKinds = await Task.Run(() => meshEntries.Where(e => LegacyMeshSupport.IsXffName(e.Name))
                .Select(e => { try { return (e.Name, Kind: Cairn.Formats.XForge.XffClassifier.TryClassify(e.Read(), e.Name)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return (e.Name, Kind: null); } })
                .ToDictionary(x => x.Name, x => x.Kind, StringComparer.OrdinalIgnoreCase));
            var items = meshEntries.Select(e => xffKinds.TryGetValue(e.Name, out var kind)
                    ? new LegacyConvertItem(e.Name, Path.ChangeExtension(e.Name, kind == Cairn.Formats.XForge.XffKind.Character ? ".v3c" : ".v3m"), null,
                        Error: kind is Cairn.Formats.XForge.XffKind.Level ? "a level, which the Levels module converts" : kind is null ? "not a readable N-Gage scene" : null,
                        IsXff: true, ClipName: kind == Cairn.Formats.XForge.XffKind.Character ? XffMeshConverter.ClipName(e.Name) : null)
                    : new LegacyConvertItem(e.Name, Path.ChangeExtension(e.Name, OutputExtensionByName(e.Name)), null, IsRedFactionII: rf2.Contains(e.Name)))
                .Concat(clipEntries.Select(e => new LegacyConvertItem(e.Name, e.Name, null, IsClip: true))).ToList();
            var window = new LegacyMeshConvertWindow(workspace.Dialogs, items, packfile, nextTo, Defaults, NoNextToReason(request.ArchiveFolder)) { Owner = Host.MainWindow };
            if (window.ShowDialog() != true || window.Result is not { } chosen) return null;
            Remember(chosen);
            choice = chosen;
        }
        var defaults = Defaults;
        choice ??= new LegacyConvertChoice(packfile is not null ? LegacyConvertTarget.IntoPackfile : LegacyConvertTarget.Folder,
            nextTo ?? workspace.DefaultOutputFolder(defaults.Folder), defaults.Replace) { Rf2Textures = defaults.Rf2Textures, XffScale = defaults.XffScale, XffWriteTextures = defaults.XffWriteTextures };
        var chosenOptions = choice;

        var converted = new List<LegacyMeshConversion>();
        var clips = new List<Rf2ClipConversion>();
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
                    if (entry.Name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                    {
                        var clip = Rf2AnimationConverter.Convert(bytes, entry.Name);
                        if (outputs.Add(clip.OutputName)) clips.Add(clip);
                        else failed.Add($"{entry.Name}: another selected entry makes {clip.OutputName} too; left out");
                        continue;
                    }
                    // a Red Faction II mesh: the .peg entries beside it, read only for kept textures or a character's atlas
                    var options = chosenOptions.Options(LegacyMeshSupport.IsRedFactionII(bytes) ? LegacyMeshTextures.PacksForEntry(entry.Name, byName.Values, request.ArchiveName) : null);
                    var conversion = LegacyMeshSupport.Convert(bytes, entry.Name, name => byName.TryGetValue(name, out var twin) ? twin.Read() : null, options);
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
        string what = clipEntries.Count == 0 ? "meshes" : meshEntries.Count == 0 ? "animations" : "meshes and animations";
        if (request.RunAsync is { } run) finished = await run("Converting " + what, Work);
        else { await Work(new Progress<(double, string)>(), CancellationToken.None); finished = true; }
        if (!finished) return null;

        string where;
        var errors = new List<string>();
        var files = converted.SelectMany(c => c.Clip is { } clip ? new[] { (c.OutputName, c.Bytes), (clip.OutputName, clip.Bytes) } : [(c.OutputName, c.Bytes)])
            .Concat(clips.Select(c => (c.OutputName, c.Bytes))).ToList();
        if (choice.Target == LegacyConvertTarget.IntoPackfile && request.AddFiles is { } add)
        {
            string label = (converted.Count, clips.Count) switch
            {
                (1, 0) => $"Convert {converted[0].SourceName} to {Path.GetExtension(converted[0].OutputName)}",
                (0, 1) => $"Convert {clips[0].SourceName} to a Red Faction clip",
                _ => $"Convert {files.Count:N0} {what}",
            };
            // a clip keeps its name: never over the entry it was read from, or another Red Faction II clip (both are kept)
            var all = new Dictionary<string, ArchiveBatchEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in request.AllEntries.Concat(request.Entries)) all.TryAdd(e.Name, e);
            var named = Rf2ClipOutputs.PackfileNames([.. files, .. DistinctTextures(converted)], clipEntries.Select(e => e.Name), all.Keys,
                name => all.TryGetValue(name, out var e) ? e.Read() : null);
            var names = files.Count == 0 ? [] : add(label, named, choice.Replace);
            where = string.Format(CultureInfo.CurrentCulture, " ({0:N0} added to {1}; Undo removes them)", names.Count, request.ArchiveName);
        }
        else
        {
            string folder = choice.Folder ?? nextTo ?? workspace.DefaultOutputFolder(defaults.Folder);
            // the entries of a loose Red Faction II group are files: a converted clip is never written over one
            var sources = request.ArchiveFolder is { } archiveDir
                ? Rf2ClipOutputs.LooseFiles(Path.Combine(archiveDir, request.ArchiveName), entries.Select(e => e.Name)) : [];
            var written = WriteFiles(folder, converted, choice.Replace, out errors, clips, sources);
            int count = Math.Min(written.Count, files.Count);
            where = string.Format(CultureInfo.CurrentCulture, " ({0:N0} written to {1}{2})", count, folder,
                written.Count > count ? string.Format(CultureInfo.CurrentCulture, ", with {0:N0} textures", written.Count - count) : "");
        }
        int approximated = converted.Count(c => !c.IsLossless) + clips.Count(c => !c.IsLossless);
        string summary = string.Format(CultureInfo.CurrentCulture, "Converted {0:N0} of {1:N0} {2}", converted.Count + clips.Count, entries.Count, what)
            + (approximated > 0 ? string.Format(CultureInfo.CurrentCulture, ", {0:N0} with notes", approximated) : "")
            + where + ".";
        LastBatch = new BatchResult(summary, converted, [.. failed, .. errors]) { Clips = clips };
        if (request.Interactive && !workspace.IsDiagnosticRun)
        {
            var reports = converted.Select(c => (c.SourceName, c.OutputName, c.Report)).Concat(clips.Select(c => (c.SourceName, c.OutputName, c.Report))).ToList();
            ShowReport(summary, what, reports, [.. failed, .. errors]);
        }
        return summary;
    }

    /// <summary>True when the entry is a Red Faction II clip (read to tell; false when it cannot be read).</summary>
    private static bool IsRf2Clip(ArchiveBatchEntry entry)
    {
        try { return Rf2AnimationConverter.IsRedFactionII(entry.Read()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    private static string OutputExtensionByName(string name) =>
        name.EndsWith(".vcm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase) ? ".v3c" : ".v3m";

    /// <summary>The batch report: failures first, then each remark with the files it concerns.</summary>
    private void ShowReport(string summary, string what, IReadOnlyList<(string SourceName, string OutputName, ImmutableArray<string> Report)> converted, IReadOnlyList<string> failed)
    {
        var lines = failed.Select(f => "• " + f).ToList();
        string file = what == "animations" ? "the clip" : what == "meshes" ? "the mesh" : "the file";
        var notes = converted.SelectMany(c => c.Report.Select(n => (Key: n.Replace(Path.GetFileName(c.OutputName), file, StringComparison.Ordinal), c.SourceName)))
            .GroupBy(n => n.Key).Select(g => g.Count() == 1 ? $"• {g.First().SourceName}: {g.Key}" : $"• {g.Key} ({g.Count():N0} {what})").ToList();
        string text = summary + (lines.Count > 0 ? "\n\nNot converted:\n" + string.Join("\n", lines.Take(10)) + (lines.Count > 10 ? $"\n... and {lines.Count - 10:N0} more" : "") : "")
            + (notes.Count > 0 ? "\n\nWhat the conversion changed:\n" + string.Join("\n", notes.Take(14)) + (notes.Count > 14 ? $"\n... and {notes.Count - 14:N0} more" : "") : "");
        workspace.Dialogs.Choose(char.ToUpperInvariant(what[0]) + what[1..] + " converted", text, ["OK"], 0);
    }
}
