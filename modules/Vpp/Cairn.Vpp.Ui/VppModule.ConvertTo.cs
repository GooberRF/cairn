using System.Windows;
using System.Windows.Controls;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ps2;
using Cairn.Vpp.Ui.Commands;
using Cairn.Vpp.Ui.Conversion;
using Cairn.Vpp.Ui.Documents;

namespace Cairn.Vpp.Ui;

// Convert to…: the entry list's submenu for one selected entry (every module's targets for its name), and this module's
// own targets (IAssetConverter): images to .dds and PEG texture packs to .tga, in their packfile; a PEG tab's textures.
public sealed partial class VppModule : IAssetConverter
{
    /// <summary>The entry list's "Convert to" submenu header (self-tests, captures).</summary>
    public const string ConvertToHeader = "Convert t_o";

    /// <summary>
    /// Fills the context menu's Convert to submenu for <paramref name="doc"/>'s selection: shown for one entry some module
    /// converts, hidden otherwise. Called each time the menu opens (and by self-tests).
    /// </summary>
    public void FillConvertToMenu(VppDocument doc, MenuItem menu)
    {
        var item = doc.SelectedItems.Count == 1 ? doc.SelectedItems[0] : null;
        // in a Red Faction II group a file missing on disk has nothing to convert
        bool none = item is null || Shell is null || (doc.IsReadOnly && item.Source is MissingFileSource);
        // some targets depend on what the entry holds (a Red Faction II .rfa): its first bytes go along
        var choices = none ? [] : AssetConversions.TargetsFor(Shell!.Modules, item!.Name, HeadOf(item));
        AssetConversions.FillMenu(menu, choices, c => doc.Commands.Fire(() => ConvertEntryAsync(doc, item!, c)));
        menu.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        menu.IsEnabled = !doc.IsBusy;
    }

