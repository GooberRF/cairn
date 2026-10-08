using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cairn.Formats;
using Cairn.Formats.Imaging;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ps2;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui;

// PlayStation 2 content: .peg texture packs open as packfile-like documents (saved as PC packfiles), selected .peg
// entries convert with "Convert to .tga..." (a dialog per selection), .peg entries preview their textures, and any image
// (or PEG texture) extracts as PNG.
public sealed partial class VppModule : IAssetPreviewProvider
{
    /// <summary>The .peg document kind (opens as a packfile of the converted textures).</summary>
    public PegKind PegKind { get; }

    /// <summary>The menu item of "Convert to .tga..." (the Packfile menu and the entry list's context menu).</summary>
    public const string ConvertPegsHeader = "Convert to .t_ga...";

    /// <summary>Its tooltip.</summary>
    public const string ConvertPegsTip = "Convert the selected PlayStation 2 texture packs (.peg) to .tga files the PC game loads: choose the textures first (one undoable change)";

    /// <summary>True when the selection holds a .peg entry (what "Convert to .tga..." converts).</summary>
    public static bool HasSelectedPegs(VppDocument doc) => doc.SelectedItems.Any(i => Ps2Packfiles.IsPeg(i.Name));

    /// <summary>Adds "Convert to .tga..." and "Extract as PNG..." to the Packfile menu.</summary>
    private void InitializePs2()
    {
        if (_menus.FirstOrDefault(m => m.Slot == MenuSlot.TopLevel && m.Item is MenuItem { Name: "PackfileMenu" })?.Item is not MenuItem menu) return;
        var convert = new MenuItem
        {
            Header = ConvertPegsHeader,
            ToolTip = ConvertPegsTip,
            Command = Cmd(d => d.Commands.Fire(() => ConvertSelectedPegsAsync(d)), HasSelectedPegs),
        };
        var png = new MenuItem
        {
            Header = "Extract as P_NG...",
            ToolTip = "Write the selected images (and every texture of selected PEG texture packs) to a folder as PNG files",
            Command = AsyncCmd(d => ExtractAsPngAsync(d), d => d.SelectedItems.Any(i => PngExport.CanExport(i.Name))),
        };
        // after "Extract all..." (index 4: add files, add folder, separator, extract selected, extract all)
        int at = Math.Min(5, menu.Items.Count);
        menu.Items.Insert(at, png);
        // before the separator that precedes Validate, next to the DDS converter
        menu.Items.Insert(Math.Max(menu.Items.Count - 2, 0), convert);
    }

    // ---- opening a .peg ---------------------------------------------------------------------------------------

    /// <summary>
    /// A packfile document holding <paramref name="bytes"/>' textures as the files the PC game loads (never saved over
    /// the .peg: it has no path, so Save is Save As a .vpp). Converted off the UI thread while the window stays live.
    /// </summary>
    /// <exception cref="AssetFormatException">Not a PEG Cairn can read.</exception>
    internal VppDocument OpenPeg(byte[] bytes, string name, string? path, string? originText)
    {
        // a work copy ("Open in Cairn" on a packfile entry) is described as the entry it came from
        if (path is not null && IsWorkCopy(path) && ArchiveEntryOf(path) is { } entry)
        {
            name = entry.EntryName;
            originText ??= $"{entry.EntryName} in {Path.GetFileName(entry.ArchivePath)}";
        }
        originText ??= path ?? name;
        bool decodeMpeg2 = Settings.DecodeMpeg2;
        var blackKey = Settings.Mpeg2BlackKey;
        var task = Task.Run(() => PegConverter.Convert(bytes, name, fpsFor: FpsFor, decodeMpeg2: decodeMpeg2, blackKey: blackKey));
        using (BusyTracker.Begin("converting " + name)) VppDocument.WaitPumping(task, Shell.Dispatcher);
        var result = task.GetAwaiter().GetResult();
        if (result.Error is { } error) throw new AssetFormatException(error);
        var items = result.Files.Select(f => new VppItem(f.Name, new MemorySource(f.Bytes), VppItemState.Added)).ToImmutableArray();
        var doc = new VppDocument(this, new VppPackage(null, items), name, null, "Converted from " + originText) { PegSource = result };
        if (items.Length > 0) doc.SelectNames([items[0].Name]);
        return doc;
    }

