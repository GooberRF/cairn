using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.ViewModels.ClipTools;

/// <summary>
/// A clip document's "compare with" state: another clip played as a ghost skeleton in sync with the
/// document (same offset from start, looped over the compared clip's duration), shown and hidden from
/// the chip in the viewport header. Not part of the document: no undo step, not saved.
/// </summary>
public sealed class ClipCompareViewModel : ObservableObject
{
    /// <summary>The ghost id in <see cref="SceneViewModel"/>.</summary>
    public const string GhostId = "compare";

    /// <summary>The theme brush the ghost is drawn with.</summary>
    public const string BrushKey = "Viewport.GhostCompare";

    private readonly ClipDocumentViewModel _document;
    private RfaClip? _clip;
    private string? _name;
    private bool _isVisible = true;

    public ClipCompareViewModel(ClipDocumentViewModel document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        ClearCommand = new RelayCommand(Clear, () => IsActive);
        ToggleVisibleCommand = new RelayCommand(() => IsVisible = !IsVisible, () => IsActive);
    }

    /// <summary>The compared clip, or null.</summary>
    public RfaClip? Clip => _clip;

    /// <summary>The compared clip's file name, or null.</summary>
    public string? ClipName => _name;

    /// <summary>True when a clip is being compared.</summary>
    public bool IsActive => _clip is not null;

