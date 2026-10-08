using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Formats;
using Cairn.Formats.Imaging;
using Cairn.Previews;
using Cairn.Ui.Services;
using Cairn.Vpp.Ps2;

namespace Cairn.Vpp.Ui.Preview;

/// <summary>
/// The preview of a PEG texture pack (PlayStation 2): its textures in a list (size, format, mips, frames; MPEG-2
/// backgrounds marked as not converted when decoding them is switched off) above the shared image preview of the
/// selected one, which plays an animated texture. The directory is read at once; textures are decoded off the UI
/// thread when selected.
/// </summary>
public sealed class PegPreview : UserControl, IDisposable
{
    private readonly byte[] _bytes;
    private readonly string _name;
    private readonly PegFile _pack;
    private readonly bool _decodeMpeg2;
    private readonly PegBlackKey? _blackKey;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Focusable = true };
    private readonly ContentControl _image = new() { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Reads the pack's directory; throws <see cref="AssetFormatException"/> when it is not a readable PEG.</summary>
    /// <param name="bytes">The PEG file.</param>
    /// <param name="name">Its name.</param>
    /// <param name="decodeMpeg2">Decode MPEG-2 compressed backgrounds (the Packfiles setting); false only lists them.</param>
    /// <param name="blackKey">Black as transparent for MPEG-2 backgrounds (the Packfiles setting), or null.</param>
    public PegPreview(byte[] bytes, string name, bool decodeMpeg2 = true, PegBlackKey? blackKey = null)
    {
        _bytes = bytes;
        _name = name;
        _decodeMpeg2 = decodeMpeg2;
        _blackKey = blackKey;
        _pack = PegCodec.Read(bytes, name);

        var summary = PreviewUi.Secondary($"PEG texture pack (PlayStation 2), version {_pack.Version}: {_pack.Summary}");
        summary.Margin = new Thickness(8, 4, 8, 4);
        summary.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetName(_list, "Textures");
        _list.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        _list.SetResourceReference(ForegroundProperty, "App.Text");
        foreach (var t in _pack.Textures) _list.Items.Add(Row(t, decodeMpeg2));
        _list.SelectionChanged += (_, _) => ShowSelected();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 48 });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 80 });
        grid.Children.Add(summary);
        var listBorder = new Border { Child = _list, BorderThickness = new Thickness(0, 1, 0, 1) };
        listBorder.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        grid.Children.Add(listBorder);
        Grid.SetRow(listBorder, 1);
        Grid.SetRow(_image, 2);
        grid.Children.Add(_image);
        Content = grid;
        SetResourceReference(BackgroundProperty, "App.PaneBackground");

        // the first texture that can be shown
        int first = _pack.Textures.ToList().FindIndex(t => t.CanDecode && (decodeMpeg2 || !t.IsMpeg2));
        if (_pack.Textures.Count > 0) _list.SelectedIndex = Math.Max(0, first);
        else _image.Content = PreviewUi.Message("This texture pack holds no textures.");
    }

    /// <summary>The pack's directory (for tests).</summary>
    public PegFile Pack => _pack;

    /// <summary>The texture shown now (for tests).</summary>
    public PegTexture? Selected => _list.SelectedIndex >= 0 ? _pack.Textures[_list.SelectedIndex] : null;

    /// <summary>The image preview of the selected texture once decoded, else null (for tests).</summary>
    public ImagePreview? Image => _image.Content as ImagePreview;

    /// <summary>The decode in progress, or a completed task.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>Selects the texture at <paramref name="index"/> (tests, keyboard).</summary>
    public void Select(int index) => _list.SelectedIndex = index;

    private static FrameworkElement Row(PegTexture t, bool decodeMpeg2)
    {
        bool off = t.IsMpeg2 && !decodeMpeg2;
        string state = t.Problem is not null ? " — cannot be decoded" : off ? " — not converted (MPEG-2 decoding is switched off)" : string.Empty;
        var name = PreviewUi.Text(t.Name.Length > 0 ? t.Name : $"#{t.Index}");
        var detail = PreviewUi.Secondary(t.Describe() + state);
        detail.Margin = new Thickness(8, 0, 0, 0);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 1, 2, 1) };
        panel.Children.Add(name);
        panel.Children.Add(detail);
        if (!t.CanDecode || off) panel.Opacity = 0.6;
        panel.ToolTip = t.Problem is { } p ? $"{t.Name}: {p}" : off ? $"{t.Name}: {PegConverter.Mpeg2Reason}" : $"{t.Name}: {t.Describe()}";
        AutomationProperties.SetName(panel, t.Name);
        return panel;
    }

    private void ShowSelected()
    {
        if (_disposed || Selected is not { } t) return;
        _cts?.Cancel();
        DisposeImage();
        if (t.IsMpeg2 && !_decodeMpeg2)
        {
            _image.Content = PreviewUi.Message($"{t.Name} is an MPEG-2 compressed background.", "Decoding MPEG-2 backgrounds is switched off in Settings > Packfiles: it is listed, and left out when the textures are converted.");
            Pending = Task.CompletedTask;
            return;
        }
        if (t.Problem is { } problem)
        {
            _image.Content = PreviewUi.Message($"{t.Name} cannot be decoded.", problem + ".", warning: true);
            Pending = Task.CompletedTask;
            return;
        }
        _image.Content = PreviewUi.Message("Decoding " + t.Name + "...");
        var cts = _cts = new CancellationTokenSource();
        Pending = DecodeAsync(t, cts.Token);
    }

    private async Task DecodeAsync(PegTexture t, CancellationToken ct)
    {
        ImageData data;
        try
        {
            using (BusyTracker.Begin("peg preview"))
                data = await Task.Run(() =>
                {
                    var frames = new List<BgraImage>(t.FrameCount);
                    for (int f = 0; f < t.FrameCount; f++)
                    {
                        ct.ThrowIfCancellationRequested();
                        frames.Add(PegCodec.DecodeFrame(_bytes, t, f, 0, PegBlackKey.For(t, _blackKey)));
                    }
                    string label = string.Create(CultureInfo.InvariantCulture, $"PEG {t.FormatLabel}")
                        + (PegBlackKey.For(t, _blackKey) is { } key ? ", " + key.Describe() : string.Empty);
                    return ImageData.FromFrames(t.Name, frames, PegConverter.DefaultFps, label);
                }, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (ImageDecodeException ex)
        {
            if (!_disposed && !ct.IsCancellationRequested) _image.Content = PreviewUi.Message($"{t.Name} cannot be decoded.", ex.Message, warning: true);
            return;
        }
        if (_disposed || ct.IsCancellationRequested) return;
        _image.Content = new ImagePreview(data);
    }

    private void DisposeImage()
    {
        var old = _image.Content as IDisposable;
        _image.Content = null;
        old?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        DisposeImage();
    }
}
