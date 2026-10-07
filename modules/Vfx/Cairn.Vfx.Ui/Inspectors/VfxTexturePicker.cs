using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cairn.Assets;
using Cairn.Ui.Services;
using Cairn.Viewport;
using Cairn.Vfx.Ui.Dialogs;

namespace Cairn.Vfx.Ui.Inspectors;

/// <summary>
/// Texture picker for the Material tab: searchable list of every texture the asset host sees (loose folders and
/// archives, listed off the UI thread), a preview with size / alpha / frame count, free text and the
/// <c>$original_map</c> placeholders. The result is a texture name, not bytes.
/// </summary>
public static class VfxTexturePicker
{
    internal static readonly string[] Extensions = [".tga", ".vbm", ".dds"];

    internal static readonly (string Name, string Tip)[] Placeholders =
    [
        ("$original_map", "Placeholder: the game replaces it with the texture of the object the effect is attached to (for example a weapon or item skin)"),
        ("$original_map_rgb", "Placeholder: like $original_map, but only the colour channels of the host's texture are used (its alpha is ignored)"),
    ];

    /// <summary>Builds the dialog; <paramref name="result"/> reads the chosen name after OK.</summary>
    public static VfxForm Build(Window? owner, Func<AssetResolver?> resolver, string current, out Func<string> result)
    {
        var form = new VfxForm("Choose texture", owner, "_Choose") { Width = 680 };
        var filter = form.Text("_Search:", "", "Type part of a texture or archive name");
        var list = form.Row("_Textures:", new ListBox { Height = 240 }, "Textures in the game folder, its archives and the mod folder");
        var preview = new Image { Width = 128, Height = 128, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0) };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(preview, "Texture preview");
        var facts = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        facts.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        var checker = new Border { Child = preview, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
        checker.SetResourceReference(Border.BorderBrushProperty, "App.Border");
        checker.SetResourceReference(Border.BackgroundProperty, "App.ChromeBackground");
        var previewRow = new DockPanel { Height = 132 };
        DockPanel.SetDock(checker, Dock.Left);
        previewRow.Children.Add(checker);
        previewRow.Children.Add(facts);
        form.Row("Preview:", previewRow, "Preview of the selected texture (first frame of an animated VBM)");
        var name = form.Text("_Name:", current, "The texture name stored in the material; any name may be typed (it is resolved when the effect is loaded)");
        var holders = new WrapPanel();
        foreach (var (ph, tip) in Placeholders)
        {
            var b = new Button { Content = ph, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 2, 8, 2) };
            AutomationProperties.SetName(b, ph);
            b.Click += (_, _) => { name.Text = ph; list.SelectedItem = null; };
            holders.Children.Add(b);
        }
        holders.Children.Add(new TextBlock { Text = "Replaced at run time by the host object's texture", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        form.Row("Placeholders:", holders, "Names the game substitutes with the texture of the object carrying the effect");

        IReadOnlyList<VfxAssetPicker.Item> all = [];
        var textures = new TextureService(resolver);
        CancellationTokenSource? cts = null;
        void Apply()
        {
            var f = filter.Text.Trim();
            list.ItemsSource = all.Where(i => f.Length == 0 || i.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || i.Source.Contains(f, StringComparison.OrdinalIgnoreCase)).Take(3000).ToList();
            list.SelectedItem = all.FirstOrDefault(i => string.Equals(i.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (list.SelectedItem is { } sel) list.ScrollIntoView(sel);
        }
        async void Preview(string tex)
        {
            cts?.Cancel();
            var my = cts = new CancellationTokenSource();
            preview.Source = null;
            if (tex.Length == 0 || tex.StartsWith('$')) { facts.Text = tex.StartsWith('$') ? Placeholders.FirstOrDefault(p => p.Name == tex).Tip ?? "Placeholder" : ""; return; }
            facts.Text = "Loading...";
            using var busy = BusyTracker.Begin("texture preview " + tex);
            TextureImage? img = null;
            try { img = await textures.GetAsync(tex, my.Token); } catch (OperationCanceledException) { return; } catch (Exception ex) { facts.Text = ex.Message; return; }
            if (my.IsCancellationRequested) return;
            if (img is null) { facts.Text = $"{tex}\nNot found or not decodable through the asset host (the name is still allowed)."; return; }
            preview.Source = img.Image;
            facts.Text = $"{img.ResolvedName}\n{img.Image.PixelWidth} x {img.Image.PixelHeight}, {(img.HasAlpha ? "with alpha" : "opaque")}" +
                (img.IsAnimated ? $"\nAnimated: {img.FrameCount} frames at {img.FramesPerSecond:0.##} fps" : "") + $"\n{img.Location}";
        }
        filter.TextChanged += (_, _) => Apply();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is VfxAssetPicker.Item i) { name.Text = i.Name; } };
        name.TextChanged += (_, _) => { Preview(name.Text.Trim()); form.CanAccept = name.Text.Trim().Length > 0; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) form.DialogResult = true; };
        form.Summary = "Listing textures...";
        Preview(current);
        form.Loaded += async (_, _) =>
        {
            using var busy = BusyTracker.Begin("texture list");
            var r = resolver();
            all = r is null ? [] : await Task.Run(() => VfxAssetPicker.List(r, Extensions));
            form.Summary = all.Count == 0 ? "No textures found (set the game folder in Settings); type a name instead." : $"{all.Count} textures";
            Apply();
        };
        form.Closed += (_, _) => cts?.Cancel();
        result = () => name.Text.Trim();
        return form;
    }

    /// <summary>Shows the picker modally; the chosen name, or null when cancelled.</summary>
    public static string? Pick(Window? owner, Func<AssetResolver?> resolver, string current)
    {
        var form = Build(owner, resolver, current, out var result);
        return form.ShowDialog() == true ? result() : null;
    }
}
