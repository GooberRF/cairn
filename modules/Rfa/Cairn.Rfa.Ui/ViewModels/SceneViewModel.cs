using System.Numerics;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.Viewport;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>A batch address inside a mesh, for highlights.</summary>
public readonly record struct BatchRef(int Submesh, int Lod, int Batch);

/// <summary>What a click in a mesh tab's viewport picked besides bones.</summary>
public enum SceneItemKind
{
    /// <summary>Nothing (a click on empty space).</summary>
    None,
    /// <summary>A collision sphere (index in file order).</summary>
    Sphere,
    /// <summary>A prop point (index in <see cref="SceneViewModel.PropPoints"/>).</summary>
    Prop,
}

/// <summary>A collision sphere or prop point picked in the viewport, or nothing.</summary>
public readonly record struct ScenePick(SceneItemKind Kind, int Index);

/// <summary>
/// What one document's viewport shows: the mesh, its skeleton and current pose, the clip and time
/// driving it, the LOD, highlights and notices, and the camera. Both document kinds own one; the
/// <c>Viewport.ViewportControl</c> renders it and writes bone picks back into <see cref="Selection"/>.
/// </summary>
/// <remarks>
/// The pose comes from <see cref="Clip"/> at <see cref="Time"/> through <see cref="ClipSampler"/> (via
/// <see cref="Pose.Sample"/>), or the bind pose. <see cref="PoseOverride"/> is the seam for the layered
/// "play as action over state" preview of phase 5: it receives the pose buffers and the time and fills
/// them through <c>ClipBlender</c>; returning false falls back to the plain clip.
/// </remarks>
public sealed class SceneViewModel : ObservableObject
{
    private V3dFile? _mesh;
    private RfaClip? _clip;
    private float _time;
    private int _lod;
    private string? _notice;
    private bool _noticeIsWarning;
    private IReadOnlyList<Vector3> _rootPath = [];
    private IReadOnlySet<BatchRef> _highlightBatches = new HashSet<BatchRef>();
    private int _highlightSphere = -1;
    private int _highlightProp = -1;

