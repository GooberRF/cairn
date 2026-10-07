using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Ui.Services;
using Cairn.Formats.Imaging;
using Cairn.Atx.Playback;
using Cairn.Atx.Schema;
using Cairn.Workspace;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>Whether the preview obeys the file's animation mode or forces a loop.</summary>
public enum PreviewModeOverride
{
    /// <summary>Play exactly as the game would, Static included.</summary>
    FollowFile,

    /// <summary>Loop the frames whatever the file says, to check the artwork.</summary>
    ForceLoop,
}

/// <summary>
/// The preview player for one document: what to show, and when to show the next thing.
///
/// Timing is <see cref="AtxPlayback"/>, the port of the engine's own controller, so ping-pong turns
/// around and Play Once holds exactly where the game would. The view drives it from
/// <c>CompositionTarget.Rendering</c> with a real elapsed delta multiplied by the speed, and every
/// frame is composited ahead of time into <see cref="CurrentImage"/>-shaped bitmaps, so a tick that
/// changes the frame costs one property change and nothing else — no decode, no allocation, no work
/// on the dispatcher.
/// </summary>
public sealed class PreviewViewModel : ObservableObject, IDisposable
{
    /// <summary>Speeds the transport offers, in the order the combo lists them.</summary>
    public static IReadOnlyList<double> SpeedOptions { get; } = [0.25, 0.5, 1.0, 2.0, 4.0];

    /// <summary>Zoom steps in percent; 0 means "fit to the pane".</summary>
    public static IReadOnlyList<double> ZoomSteps { get; } = [50, 75, 100, 150, 200, 400, 800];

    /// <summary>How much composited pixel data one document keeps resident before loading lazily.</summary>
    private const long PrefetchBudgetBytes = 128L * 1024 * 1024;

    private readonly DocumentViewModel _document;

    private CompositeFrame?[] _composites = [];
    private string[] _frameNames = [];
    private string _signature = string.Empty;
    private CancellationTokenSource _loads = new();

    private AtxPlayback _playback = new(new PlaybackSpec(AtxAnimationMode.Static, false, 100, []));
    private EngineFormat _effectiveFormat = EngineFormat.Argb8888;
    private string? _maskName;

    private BitmapSource? _currentImage;
    private string? _placeholder = "Open a file to preview its frames.";
    private int _currentIndex = -1;
    private int _selectedIndex = -1;
    private bool _isPlaying;
    private bool _isActive;
    private double _speed = 1.0;
    private double _zoom;
    private PreviewModeOverride _modeOverride = PreviewModeOverride.FollowFile;
    private bool _simulateFormat;
    private bool _tile;
    private bool _alphaOnly;
    private bool _disposed;

