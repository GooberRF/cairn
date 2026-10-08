using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Vf.Rendering;

namespace Cairn.Vf.Ui.Views;

/// <summary>What glyphs are drawn on. Stock fonts are white, so the dark backdrop is the default in both themes.</summary>
public enum VfBackdrop { Dark, Checker, Light }

/// <summary>Helpers that turn rendered glyphs and text into WPF images (frozen, nearest-neighbour scaled).</summary>
internal static class VfImages
{
    /// <summary>A frozen BGRA bitmap, or null for an empty one.</summary>
    public static BitmapSource? ToSource(VfBitmap bitmap)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0) return null;
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null, bitmap.Bgra, bitmap.Width * 4);
        source.Freeze();
        return source;
    }

    /// <summary>An image of <paramref name="bitmap"/> at <paramref name="zoom"/> × its size, pixels kept sharp.</summary>
    public static Image Image(VfBitmap bitmap, double zoom)
    {
        var image = new Image
        {
            Source = ToSource(bitmap),
            Width = bitmap.Width * zoom,
            Height = bitmap.Height * zoom,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        return image;
    }

    /// <summary>Shown where an image cannot be drawn (a damaged font far too large): the reason, in the theme's text colour.</summary>
    public static TextBlock Placeholder(string reason)
    {
        var text = new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(8), FontStyle = FontStyles.Italic };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Warning");
        System.Windows.Automation.AutomationProperties.SetName(text, "Not drawn");
        return text;
    }

    /// <summary>The theme resource key of a backdrop's brush.</summary>
    public static string BrushKey(VfBackdrop backdrop) => backdrop switch
    {
        VfBackdrop.Light => "Preview.White",
        VfBackdrop.Checker => "Preview.CheckerBrush",
        _ => "Preview.Black",
    };

    /// <summary>Paints <paramref name="border"/> with the backdrop's theme brush.</summary>
    public static void SetBackdrop(Border border, VfBackdrop backdrop) => border.SetResourceReference(Border.BackgroundProperty, BrushKey(backdrop));

    /// <summary>A zoom that makes a line about <paramref name="target"/> pixels high (1 to <paramref name="max"/>).</summary>
    public static int FitZoom(int height, int target, int max) => height <= 0 ? 1 : Math.Clamp((int)Math.Round((double)target / height), 1, max);
}