    public SceneViewModel(ViewportDisplaySettings display, BoneSelection selection, TextureService textures)
    {
        Display = display ?? throw new ArgumentNullException(nameof(display));
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        Textures = textures ?? throw new ArgumentNullException(nameof(textures));
        // weak: the display settings are the workspace's and outlive every document's scene
        System.Windows.WeakEventManager<ViewportDisplaySettings, EventArgs>.AddHandler(Display, nameof(ViewportDisplaySettings.Changed), OnDisplayChanged);
        Selection.Changed += (_, _) => OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        RefreshPose();
        OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The shared display toggles.</summary>
    public ViewportDisplaySettings Display { get; }

    /// <summary>The document's bone selection.</summary>
    public BoneSelection Selection { get; }

    /// <summary>
    /// Raised after a click in the viewport picked a bone (already written into <see cref="Selection"/>):
    /// the document brings up that bone's editor (mesh structure node, clip Bone inspector).
    /// </summary>
    public event EventHandler<int>? BonePicked;

    /// <summary>Called by the viewport after a click picked <paramref name="bone"/>.</summary>
    public void NotifyBonePicked(int bone)
    {
        if (bone >= 0) BonePicked?.Invoke(this, bone);
    }

    /// <summary>
    /// Raised after a click in a mesh tab's viewport picked a collision sphere or prop point, or nothing
    /// (<see cref="SceneItemKind.None"/>): the document selects (or lets go of) its Structure node.
    /// </summary>
    public event EventHandler<ScenePick>? ItemPicked;

    /// <summary>Called by the viewport after a click picked a sphere, a prop point or nothing.</summary>
    public void NotifyItemPicked(ScenePick pick) => ItemPicked?.Invoke(this, pick);

    /// <summary>Decodes and caches textures.</summary>
    public TextureService Textures { get; }

    /// <summary>The camera (kept here so it survives the view being rebuilt).</summary>
    public OrbitCamera Camera { get; } = new();

    /// <summary>The mesh shown, or null.</summary>
    public V3dFile? Mesh => _mesh;

    /// <summary>The mesh's file name, for notices.</summary>
    public string? MeshName { get; private set; }

    /// <summary>Resolves the mesh's texture names (document folder first).</summary>
    public AssetResolver? TextureResolver { get; private set; }

    /// <summary>The mesh's skeleton (empty for a static mesh or none).</summary>
    public Skeleton Skeleton { get; private set; } = Skeleton.Empty;

    /// <summary>Current pose buffers, or null when the mesh has no bones.</summary>
    public Pose? Pose { get; private set; }

    /// <summary>Incremented whenever the mesh is replaced.</summary>
    public int MeshVersion { get; private set; }

    /// <summary>Incremented whenever the pose (or the morph state) changes.</summary>
    public int PoseVersion { get; private set; }

    /// <summary>The clip driving the pose, or null for the bind pose.</summary>
    public RfaClip? Clip => _clip;

    /// <summary>The time the pose is sampled at, in ticks.</summary>
    public float Time => _time;

    /// <summary>Bytes allocated by pose evaluation since the scene was created (frame-time report).</summary>
    public long PoseAllocatedBytes { get; set; }

    /// <summary>Milliseconds the last pose evaluation took (frame-time report).</summary>
    public double LastPoseMs { get; private set; }

    /// <summary>
    /// Fills the pose for a time (the layered "play as action over state" preview); false = use the
    /// clip. It receives the clip the scene would otherwise sample (<see cref="EffectiveClip"/>), so a
    /// clip tool's preview also plays layered.
    /// </summary>
    public Func<Pose, RfaClip, float, bool>? PoseOverride { get; set; }

    /// <summary>
    /// A clip shown instead of <see cref="Clip"/> while a clip tool's dialog is open (its live preview),
    /// or null. The document itself is not touched.
    /// </summary>
    public RfaClip? PreviewClip => _previewClip;

    /// <summary>The clip the pose is sampled from: <see cref="PreviewClip"/> when set, else <see cref="Clip"/>.</summary>
    public RfaClip? EffectiveClip => _previewClip ?? _clip;

    private RfaClip? _previewClip;

    /// <summary>Shows a preview snapshot instead of the document's clip (null ends the preview).</summary>
    public void SetPreviewClip(RfaClip? clip)
    {
        if (ReferenceEquals(clip, _previewClip)) return;
        _previewClip = clip;
        RebuildRootPath();
        Raise(nameof(PreviewClip));
        Raise(nameof(HasMorph));
        RefreshPose();
        PreviewClipChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when <see cref="PreviewClip"/> changes (the viewport shows a "Preview" badge).</summary>
    public event EventHandler? PreviewClipChanged;

    // ── Ghost skeletons ──────────────────────────────────────────────────────

    private readonly List<GhostSkeleton> _ghosts = [];

    /// <summary>
    /// Extra skeletons drawn dimmed over the scene (the unedited clip, a compared clip), each posed from
    /// its own clip at the scene's time (mapped by <see cref="GhostSkeleton.TimeMap"/>).
    /// </summary>
    public IReadOnlyList<GhostSkeleton> Ghosts => _ghosts;

    /// <summary>
    /// Adds or replaces the ghost <paramref name="id"/>; a null clip removes it. <paramref name="brushKey"/>
    /// names the theme brush it is drawn with ("Viewport.Ghost", "Viewport.GhostCompare").
    /// </summary>
    public void SetGhost(string id, RfaClip? clip, string label, string brushKey = "Viewport.Ghost", Func<float, float>? timeMap = null,
        Skeleton? skeleton = null, Vector3 offset = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        int index = _ghosts.FindIndex(g => g.Id == id);
        if (clip is null)
        {
            if (index < 0) return;
            _ghosts.RemoveAt(index);
        }
        else
        {
            var ghost = new GhostSkeleton(id, clip, label, brushKey, timeMap, skeleton) { Offset = offset };
            if (index >= 0) _ghosts[index] = ghost;
            else _ghosts.Add(ghost);
        }
        RefreshPose();
    }

    /// <summary>True when a ghost with this id is shown.</summary>
    public bool HasGhost(string id) => _ghosts.Exists(g => g.Id == id);

    /// <summary>True while the bind pose is shown (no clip, the Bind pose toggle, or <see cref="ForceBindPose"/>).</summary>
    public bool ShowingBindPose => Display.BindPose || _forceBindPose || EffectiveClip is null;

    private bool _forceBindPose;

    /// <summary>
    /// Shows this scene's bind pose whatever the clip and the shared Bind pose toggle say: a mesh tab sets it
    /// while a bone's bind is being edited with the gizmo (the gizmo and the skeleton must show the pose being
    /// edited). Only this scene changes; the toggle and the other tabs do not.
    /// </summary>
    public bool ForceBindPose
    {
        get => _forceBindPose;
        set
        {
            if (!Set(ref _forceBindPose, value)) return;
            Raise(nameof(HasMorph));
            RefreshPose();
            OverlayChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The LOD drawn (clamped per submesh to what it has).</summary>
    public int Lod
    {
        get => _lod;
        set
        {
            int clamped = Math.Clamp(value, 0, Math.Max(0, LodCount - 1));
            if (!Set(ref _lod, clamped)) return;
            MeshVersion++;
            MeshChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The most LODs any submesh has.</summary>
    public int LodCount => _mesh?.Submeshes.Select(s => s.Lods.Length).DefaultIfEmpty(0).Max() ?? 0;

    /// <summary>LOD choices for the toolbar ("LOD 0" …).</summary>
    public IReadOnlyList<string> LodChoices => [.. Enumerable.Range(0, Math.Max(1, LodCount)).Select(i => $"LOD {i}")];

    /// <summary>A notice drawn over the viewport (bone-count mismatch, loading, missing mesh), or null.</summary>
    public string? Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    /// <summary>True when <see cref="Notice"/> is a warning rather than information.</summary>
    public bool NoticeIsWarning
    {
        get => _noticeIsWarning;
        private set => Set(ref _noticeIsWarning, value);
    }

    /// <summary>The root bone's model-space path over the clip (one point per frame), or empty.</summary>
    public IReadOnlyList<Vector3> RootPath => _rootPath;

    /// <summary>Batches highlighted by the structure tree.</summary>
    public IReadOnlySet<BatchRef> HighlightBatches => _highlightBatches;

    /// <summary>The collision sphere highlighted, or -1.</summary>
    public int HighlightSphere => _highlightSphere;

    /// <summary>The prop point highlighted (index in <see cref="PropPoints"/>), or -1.</summary>
    public int HighlightProp => _highlightProp;

    /// <summary>The mesh-level prop points (the first non-empty LOD list; every stock LOD repeats it).</summary>
    public IReadOnlyList<V3dPropPoint> PropPoints { get; private set; } = [];

    /// <summary>True when the clip has morph data and the mesh can take it (the viewport morphs LOD 0).</summary>
    public bool HasMorph => EffectiveClip is { } morphClip && !Display.BindPose && !_forceBindPose && MorphSampler.HasMorph(morphClip)
        && _mesh?.Submeshes.FirstOrDefault()?.Lods.FirstOrDefault() is { } lod && lod.Batches.Length > 0;

    /// <summary>Raised when the mesh, the LOD or the texture resolver changes (geometry must be rebuilt).</summary>
    public event EventHandler? MeshChanged;

    /// <summary>Raised when the pose changes (vertices must be re-skinned).</summary>
    public event EventHandler? PoseChanged;

    /// <summary>Raised when only the overlay needs repainting (selection, highlights, toggles).</summary>
    public event EventHandler? OverlayChanged;

    /// <summary>Raised to ask the view to frame everything (false) or the selection (true).</summary>
    public event EventHandler<bool>? FrameRequested;

    /// <summary>Replaces the mesh (null clears it) and the resolver its textures come from.</summary>
    public void SetMesh(V3dFile? mesh, string? name, AssetResolver? textureResolver)
    {
        // A different mesh (not an edit of the same one) is framed afresh.
        if (!string.Equals(name, MeshName, StringComparison.OrdinalIgnoreCase)) Camera.HasBeenFramed = false;
        _mesh = mesh;
        MeshName = name;
        TextureResolver = textureResolver;
        Skeleton = mesh is null ? Skeleton.Empty : Skeleton.FromFile(mesh);
        Pose = Skeleton.Count > 0 ? new Pose(Skeleton) : null;
        PropPoints = MeshPropPoints(mesh);
        Selection.Clamp(Skeleton.Count);
        _lod = Math.Clamp(_lod, 0, Math.Max(0, LodCount - 1));
        MeshVersion++;
        RaiseAll(nameof(Mesh), nameof(MeshName), nameof(LodCount), nameof(LodChoices), nameof(Lod), nameof(Skeleton));
        RebuildRootPath();
        RefreshPose();
        MeshChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IReadOnlyList<V3dPropPoint> MeshPropPoints(V3dFile? mesh) =>
        mesh?.Submeshes.SelectMany(s => s.Lods).Select(l => l.PropPoints).FirstOrDefault(p => p.Length > 0) is { } props ? [.. props] : [];

    /// <summary>
    /// Swaps in an edited snapshot of the same mesh during a gizmo drag, without rebuilding the geometry: a
    /// changed bind re-skins (new skeleton, same bone count), spheres and prop points repaint. Anything else
    /// (another bone count) falls back to <see cref="SetMesh"/>. The document calls <see cref="SetMesh"/>
    /// when the drag ends.
    /// </summary>
    public void SetMeshLive(V3dFile mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var old = _mesh;
        if (old is null)
        {
            SetMesh(mesh, MeshName, TextureResolver);
            return;
        }
        _mesh = mesh;
        PropPoints = MeshPropPoints(mesh);
        if (!ReferenceEquals(old.BoneSection, mesh.BoneSection))
        {
            var skeleton = Skeleton.FromFile(mesh);
            if (skeleton.Count != Skeleton.Count)
            {
                SetMesh(mesh, MeshName, TextureResolver);
                return;
            }
            Skeleton = skeleton;
            Pose = skeleton.Count > 0 ? new Pose(skeleton) : null;
            Raise(nameof(Skeleton));
            RefreshPose();
        }
        Raise(nameof(Mesh));
        OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the clip and time the pose is sampled from.</summary>
    public void SetAnimation(RfaClip? clip, float time)
    {
        bool clipChanged = !ReferenceEquals(clip, _clip);
        _clip = clip;
        _time = time;
        if (clipChanged)
        {
            RebuildRootPath();
            Raise(nameof(HasMorph));
        }
        RefreshPose();
    }

    /// <summary>Re-evaluates the pose (time, toggles or the override changed).</summary>
    public void RefreshPose()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        if (Pose is { } pose)
        {
            var clip = EffectiveClip;
            if (ShowingBindPose || clip is null) pose.ResetToRest();
            else if (PoseOverride is not { } over || !over(pose, clip, _time)) pose.Sample(clip, _time);
        }
        foreach (var ghost in _ghosts) ghost.Update(Skeleton, _time);
        LastPoseMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        PoseAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        PoseVersion++;
        PoseChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows or clears the notice.</summary>
    public void SetNotice(string? text, bool warning = false)
    {
        Notice = string.IsNullOrWhiteSpace(text) ? null : text;
        NoticeIsWarning = warning;
    }

    /// <summary>Highlights batches, a sphere or a prop point (the structure tree's selection).</summary>
    public void SetHighlight(IEnumerable<BatchRef>? batches, int sphere = -1, int prop = -1)
    {
        _highlightBatches = batches is null ? new HashSet<BatchRef>() : new HashSet<BatchRef>(batches);
        _highlightSphere = sphere;
        _highlightProp = prop;
        MeshVersion++;
        MeshChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks the view to frame everything or the selection (F).</summary>
    public void RequestFrame(bool selection) => FrameRequested?.Invoke(this, selection);

    private void RebuildRootPath()
    {
        _rootPath = [];
        if (EffectiveClip is { } clip && Skeleton.Count > 0)
        {
            int root = -1;
            for (int i = 0; i < Skeleton.Count; i++)
            {
                if (Skeleton.EffectiveParents[i] < 0)
                {
                    root = i;
                    break;
                }
            }
            if (root >= 0 && root < clip.BoneCount && clip.EndTime > clip.StartTime)
            {
                var track = clip.Bones[root];
                var points = new List<Vector3>();
                for (int t = clip.StartTime; t <= clip.EndTime; t += RfaClip.TicksPerFrame / 2)
                    points.Add(ClipSampler.SamplePosition(track.PositionKeys.AsSpan(), t));
                points.Add(ClipSampler.SamplePosition(track.PositionKeys.AsSpan(), clip.EndTime));
                _rootPath = points;
            }
        }
        Raise(nameof(RootPath));
        OverlayChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// One dimmed skeleton drawn over the scene: a clip posed on the scene's skeleton at the scene's time
/// (optionally remapped, e.g. looped over a compared clip of another length). The pose buffers are
/// reused every frame.
/// </summary>
public sealed class GhostSkeleton
{
    private Pose? _pose;

    internal GhostSkeleton(string id, RfaClip clip, string label, string brushKey, Func<float, float>? timeMap, Skeleton? skeleton = null)
    {
        Id = id;
        Clip = clip;
        Label = label;
        BrushKey = brushKey;
        TimeMap = timeMap;
        OwnSkeleton = skeleton is { Count: > 0 } ? skeleton : null;
    }

    /// <summary>
    /// The ghost's own skeleton (the retarget dialog's SOURCE skeleton over the target mesh), or null to
    /// pose the clip on the scene's skeleton.
    /// </summary>
    public Skeleton? OwnSkeleton { get; }

    /// <summary>The skeleton the ghost is posed on and drawn with (its own, else the scene's at the last update).</summary>
    public Skeleton? Skeleton => _pose?.Skeleton;

    /// <summary>
    /// Model-space translation added when the ghost is drawn: the retarget source ghost is lifted or lowered
    /// so its ground lies on the target's (the hip-height result's <c>GroundMapping</c>).
    /// </summary>
    public Vector3 Offset { get; init; }

    /// <summary>The ghost's id ("saved", "compare").</summary>
    public string Id { get; }

    /// <summary>The clip it plays.</summary>
    public RfaClip Clip { get; }

    /// <summary>What it is, for the viewport legend ("Unedited", "Compare: ult2_run.rfa").</summary>
    public string Label { get; }

    /// <summary>The theme brush it is drawn with.</summary>
    public string BrushKey { get; }

    /// <summary>Maps the scene time (ticks) to the ghost clip's time; null = the same time.</summary>
    public Func<float, float>? TimeMap { get; }

    /// <summary>The current pose, or null before the first update or without a skeleton.</summary>
    public Pose? Pose => _pose;

    internal void Update(Skeleton sceneSkeleton, float time)
    {
        var skeleton = OwnSkeleton ?? sceneSkeleton;
        if (skeleton.Count == 0)
        {
            _pose = null;
            return;
        }
        if (_pose is null || !ReferenceEquals(_pose.Skeleton, skeleton)) _pose = new Pose(skeleton);
        float t = TimeMap?.Invoke(time) ?? time;
        _pose.Sample(Clip, t);
    }
}
