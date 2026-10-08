#if CAIRN_MODULE_VPP && CAIRN_MODULE_SND
using System.Windows;
using Cairn.Previews;
using Cairn.Snd;
using Cairn.Snd.Ui;
using Cairn.Snd.Ui.Dialogs;
using Cairn.Snd.Ui.Documents;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Vpp.Writing;

namespace Cairn.Shell;

/// <summary>
/// Sounds in packfiles (the packfile and sounds modules together): PS2 sound entries get an Info line, preview with a
/// waveform and "Open in Cairn", "Convert sounds..." turns a selection into WAV entries as ONE undo step, and a sound
/// opened from the packfile converts back into it. The packfile is built from synthetic sounds.
/// </summary>
internal static class SoundPackfileSelfTests
{
    [SelfTest("snd.packfile")]
    public static async Task SoundsInAPackfile(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var vpp = shell.Modules.OfType<VppModule>().FirstOrDefault();
        var snd = shell.Modules.OfType<SndModule>().FirstOrDefault();
        if (vpp is null || snd is null) { ctx.Skip("needs the packfile and sounds modules"); return; }
        string folder = Path.Combine(Path.GetTempPath(), "cairn-snd-vpp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<IDocument>();
        try
        {
            var package = VppPackage.Empty;
            foreach (var (name, bytes) in new[] { ("shot.vse", SyntheticSounds.OneShotVse(400)), ("hum.vse", SyntheticSounds.LoopingVse(300)), ("music.vmu", SyntheticSounds.Music(1500)), ("readme.txt", "not a sound"u8.ToArray()) })
                package = VppEdit.AddBytes(package, name, bytes, VppClashPolicy.Replace).Package;
            string packPath = Path.Combine(folder, "sounds.vpp");
            VppSaver.Save(package, packPath, null, CancellationToken.None, VppSaveOptions.Default);
            if (!ctx.Check(shell.OpenFile(packPath) && shell.ActiveDocument is VppDocument, "the packfile opens")) return;
            var doc = (VppDocument)shell.ActiveDocument!;
            opened.Add(doc);
            await ctx.SettleAsync();

            // Info column and type names
            var shot = doc.Current.Find("shot.vse")!;
            string Info(VppItem item) => VppInfo.Summarize(item.Name, item.Source.Open, item.Size).Text;
            ctx.Check(Info(shot) == "22,050 Hz mono, 0.5 s, PS ADPCM", $"Info: {Info(shot)}");
            ctx.Check(Info(doc.Current.Find("hum.vse")!).EndsWith("PS ADPCM, loops", StringComparison.Ordinal), $"Info: {Info(doc.Current.Find("hum.vse")!)}");
            ctx.Check(Info(doc.Current.Find("music.vmu")!).StartsWith("44,100 Hz stereo", StringComparison.Ordinal), $"Info: {Info(doc.Current.Find("music.vmu")!)}");
            ctx.Check(VppFileTypes.Describe("x.vse").DisplayName == "PS2 sound effect" && VppFileTypes.Describe("x.vmu").DisplayName == "PS2 music", "type names");
            var facts = VppFacts.Describe(shot);
            ctx.Check(facts.Rows.Any(r => r.Label == "Codec" && r.Value == "PS ADPCM") && facts.Rows.Any(r => r.Label == "SPU pitch"), "details pane: codec and SPU pitch");

            // preview: waveform, PS ADPCM, Open in Cairn
            var window = new Window { Width = 700, Height = 300, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow, Left = -2000, Top = -2000 };
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            window.Show();
            try
            {
                area.Show(shot, [shot], doc.Current, immediate: true);
                await area.SettleAsync();
                await ctx.SettleAsync();
                var audio = area.Preview.View as AudioPreview;
                ctx.Check(audio is not null, $"the .vse previews as a sound ({area.Preview.Kind})");
                ctx.Check(audio?.FormatText.Contains("PS ADPCM", StringComparison.Ordinal) == true, $"preview format line: {audio?.FormatText}");
                ctx.Check(audio?.OpenInCairnButton is not null, "the preview offers Open in Cairn");
            }
            finally
            {
                area.Dispose();
                window.Close();
            }

            // batch conversion into the packfile: one undo step
            doc.SelectNames(["shot.vse", "hum.vse", "music.vmu", "readme.txt"]);
            await ctx.SettleAsync();
            string? summary = await vpp.ConvertWithAsync(doc, snd, interactive: false,
                options: new SndConvertChoice(SoundTarget.IntoPackfile, null, new SoundConvertOptions(), Replace: false));
            ctx.Check(summary?.StartsWith("Converted 3 sounds to WAV", StringComparison.Ordinal) == true, $"summary: {summary}");
            ctx.Check(new[] { "shot.wav", "hum.wav", "music.wav" }.All(n => doc.Current.Find(n) is not null) && doc.Current.Find("readme.wav") is null, "three WAV entries were added; the text file was skipped");
            ctx.Check(doc.CanUndo && doc.UndoLabel?.Contains("Convert 3 sounds to WAV", StringComparison.Ordinal) == true, $"one undo step: {doc.UndoLabel}");
            ctx.Check(Info(doc.Current.Find("hum.wav")!).StartsWith("11,025 Hz, 16-bit mono", StringComparison.Ordinal), $"the WAV entry's Info: {Info(doc.Current.Find("hum.wav")!)}");
            doc.Undo();
            ctx.Check(new[] { "shot.wav", "hum.wav", "music.wav" }.All(n => doc.Current.Find(n) is null), "Undo removes all three");
            doc.Redo();
            ctx.Check(doc.Current.Find("music.wav") is not null, "Redo adds them back");

            // Open in Cairn, then convert back into the packfile
            await doc.Commands.OpenInCairnAsync(doc.Current.Find("shot.vse")!);
            await ctx.SettleAsync();
            if (ctx.Check(shell.ActiveDocument is SndDocument, "Open in Cairn opens a sound tab"))
            {
                var sound = (SndDocument)shell.ActiveDocument!;
                opened.Add(sound);
                ctx.Check(snd.PackfileOf(sound) == "sounds.vpp" && snd.NextToFolder(sound) == folder, $"the tab knows its packfile ({snd.PackfileOf(sound)}, {snd.NextToFolder(sound)})");
                var written = await snd.ConvertDocumentAsync(sound, new SndConvertChoice(SoundTarget.IntoPackfile, null, new SoundConvertOptions(), Replace: false));
                ctx.Check(written is { Count: 1 } && written[0] == "shot (2).wav" && doc.Current.Find("shot (2).wav") is not null, $"converted into the packfile as {(written is { Count: 1 } ? written[0] : "nothing")} (shot.wav was taken)");
                ctx.Check(doc.UndoLabel == "Convert shot.vse to WAV", $"one undo step in the packfile: {doc.UndoLabel}");
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (shell.Documents.Contains(d)) shell.CloseDiscarding(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
#endif
