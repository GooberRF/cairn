using System.Windows;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Ui.Views;
using Cairn.Vf.Validation;

namespace Cairn.Vf.Ui.Documents;

/// <summary>
/// An open <c>.vf</c> font. The content is an immutable <see cref="VfFont"/> in the snapshot history (edits are
/// <see cref="SnapshotDocument{T}.Apply"/> calls with <see cref="VfEdits"/> operations); Save and Save As write it
/// through <see cref="VfWriter"/>, so an unchanged font is saved byte for byte. View state (selected glyph, sample
/// text, zooms, backdrop) lives here too and is never saved.
/// </summary>
public sealed class VfDocument : SnapshotDocument<VfFont>
{
    /// <summary>The default sample text.</summary>
    public const string DefaultSample = "The quick brown fox jumps over the lazy dog";

    private readonly VfModule _module;
    private IReadOnlyList<VfProblem> _loadProblems;
    private VfFont _loadedSnapshot;
    private int _selected;
    private string _sampleText = DefaultSample;
    private bool _showAll;
    private int _sampleZoom, _gridZoom;
    private VfBackdrop _backdrop = VfBackdrop.Dark;

    internal VfDocument(VfModule module, IDocumentKind kind, VfFont font, IReadOnlyList<VfProblem> loadProblems, string name, string? path, string? origin = null)
        : base(module.ShellContext, kind, font, name, path, origin)
    {
        _module = module;
        _loadProblems = loadProblems;
        _loadedSnapshot = font;
        _sampleText = PickSample(font);
        _sampleZoom = VfImages.FitZoom(font.Height, 36, 8);
        _gridZoom = VfImages.FitZoom(font.Height, 40, 6);
        ShowProblemsCommand = new RelayCommand(() => _module.ShowProblems(this));
        Problems = BuildProblems();
    }

    /// <summary>Raised after the font changed (edit, undo, reload) and the problems were re-checked.</summary>
    public event EventHandler? FontChanged;
    /// <summary>Raised when the selected glyph changes.</summary>
    public event EventHandler? SelectionChanged;
    /// <summary>Raised when the sample text, zooms or backdrop change.</summary>
    public event EventHandler? ViewOptionsChanged;

    /// <summary>The problems of the current font (reader findings at load plus <see cref="VfValidator"/>).</summary>
    public IReadOnlyList<VfProblem> Problems { get; private set; }

    /// <summary>Opens the Problems tab.</summary>
    public RelayCommand ShowProblemsCommand { get; }

    /// <summary>The selected glyph index (-1 when the font has none).</summary>
    public int SelectedGlyph
    {
        get => _selected;
        set
        {
            int clamped = Current.GlyphCount == 0 ? -1 : Math.Clamp(value, 0, Current.GlyphCount - 1);
            if (!Set(ref _selected, clamped)) return;
            Raise(nameof(StatusItems));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            _module.RefreshCommands();
        }
    }

    /// <summary>Selects the glyph of a character code; false when the font has none.</summary>
    public bool SelectCharacter(int code)
    {
        int i = Current.IndexOf(code);
        if (i < 0 || code is < 0 or > 255) return false;
        SelectedGlyph = i;
        return true;
    }

    /// <summary>The sample text shown under the glyphs.</summary>
    public string SampleText { get => _sampleText; set { if (Set(ref _sampleText, value ?? string.Empty)) ViewOptionsChanged?.Invoke(this, EventArgs.Empty); } }
    /// <summary>True to show every character of the font instead of the sample text.</summary>
    public bool ShowAllCharacters { get => _showAll; set { if (Set(ref _showAll, value)) ViewOptionsChanged?.Invoke(this, EventArgs.Empty); } }
    /// <summary>The sample's zoom, 1 to 8.</summary>
    public int SampleZoom { get => _sampleZoom; set { if (Set(ref _sampleZoom, Math.Clamp(value, 1, 8))) ViewOptionsChanged?.Invoke(this, EventArgs.Empty); } }
    /// <summary>The glyph grid's zoom, 1 to 6.</summary>
    public int GridZoom { get => _gridZoom; set { if (Set(ref _gridZoom, Math.Clamp(value, 1, 6))) ViewOptionsChanged?.Invoke(this, EventArgs.Empty); } }
    /// <summary>What glyphs are drawn on.</summary>
    public VfBackdrop Backdrop { get => _backdrop; set { if (Set(ref _backdrop, value)) ViewOptionsChanged?.Invoke(this, EventArgs.Empty); } }

