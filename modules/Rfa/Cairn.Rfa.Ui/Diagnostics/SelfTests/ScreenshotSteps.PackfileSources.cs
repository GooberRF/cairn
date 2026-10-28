using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Screenshot switches for open packfiles as library sources (L14), run after the command-line files opened (a packfile
/// first): <c>--open-entry &lt;name&gt;</c> opens that entry of the packfile in front the way its Open in Cairn does;
/// <c>--pick-mesh &lt;name&gt;</c> picks that mesh in the clip tab's Preview mesh list; <c>--library-tab Meshes|Clips</c>;
/// <c>--picker-shot &lt;png&gt;</c> opens the Preview mesh (or Preview clip) list and writes its drop-down to the file (a
/// drop-down is a window of its own, which the main window's capture leaves out).
/// </summary>
internal static class PackfileSourcesScreenshotSteps
{
    [ScreenshotStep(20)]
    public static async Task Apply(ScreenshotContext ctx)
    {
        var ws = ctx.Model;
        if (ctx.Extra("open-entry") is { } entry && ctx.Shell.ActiveDocument?.FilePath is { } archive)
        {
            var provider = ctx.Shell.Modules.OfType<IWorkCopyProvider>().FirstOrDefault(p => p.CanOpenArchive(archive));
            await ws.Packfiles.Idle;
            if (provider is null || !provider.OpenArchiveEntry(archive, entry)) ctx.Log($"--open-entry: {entry} could not be opened from {archive}");
            for (int i = 0; i < 300 && ws.ActiveDocument?.DisplayName.Equals(entry, StringComparison.OrdinalIgnoreCase) != true; i++) await Task.Delay(100);
            if (ws.ActiveDocument is ClipDocumentViewModel opened)
                for (int i = 0; i < 1200 && opened.PreviewMesh is null && !opened.Scene.NoticeIsWarning; i++) await Task.Delay(100);
            ctx.Log($"--open-entry: {ws.ActiveDocument?.DisplayName ?? "nothing"} in front");
        }
        await ws.Packfiles.Idle;
        await ctx.SettleAsync();
        if (ctx.Extra("pick-mesh") is { } mesh && ctx.Clip is { } clip)
        {
            var option = clip.PreviewMeshOptions.FirstOrDefault(o => o.Mesh.Name.Equals(mesh, StringComparison.OrdinalIgnoreCase));
            if (option is null) ctx.Log($"--pick-mesh: {mesh} is not in the Preview mesh list");
            else
            {
                clip.SelectedPreviewMesh = option;
                for (int i = 0; i < 600 && !string.Equals(clip.Scene.MeshName, mesh, StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
                ctx.Log($"--pick-mesh: playing on {clip.Scene.MeshName}");
            }
        }
        if (ctx.Extra("library-tab") is { } tab)
        {
            ws.Library.SelectedTab = tab.Equals("Clips", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            ctx.Shell.ShowPanel("rfa.library");
        }
        await ctx.SettleAsync();
        if (ctx.Extra("picker-shot") is { } png)
        {
            string name = ctx.Clip is not null ? "Preview mesh" : "Preview clip";
            var box = Find<ComboBox>(ctx.Window, b => b.IsVisible && System.Windows.Automation.AutomationProperties.GetName(b) == name);
            if (box is null) { ctx.Log($"--picker-shot: no visible {name} list"); return; }
            box.IsDropDownOpen = true;
            await Task.Delay(600);
            await ctx.SettleAsync();
            if (box.Template.FindName("PART_Popup", box) is Popup { Child: FrameworkElement child })
            {
                child.UpdateLayout();
                Capture(child, png, ctx.Window.Background);
                int shown = CountAll<ComboBoxItem>(child, i => i.IsVisible && i.ActualHeight > 0);
                ctx.Log($"--picker-shot: wrote {png} ({box.Items.Count} entries, {shown} item rows realised, drop-down {child.ActualWidth:0}x{child.ActualHeight:0})");
            }
            box.IsDropDownOpen = false;
        }
    }

    private static void Capture(FrameworkElement element, string path, Brush? background)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var area = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.Width * dpi.DpiScaleX), (int)Math.Ceiling(area.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, area);
            dc.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area, Stretch = Stretch.Fill }, null, area);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static int CountAll<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        int n = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && match(hit)) n++;
            n += CountAll(child, match);
        }
        return n;
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && match(hit)) return hit;
            if (Find(child, match) is { } deeper) return deeper;
        }
        return null;
    }
}
