using System.Numerics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Assets;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Viewport;
using Cairn.Vfx.Rfe;

namespace Cairn.Vfx.Ui.Rfe;

/// <summary>An orbit viewport showing an <see cref="RfeScene"/>.</summary>
public sealed class RfeViewport : ViewportSurface
{
    private readonly RfeScene _scene;
    private bool _framed;

    public RfeViewport(RfeScene scene, RfeTextureSource textures)
    {
        _scene = scene;
        Display = new Viewport.VfxDisplaySettings();
        Renderer = new RfeSceneRenderer(scene, textures);
        Models.Add(Renderer.Root);
        Renderer.TexturesChanged += (_, _) => Invalidate();
        Render += (_, _) => Renderer.Update(Camera);
        FrameRequested += (_, _) => FrameAll();
        // framed once laid out (the camera may already count as framed by the time this view first shows)
        Loaded += (_, _) =>
        {
            if (_framed) return;
            _framed = true;
            Dispatcher.BeginInvoke(FrameAll, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        AutomationProperties.SetName(this, "Red Faction II effect preview");
        ToolTip = "Left-drag: orbit · Middle or Shift+left-drag: pan · Wheel: zoom · F: frame";
    }

    public RfeSceneRenderer Renderer { get; }

    /// <summary>Frames the meshes and particles over a few sampled times (the bounding radius when nothing shows).</summary>
    public void FrameAll()
    {
        var points = new List<Vector3>();
        double keep = _scene.Elapsed;
        float duration = _scene.Effect.Duration;
        foreach (float f in new[] { 0.05f, 0.15f, 0.3f, 0.5f })
        {
            _scene.Seek(f * duration);
            Renderer.Update(Camera);
            Renderer.CollectPoints(points);
        }
        _scene.Seek(keep);
        Renderer.Update(Camera);
        // frame the bulk of it around its middle (the median point), so a few far-flung particles (debris falling with
        // gravity, sparks) do not shrink the effect to a dot; with meshes, the view is at most 1.5 bounding radii across
        float radius = _scene.Effect.Radius > 0 && float.IsFinite(_scene.Effect.Radius) ? _scene.Effect.Radius : 0;
        float r = radius;
        var centre = Vector3.Zero;
        if (points.Count > 0)
        {
            float Median(Func<Vector3, float> axis) => points.Select(axis).Order().ElementAt(points.Count / 2);
            centre = new Vector3(Median(p => p.X), Median(p => p.Y), Median(p => p.Z));
            var distances = points.Select(p => (p - centre).Length()).Order().ToList();
            float bulk = distances[(int)Math.Round(0.85 * (distances.Count - 1))];
            r = radius > 0 ? Math.Min(bulk, radius * 1.5f) : bulk;
        }
        r = Math.Clamp(r, 0.25f, 200);
        var min = centre - new Vector3(r); var max = centre + new Vector3(r);
        var pad = Vector3.Max(max - min, new Vector3(0.2f)) * 0.05f;
        Camera.FrameBox(min - pad, max + pad, ActualWidth > 0 ? ActualWidth / Math.Max(1, ActualHeight) : 1.6);
        Camera.HasBeenFramed = true;
        Camera.NotifyChanged();
        Invalidate();
    }
}

/// <summary>
/// Read-only preview of a Red Faction II effect for the packfile preview pane: it loops in an orbit viewport with a
/// play/pause button; textures come from the <c>.peg</c> packs beside it (and the install's). Parsing runs off the UI
/// thread; an effect that cannot be read (the old layout) shows a one-line message. Disposing stops playback.
/// </summary>
public sealed class RfePreview : Grid, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TextBlock _message;
    private readonly RfeLoopClock _clock = new();
    private PlaybackViewModel? _playback;
    private RfeScene? _scene;
    private RfeViewport? _viewport;
    private bool _disposed;

    /// <param name="bytes">The whole file.</param>
    /// <param name="fileName">Its name.</param>
    /// <param name="shell">The shell (to find the install of the packfile the effect is in), or null.</param>
    /// <param name="siblings">The files beside it, or null.</param>
    public RfePreview(byte[] bytes, string fileName, IShellContext? shell, IAssetSiblings? siblings)
    {
        FileName = fileName;
        SetResourceReference(BackgroundProperty, "Viewport.Background");
        AutomationProperties.SetName(this, "Effect preview: " + fileName);
        _message = new TextBlock { Text = "Loading " + fileName + "…", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        Children.Add(_message);
        Loaded += (_, _) => _playback?.Play();
        Unloaded += (_, _) => _playback?.Pause();
        var around = siblings is null ? RfeSurroundings.None : RfeSurroundings.For(shell, siblings);
        Loading = LoadAsync(bytes, fileName, around, _cts.Token);
    }

    /// <summary>The previewed file's name.</summary>
    public string FileName { get; }
    /// <summary>Completes when the effect or the message is shown, or the preview was disposed first.</summary>
    public Task Loading { get; }
    /// <summary>The message shown instead of the effect, or null once the effect shows.</summary>
    public string? Message => _message.Parent is null ? null : _message.Text;
    /// <summary>The renderer once the effect shows (diagnostics).</summary>
    public RfeSceneRenderer? Renderer => _viewport?.Renderer;
    /// <summary>The scene once the effect shows (diagnostics).</summary>
    public RfeScene? Scene => _scene;
    /// <summary>The transport once the effect shows.</summary>
    public PlaybackViewModel? Playback => _playback;
    /// <summary>The texture packs, once the effect shows.</summary>
    public RfeTextureSource? Textures { get; private set; }

    private async Task LoadAsync(byte[] bytes, string fileName, RfeSurroundings around, CancellationToken ct)
    {
        using var busy = BusyTracker.Begin("effect preview");
        RfeScene scene;
        RfeTextureSource textures;
        try
        {
            (scene, textures) = await Task.Run(() =>
            {
                var effect = RfeReader.Read(bytes, fileName);
                var t = new RfeTextureSource(effect, fileName, around);
                _ = t.Packs; // find the packs here, off the UI thread
                return (new RfeScene(effect, fileName), t);
            }, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed) _message.Text = $"Cannot preview {fileName}: {ex.Message}";
            return;
        }
        if (_disposed) return;
        _scene = scene;
        Textures = textures;
        _playback = RfeDocument.NewPlayback(scene.Effect.Duration);
        _playback.Loop = true;
        _playback.TimeChanged += OnTimeChanged;
        _viewport = new RfeViewport(scene, textures);
        Children.Remove(_message);
        Children.Add(_viewport);
        Children.Add(Preview.VfxPreview.PlayButton(_playback));
        if (IsLoaded) _playback.Play();
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        if (_playback is null || _scene is null || _viewport is null) return;
        // unwrapped across loops, as the tab does: the particles run on instead of restarting from nothing
        _scene.Seek(_clock.Elapsed(_playback, _scene.Effect.Duration));
        _viewport.Invalidate();
    }

    /// <summary>Stops playback, cancels a load still running and detaches everything from the render loop.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        if (_playback is { } p) { p.Pause(); p.TimeChanged -= OnTimeChanged; }
        _playback = null;
        _scene = null;
        _viewport = null;
        Children.Clear();
    }
}