    /// <summary>
    /// The PC game's frame rate for an animated texture of this name (its stock <c>.vbm</c>, through the game data), or
    /// null when the game data has none. Any thread.
    /// </summary>
    internal int? FpsFor(string textureName)
    {
        try
        {
            if (Shell?.Assets.Resolver is not { } resolver) return null;
            string stem = Path.GetFileNameWithoutExtension(textureName);
            if (resolver.Resolve(stem + ".vbm") is not { } location || !location.ResolvedName.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase)) return null;
            byte[] head;
            using (var stream = location.Open())
            {
                head = new byte[32];
                if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
            }
            var info = VbmCodec.ReadInfo(head, location.ResolvedName);
            return info.FrameCount > 1 && info.Fps is > 0 and <= 120 ? info.Fps : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageDecodeException or AssetFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---- converting a PlayStation 2 packfile's PEG entries -----------------------------------------------------

    /// <summary>
    /// "Convert to .tga...": reads the selected .peg entries (or <paramref name="pegs"/>), shows the dialog that lists
    /// their textures, then converts what was ticked (<see cref="ConvertPegTexturesAsync"/>). Null when cancelled.
    /// </summary>
    public async Task<Ps2ConvertReport?> ConvertSelectedPegsAsync(VppDocument doc, IReadOnlyList<VppItem>? pegs = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.IsBusy) return null;
        pegs ??= [.. doc.SelectedItems.Where(i => Ps2Packfiles.IsPeg(i.Name))];
        if (pegs.Count == 0) { doc.ShowStatus("Select .peg entries to convert them to .tga."); return null; }
        if (!doc.EnsureReadable(pegs)) return null;
        var picks = ReadPegs(doc, pegs);
        if (picks.Count == 0) return null;
        var window = Dialogs.VppPegConvertWindow.Create(doc, picks, Settings);
        if (window.ShowDialog() != true || window.Chosen is not { } choice) return null;
        return await ConvertPegTexturesAsync(doc, choice);
    }

    /// <summary>Reads the directories of <paramref name="pegs"/> (off the UI thread, the window kept live), with their positions in the packfile.</summary>
    internal IReadOnlyList<Dialogs.PegPick> ReadPegs(VppDocument doc, IReadOnlyList<VppItem> pegs)
    {
        var package = doc.Current;
        var indexed = pegs.Select(p => (Item: p, Index: IndexOf(package, p))).Where(p => p.Index >= 0).ToList();
        var task = Task.Run(() => indexed.Select(p =>
        {
            try { return new Dialogs.PegPick(p.Item, p.Index, PegCodec.Read(p.Item.Source.ReadAll(), p.Item.Name), null); }
            catch (Exception ex) when (ex is AssetFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new Dialogs.PegPick(p.Item, p.Index, null, ex.Message);
            }
        }).ToList());
        using (BusyTracker.Begin("reading PEG texture packs")) VppDocument.WaitPumping(task, Shell.Dispatcher);
        return task.GetAwaiter().GetResult();
    }

    private static int IndexOf(VppPackage package, VppItem item)
    {
        for (int i = 0; i < package.Count; i++) if (ReferenceEquals(package.Items[i], item)) return i;
        return -1;
    }

    /// <summary>The choice that converts every texture of <paramref name="pegs"/> (all .peg entries when null), black as the settings say.</summary>
    public Dialogs.PegConvertChoice AllTextures(VppDocument doc, IEnumerable<string>? pegs = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var names = pegs is null ? null : new HashSet<string>(pegs, Editing.VppNames.Comparer);
        var entries = doc.Current.Items.Select((item, index) => (item, index))
            .Where(p => Ps2Packfiles.IsPeg(p.item.Name) && (names is null || names.Contains(p.item.Name)))
            .Select(p => new PegEntryChoice(p.index)).ToList();
        return new Dialogs.PegConvertChoice(entries, Settings.Mpeg2BlackKey);
    }

