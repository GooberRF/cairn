using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Formats;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Validation;

namespace Cairn.Vf.Ui.Views;

/// <summary>
/// The compact read-only view of a font for other modules' preview panes (packfile entries): a one-line summary, the
/// sample text and every character drawn with the font's own spacing, and any errors. Fonts are small, so it is
/// built at once; it touches no document, history or settings.
/// </summary>
public sealed class VfPreview : DockPanel
{
    public VfPreview(byte[] bytes, string fileName)
    {
        FileName = fileName;
        SetResourceReference(BackgroundProperty, "Preview.Background");
        AutomationProperties.SetName(this, "Font preview of " + fileName);
        VfFont font;
        var problems = new List<VfProblem>();
        try { font = VfReader.Read(bytes, fileName, problems); }
        catch (AssetFormatException ex)
        {
            Children.Add(Text(ex.Message, "App.Text", 12));
            return;
        }
        problems.AddRange(VfValidator.Validate(font));
        Font = font;

        var summary = Text(string.Format(CultureInfo.CurrentCulture, "Version {0} · {1} · {2} glyphs ({3}) · {4} px high{5}",
            font.Version, VfFont.FormatName(font.Format), font.GlyphCount,
            font.GlyphCount == 0 ? "none" : $"{font.FirstCharacter}-{font.LastCharacter}", font.Height,
            font.Kerning.Length > 0 ? $" · {font.Kerning.Length} kerning pairs" : ""), "App.SecondaryText", 12);
        SetDock(summary, Dock.Top);
        Children.Add(summary);
        foreach (var p in problems.Where(p => p.Severity == VfSeverity.Error).Take(3))
        {
            var t = Text($"{p.Code}: {p.Message}", "Severity.Error", 12);
            SetDock(t, Dock.Top);
            Children.Add(t);
        }

        var stack = new StackPanel();
        int zoom = VfImages.FitZoom(font.Height, 32, 4);
        string sample = Documents.VfDocument.PickSample(font);
        SampleLayout = VfLayout.Layout(font, VfLayout.Encode(sample));
        // a font with none of the sample's letters (a digits-only HUD font) shows only its characters
        if (Documents.VfDocument.MissingCount(font, sample) < sample.Count(c => c != ' ')) stack.Children.Add(Block(font, SampleLayout, zoom, "Sample text"));
        int allZoom = VfImages.FitZoom(font.Height, 20, 3);
        stack.Children.Add(Block(font, VfLayout.Layout(font, VfLayout.AllCharacters(font)), allZoom, "Every character"));
        Children.Add(new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    /// <summary>The previewed file's name.</summary>
    public string FileName { get; }
    /// <summary>The font, or null when it could not be read.</summary>
    public VfFont? Font { get; }
    /// <summary>The sample line's layout (null when the font could not be read).</summary>
    public VfTextLayout? SampleLayout { get; }

    private static Border Block(VfFont font, VfTextLayout layout, int zoom, string name)
    {
        var border = new Border { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 6, 8, 2), Padding = new Thickness(4) };
        VfImages.SetBackdrop(border, VfBackdrop.Dark);
        try { border.Child = VfImages.Image(VfRender.Text(font, layout), zoom); }
        catch (VfImageTooLargeException ex) { border.Child = VfImages.Placeholder(ex.Message); }
        AutomationProperties.SetName(border, name);
        return border;
    }

    private static TextBlock Text(string text, string brush, double size)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 0), FontSize = size };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }
}
