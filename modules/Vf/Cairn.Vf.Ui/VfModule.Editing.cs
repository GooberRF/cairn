using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Formats.Imaging;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;
using Cairn.Vf.Model;
using Cairn.Vf.Sheets;
using Cairn.Vf.Ui.Dialogs;
using Cairn.Vf.Ui.Documents;

namespace Cairn.Vf.Ui;

/// <summary>The fonts module's editing commands: the Font menu, its toolbar buttons and shortcuts, image sheets.</summary>
public sealed partial class VfModule
{
    private List<MenuContribution>? _menus;
    private List<ToolbarContribution>? _toolbar;
    private List<ShortcutInfo>? _shortcuts;
    private ModuleSettings? _settings;
    private RelayCommand? _replace, _paste, _range, _height, _export, _import;

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        shell.ActiveDocumentChanged += (_, _) => RefreshCommands();
    }

    private ModuleSettings Settings => _settings ??= new ModuleSettings(Shell.Settings, Id);

    private VfDocument? Active => Shell?.ActiveDocument as VfDocument;

    private static bool IsFont(IDocument? d) => d is VfDocument;

    private bool HasGlyph() => Active is { } d && d.SelectedGlyph >= 0 && d.SelectedGlyph < d.Current.GlyphCount;

    /// <summary>Replace the selected glyph with an image file (Ctrl+R).</summary>
    public RelayCommand ReplaceGlyphCommand => _replace ??= new RelayCommand(() => { if (Active is { } d) ReplaceFromFile(d); }, HasGlyph);
    /// <summary>Replace the selected glyph with the image on the clipboard (Ctrl+Shift+V).</summary>
    public RelayCommand PasteGlyphCommand => _paste ??= new RelayCommand(() => { if (Active is { } d) ReplaceFromClipboard(d); }, HasGlyph);
    /// <summary>Add or remove characters.</summary>
    public RelayCommand RangeCommand => _range ??= new RelayCommand(() => { if (Active is { } d) ChangeRange(d); }, () => Active is not null);
    /// <summary>Change the font height.</summary>
    public RelayCommand HeightCommand => _height ??= new RelayCommand(() => { if (Active is { } d) ChangeHeight(d); }, () => Active is not null);
    /// <summary>Export an image sheet (Ctrl+Shift+E).</summary>
    public RelayCommand ExportSheetCommand => _export ??= new RelayCommand(() => { if (Active is { } d) ExportSheetInteractive(d); }, () => Active is { Current.GlyphCount: > 0 });
    /// <summary>Import an image sheet (Ctrl+Shift+I).</summary>
    public RelayCommand ImportSheetCommand => _import ??= new RelayCommand(() => { if (Active is { } d) ImportSheetInteractive(d); }, () => Active is not null);

    internal void RefreshCommands()
    {
        foreach (var c in new[] { _replace, _paste, _range, _height, _export, _import }) c?.RaiseCanExecuteChanged();
    }

    public override IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts ??=
    [
        new("Font", "Replace the selected glyph with an image", Key.R, ModifierKeys.Control, ReplaceGlyphCommand, IsFont),
        new("Font", "Replace the selected glyph with the image on the clipboard", Key.V, ModifierKeys.Control | ModifierKeys.Shift, PasteGlyphCommand, IsFont),
        new("Font", "Export the font as an image sheet", Key.E, ModifierKeys.Control | ModifierKeys.Shift, ExportSheetCommand, IsFont),
        new("Font", "Import an edited image sheet", Key.I, ModifierKeys.Control | ModifierKeys.Shift, ImportSheetCommand, IsFont),
    ];

    private static MenuItem Item(string header, ICommand command, string? gesture = null, string? tip = null)
    {
        var item = new MenuItem { Header = header, Command = command, InputGestureText = gesture ?? "" };
        if (tip is not null) item.ToolTip = tip;
        return item;
    }

    public override IReadOnlyList<MenuContribution> Menus => _menus ??= BuildMenus();

    private List<MenuContribution> BuildMenus()
    {
        var format = new MenuItem { Header = "Pixel _Format" };
        foreach (var f in new[] { VfPixelFormat.Mono, VfPixelFormat.Indexed, VfPixelFormat.Rgba4444 })
        {
            var item = new MenuItem { Header = VfFont.FormatName(f), IsCheckable = true, Tag = f };
            item.Click += (_, _) => { if (Active is { } d) d.ConvertFormat(f); };
            format.Items.Add(item);
        }
        format.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem item in format.Items) item.IsChecked = Active?.Current.Format == (VfPixelFormat)item.Tag;
        };
        format.ToolTip = "Convert every glyph to another pixel format (colours become coverage when converting to monochrome)";

        var font = new MenuItem { Header = "F_ont" };
        foreach (var child in new Control[]
        {
            Item("_Replace Glyph from Image…", ReplaceGlyphCommand, "Ctrl+R", "Replace the selected glyph with a picture, converted to this font's pixel format"),
            Item("_Paste Glyph Image", PasteGlyphCommand, "Ctrl+Shift+V", "Replace the selected glyph with the picture on the clipboard"),
            new Separator(),
            Item("_Add or Remove Characters…", RangeCommand, null, "Change the font's first and last character"),
            Item("Change _Height…", HeightCommand, null, "Add or cut rows of every glyph"),
            format,
            new Separator(),
            Item("_Export Image Sheet…", ExportSheetCommand, "Ctrl+Shift+E", "Write the glyphs as a PNG grid plus a JSON file of the metrics, to edit in an image editor"),
            Item("_Import Image Sheet…", ImportSheetCommand, "Ctrl+Shift+I", "Read an edited sheet back into this font (one undo step)"),
        })
            font.Items.Add(child);

        return
        [
            new(MenuSlot.TopLevel, 31, font, IsFont),
            new(MenuSlot.FileExport, 32, Item("Font as _Image Sheet…", ExportSheetCommand, "Ctrl+Shift+E", "Write the font's glyphs as a PNG grid plus a JSON file"), IsFont),
            new(MenuSlot.FileImport, 32, Item("Image Sheet into _Font…", ImportSheetCommand, "Ctrl+Shift+I", "Read an edited sheet back into the active font"), IsFont),
        ];
    }

    public override IReadOnlyList<ToolbarContribution> ToolbarItems => _toolbar ??=
    [
        new(32, ToolText("Replace Glyph…", ReplaceGlyphCommand, "Replace the selected glyph with a picture (Ctrl+R)"), IsFont),
        new(33, ToolText("Export Sheet…", ExportSheetCommand, "Write the glyphs as a PNG grid plus a JSON file (Ctrl+Shift+E)"), IsFont),
        new(34, ToolText("Import Sheet…", ImportSheetCommand, "Read an edited sheet back into this font (Ctrl+Shift+I)"), IsFont),
    ];

    private static Button ToolText(string text, ICommand command, string tip)
    {
        var b = new Button { Content = text, Command = command, ToolTip = tip, Padding = new Thickness(8, 2, 8, 2) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "ToolButton");
        System.Windows.Automation.AutomationProperties.SetName(b, text.TrimEnd('…'));
        return b;
    }

    // ── Replace a glyph ──────────────────────────────────────────────────────────────────────────────────────

    private string? Folder(VfDocument document, string key)
    {
        if (Settings.Get<string>(key) is { Length: > 0 } last && Directory.Exists(last)) return last;
        if (document.FilePath is { } path && !Shell.Modules.OfType<IWorkCopyProvider>().Any(p => p.IsWorkCopy(path))) return document.Folder;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void Remember(string key, string? folder)
    {
        if (!Shell.IsDiagnosticRun && folder is not null) Settings.Set(key, folder);
    }

    private void ReplaceFromFile(VfDocument document)
    {
        string all = string.Join(";", ImageProbe.ReadableExtensions.Select(e => "*" + e));
        var paths = Shell.Dialogs.OpenFiles(Folder(document, "imageFolder"), "Replace glyph with an image", $"Images ({all})|{all}|All files (*.*)|*.*", false);
        if (paths.Length == 0) return;
        Remember("imageFolder", Path.GetDirectoryName(paths[0]));
        BgraImage image;
        try { image = ImageDecoder.DecodeFile(paths[0]); }
        catch (ImageDecodeException ex) { Shell.Dialogs.ShowError("The image could not be read", ex.Message); return; }
        ReplaceWithImage(document, image, Path.GetFileName(paths[0]));
    }

    private void ReplaceFromClipboard(VfDocument document)
    {
        if (ClipboardImage() is not { } image)
        {
            Shell.Dialogs.ShowError("No picture on the clipboard", "Copy a picture in an image editor (or a screenshot tool) first, then paste it as the glyph.");
            return;
        }
        ReplaceWithImage(document, image, "the clipboard picture");
    }

    /// <summary>The replace window over <paramref name="image"/>; the glyph is replaced when the user agrees.</summary>
    internal bool ReplaceWithImage(VfDocument document, BgraImage image, string sourceName)
    {
        int index = document.SelectedGlyph;
        if (index < 0 || index >= document.Current.GlyphCount) return false;
        var window = new VfReplaceGlyphWindow(document.Current, index, image, sourceName, document.Backdrop) { Owner = Shell.MainWindow };
        bool ok;
        using (ModalScope.Enter()) ok = window.ShowDialog() == true;
        return ok && window.Result is { } r && document.ReplaceGlyph(index, r.Width, r.Pixels, r.Spacing);
    }

    /// <summary>The picture on the clipboard (PNG with transparency when offered, else the bitmap), or null.</summary>
    internal static BgraImage? ClipboardImage()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data?.GetDataPresent("PNG") == true && data.GetData("PNG") is MemoryStream png) return ImageDecoder.Decode(png.ToArray(), "clipboard.png");
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource source) return FromSource(source);
        }
        catch (Exception ex) when (ex is ExternalException or ImageDecodeException or InvalidOperationException or ArgumentException) { }
        return null;
    }

    private static BgraImage FromSource(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var image = new BgraImage(source.PixelWidth, source.PixelHeight);
        source.CopyPixels(image.Pixels, image.Stride, 0);
        return image;
    }

    // ── Range and height ─────────────────────────────────────────────────────────────────────────────────────

    private void ChangeRange(VfDocument document)
    {
        var f = document.Current;
        var w = new VfFormWindow("Add or Remove Characters",
            $"The font has characters {f.FirstCharacter} to {f.LastCharacter}. Characters in both ranges keep their glyphs; new ones get blank glyphs, removed ones lose their kerning pairs.",
            "_Apply", "Change the range (one undo step)") { Owner = Shell.MainWindow };
        var first = w.AddNumber("First character", f.FirstCharacter, 0, 255, "Character code of the first glyph (32 = space in every stock font)");
        var last = w.AddNumber("Last character", f.LastCharacter, 0, 255, "Character code of the last glyph (255 at most: text has no higher codes)");
        var width = w.AddNumber("New glyph width", Math.Max(1, f.DefaultSpacing - 1), 0, 255, "Width of the blank glyphs added", " px");
        void Note()
        {
            int a = (int)first.Value, b = (int)last.Value;
            w.Note = b < a ? "The last character must not come before the first."
                : string.Format(CultureInfo.CurrentCulture, "{0} characters; {1} added, {2} removed.", b - a + 1,
                    Enumerable.Range(a, b - a + 1).Count(c => f.IndexOf(c) < 0 || c - f.FirstCharacter >= f.GlyphCount),
                    Enumerable.Range(f.FirstCharacter, f.GlyphCount).Count(c => c < a || c > b));
        }
        first.ValueChanged += (_, _) => Note();
        last.ValueChanged += (_, _) => Note();
        Note();
        bool ok;
        using (ModalScope.Enter()) ok = w.ShowDialog() == true;
        if (!ok) return;
        int from = (int)first.Value, to = (int)last.Value;
        if (to < from) { Shell.Dialogs.ShowError("Characters not changed", "The last character must not come before the first."); return; }
        document.SetRange(from, to - from + 1, (int)width.Value);
    }

    private void ChangeHeight(VfDocument document)
    {
        var f = document.Current;
        var w = new VfFormWindow("Change Height", $"Every glyph is {f.Height} pixels high. A new height adds clear rows or cuts rows off every glyph.",
            "_Apply", "Change the height (one undo step)") { Owner = Shell.MainWindow };
        var height = w.AddNumber("Height", f.Height, 1, 255, "The new height of every glyph and line", " px");
        var where = w.AddChoice("Rows", ["At the bottom (glyphs keep their top)", "At the top (glyphs keep their bottom)"], 0, "Where rows are added or cut");
        bool ok;
        using (ModalScope.Enter()) ok = w.ShowDialog() == true;
        if (ok) document.SetHeight((int)height.Value, where.SelectedIndex != 1);
    }

    // ── Image sheets ─────────────────────────────────────────────────────────────────────────────────────────

    private void ExportSheetInteractive(VfDocument document)
    {
        document.CommitPendingEdits();
        var f = document.Current;
        var w = new VfFormWindow("Export Image Sheet",
            "The glyphs are written as a grid of cells in a PNG, each glyph at the top left of its cell, plus a JSON file of the same name with every metric. Edit either and import the sheet back.",
            "_Export…", "Choose where to write the sheet") { Owner = Shell.MainWindow };
        var extra = w.AddNumber("Extra cell width", Settings.Get("sheetExtra", 4), 0, 64, "Clear columns added to every cell beyond the widest glyph: room to widen glyphs", " px");
        var guide = w.AddCheck("Guide colour", "Magenta outside the glyphs and between cells", Settings.Get("sheetGuide", true), "Shows each glyph's width in the image editor; off leaves it clear");
        var mono = w.AddChoice("Monochrome pixels", ["Grey on black (any editor)", "White on transparent"], Settings.Get("sheetMono", 0), "How coverage is drawn (monochrome fonts only)");
        mono.IsEnabled = f.Format == VfPixelFormat.Mono || VfPixelConvert.IsWhitePalette(f);
        bool ok;
        using (ModalScope.Enter()) ok = w.ShowDialog() == true;
        if (!ok) return;
        var options = new VfSheetOptions(16, (int)extra.Value, guide.IsChecked == true ? VfSheet.DefaultGuide : null, mono.SelectedIndex == 1 ? VfMonoStyle.Alpha : VfMonoStyle.Gray);
        if (!Shell.IsDiagnosticRun) { Settings.Set("sheetExtra", (int)extra.Value); Settings.Set("sheetGuide", guide.IsChecked == true); Settings.Set("sheetMono", mono.SelectedIndex); }
        string name = Path.GetFileNameWithoutExtension(document.DisplayName) + ".png";
        if (Shell.Dialogs.SaveFile(Folder(document, "sheetFolder"), "Export image sheet", "PNG images (*.png)|*.png", name, ".png") is not { } path) return;
        Remember("sheetFolder", Path.GetDirectoryName(path));
        // The save dialog has asked about the PNG already; the JSON next to it is asked about separately.
        if (ExportSheet(document, path, options, imageConfirmed: true))
            Shell.ShowStatus($"Exported {f.GlyphCount} glyphs to {Path.GetFileName(path)} and {Path.GetFileName(SidecarOf(path))}");
    }

    /// <summary>The sidecar next to a sheet image ("x.png" → "x.json").</summary>
    public static string SidecarOf(string imagePath) => Path.ChangeExtension(imagePath, ".json");

    /// <summary>
    /// Writes the sheet PNG and its sidecar. Files that exist already are replaced only when the user agrees (the
    /// sidecar may hold hand-edited metrics; a diagnostic run declines and logs it). False when nothing was written
    /// (declined, or failed and reported).
    /// </summary>
    /// <param name="document">The font.</param>
    /// <param name="pngPath">Where the PNG goes; the JSON sidecar goes next to it.</param>
    /// <param name="options">The sheet's layout.</param>
    /// <param name="imageConfirmed">True when the PNG's replacement was confirmed already (the save dialog asks).</param>
    internal bool ExportSheet(VfDocument document, string pngPath, VfSheetOptions options, bool imageConfirmed = false)
    {
        var existing = new[] { imageConfirmed ? null : pngPath, SidecarOf(pngPath) }.OfType<string>().Where(File.Exists).Select(Path.GetFileName).ToList();
        if (existing.Count > 0 && !Shell.Dialogs.Confirm(
                existing.Count == 1 ? $"Replace {existing[0]}?" : $"Replace {existing[0]} and {existing[1]}?",
                $"{string.Join(" and ", existing)} {(existing.Count == 1 ? "exists" : "exist")} already in {Path.GetDirectoryName(Path.GetFullPath(pngPath))}. "
                    + "Exporting replaces the sheet's image and its JSON file; metrics edited in the JSON file by hand are lost.",
                "_Replace"))
        {
            Shell.ShowStatus("Sheet not exported: " + string.Join(" and ", existing) + " left as it was");
            return false;
        }
        try
        {
            var (image, json) = VfSheet.Export(document.Current, Path.GetFileName(pngPath), options);
            Cairn.Workspace.AtomicFile.WriteAllBytes(pngPath, PngEncoder.Encode(image));
            Cairn.Workspace.AtomicFile.WriteAllBytes(SidecarOf(pngPath), System.Text.Encoding.UTF8.GetBytes(json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfSheetException or ImageDecodeException)
        {
            Shell.Dialogs.ShowError("The sheet could not be exported", ex.Message);
            return false;
        }
    }

    private void ImportSheetInteractive(VfDocument document)
    {
        var paths = Shell.Dialogs.OpenFiles(Folder(document, "sheetFolder"), "Import image sheet", "Image sheets (*.png;*.json)|*.png;*.json|All files (*.*)|*.*", false);
        if (paths.Length == 0) return;
        Remember("sheetFolder", Path.GetDirectoryName(paths[0]));
        ImportSheet(document, paths[0]);
    }

    /// <summary>
    /// Reads a sheet (the PNG or its JSON sidecar may be given) into the font as one undo step. False when nothing
    /// changed or it failed (reported).
    /// </summary>
    internal bool ImportSheet(VfDocument document, string path)
    {
        try
        {
            string json, png;
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                json = File.ReadAllText(path);
                png = Path.Combine(Path.GetDirectoryName(path) ?? "", VfSheet.ImageNameOf(json) is { Length: > 0 } n ? Path.GetFileName(n) : Path.GetFileNameWithoutExtension(path) + ".png");
            }
            else
            {
                png = path;
                string sidecar = SidecarOf(path);
                if (!File.Exists(sidecar)) throw new VfSheetException($"{Path.GetFileName(sidecar)} is missing: the sheet's metrics are read from the JSON file exported with it.");
                json = File.ReadAllText(sidecar);
            }
            if (!File.Exists(png)) throw new VfSheetException($"{Path.GetFileName(png)}, the sheet's image, is missing.");
            var font = VfSheet.Import(document.Current, ImageDecoder.DecodeFile(png), json);
            if (ReferenceEquals(font, document.Current)) { Shell.ShowStatus("The sheet matches the font: nothing changed"); return false; }
            int changed = Enumerable.Range(0, font.GlyphCount).Count(i => i >= document.Current.GlyphCount
                || !font.Glyphs[i].Pixels.AsSpan().SequenceEqual(document.Current.Glyphs[i].Pixels.AsSpan()) || font.Glyphs[i].Width != document.Current.Glyphs[i].Width || font.Glyphs[i].Spacing != document.Current.Glyphs[i].Spacing);
            bool done = document.ImportFont(font, "Import image sheet");
            if (done) Shell.ShowStatus($"Imported {Path.GetFileName(png)}: {changed} glyph{(changed == 1 ? "" : "s")} changed");
            return done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfSheetException or ImageDecodeException
            or System.Text.Json.JsonException or FormatException or ArgumentException or NotSupportedException || VfDocument.IsEditFailure(ex))
        {
            // The sidecar is edited by hand: whatever is wrong with it (or the image) is reported, never a crash.
            Shell.Dialogs.ShowError("The sheet could not be imported", ex.Message);
            return false;
        }
    }
}
