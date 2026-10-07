using System.Numerics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Cairn.Assets;
using Cairn.Ui.Services;
using Cairn.Viewport;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Viewport;

namespace Cairn.Vfx.Ui.Preview;

/// <summary>One effect playing in a loop for a preview: sampler and simulators, no document, history or selection.</summary>
internal sealed class VfxPreviewScene : IVfxScene
{
    public VfxPreviewScene(VfxFile file)
    {
        Sampler = new VfxSampler(file);
        int end = file.EndFrame;
        if (end <= 0)
            foreach (var m in Sampler.Meshes) end = Math.Max(end, (int)MathF.Ceiling(m.StartSeconds * VfxTime.FramesPerSecond) + m.FrameCount);
        EndFrame = Math.Max(1, end);
        Simulators = [.. Enumerable.Range(0, Sampler.ParticleSystems.Count).Select(i => new VfxParticleSimulator(Sampler, i, 1, VfxPlaybackMode.Loop))];
        Seek(0);
    }

    public VfxSampler Sampler { get; }
    public VfxParticleSimulator[] Simulators { get; }
    public IReadOnlySet<int> HiddenSections { get; } = new HashSet<int>();
    public int EndFrame { get; }
    public VfxPlaybackState State { get; private set; }
    public float TimelineFrame { get; private set; }
    /// <summary>The time shown, in ticks.</summary>
    public double Ticks { get; private set; }

    public void Seek(double ticks)
    {
        Ticks = ticks;
        TimelineFrame = (float)(ticks / VfxDocument.VfxTimeBase.TicksPerFrame);
        State = VfxPlayback.Evaluate(VfxPlaybackMode.Loop, ticks / VfxDocument.VfxTimeBase.TicksPerSecond, EndFrame);
    }
}

/// <summary>The effect renderer on a plain orbit viewport: no overlay, selection, gizmo or editing input.</summary>
internal sealed class VfxPreviewViewport : ViewportSurface
{
    private readonly VfxPreviewScene _scene;

    public VfxPreviewViewport(VfxPreviewScene scene, TextureService textures)
    {
        _scene = scene;
        Renderer = new VfxSceneRenderer(scene, textures);
        Models.Add(Renderer.Root);
        Renderer.TexturesChanged += (_, _) => Invalidate();
        Render += (_, _) => Renderer.Update(Camera);
        FrameRequested += (_, _) => FrameAll();
        Loaded += (_, _) => { if (!Camera.HasBeenFramed) FrameAll(); };
        AutomationProperties.SetName(this, "Effect preview");
        ToolTip = "Left-drag: orbit · Middle or Shift+left-drag: pan · Wheel: zoom · F: frame";
    }

    public VfxSceneRenderer Renderer { get; }

    /// <summary>Frames the meshes over a few sampled frames, as the document viewport does.</summary>
    public void FrameAll()
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        double keep = _scene.Ticks;
        foreach (float f in new[] { 0f, 0.25f, 0.5f, 0.75f })
        {
            _scene.Seek(f * _scene.EndFrame * VfxDocument.VfxTimeBase.TicksPerFrame);
            Renderer.Update(Camera);
            if (Renderer.TryGetBounds(-1, out var a, out var b)) { min = Vector3.Min(min, a); max = Vector3.Max(max, b); }
        }
        _scene.Seek(keep);
        var s = _scene.Sampler;
        for (int i = 0; i < s.Dummies.Count; i++) { var p = s.SampleDummy(i, 0).Position; min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        if (min.X > max.X) { min = new(-1); max = new(1); }
        var pad = Vector3.Max(max - min, new Vector3(0.2f)) * 0.05f;
        Camera.FrameBox(min - pad, max + pad, ActualWidth > 0 ? ActualWidth / Math.Max(1, ActualHeight) : 1.6);
        Camera.HasBeenFramed = true;
        Camera.NotifyChanged();
        Invalidate();
    }
}