    /// <summary>
    /// Converts the chosen textures of the chosen .peg entries as the document's operation (progress, Cancel), then
    /// replaces those entries by their files (or adds the files after them) as ONE undo step. Returns the report, or
    /// null when cancelled or nothing to do.
    /// </summary>
    public async Task<Ps2ConvertReport?> ConvertPegTexturesAsync(VppDocument doc, Dialogs.PegConvertChoice choice, bool showSkipped = true)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(choice);
        if (doc.IsBusy) return null;
        var package = doc.Current;
        var chosen = choice.Entries.Where(e => e.Index >= 0 && e.Index < package.Count && Ps2Packfiles.IsPeg(package.Items[e.Index].Name)).ToList();
        var pegs = chosen.Select(e => package.Items[e.Index]).ToList();
        if (pegs.Count == 0) { doc.ShowStatus("No PEG texture packs to convert."); return null; }
        if (!doc.EnsureReadable(pegs)) return null;
        IReadOnlyList<PegEntryConversion>? conversions = null;
        bool decodeMpeg2 = Settings.DecodeMpeg2;
        bool finished = await doc.RunOperationAsync(pegs.Count == 1 ? $"Converting {pegs[0].Name}" : "Converting PEG textures", async op =>
            conversions = await Task.Run(() => Ps2Packfiles.ConvertEntries(package,
                (done, total, name) => op.Report(total == 0 ? 1 : (double)done / total, name), FpsFor, op.Token, decodeMpeg2, choice.BlackKey, chosen), op.Token));
        if (!finished || conversions is null) return null;
        Ps2ConvertReport? report = null;
        doc.ApplyEdit(pegs.Count == 1 ? $"Convert {pegs[0].Name} to .tga" : $"Convert {pegs.Count:N0} PEG texture packs to .tga", p =>
        {
            report = Ps2Packfiles.Apply(p, conversions, choice.KeepPegs);
            return report.Package;
        });
        if (report is null) return null;
        doc.Notice = report.Summary + (choice.KeepPegs ? " Undo (Ctrl+Z) removes the files again" : " Undo (Ctrl+Z) brings the .peg entries back") + "; Save writes the packfile.";
        if (report.AddedFiles.Count > 0) doc.SelectNames([report.AddedFiles[0]]);
        if (showSkipped && (report.Skipped.Count > report.NotChosen || report.Unreadable.Count > 0 || report.Conflicts is { Count: > 0 }))
        {
            // Unreadable packs and textures whose versions differ first: they are what to look at; identical copies last.
            var conflicts = report.Conflicts ?? [];
            var listed = new HashSet<string>(conflicts, StringComparer.Ordinal);
            var rest = report.Skipped.Where(s => !listed.Contains(s.Reason) && s.Reason != PegConverter.NotChosenReason).ToList();
            var lines = report.Unreadable.Select(u => $"• {u.Peg}: {u.Reason}")
                .Concat(conflicts.Select(c => "• " + c))
                .Concat(rest.Where(s => !s.Reason.Contains("one copy is enough", StringComparison.Ordinal)).Select(s => $"• {s.Texture} ({s.Peg}): {s.Reason}"))
                .Concat(rest.Where(s => s.Reason.Contains("one copy is enough", StringComparison.Ordinal)).Select(s => $"• {s.Texture} ({s.Peg}): {s.Reason}")).ToList();
            Shell.Dialogs.Choose(report.Skipped.Count > report.NotChosen || report.Unreadable.Count > 0 ? "Some textures were not converted" : "Textures replaced", report.Summary + "\n\n" + string.Join("\n", lines.Take(14))
                + (lines.Count > 14 ? $"\n... and {lines.Count - 14:N0} more" : string.Empty), ["OK"], 0);
        }
        return report;
    }

    // ---- previews of .peg entries -------------------------------------------------------------------------------

    /// <inheritdoc/>
    public bool CanPreview(string fileName) => Ps2Packfiles.IsPeg(fileName ?? string.Empty);

    /// <inheritdoc/>
    public FrameworkElement? CreatePreview(byte[] bytes, string fileName) => new PegPreview(bytes, fileName, Settings.DecodeMpeg2, Settings.Mpeg2BlackKey);

    // ---- extract as PNG -----------------------------------------------------------------------------------------

    /// <summary>Asks for a folder and writes the selected images (and the textures of selected .peg entries) as PNG files.</summary>
    public async Task ExtractAsPngAsync(VppDocument doc)
    {
        var items = doc.SelectedItems.Where(i => PngExport.CanExport(i.Name)).ToList();
        if (items.Count == 0) { doc.ShowStatus("Select images or PEG texture packs to extract as PNG."); return; }
        var folder = Shell.Dialogs.PickFolder(doc.Folder, items.Count == 1 ? $"Extract {items[0].Name} as PNG to" : $"Extract {items.Count:N0} images as PNG to");
        if (folder is not null) await ExtractAsPngAsync(doc, items, folder);
    }

    /// <summary>Writes <paramref name="items"/> into <paramref name="folder"/> as PNG files (never over an existing file). Returns the paths written.</summary>
    public async Task<IReadOnlyList<string>> ExtractAsPngAsync(VppDocument doc, IReadOnlyList<VppItem> items, string folder)
    {
        if (doc.IsBusy || !doc.EnsureReadable(items)) return [];
        var written = new List<string>();
        var failures = new List<string>();
        bool decodeMpeg2 = Settings.DecodeMpeg2;
        var blackKey = Settings.Mpeg2BlackKey;
        await doc.RunOperationAsync(items.Count == 1 ? $"Extracting {items[0].Name} as PNG" : $"Extracting {items.Count:N0} images as PNG", op => Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            for (int i = 0; i < items.Count; i++)
            {
                op.Token.ThrowIfCancellationRequested();
                op.Report((double)i / items.Count, items[i].Name);
                try
                {
                    foreach (var (name, image) in PngExport.Images(items[i].Name, items[i].Source.ReadAll(), op.Token, decodeMpeg2, blackKey))
                    {
                        string path = FreePath(folder, Work.VppWorkFolder.SafeFileName(name));
                        AtomicFile.WriteAllBytes(path, PngEncoder.Encode(image));
                        written.Add(path);
                    }
                }
                catch (Exception ex) when (ex is ImageDecodeException or AssetFormatException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{items[i].Name}: {ex.Message}");
                }
            }
            op.Report(1, "done");
        }, op.Token));
        doc.ShowStatus(string.Create(CultureInfo.CurrentCulture, $"Wrote {written.Count:N0} PNG file(s) to {folder}") + (failures.Count > 0 ? $"; {failures.Count:N0} could not be decoded" : string.Empty));
        if (failures.Count > 0) Shell.Dialogs.ShowError($"{failures.Count} file(s) could not be extracted as PNG", "The others were written.", string.Join(Environment.NewLine, failures));
        return written;
    }

    private static string FreePath(string folder, string file)
    {
        string path = Path.Combine(folder, file);
        string stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
        return path;
    }
}

