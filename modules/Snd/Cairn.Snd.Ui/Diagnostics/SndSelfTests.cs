using System.Windows;
using System.Windows.Controls;
using Cairn.Formats.Audio;
using Cairn.Snd.Ui.Dialogs;
using Cairn.Snd.Ui.Documents;
using Cairn.Snd.Ui.Playback;
using Cairn.Ui.Diagnostics;

namespace Cairn.Snd.Ui.Diagnostics;

/// <summary>Self-tests of the sounds module (run with <c>Cairn.exe --selftest</c>).</summary>
internal static class SndSelfTests
{
    private static SndModule? Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<SndModule>().FirstOrDefault();

    /// <summary>The synthetic files every test opens: name, bytes, expected rate, channels, frames, loops.</summary>
    internal static IReadOnlyList<(string Name, byte[] Bytes, int Rate, int Channels, long Frames, bool Loops)> Samples()
    {
        var tone = SyntheticSounds.Sine(300, 37, 9000);
        return
        [
            ("shot.vse", SyntheticSounds.OneShotVse(400), 22050, 1, 400 * 28, false),
            ("hum.vse", SyntheticSounds.LoopingVse(300), 11025, 1, 300 * 28, true),
            ("old.vse", SyntheticSounds.VseOld([.. SyntheticSounds.Encode(SyntheticSounds.Sine(200)), .. new byte[16 * 40], .. SyntheticSounds.EndMarker()], 254), 22050, 1, 5601, false),
            ("music.vmu", SyntheticSounds.Music(2000), 44100, 2, 2000 * 28, true),
            ("tone.wav", SoundWriter.Wav16(tone, 1, 22050, new SoundLoop(1000, 5000, true, "test")), 22050, 1, tone.Length, true),
            ("tone.aif", SyntheticSounds.Aiff(tone, 1, 22050), 22050, 1, tone.Length, false),
        ];
    }

