using Cairn.Assets;
using Cairn.Formats;
using Cairn.Formats.Imaging;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Linting;
using Cairn.Ui.Modules;
using Cairn.Vfx.Linting;
using Cairn.Vfx.Rfe;
using Cairn.Vfx.Ui.Dialogs;
using Cairn.Vfx.Ui.Documents;
using Cairn.Workspace;

namespace Cairn.Vfx.Ui.Rfe;

/// <summary>Where and how a Red Faction II effect is converted.</summary>
/// <param name="Folder">The output folder (ignored when <paramref name="IntoPackfile"/>).</param>
/// <param name="Textures">Translate the texture names, or keep them and write the textures from the .peg packs.</param>
/// <param name="Replace">True to replace files of the same name (never the source or a Red Faction II file).</param>
/// <param name="IntoPackfile">True to add the outputs to the packfile the source is in (one undo step), when it takes files.</param>
public sealed record RfeConvertChoice(string Folder, Rf2TextureMode Textures = Rf2TextureMode.Translate, bool Replace = false, bool IntoPackfile = false);

/// <summary>
/// File › Convert to › Red Faction effect (.vfx) for a Red Faction II effect (its tab, a packfile entry) and the packfile
/// batch "Convert effects...": the window (where, textures, the report), the writing (never over the source or a Red
/// Faction II file: a free name instead), and the converted tab with the report in Problems as information.
/// </summary>
public sealed class RfeConversionUi(IShellContext shell)
{
    /// <summary>The Convert to target.</summary>
    public static ConversionTarget Target { get; } = new(".vfx", "Red Faction effect (.vfx)",
        "Convert to a Red Faction effect (version 0x40006): meshes and materials exactly, particles as closely as Red Faction allows, with a report of what was approximated; the window asks where to write it", true);

    /// <summary>True for a name the conversion takes (<c>.rfe</c>).</summary>
    public static bool IsRfeName(string? name) => RfeReader.HasExtension(name);

    /// <summary>The last conversion (diagnostics).</summary>
    public RfeConversion? LastConversion { get; private set; }

    /// <summary>The files the last conversion wrote (paths, or entry names when into a packfile).</summary>
    public IReadOnlyList<string> LastOutputs { get; private set; } = [];

    /// <summary>What a conversion without the window does with the texture names (translate them, by default).</summary>
    public Rf2TextureMode QuietTextures { get; set; } = Rf2TextureMode.Translate;

    private string? _lastFolder;