/// <summary>
/// Read-only preview of a .vfx file for other modules (the packfile preview pane): the effect loops in an
/// orbit viewport with a play/pause button. Parsing runs off the UI thread; a file that cannot be read shows
/// a one-line message. <see cref="Dispose"/> stops playback and detaches the viewport from the render loop.
/// </summary>
public sealed class VfxPreview : Grid, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<AssetResolver?> _resolver;
    private readonly TextBlock _message;
    private PlaybackViewModel? _playback;
    private VfxPreviewScene? _scene;
    private VfxPreviewViewport? _viewport;
    private bool _disposed;

    /// <param name="bytes">The whole file.</param>
    /// <param name="fileName">Its name (for messages and the reader).</param>
    /// <param name="resolver">Resolves texture names (read when the textures load).</param>
    public VfxPreview(byte[] bytes, string fileName, Func<AssetResolver?> resolver)
    {
        _resolver = resolver;
        FileName = fileName;
        SetResourceReference(BackgroundProperty, "Viewport.Background");
        AutomationProperties.SetName(this, "Effect preview: " + fileName);
        _message = new TextBlock { Text = "Loading " + fileName + "…", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        Children.Add(_message);
        Loaded += (_, _) => _playback?.Play();
        Unloaded += (_, _) => _playback?.Pause();
        Loading = LoadAsync(bytes, fileName, _cts.Token);
    }

    /// <summary>The previewed file's name.</summary>
    public string FileName { get; }
    /// <summary>Completes when the effect is shown, the message is shown or the preview was disposed first.</summary>
    public Task Loading { get; }
    /// <summary>The message shown instead of the effect (loading or why it cannot be shown); null once the effect shows.</summary>
    public string? Message => _message.Parent is null ? null : _message.Text;
    /// <summary>The renderer once the effect is shown (diagnostics).</summary>
    public VfxSceneRenderer? Renderer => _viewport?.Renderer;
    /// <summary>The transport once the effect is shown.</summary>
    public PlaybackViewModel? Playback => _playback;

    private async Task LoadAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        using var busy = BusyTracker.Begin("effect preview");
        VfxPreviewScene scene;
        try
        {
            scene = await Task.Run(() => new VfxPreviewScene(VfxReader.Read(bytes, fileName)), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed) _message.Text = $"Cannot preview {fileName}: {ex.Message}";
            return;
        }
        if (_disposed) return;
        try { Show(scene); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Clear();
            _message.Text = $"Cannot preview {fileName}: {ex.Message}";
            if (_message.Parent is null) Children.Add(_message);
        }
    }

    private void Show(VfxPreviewScene scene)
    {
        _scene = scene;
        var tb = VfxDocument.VfxTimeBase;
        _playback = new PlaybackViewModel(() => TimeUnit.Frames, tb) { Loop = true };
        _playback.SetClip(new PlaybackRange(0, (int)(scene.EndFrame * tb.TicksPerFrame), 0, 0, Array.Empty<int>()));
        _playback.Seek(0);
        _playback.TimeChanged += OnTimeChanged;
        _viewport = new VfxPreviewViewport(scene, new TextureService(_resolver));
        Children.Remove(_message);
        Children.Add(_viewport);
        Children.Add(PlayButton(_playback));
        if (IsLoaded) _playback.Play();
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        if (_playback is null || _scene is null || _viewport is null) return;
        _scene.Seek(_playback.Time);
        _viewport.Invalidate();
    }

    /// <summary>A round play/pause button over the bottom-left corner of the viewport.</summary>
    internal static Button PlayButton(PlaybackViewModel playback)
    {
        var button = new Button
        {
            Command = playback.PlayPauseCommand,
            MinWidth = 32, Height = 26, Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
        };
        button.SetResourceReference(StyleProperty, "ToolButton");
        button.SetResourceReference(BackgroundProperty, "App.ChromeBackground");
        var glyph = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 };
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(PlaybackViewModel.PlayGlyph)) { Source = playback });
        button.Content = glyph;
        button.SetBinding(ToolTipProperty, new Binding(nameof(PlaybackViewModel.IsPlaying)) { Source = playback, Converter = PlayTip.Instance });
        AutomationProperties.SetName(button, "Play or pause the preview");
        return button;
    }

    private sealed class PlayTip : IValueConverter
    {
        public static readonly PlayTip Instance = new();
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? "Pause" : "Play";
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    private void Clear()
    {
        if (_playback is { } p) { p.Pause(); p.TimeChanged -= OnTimeChanged; }
        _playback = null;
        _scene = null;
        _viewport = null;
        Children.Clear(); // the viewport unhooks from the render loop when it unloads
    }

    /// <summary>Stops playback, cancels a load still running and detaches everything from the render loop.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        Clear();
    }
}