    private static string TempFolder(string what)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"cairn-snd-{what}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [SelfTest("snd.open-types")]
    public static async Task OpenTypes(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no sounds module in this build"); return; }
        string folder = TempFolder("open");
        try
        {
            foreach (var (name, bytes, rate, channels, frames, loops) in Samples())
            {
                string path = Path.Combine(folder, name);
                File.WriteAllBytes(path, bytes);
                if (!ctx.Check(ctx.Shell.OpenFile(path) && ctx.Shell.ActiveDocument is SndDocument, $"{name}: opens in a sound tab")) continue;
                var doc = (SndDocument)ctx.Shell.ActiveDocument!;
                await ctx.SettleAsync();
                var view = (SndDocumentView)doc.View;
                string Detail(string label) => view.DetailRows.FirstOrDefault(r => r.Label == label).Value ?? "";
                ctx.Check(doc.Sound.SampleRate == rate && doc.Sound.Channels == channels && doc.Sound.FrameCount == frames,
                    $"{name}: {doc.Sound.SampleRate} Hz, {doc.Sound.Channels} ch, {doc.Sound.FrameCount} frames (want {rate}, {channels}, {frames})");
                ctx.Check((doc.Sound.Loop is not null) == loops && doc.Looping == loops, $"{name}: loop {(loops ? "found and on" : "none")}");
                ctx.Check(Detail("Sample rate").StartsWith($"{rate:N0} Hz", StringComparison.Ordinal) && Detail("Codec").Length > 0 && Detail("Duration").Length > 0 && Detail("Bit depth").Length > 0,
                    $"{name}: details list rate '{Detail("Sample rate")}', codec '{Detail("Codec")}', bit depth '{Detail("Bit depth")}'");
                ctx.Check(doc.IsReadOnly && !doc.IsDirty, $"{name}: read-only, never dirty");
                ctx.Check(doc.StatusItems.Count >= 2, $"{name}: status bar items ({string.Join(" | ", doc.StatusItems.Select(s => s.Text))})");
                ctx.Check(view.Wave.ActualWidth > 0 && view.Wave.FramesPerPixel > 0, $"{name}: waveform laid out ({view.Wave.ActualWidth:0} px, {view.Wave.FramesPerPixel:0.0} frames per pixel)");

                // silent playing in a diagnostic run: the position runs, pause holds it, stop goes back
                ctx.Check(doc.Player is SilentPlayer, $"{name}: diagnostic runs play silently");
                doc.Play();
                int wait = (int)Math.Clamp(doc.Sound.Duration.TotalMilliseconds * 0.3, 20, 250); // well before the end of a short sound
                await Task.Delay(wait);
                long playing = doc.Position;
                ctx.Check(doc.IsPlaying && playing > 0, $"{name}: playing advances ({playing} frames after {wait} ms)");
                doc.Pause();
                long paused = doc.Position;
                await Task.Delay(120);
                ctx.Check(!doc.IsPlaying && doc.Position == paused, $"{name}: pause holds the position ({paused})");
                doc.Seek(frames / 2);
                ctx.Check(Math.Abs(doc.Position - frames / 2) <= 1, $"{name}: seek to the middle ({doc.Position})");
                doc.Stop();
                ctx.Check(doc.Position == 0 && !doc.IsPlaying, $"{name}: stop goes back to the start");
                view.ZoomBy(0.25);
                await ctx.SettleAsync();
                ctx.Check(view.Wave.FramesPerPixel < frames / Math.Max(1, view.Wave.ActualWidth), $"{name}: zooms in ({view.Wave.FramesPerPixel:0.00} frames per pixel)");
                view.Fit();
                ctx.Shell.Close(doc);
            }

            // a looping sound keeps playing past its end; with the loop off it stops at the end
            string loopPath = Path.Combine(folder, "hum.vse");
            ctx.Shell.OpenFile(loopPath);
            if (ctx.Shell.ActiveDocument is SndDocument hum)
            {
                hum.Seek(hum.Sound.FrameCount - 1000);
                hum.Play();
                await Task.Delay(400);
                ctx.Check(hum.IsPlaying && hum.Position < hum.Sound.FrameCount, $"looping: still playing after the end ({hum.Position})");
                hum.Looping = false;
                hum.Stop();
                hum.Seek(hum.Sound.FrameCount - 500);
                hum.Play();
                await Task.Delay(400);
                ctx.Check(!hum.IsPlaying, "loop off: playing stops at the end");
                ctx.Shell.Close(hum);
            }

            // an Ogg Vorbis file from the game data, when there is one
            if (ctx.Shell.Assets.HasSources)
            {
                await ctx.Shell.Assets.ArchivesIndexed;
                var ogg = await Task.Run(() => ctx.Shell.Assets.Resolver.Enumerate([".ogg"]).FirstOrDefault());
                if (ogg is null) ctx.Log("  no .ogg in the game data: skipped");
                else
                {
                    var doc = (SndDocument)module.Kind.OpenBytes(ogg.ReadAllBytes(), ogg.ResolvedName, ogg.ResolvedName + " (self-test)");
                    ctx.Shell.AddDocument(doc);
                    await ctx.SettleAsync();
                    ctx.Check(doc.Sound.Codec == "Vorbis" && doc.Sound.FrameCount > 0, $"{ogg.ResolvedName}: Ogg Vorbis opens ({doc.Sound.RateAndChannels}, {doc.Sound.Duration})");
                    ctx.Shell.Close(doc);
                }
            }
        }
        finally { TryDelete(folder); }
    }

    [SelfTest("snd.damaged")]
    public static async Task Damaged(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no sounds module in this build"); return; }
        var good = SyntheticSounds.OneShotVse(100);
        var cut = good[..(good.Length - 700)];
        var doc = (SndDocument)module.Kind.OpenBytes(cut, "cut.vse", "cut.vse (self-test)");
        ctx.Shell.AddDocument(doc);
        await ctx.SettleAsync();
        ctx.Check(doc.Sound.Problems.Any(p => p.Code == "SND002"), "a cut-short .vse opens with a problem (SND002)");
        var panel = SndProblemsPanel.For(doc);
        ctx.Check(panel.Count == doc.Sound.Problems.Count, $"the Problems tab lists them ({panel.Count})");
        ctx.Check(doc.StatusItems.Any(s => s.Text.Contains("warning", StringComparison.Ordinal) && s.Command is not null), "the status bar counts the warnings and opens the Problems tab");
        ctx.Shell.Close(doc);
        try
        {
            module.Kind.OpenBytes([1, 2, 3], "tiny.vse", "tiny.vse (self-test)");
            ctx.Check(false, "a 3-byte .vse is refused");
        }
        catch (Cairn.Formats.AssetFormatException ex)
        {
            ctx.Check(ex.Message.Contains("too short", StringComparison.Ordinal), $"a 3-byte .vse is refused: {ex.Message}");
        }
    }