/// <summary>Which entries "Extract as PNG" writes, and the images it writes for each.</summary>
internal static class PngExport
{
    private static readonly HashSet<string> Exportable = new(StringComparer.OrdinalIgnoreCase) { ".tga", ".dds", ".png", ".jpg", ".jpeg", ".vbm", ".peg" };

    /// <summary>True for an image or a PEG texture pack.</summary>
    public static bool CanExport(string name) => Exportable.Contains(Path.GetExtension(name));

    /// <summary>
    /// (file name, image) pairs for one entry: an image as "stem.png"; an animated VBM's frames as "stem_00.png"...;
    /// a PEG's textures as "texture.png" (animations numbered; MPEG-2 backgrounds only when <paramref name="decodeMpeg2"/>,
    /// with black as transparent when <paramref name="blackKey"/> is given).
    /// </summary>
    public static IEnumerable<(string Name, BgraImage Image)> Images(string name, byte[] bytes, CancellationToken ct, bool decodeMpeg2 = true, PegBlackKey? blackKey = null)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (Ps2Packfiles.IsPeg(name))
        {
            var pack = PegCodec.Read(bytes, name);
            foreach (var t in pack.Textures.Where(t => t.CanDecode && (decodeMpeg2 || !t.IsMpeg2)))
            {
                ct.ThrowIfCancellationRequested();
                string textureStem = PegConverter.StemOf(t.Name, t.Index);
                if (t.FrameCount == 1) { yield return (textureStem + ".png", PegCodec.DecodeFrame(bytes, t, 0, 0, PegBlackKey.For(t, blackKey))); continue; }
                var names = PegConverter.FrameNames(textureStem, t.FrameCount);
                for (int f = 0; f < t.FrameCount; f++) yield return (Path.ChangeExtension(names[f], ".png"), PegCodec.DecodeFrame(bytes, t, f));
            }
            yield break;
        }
        if (ImageProbe.Detect(bytes, name) == ImageContainer.Vbm && VbmCodec.ReadInfo(bytes, name) is { FrameCount: > 1 } vbm)
        {
            var names = PegConverter.FrameNames(stem, vbm.FrameCount);
            for (int f = 0; f < vbm.FrameCount; f++)
            {
                ct.ThrowIfCancellationRequested();
                yield return (Path.ChangeExtension(names[f], ".png"), VbmCodec.DecodeFrame(bytes, f, name));
            }
            yield break;
        }
        yield return (stem + ".png", ImageDecoder.Decode(bytes, name));
    }
}

/// <summary>The .peg document kind: a PlayStation 2 texture pack opens as a packfile of its textures converted for the PC game.</summary>
public sealed class PegKind(VppModule module) : IDocumentKind
{
    public string Id => "vpp.peg";
    public string DisplayName => "PS2 texture pack";
    public IReadOnlyList<string> Extensions { get; } = [".peg"];
    public string FileFilter => "PS2 texture packs (*.peg)|*.peg";
    public bool CanCreateNew => false;
    public string AssociationDescription => "Red Faction PS2 texture pack";

    public IDocument? CreateNew() => null;

    /// <summary>Reads the .peg (never written to) and opens its textures as a new, unsaved packfile.</summary>
    public IDocument Open(string path)
    {
        string full = Path.GetFullPath(path);
        return module.OpenPeg(File.ReadAllBytes(full), Path.GetFileName(full), full, null);
    }

    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) => module.OpenPeg(bytes, displayName, null, originText);

    /// <summary>Recovery data of a converted texture pack is a packfile's (the document is a packfile).</summary>
    public IDocument Restore(RecoverySnapshot snapshot) => module.Kind.Restore(snapshot);
}
