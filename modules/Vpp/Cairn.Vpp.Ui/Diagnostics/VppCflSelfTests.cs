using System.Text;
using System.Windows;
using Cairn.Formats.XForge;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Vpp.XForge;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// X-Forge CFL3 archives (<c>Cairn.exe --probe-module --selftest-only vpp.cfl*</c>): a synthetic archive opened through
/// the shell (read only; stored and zlib entries listed, previewed, extracted byte-exact; entries that cannot be read
/// listed with the reason and skipped; a work copy resolves textures from the same archive), and Red Faction for
/// N-Gage's <c>redfaction.cfl</c> when it is configured (only read).
/// </summary>
public static class VppCflSelfTests
{
    private static string NewFolder() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairn-cfl-test-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private static object? PreviewView(VppPreviewArea area) => (area.Preview.View as ModulePreviewHost)?.Inner ?? area.Preview.View;

    /// <summary>A small 32-bit uncompressed TGA (bottom-up).</summary>
    internal static byte[] Tga(int width, int height)
    {
        var b = new byte[18 + width * height * 4];
        b[2] = 2;
        b[12] = (byte)width;
        b[14] = (byte)height;
        b[16] = 32;
        b[17] = 8;
        for (int i = 18; i < b.Length; i += 4) { b[i] = 40; b[i + 1] = (byte)(i % 200); b[i + 2] = 180; b[i + 3] = 255; }
        return b;
    }

    /// <summary>A short 8-bit mono 11,025 Hz PCM sound, as the N-Gage sounds are.</summary>
    internal static byte[] Wav(int samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(11025); w.Write(11025); w.Write((short)1); w.Write((short)8);
        w.Write("data"u8); w.Write(samples);
        for (int i = 0; i < samples; i++) w.Write((byte)(128 + (int)(60 * Math.Sin(i * 0.2))));
        w.Flush();
        return ms.ToArray();
    }