    [SelfTest("snd.convert")]
    public static async Task Convert(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no sounds module in this build"); return; }
        string folder = TempFolder("convert");
        try
        {
            foreach (var (name, bytes, rate, channels, frames, loops) in Samples())
            {
                var doc = (SndDocument)module.Kind.OpenBytes(bytes, name, name + " (self-test)");
                ctx.Shell.AddDocument(doc);
                await ctx.SettleAsync();
                var choice = new SndConvertChoice(SoundTarget.Folder, folder, new SoundConvertOptions(), Replace: false);
                var written = await module.ConvertDocumentAsync(doc, choice);
                if (SoundConversion.SameFormatReason(doc.Sound, SoundOutputFormat.Wav) is { } same)
                {
                    // a 16-bit WAV to WAV changes nothing: not converted, nothing written
                    ctx.Check(written is null && !File.Exists(Path.Combine(folder, SoundConversion.OutputName(name, SoundOutputFormat.Wav))), $"{name}: not converted to WAV ({same})");
                    ctx.Shell.Close(doc);
                    continue;
                }
                if (!ctx.Check(written is { Count: 1 } && File.Exists(written[0]), $"{name}: converted to {(written is { Count: 1 } ? Path.GetFileName(written[0]) : "nothing")}")) { ctx.Shell.Close(doc); continue; }
                var back = SoundDecoder.Decode(File.ReadAllBytes(written![0]), Path.GetFileName(written[0]));
                ctx.Check(back.SampleRate == rate && back.Channels == channels && back.FrameCount == frames && back.Samples.AsSpan().SequenceEqual(doc.Sound.Samples),
                    $"{name}: the WAV holds the decoded sound exactly ({back.SampleRate} Hz, {back.Channels} ch, {back.FrameCount} frames)");
                ctx.Check((back.Loop is not null) == loops && (!loops || (back.Loop!.Start == doc.Sound.Loop!.Start && back.Loop.End == doc.Sound.Loop.End)),
                    $"{name}: loop {(loops ? "kept in the 'smpl' chunk" : "none")}");
                ctx.Shell.Close(doc);
            }
            // a second conversion of the same name takes a free name; Replace asks first (unanswered: nothing written)
            var again = (SndDocument)module.Kind.OpenBytes(SyntheticSounds.OneShotVse(400), "shot.vse", "shot.vse (self-test)");
            ctx.Shell.AddDocument(again);
            var second = await module.ConvertDocumentAsync(again, new SndConvertChoice(SoundTarget.Folder, folder, new SoundConvertOptions(), false));
            ctx.Check(second is { Count: 1 } && Path.GetFileName(second[0]) == "shot (2).wav", $"a taken name gets a free one ({(second is { Count: 1 } ? Path.GetFileName(second[0]) : "-")})");
            string shot = Path.Combine(folder, "shot.wav");
            byte[] before = [9, 9, 9];
            File.WriteAllBytes(shot, before);
            var unanswered = await module.ConvertDocumentAsync(again, new SndConvertChoice(SoundTarget.Folder, folder, new SoundConvertOptions { WriteLoop = false }, true));
            ctx.Check(unanswered is null && File.ReadAllBytes(shot).AsSpan().SequenceEqual(before), "Replace asks before replacing; no answer leaves the file as it was");
            var dialogs = ctx.Shell.Dialogs as Cairn.Ui.Services.DialogService;
            var previous = dialogs?.NonInteractiveChoice;
            try
            {
                if (dialogs is not null) dialogs.NonInteractiveChoice = (heading, buttons) => heading == "shot.wav already exists" ? buttons.ToList().IndexOf("Replace") : -1;
                var third = await module.ConvertDocumentAsync(again, new SndConvertChoice(SoundTarget.Folder, folder, new SoundConvertOptions { WriteLoop = false }, true));
                ctx.Check(third is { Count: 1 } && Path.GetFileName(third[0]) == "shot.wav" && !File.ReadAllBytes(shot).AsSpan().SequenceEqual(before), "answered Replace: the file is replaced");
            }
            finally { if (dialogs is not null) dialogs.NonInteractiveChoice = previous; }

            // the reviewer's case: a 24-bit master.wav converted next to itself with Replace ticked: written as
            // "master (converted).wav"; the source keeps its 24 bits and its LIST chunk
            string master = Path.Combine(folder, "master.wav");
            byte[] master24 = SyntheticSounds.Wav24(SyntheticSounds.Sine(3000), 1, 22050);
            File.WriteAllBytes(master, master24);
            if (ctx.Shell.OpenFile(master) && ctx.Shell.ActiveDocument is SndDocument masterDoc)
            {
                var next = await module.ConvertDocumentAsync(masterDoc, new SndConvertChoice(SoundTarget.NextToSource, folder, new SoundConvertOptions(), true));
                ctx.Check(next is { Count: 1 } && Path.GetFileName(next[0]) == "master (converted).wav" && File.ReadAllBytes(master).AsSpan().SequenceEqual(master24),
                    $"a 24-bit WAV converts next to itself as {(next is { Count: 1 } ? Path.GetFileName(next[0]) : "nothing")}; the source is untouched");
                ctx.Check(SoundDecoder.Decode(File.ReadAllBytes(master), "master.wav").SourceBits == 24, "the source is still 24-bit");
                ctx.Shell.Close(masterDoc);
            }
            else ctx.Check(false, "master.wav opens");
            // Save As writes a WAV; any other extension is refused
            string saved = Path.Combine(folder, "saved.wav");
            again.SaveTo(saved);
            ctx.Check(File.Exists(saved) && SoundDecoder.Decode(File.ReadAllBytes(saved), "saved.wav").FrameCount == again.Sound.FrameCount, "Save As writes a WAV");
            try { again.SaveTo(Path.Combine(folder, "x.vse")); ctx.Check(false, "Save As .vse is refused"); }
            catch (InvalidOperationException) { ctx.Check(!File.Exists(Path.Combine(folder, "x.vse")), "Save As .vse is refused (PS2 sounds are never written)"); }
            ctx.Check(SoundConversion.Notes(again.Sound, new SoundConvertOptions()).Any(n => n.Contains("lossy", StringComparison.Ordinal)), "the report says PS ADPCM was lossy already");
            ctx.Shell.Close(again);

            // Ogg Vorbis: every sample converts, decodes back with the same rate, channels and length, keeps its loop as
            // LOOPSTART/LOOPLENGTH comments and stays close to the source
            ctx.Check(OggVorbisWriter.IsAvailable, $"the Ogg Vorbis encoder loads ({OggVorbisWriter.Version ?? "missing"})");
            foreach (var (name, bytes, rate, channels, frames, loops) in Samples())
            {
                var doc = (SndDocument)module.Kind.OpenBytes(bytes, name, name + " (self-test)");
                ctx.Shell.AddDocument(doc);
                await ctx.SettleAsync();
                var options = new SoundConvertOptions { Format = SoundOutputFormat.Ogg, Quality = 0.5f };
                var written = await module.ConvertDocumentAsync(doc, new SndConvertChoice(SoundTarget.Folder, folder, options, Replace: false));
                if (!ctx.Check(written is { Count: 1 } && File.Exists(written[0]) && written[0].EndsWith(".ogg", StringComparison.Ordinal),
                    $"{name}: converted to {(written is { Count: 1 } ? Path.GetFileName(written[0]) : "nothing")}")) { ctx.Shell.Close(doc); continue; }
                var ogg = File.ReadAllBytes(written![0]);
                var back = SoundDecoder.Decode(ogg, Path.GetFileName(written[0]));
                double snr = Snr(doc.Sound.Samples, back.Samples);
                ctx.Check(back.Codec == "Vorbis" && back.SampleRate == rate && back.Channels == channels && back.FrameCount == frames && snr > 12,
                    $"{name}: the Ogg decodes to {back.SampleRate} Hz, {back.Channels} ch, {back.FrameCount} frames, SNR {snr:0.0} dB ({ogg.Length:N0} bytes from {frames * channels * 2:N0})");
                ctx.Check((back.Loop is not null) == loops && (!loops || (back.Loop!.Start == doc.Sound.Loop!.Start && back.Loop.End == doc.Sound.Loop.End)),
                    $"{name}: loop {(loops ? $"kept as LOOPSTART/LOOPLENGTH ({back.Loop?.Start}..{back.Loop?.End})" : "none")}");
                ctx.Shell.Close(doc);
            }
            var lossy = SoundConversion.Notes(SoundDecoder.Decode(SyntheticSounds.OneShotVse(400), "shot.vse"), new SoundConvertOptions { Format = SoundOutputFormat.Ogg });
            ctx.Check(lossy.Any(n => n.Contains("Ogg Vorbis (q5) is lossy too", StringComparison.Ordinal)) && lossy.Any(n => n.Contains("no Ogg equivalent", StringComparison.Ordinal)),
                "the Ogg report says the source and the Ogg are both lossy, and what the header loses");
        }
        finally { TryDelete(folder); }
    }