    /// <summary>The first 16 bytes of an entry (fewer for a shorter one), or none when it cannot be read.</summary>
    private static byte[] HeadOf(VppItem item)
    {
        try
        {
            using var stream = item.Source.Open();
            var head = new byte[(int)Math.Clamp(item.Size, 0, 16)];
            int read = 0;
            while (read < head.Length && stream.Read(head, read, head.Length - read) is int n and > 0) read += n;
            return read == head.Length ? head : head[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Converts the entry <paramref name="item"/> of <paramref name="doc"/> with <paramref name="choice"/>: this module's
    /// own targets work on the packfile directly; another module's converter gets the entry's bytes with this packfile's
    /// services (add the outputs as ONE undo step, read the other entries). Returns what the converter returned.
    /// </summary>
    public async Task<string?> ConvertEntryAsync(VppDocument doc, VppItem item, ConversionChoice choice, bool interactive = true, string? outputFolder = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(choice);
        if (doc.IsBusy || !doc.EnsureReadable([item])) return null;
        if (ReferenceEquals(choice.Converter, this)) return await ConvertOwnEntryAsync(doc, item, choice.Target.Extension, interactive, outputFolder);
        byte[] bytes;
        try
        {
            using (BusyTracker.Begin("reading " + item.Name)) bytes = await Task.Run(item.Source.ReadAll);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Shell.Dialogs.ShowError($"Could not read {item.Name}", ex.Message);
            return null;
        }
        var package = doc.Current;
        var request = new AssetConversionRequest(item.Name, bytes, choice.Target.Extension)
        {
            ArchivePath = doc.FilePath,
            Interactive = interactive,
            OutputFolder = outputFolder,
            // a read-only Red Faction II group takes no files: "into the packfile" is not offered
            AddToArchive = doc.IsReadOnly ? null : (label, files, replace) => AddToPackfile(doc, label, files, replace),
            ArchiveEntryNames = [.. package.Items.Select(i => i.Name)],
            ReadSibling = name =>
            {
                try { return package.Find(name)?.Source.ReadAll(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
            },
        };
        string? result = await AssetConversions.RunAsync(Shell, choice, request);
        if (result is not null) doc.ShowStatus($"Converted {item.Name}: {Path.GetFileName(result)}");
        return result;
    }

    // ---- this module's targets (IAssetConverter) ---------------------------------------------------------------

    /// <inheritdoc/>
    public bool CanConvert(string sourceName, string targetExtension) =>
        (DdsConversion.IsConvertible(sourceName) && string.Equals(targetExtension, ".dds", StringComparison.OrdinalIgnoreCase))
        || (Ps2Packfiles.IsPeg(sourceName) && string.Equals(targetExtension, ".tga", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public IReadOnlyList<ConversionTarget> TargetsFor(string sourceName)
    {
        if (DdsConversion.IsConvertible(sourceName))
            return [new(".dds", "DDS texture (.dds)", "Convert the image to DDS for Alpine Faction (compressed or not, with mipmaps): in the packfile or into a folder", true)];
        if (Ps2Packfiles.IsPeg(sourceName))
            return [new(".tga", "Texture files (.tga)", "Convert the PEG texture pack (PlayStation 2 or Red Faction II) to the .tga files the PC game loads (numbered frames plus an .atx for animated ones)", false)];
        return [];
    }

    /// <summary>
    /// A PEG tab: its textures written to a folder. An entry of an open packfile: the packfile's own conversion (the DDS
    /// window, the PEG window). Bytes of no open packfile: a PEG opens as a tab first; an image converts to a folder.
    /// </summary>
    public async Task<string?> ConvertAsync(AssetConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanConvert(request.FileName, request.TargetExtension)) return null;
        if (request.Document is VppDocument { PegSource: not null } pegTab) return await ExtractPegTabAsync(pegTab, request.Interactive, request.OutputFolder);
        if (request.ArchivePath is { } archive && OpenPackfile(archive) is { } doc && doc.Current.Find(request.FileName) is { } item)
            return await ConvertOwnEntryAsync(doc, item, request.TargetExtension, request.Interactive, request.OutputFolder);
        if (Ps2Packfiles.IsPeg(request.FileName))
        {
            VppDocument peg;
            try { peg = OpenPeg(request.Bytes, request.FileName, request.FilePath, null); }
            catch (Cairn.Formats.AssetFormatException ex)
            {
                Shell.Dialogs.ShowError($"Could not convert {request.FileName}", ex.Message);
                return null;
            }
            Shell.AddDocument(peg);
            return await ExtractPegTabAsync(peg, request.Interactive, request.OutputFolder);
        }
        return await ConvertImageBytesAsync(request);
    }

    /// <summary>The open packfile tab saved at <paramref name="path"/>, or null.</summary>
    private VppDocument? OpenPackfile(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return Shell.Documents.OfType<VppDocument>().FirstOrDefault(d => d.FilePath is { } p && string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An entry converted in its packfile: an image through the DDS converter, a .peg through the PEG converter.</summary>
    private async Task<string?> ConvertOwnEntryAsync(VppDocument doc, VppItem item, string extension, bool interactive, string? outputFolder)
    {
        if (doc.IsReadOnly) return await ConvertReadOnlyEntryAsync(doc, item, extension, interactive, outputFolder);
        if (string.Equals(extension, ".dds", StringComparison.OrdinalIgnoreCase))
        {
            var (images, skipped) = DdsConversion.Split([item]);
            if (images.Count == 0) return null;
            DdsConvertSettings settings;
            if (interactive)
            {
                var window = VppDdsConvertWindow.Create(doc, images, skipped);
                if (window.ShowDialog() != true || window.Chosen is not { } chosen) return null;
                settings = chosen;
            }
            else
            {
                settings = DdsConvertSettings.Load(_store) with
                {
                    Output = outputFolder is null ? DdsOutputMode.KeepOriginals : DdsOutputMode.Folder,
                    Folder = outputFolder,
                    Existing = DdsExistingPolicy.Skip,
                };
            }
            if (!await RunConversionAsync(doc, images, settings)) return null;
            string dds = DdsConversion.DdsNameFor(item.Name);
            return settings.Output == DdsOutputMode.Folder && settings.Folder is { } folder ? Path.Combine(folder, dds) : dds;
        }
        var report = interactive ? await ConvertSelectedPegsAsync(doc, [item]) : await ConvertPegTexturesAsync(doc, AllTextures(doc, [item.Name]), showSkipped: false);
        return report?.AddedFiles.FirstOrDefault();
    }

    /// <summary>
    /// An entry of a read-only packfile (a Red Faction II group) converted into a folder, never into the packfile: a
    /// .peg's textures as .tga files (as for a PEG tab, without opening one), an image as .dds with the remembered settings.
    /// </summary>
    private async Task<string?> ConvertReadOnlyEntryAsync(VppDocument doc, VppItem item, string extension, bool interactive, string? outputFolder)
    {
        byte[] bytes;
        try
        {
            using (BusyTracker.Begin("reading " + item.Name)) bytes = await Task.Run(item.Source.ReadAll);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Shell.Dialogs.ShowError($"Could not read {item.Name}", ex.Message);
            return null;
        }
        if (string.Equals(extension, ".dds", StringComparison.OrdinalIgnoreCase))
            return await ConvertImageBytesAsync(new AssetConversionRequest(item.Name, bytes, extension) { Interactive = interactive, OutputFolder = outputFolder ?? doc.Folder });
        VppDocument peg;
        try { peg = OpenPeg(bytes, item.Name, null, $"{item.Name} in {doc.DisplayName}"); }
        catch (Cairn.Formats.AssetFormatException ex)
        {
            Shell.Dialogs.ShowError($"Could not convert {item.Name}", ex.Message);
            return null;
        }
        using (peg) return await ExtractPegTabAsync(peg, interactive, outputFolder);
    }

    /// <summary>A PEG tab's textures (the files it shows) written to a folder: picked, or <paramref name="outputFolder"/>. Returns the first path.</summary>
    private async Task<string?> ExtractPegTabAsync(VppDocument doc, bool interactive, string? outputFolder)
    {
        if (doc.Current.Count == 0)
        {
            Shell.Dialogs.ShowError($"Could not convert {doc.DisplayName}", "The texture pack has no textures Cairn could convert.");
            return null;
        }
        string? folder = outputFolder;
        if (interactive && folder is null)
        {
            folder = Shell.Dialogs.PickFolder(Settings.ExtractFolderOr(null), $"Write the textures of {doc.DisplayName} to");
            if (folder is null) return null;
            Settings.LastExtractFolder = folder;
        }
        folder ??= Settings.ExtractFolderOr(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))!;
        var written = await doc.Commands.ExtractAsync(doc.Current.Items, folder, interactive ? VppOverwritePolicy.Ask : VppOverwritePolicy.SkipExisting, quiet: !interactive);
        return written?.FirstOrDefault();
    }

    /// <summary>An image that is in no open packfile: encoded with the remembered DDS settings into a folder (picked, or the request's).</summary>
    private async Task<string?> ConvertImageBytesAsync(AssetConversionRequest request)
    {
        string? folder = request.OutputFolder ?? (request.FilePath is { } path ? Path.GetDirectoryName(path) : null);
        if (request.Interactive)
        {
            folder = Shell.Dialogs.PickFolder(folder ?? Settings.ExtractFolderOr(null), $"Write {DdsConversion.DdsNameFor(request.FileName)} to");
            if (folder is null) return null;
        }
        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var encode = DdsConvertSettings.Load(_store).Encode;
        var source = new VppItem(request.FileName, new MemorySource(request.Bytes), VppItemState.Added);
        DdsConvertResult result;
        using (BusyTracker.Begin("converting " + request.FileName)) result = await Task.Run(() => DdsConversion.ConvertOne(source, encode, CancellationToken.None));
        if (result.Result is not { } encoded)
        {
            Shell.Dialogs.ShowError($"Could not convert {request.FileName}", result.Error ?? "The image could not be encoded.");
            return null;
        }
        string target = Path.Combine(folder, result.DdsName);
        try
        {
            Directory.CreateDirectory(folder);
            Cairn.Workspace.AtomicFile.WriteAllBytes(target, encoded.Bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Shell.Dialogs.ShowError($"Could not write {result.DdsName}", ex.Message);
            return null;
        }
        Shell.ShowStatus($"Wrote {result.DdsName} to {folder}");
        return target;
    }
}
