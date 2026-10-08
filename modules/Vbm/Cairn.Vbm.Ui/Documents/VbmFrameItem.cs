using System.Globalization;
using System.Windows.Media.Imaging;
using Cairn.Formats.Imaging;
using Cairn.Previews;
using Cairn.Ui.Mvvm;

namespace Cairn.Vbm.Ui.Documents;

/// <summary>
/// One frame in the strip. The thumbnail is decoded the first time the strip shows it (the strip virtualises, so a long
/// animation only decodes what is on screen), from the smallest mip level that is still at least thumbnail size.
/// </summary>
public sealed class VbmFrameItem(VbmFile file, int index) : ObservableObject
{
    /// <summary>The thumbnail's target size in pixels.</summary>
    public const int ThumbnailSize = 64;

    private BitmapSource? _thumbnail;
    private bool _failed;
    private bool _isCurrent;

    /// <summary>True for the frame the preview shows (the playing frame while it plays): the strip marks it.</summary>
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    /// <summary>The frame (0-based).</summary>
    public int Index { get; } = index;

    /// <summary>The frame's data in the snapshot this item was made from.</summary>
    public VbmFrame Frame { get; } = file.Frames[index];

    /// <summary>The number shown under the thumbnail (counting from 1).</summary>
    public string Label => (Index + 1).ToString(CultureInfo.CurrentCulture);

    public string ToolTip => string.Format(CultureInfo.CurrentCulture, "Frame {0} of {1}", Index + 1, file.FrameCount);

    /// <summary>The thumbnail, decoded on first use; null when the frame cannot be decoded.</summary>
    public BitmapSource? Thumbnail
    {
        get
        {
            if (_thumbnail is not null || _failed) return _thumbnail;
            try
            {
                int level = 0;
                while (level + 1 < Frame.Levels.Count && file.LevelSize(level + 1) is var (w, h) && Math.Max(w, h) >= ThumbnailSize) level++;
                var (lw, lh) = file.LevelSize(level);
                _thumbnail = ImageData.ToBitmap(VbmEncoder.DecodeLevel(Frame.Levels[level], lw, lh, file.Format, file.Version), false);
            }
            catch (ImageDecodeException) { _failed = true; }
            return _thumbnail;
        }
    }
}