    /// <summary>Signal-to-noise ratio of <paramref name="decoded"/> against <paramref name="source"/> in dB.</summary>
    internal static double Snr(short[] source, short[] decoded)
    {
        double signal = 0, noise = 0;
        int n = Math.Min(source.Length, decoded.Length);
        for (int i = 0; i < n; i++) { double d = source[i] - decoded[i]; signal += (double)source[i] * source[i]; noise += d * d; }
        return noise <= 0 ? 99 : 10 * Math.Log10(signal / noise);
    }

    [SelfTest("snd.settings-page")]
    public static Task SettingsPage(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no sounds module in this build"); return Task.CompletedTask; }
        var page = module.SettingsPages.OfType<SndSettingsPage>().First();
        page.Load();
        ctx.Check(page.Title == "Sounds" && page.Format.Items.Count == 2 && page.Format.Items.OfType<ComboBoxItem>().All(i => i.IsEnabled),
            "Settings > Sounds offers WAV and Ogg Vorbis");
        ctx.Check(page.Quality.Value >= -1 && page.Quality.Value <= 10 && page.QualityText.Text.StartsWith('q'),
            $"the Ogg quality slider shows '{page.QualityText.Text}'");
        page.Quality.Value = 3;
        ctx.Check(page.QualityText.Text.StartsWith("q3, about 112 kbit/s", StringComparison.Ordinal), $"moving it updates the text ('{page.QualityText.Text}')");
        page.Load();
        ctx.Check(page.IntoPackfile.IsChecked == true || page.NextToSource.IsChecked == true || page.ToFolder.IsChecked == true, "one target is chosen");
        // Commit is not called: a diagnostic run never writes the user's settings
        return Task.CompletedTask;
    }

