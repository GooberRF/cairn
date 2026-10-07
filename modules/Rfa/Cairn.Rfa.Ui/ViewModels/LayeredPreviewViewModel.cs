using System.Collections.ObjectModel;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>An entry of the layered preview's state picker.</summary>
/// <param name="Clip">The library clip.</param>
/// <param name="Label">"ult2_stand.rfa".</param>
/// <param name="Note">"state stand · entity.tbl", "25 bones".</param>
/// <param name="IsTableState">True when the tables use it as a state of the preview mesh.</param>
public sealed record StateClipOption(LibraryClip Clip, string Label, string Note, bool IsTableState)
{
    public string ToolTip => $"{Clip.Name} · {Note} · {Clip.Location.DisplayLocation}";

    public override string ToString() => Label;
}

/// <summary>
/// "Play as action over state" (DESIGN.md "Layered preview"): the document's clip plays as an ACTION —
/// per-bone weights, ramp in and ramp out — over a looping STATE clip picked from the library, blended
/// exactly as the engine does through <see cref="ClipBlender"/>. It drives the viewport through
/// <see cref="SceneViewModel.PoseOverride"/>, so a clip tool's preview also plays layered, and it never
/// touches the document.
/// </summary>
public sealed class LayeredPreviewViewModel : ObservableObject
{
    private readonly ClipDocumentViewModel _doc;
    private StateClipOption? _selected;
    private RfaClip? _state;
    private bool _isActive;
    private string? _status;
    private int _request;

    internal LayeredPreviewViewModel(ClipDocumentViewModel document)
    {
        _doc = document;
        ClearCommand = new RelayCommand(() => IsActive = false, () => _isActive);
        _doc.PreviewSkeletonChanged += (_, _) => RebuildOptions();
        RebuildOptions();
    }

    /// <summary>The state picker's entries: the preview mesh's table states first, then clips with the clip's bone count.</summary>
    public ObservableCollection<StateClipOption> Options { get; } = [];

    /// <summary>The state clip; picking one loads it (off the UI thread) and turns the layering on.</summary>
    public StateClipOption? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (value is null)
            {
                // A load still in flight must not turn layering back on when it lands.
                ++_request;
                IsActive = false;
                return;
            }
            _ = LoadAsync(value);
        }
    }

    /// <summary>True while the viewport plays the clip layered over the state.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (value && _state is null)
            {
                if (_selected is null && Options.Count > 0) Selected = Options[0];
                // Turned on once the state has loaded.
                Raise();
                return;
            }
            if (!value) ++_request;
            if (!Set(ref _isActive, value)) return;
            Apply();
        }
    }

    /// <summary>The loaded state clip, or null.</summary>
    public RfaClip? State => _state;

    /// <summary>The badge over the viewport while active.</summary>
    public string BadgeText => _isActive && _state is not null && _selected is not null
        ? $"Layered: this clip as an action over the state {_selected.Label}"
        : string.Empty;

    /// <summary>A load failure or other note, or null.</summary>
    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string ToolTip =>
        "Play as action over state: the clip plays as an action (its bone weights and ramps) over a looping state clip, blended exactly as the game does. "
        + "Pick the state here; the table states of the preview mesh come first. Only the preview changes; the document does not.";

    public RelayCommand ClearCommand { get; }

    /// <summary>Re-reads the library (library, tables or preview mesh changed).</summary>
    public void RebuildOptions()
    {
        var shell = _doc.Shell;
        var library = shell.Assets.Snapshot;
        var usage = shell.Assets.Usage;
        int bones = _doc.Current.BoneCount;
        var options = new List<StateClipOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_doc.Scene.MeshName is { } mesh)
        {
            foreach (var list in usage.ClipListsForMesh(mesh))
            {
                foreach (var use in list.Clips.Where(c => c.Kind == ClipUsageKind.State))
                {
                    if (library.FindClip(use.ClipBaseName) is { IsReadable: true } clip && clip.BoneCount == bones && seen.Add(clip.Name)
                        && !string.Equals(clip.Name, _doc.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        string note = $"state {use.SlotName}" + (use.WeaponBlock is { Length: > 0 } w ? $" ({w})" : string.Empty) + $" · {list.Table}";
                        options.Add(new StateClipOption(clip, clip.Name, note, true));
                    }
                }
            }
        }
        foreach (var clip in library.CompatibleClips(bones))
        {
            if (!clip.IsReadable || !seen.Add(clip.Name) || string.Equals(clip.Name, _doc.DisplayName, StringComparison.OrdinalIgnoreCase)) continue;
            options.Add(new StateClipOption(clip, clip.Name, $"{clip.BoneCount} bones", false));
        }
        var keep = _selected;
        Options.Clear();
        foreach (var o in options) Options.Add(o);
        if (keep is not null && Options.FirstOrDefault(o => string.Equals(o.Clip.Name, keep.Clip.Name, StringComparison.OrdinalIgnoreCase)) is { } again)
        {
            _selected = again;
            Raise(nameof(Selected));
        }
    }

    /// <summary>Picks a state clip by name (diagnostics, tests); returns false when it is not offered.</summary>
    public async Task<bool> UseStateAsync(string clipName)
    {
        var option = Options.FirstOrDefault(o => string.Equals(o.Clip.Name, clipName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(o.Clip.BaseName, clipName, StringComparison.OrdinalIgnoreCase));
        if (option is null) return false;
        _selected = option;
        Raise(nameof(Selected));
        await LoadAsync(option);
        return _state is not null;
    }

    private async Task LoadAsync(StateClipOption option)
    {
        int request = ++_request;
        Status = $"Loading {option.Label}…";
        try
        {
            var clip = await _doc.Shell.Assets.LoadClipAsync(option.Clip);
            if (request != _request) return;
            _state = clip;
            Status = clip.BoneCount != _doc.Current.BoneCount
                ? $"{option.Label} has {clip.BoneCount} bones and this clip {_doc.Current.BoneCount}; bones are matched by index, as in the game."
                : null;
            Raise(nameof(State));
            _isActive = true;
            Raise(nameof(IsActive));
            Apply();
        }
        catch (Exception ex) when (RfaWorkspace.IsReadFailure(ex))
        {
            if (request != _request) return;
            Status = $"{option.Label} could not be loaded: {ex.Message}";
            _state = null;
            _isActive = false;
            Raise(nameof(IsActive));
            Apply();
        }
    }

    private void Apply()
    {
        _doc.Scene.PoseOverride = _isActive && _state is not null ? Blend : null;
        _doc.Scene.RefreshPose();
        RaiseAll(nameof(BadgeText), nameof(IsActive));
        ClearCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The state's time for an action time: the state loops from its start, in step with the action's start.</summary>
    public static float StateTime(RfaClip state, RfaClip action, float actionTime)
    {
        int duration = state.EndTime - state.StartTime;
        if (duration <= 0) return state.StartTime;
        double dt = actionTime - action.StartTime;
        double phase = dt - Math.Floor(dt / duration) * duration;
        return (float)(state.StartTime + phase);
    }

    private bool Blend(Pose pose, RfaClip action, float time)
    {
        if (_state is not { } state) return false;
        ClipBlender.Blend(state, StateTime(state, action, time), action, time, pose.Local, pose.Skeleton.RestLocal.AsSpan());
        pose.SolveWorld();
        return true;
    }
}
