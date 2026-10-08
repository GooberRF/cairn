using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Vpp.Writing;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// Type-to-select in the entry list and the toolbar's sound autoplay toggle
/// (<c>Cairn.exe --selftest-only vpp.list-typing</c>, <c>vpp.autoplay</c>).
/// </summary>
public static class VppListTypingSelfTests
{
    private static byte[] Wave(int rate, int seconds)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        int data = rate * 2 * seconds;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((ushort)1); w.Write((ushort)1); w.Write(rate); w.Write(rate * 2); w.Write((ushort)2); w.Write((ushort)16);
        w.Write("data"u8); w.Write(data); w.Write(new byte[data]);
        return s.ToArray();
    }

    private static VppDocument Open(SelfTestContext ctx, string folder, params (string Name, byte[] Data)[] files)
    {
        var p = VppEdit.AddSources(VppPackage.Empty, [.. files.Select(f => (f.Name, (VppSource)new MemorySource(f.Data)))], VppClashPolicy.KeepBoth).Package;
        string path = Path.Combine(folder, "typing.vpp");
        VppSaver.Save(p, path, null, CancellationToken.None, VppSaveOptions.Default);
        var doc = (VppDocument)ctx.Shell.Modules.OfType<VppModule>().First().Kind.Open(path);
        ctx.Shell.AddDocument(doc);
        SelfTestPump.Pump(200);
        return doc;
    }

    private static string Selected(VppDocument doc) => string.Join(", ", doc.SelectedItems.Select(i => i.Name));

    [SelfTest("vpp.list-typing")]
    public static void ListTyping(SelfTestContext ctx)
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-vpp-typing-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        VppDocument? doc = null;
        try
        {
            var names = new List<(string, byte[])>();
            for (int i = 0; i < 120; i++) names.Add(($"aaa_filler_{i:000}.txt", [1]));
            names.Add(("boom.tga", [1]));
            names.Add(("bridge.tga", [1]));
            names.Add(("crate.tga", [1]));
            doc = Open(ctx, folder, [.. names]);
            var list = ((VppDocumentView)doc.View).FileList;

            list.TypeForTest("c");
            SelfTestPump.Pump(100);
            ctx.Check(Selected(doc) == "crate.tga", $"typing 'c' selects crate.tga ({Selected(doc)})");

            SelfTestPump.Pump(1500); // past the type-ahead pause, so the next letters start a new search
            list.TypeForTest("bri");
            SelfTestPump.Pump(100);
            ctx.Check(Selected(doc) == "bridge.tga", $"typing 'bri' selects bridge.tga, not boom.tga ({Selected(doc)})");
        }
        finally
        {
            if (doc is not null) ctx.Shell.Close(doc);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [SelfTest("vpp.autoplay")]
    public static async Task AutoPlay(SelfTestContext ctx)
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-vpp-autoplay-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        bool wasOn = module.Settings.AutoPlaySounds;
        double volume = AudioPreview.Volume;
        AudioPreview.Volume = 0; // silent in test runs
        VppDocument? doc = null;
        try
        {
            doc = Open(ctx, folder, ("one.wav", Wave(22050, 3)), ("two.wav", Wave(22050, 3)), ("wall.tga", [1]));
            var view = (VppDocumentView)doc.View;

            async Task<AudioPreview?> SelectAsync(string name)
            {
                doc.SetSelection([doc.Current.Find(name)!]);
                await Task.Delay(VppPreviewController.Debounce + TimeSpan.FromMilliseconds(100));
                if (view.PreviewHost.Content is VppPreviewPane pane) await pane.Pending;
                SelfTestPump.Pump(100);
                return (view.PreviewHost.Content as VppPreviewPane)?.View as AudioPreview;
            }

            ctx.Check(module.AutoPlayToggle is not null, "the toolbar has the autoplay toggle");
            module.Settings.AutoPlaySounds = false;
            var first = await SelectAsync("one.wav");
            ctx.Check(first is { IsPlaying: false }, $"autoplay off: selecting a sound waits for Play ({first?.IsPlaying})");

            module.Settings.AutoPlaySounds = true;
            var second = await SelectAsync("two.wav");
            ctx.Check(second is { IsPlaying: true }, $"autoplay on: selecting a sound plays it ({second?.IsPlaying})");
            _ = await SelectAsync("wall.tga");
            ctx.Check(second is { IsPlaying: false }, "selecting another entry stops the sound");
        }
        finally
        {
            module.Settings.AutoPlaySounds = wasOn;
            AudioPreview.Volume = volume;
            if (doc is not null) ctx.Shell.Close(doc);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