    /// <summary>The default sample, or its upper-case form when the font has more capitals than small letters (HUD fonts).</summary>
    public static string PickSample(VfFont font)
    {
        string upper = DefaultSample.ToUpperInvariant();
        return MissingCount(font, DefaultSample) <= MissingCount(font, upper) ? DefaultSample : upper;
    }

    /// <summary>How many characters of <paramref name="text"/> (spaces aside) the font has no glyph for.</summary>
    public static int MissingCount(VfFont font, string text) => VfLayout.Encode(text).Count(b => b > 32 && font.IndexOf(b) < 0);

    /// <summary>The bytes the sample strip lays out (the sample text in the game's code page, or every character).</summary>
    public byte[] SampleBytes => _showAll ? VfLayout.AllCharacters(Current) : VfLayout.Encode(_sampleText);

    /// <summary>The sample laid out as the game does it.</summary>
    public VfTextLayout SampleLayout() => VfLayout.Layout(Current, SampleBytes);

    public override IReadOnlyList<StatusItem> StatusItems
    {
        get
        {
            var f = Current;
            var items = new List<StatusItem>
            {
                new($"VF v{f.Version} · {VfFont.FormatName(f.Format)}", "Font format version and pixel format"),
                new($"{f.GlyphCount} glyphs", $"Characters {f.FirstCharacter} to {f.LastCharacter}"),
                new($"{f.Height} px high", "Every glyph and line is this tall"),
            };
            if (_selected >= 0 && _selected < f.GlyphCount)
                items.Add(new($"{VfReader.Describe(f.CharacterOf(_selected))}", "The selected glyph (arrow keys move the selection)"));
            int errors = Problems.Count(p => p.Severity == VfSeverity.Error), warnings = Problems.Count(p => p.Severity == VfSeverity.Warning);
            if (errors + warnings > 0)
                items.Add(new(string.Join(", ", new[] { errors > 0 ? Plural(errors, "error") : null, warnings > 0 ? Plural(warnings, "warning") : null }.OfType<string>()),
                    "Problems found in this font: click to open the Problems tab", ShowProblemsCommand));
            return items;
        }
    }

    internal static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    protected override VfFont Parse(byte[] bytes, string name)
    {
        var problems = new List<VfProblem>();
        var font = VfReader.Read(bytes, name, problems);
        _loadProblems = problems;
        _loadedSnapshot = font;
        return font;
    }

    protected override byte[] Write(VfFont snapshot) => VfWriter.Write(snapshot);

    // ── Saving ───────────────────────────────────────────────────────────────────────────────────────────────

    private bool _errorSaveConfirmed;

    /// <summary>The number of errors in <see cref="Problems"/>.</summary>
    public int ErrorCount => Problems.Count(p => p.Severity == VfSeverity.Error);

    /// <summary>
    /// Asks once per document before a font with errors is written (a diagnostic run never shows the prompt and
    /// cancels). A font the writer cannot write at all is reported and not saved.
    /// </summary>
    public override bool ConfirmSave()
    {
        try { VfWriter.Write(Current); }
        catch (InvalidOperationException ex)
        {
            Shell.Dialogs.ShowError("This font cannot be saved", ex.Message);
            return false;
        }
        if (ErrorCount == 0 || _errorSaveConfirmed) return true;
        if (!Shell.Dialogs.ConfirmSaveWithErrors(DisplayName, ErrorCount)) return false;
        _errorSaveConfirmed = true;
        return true;
    }