    [ScreenshotDialog("snd.convert")]
    public static Window? ConvertDialog(ScreenshotContext ctx)
    {
        if (ctx.Shell.Modules.OfType<SndModule>().FirstOrDefault() is not { } module) return null;
        var sound = Ps2Sound.Decode(SyntheticSounds.LoopingVse(), "amb_hum_loop.vse");
        var window = new SndConvertWindow(ctx.Shell.Dialogs, ["amb_hum_loop.vse"], "RF_PS2.VPP", Path.Combine(Path.GetTempPath(), "sounds"),
            module.Settings, o => SoundConversion.Notes(sound, o)) { Owner = ctx.MainWindow };
        window.Select(SoundOutputFormat.Wav);
        return window;
    }

    [ScreenshotDialog("snd.convert-ogg")]
    public static Window? ConvertDialogOgg(ScreenshotContext ctx)
    {
        if (ctx.Shell.Modules.OfType<SndModule>().FirstOrDefault() is not { } module) return null;
        var sound = Ps2Sound.Decode(SyntheticSounds.LoopingVse(), "amb_hum_loop.vse");
        var window = new SndConvertWindow(ctx.Shell.Dialogs, ["amb_hum_loop.vse"], "RF_PS2.VPP", Path.Combine(Path.GetTempPath(), "sounds"),
            module.Settings, o => SoundConversion.Notes(sound, o)) { Owner = ctx.MainWindow };
        window.Select(SoundOutputFormat.Ogg, 0.5f);
        return window;
    }

