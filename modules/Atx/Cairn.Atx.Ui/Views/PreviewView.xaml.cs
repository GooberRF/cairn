using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Atx.Ui.Services;
using Cairn.Atx.Ui.ViewModels;

namespace Cairn.Atx.Ui.Views;

/// <summary>
/// The preview pane's plumbing. Everything that can be expressed as a binding is in the XAML; this
/// file owns the four things that cannot be:
///
/// <list type="bullet">
/// <item>the render loop — <c>CompositionTarget.Rendering</c> with a <see cref="Stopwatch"/> delta,
/// attached only while the player is actually playing and the pane is actually on screen, so a
/// background tab or a minimised window costs nothing;</item>
/// <item>zoom arithmetic — Fit has to know the viewport, and the scaling mode flips to
/// nearest-neighbour above 100 % so a magnified texel stays a square;</item>
/// <item>tiling — three by three is nine <see cref="Image"/> cells sharing one frozen bitmap;</item>
/// <item>wheel zoom and drag panning.</item>
/// </list>
/// </summary>
public partial class PreviewView : UserControl
{
    private static readonly (string Label, double Value)[] ZoomChoices =
    [
        ("Fit", 0), ("50 %", 50), ("75 %", 75), ("100 %", 100),
        ("150 %", 150), ("200 %", 200), ("400 %", 400), ("800 %", 800),
    ];

    private readonly Stopwatch _clock = new();
    private TimeSpan _lastTick;
    private bool _rendering;
    private bool _syncing;

    private DocumentViewModel? _document;
    private PreviewViewModel? _preview;
    private Window? _window;
    private ThemeService? _theme;

    private Point _panOrigin;
    private Vector _panOffset;
    private bool _panning;