    // ── Edits (each one undo step) ───────────────────────────────────────────────────────────────────────────

    private bool Edit(string label, Func<VfFont, VfFont> edit) => Run(label, () => Apply(label, edit));

    /// <summary>
    /// Applies an edit from a number box: the next value of a spin in progress (one undo step for the gesture), else
    /// a step of its own. A value the edit refuses goes to <paramref name="refused"/> (the status bar when null).
    /// </summary>
    public bool EditValue(string label, Func<VfFont, VfFont> edit, Action<string>? refused = null) =>
        Run(label, () =>
        {
            if (!History.IsCoalescing) return Apply(label, edit);
            UpdateEdit(edit);
            return true;
        }, refused);

    /// <summary>
    /// Runs an edit without letting it throw: a value it refuses (<see cref="ArgumentException"/>) is a status message;
    /// anything else a damaged font makes it fail on (arithmetic overflow, data out of range, a font that cannot be
    /// laid out) is reported in an error dialog. The font is unchanged in both cases.
    /// </summary>
    private bool Run(string label, Func<bool> action, Action<string>? refused = null)
    {
        try { return action(); }
        catch (ArgumentException ex)
        {
            (refused ?? ShowStatus)(ex.Message);
            return false;
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            Shell.Dialogs.ShowError($"{label}: the font could not be changed", ex.Message, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>The exceptions an edit of a damaged font can end in (reported, never left to crash the app).</summary>
    internal static bool IsEditFailure(Exception ex) =>
        ex is ArithmeticException or InvalidOperationException or IndexOutOfRangeException or OutOfMemoryException;

    private bool HasGlyph(int index) => index >= 0 && index < Current.GlyphCount;

    /// <summary>Sets a glyph's width: narrower crops columns on the right, wider adds clear columns.</summary>
    public bool SetWidth(int index, int width) => HasGlyph(index) && width >= 0 && width != Current.Glyphs[index].Width && Edit("Change width", f => VfEdits.WithWidth(f, index, width));

    /// <summary>Sets a glyph's spacing (pen advance).</summary>
    public bool SetSpacing(int index, int spacing) => HasGlyph(index) && spacing != Current.Glyphs[index].Spacing && Edit("Change spacing", f => VfEdits.WithSpacing(f, index, spacing));

    /// <summary>Sets a glyph's user data.</summary>
    public bool SetUserData(int index, int value) => HasGlyph(index) && value is >= 0 and <= ushort.MaxValue && value != Current.Glyphs[index].UserData && Edit("Change user data", f => VfEdits.WithUserData(f, index, (ushort)value));

    /// <summary>Sets the default spacing.</summary>
    public bool SetDefaultSpacing(int spacing) => spacing != Current.DefaultSpacing && Edit("Change default spacing", f => VfEdits.WithDefaultSpacing(f, spacing));

    /// <summary>Gives a glyph new pixels (and width), optionally a new spacing.</summary>
    public bool ReplaceGlyph(int index, int width, byte[] pixels, int? spacing = null, string label = "Replace glyph")
    {
        if (!HasGlyph(index)) return false;
        return Edit(label, f =>
        {
            var next = VfEdits.WithGlyphPixels(f, index, width, pixels);
            return spacing is { } s ? VfEdits.WithSpacing(next, index, s) : next;
        });
    }

    /// <summary>Changes the character range; new characters get blank glyphs <paramref name="newWidth"/> wide.</summary>
    public bool SetRange(int first, int count, int newWidth)
    {
        if (first is < 0 or > 255 || count < 1 || (first == Current.FirstCharacter && count == Current.GlyphCount)) return false;
        int selectedCode = Current.CharacterOf(Math.Max(0, _selected));
        bool done = Edit(count > Current.GlyphCount ? "Add characters" : "Change characters", f => VfEdits.WithRange(f, first, count, newWidth));
        if (done) SelectedGlyph = Math.Clamp(selectedCode - first, 0, count - 1);
        return done;
    }

    /// <summary>Changes the font height (rows added or cut at the bottom, or at the top).</summary>
    public bool SetHeight(int height, bool keepTop) => height >= 1 && height != Current.Height && Edit("Change height", f => VfEdits.WithHeight(f, height, keepTop));

    /// <summary>Converts the pixels to another format.</summary>
    public bool ConvertFormat(VfPixelFormat format) => format != Current.Format && Edit("Convert to " + VfFont.FormatName(format), f => VfEdits.WithFormat(f, format));

    /// <summary>Adds or changes the kerning pair (glyph indices); offset 0 removes it.</summary>
    public bool SetKernPair(int left, int right, int offset)
    {
        if (!HasGlyph(left) || !HasGlyph(right) || left > 255 || right > 255) return false;
        var existing = Current.Kerning.FirstOrDefault(k => k.Left == left && k.Right == right);
        if ((existing?.Offset ?? 0) == offset) return false;
        string label = offset == 0 ? "Remove kerning pair" : existing is null ? "Add kerning pair" : "Change kerning pair";
        return Edit(label, f => VfEdits.WithKernPair(f, left, right, offset));
    }

    /// <summary>Replaces the whole font with an imported one (an image sheet), as one step.</summary>
    public bool ImportFont(VfFont font, string label) => !ReferenceEquals(font, Current) && Edit(label, _ => font);

    /// <summary>Starts a paint stroke on a glyph (one undo step until <see cref="EndStroke"/>).</summary>
    public void BeginStroke() => BeginEdit("Paint pixels");

    /// <summary>Sets pixel (<paramref name="x"/>, <paramref name="y"/>) of a glyph to a raw value during a stroke (or as its own step outside one).</summary>
    public void PaintPixel(int index, int x, int y, int raw)
    {
        if (!HasGlyph(index)) return;
        var g = Current.Glyphs[index];
        if (x < 0 || y < 0 || x >= g.Width || y >= Current.Height || VfRender.RawPixel(Current, g, x, y) == raw) return;
        Func<VfFont, VfFont> edit = f =>
        {
            var pixels = f.Glyphs[index].Pixels.ToArray();
            int bpp = f.BytesPerPixel, p = (y * f.Glyphs[index].Width + x) * bpp;
            pixels[p] = (byte)raw;
            if (bpp == 2) pixels[p + 1] = (byte)(raw >> 8);
            return VfEdits.WithGlyphPixels(f, index, f.Glyphs[index].Width, pixels);
        };
        if (History.IsCoalescing) Run("Paint pixels", () => { UpdateEdit(edit); return true; });
        else Edit("Paint pixel", edit);
    }

    /// <summary>Ends a paint stroke.</summary>
    public void EndStroke() => CommitEdit();

    private IReadOnlyList<VfProblem> BuildProblems()
    {
        // Reader findings describe the bytes as loaded: an offset problem is repaired once the font is laid out
        // afresh, trailing bytes stay with the font.
        var load = ReferenceEquals(Current, _loadedSnapshot) ? _loadProblems : _loadProblems.Where(p => p.Code == "VF020").ToList();
        return [.. load, .. VfValidator.Validate(Current)];
    }

    protected override void OnSnapshotChanged()
    {
        // During a paint stroke or a spin (one undo step for the gesture) the checks wait for its end instead of
        // running for every pixel or step.
        if (!History.IsCoalescing) Problems = BuildProblems();
        if (_selected >= Current.GlyphCount) _selected = Current.GlyphCount - 1;
        RaiseAll(nameof(StatusItems), nameof(Problems), nameof(SelectedGlyph), nameof(ErrorCount));
        FontChanged?.Invoke(this, EventArgs.Empty);
        _module.RefreshCommands();
    }

    public override void Dispose()
    {
        _module.Forget(this);
        base.Dispose();
    }

    protected override FrameworkElement CreateView() => new VfDocumentView(this);
}
