using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Windows.Controls;
using Cairn.Ui.Diagnostics;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.List;
using Cairn.Vpp.Writing;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// The entry list's Info column (<c>Cairn.exe --selftest-only vpp.info-column</c>): a packfile of small generated
/// files of several types is saved to a temporary folder and opened; the column must fill in the background with each
/// type's one-line summary, sort, and follow replace / add / rename / undo.
/// </summary>
public static class VppInfoColumnSelfTests
{
    private static byte[] Tga(int type, int depth, int size)
    {
        var b = new byte[18 + 64];
        b[2] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), (ushort)size);
        b[16] = (byte)depth;
        return b;
    }

    private static byte[] Dds(int size, int mips)
    {
        var b = new byte[128 + 64];
        "DDS "u8.CopyTo(b);
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        U32(4, 124); U32(8, 0x21007); U32(12, (uint)size); U32(16, (uint)size); U32(28, (uint)mips);
        U32(76, 32); U32(80, 4); "DXT1"u8.CopyTo(b.AsSpan(84)); U32(108, 0x401008);
        return b;
    }

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

    /// <summary>A level with only a header and a level info section (all the Info column reads).</summary>
    private static byte[] Level(string name, string author, uint timestamp)
    {
        var s = new MemoryStream();
        var w = new BinaryWriter(s);
        void Str(string v) { var bytes = Encoding.Latin1.GetBytes(v); w.Write((ushort)bytes.Length); w.Write(bytes); }
        w.Write(0xD4BADA55); w.Write(200); w.Write(timestamp); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        Str(name); Str("");
        var info = new MemoryStream();
        var iw = new BinaryWriter(info);
        iw.Write(1);
        foreach (var v in new[] { name, author, "a date" }) { var bytes = Encoding.Latin1.GetBytes(v); iw.Write((ushort)bytes.Length); iw.Write(bytes); }
        iw.Write((byte)0); iw.Write((byte)1);
        w.Write(0x01000000); w.Write((int)info.Length); w.Write(info.ToArray());
        w.Write(0); w.Write(0);
        return s.ToArray();
    }

    private static void WaitForInfo(VppDocument doc)
    {
        var clock = Stopwatch.StartNew();
        while (doc.List.InfoCache is { IsFilling: true } && clock.ElapsedMilliseconds < 20_000) SelfTestPump.Pump(30);
        SelfTestPump.Pump(50); // the last batch is applied on the UI thread
    }

    private static string InfoOf(VppDocument doc, string name) =>
        doc.List.AllRows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))?.InfoText ?? "(no row)";

    [SelfTest("vpp.info-column")]
    public static void InfoColumn(SelfTestContext ctx)
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-vpp-info-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        VppDocument? doc = null;
        try
        {
            const uint saved = 1_700_000_000; // 2023-11-14 22:13:20 UTC
            var p = VppEdit.AddSources(VppPackage.Empty,
            [
                ("level.rfl", new MemorySource(Level("Info Test", "Cairn", saved))),
                ("wall.tga", new MemorySource(Tga(10, 32, 256))),
                ("rock.dds", new MemorySource(Dds(512, 10))),
                ("boom.wav", new MemorySource(Wave(22050, 2))),
                ("weapons.tbl", new MemorySource(Encoding.Latin1.GetBytes("#Primary Weapons\r\n$Name: \"a\"\r\n$Name: \"b\"\r\n#End\r\n"))),
                ("broken.v3m", new MemorySource([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16])),
                ("notes.xyz", new MemorySource([1, 2, 3])),
            ], VppClashPolicy.KeepBoth).Package;
            string path = Path.Combine(folder, "info.vpp");
            VppSaver.Save(p, path, null, CancellationToken.None, VppSaveOptions.Default);

            doc = (VppDocument)module.Kind.Open(path);
            ctx.Shell.AddDocument(doc);
            var clock = Stopwatch.StartNew();
            WaitForInfo(doc);
            ctx.Log($"filled {doc.List.AllRows.Count} rows in {clock.ElapsedMilliseconds} ms");

            string level = VppInfo.LevelLine("Info Test", "Cairn", DateTimeOffset.FromUnixTimeSeconds(saved), null, TimeZoneInfo.Local);
            var expected = new Dictionary<string, string>
            {
                ["level.rfl"] = level,
                ["wall.tga"] = "256x256, 32-bit, RLE compressed",
                ["rock.dds"] = "512x512, DXT1, 10 mipmaps",
                ["boom.wav"] = "22,050 Hz, 16-bit mono, 2.0 s",
                ["weapons.tbl"] = "weapons table, 2 entries",
                ["broken.v3m"] = VppInfo.UnreadableText,
                ["notes.xyz"] = "",
            };
            foreach (var (name, text) in expected) ctx.Check(InfoOf(doc, name) == text, $"{name}: '{InfoOf(doc, name)}'");
            var broken = doc.List.AllRows.First(r => r.Name == "broken.v3m");
            ctx.Check(broken.InfoIsUnreadable && broken.InfoToolTip?.StartsWith(VppInfo.UnreadableText + ": ", StringComparison.Ordinal) == true,
                $"unreadable data is marked, the reason in the tooltip ({broken.InfoToolTip})");
            ctx.Check(doc.List.AllRows.First(r => r.Name == "level.rfl").InfoToolTip == level, "the tooltip holds the full text");

            // the column: last, titled Info, stretched to the list's right edge
            SelfTestPump.Pump(100);
            var view = (VppDocumentView)doc.View;
            var grid = (GridView)view.FileList.List.View;
            var info = view.FileList.InfoColumn;
            ctx.Check(grid.Columns[^1] == info && (info.Header as string)?.StartsWith("Info", StringComparison.Ordinal) == true, "Info is the last column");
            double total = grid.Columns.Sum(c => c.ActualWidth);
            double listWidth = view.FileList.List.ActualWidth;
            ctx.Log($"columns {total:0} px wide in a {listWidth:0} px list; Info {info.ActualWidth:0} px");
            double others = total - info.ActualWidth;
            bool fits = listWidth - others - 6 >= 160;
            ctx.Check(fits ? total <= listWidth && total >= listWidth - 30 : Math.Abs(info.ActualWidth - 160) < 1,
                fits ? "Info stretches over the rest of the width" : "Info keeps its 160 px minimum in a narrow list (the list scrolls sideways)");

            // sorting by Info (numbers by value), then back
            doc.List.SortBy(VppListSort.Info, descending: false);
            var order = doc.List.Visible.Select(r => r.Name).ToList();
            ctx.Check(order.Take(3).SequenceEqual(["wall.tga", "rock.dds", "boom.wav"]) && order[^1] == "notes.xyz",
                $"sorted by Info, numbers by value, empty last: {string.Join(", ", order)}");
            doc.List.SortBy(VppListSort.PackfileOrder, descending: false);

            // replace: the entry's new data is read again; the others keep their lines without a new read
            string replacement = Path.Combine(folder, "wall_new.tga");
            File.WriteAllBytes(replacement, Tga(2, 24, 128));
            doc.ApplyEdit("Replace wall.tga", q => VppEdit.Replace(q, "wall.tga", FileSource.FromFile(replacement)));
            ctx.Check(InfoOf(doc, "rock.dds") == "512x512, DXT1, 10 mipmaps", "unchanged entries keep their line at once");
            WaitForInfo(doc);
            ctx.Check(InfoOf(doc, "wall.tga") == "128x128, 24-bit, uncompressed", $"a replaced entry is read again ({InfoOf(doc, "wall.tga")})");

            // an added file is read from disk
            string added = Path.Combine(folder, "ding.wav");
            File.WriteAllBytes(added, Wave(11025, 1));
            doc.ApplyEdit("Add ding.wav", q => VppEdit.AddFiles(q, [added], VppClashPolicy.KeepBoth).Package);
            WaitForInfo(doc);
            ctx.Check(InfoOf(doc, "ding.wav") == "11,025 Hz, 16-bit mono, 1.0 s", $"an added file gets its line ({InfoOf(doc, "ding.wav")})");

            // renamed to another type: the content decides, and says so
            doc.ApplyEdit("Rename rock.dds", q => VppEdit.Rename(q, "rock.dds", "rock.tga"));
            WaitForInfo(doc);
            ctx.Check(InfoOf(doc, "rock.tga") == "DDS data: 512x512, DXT1, 10 mipmaps", $"renamed to .tga: '{InfoOf(doc, "rock.tga")}'");

            // undo: the cached lines come back without reading
            doc.Undo(); doc.Undo(); doc.Undo();
            ctx.Check(doc.List.InfoCache?.IsFilling == false, "undo needs no new reads");
            ctx.Check(InfoOf(doc, "wall.tga") == "256x256, 32-bit, RLE compressed" && InfoOf(doc, "rock.dds") == "512x512, DXT1, 10 mipmaps",
                "undo shows the earlier lines at once");
        }
        finally
        {
            if (doc is not null)
            {
                while (doc.CanUndo) doc.Undo();
                if (ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc); else doc.Dispose();
            }
            Work.VppWorkFolder.TryDeleteFolder(folder);
        }
    }

    /// <summary>
    /// The largest packfile in the game folder (read only, never saved): the column fills without holding up the UI
    /// thread, and every entry gets a line. Logs the time and the longest UI stall seen while it filled.
    /// </summary>
    [SelfTest("vpp.info-column-large")]
    public static void InfoColumnLarge(SelfTestContext ctx)
    {
        if (Cairn.Workspace.LocalPaths.GameDirectory is not { } game || !Directory.Exists(game)) { ctx.Skip("no game directory"); return; }
        var source = Directory.EnumerateFiles(game, "*.vpp").Select(p => new FileInfo(p)).OrderBy(f => f.Length).LastOrDefault();
        if (source is null) { ctx.Skip("no packfile in the game directory"); return; }
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        var doc = (VppDocument)module.Kind.Open(source.FullName);
        try
        {
            var clock = Stopwatch.StartNew();
            ctx.Shell.AddDocument(doc);
            // the first pump builds and lays out the document's view; the pauses after it are the fill's doing
            var first = Stopwatch.StartNew();
            SelfTestPump.Pump(20);
            double opening = first.Elapsed.TotalMilliseconds, longest = 0;
            while (doc.List.InfoCache is { IsFilling: true } && clock.ElapsedMilliseconds < 180_000)
            {
                var pump = Stopwatch.StartNew();
                SelfTestPump.Pump(20);
                longest = Math.Max(longest, pump.Elapsed.TotalMilliseconds - 20);
            }
            SelfTestPump.Pump(50);
            var rows = doc.List.AllRows;
            int filled = rows.Count(r => r.Info is not null), unreadable = rows.Count(r => r.InfoIsUnreadable);
            ctx.Log($"{source.Name} ({source.Length / (1024.0 * 1024):0} MB, {rows.Count:N0} entries): Info filled in {clock.ElapsedMilliseconds:N0} ms, "
                + $"opening {opening:0} ms, longest UI pause while filling {longest:0} ms, {unreadable} unreadable");
            foreach (var row in rows.Take(3)) ctx.Log($"  {row.Name}: {row.InfoText}");
            ctx.Check(filled == rows.Count, $"every entry has its line ({filled:N0} of {rows.Count:N0})");
            ctx.Check(clock.ElapsedMilliseconds < 120_000, "the column fills within two minutes");
            ctx.Check(longest < 250, "the UI thread is never held up for long while it fills");
        }
        finally
        {
            if (ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc); else doc.Dispose();
        }
    }
}