    internal PreviewViewModel(DocumentViewModel document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));

        FirstCommand = new RelayCommand(() => Seek(0, pause: true), () => FrameCount > 0);
        PreviousCommand = new RelayCommand(() => Step(-1), () => FrameCount > 0);
        PlayPauseCommand = new RelayCommand(TogglePlay, () => FrameCount > 0);
        NextCommand = new RelayCommand(() => Step(+1), () => FrameCount > 0);
        LastCommand = new RelayCommand(() => Seek(FrameCount - 1, pause: true), () => FrameCount > 0);
        ForceLoopCommand = new RelayCommand(() =>
        {
            ModeOverride = PreviewModeOverride.ForceLoop;
            Play();
        });
    }

    // ── What the view shows ───────────────────────────────────────────────────

    /// <summary>The composited frame, or null when there is nothing to show.</summary>
    public BitmapSource? CurrentImage
    {
        get => _currentImage;
        private set
        {
            if (!Set(ref _currentImage, value)) return;
            RaiseAll(nameof(HasImage), nameof(ImageWidth), nameof(ImageHeight));
        }
    }

    /// <summary>True when a frame is on screen.</summary>
    public bool HasImage => _currentImage is not null;

    /// <summary>The composited frame's width in pixels.</summary>
    public int ImageWidth => _currentImage?.PixelWidth ?? 0;

    /// <summary>The composited frame's height in pixels.</summary>
    public int ImageHeight => _currentImage?.PixelHeight ?? 0;

    /// <summary>Why there is no picture: no document, no frames, or a file that could not be read.</summary>
    public string? PlaceholderText
    {
        get => _placeholder;
        private set { if (Set(ref _placeholder, value)) Raise(nameof(HasPlaceholder)); }
    }

    /// <summary>True when the placeholder is showing instead of a frame.</summary>
    public bool HasPlaceholder => !HasImage && !string.IsNullOrEmpty(_placeholder);

    /// <summary>"Frame 3 / 12 · 250 ms · 64×64 ARGB 8888".</summary>
    public string ReadoutText { get; private set; } = string.Empty;

    /// <summary>The frame the player is showing, 0-based.</summary>
    public int CurrentIndex
    {
        get => _currentIndex;
        private set
        {
            if (!Set(ref _currentIndex, value)) return;
            _document.Frames.PlayingIndex = value;
        }
    }

    /// <summary>The frame selected in the frames list, outlined on the scrubber.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        private set => Set(ref _selectedIndex, value);
    }

    /// <summary>How many frames the document has.</summary>
    public int FrameCount => _frameNames.Length;

    /// <summary>One weight per frame — its effective time — for the scrubber's segment widths.</summary>
    public IReadOnlyList<double> ScrubberWeights { get; private set; } = [];

    // ── Transport ─────────────────────────────────────────────────────────────

    /// <summary>True while frames are advancing.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;
            RaiseAll(nameof(PlayGlyph), nameof(PlayTooltip), nameof(PlayAutomationName));
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when play or pause starts or stops, so the view can attach its render hook.</summary>
    public event EventHandler? PlaybackStateChanged;

    /// <summary>Segoe MDL2 glyph for the play/pause button.</summary>
    public string PlayGlyph => _isPlaying ? "" : "";

    /// <summary>Tooltip for the play/pause button.</summary>
    public string PlayTooltip => _isPlaying
        ? "Pause the preview (Space)"
        : "Play the preview (Space)";

    /// <summary>Accessible name for the play/pause button.</summary>
    public string PlayAutomationName => _isPlaying ? "Pause" : "Play";

    /// <summary>
    /// True when the tab is in front and the window is not minimised. Playback stops otherwise, so a
    /// background tab never spends a frame of the render loop.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!Set(ref _isActive, value)) return;
            if (!value && IsPlaying) Pause();
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Playback speed multiplier, 0.25× to 4×.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            double clamped = Math.Clamp(value, 0.25, 4.0);
            if (Set(ref _speed, clamped)) Raise(nameof(SpeedText));
        }
    }

    /// <summary>The speed as "1×".</summary>
    public string SpeedText => _speed.ToString("0.##", CultureInfo.CurrentCulture) + "×";

    /// <summary>Whether the preview obeys the file's mode or forces a loop.</summary>
    public PreviewModeOverride ModeOverride
    {
        get => _modeOverride;
        set
        {
            if (!Set(ref _modeOverride, value)) return;
            RaiseAll(nameof(IsFollowFile), nameof(IsForceLoop));
            RebuildPlayback(preserveFrame: true);
        }
    }

    /// <summary>True when the mode selector is on "Follow file".</summary>
    public bool IsFollowFile
    {
        get => _modeOverride == PreviewModeOverride.FollowFile;
        set { if (value) ModeOverride = PreviewModeOverride.FollowFile; }
    }

    /// <summary>True when the mode selector is on "Force loop".</summary>
    public bool IsForceLoop
    {
        get => _modeOverride == PreviewModeOverride.ForceLoop;
        set { if (value) ModeOverride = PreviewModeOverride.ForceLoop; }
    }

    /// <summary>The mode the player is actually using, after the override.</summary>
    public AtxAnimationMode EffectiveMode => _playback.Spec.Mode;

    /// <summary>
    /// True when the file says Static and the preview is following it, so pressing Play would do
    /// nothing. The view shows the explanation and the one-click way out.
    /// </summary>
    public bool ShowStaticNotice => _modeOverride == PreviewModeOverride.FollowFile
        && FrameCount > 1
        && _document.Model?.Header.EffectiveAnimationMode == AtxAnimationMode.Static;

    /// <summary>The one line the static notice shows; it sits in a slim bar over the picture.</summary>
    public string StaticNoticeText => "Static mode — frames do not advance on their own.";

    /// <summary>The whole explanation, as the notice's tooltip and accessible name.</summary>
    public string StaticNoticeDetail =>
        "This texture's animation mode is Static, so the game only changes its frame when a level "
        + "event (ATX_Set_Frame) asks it to. Force loop plays the frames here anyway; it does not "
        + "change the file.";

    // ── Toggles ───────────────────────────────────────────────────────────────

    /// <summary>Quantise each frame to the format the engine will store it in.</summary>
    public bool SimulateFormat
    {
        get => _simulateFormat;
        set { if (Set(ref _simulateFormat, value)) ReloadAll(); }
    }

    /// <summary>Draw the frame three by three, to check the seams.</summary>
    public bool Tile
    {
        get => _tile;
        set => Set(ref _tile, value);
    }

    /// <summary>Show the alpha channel as grey instead of the colour.</summary>
    public bool ShowAlphaOnly
    {
        get => _alphaOnly;
        set { if (Set(ref _alphaOnly, value)) ReloadAll(); }
    }

    /// <summary>Zoom in percent; 0 fits the frame to the pane.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            double clamped = value <= 0 ? 0 : Math.Clamp(value, 25, 800);
            if (Set(ref _zoom, clamped)) Raise(nameof(ZoomText));
        }
    }

    /// <summary>The zoom as "Fit" or "200 %".</summary>
    public string ZoomText => _zoom <= 0
        ? "Fit"
        : _zoom.ToString("0", CultureInfo.CurrentCulture) + " %";

    /// <summary>What the canvas shows behind the frame, from Settings.</summary>
    public Brush BackgroundBrush { get; private set; } = Brushes.Transparent;

    /// <summary>True when the checkerboard is in use, so the view can tile it.</summary>
    public bool IsCheckerboard { get; private set; } = true;

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand FirstCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand LastCommand { get; }
    public RelayCommand ForceLoopCommand { get; }

    /// <summary>Starts playback, forcing a loop first when the file would never animate.</summary>
    public void Play()
    {
        if (FrameCount < 2) return;
        _playback.Play();
        IsPlaying = _playback.Playing && EffectiveMode != AtxAnimationMode.Static;
    }

    /// <summary>Stops playback where it is.</summary>
    public void Pause()
    {
        _playback.Pause();
        IsPlaying = false;
    }

    private void TogglePlay()
    {
        if (IsPlaying) { Pause(); return; }
        // Play Once has already finished when it is holding the last frame; start it again.
        if (EffectiveMode == AtxAnimationMode.PlayOnce && _playback.CurrentFrame >= FrameCount - 1)
        {
            _playback.SetFrame(0);
            CurrentIndex = 0;
            ShowCurrent();
        }
        Play();
    }

    private void Step(int delta)
    {
        if (FrameCount == 0) return;
        int next = (_playback.CurrentFrame + delta + FrameCount) % FrameCount;
        Seek(next, pause: true);
    }

    private void StepZoom(int direction)
    {
        double current = _zoom <= 0 ? CurrentFitPercent : _zoom;
        var steps = ZoomSteps;
        if (direction > 0)
        {
            foreach (double step in steps)
            {
                if (step > current + 0.5) { Zoom = step; return; }
            }
            Zoom = steps[^1];
        }
        else
        {
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                if (steps[i] < current - 0.5) { Zoom = steps[i]; return; }
            }
            Zoom = steps[0];
        }
    }

    /// <summary>
    /// The percentage the pane is currently showing the frame at, which the view reports so that
    /// zooming out of Fit starts from what the user can see rather than from 100 %.
    /// </summary>
    public double CurrentFitPercent { get; set; } = 100;

    /// <summary>Ctrl+wheel: one notch per zoom step.</summary>
    public void AdjustZoom(int notches)
    {
        for (int i = 0; i < Math.Abs(notches); i++) StepZoom(Math.Sign(notches));
    }

    /// <summary>Shows one frame, optionally pausing on it (the scrubber keeps playing).</summary>
    public void Seek(int index, bool pause)
    {
        if (FrameCount == 0) return;
        index = Math.Clamp(index, 0, FrameCount - 1);
        if (pause) Pause();
        _playback.SetFrame(index);
        CurrentIndex = index;
        ShowCurrent();
    }

    // ── The render loop ───────────────────────────────────────────────────────

    /// <summary>
    /// Advances the controller by a real elapsed time. Returns true when the visible frame changed,
    /// which is the only case that touches a property.
    /// </summary>
    /// <param name="deltaSeconds">Seconds since the last tick, before the speed multiplier.</param>
    public bool Tick(double deltaSeconds)
    {
        if (!_isPlaying || deltaSeconds <= 0) return false;
        // A long stall (a modal dialog, a slow save) must not fast-forward the animation.
        if (deltaSeconds > 0.25) deltaSeconds = 0.25;

        bool changed = _playback.Advance(deltaSeconds * _speed);
        if (!_playback.Playing && _isPlaying) IsPlaying = false; // Play Once reached the end.
        if (!changed) return false;

        CurrentIndex = _playback.CurrentFrame;
        ShowCurrent();
        return true;
    }

    // ── Document changes ──────────────────────────────────────────────────────

    /// <summary>Rebuilds everything derived from the model, keeping the current frame where valid.</summary>
    public void OnDocumentRefreshed()
    {
        if (_disposed) return;

        var model = _document.Model;
        var frames = model?.Frames ?? [];
        var names = frames.Select(f => f.EffectiveFile ?? string.Empty).ToArray();

        _maskName = model?.Header.EffectiveAlphaMask;
        _effectiveFormat = ResolveEffectiveFormat(model);
        ApplyBackground();

        string signature = string.Join(
            '',
            _maskName ?? "-",
            ((int)_effectiveFormat).ToString(CultureInfo.InvariantCulture),
            _simulateFormat ? "q" : "-",
            _alphaOnly ? "a" : "-");

        bool signatureChanged = !string.Equals(signature, _signature, StringComparison.Ordinal);
        _signature = signature;

        var previous = _composites;
        var previousNames = _frameNames;
        _frameNames = names;
        _composites = new CompositeFrame?[names.Length];
        if (!signatureChanged)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (i < previousNames.Length && i < previous.Length
                    && string.Equals(previousNames[i], names[i], StringComparison.Ordinal))
                {
                    _composites[i] = previous[i];
                }
            }
        }

        ScrubberWeights = model is null
            ? []
            : [.. Enumerable.Range(0, names.Length).Select(i => (double)model.FrameTimeMs(i))];
        Raise(nameof(ScrubberWeights));
        Raise(nameof(FrameCount));

        // Loads already in flight were issued against the old signature. They share the current
        // token and name the same files, so every guard in LoadAsync would still pass and the
        // un-masked (or un-quantised) picture would land on top of the new one — and stick, because
        // EnsureLoaded never re-requests a slot that already holds something.
        if (signatureChanged) CancelLoads();

        SelectedIndex = _document.Frames.PrimarySelectedIndex;
        RebuildPlayback(preserveFrame: true);
        RequestLoads();
        ShowCurrent();
        RefreshCommands();
        RaiseAll(nameof(ShowStaticNotice), nameof(StaticNoticeText), nameof(StaticNoticeDetail),
            nameof(EffectiveMode));
    }

    /// <summary>The frames list changed its selection: the design says the preview pauses on it.</summary>
    public void OnFrameSelected(int index)
    {
        SelectedIndex = index;
        if (index < 0 || index >= FrameCount) return;
        if (index == _playback.CurrentFrame && !IsPlaying) return;
        Seek(index, pause: true);
    }

    /// <summary>Re-reads the preview background after the Settings dialog changes it.</summary>
    public void OnSettingsChanged()
    {
        ApplyBackground();
        Raise(nameof(BackgroundBrush));
        Raise(nameof(IsCheckerboard));
    }

    /// <summary>Drops every composited frame, e.g. when an image changed on disk.</summary>
    public void ReloadAll()
    {
        _composites = new CompositeFrame?[_frameNames.Length];
        _signature = string.Empty;
        CancelLoads();
        RequestLoads();
        ShowCurrent();
    }

    internal static EngineFormat ResolveEffectiveFormat(Cairn.Atx.Model.AtxModel? model)
    {
        if (model is null) return EngineFormat.Argb8888;
        var target = AtxSchema.ParseFormatToken(model.Header.EffectiveFormat)?.Format;
        bool hasMask = !string.IsNullOrWhiteSpace(model.Header.EffectiveAlphaMask);
        // Without a probe of frame 0 the source format is unknown; 8888 loses nothing, and the asset
        // pass replaces this with the real answer as soon as it lands.
        var source = EngineFormat.Argb8888;
        return AlphaMask.EffectiveFormat(target ?? source, target, hasMask);
    }

    private void ApplyBackground()
    {
        var settings = _document.Shell.AtxSettings;
        IsCheckerboard = settings.PreviewBackground == PreviewBackground.Checkerboard;
        BackgroundBrush = settings.PreviewBackground switch
        {
            PreviewBackground.Black => _document.Shell.Theme.Brush("Preview.Black", Brushes.Black),
            PreviewBackground.White => _document.Shell.Theme.Brush("Preview.White", Brushes.White),
            PreviewBackground.Custom => CustomBrush(settings.PreviewCustomColor),
            _ => _document.Shell.Theme.Brush("Preview.CheckerBrush", Brushes.Transparent),
        };
    }

    private static Brush CustomBrush(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex)
                && ColorConverter.ConvertFromString(hex) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException) { }
        return Brushes.Black;
    }

    private void RebuildPlayback(bool preserveFrame)
    {
        var model = _document.Model;
        int keep = preserveFrame ? _playback.CurrentFrame : 0;
        bool wasPlaying = _isPlaying;

        PlaybackSpec spec;
        if (model is null || model.Frames.Count == 0)
        {
            spec = new PlaybackSpec(AtxAnimationMode.Static, false, AtxSchema.DefaultFrameTimeMs, []);
        }
        else
        {
            spec = PlaybackSpec.FromModel(model);
            if (_modeOverride == PreviewModeOverride.ForceLoop)
            {
                spec = spec with { Mode = AtxAnimationMode.Loop, InitiallyOn = true };
            }
        }

        // Every parse and every asset pass lands here, so rebuilding unconditionally would reset
        // the dwell accumulator and the ping-pong direction several times a second while the user
        // edits — the animation visibly stalls, and a backwards ping-pong snaps forwards.
        if (preserveFrame && SameSpec(spec, _playback.Spec))
        {
            Raise(nameof(EffectiveMode));
            Raise(nameof(ShowStaticNotice));
            return;
        }

        _playback = new AtxPlayback(spec);
        _playback.Pause();
        if (keep > 0 && keep < spec.FrameCount) _playback.SetFrame(keep);
        CurrentIndex = spec.FrameCount == 0 ? -1 : _playback.CurrentFrame;

        if (wasPlaying && spec.FrameCount > 1 && spec.Mode != AtxAnimationMode.Static) Play();
        else IsPlaying = false;

        Raise(nameof(EffectiveMode));
        Raise(nameof(ShowStaticNotice));
    }

    /// <summary>
    /// Whether two specs would play identically. <see cref="PlaybackSpec"/> is a record, but its
    /// list of per-frame overrides compares by reference, so the generated equality would call
    /// every freshly built spec different.
    /// </summary>
    private static bool SameSpec(PlaybackSpec a, PlaybackSpec b) =>
        a.Mode == b.Mode && a.InitiallyOn == b.InitiallyOn
        && a.BaseFrameTimeMs == b.BaseFrameTimeMs
        && a.FrameTimeOverridesMs.SequenceEqual(b.FrameTimeOverridesMs);

    private void RefreshCommands()
    {
        FirstCommand.RaiseCanExecuteChanged();
        PreviousCommand.RaiseCanExecuteChanged();
        PlayPauseCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        LastCommand.RaiseCanExecuteChanged();
    }

    // ── Compositing ───────────────────────────────────────────────────────────

    /// <summary>Puts the current frame on screen from what is already composited.</summary>
    private void ShowCurrent()
    {
        int index = _currentIndex;
        if (index < 0 || index >= _composites.Length)
        {
            CurrentImage = null;
            PlaceholderText = _document.FrameCount == 0
                ? "This file has no frames yet. Add some to see them here."
                : "Select a frame to see it here.";
            UpdateReadout(null);
            return;
        }

        var composite = _composites[index];
        if (composite is null)
        {
            // Not composited yet: keep the last picture rather than flashing, and fetch it.
            EnsureLoaded(index);
            if (CurrentImage is null) PlaceholderText = "Decoding…";
            UpdateReadout(null);
            return;
        }

        CurrentImage = composite.Image;
        PlaceholderText = composite.Image is null
            ? composite.Error ?? $"Frame {index} could not be shown."
            : null;
        UpdateReadout(composite);
    }

    private void UpdateReadout(CompositeFrame? composite)
    {
        int index = _currentIndex;
        int count = FrameCount;
        if (count == 0 || index < 0)
        {
            ReadoutText = string.Empty;
            Raise(nameof(ReadoutText));
            return;
        }

        var parts = new List<string>(4)
        {
            string.Format(CultureInfo.CurrentCulture, "Frame {0} / {1}", index, count),
            _playback.FrameTimeMs(index).ToString(CultureInfo.CurrentCulture) + " ms",
        };

        var info = _document.ResolvedFrame(index)?.Info;
        if (info is not null)
        {
            parts.Add($"{info.Width}×{info.Height} {EngineFormats.DisplayName(info.Format)}");
        }
        else if (composite?.Image is not null)
        {
            parts.Add($"{composite.Width}×{composite.Height}");
        }
        if (_simulateFormat && EngineFormats.IsUncompressedRgb(_effectiveFormat))
        {
            parts.Add("shown as " + EngineFormats.DisplayName(_effectiveFormat));
        }

        ReadoutText = string.Join(" · ", parts);
        Raise(nameof(ReadoutText));
    }

    /// <summary>
    /// Composites every frame up front, newest request wins. Sequences too big to hold in memory
    /// stop at the budget; the rest load on demand as playback reaches them.
    /// </summary>
    private void RequestLoads()
    {
        long budget = PrefetchBudgetBytes;
        int start = Math.Max(0, _currentIndex);
        // Bounded by count as well as by memory: small frames slip under the byte budget, and
        // prefetching a 2,000-frame sequence would queue two thousand decodes at once. The rest
        // load on demand as playback or the scrubber reaches them.
        int limit = Math.Min(_frameNames.Length, PrefetchFrameLimit);
        for (int offset = 0; offset < limit; offset++)
        {
            int index = (start + offset) % _frameNames.Length;
            if (_composites[index] is { } done) { budget -= done.Bytes; continue; }
            if (budget <= 0) break;
            budget -= EstimatedBytes(index);
            EnsureLoaded(index);
        }
    }

    /// <summary>How many frames ahead of the current one the preview composites in advance.</summary>
    private const int PrefetchFrameLimit = 192;

    private long EstimatedBytes(int index)
    {
        var info = _document.ResolvedFrame(index)?.Info;
        return info is null ? 1024L * 1024 : (long)info.Width * info.Height * 4;
    }

    private void EnsureLoaded(int index)
    {
        if (index < 0 || index >= _frameNames.Length) return;
        if (_composites[index] is not null) return;
        string name = _frameNames[index];
        if (string.IsNullOrEmpty(name))
        {
            _composites[index] = new CompositeFrame(null, $"Frame {index} has no image file.");
            return;
        }

        var request = new CompositeRequest(name, _maskName, _effectiveFormat, _simulateFormat, _alphaOnly);
        // A frame already composited under this exact key costs nothing to fetch synchronously.
        if (_document.Shell.Compositor.Peek(_document.Resolver, request) is { } cached)
        {
            _composites[index] = cached;
            return;
        }
        // Without this, scrubbing back and forth over a frame that has not finished compositing
        // queues a fresh decode on every pass and floods the thread pool.
        if (!_loading.Add(index)) return;
        _ = LoadAsync(index, name, request, _loads.Token);
    }

    private readonly HashSet<int> _loading = [];

    private async Task LoadAsync(int index, string name, CompositeRequest request, CancellationToken token)
    {
        try
        {
            var composite = await _document.Shell.Compositor
                .GetAsync(_document.Resolver, request, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || _disposed) return;
            if (index >= _frameNames.Length
                || !string.Equals(_frameNames[index], name, StringComparison.Ordinal)) return;
            // The mask, target format or a display toggle may have changed while this was decoding.
            if (!string.Equals(request.MaskName, _maskName, StringComparison.Ordinal)
                || request.EffectiveFormat != _effectiveFormat
                || request.Simulate != _simulateFormat || request.AlphaOnly != _alphaOnly) return;

            _composites[index] = composite;
            if (index == _currentIndex) ShowCurrent();
        }
        catch (OperationCanceledException) { }
        finally { _loading.Remove(index); }
    }

    private void CancelLoads()
    {
        var old = _loads;
        _loads = new CancellationTokenSource();
        _loading.Clear();
        old.Cancel();
        old.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pause();
        _loads.Cancel();
        _loads.Dispose();
        _composites = [];
    }
}