    /// <summary>Shows or hides the ghost (the comparison is kept).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!Set(ref _isVisible, value)) return;
            RaiseAll(nameof(VisibilityGlyph), nameof(VisibilityToolTip));
            Apply();
        }
    }

    /// <summary>"Compare: ult2_run.rfa".</summary>
    public string ChipText => _name is null ? string.Empty : "Compare: " + _name;

    /// <summary>The chip's tooltip: what the ghost is and how it is timed.</summary>
    public string ChipToolTip => _clip is null || _name is null
        ? string.Empty
        : string.Format(CultureInfo.CurrentCulture,
            "{0} plays as a ghost skeleton over this clip: {1} bones, {2}. It starts with this clip and loops over its own length.{3}",
            _name, _clip.BoneCount, TimeFormat.Duration(_clip.Duration),
            _clip.BoneCount != _document.Current.BoneCount ? $"\nIts bone count differs from this clip's ({_document.Current.BoneCount}): bones match by index." : string.Empty);

    /// <summary>Eye / crossed-eye glyph (Segoe MDL2).</summary>
    public string VisibilityGlyph => _isVisible ? "" : "";

    public string VisibilityToolTip => _isVisible ? "Hide the compared clip's ghost" : "Show the compared clip's ghost";

    public RelayCommand ClearCommand { get; }

    public RelayCommand ToggleVisibleCommand { get; }

    /// <summary>Compares with <paramref name="clip"/> (shown).</summary>
    public void Set(RfaClip clip, string name)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _name = name;
        _isVisible = true;
        RaiseState();
        Apply();
    }

    /// <summary>Stops comparing.</summary>
    public void Clear()
    {
        if (_clip is null) return;
        _clip = null;
        _name = null;
        _isVisible = true;
        RaiseState();
        Apply();
    }

    /// <summary>What a dialog restores on Cancel.</summary>
    public (RfaClip? Clip, string? Name, bool Visible) Capture() => (_clip, _name, _isVisible);

    /// <summary>Puts back a state from <see cref="Capture"/>.</summary>
    public void Restore((RfaClip? Clip, string? Name, bool Visible) state)
    {
        _clip = state.Clip;
        _name = state.Name;
        _isVisible = state.Visible;
        RaiseState();
        Apply();
    }

    /// <summary>
    /// The compared clip's time for the document time <paramref name="time"/>: the same offset from the
    /// (shown) clip's start, looped over the compared clip's duration; the compared clip's end is held at
    /// the exact end of each loop rather than snapping to its start.
    /// </summary>
    public float MapTime(float time)
    {
        var clip = _clip;
        if (clip is null) return time;
        int duration = clip.Duration;
        if (duration <= 0) return clip.StartTime;
        double offset = time - _document.ShownClip.StartTime;
        if (offset <= 0) return clip.StartTime;
        double wrapped = offset % duration;
        if (wrapped == 0) wrapped = duration;
        return (float)(clip.StartTime + wrapped);
    }

    private void Apply()
    {
        var show = _clip is not null && _isVisible ? _clip : null;
        _document.Scene.SetGhost(GhostId, show, ChipText, BrushKey, show is null ? null : MapTime);
    }

    private void RaiseState()
    {
        RaiseAll(nameof(Clip), nameof(ClipName), nameof(IsActive), nameof(IsVisible), nameof(ChipText), nameof(ChipToolTip),
            nameof(VisibilityGlyph), nameof(VisibilityToolTip));
        ClearCommand.RaiseCanExecuteChanged();
        ToggleVisibleCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>
/// Clip › Compare With…: picks a library clip and shows it as the compare ghost while the dialog is open
/// (live); OK keeps it, Cancel puts back whatever was compared before. Not an undo step.
/// </summary>
public sealed class CompareDialogViewModel : ClipDialogViewModel
{
    private readonly (RfaClip? Clip, string? Name, bool Visible) _before;
    private CancellationTokenSource? _cts;
    private RfaClip? _loaded;
    private string? _loadedName;
    private bool _ended;
    private bool _committed;
    private int _request;

    public CompareDialogViewModel(ClipDocumentViewModel document) : base(document)
    {
        _before = document.Compare.Capture();
        var snapshot = Shell.Assets.Snapshot;
        int bones = document.Current.BoneCount;
        var choices = snapshot.Clips
            .Where(c => c.IsReadable && !string.Equals(c.Name, document.DisplayName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.BoneCount == bones ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new LibraryClipChoice(c, c.BoneCount == bones ? TimeText(c) : $"{c.BoneCount} bones — does not fit"));
        Clips = new FilteredList<LibraryClipChoice>(choices, c => c.Clip.Name);
        Clips.SelectionChanged += (_, _) => Load();
        SummaryLines = [Clips.IsEmpty ? "The library has no other clips. Set the game folder or add a search folder (Tools › Settings)." : "Pick a clip to see it as a ghost beside this one."];
    }

    private static string TimeText(Cairn.Rfa.Assets.LibraryClip c) =>
        c.Facts is { } f ? string.Format(CultureInfo.CurrentCulture, "{0:0.##} frames", (f.EndTime - f.StartTime) / (double)RfaClip.TicksPerFrame) : string.Empty;

    public override string ToolId => "compare";

    public override string Title => "Compare With";

    public override string Heading => "Compare with another clip";

    public override string Description =>
        "Plays another clip as a ghost skeleton over this one, in sync from the start and looped over its own length. "
        + "The comparison stays until you clear it from the chip in the viewport header; it never changes the clip.";

    public override string ApplyText => "Compare";

    public override string ApplyToolTip => "Keep the ghost of the picked clip in the viewport (no undo step; clear it from the viewport header)";

    public override string PreviewHint => "The ghost shows in the viewport as soon as you pick a clip. Cancel puts back what was there before.";

    /// <summary>Library clips, same bone count first.</summary>
    public FilteredList<LibraryClipChoice> Clips { get; }

    public override bool CanApply => !_ended && !IsBusy && Error is null && _loaded is not null;

    /// <summary>The loaded clip shown as the ghost, or null.</summary>
    public RfaClip? Loaded => _loaded;

    private async void Load()
    {
        if (_ended || Clips.Selected is not { } choice) return;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        int request = ++_request;
        IsBusy = true;
        Error = null;
        SummaryLines = [$"Loading {choice.Clip.Name}…"];
        try
        {
            var clip = await Shell.Assets.LoadClipAsync(choice.Clip, cts.Token).ConfigureAwait(true);
            if (request != _request || _ended) return;
            _loaded = clip;
            _loadedName = choice.Clip.Name;
            Document.Compare.Set(clip, choice.Clip.Name);
            var lines = new List<string>
            {
                $"{choice.Clip.Name}: {clip.BoneCount} bones, {TimeFormat.Duration(clip.Duration)}",
                clip.Duration == Document.Current.Duration
                    ? "Same length as this clip: both loop together."
                    : $"This clip is {TimeFormat.Duration(Document.Current.Duration)}; the ghost loops over its own length.",
            };
            if (clip.BoneCount != Document.Current.BoneCount)
                lines.Add($"Its bone count differs from this clip's ({Document.Current.BoneCount}); bones match by index, so the ghost may look wrong.");
            SummaryLines = lines;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Cairn.Formats.AssetFormatException or ArgumentException or InvalidOperationException)
        {
            if (request != _request || _ended) return;
            _loaded = null;
            Error = $"{choice.Clip.Name} could not be read: {UserMessage(ex)}";
            SummaryLines = [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Cairn.Ui.Services.ErrorLog.Write("compare with", ex);
            if (request != _request || _ended) return;
            _loaded = null;
            Error = $"{choice.Clip.Name} could not be shown ({ex.GetType().Name}: {ex.Message}). Details were written to the error log.";
            SummaryLines = [];
        }
        finally
        {
            if (request == _request)
            {
                IsBusy = false;
                Raise(nameof(Loaded));
                Raise(nameof(CanApply));
            }
        }
    }

    /// <summary>Waits until no load is running (tests).</summary>
    public async Task<bool> SettleAsync(int timeoutMs = 60_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (IsBusy && !_ended)
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(15).ConfigureAwait(true);
        }
        return true;
    }

    public override bool Commit()
    {
        if (!CanApply || _loaded is null) return false;
        Document.Compare.Set(_loaded, _loadedName ?? "clip");
        _committed = true;
        End();
        return true;
    }

    public override void End()
    {
        if (_ended) return;
        _ended = true;
        _cts?.Cancel();
        if (!_committed) Document.Compare.Restore(_before);
        IsBusy = false;
        Raise(nameof(CanApply));
    }
}
