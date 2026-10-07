using System.Windows;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Viewport;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Ui.Mvvm;

namespace Cairn.Vfx.Ui.Documents;

/// <summary>
/// An open .vfx file. Owns the snapshot history, the sampler/particle simulators built from the current
/// snapshot, playback (VFX time base) and the selection. Views and the module read everything from here.
/// </summary>
public sealed class VfxDocument : SnapshotDocument<VfxFile>, Viewport.IVfxScene
{
    IReadOnlySet<int> Viewport.IVfxScene.HiddenSections => HiddenSections;

    /// <summary>4800 ticks per second, 320 ticks per frame (15 fps).</summary>
    public static readonly TimeBase VfxTimeBase = new(4800, 320);

    private VfxPlaybackMode _mode = VfxPlaybackMode.Loop;
    private VfxPlaybackState _state;

    public VfxDocument(IShellContext shell, IDocumentKind kind, VfxFile file, string name, string? path, string? origin = null)
        : base(shell, kind, file, name, path, origin)
    {
        Playback = new PlaybackViewModel(() => TimeUnit.Frames, VfxTimeBase);
        Playback.TimeChanged += (_, _) => UpdateState();
        Selection.Changed += (_, _) => Raise(nameof(SelectedSection));
        ConvertCommand = new RelayCommand(Convert, () => IsOlderVersion);
        Rebuild();
    }

    public PlaybackViewModel Playback { get; }
    public VfxSelection Selection { get; } = new();
    public VfxSampler Sampler { get; private set; } = null!;
    public VfxParticleSimulator[] Simulators { get; private set; } = [];
    /// <summary>Sections hidden in the preview (view state, never saved).</summary>
    public HashSet<int> HiddenSections { get; } = [];
    public RelayCommand ConvertCommand { get; }
    /// <summary>Effect end frame used for playback: header end frame, or the longest mesh when that is 0.</summary>
    public int EndFrame { get; private set; }
    /// <summary>Playback state at the current transport time (effect frame, visibility, emission).</summary>
    public VfxPlaybackState State => _state;
    /// <summary>Unwrapped playback frame (transport time in frames) for the particle simulators.</summary>
    public float TimelineFrame => (float)(Playback.Time / VfxTimeBase.TicksPerFrame);
    public bool IsOlderVersion => Current.Version < VfxVersion.Current;
    public string VersionText => $"0x{Current.Version:X5}";
    public VfxSection? SelectedSection => Selection.Primary >= 0 && Selection.Primary < Current.Sections.Length ? Current.Sections[Selection.Primary] : null;

    /// <summary>Raised when the sampler is rebuilt (snapshot changed).</summary>
    public event EventHandler? SceneChanged;
    /// <summary>Raised when the effect frame or visibility changes.</summary>
    public event EventHandler? FrameChanged;
    /// <summary>Raised when a preview visibility toggle changes.</summary>
    public event EventHandler? VisibilityChanged;