    /// <summary>The Convert window for a 16-bit WAV in the game directory: WAV not offered, next to the source off.</summary>
    [ScreenshotDialog("snd.convert-wav-source")]
    public static Window? ConvertDialogWavSource(ScreenshotContext ctx)
    {
        if (ctx.Shell.Modules.OfType<SndModule>().FirstOrDefault() is not { } module) return null;
        var sound = SoundDecoder.Decode(SoundWriter.Wav16(SyntheticSounds.Sine(4000), 1, 22050), "lsf.wav");
        var window = new SndConvertWindow(ctx.Shell.Dialogs, ["lsf.wav"], "dm-uac.vpp", null, module.Settings, o => SoundConversion.Notes(sound, o),
            f => SoundConversion.SameFormatReason(sound, f), "that is the game directory") { Owner = ctx.MainWindow };
        return window;
    }

    [SelfTest("snd.batch-ogg")]
    public static async Task BatchOgg(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no sounds module in this build"); return; }
        var samples = Samples();
        var entries = samples.Select(s => new Cairn.Ui.Modules.ArchiveBatchEntry(s.Name, s.Bytes.Length, () => s.Bytes))
            .Append(new Cairn.Ui.Modules.ArchiveBatchEntry("broken.vse", 3, () => [1, 2, 3])).ToList();
        var added = new List<(string Name, byte[] Bytes)>();
        string? label = null;
        var request = new Cairn.Ui.Modules.ArchiveBatchRequest("test.vpp", null, entries)
        {
            Interactive = false,
            Options = new SndConvertChoice(SoundTarget.IntoPackfile, null, new SoundConvertOptions { Format = SoundOutputFormat.Ogg, Quality = 0.3f }, false),
            AddFiles = (l, files, _) => { label = l; added.AddRange(files); return [.. files.Select(f => f.Name)]; },
        };
        string? summary = await module.ConvertAsync(request);
        // tone.wav and tone.aif both give tone.ogg: the .wav's is kept, the other left out and reported
        ctx.Check(summary is not null && summary.StartsWith($"Converted {samples.Count - 1} sounds to OGG; 1 left out", StringComparison.Ordinal)
            && summary.Contains("; 1 could not be converted", StringComparison.Ordinal), $"batch summary: {summary}");
        ctx.Check(module.LastBatch?.Any(r => r.SourceName == "tone.aif" && r.Skipped && r.Error!.Contains("tone.wav also makes tone.ogg", StringComparison.Ordinal)) == true,
            "tone.aif is left out: tone.wav makes tone.ogg");
        ctx.Check(label == $"Convert {samples.Count - 1} sounds to OGG" && added.Count == samples.Count - 1, $"one undo step '{label}' adds {added.Count} files");
        foreach (var ((name, bytes), source) in added.Zip(samples.Where(s => s.Name != "tone.aif")))
        {
            var back = SoundDecoder.Decode(bytes, name);
            ctx.Check(name.EndsWith(".ogg", StringComparison.Ordinal) && back.Codec == "Vorbis" && back.FrameCount == source.Frames && (back.Loop is not null) == source.Loops,
                $"{name}: {back.FrameCount} frames at {back.SampleRate} Hz, loop {(back.Loop is null ? "none" : $"{back.Loop.Start}..{back.Loop.End}")}");
        }
    }

