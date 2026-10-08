using System.Windows;
using System.Windows.Documents;

namespace Cairn.Vbm.Ui;

/// <summary>The "Volition bitmaps" help topic.</summary>
public static class VbmHelp
{
    /// <summary>Builds the topic.</summary>
    public static FlowDocument Build()
    {
        var doc = new FlowDocument { PagePadding = new Thickness(16), FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13 };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "App.Text");
        doc.SetResourceReference(FlowDocument.BackgroundProperty, "App.PaneBackground");

        void Heading(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        void Para(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 6) });
        void Bullets(params string[] items)
        {
            var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 6) };
            foreach (string item in items) list.ListItems.Add(new ListItem(new Paragraph(new Run(item)) { Margin = new Thickness(0, 0, 0, 2) }));
            doc.Blocks.Add(list);
        }

        doc.Blocks.Add(new Paragraph(new Run("Volition bitmaps (.vbm)")) { FontSize = 20, FontWeight = FontWeights.SemiBold });
        Para("A VBM is Red Faction's own bitmap: one or more frames of 16-bit pixels, each frame with optional mip levels, " +
             "and a frame rate the game plays an animated one at. Interface panels, animated level textures and many effects use them.");

        Heading("The file");
        Bullets(
            "Pixel formats: 1555 (5 bits per colour, each pixel fully opaque or fully transparent), 4444 (16 levels of transparency, " +
            "coarser colour) and 565 (no transparency, the best colour).",
            "Version 1 files store the 1555 transparency bit inverted; version 2 store it the standard way. Every stock 1555 bitmap is " +
            "version 1, so new 1555 bitmaps are written as version 1; 4444 and 565 bitmaps are written as version 2.",
            "Mip levels are smaller copies of every frame (each half the size of the one before) for distant surfaces. Interface images need none.",
            "The game keeps a bitmap's frame count in one byte, so it plays at most 255 frames.");

        Heading("Viewing");
        Bullets(
            "Space plays or pauses; comma and period step one frame; the frame strip selects frames (Ctrl and Shift for several).",
            "Fit and 100% set the zoom; the mouse wheel zooms. Alpha shows the transparency as grey; Checkerboard toggles the pattern behind transparent pixels.",
            "Smooth and Pixels choose how a zoomed bitmap is drawn: blended, or each pixel a sharp square. Until you pick one, zoom above 100% shows pixels and 100% or less is smooth.",
            "The frame strip marks the frame on show (the playing one) with an accent outline and a play mark under it, apart from the selection.",
            "The mip list shows a smaller level at the size of the full one, so the loss of detail is visible.");

        Heading("Editing");
        Bullets(
            "Frame rate: type it in the panel on the right (one undo step).",
            "Replace Frame (Ctrl+R) and Add Frames (Insert) take TGA, PNG, JPG, DDS, BMP or other VBM files; image files dropped on the frame strip " +
            "are added where you drop them. Every image is converted to the bitmap's pixel format with its mip levels rebuilt.",
            "An image of another size asks how to resize it: the filter (nearest for hard pixel edges, bilinear, or high quality) and the fit " +
            "(stretch, keep the aspect with transparent padding, or crop the centre), with a preview. The choice is remembered (Settings > Volition bitmaps).",
            "Drag frames along the strip to reorder them (one undo step per drop). Ctrl+C copies the selected frames and Ctrl+V pastes them after the " +
            "selection, in this bitmap or another tab; Ctrl+V with an image on the clipboard (copied from another program) adds it as a frame.",
            "Duplicate (Ctrl+D), Remove (Delete), Move Earlier / Later (Alt+Left / Alt+Right) and Reverse Frame Order act on the selected frames.",
            "Bitmap > Pixel Format and Mip Levels rebuild every frame from its full-size level.",
            "Frames you do not touch keep their exact bytes: opening a bitmap and saving it gives back the same file.");

        Heading("Converting and exporting");
        Bullets(
            "Convert to ATX writes one TGA per frame plus an animated texture (.atx) through the animated textures module's import " +
            "window, and opens the .atx. A bitmap opened from a packfile offers your last import folder rather than a temporary one.",
            "Export Frames writes the frames (all of them or the selection) as TGA or PNG, named name_00, name_01 and so on.",
            "File > New > Volition bitmap makes a bitmap from images: pick them in playing order, then choose the size, pixel format, frame rate and mip levels " +
            "(Settings > Volition bitmaps sets what the window starts with).");

        Heading("Problems");
        Para("The Problems tab lists what is wrong with the open bitmap: a file that stops early (the complete frames are kept), " +
             "bytes after the last frame, a mip count the size cannot hold, more frames than the game plays, an animation without a frame rate, " +
             "and sizes that are not powers of two (fine for interface images, not for textures on level geometry or meshes).");
        return doc;
    }
}