    public VfxPlaybackMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            Playback.Loop = value == VfxPlaybackMode.Loop;
            BuildSimulators();
            UpdateState();
            Raise(nameof(ModeText));
        }
    }

    public string ModeText => _mode switch { VfxPlaybackMode.OneShot => "One-shot", VfxPlaybackMode.HoldLastFrame => "Hold last frame", _ => "Loop" };

    /// <summary>"1 frame", "2 frames".</summary>
    internal static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    public override IReadOnlyList<StatusItem> StatusItems =>
    [
        new($"VFX {VersionText}", IsOlderVersion ? "Older format version: convert to edit" : "Current format version"),
        new(Plural(EndFrame, "frame"), "Effect length at 15 frames per second"),
        new(Plural(Sampler.Meshes.Count + Sampler.ParticleSystems.Count + Sampler.Dummies.Count + Sampler.Lights.Count, "object"), "Meshes, particle systems, dummies and lights"),
        new(ModeText, "Playback mode"),
        .. _gizmoStatus,
        .. _vertexStatus,
        .. _problemStatus,
    ];

    private IReadOnlyList<StatusItem> _problemStatus = [], _vertexStatus = [], _gizmoStatus = [];

    /// <summary>Gizmo toggles ("Auto-key on, Local axes"; set by VfxModule.Timeline.cs).</summary>
    public IReadOnlyList<StatusItem> GizmoStatus { get => _gizmoStatus; set { _gizmoStatus = value; RaiseAll(nameof(StatusItems)); } }

    /// <summary>Vertex mode status ("Vertex mode: N of M vertices, scope"; set by VfxModule.Vertex.cs while the mode is on).</summary>
    public IReadOnlyList<StatusItem> VertexStatus
    {
        get => _vertexStatus;
        set { if (_vertexStatus.Count == 0 && value.Count == 0) return; _vertexStatus = value; RaiseAll(nameof(StatusItems)); }
    }

    /// <summary>Problem counts shown on the status bar (set by the Problems tab after each check; clicking opens it).</summary>
    public IReadOnlyList<StatusItem> ProblemStatus
    {
        get => _problemStatus;
        set { _problemStatus = value; RaiseAll(nameof(StatusItems)); }
    }

    public void SetHidden(int section, bool hidden)
    {
        if (hidden ? HiddenSections.Add(section) : HiddenSections.Remove(section)) VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scrubs to an effect frame.</summary>
    public void SeekFrame(float frame) => Playback.Seek((float)(frame * VfxTimeBase.TicksPerFrame));

    protected override VfxFile Parse(byte[] bytes, string name) => VfxReader.Read(bytes, name);
    protected override byte[] Write(VfxFile snapshot) => VfxWriter.Write(snapshot);

    protected override void OnSnapshotChanged()
    {
        var map = SectionMap();
        var hidden = HiddenSections.Select(map).Where(i => i >= 0).ToList();
        bool hiddenChanged = !HiddenSections.SetEquals(hidden);
        if (hiddenChanged) { HiddenSections.Clear(); HiddenSections.UnionWith(hidden); }
        Rebuild();
        Selection.Remap(map);
        if (hiddenChanged) VisibilityChanged?.Invoke(this, EventArgs.Empty);
        RaiseAll(nameof(IsOlderVersion), nameof(VersionText), nameof(StatusItems), nameof(SelectedSection));
        ConvertCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The sections that selection and hidden indices refer to (the snapshot before the current one).</summary>
    private System.Collections.Immutable.ImmutableArray<VfxSection> _indexedSections;

    /// <summary>
    /// Maps a section index of the previous snapshot to the same object's index now (-1 when it is gone). Objects are
    /// matched by reference, so a move, delete, duplicate or their undo keeps the selection and hidden state on the
    /// object; a section replaced in place by an edit (a new reference the previous snapshot did not have) keeps its index.
    /// </summary>
    private Func<int, int> SectionMap()
    {
        var old = _indexedSections;
        var now = Current.Sections;
        _indexedSections = now;
        if (old.IsDefault || old == now) return i => i < now.Length ? i : -1;
        var at = new Dictionary<VfxSection, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < now.Length; i++) at.TryAdd(now[i], i);
        var was = new HashSet<VfxSection>(old, ReferenceEqualityComparer.Instance);
        return i => i < 0 || i >= old.Length ? -1
            : at.TryGetValue(old[i], out int j) ? j
            : i < now.Length && !was.Contains(now[i]) ? i : -1;
    }

    private void Rebuild()
    {
        _indexedSections = Current.Sections;
        Sampler = new VfxSampler(Current);
        int end = Current.EndFrame;
        if (end <= 0)
            foreach (var m in Sampler.Meshes) end = Math.Max(end, (int)MathF.Ceiling(m.StartSeconds * VfxTime.FramesPerSecond) + m.FrameCount);
        EndFrame = Math.Max(1, end);
        Playback.SetClip(new PlaybackRange(0, (int)(EndFrame * VfxTimeBase.TicksPerFrame), 0, 0, Array.Empty<int>()));
        Playback.Loop = _mode == VfxPlaybackMode.Loop;
        BuildSimulators();
        SceneChanged?.Invoke(this, EventArgs.Empty);
        UpdateState();
    }

    private void BuildSimulators() =>
        Simulators = [.. Enumerable.Range(0, Sampler.ParticleSystems.Count).Select(i => new VfxParticleSimulator(Sampler, i, 1, _mode))];

    private void UpdateState()
    {
        _state = VfxPlayback.Evaluate(_mode, Playback.Time / VfxTimeBase.TicksPerSecond, EndFrame);
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Convert()
    {
        if (Apply("Convert to current format", VfxUpgrade.ToCurrent)) ShowStatus("Converted to the current format version.");
    }

    /// <summary>
    /// The first Save after converting an older file asks once whether to overwrite the original (its legacy fields are
    /// not kept) or save a new file; the answer holds for this document.
    /// </summary>
    public override bool? ChooseSaveAsInstead()
    {
        if (FilePath is null || SavedSnapshot.Version >= VfxVersion.Current || IsOlderVersion) return false;
        if (_saveAsChosen is { } remembered) return remembered;
        int choice = Shell.Dialogs.Choose("Save the converted effect",
            $"{DisplayName} was converted from an older format version. Overwriting the original replaces it on disk; the older layout's unused legacy fields are not kept.",
            ["Save As...", "Overwrite original", "Cancel"], 2);
        if (choice == 2) return null;
        _saveAsChosen = choice == 0;
        return _saveAsChosen;
    }

    private bool? _saveAsChosen;

    public override void OnDeactivated() { Playback.Pause(); base.OnDeactivated(); }

    /// <summary>Stops the playback clock first: a playing clock is held by the static render event and would keep the document alive.</summary>
    public override void Dispose() { Playback.Pause(); base.Dispose(); }

    protected override FrameworkElement CreateView() => new VfxDocumentView(this);
}