    /// <summary>
    /// A packfile batch never replaces what is there without asking: x.wav + x.aif (as RED leaves them) with x.wav in the
    /// packfile, a damaged AIFF in the middle, and the same into a folder that already holds the names.
    /// </summary>
    [SelfTest("snd.batch-names")]
    public static async Task BatchNames(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no sounds module in this build"); return; }
        var tone = SyntheticSounds.Sine(2000, 37, 9000);
        byte[] wav = SoundWriter.Wav16(tone, 1, 22050), aif = SyntheticSounds.Aiff(tone, 1, 22050), bad = SyntheticSounds.Aiff(tone, 1, 22050);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(46), 0x00100000); // SSND data offset past the end
        Cairn.Ui.Modules.ArchiveBatchEntry E(string name, byte[] bytes) => new(name, bytes.Length, () => bytes);
        var selected = new[] { E("lsf.wav", wav), E("lsf.aif", aif), E("broken.aif", bad), E("shot.vse", SyntheticSounds.OneShotVse(200)) };
        var all = selected.Append(E("music.vmu", SyntheticSounds.Music(200))).ToList();

        // into the packfile as WAV: lsf.wav is a WAV already, lsf.aif's lsf.wav is the packfile's own lsf.wav (left alone),
        // broken.aif is listed, shot.wav is added
        var added = new List<string>();
        bool? replaced = null;
        var request = new Cairn.Ui.Modules.ArchiveBatchRequest("test.vpp", null, selected)
        {
            Interactive = false,
            Options = new SndConvertChoice(SoundTarget.IntoPackfile, null, new SoundConvertOptions(), false),
            AddFiles = (_, files, replace) => { replaced = replace; added.AddRange(files.Select(f => f.Name)); return [.. files.Select(f => f.Name)]; },
            AllEntries = all,
        };
        string? summary = await module.ConvertAsync(request);
        var results = module.LastBatch ?? [];
        ctx.Check(added.SequenceEqual(["shot.wav"]) && replaced == false, $"only shot.wav is added, nothing replaced ({string.Join(", ", added)})");
        ctx.Check(results.Any(r => r.SourceName == "lsf.wav" && r.Skipped && r.Error!.Contains("16-bit WAV already", StringComparison.Ordinal)), "lsf.wav: a WAV already, left out");
        ctx.Check(results.Any(r => r.SourceName == "lsf.aif" && r.Skipped && r.Error!.Contains("already has lsf.wav", StringComparison.Ordinal)), "lsf.aif: the packfile's lsf.wav is left as it is");
        ctx.Check(results.Any(r => r.SourceName == "broken.aif" && !r.Succeeded && !r.Skipped && r.Error!.Contains("past the end", StringComparison.Ordinal)),
            "broken.aif is listed as not converted; the batch goes on");
        ctx.Check(summary?.StartsWith("Converted 1 sound to WAV; 2 left out", StringComparison.Ordinal) == true && summary.Contains("1 could not be converted", StringComparison.Ordinal), $"summary: {summary}");

