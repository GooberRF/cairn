using Cairn.Ui.Mvvm;
using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// What a <c>Viewport.ViewportControl</c> renders and drives: a scene, a transport, and the shared
/// display toggles. Every document is one; a dialog with its own embedded viewport (retarget, glTF
/// import) uses a <see cref="ViewportPreviewHost"/>.
/// </summary>
public interface IViewportHost
{
    /// <summary>What the viewport shows.</summary>
    SceneViewModel Scene { get; }

    /// <summary>The transport (Space, the arrow keys and the render loop's "is playing" use it).</summary>
    PlaybackViewModel Playback { get; }

    /// <summary>The shared display toggles (the viewport toolbar binds here).</summary>
    ViewportDisplaySettings Display { get; }
}

/// <summary>
/// A scene and transport that belong to a dialog, not to a document: a mesh, a clip playing on it, and
/// optionally ghost skeletons — for the retarget dialog's result preview, the glTF import previews,
/// and the like. Nothing here touches any document. Dispose it when the dialog closes (it pauses).
/// </summary>
public sealed class ViewportPreviewHost : ObservableObject, IViewportHost, IDisposable
{
    private RfaClip? _clip;
    private bool _disposed;

    public ViewportPreviewHost(RfaWorkspace shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        Shell = shell;
        Selection = new BoneSelection();
        Scene = new SceneViewModel(shell.Display, Selection, shell.Textures);
        Playback = new PlaybackViewModel(() => shell.TimeUnit, RfaTime.Base);
        Playback.UnitCycleRequested += (_, _) => shell.CycleTimeUnit();
        Playback.TimeChanged += (_, _) => Scene.SetAnimation(_clip, Playback.Time);
        Playback.Loop = true;
    }

    /// <summary>The shell (time unit, display, textures).</summary>
    public RfaWorkspace Shell { get; }

    /// <summary>The preview's own bone selection (clicking a joint selects it here, nowhere else).</summary>
    public BoneSelection Selection { get; }

    public SceneViewModel Scene { get; }

    public PlaybackViewModel Playback { get; }

    public ViewportDisplaySettings Display => Shell.Display;

    /// <summary>The clip playing, or null for the bind pose.</summary>
    public RfaClip? Clip => _clip;

    /// <summary>Shows <paramref name="mesh"/> (null clears it); textures resolve through <paramref name="textures"/>.</summary>
    public void SetMesh(V3dFile? mesh, string? name, AssetResolver? textures)
    {
        if (_disposed) return;
        Scene.SetMesh(mesh, name, textures);
        Scene.SetAnimation(_clip, Playback.Time);
        Raise(nameof(Scene));
    }

    /// <summary>
    /// Plays <paramref name="clip"/> (null: the bind pose). The playhead keeps its place when it still
    /// falls inside the new clip, so a recomputed preview does not jump back to the start.
    /// </summary>
    public void SetClip(RfaClip? clip)
    {
        if (_disposed) return;
        float time = Playback.Time;
        _clip = clip;
        Playback.SetClip(clip);
        if (clip is not null && time >= clip.StartTime && time <= clip.EndTime) Playback.Seek(time);
        Scene.SetAnimation(clip, Playback.Time);
        Raise(nameof(Clip));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Playback.Pause();
    }
}