    public PreviewView()
    {
        InitializeComponent();
        BuildCombos();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => UpdateActive();
        SizeChanged += (_, _) => UpdateLayoutScale();
        // The pane can also change size without the control doing so — a splitter drag, or the
        // problems panel opening — and Fit has to follow it.
        Viewport.SizeChanged += (_, _) => UpdateLayoutScale();

        Scrubber.SeekRequested += (_, index) => _preview?.Seek(index, pause: false);
        Viewport.PreviewMouseWheel += OnViewportWheel;
        Viewport.PreviewMouseLeftButtonDown += OnViewportMouseDown;
        Viewport.PreviewMouseMove += OnViewportMouseMove;
        Viewport.PreviewMouseLeftButtonUp += OnViewportMouseUp;
        PlayerHost.PreviewMouseDown += (_, _) => Focus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_window is null)
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.StateChanged += OnWindowStateChanged;
        }
        if (_theme is null && AtxModule.Workspace is { } app)
        {
            _theme = app.Theme;
            _theme.ThemeChanged += OnThemeChanged;
        }
        UpdateActive();
        UpdateVisual();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _preview?.OnSettingsChanged();
        Scrubber.RefreshTheme();
        UpdateVisual();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateActive();

    // ── Document wiring ───────────────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_preview is not null)
        {
            _preview.PropertyChanged -= OnPreviewPropertyChanged;
            _preview.PlaybackStateChanged -= OnPlaybackStateChanged;
            _preview.IsActive = false;
        }

        _document = e.NewValue as DocumentViewModel;
        _preview = _document?.Preview;

        if (_preview is not null)
        {
            _preview.PropertyChanged += OnPreviewPropertyChanged;
            _preview.PlaybackStateChanged += OnPlaybackStateChanged;
            SyncCombos();
        }
        UpdateActive();
        UpdateVisual();
    }

    private void Detach()
    {
        StopRendering();
        if (_preview is not null) _preview.IsActive = false;
        if (_window is not null) _window.StateChanged -= OnWindowStateChanged;
        _window = null;
        if (_theme is not null) _theme.ThemeChanged -= OnThemeChanged;
        _theme = null;
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PreviewViewModel.CurrentImage):
            case nameof(PreviewViewModel.Tile):
                UpdateVisual();
                break;
            case nameof(PreviewViewModel.Zoom):
                SyncCombos();
                UpdateLayoutScale();
                break;
            case nameof(PreviewViewModel.Speed):
            case nameof(PreviewViewModel.IsFollowFile):
                SyncCombos();
                break;
        }
    }

    // ── The render loop ───────────────────────────────────────────────────────

    private void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        if (_preview is { IsPlaying: true, IsActive: true }) StartRendering();
        else StopRendering();
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _clock.Restart();
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        double delta = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        _preview?.Tick(delta);
    }

    /// <summary>
    /// A hidden pane or a minimised window must not animate: the design's quality bar says no work
    /// the user cannot see, and a render hook that runs for every open tab would do exactly that.
    /// </summary>
    private void UpdateActive()
    {
        if (_preview is null) { StopRendering(); return; }
        bool active = IsVisible && _window?.WindowState != WindowState.Minimized;
        _preview.IsActive = active;
        if (!active) StopRendering();
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Space && _preview is not null)
        {
            _preview.PlayPauseCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // ── Combos ────────────────────────────────────────────────────────────────

    private void BuildCombos()
    {
        foreach (var (label, _) in ZoomChoices) ZoomBox.Items.Add(label);
        foreach (double speed in PreviewViewModel.SpeedOptions)
        {
            SpeedBox.Items.Add(speed.ToString("0.##", CultureInfo.CurrentCulture) + "×");
        }
        ModeBox.Items.Add("Follow file");
        ModeBox.Items.Add("Force loop");
        _syncing = true;
        ZoomBox.SelectedIndex = 0;
        SpeedBox.SelectedIndex = 2;
        ModeBox.SelectedIndex = 0;
        _syncing = false;
    }

    private void SyncCombos()
    {
        if (_preview is null) return;
        _syncing = true;
        try
        {
            int zoom = Array.FindIndex(ZoomChoices, c => Math.Abs(c.Value - _preview.Zoom) < 0.5);
            ZoomBox.SelectedIndex = zoom >= 0 ? zoom : 0;

            int speed = 0;
            for (int i = 0; i < PreviewViewModel.SpeedOptions.Count; i++)
            {
                if (Math.Abs(PreviewViewModel.SpeedOptions[i] - _preview.Speed) < 0.001) speed = i;
            }
            SpeedBox.SelectedIndex = speed;
            ModeBox.SelectedIndex = _preview.IsForceLoop ? 1 : 0;
        }
        finally { _syncing = false; }
    }

    private void OnZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _preview is null) return;
        int index = ZoomBox.SelectedIndex;
        if (index >= 0 && index < ZoomChoices.Length) _preview.Zoom = ZoomChoices[index].Value;
        UpdateLayoutScale();
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _preview is null) return;
        int index = SpeedBox.SelectedIndex;
        if (index >= 0 && index < PreviewViewModel.SpeedOptions.Count)
            _preview.Speed = PreviewViewModel.SpeedOptions[index];
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _preview is null) return;
        _preview.ModeOverride = ModeBox.SelectedIndex == 1
            ? PreviewModeOverride.ForceLoop
            : PreviewModeOverride.FollowFile;
    }

    // ── Canvas ────────────────────────────────────────────────────────────────

    /// <summary>Re-points every tile at the current bitmap and repaints the background.</summary>
    private void UpdateVisual()
    {
        if (_preview is null)
        {
            FrameImage.Source = null;
            return;
        }

        Surface.Background = _preview.BackgroundBrush;

        bool tile = _preview.Tile;
        int cells = tile ? 9 : 1;
        Tiles.Rows = tile ? 3 : 1;
        Tiles.Columns = tile ? 3 : 1;
        while (Tiles.Children.Count > cells) Tiles.Children.RemoveAt(Tiles.Children.Count - 1);
        while (Tiles.Children.Count < cells)
        {
            Tiles.Children.Add(new Image { Stretch = Stretch.Fill, Focusable = false });
        }

        foreach (var child in Tiles.Children.OfType<Image>()) child.Source = _preview.CurrentImage;
        AutomationProperties.SetName(
            FrameImage, tile ? "Composited frame, tiled three by three" : "Composited frame");
        UpdateLayoutScale();
    }

    /// <summary>
    /// Sizes the surface for the chosen zoom (or works out what Fit means right now) and picks the
    /// scaling mode: nearest-neighbour when magnifying, so texels stay square, and the high-quality
    /// filter when shrinking, so a 512 px texture in a 200 px pane is still legible.
    /// </summary>
    private void UpdateLayoutScale()
    {
        if (_preview is null || !_preview.HasImage) return;
        int width = _preview.ImageWidth;
        int height = _preview.ImageHeight;
        if (width <= 0 || height <= 0) return;

        int repeat = _preview.Tile ? 3 : 1;
        double contentWidth = width * repeat;
        double contentHeight = height * repeat;

        double scale;
        if (_preview.Zoom > 0)
        {
            scale = _preview.Zoom / 100.0;
        }
        else
        {
            // ActualWidth/Height, not ViewportWidth/Height: the ScrollViewer's viewport properties
            // are only refreshed part-way through the layout pass, so reading them from a
            // SizeChanged handler gives the previous pass's numbers — which is how a 64 px texture
            // used to settle at 75 px in a pane with room for four times that and never recover.
            // Stage margin (10 each side) plus the surface's 1 px border.
            double availableWidth = Math.Max(16, Viewport.ActualWidth - 22);
            double availableHeight = Math.Max(16, Viewport.ActualHeight - 22);
            scale = Math.Min(availableWidth / contentWidth, availableHeight / contentHeight);
            scale = Math.Clamp(scale, 0.05, 8.0);
            // Above 100 % the image is drawn nearest-neighbour, and a fractional factor would make
            // some texels a pixel wider than their neighbours. Whole multiples keep them square,
            // which is the whole point of magnifying a 64 px texture.
            if (scale > 1) scale = Math.Floor(scale);
            _preview.CurrentFitPercent = scale * 100;
        }

        Surface.Width = Math.Max(1, Math.Round(contentWidth * scale));
        Surface.Height = Math.Max(1, Math.Round(contentHeight * scale));

        var mode = scale > 1.001 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        foreach (var child in Tiles.Children.OfType<Image>())
        {
            RenderOptions.SetBitmapScalingMode(child, mode);
        }
    }

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        if (_preview is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        _preview.AdjustZoom(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Viewport.ScrollableWidth <= 0 && Viewport.ScrollableHeight <= 0) return;
        _panning = true;
        _panOrigin = e.GetPosition(Viewport);
        _panOffset = new Vector(Viewport.HorizontalOffset, Viewport.VerticalOffset);
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var delta = e.GetPosition(Viewport) - _panOrigin;
        Viewport.ScrollToHorizontalOffset(_panOffset.X - delta.X);
        Viewport.ScrollToVerticalOffset(_panOffset.Y - delta.Y);
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Arrow;
    }
}