        // into a folder that already has shot.wav and lsf.wav: nothing is overwritten, the report says why
        string folder = TempFolder("batch-names");
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "shot.wav"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(folder, "lsf.wav"), [4, 5, 6]);
            var toFolder = new Cairn.Ui.Modules.ArchiveBatchRequest("test.vpp", folder, selected)
            {
                Interactive = false,
                Options = new SndConvertChoice(SoundTarget.Folder, folder, new SoundConvertOptions(), false),
            };
            summary = await module.ConvertAsync(toFolder);
            ctx.Check(File.ReadAllBytes(Path.Combine(folder, "shot.wav")).SequenceEqual(new byte[] { 1, 2, 3 }) && File.ReadAllBytes(Path.Combine(folder, "lsf.wav")).SequenceEqual(new byte[] { 4, 5, 6 })
                && Directory.GetFiles(folder).Length == 2, $"into a folder: nothing overwritten, no copies ({summary})");
            ctx.Check(module.LastBatch?.Count(r => r.Skipped && r.Error!.Contains("already has", StringComparison.Ordinal)) == 2, "both taken names are reported");
        }
        finally { TryDelete(folder); }
    }

    /// <summary>The waveOut player lets its device and thread go when playing stops (and after a long pause), and Dispose twice is harmless.</summary>
    [SelfTest("snd.player-release")]
    public static async Task PlayerRelease(SelfTestContext ctx)
    {
        var silence = new short[22050 / 4];
        var player = new WaveOutPlayer(silence, 1, 22050);
        var pausedRelease = WaveOutPlayer.PausedRelease;
        try
        {
            player.Play();
            if (player.Failure is { } failure) { ctx.Log($"  (no audio device: {failure}; release not checked)"); }
            else
            {
                ctx.Check(player.IsDeviceOpen && player.HasWorker, "playing opens the device and its worker");
                var until = DateTime.UtcNow.AddSeconds(3);
                while ((player.State != PlayerState.Stopped || player.IsDeviceOpen || player.HasWorker) && DateTime.UtcNow < until) await Task.Delay(50);
                ctx.Check(player.State == PlayerState.Stopped && !player.IsDeviceOpen && !player.HasWorker, "at the end the device and the worker thread are let go");
                player.Play();
                await Task.Delay(60);
                player.Stop();
                until = DateTime.UtcNow.AddSeconds(2);
                while ((player.IsDeviceOpen || player.HasWorker) && DateTime.UtcNow < until) await Task.Delay(20);
                ctx.Check(!player.IsDeviceOpen && !player.HasWorker, "Stop lets them go too");
                WaveOutPlayer.PausedRelease = TimeSpan.FromMilliseconds(150);
                player.Play();
                await Task.Delay(80);
                player.Pause();
                long at = player.Position;
                until = DateTime.UtcNow.AddSeconds(2);
                while (player.IsDeviceOpen && DateTime.UtcNow < until) await Task.Delay(20);
                ctx.Check(!player.IsDeviceOpen && player.State == PlayerState.Paused && player.Position == at, $"a long pause lets the device go and keeps the place ({at})");
                player.Play();
                ctx.Check(player.IsDeviceOpen && player.State == PlayerState.Playing, "playing on reopens it");
                player.Stop();
            }
        }
        finally
        {
            WaveOutPlayer.PausedRelease = pausedRelease;
            player.Dispose();
        }
        try { player.Dispose(); player.Seek(10); player.Play(); ctx.Check(true, "a second Dispose (and use after it) does nothing"); }
        catch (Exception ex) { ctx.Check(false, $"a second Dispose throws {ex.GetType().Name}"); }
    }

    [SelfTest("snd.convert-dialog")]
    public static Task ConvertDialogChoices(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("no sounds module in this build"); return Task.CompletedTask; }
        var sound = Ps2Sound.Decode(SyntheticSounds.LoopingVse(), "amb_hum_loop.vse");
        var window = new SndConvertWindow(ctx.Shell.Dialogs, ["amb_hum_loop.vse"], null, Path.GetTempPath(), module.Settings, o => SoundConversion.Notes(sound, o));
        try
        {
            window.Select(SoundOutputFormat.Ogg, 0.7f);
            var ogg = window.Current();
            ctx.Check(ogg is { Options.Format: SoundOutputFormat.Ogg } && Math.Abs(ogg.Options.Quality - 0.7f) < 1e-4,
                $"Ogg chosen at {ogg?.Options.Quality:0.0}: the choice carries format and quality");
            ctx.Check(window.Current() is { } c && SoundConversion.OutputName("amb_hum_loop.vse", c.Options.Format) == "amb_hum_loop.ogg", "the output is named .ogg (lower case)");
            window.Select(SoundOutputFormat.Wav);
            ctx.Check(window.Current() is { Options.Format: SoundOutputFormat.Wav }, "WAV chosen again");
            window.Select(SoundOutputFormat.Ogg, 5f);
            ctx.Check(Math.Abs(window.Quality - 1.0f) < 1e-4, $"a quality past the end is held at q10 ({window.Quality:0.0})");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }
}
