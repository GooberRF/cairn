using System.Globalization;
using System.Windows;
using Cairn.Formats.Imaging;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;

namespace Cairn.Vbm.Ui.Documents;

/// <summary>
/// An open .vbm. The snapshot is the whole <see cref="VbmFile"/> (raw pixels per frame and level), so an unchanged file
/// saves byte for byte and every edit is one undo step. Also keeps the view state commands need: the frame shown, the
/// selected frames and whether it plays. A file that cannot be read at all still opens, read-only, with the reason in
/// the Problems tab.
/// </summary>
public sealed class VbmDocument : SnapshotDocument<VbmFile>
{
    private VbmProblem? _unreadable; // cleared when a reload reads the file after all
    private VbmReadResult? _read;
    private int _currentFrame;
    private IReadOnlyList<int> _selected = [0];
    private bool _isPlaying;
    private IReadOnlyList<VbmProblem> _problems = [];

    private VbmDocument(VbmModule module, IDocumentKind kind, VbmFile file, VbmReadResult? read, VbmProblem? unreadable,
        string name, string? path, string? origin)
        : base(module.ShellContext, kind, file, name, path, origin)
    {
        Module = module;
        _read = read;
        _unreadable = unreadable;
        _isPlaying = _resume = unreadable is null && file.IsAnimated; // an animation starts playing, as previews do
        UpdateProblems();
    }

    private bool _resume;

    /// <summary>The module.</summary>
    public VbmModule Module { get; }

    /// <summary>The packfile an entry opened directly (not through a work copy) came from, or null.</summary>
    public string? ArchivePath { get; internal set; }

    /// <summary>True when the file could not be read; the document then shows only its problem.</summary>
    public bool IsBroken => _unreadable is not null;

    /// <summary>A broken file cannot be edited or saved.</summary>
    public override bool IsReadOnly => IsBroken;

    /// <summary>The problems of the current snapshot (the read problems while it is still the file as loaded).</summary>
    public IReadOnlyList<VbmProblem> Problems { get => _problems; private set => Set(ref _problems, value); }

    /// <summary>Raised after every snapshot change (edit, undo, redo, reload).</summary>
    public event EventHandler? ContentChanged;

    /// <summary>The frame the preview shows (0-based).</summary>
    public int CurrentFrame
    {
        get => _currentFrame;
        set
        {
            if (Set(ref _currentFrame, Math.Clamp(value, 0, Current.FrameCount - 1))) Raise(nameof(FrameText));
        }
    }

    /// <summary>"Frame 3 / 8".</summary>
    public string FrameText => string.Format(CultureInfo.CurrentCulture, "Frame {0} / {1}", _currentFrame + 1, Current.FrameCount);

    /// <summary>The selected frames, in order (at least the current one while the document has frames).</summary>
    public IReadOnlyList<int> SelectedFrames
    {
        get => _selected;
        set
        {
            var clean = (value ?? []).Where(i => i >= 0 && i < Current.FrameCount).Distinct().Order().ToList();
            if (clean.Count == 0) clean.Add(Math.Clamp(_currentFrame, 0, Current.FrameCount - 1));
            if (clean.SequenceEqual(_selected)) return;
            _selected = clean;
            Raise(nameof(SelectedFrames));
            Module.RefreshCommands();
        }
    }

