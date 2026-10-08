using Cairn.Formats.Imaging;
using Cairn.Ui.Modules;

namespace Cairn.Vbm.Ui;

/// <summary>
/// The module's remembered choices ("vbm." keys in the app's settings): the defaults for new bitmaps and how images of
/// another size are fitted. Read each time they are needed, so the settings page and the resize window share them.
/// </summary>
public sealed class VbmSettings(ModuleSettings store)
{
    /// <summary>The frame rate a new bitmap starts with when nothing was chosen.</summary>
    public const int FallbackFps = 15;

    private readonly ModuleSettings _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>The frame rate the New VBM window starts with.</summary>
    public int DefaultFps
    {
        get => Math.Clamp(_store.Get("newFps", FallbackFps), 0, VbmEditing.MaxFps);
        set => _store.Set("newFps", Math.Clamp(value, 0, VbmEditing.MaxFps));
    }

    /// <summary>The pixel format the New VBM window starts with; null = the one the images suit.</summary>
    public VbmPixelFormat? DefaultFormat
    {
        get => _store.Get<string>("newFormat") is { } s && Enum.TryParse<VbmPixelFormat>(s, out var f) && Enum.IsDefined(f) ? f : null;
        set => _store.Set("newFormat", value?.ToString());
    }

    /// <summary>True when the New VBM window starts with mipmaps (see <see cref="VbmResize.DefaultMipLevels"/>).</summary>
    public bool Mipmaps
    {
        get => _store.Get("newMipmaps", false);
        set => _store.Set("newMipmaps", value);
    }

    /// <summary>The filter and fit used for images of another size (the last ones chosen in the resize window).</summary>
    public VbmResizeOptions Resize
    {
        get => new(Enum.IsDefined(_store.Get("resizeFilter", VbmResizeFilter.HighQuality)) ? _store.Get("resizeFilter", VbmResizeFilter.HighQuality) : VbmResizeFilter.HighQuality,
            Enum.IsDefined(_store.Get("resizeFit", VbmFitMode.Stretch)) ? _store.Get("resizeFit", VbmFitMode.Stretch) : VbmFitMode.Stretch);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _store.Set("resizeFilter", value.Filter);
            _store.Set("resizeFit", value.Fit);
        }
    }

    /// <summary>True to show the resize window each time an image of another size comes in (else the remembered choice is used).</summary>
    public bool AskResize
    {
        get => _store.Get("resizeAsk", true);
        set => _store.Set("resizeAsk", value);
    }

    /// <summary>The mip levels a new <paramref name="width"/> x <paramref name="height"/> bitmap starts with.</summary>
    public int MipLevelsFor(int width, int height) => Mipmaps ? VbmResize.DefaultMipLevels(width, height) : 1;
}
