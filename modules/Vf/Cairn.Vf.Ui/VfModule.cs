using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Ui.Documents;
using Cairn.Vf.Ui.Views;
using Cairn.Workspace;

namespace Cairn.Vf.Ui;

/// <summary>
/// The fonts (.vf) module: opens VFNT bitmap fonts in tabs (glyph grid, sample text drawn as the game draws it,
/// inspector, problems) and previews them for other modules (packfile entries).
/// </summary>
public sealed partial class VfModule : ModuleBase, IAssetPreviewProvider
{
    /// <summary>The bottom Problems tab's id.</summary>
    public const string ProblemsPanelId = "vf.problems";

    private readonly List<VfDocument> _documents = [];
    private VfKind? _kind;
    private IReadOnlyDictionary<string, string>? _pendingOptions;

    public override string Id => "vf";
    public override string DisplayName => "Fonts";

    /// <summary>The font kind.</summary>
    public VfKind Kind => _kind ??= new VfKind(this);

    /// <summary>The shell (for documents).</summary>
    internal IShellContext ShellContext => Shell;

    /// <summary>The open font documents.</summary>
    public IReadOnlyList<VfDocument> OpenDocuments => _documents;

    public override IReadOnlyList<IDocumentKind> DocumentKinds => [Kind];

    public override IReadOnlyList<PanelContribution> Panels { get; } =
        [new PanelContribution(ProblemsPanelId, "Problems", PanelSide.Bottom, 10, d => d is VfDocument f ? VfProblemsPanel.For(f) : null)];

    public override IReadOnlyList<HelpTopic> HelpTopics { get; } = [new HelpTopic("vf.fonts", "Fonts (.vf)", BuildHelp)];

    internal VfDocument Track(VfDocument document)
    {
        _documents.Add(document);
        return document;
    }

    internal void Forget(VfDocument document) => _documents.Remove(document);

