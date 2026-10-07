using System.IO;
using System.Windows;
using Cairn.Atx.Ui.ViewModels;
using Cairn.Atx.Ui.Views.Dialogs;
using Cairn.Ui.Diagnostics;

namespace Cairn.Atx.Ui.Diagnostics;

/// <summary>ATX dialogs for <c>--dialog name</c> screenshot runs, over the active .atx (or the hazard_strip sample).</summary>
internal static class AtxScreenshots
{
    private static DocumentViewModel Document(ScreenshotContext ctx)
    {
        if (ctx.Shell.ActiveDocument is DocumentViewModel active) return active;
        string? samples = AtxSelfTestSupport.SamplesFolder();
        var doc = samples is null
            ? (DocumentViewModel)AtxModule.DocumentKind.CreateNew()!
            : (DocumentViewModel)AtxModule.DocumentKind.Open(Path.Combine(samples, "hazard_strip.atx"));
        ctx.Shell.AddDocument(doc);
        AtxSelfTestSupport.Pump(300);
        return doc;
    }

    private static Window Shown(Window window)
    {
        window.Show();
        AtxSelfTestSupport.Pump(300);
        return window;
    }

    [ScreenshotDialog("atx-vpp-browser")]
    public static Window VppBrowser(ScreenshotContext ctx) => Shown(VppBrowserDialog.CreateForCapture(ctx.MainWindow, Document(ctx)));

    [ScreenshotDialog("atx-add-sequence")]
    public static Window AddSequence(ScreenshotContext ctx) => Shown(AddSequenceDialog.CreateForCapture(ctx.MainWindow, Document(ctx)));

    [ScreenshotDialog("atx-bulk-timing")]
    public static Window BulkTiming(ScreenshotContext ctx) => Shown(BulkTimingDialog.CreateForCapture(ctx.MainWindow, Document(ctx)));

    [ScreenshotDialog("atx-vbm-import")]
    public static Window? VbmImport(ScreenshotContext ctx)
    {
        string path = Path.Combine(Path.GetTempPath(), "cairn-atx-shot", "glow_anim.vbm");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, AtxFlowTests.Vbm(6));
        var source = VbmImportSource.FromFile(path);
        var load = VbmImportLoader.Load(source);
        if (load.Bytes is null || load.Info is null) return null;
        return Shown(VbmImportDialog.CreateForCapture(ctx.MainWindow, AtxModule.Workspace!, source, load.Bytes, load.Info));
    }
}