    /// <summary>Converts one effect (a tab or a packfile entry). Returns the path (or entry name) of the .vfx written, or null.</summary>
    public async Task<string?> ConvertAsync(AssetConversionRequest request)
    {
        RfeFile effect;
        try { effect = request.Document is RfeDocument tab ? tab.Effect : RfeReader.Read(request.Bytes, request.FileName); }
        catch (AssetFormatException ex)
        {
            shell.Dialogs.ShowError($"Could not convert {request.FileName}", ex.Message);
            return null;
        }
        string? sourcePath = request.FilePath;
        RfeConvertChoice? choice = request.Interactive ? null
            : new RfeConvertChoice(request.OutputFolder ?? DefaultFolder(sourcePath), QuietTextures, IntoPackfile: request.OutputFolder is null && request.AddToArchive is not null);
        // finding the packs and converting read and decode .peg packs: off the UI thread. The window's report needs both
        // texture modes, so both are made here once; the window only picks one.
        Rf2TextureMode[] modes = choice is null ? [Rf2TextureMode.Translate, Rf2TextureMode.KeepOriginals] : [choice.Textures];
        Dictionary<Rf2TextureMode, RfeConversion> conversions;
        try
        {
            using (Cairn.Ui.Services.BusyTracker.Begin("effect conversion"))
                conversions = await Task.Run(() =>
                {
                    var packs = PacksFor(request, effect);
                    return modes.ToDictionary(m => m, m => RfeToVfx.Convert(effect, request.FileName, new RfeConvertOptions { Textures = m, TexturePacks = packs }));
                });
        }
        catch (AssetFormatException ex)
        {
            shell.Dialogs.ShowError($"Could not convert {request.FileName}", ex.Message);
            return null;
        }
        if (choice is null)
        {
            choice = Ask(conversions, DefaultFolder(sourcePath), request.AddToArchive is not null);
            if (choice is null) return null;
        }
        var conversion = conversions[choice.Textures];
        LastConversion = conversion;
        IReadOnlyList<string> written;
        try
        {
            if (choice.IntoPackfile && request.AddToArchive is { } add)
            {
                var names = add($"Convert {request.FileName} to .vfx", PackfileFiles(conversion, request.ArchiveEntryNames, choice.Replace, null), choice.Replace);
                if (names is null) return null;
                written = names;
            }
            else written = WriteFolder(choice.Folder, conversion, choice.Replace, sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            shell.Dialogs.ShowError($"Could not convert {request.FileName}", ex.Message);
            return null;
        }
        LastOutputs = written;
        string? main = written.FirstOrDefault();
        if (main is not null && !choice.IntoPackfile) Open(main, conversion);
        shell.ShowStatus($"Converted {request.FileName} to {Path.GetFileName(main ?? conversion.OutputName)} ({conversion.Approximations:N0} approximated or dropped, see Problems).");
        return main;
    }

    /// <summary>The packfile batch: every selected <c>.rfe</c> into a folder (or the packfile). Returns a summary, or null when cancelled.</summary>
    public async Task<string?> ConvertBatchAsync(ArchiveBatchRequest request)
    {
        var entries = request.Entries.Where(e => IsRfeName(e.Name)).ToList();
        if (entries.Count == 0) return "No Red Faction II effects (.rfe) are selected.";
        string? archive = request.ArchiveFolder is { } folder ? Path.Combine(folder, request.ArchiveName) : null;
        var choice = request.Options as RfeConvertChoice;
        if (choice is null)
        {
            if (!request.Interactive) choice = new RfeConvertChoice(DefaultFolder(null));
            else
            {
                choice = AskBatch(entries.Count, DefaultFolder(null), request.AddFiles is not null);
                if (choice is null) return null;
            }
        }
        var all = request.AllEntries.Count > 0 ? request.AllEntries : request.Entries;
        var byName = new Dictionary<string, ArchiveBatchEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in all) byName.TryAdd(e.Name, e);
        byte[]? Read(string n)
        {
            try { return byName.TryGetValue(n, out var e) ? e.Read() : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        }
        string install = RfeTextures.InstallOf(archive) ?? "";
        int converted = 0, approximated = 0;
        var failed = new List<string>();
        var packfileFiles = new List<(string, byte[])>();
        var batchTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new List<string>();
        async Task Work(IProgress<(double, string)> progress, CancellationToken ct)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                progress.Report((i / (double)entries.Count, entry.Name));
                try
                {
                    var conversion = await Task.Run(() =>
                    {
                        var effect = RfeReader.Read(entry.Read(), entry.Name);
                        var packs = RfeTextures.PacksFor(entry.Name, [.. effect.Textures.Where(t => t.Length > 0)], byName.Keys, (n, _) => Read(n), request.ArchiveName, install.Length > 0 ? install : null, archive);
                        return RfeToVfx.Convert(effect, entry.Name, new RfeConvertOptions { Textures = choice.Textures, TexturePacks = packs });
                    }, ct);
                    // a texture the packfile has (or an earlier effect of this batch added) is not added again
                    if (choice.IntoPackfile && request.AddFiles is not null) packfileFiles.AddRange(PackfileFiles(conversion, byName.Keys, choice.Replace, batchTextures));
                    else outputs.AddRange(WriteFolder(choice.Folder, conversion, choice.Replace, null));
                    converted++;
                    if (conversion.Approximations > 0) approximated++;
                    LastConversion = conversion;
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    // whatever goes wrong with one entry (damage, a read that fails, a bug) is reported, and the batch goes on
                    failed.Add($"{entry.Name}: {ex.Message}");
                }
            }
        }
        bool done;
        if (request.RunAsync is { } run) done = await run("Converting effects", Work);
        else
        {
            try { await Work(new Progress<(double, string)>(), CancellationToken.None); done = true; }
            catch (OperationCanceledException) { done = false; }
        }
        if (!done) return null;
        if (packfileFiles.Count > 0 && request.AddFiles is { } addFiles) outputs.AddRange(addFiles("Convert effects to .vfx", packfileFiles, choice.Replace));
        LastOutputs = outputs;
        string summary = $"Converted {converted:N0} of {entries.Count:N0} effect{(entries.Count == 1 ? "" : "s")} to .vfx ({approximated:N0} with approximations)"
            + (failed.Count > 0 ? $"; {failed.Count:N0} could not be converted: {string.Join("; ", failed.Take(3))}" : "") + ".";
        if (failed.Count > 0 && converted == 0 && request.Interactive) shell.Dialogs.ShowError("Could not convert the effects", string.Join("\n", failed));
        shell.ShowStatus(summary);
        return summary;
    }

    /// <summary>The report as Problems lists it (information).</summary>
    public static IReadOnlyList<VfxDiagnostic> Diagnostics(RfeConversion conversion, string source) =>
        [.. conversion.Notes.Select(n => new VfxDiagnostic(n.Kind switch { RfeNoteKind.Approximated => "RFE002", RfeNoteKind.Dropped => "RFE003", _ => "RFE001" },
            DiagnosticSeverity.Info, n.ToString(),
            $"From the conversion of the Red Faction II effect {source}. Check approximated and dropped behaviour in the game.", VfxLocation.Document))];

    private void Open(string path, RfeConversion conversion)
    {
        if (!shell.OpenFile(path)) return;
        if (shell.ActiveDocument is VfxDocument doc && string.Equals(doc.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            doc.ConversionNotes = Diagnostics(conversion, conversion.OutputName.Replace(".vfx", ".rfe", StringComparison.OrdinalIgnoreCase));
            shell.ShowPanel(Problems.VfxProblemsPanel.PanelId);
        }
    }

    private static PegTextureSet PacksFor(AssetConversionRequest request, RfeFile effect)
    {
        if (request.Document is RfeDocument tab) return tab.Textures.Packs;
        var textures = effect.Textures.Where(t => t.Length > 0).ToList();
        if (request.ReadSibling is { } read && request.ArchiveEntryNames.Count > 0)
            return RfeTextures.PacksFor(request.FileName, textures, request.ArchiveEntryNames, (n, _) => read(n),
                Path.GetFileName(request.ArchivePath ?? "the packfile"), RfeTextures.InstallOf(request.ArchivePath), request.ArchivePath);
        if (request.FilePath is { } path)
        {
            var around = RfeSurroundings.ForFolder(path);
            return RfeTextures.PacksFor(request.FileName, textures, around.Names, around.Read, around.Label, around.Install, around.Source);
        }
        return new PegTextureSet("nothing beside it", []);
    }

    /// <summary>
    /// What a conversion adds to a packfile: the effect (it takes a free name unless replacing), and each texture the
    /// packfile does not hold yet (every one when replacing), none twice when <paramref name="added"/> collects a batch's.
    /// A texture already there is the one the effect names, so adding it again under a free name would only add an
    /// entry nothing uses.
    /// </summary>
    public static List<(string Name, byte[] Bytes)> PackfileFiles(RfeConversion conversion, IEnumerable<string> existing, bool replace, ISet<string>? added)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        ArgumentNullException.ThrowIfNull(existing);
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var files = new List<(string, byte[])> { (conversion.OutputName, conversion.Bytes) };
        foreach (var (name, bytes) in conversion.Textures)
        {
            if (!replace && have.Contains(name)) continue;
            if (added is not null && !added.Add(name)) continue;
            files.Add((name, bytes));
        }
        return files;
    }

    /// <summary>The folder outputs go to by default: the last one used, the source's folder (not inside a Red Faction II install), or Documents.</summary>
    private string DefaultFolder(string? sourcePath)
    {
        if (_lastFolder is { } last && Directory.Exists(last)) return last;
        if (sourcePath is not null && RfeTextures.InstallOf(sourcePath) is null && Path.GetDirectoryName(sourcePath) is { Length: > 0 } folder && Directory.Exists(folder)) return folder;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>
    /// Writes the effect and its textures into <paramref name="folder"/>. The effect takes a free name ("x (2).vfx") rather
    /// than replace the source or a Red Faction II file, or any file unless <paramref name="replace"/>; a texture of the same
    /// name is kept unless replacing.
    /// </summary>
    public List<string> WriteFolder(string folder, RfeConversion conversion, bool replace, string? sourcePath)
    {
        Directory.CreateDirectory(folder);
        string? source = Rf2ClipOutputs.FullPath(sourcePath ?? "");
        bool MustKeep(string p) => (source is not null && string.Equals(Rf2ClipOutputs.FullPath(p), source, StringComparison.OrdinalIgnoreCase)) || IsRedFactionIIFile(p);
        var written = new List<string>();
        string main = Rf2ClipOutputs.FolderPath(folder, conversion.OutputName, replace, File.Exists, MustKeep);
        AtomicFile.WriteAllBytes(main, conversion.Bytes);
        written.Add(main);
        foreach (var (name, bytes) in conversion.Textures)
        {
            string path = Path.Combine(folder, name);
            if (File.Exists(path) && (!replace || MustKeep(path))) continue;
            AtomicFile.WriteAllBytes(path, bytes);
            written.Add(path);
        }
        if (!shell.IsDiagnosticRun) _lastFolder = folder;
        return written;
    }

    /// <summary>True for a file that is a Red Faction II effect, texture pack or mesh by its first bytes.</summary>
    public static bool IsRedFactionIIFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16);
            Span<byte> head = stackalloc byte[8];
            int n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return RfeReader.IsEffect(head[..n]) || PegCodec.IsPeg(head[..n]) || LegacyMeshSupport.IsRedFactionII(head[..n]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // when in doubt, keep it
        }
    }

    private static readonly string[] TextureChoices =
    [
        "Translate the names to Red Faction names (REDUX's tables)",
        "Keep the Red Faction II names and write the textures as .tga",
    ];

    /// <summary>The window; <paramref name="conversions"/> holds the conversion for each texture mode (made off the UI thread), so a change only shows another report.</summary>
    private RfeConvertChoice? Ask(IReadOnlyDictionary<Rf2TextureMode, RfeConversion> conversions, string folder, bool canAdd)
    {
        var form = new VfxForm("Convert to a Red Faction effect", shell.MainWindow, "_Convert") { Width = 640 };
        var target = form.Combo("_Write to", canAdd ? ["A folder", "The packfile (new entries, one undo step)"] : ["A folder"], 0, "Where the converted effect goes");
        var box = form.Text("_Folder", folder, "The folder the .vfx (and its textures) is written to");
        var textures = form.Combo("_Textures", TextureChoices, 0, "Red Faction finds textures by name: translate the Red Faction II names, or keep them and write the textures from the .peg packs beside the effect");
        var replace = form.Check("_Replace", false, "Replace files of the same name (never the source or a Red Faction II file: those get a free name)");
        RfeConvertChoice Read() => new(box.Text.Trim(), textures.SelectedIndex == 1 ? Rf2TextureMode.KeepOriginals : Rf2TextureMode.Translate, replace.IsChecked == true, target.SelectedIndex == 1);
        form.Changed += (_, _) =>
        {
            var choice = Read();
            box.IsEnabled = !choice.IntoPackfile;
            var conversion = conversions[choice.Textures];
            form.Summary = $"{conversion.OutputName}{(conversion.Textures.Length > 0 ? $" and {conversion.Textures.Length} texture{(conversion.Textures.Length == 1 ? "" : "s")}" : "")}: "
                + $"{conversion.Approximations} line{(conversion.Approximations == 1 ? "" : "s")} approximate or drop something (they are listed in Problems after converting).\n"
                + string.Join("\n", conversion.Notes.Select(n => "• " + n));
            form.CanAccept = choice.IntoPackfile || choice.Folder.Length > 0;
        };
        return form.ShowDialog() == true ? Read() : null;
    }

    private RfeConvertChoice? AskBatch(int count, string folder, bool canAdd)
    {
        var form = new VfxForm("Convert effects", shell.MainWindow, "_Convert");
        var target = form.Combo("_Write to", canAdd ? ["A folder", "The packfile (new entries, one undo step)"] : ["A folder"], 0, "Where the converted effects go");
        var box = form.Text("_Folder", folder, "The folder the .vfx files (and their textures) are written to");
        var textures = form.Combo("_Textures", TextureChoices, 0, "Translate the Red Faction II texture names, or keep them and write the textures from the .peg packs");
        var replace = form.Check("_Replace", false, "Replace files of the same name (never a Red Faction II file)");
        form.Changed += (_, _) =>
        {
            form.Summary = $"{count:N0} Red Faction II effect{(count == 1 ? "" : "s")} to Red Faction .vfx files. The summary counts the effects that needed approximations; convert one effect on its own (Convert to) to see its full report.";
            box.IsEnabled = target.SelectedIndex == 0;
        };
        if (form.ShowDialog() != true) return null;
        return new RfeConvertChoice(box.Text.Trim(), textures.SelectedIndex == 1 ? Rf2TextureMode.KeepOriginals : Rf2TextureMode.Translate, replace.IsChecked == true, target.SelectedIndex == 1);
    }
}