    /// <summary>Brings the document's Problems tab forward.</summary>
    internal void ShowProblems(VfDocument document)
    {
        var panel = VfProblemsPanel.For(document);
        if (Shell.ShowPanel(ProblemsPanelId)) Shell.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, panel.FocusList);
    }

    // ── Preview provider (packfile preview pane) ──────────────────────────────────────────────────────────────

    public bool CanPreview(string fileName) => fileName.EndsWith(".vf", StringComparison.OrdinalIgnoreCase);

    public FrameworkElement? CreatePreview(byte[] bytes, string fileName) => CanPreview(fileName) ? new VfPreview(bytes, fileName) : null;

    // ── Diagnostic options ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Diagnostic runs (applied to the first font that becomes active): <c>--vf-glyph A</c> (a character, or <c>#n</c> for
    /// a glyph number), <c>--vf-sample "text"</c>, <c>--vf-all-chars</c>, <c>--vf-zoom n</c> (sample), <c>--vf-grid-zoom n</c>,
    /// <c>--vf-backdrop dark|checker|light</c>.
    /// </summary>
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options)
    {
        if (!options.Keys.Any(k => k.StartsWith("vf-", StringComparison.OrdinalIgnoreCase))) return;
        if (Shell.ActiveDocument is VfDocument doc) { ApplyOptions(doc, options); ApplyEditOptions(doc, options); return; }
        _pendingOptions = options;
        Shell.ActiveDocumentChanged += OnFirstActive;
    }

    private void OnFirstActive(object? sender, EventArgs e)
    {
        if (Shell.ActiveDocument is not VfDocument doc || _pendingOptions is not { } options) return;
        Shell.ActiveDocumentChanged -= OnFirstActive;
        _pendingOptions = null;
        ApplyOptions(doc, options);
        ApplyEditOptions(doc, options);
    }

    /// <summary>
    /// Diagnostic edits (screenshots of the editor): <c>--vf-demo-edit</c> paints a stroke on the selected glyph, widens it
    /// by one pixel and adds a kerning pair with the next character; <c>--vf-export-sheet path.png</c> writes the font's
    /// image sheet (default options) there.
    /// </summary>
    private void ApplyEditOptions(VfDocument doc, IReadOnlyDictionary<string, string> options)
    {
        if (options.ContainsKey("vf-demo-edit") && doc.SelectedGlyph >= 0)
        {
            int i = doc.SelectedGlyph;
            doc.SetWidth(i, doc.Current.Glyphs[i].Width + 1);
            int w = doc.Current.Glyphs[i].Width, h = doc.Current.Height, value = Cairn.Vf.Model.VfPixelConvert.MaxValue(doc.Current.Format);
            if (doc.Current.Format == VfPixelFormat.Indexed) value = doc.Current.Glyphs.SelectMany(g => g.Pixels).Max(); // the most solid entry the font uses (stock white ramp)
            doc.BeginStroke();
            for (int y = 0; y < h; y++) doc.PaintPixel(i, w - 1, y, y % 2 == 0 ? value : 0);
            doc.EndStroke();
            if (i + 1 < doc.Current.GlyphCount && i < 127) doc.SetKernPair(i, i + 1, -1);
        }
        if (options.TryGetValue("vf-export-sheet", out var sheet) && sheet.Length > 0)
            ExportSheet(doc, Path.GetFullPath(sheet), new Cairn.Vf.Sheets.VfSheetOptions());
    }

    internal static void ApplyOptions(VfDocument doc, IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("vf-sample", out var sample)) doc.SampleText = sample;
        if (options.TryGetValue("vf-all-chars", out var all)) doc.ShowAllCharacters = all != "false";
        if (options.TryGetValue("vf-zoom", out var zoom) && int.TryParse(zoom, out int z)) doc.SampleZoom = z;
        if (options.TryGetValue("vf-grid-zoom", out var gridZoom) && int.TryParse(gridZoom, out int gz)) doc.GridZoom = gz;
        if (options.TryGetValue("vf-backdrop", out var backdrop) && Enum.TryParse<VfBackdrop>(backdrop, true, out var b)) doc.Backdrop = b;
        if (options.TryGetValue("vf-glyph", out var glyph) && glyph.Length > 0)
        {
            if (glyph.StartsWith('#') && int.TryParse(glyph[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) doc.SelectedGlyph = index;
            else if (VfFont.TextEncoding.GetBytes(glyph[..1]) is [var code]) doc.SelectCharacter(code);
        }
    }

    // ── Help ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static FlowDocument BuildHelp()
    {
        var doc = new FlowDocument { PagePadding = new Thickness(16), FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13 };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "App.Text");
        doc.SetResourceReference(FlowDocument.BackgroundProperty, "App.PaneBackground");
        void Heading(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        void Para(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 6) });
        doc.Blocks.Add(new Paragraph(new Run("Red Faction fonts (.vf)")) { FontSize = 20, FontWeight = FontWeights.SemiBold });
        Para("A .vf file is a bitmap font: one picture per character, all of the same height. The game's own fonts are in ui.vpp " +
             "(bigfont, smallfont, rfpc-large, rfpc-medium and rfpc-small). Opening one shows every glyph, a sample line drawn the way " +
             "the game draws text, and the selected glyph's details on the right.");
        Heading("Pixel formats");
        Para("Monochrome fonts store a coverage value from 0 to 14 per pixel and are drawn white; indexed fonts store a palette of " +
             "256 colours; RGBA 4444 fonts store a colour per pixel. The game turns every font into a 4-bit-per-channel texture, " +
             "and Cairn shows the colours that texture has. Stock fonts are white, so the Dark backdrop shows them best.");
        Heading("Spacing and kerning");
        Para("Each character is drawn at the pen position with its own width, then the pen moves by the character's spacing. " +
             "A kerning pair adds an offset when one particular character follows another. The game finds a pair only when the " +
             "pairs are sorted by first, then second character; the Problems tab names pairs that never apply. Characters the " +
             "font does not have move the pen by the default spacing and draw nothing.");
        Heading("Editing");
        Para("Width, spacing, user data, height and default spacing are number boxes in the inspector; the enlarged glyph is a " +
             "pixel editor (left button paints, right button clears); the Kerning list adds, changes and removes pairs. The Font " +
             "menu replaces a glyph with a picture (Ctrl+R) or the clipboard (Ctrl+Shift+V), adds or removes characters, changes " +
             "the height or the pixel format, and exports or imports an image sheet: a PNG of every glyph plus a JSON file of the " +
             "metrics (Ctrl+Shift+E, Ctrl+Shift+I). Every edit is one undo step.");
        Heading("Limits");
        Para("Text uses character codes 0 to 255 (Windows-1252), so glyphs past code 255 are never drawn. When the game loads a font " +
             "it packs all glyphs into one texture of at most 256 × 256 pixels; a font that does not fit stops the game with " +
             "\"Font too big!\". The inspector's Texture row shows how much room a font uses.");
        return doc;
    }
}

/// <summary>The <c>.vf</c> document kind.</summary>
public sealed class VfKind(VfModule module) : IDocumentKind
{
    public string Id => "vf";
    public string DisplayName => "Font";
    public IReadOnlyList<string> Extensions { get; } = [".vf"];
    public string FileFilter => "Fonts (*.vf)|*.vf";
    public bool CanCreateNew => false;
    public string AssociationDescription => "Red Faction font";

    public IDocument? CreateNew() => null;

    public IDocument Open(string path) => Create(AtomicFile.ReadAllBytes(path), Path.GetFileName(path), path, null);

    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) => Create(bytes, displayName, null, originText);

    public IDocument Restore(RecoverySnapshot snapshot)
    {
        var doc = Create(snapshot.Data, snapshot.DisplayName, snapshot.OriginalPath is { } p && File.Exists(p) ? p : null,
            "Recovered " + snapshot.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        doc.MarkAsNew();
        return doc;
    }

    private VfDocument Create(byte[] bytes, string name, string? path, string? origin)
    {
        var problems = new List<VfProblem>();
        var font = VfReader.Read(bytes, name, problems);
        return module.Track(new VfDocument(module, this, font, problems, name, path, origin));
    }
}