    /// <summary>True while the animation plays.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (Set(ref _isPlaying, value && Current.IsAnimated && !IsBroken)) Module.RefreshCommands();
        }
    }

    /// <summary>The file's size on disk, or null when it has none.</summary>
    public long? FileSize
    {
        get
        {
            if (FilePath is null || IsDirty) return null;
            try { return new FileInfo(FilePath).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
    }

    /// <summary>Error and warning counts for the status bar and the Problems tab header.</summary>
    public (int Errors, int Warnings) ProblemCounts =>
        (_problems.Count(p => p.Severity == VbmSeverity.Error), _problems.Count(p => p.Severity == VbmSeverity.Warning));

    public override IReadOnlyList<StatusItem> StatusItems
    {
        get
        {
            if (IsBroken) return [new("Not readable", _unreadable!.Message, Module.ShowProblemsCommand)];
            var c = CultureInfo.CurrentCulture;
            var f = Current;
            var items = new List<StatusItem>
            {
                new(string.Format(c, "{0} x {1}", f.Width, f.Height), "Size of each frame"),
                new(Short(f.Format), f.Format.DisplayName()),
                new(f.IsAnimated ? string.Format(c, "{0} frames at {1} fps", f.FrameCount, f.Fps) : "1 frame", "Frames and frame rate"),
            };
            var (errors, warnings) = ProblemCounts;
            if (errors + warnings > 0)
                items.Add(new(string.Join(", ", new[] { Count(errors, "error"), Count(warnings, "warning") }.Where(s => s.Length > 0)),
                    "Problems found in this bitmap: click to open the Problems tab", Module.ShowProblemsCommand));
            return items;

            static string Count(int n, string word) => n == 0 ? "" : n == 1 ? $"1 {word}" : $"{n} {word}s";
        }
    }

    /// <summary>"1555", "4444", "565".</summary>
    public static string Short(VbmPixelFormat format) => format switch
    {
        VbmPixelFormat.Argb1555 => "1555",
        VbmPixelFormat.Argb4444 => "4444",
        _ => "565",
    };

    // ── Loading ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A document over <paramref name="bytes"/>; an unreadable file gives a broken, read-only document.</summary>
    internal static VbmDocument Load(VbmModule module, IDocumentKind kind, byte[] bytes, string name, string? path, string? origin)
    {
        try
        {
            var read = VbmFile.Read(bytes, name);
            return new VbmDocument(module, kind, read.File, read, null, name, path, origin);
        }
        catch (ImageDecodeException ex)
        {
            var placeholder = new VbmFile(2, 1, 1, VbmPixelFormat.Rgb565, 0, 1, [new VbmFrame([new byte[2]])], []);
            return new VbmDocument(module, kind, placeholder, null, VbmChecks.Unreadable(ex.Message), name, path, origin);
        }
    }

    /// <summary>A new, unsaved document over <paramref name="file"/>.</summary>
    internal static VbmDocument New(VbmModule module, IDocumentKind kind, VbmFile file, string name)
    {
        var document = new VbmDocument(module, kind, file, null, null, name, null, null);
        document.MarkAsNew();
        return document;
    }

    protected override VbmFile Parse(byte[] bytes, string name)
    {
        try
        {
            var read = VbmFile.Read(bytes, name);
            _read = read;
            _unreadable = null; // a broken file fixed on disk and reloaded can be edited and saved again
            return read.File;
        }
        catch (ImageDecodeException ex) { throw new InvalidDataException(ex.Message, ex); }
    }

    protected override byte[] Write(VbmFile snapshot) => snapshot.Write();

    public override byte[]? CaptureRecovery() => IsBroken ? null : base.CaptureRecovery();

    public override bool ConfirmSave()
    {
        if (!IsBroken) return true;
        Shell.Dialogs.ShowError("This bitmap cannot be saved", _unreadable!.Message);
        return false;
    }

    protected override void OnSnapshotChanged()
    {
        _currentFrame = Math.Clamp(_currentFrame, 0, Current.FrameCount - 1);
        var selected = _selected.Where(i => i < Current.FrameCount).ToList();
        _selected = selected.Count > 0 ? selected : [_currentFrame];
        if (!Current.IsAnimated) _isPlaying = false;
        // During a spin (one undo step for the gesture) the checks wait for its end instead of running per step.
        if (!History.IsCoalescing) UpdateProblems();
        RaiseAll(nameof(CurrentFrame), nameof(FrameText), nameof(SelectedFrames), nameof(IsPlaying), nameof(StatusItems), nameof(FileSize),
            nameof(IsBroken), nameof(IsReadOnly), nameof(CanUndo), nameof(CanRedo));
        ContentChanged?.Invoke(this, EventArgs.Empty);
        Module.RefreshCommands();
    }

    protected override void OnSavedCore()
    {
        _read = null; // the file on disk is now exactly the snapshot: nothing left over from the original bytes
        UpdateProblems();
        RaiseAll(nameof(StatusItems), nameof(FileSize));
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateProblems()
    {
        if (_unreadable is not null) { Problems = [_unreadable]; return; }
        var read = _read is not null && ReferenceEquals(_read.File, Current) ? _read : null;
        Problems = VbmChecks.Check(Current, read);
    }

    protected override FrameworkElement CreateView() => new VbmDocumentView(this);

    /// <summary>Playback stops while another tab is active and resumes when this one comes back.</summary>
    public override void OnDeactivated()
    {
        _resume = _isPlaying;
        IsPlaying = false;
    }

    public override void OnActivated()
    {
        if (_resume) IsPlaying = true;
        _resume = false;
    }

    // ── Edits (each one undo step) ───────────────────────────────────────────────────────────────────────────

    /// <summary>Sets the frame rate.</summary>
    public bool SetFps(int fps) => Apply("Change frame rate", f => VbmEditing.WithFps(f, Math.Clamp(fps, 0, VbmEditing.MaxFps)));

    /// <summary>Replaces frame <paramref name="index"/> with <paramref name="image"/> (fitted with <paramref name="resize"/> and converted).</summary>
    public bool ReplaceFrame(int index, BgraImage image, VbmResizeOptions? resize = null)
    {
        bool done = Apply("Replace frame", f => VbmEditing.ReplaceFrame(f, index, image, resize));
        if (done) { CurrentFrame = index; SelectedFrames = [index]; }
        return done;
    }

    /// <summary>Inserts <paramref name="images"/> as frames before <paramref name="index"/> and selects them.</summary>
    public bool InsertFrames(int index, IReadOnlyList<BgraImage> images, VbmResizeOptions? resize = null, string? label = null)
    {
        bool done = Apply(label ?? (images.Count == 1 ? "Add frame" : "Add frames"), f => VbmEditing.InsertFrames(f, index, images, resize));
        if (done) { CurrentFrame = index; SelectedFrames = [.. Enumerable.Range(index, images.Count)]; }
        return done;
    }

    /// <summary>
    /// Inserts frames <paramref name="indices"/> of <paramref name="source"/> (another bitmap, or this one as it was) before
    /// <paramref name="index"/> and selects them: their exact data when the layouts match, else fitted and converted.
    /// </summary>
    public bool InsertFramesFrom(int index, VbmFile source, IReadOnlyList<int> indices, VbmResizeOptions? resize, string label)
    {
        int count = indices.Count(i => i >= 0 && i < source.FrameCount);
        index = Math.Clamp(index, 0, Current.FrameCount);
        bool done = Apply(label, f => VbmEditing.InsertFramesFrom(f, index, source, indices, resize));
        if (done) { CurrentFrame = index; SelectedFrames = [.. Enumerable.Range(index, count)]; }
        return done;
    }

    /// <summary>Moves the frames <paramref name="indices"/> as a block to just before frame <paramref name="before"/> (a drag in the strip).</summary>
    public bool MoveFramesTo(IReadOnlyList<int> indices, int before)
    {
        IReadOnlyList<int> moved = [];
        before = Math.Clamp(before, 0, Current.FrameCount);
        bool done = Apply(indices.Count == 1 ? "Move frame" : "Move frames", f => VbmEditing.MoveFramesTo(f, indices, before, out moved));
        if (done) { SelectedFrames = moved; CurrentFrame = moved[0]; }
        return done;
    }

    /// <summary>Removes the selected frames (never the last one left).</summary>
    public bool RemoveSelected()
    {
        if (_selected.Count >= Current.FrameCount) return false;
        int first = _selected[0];
        bool done = Apply(_selected.Count == 1 ? "Remove frame" : "Remove frames", f => VbmEditing.RemoveFrames(f, _selected));
        if (done) { CurrentFrame = Math.Min(first, Current.FrameCount - 1); SelectedFrames = [CurrentFrame]; }
        return done;
    }

    /// <summary>Duplicates the selected frames after the last of them and selects the copies.</summary>
    public bool DuplicateSelected()
    {
        IReadOnlyList<int> copies = [];
        bool done = Apply(_selected.Count == 1 ? "Duplicate frame" : "Duplicate frames", f => VbmEditing.DuplicateFrames(f, _selected, out copies));
        if (done) { CurrentFrame = copies[0]; SelectedFrames = copies; }
        return done;
    }

    /// <summary>Moves the selected frames one place earlier (-1) or later (+1).</summary>
    public bool MoveSelected(int delta)
    {
        IReadOnlyList<int> moved = _selected;
        bool done = Apply(delta < 0 ? "Move frames earlier" : "Move frames later", f => VbmEditing.MoveFrames(f, _selected, delta, out moved));
        if (done) { SelectedFrames = moved; CurrentFrame = moved[0]; }
        return done;
    }

    /// <summary>Reverses the order of every frame.</summary>
    public bool ReverseFrames() => Current.IsAnimated && Apply("Reverse frame order", VbmEditing.Reverse);

    /// <summary>Converts every frame to <paramref name="format"/>.</summary>
    public bool ConvertFormat(VbmPixelFormat format) =>
        Apply("Change pixel format to " + Short(format), f => VbmEditing.ConvertFormat(f, format));

    /// <summary>Rebuilds every frame's mip chain with <paramref name="levels"/> levels.</summary>
    public bool SetMipLevels(int levels) =>
        Apply(levels == 1 ? "Remove mipmaps" : "Set mip levels", f => VbmEditing.WithMipLevels(f, Math.Clamp(levels, 1, VbmFile.MaxMipLevels(f.Width, f.Height))));
}
