using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Playback;
using Cairn.Atx.Ui.Services;
using Cairn.Atx.Ui.ViewModels;

namespace Cairn.Atx.Ui.Preview;

/// <summary>
/// Read-only preview of an .atx file for other modules (the packfile preview pane): the frames composited
/// as the engine shows them (alpha mask, target format) by the module's <see cref="FrameCompositor"/>, played
/// by <see cref="AtxPlayback"/> (the engine's timing, Static included), with a play/pause button and a
/// readout. Parsing and compositing run off the UI thread; failures show a one-line message.
/// <see cref="Dispose"/> stops the render hook and cancels the composites still running.
/// </summary>
public sealed class AtxPreview : Grid, IDisposable
{
    /// <summary>Frames composited for a preview; longer sequences show their first frames only.</summary>
    internal const int MaxFrames = 64;

    private readonly CancellationTokenSource _cts = new();
    private readonly Func<AssetResolver?> _resolver;
    private readonly TextBlock _message;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Margin = new Thickness(10) };
    private readonly TextBlock _readout = new() { FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _play = new() { MinWidth = 32, Height = 26 };
    private readonly TextBlock _glyph = new() { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Text = "" };
    private AtxPlayback? _playback;
    private CompositeFrame?[] _frames = [];
    private int[] _frameTimes = [];
    private bool _hooked;
    private TimeSpan _lastTick;
    private bool _disposed;

    /// <param name="bytes">The whole file.</param>
    /// <param name="fileName">Its name (messages and TOML errors).</param>
    /// <param name="resolver">Resolves the frame and mask names (read when the frames load).</param>
    public AtxPreview(byte[] bytes, string fileName, Func<AssetResolver?> resolver)
    {
        _resolver = resolver;
        FileName = fileName;
        SetResourceReference(BackgroundProperty, "Preview.CheckerBrush");
        AutomationProperties.SetName(this, "Animated texture preview: " + fileName);
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(_image, "Composited frame");
        _message = new TextBlock { Text = "Loading " + fileName + "…", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12), Padding = new Thickness(8, 4, 8, 4) };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _message.SetResourceReference(TextBlock.BackgroundProperty, "App.PaneBackground");
        _readout.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _play.Content = _glyph;
        _play.SetResourceReference(StyleProperty, "ToolButton");
        _play.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        _play.Click += (_, _) => TogglePlay();
        AutomationProperties.SetName(_play, "Play or pause the preview");
        Children.Add(_message);
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
        Loading = LoadAsync(bytes, fileName, _cts.Token);
    }

    /// <summary>The previewed file's name.</summary>
    public string FileName { get; }
    /// <summary>Completes when the frames are composited (or failed), the message is shown, or the preview was disposed first.</summary>
    public Task Loading { get; }
    /// <summary>The message shown instead of the frames; null while a frame shows.</summary>
    public string? Message => _message.Parent is null ? null : _message.Text;
    /// <summary>Frames composited with a picture (diagnostics).</summary>
    public int FramesShown => _frames.Count(f => f?.Image is not null);
    /// <summary>The frame on screen, or -1.</summary>
    public int CurrentFrame => _playback?.CurrentFrame ?? -1;

    private sealed record Parsed(AtxModel Model, CompositeRequest[] Requests, int[] FrameTimes, PlaybackSpec Spec, int TotalFrames);

    private async Task LoadAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        using var busy = BusyTracker.Begin("animated texture preview");
        Parsed parsed;
        try
        {
            parsed = await Task.Run(() => Parse(bytes, fileName), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed) _message.Text = $"Cannot preview {fileName}: {ex.Message}";
            return;
        }
        if (_disposed) return;
        var resolver = _resolver();
        if (parsed.Requests.Length == 0 || resolver is null)
        {
            _message.Text = parsed.Requests.Length == 0 ? $"{fileName} has no frames." : "No game data to find the frames in.";
            return;
        }
        _frameTimes = parsed.FrameTimes;
        var frames = _frames = new CompositeFrame?[parsed.Requests.Length];
        _playback = new AtxPlayback(parsed.Spec);
        _playback.Pause();
        // one compositor per preview: nothing is added to the module's cache
        var compositor = new FrameCompositor();
        var loads = parsed.Requests.Select((r, i) => LoadFrameAsync(compositor, resolver, r, frames, i, ct)).ToArray();
        try { await Task.WhenAll(loads); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed) _message.Text = $"Cannot preview {fileName}: {ex.Message}";
            return;
        }
        if (_disposed) return;
        ShowFrames(parsed);
    }

    private static Parsed Parse(byte[] bytes, string fileName)
    {
        var (text, _) = Cairn.Atx.Workspace.AtxTextFiles.Decode(bytes);
        var result = AtxParser.Parse(text, fileName);
        var model = result.Model ?? throw new FormatException(result.Diagnostics.FirstOrDefault()?.Message ?? "the file is not valid TOML");
        var format = PreviewViewModel.ResolveEffectiveFormat(model);
        string? mask = model.Header.EffectiveAlphaMask;
        int count = Math.Min(model.Frames.Count, MaxFrames);
        var requests = new CompositeRequest[count];
        var times = new int[count];
        for (int i = 0; i < count; i++)
        {
            requests[i] = new CompositeRequest(model.Frames[i].EffectiveFile ?? string.Empty, mask, format, false, false);
            times[i] = model.FrameTimeMs(i);
        }
        var spec = PlaybackSpec.FromModel(model);
        if (spec.FrameCount > count) spec = spec with { FrameTimeOverridesMs = [.. spec.FrameTimeOverridesMs.Take(count)] };
        return new Parsed(model, requests, times, spec, model.Frames.Count);
    }

    private static async Task LoadFrameAsync(FrameCompositor compositor, AssetResolver resolver, CompositeRequest request, CompositeFrame?[] frames, int index, CancellationToken ct)
    {
        frames[index] = string.IsNullOrEmpty(request.FrameName)
            ? new CompositeFrame(null, $"Frame {index} has no image file.")
            : await compositor.GetAsync(resolver, request, ct);
    }

    private void ShowFrames(Parsed parsed)
    {
        Children.Remove(_message);
        Children.Add(_image);
        var bar = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8) };
        DockPanel.SetDock(_play, Dock.Left);
        bar.Children.Add(_play);
        var readoutBack = new Border { Child = _readout, Padding = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        readoutBack.SetResourceReference(Border.BackgroundProperty, "App.PaneBackground");
        bar.Children.Add(readoutBack);
        Children.Add(bar);
        // the message stays available for frames that cannot be shown (missing files)
        _message.VerticalAlignment = VerticalAlignment.Top;
        bool animates = parsed.Spec.Mode != Cairn.Atx.Schema.AtxAnimationMode.Static && _frames.Length > 1;
        _play.IsEnabled = animates;
        _play.ToolTip = animates ? "Pause" : "Static: the game changes this texture's frame only when a level event asks";
        _note = parsed.TotalFrames > _frames.Length ? $" (first {_frames.Length} of {parsed.TotalFrames} frames)" : animates ? string.Empty : " · static";
        ShowCurrent();
        if (animates) { _playback!.Play(); UpdatePlayButton(); Hook(); }
    }

    private string _note = string.Empty;

    private void ShowCurrent()
    {
        if (_playback is null || _frames.Length == 0) return;
        int index = Math.Clamp(_playback.CurrentFrame, 0, _frames.Length - 1);
        var frame = _frames[index];
        _image.Source = frame?.Image;
        string error = frame?.Image is null ? frame?.Error ?? $"Frame {index} could not be shown." : string.Empty;
        if (error.Length > 0)
        {
            _message.Text = error;
            if (_message.Parent is null) Children.Add(_message);
        }
        else if (_message.Parent is not null) Children.Remove(_message);
        string size = frame?.Image is { } img ? $" · {img.PixelWidth}×{img.PixelHeight}" : string.Empty;
        _readout.Text = $"Frame {index + 1} / {_frames.Length} · {_frameTimes[index]} ms{size}{_note}";
    }

    private void TogglePlay()
    {
        if (_playback is null) return;
        if (_playback.Playing) _playback.Pause();
        else
        {
            if (_playback.Spec.Mode == Cairn.Atx.Schema.AtxAnimationMode.PlayOnce && _playback.CurrentFrame >= _frames.Length - 1) _playback.SetFrame(0);
            _playback.Play();
        }
        UpdatePlayButton();
        Hook();
    }

    private void UpdatePlayButton()
    {
        bool playing = _playback?.Playing == true;
        _glyph.Text = playing ? "" : "";
        if (_play.IsEnabled) _play.ToolTip = playing ? "Pause" : "Play";
    }

    // ── Render hook: only while loaded and playing ───────────────────────────

    private void Hook()
    {
        if (_hooked || _disposed || !IsLoaded || _playback is not { Playing: true }) return;
        _hooked = true;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_playback is not { Playing: true } playback) { Unhook(); UpdatePlayButton(); return; }
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (now == _lastTick) return;
        double delta = _lastTick == TimeSpan.Zero ? 0 : (now - _lastTick).TotalSeconds;
        _lastTick = now;
        // a long stall must not fast-forward the animation
        if (delta > 0 && playback.Advance(Math.Min(delta, 0.25))) ShowCurrent();
        if (!playback.Playing) { Unhook(); UpdatePlayButton(); }
    }

    /// <summary>Stops the render hook and cancels composites still running.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        Unhook();
        _playback?.Pause();
        _image.Source = null;
        _frames = [];
    }
}
