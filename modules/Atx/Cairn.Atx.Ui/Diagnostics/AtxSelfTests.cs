using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Cairn.Atx.Ui.ViewModels;
using Cairn.Ui.Diagnostics;

namespace Cairn.Atx.Ui.Diagnostics;

/// <summary>Non-interactive checks of the ATX document flows (run with --selftest).</summary>
internal static class AtxSelfTests
{
    private static void Pump(int ms = 400) => Cairn.Ui.Diagnostics.SelfTestPump.Pump(ms);

    private static string? SamplesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "samples", "atx"))) return Path.Combine(dir.FullName, "samples", "atx");
        return null;
    }

    [SelfTest("atx.new-from-templates")]
    public static void NewFromTemplates(SelfTestContext ctx)
    {
        var ws = AtxModule.Workspace!;
        foreach (var kind in Enum.GetValues<NewFileTemplateKind>())
        {
            using var doc = new DocumentViewModel(ws, NewFileTemplates.Create(kind), null, $"t-{kind}.atx");
            Pump(200);
            // Templates name frame files that do not exist yet, so missing-file errors are expected.
            ctx.Log($"template {kind}: {doc.FrameCount} frames, {doc.Diagnostics.Count} diagnostics");
            ctx.Check(doc.TextForSave == NewFileTemplates.Create(kind) && !doc.IsDirty,
                $"template {kind} opens as an unmodified new document");
        }
    }

    [SelfTest("atx.settings-page")]
    public static void SettingsPage(SelfTestContext ctx)
    {
        var ws = AtxModule.Workspace!;
        var s = ws.AtxSettings;
        var (template, background, colour) = (s.NewFileTemplate, s.PreviewBackground, s.PreviewCustomColor);
        try
        {
            var page = new AtxSettingsPage(ws);
            page.Load();
            ctx.Check(page.Commented.IsChecked == (template == NewFileTemplateKind.Commented) && page.Custom.Text == colour, "Load shows the current settings");
            // Edit and cancel (the shell simply does not call Commit): nothing changes.
            page.Commented.IsChecked = template != NewFileTemplateKind.Commented;
            page.Minimal.IsChecked = template == NewFileTemplateKind.Commented;
            page.Background.SelectedIndex = 3;
            page.Custom.Text = "#123456";
            ctx.Check(s.NewFileTemplate == template && s.PreviewBackground == background && s.PreviewCustomColor == colour, "cancel leaves the settings untouched");
            page.Commit();
            ctx.Check(s.NewFileTemplate != template && s.PreviewBackground == PreviewBackground.Custom && s.PreviewCustomColor == "#123456", "Commit stores template, background and custom colour");
            var again = new AtxSettingsPage(ws);
            again.Load();
            ctx.Check(again.Background.SelectedIndex == 3 && again.Custom.Text == "#123456" && again.Custom.IsEnabled, "Load after Commit round-trips (custom colour box enabled)");
            again.Custom.Text = "nonsense";
            again.Commit();
            ctx.Check(s.PreviewCustomColor == "#123456", "an invalid colour is not stored");
        }
        finally
        {
            (s.NewFileTemplate, s.PreviewBackground, s.PreviewCustomColor) = (template, background, colour);
            ws.InvalidateImageCaches();
        }
    }

    [SelfTest("atx.open-save-reopen")]
    public static void OpenSaveReopen(SelfTestContext ctx)
    {
        string? samples = SamplesFolder();
        if (!ctx.Check(samples is not null, "samples/atx found")) return;
        string temp = Path.Combine(Path.GetTempPath(), "cairn-atx-selftest");
        Directory.CreateDirectory(temp);
        foreach (string path in Directory.GetFiles(samples!, "*.atx"))
        {
            var doc = (DocumentViewModel)AtxModule.DocumentKind.Open(path);
            Pump(150);
            string copy = Path.Combine(temp, Path.GetFileName(path));
            doc.SaveTo(copy);
            ctx.Check(File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(path)),
                $"{Path.GetFileName(path)} saves byte-identical (encoding and line endings kept)");
            doc.Dispose();
        }
    }

    [SelfTest("atx.frames-edit-undo-recovery")]
    public static void FramesEditUndoRecovery(SelfTestContext ctx)
    {
        string? samples = SamplesFolder();
        if (!ctx.Check(samples is not null, "samples/atx found")) return;
        string path = Path.Combine(samples!, "hazard_strip.atx");
        var doc = (DocumentViewModel)AtxModule.DocumentKind.Open(path);
        Pump();
        string original = doc.TextForSave;
        int frames = doc.FrameCount;
        ctx.Check(frames > 1, $"hazard_strip has frames ({frames})");

        doc.Frames.SelectAllCommand.Execute(null);
        doc.Frames.DuplicateCommand.Execute(null);
        Pump();
        ctx.Check(doc.FrameCount == frames * 2, $"duplicate all doubles the frames ({doc.FrameCount})");
        ctx.Check(doc.IsDirty, "frames edit marks the document dirty");

        byte[]? snapshot = doc.CaptureRecovery();
        ctx.Check(snapshot is not null, "recovery captures a dirty document");

        doc.Undo();
        Pump();
        ctx.Check(doc.TextForSave == original, "one undo restores the source text");
        ctx.Check(!doc.IsDirty, "undo back to the saved text clears dirty");

        if (snapshot is not null)
        {
            var restored = (DocumentViewModel)AtxModule.DocumentKind.Restore(
                new Cairn.Workspace.RecoverySnapshot(doc.Id, path, doc.DisplayName, DateTime.UtcNow, "atx", snapshot));
            Pump();
            ctx.Check(restored.FrameCount == frames * 2 && restored.IsDirty, "recovery restore brings the edit back as unsaved");
            restored.Dispose();
        }
        doc.Dispose();
    }
}
