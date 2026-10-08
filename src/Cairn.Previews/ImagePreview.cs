using System.Buffers.Binary;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Formats.Imaging;

namespace Cairn.Previews;

/// <summary>Decoded image data for <see cref="ImagePreview"/>: one or more frames (VBM animation) and the DDS mip count.</summary>
public sealed class ImageData
{
    /// <summary>Most frames decoded for an animated VBM, and the most pixel bytes they may take together.</summary>
    private const int MaxFrames = 512;
    private const long MaxAnimationBytes = 256L << 20;

    private ImageData(string name, byte[] bytes, IReadOnlyList<BgraImage> frames, int fps, int mips, string container)
    {
        Name = name;
        Bytes = bytes;
        Frames = frames;
        Fps = fps;
        Mips = mips;
        Container = container;
    }

    public string Name { get; }
    public byte[] Bytes { get; }
    public IReadOnlyList<BgraImage> Frames { get; }
    public int Fps { get; }
    public int Mips { get; }
    public string Container { get; }
    public bool HasAlpha { get; private set; }

    /// <summary>Decodes on the calling (pool) thread through the shared imaging code.</summary>
    /// <exception cref="ImageDecodeException">Not an image Cairn can read.</exception>
    public static ImageData Decode(byte[] bytes, string name, CancellationToken ct)
    {
        var container = ImageProbe.Detect(bytes, name);
        ImageData data;
        if (container == ImageContainer.Vbm)
        {
            var info = VbmCodec.ReadInfo(bytes, name);
            long perFrame = (long)info.Width * info.Height * 4;
            int count = (int)Math.Clamp(Math.Min(info.FrameCount, MaxAnimationBytes / Math.Max(1, perFrame)), 1, MaxFrames);
            var frames = new List<BgraImage>(count);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                frames.Add(VbmCodec.DecodeFrame(bytes, i, name));
            }
            data = new ImageData(name, bytes, frames, info.Fps, 1, "VBM");
        }
        else
        {
            var image = ImageDecoder.Decode(bytes, name);
            int mips = 1;
            if (container == ImageContainer.Dds)
            {
                try { mips = Math.Max(1, DdsCodec.Probe(bytes, name).MipLevels ?? 1); }
                catch (ImageDecodeException) { mips = 1; }
            }
            data = new ImageData(name, bytes, [image], 0, mips, container.ToString().ToUpperInvariant());
        }
        data.HasAlpha = data.Frames.Any(HasTransparency);
        return data;
    }

    /// <summary>
    /// Image data from frames decoded elsewhere (for example a texture of a PlayStation 2 texture pack): played at
    /// <paramref name="fps"/> when there are several, with <paramref name="container"/> shown beside the size.
    /// </summary>
    public static ImageData FromFrames(string name, IReadOnlyList<BgraImage> frames, int fps, string container)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0) throw new ArgumentException("At least one frame is needed.", nameof(frames));
        var data = new ImageData(name, [], frames, fps, 1, container);
        data.HasAlpha = frames.Any(HasTransparency);
        return data;
    }

    private static bool HasTransparency(BgraImage image)
    {
        var p = image.Pixels;
        for (int i = 3; i < p.Length; i += 4) if (p[i] != 255) return true;
        return false;
    }

    /// <summary>
    /// Decodes mip <paramref name="level"/> of a DDS by handing the shared decoder a header for that level's
    /// size followed by that level's data (the levels are stored largest first, back to back).
    /// </summary>
    public BgraImage DecodeMip(int level)
    {
        if (level <= 0) return Frames[0];
        var info = DdsCodec.Probe(Bytes, Name);
        bool compressed = EngineFormats.IsCompressed(info.Format);
        int blockBytes = info.Format == EngineFormat.Dxt1 ? 8 : 16;
        int bitCount = Bytes.Length >= 92 ? BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(88)) : 32;
        long offset = 128;
        int w = info.Width, h = info.Height;
        for (int i = 0; i < level; i++)
        {
            offset += compressed ? (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes : (long)w * h * Math.Max(1, bitCount / 8);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        if (offset >= Bytes.Length) throw new ImageDecodeException($"'{Name}' ends before mip level {level}.");
        var copy = new byte[128 + (Bytes.Length - offset)];
        Bytes.AsSpan(0, 128).CopyTo(copy);
        Bytes.AsSpan((int)offset).CopyTo(copy.AsSpan(128));
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(12), h);
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(16), w);
        return DdsCodec.Decode(copy, Name);
    }

    /// <summary>A frozen bitmap of <paramref name="image"/>; with <paramref name="alphaOnly"/> the alpha channel as grey.</summary>
    public static BitmapSource ToBitmap(BgraImage image, bool alphaOnly)
    {
        byte[] pixels = image.Pixels;
        if (alphaOnly)
        {
            pixels = new byte[image.Pixels.Length];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = image.Pixels[i + 3];
                pixels[i] = a; pixels[i + 1] = a; pixels[i + 2] = a; pixels[i + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// Image preview: the picture fitted to the pane or zoomed (fit, 100 %, mouse wheel) over a checkerboard,
/// an alpha-only view, a mip level picker for DDS and playback for animated VBMs.
/// </summary>
public sealed class ImagePreview : UserControl, IDisposable
{
    private readonly ImageData _data;
    private readonly ScrollViewer _scroll;
    private readonly Border _canvas;
    private readonly Image _image;
    private readonly TextBlock _zoomText;
    private readonly TextBlock _infoText;
    private readonly ToggleButton _alphaToggle;
    private readonly ComboBox? _mipBox;
    private readonly Button? _playButton;
    private readonly TextBlock? _frameText;
    private readonly BitmapSource?[] _normal;
    private readonly BitmapSource?[] _alpha;
    private DispatcherTimer? _timer;
    private BgraImage _current;
    private BitmapSource? _mipBitmap;
    private bool _fit = true;
    private double _scale = 1;
    private int _frame;
    private bool _playing;
    private bool _disposed;
    private CancellationTokenSource? _mipCts;

    public ImagePreview(ImageData data)
    {
        _data = data;
        _current = data.Frames[0];
        _normal = new BitmapSource?[data.Frames.Count];
        _alpha = new BitmapSource?[data.Frames.Count];

        _image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        AutomationProperties.SetName(_image, data.Name);
        _canvas = new Border { Child = _image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _canvas.SetResourceReference(Border.BackgroundProperty, "Preview.CheckerBrush");
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
        };
        _scroll.SetResourceReference(BackgroundProperty, "Preview.Background");
        _scroll.SizeChanged += (_, _) => { if (_fit) ApplyScale(); };
        _scroll.PreviewMouseWheel += OnWheel;

        _zoomText = PreviewUi.Secondary("");
        _infoText = PreviewUi.Secondary("");
        _alphaToggle = PreviewUi.Toggle("Alpha", "Show the alpha channel only (white = opaque)", (_, _) => ShowFrame());
        _alphaToggle.IsEnabled = data.HasAlpha;
        if (!data.HasAlpha) _alphaToggle.ToolTip = "This image has no transparency";

        var tools = new List<UIElement>
        {
            PreviewUi.Button("Fit", "Fit the image to the pane", (_, _) => { _fit = true; ApplyScale(); }),
            PreviewUi.Button("100%", "Show the image at its own size (one image pixel per screen pixel)", (_, _) => SetScale(1)),
            _zoomText,
            PreviewUi.Divider(),
            _alphaToggle,
        };
        if (data.Mips > 1)
        {
            _mipBox = new ComboBox { MinWidth = 120, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Mip level to show" };
            AutomationProperties.SetName(_mipBox, "Mip level");
            int w = _current.Width, h = _current.Height;
            for (int i = 0; i < data.Mips; i++)
            {
                _mipBox.Items.Add(string.Format(CultureInfo.CurrentCulture, "Mip {0}: {1} x {2}", i, w, h));
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            _mipBox.SelectedIndex = 0;
            _mipBox.SelectionChanged += (_, _) => _ = LoadMipAsync(_mipBox.SelectedIndex);
            tools.Add(PreviewUi.Divider());
            tools.Add(_mipBox);
        }
        if (data.Frames.Count > 1)
        {
            _playButton = PreviewUi.Button("Pause", "Play or pause the animation (Space)", (_, _) => SetPlaying(!_playing));
            _frameText = PreviewUi.Secondary("");
            tools.Add(PreviewUi.Divider());
            tools.Add(_playButton);
            tools.Add(PreviewUi.Button("<", "Previous frame", (_, _) => Step(-1)));
            tools.Add(PreviewUi.Button(">", "Next frame", (_, _) => Step(1)));
            tools.Add(_frameText);
        }
        tools.Add(_infoText);

        var dock = new DockPanel();
        var bar = PreviewUi.Toolbar([.. tools]);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_scroll);
        Content = dock;
        Focusable = true;
        KeyDown += OnKey;
        Loaded += (_, _) => { if (_playing) StartTimer(); };
        Unloaded += (_, _) => _timer?.Stop();

        ShowFrame();
        if (data.Frames.Count > 1) SetPlaying(true);
    }

    /// <summary>The frame shown now (for tests).</summary>
    public int Frame => _frame;
    public bool IsPlaying => _playing;
    public double Scale => _scale;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _data.Frames.Count > 1) { SetPlaying(!_playing); e.Handled = true; }
        else if (e.Key is Key.Add or Key.OemPlus) { SetScale(_scale * 1.25); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { SetScale(_scale / 1.25); e.Handled = true; }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        SetScale(_scale * factor);
        e.Handled = true;
    }

    private void SetScale(double scale)
    {
        _fit = false;
        _scale = Math.Clamp(scale, 0.02, 64);
        ApplyScale();
    }

    private void ApplyScale()
    {
        double w = _image.Source?.Width ?? _current.Width, h = _image.Source?.Height ?? _current.Height;
        if (_fit)
        {
            double aw = Math.Max(1, _scroll.ActualWidth - 8), ah = Math.Max(1, _scroll.ActualHeight - 8);
            _scale = Math.Max(0.01, Math.Min(aw / w, ah / h));
        }
        _canvas.Width = Math.Max(1, Math.Round(w * _scale));
        _canvas.Height = Math.Max(1, Math.Round(h * _scale));
        RenderOptions.SetBitmapScalingMode(_image, _scale >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _zoomText.Text = string.Format(CultureInfo.CurrentCulture, "{0:0}%{1}", _scale * 100, _fit ? " (fit)" : "");
    }

    private void ShowFrame()
    {
        bool alphaOnly = _alphaToggle.IsChecked == true;
        BitmapSource source;
        if (_mipBitmap is not null && !alphaOnly) source = _mipBitmap;
        else if (_mipBitmap is not null) source = ImageData.ToBitmap(_current, true);
        else
        {
            var cache = alphaOnly ? _alpha : _normal;
            source = cache[_frame] ??= ImageData.ToBitmap(_data.Frames[_frame], alphaOnly);
        }
        _image.Source = source;
        _infoText.Text = string.Format(CultureInfo.CurrentCulture, "{0} x {1} {2}", source.PixelWidth, source.PixelHeight, _data.Container);
        if (_frameText is not null)
            _frameText.Text = string.Format(CultureInfo.CurrentCulture, "Frame {0} / {1}{2}", _frame + 1, _data.Frames.Count,
                _data.Fps > 0 ? string.Format(CultureInfo.CurrentCulture, " at {0} fps", _data.Fps) : "");
        ApplyScale();
    }

    private async Task LoadMipAsync(int level)
    {
        _mipCts?.Cancel();
        var cts = _mipCts = new CancellationTokenSource();
        try
        {
            var image = await Task.Run(() => _data.DecodeMip(level), cts.Token);
            if (cts.IsCancellationRequested || _disposed) return;
            _current = image;
            _mipBitmap = level == 0 ? null : ImageData.ToBitmap(image, false);
            ShowFrame();
        }
        catch (OperationCanceledException) { }
        catch (ImageDecodeException ex)
        {
            if (!_disposed) _infoText.Text = ex.Message;
        }
    }

    private void Step(int delta)
    {
        SetPlaying(false);
        _frame = ((_frame + delta) % _data.Frames.Count + _data.Frames.Count) % _data.Frames.Count;
        ShowFrame();
    }

    private void SetPlaying(bool playing)
    {
        if (_data.Frames.Count < 2 || _disposed) return;
        _playing = playing;
        if (_playButton is not null) _playButton.Content = playing ? "Pause" : "Play";
        if (playing) StartTimer();
        else _timer?.Stop();
    }

    private void StartTimer()
    {
        if (_disposed) return;
        if (_timer is null)
        {
            int fps = _data.Fps is > 0 and <= 120 ? _data.Fps : 15;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / fps) };
            _timer.Tick += OnTick;
        }
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) { _timer?.Stop(); return; }
        _frame = (_frame + 1) % _data.Frames.Count;
        ShowFrame();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mipCts?.Cancel();
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        _image.Source = null;
    }
}