    [SelfTest("vpp.cfl-synthetic")]
    public static async Task Synthetic(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        string root = NewFolder();
        VppDocument? doc = null;
        var opened = new List<IDocument>();
        try
        {
            byte[] text = Encoding.Latin1.GetBytes("// Café script\r\nSOUND \"snd_alert.wav\"\r\nEND\r\n");
            byte[] tga = Tga(16, 8), wav = Wav(2000), xff = Encoding.ASCII.GetBytes("not a real scene: names txtr_crate.tga");
            string path = Path.Combine(root, "redfaction.cfl");
            File.WriteAllBytes(path, SyntheticCfl.Build(
                new SyntheticCflEntry("C01_msg.txt", text, CflArchive.Stored),
                new SyntheticCflEntry("txtr_crate.tga", tga),
                new SyntheticCflEntry("snd_alert.wav", wav),
                new SyntheticCflEntry("level_01a.xff", xff),
                new SyntheticCflEntry("cipher.bin", text, 4),
                new SyntheticCflEntry("bad.tga", tga) { ListedSize = 3 }));
            var stamp = VppArchiveStamp.Of(path);

            ctx.Check(ctx.Shell.OpenFile(path), "the shell opens a .cfl");
            doc = ctx.Shell.ActiveDocument as VppDocument;
            if (doc?.Current.Cfl is not { } cfl) { ctx.Check(false, "... as an X-Forge CFL archive"); return; }
            opened.Add(doc);
            ctx.Check(doc.Kind.Id == "vpp.cfl" && doc.Kind.Extensions.SequenceEqual([".cfl"]) && ReferenceEquals(doc.Kind, module.CflKind), "its document kind is the CFL archive kind");
            ctx.Check(module.DocumentKinds.Contains(module.CflKind) && module.CanOpenArchive(path), "the module lists the kind and opens .cfl archives for work copies");
            ctx.Check(doc.IsReadOnly && !((IDocument)doc).CanSave && !doc.IsDirty, "read only: Save and Save As are off");
            ctx.Check(doc.Current.Items.Select(i => i.Name).SequenceEqual(["C01_msg.txt", "txtr_crate.tga", "snd_alert.wav", "level_01a.xff", "cipher.bin", "bad.tga"]),
                "every entry is listed under its own name: " + string.Join(", ", doc.Current.Items.Select(i => i.Name)));
            var view = (VppDocumentView)doc.View;
            ctx.Check(view.CflBannerText?.Contains("read only", StringComparison.Ordinal) == true && view.CflBannerText.Contains("2 files cannot be read", StringComparison.Ordinal),
                "the banner says it is read only and that 2 files cannot be read: " + view.CflBannerText);
            ctx.Check(view.Rf2BannerText is null && view.CacheBannerText is null && view.Ps2BannerText is null, "no other archive's banner");
            ctx.Check(view.VisibleEditTools.Count == 0, "no Add/Remove/Rename on the toolbar: " + string.Join(", ", view.VisibleEditTools));
            ctx.Check(doc.ReadOnlyReason.Contains("CFL", StringComparison.Ordinal), "the read-only reason names a CFL archive: " + doc.ReadOnlyReason);
            var codes = doc.Problems.Select(p => $"{p.Code} {p.EntryName}").ToList();
            ctx.Check(codes.Contains("CFL001 cipher.bin") && codes.Contains("CFL002 bad.tga") && doc.Problems.All(p => p.Code.StartsWith("CFL", StringComparison.Ordinal)),
                "the archive's own rules: compression not supported and corrupt, no packfile rule: " + string.Join("; ", codes));
            ctx.Check(cfl.StoredCount == 1 && cfl.ZlibCount == 4 && cfl.UnsupportedCount == 1 && cfl.CorruptCount == 1, $"counts: {cfl.StoredCount} stored, {cfl.ZlibCount} zlib, {cfl.UnsupportedCount} unsupported, {cfl.CorruptCount} corrupt");

            // nothing changes it
            ctx.Check(!doc.ApplyEdit("probe", p => Editing.VppEdit.Remove(p, ["C01_msg.txt"])), "an edit is refused");
            ctx.Check(await doc.Commands.AddPathsAsync([path]) is null, "adding files is refused");
            ctx.Check(doc.Commands.Rename("C01_msg.txt", "other.txt") is not null, "renaming is refused");
            doc.SelectNames(["C01_msg.txt"]);
            doc.Commands.RemoveSelected();
            ctx.Check(!ctx.Shell.Save(doc), "File > Save does nothing");
            ctx.Check(doc.Current.Count == 6 && !doc.IsDirty && !doc.CanUndo, "the archive is unchanged");

            // extraction unpacks byte-exact; the entries that cannot be read are skipped; Overwrite replaces the files
            string outFolder = Path.Combine(root, "out");
            var written = await doc.Commands.ExtractAsync(doc.Current.Items, outFolder, Commands.VppOverwritePolicy.Overwrite, quiet: true);
            ctx.Check(written?.Count == 4 && !File.Exists(Path.Combine(outFolder, "cipher.bin")) && !File.Exists(Path.Combine(outFolder, "bad.tga")),
                $"Extract writes the 4 readable entries and skips the 2 others ({written?.Count})");
            ctx.Check(File.ReadAllBytes(Path.Combine(outFolder, "C01_msg.txt")).AsSpan().SequenceEqual(text)
                && File.ReadAllBytes(Path.Combine(outFolder, "txtr_crate.tga")).AsSpan().SequenceEqual(tga)
                && File.ReadAllBytes(Path.Combine(outFolder, "snd_alert.wav")).AsSpan().SequenceEqual(wav), "... byte for byte, stored and zlib");
            File.WriteAllBytes(Path.Combine(outFolder, "txtr_crate.tga"), [1, 2, 3]);
            written = await doc.Commands.ExtractAsync([doc.Current.Find("txtr_crate.tga")!], outFolder, Commands.VppOverwritePolicy.Overwrite, quiet: true);
            ctx.Check(written?.Count == 1 && File.ReadAllBytes(Path.Combine(outFolder, "txtr_crate.tga")).AsSpan().SequenceEqual(tga) && Directory.GetFiles(outFolder).Length == 4,
                "Overwrite all replaces an existing file instead of keeping both");

            // Open in Cairn's work copy: linked to the archive, read only for other modules, and its textures come from the archive
            string workCopy = await doc.Work.PrepareAsync(doc.Current.Find("level_01a.xff")!, doc.Current);
            ctx.Check(File.ReadAllBytes(workCopy).AsSpan().SequenceEqual(xff), "a work copy holds the unpacked entry");
            ctx.Check(module.ArchiveEntryOf(workCopy) is { EntryName: "level_01a.xff" } && module.ArchiveOf(workCopy) is null,
                "the work copy knows its entry, but other modules are not offered to add files to the archive");
            ctx.Check(module.SourceKeyOf(workCopy) == doc.Id, "... and names the open archive as its source");
            var resolver = AssetSiblings.ResolverForDocument(ctx.Shell, workCopy);
            var texture = resolver.Resolve("TXTR_CRATE.TGA");
            ctx.Check(texture?.Origin == "redfaction.cfl" && texture.ReadAllBytes().AsSpan().SequenceEqual(tga),
                $"a file opened from the archive resolves its texture from the same archive ({texture?.DisplayLocation})");
            ctx.Check(resolver.ProbeImage("txtr_crate.tga") is { Width: 16, Height: 8 }, "... and the texture decodes");
            ctx.Check(resolver.Resolve("bad.tga") is { } bad && !CanRead(bad), "an entry that cannot be read is found by name but reads nothing");

            // the archive as a session source (the library and the preview pickers)
            var source = AssetSiblings.OpenSources(ctx.Shell).FirstOrDefault(s => s.Key == doc.Id);
            ctx.Check(source is { IsRedFactionII: false, ArchivePath: { } p } && p == path && source.Entries.Contains("txtr_crate.tga")
                && source.Entries.Read("txtr_crate.tga")?.AsSpan().SequenceEqual(tga) == true && source.Entries.ReadHead("snd_alert.wav", 4)?.AsSpan().SequenceEqual("RIFF"u8) == true,
                "the open archive is offered as a source with its unpacked entries");
            ctx.Check(source?.Entries.Read("cipher.bin") is null, "... an entry that cannot be read reads as none");

            // the context menu: Open in Cairn and Convert to for what modules read; neither for what cannot be read
            var list = view.FileList;
            doc.SelectNames(["txtr_crate.tga"]);
            list.RefreshContextMenu();
            bool converts = AssetConversions.TargetsFor(ctx.Shell.Modules, "txtr_crate.tga").Count > 0;
            ctx.Check((list.ContextMenuConvertToItem?.Visibility == Visibility.Visible) == converts, $"the Convert to submenu is there for the .tga exactly when a module converts it ({converts})");
            doc.SelectNames(["cipher.bin"]);
            list.RefreshContextMenu();
            ctx.Check(list.ContextMenuConvertToItem?.Visibility != Visibility.Visible && !doc.Commands.CanOpenInCairn, "... and not for an entry that cannot be read");

            // previews and details
            var window = new Window { Width = 700, Height = 600, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000, Top = -10000 };
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            window.Show();
            try
            {
                var tgaItem = doc.Current.Find("txtr_crate.tga")!;
                area.Show(tgaItem, [tgaItem], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(PreviewView(area) is ImagePreview, $"the .tga previews as an image ({area.Preview.Kind}, {PreviewView(area)?.GetType().Name})");
                string Row(string label) => area.Details.Rows.FirstOrDefault(r => r.Label == label)?.Value ?? "(none)";
                var entry = cfl.EntryOf(tgaItem)!;
                ctx.Check(Row("Compression") == "zlib" && Row("Unpacked size").StartsWith(tga.Length.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
                    && Row("Packed size").StartsWith(entry.PackedSize.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
                    && Row("Offset").StartsWith(entry.Offset.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal),
                    $"its details show the compression, packed and unpacked size and offset ({Row("Compression")}; {Row("Packed size")}; {Row("Unpacked size")}; {Row("Offset")})");
                ctx.Check(Row("Game").Contains("N-Gage", StringComparison.Ordinal), "... and that it is Red Faction for N-Gage's: " + Row("Game"));

                var txt = doc.Current.Find("C01_msg.txt")!;
                area.Show(txt, [txt], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(PreviewView(area) is TextPreview && Row("Compression") == "stored", $"the stored .txt previews as text ({area.Preview.Kind}, {Row("Compression")})");
                var sound = doc.Current.Find("snd_alert.wav")!;
                area.Show(sound, [sound], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(PreviewView(area) is AudioPreview, $"the .wav previews as a sound ({area.Preview.Kind})");
                var cipher = doc.Current.Find("cipher.bin")!;
                area.Show(cipher, [cipher], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(area.Details.Rows.Any(r => r.Label == "Compression" && r.Value == "compression 4 (not supported)" && r.Flagged)
                    && area.Details.Rows.Any(r => r.Label == "Problems" && r.Value.Contains("CFL001", StringComparison.Ordinal)), "an unsupported compression is flagged by number, with its problem");

                area.Show(null, [], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(Row("Format") == "X-Forge CFL3 (Red Faction for N-Gage)" && Row("Entries") == "6" && Row("Stored") == "1" && Row("zlib") == "4"
                    && Row("Unsupported").StartsWith('1') && Row("Corrupt").StartsWith('1') && Row("Packed size") != "(none)" && Row("Unpacked size") != "(none)",
                    $"with nothing selected, the details show the archive's facts ({Row("Format")}; {Row("Entries")} entries, {Row("Stored")} stored, {Row("zlib")} zlib)");
            }
            finally
            {
                area.Dispose();
                window.Close();
            }

            // probing refuses a file that is not a CFL archive, with the reason
            string fake = Path.Combine(root, "notes.cfl");
            File.WriteAllText(fake, "a flowchart from another program");
            try
            {
                module.CflKind.Open(fake);
                ctx.Check(false, "a .cfl that is not an archive is refused");
            }
            catch (Cairn.Formats.AssetFormatException ex)
            {
                ctx.Check(ex.Message.Contains("is not an X-Forge CFL archive", StringComparison.Ordinal), "a .cfl that is not an archive is refused: " + ex.Message);
            }
            ctx.Check(VppArchiveStamp.Of(path) == stamp, "the archive on disk was never written");
        }
        finally
        {
            foreach (var d in opened) if (ctx.Shell.Documents.Contains(d)) ctx.Shell.Close(d);
            Work.VppWorkFolder.TryDeleteFolder(root);
        }
    }

    private static bool CanRead(Cairn.Assets.AssetLocation location)
    {
        try { location.ReadAllBytes(); return true; }
        catch (IOException) { return false; }
    }

    [SelfTest("vpp.cfl-listed-twice")]
    public static async Task ListedTwice(SelfTestContext ctx)
    {
        string root = NewFolder();
        VppDocument? doc = null;
        try
        {
            // a name listed twice with different data: each row's work copy (Open in Cairn, Open with) holds its own data
            byte[] first = Encoding.ASCII.GetBytes("FIRST COPY\r\n"), second = Encoding.ASCII.GetBytes("SECOND COPY, other data\r\n");
            string path = Path.Combine(root, "dup.cfl");
            File.WriteAllBytes(path, SyntheticCfl.Build(new SyntheticCflOptions { DirectoryCompression = CflArchive.Stored },
                new SyntheticCflEntry("C01_msg.txt", first, CflArchive.Stored), new SyntheticCflEntry("C01_msg.txt", second, CflArchive.Stored)));
            ctx.Check(ctx.Shell.OpenFile(path), "the shell opens an archive that lists a name twice");
            doc = ctx.Shell.ActiveDocument as VppDocument;
            if (doc?.Current.Cfl is null) { ctx.Check(false, "... as an X-Forge CFL archive"); return; }
            var rows = doc.Current.Items;
            ctx.Check(rows.Length == 2 && rows.All(i => i.Name == "C01_msg.txt"), "both rows are listed under the one name");
            string copy1 = await doc.Work.PrepareAsync(rows[0], doc.Current);
            string copy2 = await doc.Work.PrepareAsync(rows[1], doc.Current);
            ctx.Check(File.ReadAllBytes(copy1).AsSpan().SequenceEqual(first), "the first row's work copy holds the first copy: " + Path.GetFileName(copy1));
            ctx.Check(copy2 != copy1 && File.ReadAllBytes(copy2).AsSpan().SequenceEqual(second),
                $"the second row's work copy holds the second copy, not the first's ({Path.GetFileName(copy2)}: \"{File.ReadAllText(copy2).Trim()}\")");
            ctx.Check(Path.GetFileName(copy2) == "C01_msg (2).txt", "... beside the first, as name (2): " + Path.GetFileName(copy2));
            ctx.Check(await doc.Work.PrepareAsync(rows[0], doc.Current) == copy1 && await doc.Work.PrepareAsync(rows[1], doc.Current) == copy2,
                "opening a row again reuses its own work copy");
        }
        finally
        {
            if (doc is not null && ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc);
            Work.VppWorkFolder.TryDeleteFolder(root);
        }
    }

    [SelfTest("vpp.cfl-check-budget")]
    public static void CheckBudget(SelfTestContext ctx)
    {
        string root = NewFolder();
        VppDocument? doc = null;
        try
        {
            // a 65 MiB entry in a file of about 66 KB passes what the open unpacks to check it (64 MiB for a small file)
            string path = Path.Combine(root, "big.cfl");
            File.WriteAllBytes(path, SyntheticCfl.Build(
                new SyntheticCflEntry("first.txt", Encoding.ASCII.GetBytes("checked\r\n")),
                new SyntheticCflEntry("big.tga", new byte[65 << 20]),
                new SyntheticCflEntry("after.txt", Encoding.ASCII.GetBytes("not checked\r\n"), CflArchive.Stored)));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ctx.Check(ctx.Shell.OpenFile(path), "the shell opens an archive that lists more than it may unpack to check it");
            doc = ctx.Shell.ActiveDocument as VppDocument;
            if (doc?.Current.Cfl is not { } cfl) { ctx.Check(false, "... as an X-Forge CFL archive"); return; }
            ctx.Log($"  opened in {clock.ElapsedMilliseconds:N0} ms");
            ctx.Check(cfl.NotChecked.SequenceEqual([1, 2]) && cfl.Problems.Count == 0, $"the checks stop at the entry that passes the budget ({string.Join(", ", cfl.NotChecked)})");
            ctx.Check(doc.Problems.Any(p => p.Code == "CFL005" && p.EntryName == "big.tga" && p.Severity == Validation.VppSeverity.Warning),
                "a CFL005 warning names the first entry not checked: " + string.Join("; ", doc.Problems.Select(p => p.Code + " " + p.EntryName)));
            var view = (VppDocumentView)doc.View;
            ctx.Check(view.CflBannerText?.Contains("2 files were not checked", StringComparison.Ordinal) == true, "the banner says so: " + view.CflBannerText);
            VppInfoColumnSelfTests.WaitForInfo(doc);
            ctx.Check(VppInfoColumnSelfTests.InfoOf(doc, "big.tga") == "not checked" && VppInfoColumnSelfTests.InfoOf(doc, "after.txt") == "not checked",
                $"the Info column shows them as not checked, without unpacking them ({VppInfoColumnSelfTests.InfoOf(doc, "big.tga")}; {VppInfoColumnSelfTests.InfoOf(doc, "after.txt")})");
            ctx.Check(doc.Current.Find("after.txt")!.Source.ReadAll().AsSpan().SequenceEqual("not checked\r\n"u8), "an entry not checked still reads when opened");
        }
        finally
        {
            if (doc is not null && ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc);
            Work.VppWorkFolder.TryDeleteFolder(root);
        }
    }

    [SelfTest("vpp.cfl-ngage", Order = 310)]
    public static async Task Ngage(SelfTestContext ctx)
    {
        string? dir = LocalPaths.NgageDirectory;
        string? path = dir is null ? null : Path.Combine(dir, "redfaction.cfl");
        if (path is null || !File.Exists(path)) { ctx.Skip("no Red Faction for N-Gage archive (" + LocalPaths.HowToSet(LocalPaths.NgageDirectoryVariable, "ngageDirectory") + ")"); return; }
        var stamp = VppArchiveStamp.Of(path);
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        string temp = NewFolder();
        VppDocument? doc = null;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ctx.Check(ctx.Shell.OpenFile(path), "the shell opens redfaction.cfl");
            doc = ctx.Shell.ActiveDocument as VppDocument;
            if (doc?.Current.Cfl is not { } cfl) { ctx.Check(false, "... as an X-Forge CFL archive"); return; }
            ctx.Log($"  opened in {clock.ElapsedMilliseconds:N0} ms: {cfl.Archive.Entries.Count:N0} entries, {cfl.StoredCount:N0} stored, {cfl.ZlibCount:N0} zlib, {doc.Current.DataBytes:N0} bytes unpacked");
            ctx.Check(doc.Current.Count == 1205 && cfl.Problems.Count == 0, $"1,205 entries, every one unpacks to its size ({doc.Current.Count:N0}, {cfl.Problems.Count:N0} unreadable)");
            ctx.Check(doc.Problems.All(p => p.Code == "CFL003") && cfl.ListedTwice.Count == 9, $"the only problems are the 9 names listed twice ({cfl.ListedTwice.Count}: {string.Join(", ", cfl.ListedTwice.Take(3).Select(t => t.Text))}...)");

            var window = new Window { Width = 900, Height = 700, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000, Top = -10000 };
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            window.Show();
            try
            {
                string Row(string label) => area.Details.Rows.FirstOrDefault(r => r.Label == label)?.Value ?? "(none)";
                area.Show(null, [], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(Row("Format") == "X-Forge CFL3 (Red Faction for N-Gage)" && Row("Stored") == cfl.StoredCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
                    && area.Details.Rows.Any(r => r.Section == "Types" && r.Label == ".pcx"), $"the archive's facts ({Row("Entries")} entries, {Row("Packed size")} packed, {Row("Unpacked size")} unpacked)");
                var tga = doc.Current.Items.First(i => i.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));
                area.Show(tga, [tga], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(PreviewView(area) is ImagePreview && Row("Compression") == "zlib", $"{tga.Name} previews as an image ({area.Preview.Kind}, {Row("Compression")}, {Row("Packed size")})");
                var txt = doc.Current.Items.First(i => i.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && cfl.EntryOf(i)!.Compression == CflArchive.Stored);
                area.Show(txt, [txt], doc.Current, immediate: true);
                await area.SettleAsync();
                ctx.Check(PreviewView(area) is TextPreview && Row("Compression") == "stored", $"{txt.Name} previews as text ({area.Preview.Kind}, {Row("Compression")})");
            }
            finally
            {
                area.Dispose();
                window.Close();
            }

            // extract one entry: the unpacked bytes, as the archive reader gives them
            var one = doc.Current.Items.First(i => i.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            var written = await doc.Commands.ExtractAsync([one], temp, Commands.VppOverwritePolicy.Overwrite, quiet: true);
            byte[] expected;
            using (var stream = File.OpenRead(path)) expected = cfl.Archive.ReadEntry(stream, cfl.EntryOf(one)!);
            ctx.Check(written?.Count == 1 && File.ReadAllBytes(written[0]).AsSpan().SequenceEqual(expected) && expected.AsSpan(0, 4).SequenceEqual("RIFF"u8),
                $"extracting {one.Name} writes its unpacked bytes ({expected.Length:N0})");

            // a scene opened from the archive finds its textures in the archive
            var scene = doc.Current.Items.First(i => i.Name.EndsWith(".xff", StringComparison.OrdinalIgnoreCase));
            string workCopy = await doc.Work.PrepareAsync(scene, doc.Current);
            var texture = doc.Current.Items.First(i => i.Name.EndsWith(".pcx", StringComparison.OrdinalIgnoreCase));
            var found = AssetSiblings.ResolverForDocument(ctx.Shell, workCopy).Resolve(texture.Name.ToUpperInvariant());
            ctx.Check(found?.Origin == "redfaction.cfl" && found.ReadAllBytes().Length == texture.Size,
                $"{scene.Name}'s work copy resolves {texture.Name} from the archive ({found?.DisplayLocation})");

            // the two copies of a name listed twice with different data each open their own data
            var twice = cfl.ListedTwice.First(t => !t.SameData);
            var copies = doc.Current.Items.Where(i => string.Equals(i.Name, twice.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            string copyA = await doc.Work.PrepareAsync(copies[0], doc.Current), copyB = await doc.Work.PrepareAsync(copies[1], doc.Current);
            ctx.Check(copyA != copyB && new FileInfo(copyA).Length == copies[0].Size && new FileInfo(copyB).Length == copies[1].Size
                && !File.ReadAllBytes(copyA).AsSpan().SequenceEqual(File.ReadAllBytes(copyB)),
                $"{twice.Name}: each row's work copy holds its own copy ({Path.GetFileName(copyA)} {copies[0].Size:N0} bytes, {Path.GetFileName(copyB)} {copies[1].Size:N0} bytes)");
        }
        finally
        {
            if (doc is not null && ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc);
            Work.VppWorkFolder.TryDeleteFolder(temp);
        }
        ctx.Check(VppArchiveStamp.Of(path) == stamp, "the archive was never written");
    }
}
