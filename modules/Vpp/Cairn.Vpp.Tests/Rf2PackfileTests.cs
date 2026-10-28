using System.Text;
using Cairn.Formats;
using Cairn.Formats.Rf2;
using Cairn.Vpp.Model;
using Cairn.Vpp.Rf2;
using Cairn.Vpp.Validation;
using Cairn.Workspace;

namespace Cairn.Vpp.Tests;

/// <summary>Red Faction II packfile groups (.toc_group): synthetic packed and loose pairs, and the real install when present.</summary>
public sealed class Rf2PackfileTests
{
    // ---- writing synthetic groups ------------------------------------------------------------------------------

    private sealed class TocBuilder
    {
        private readonly MemoryStream _out = new();

        public TocBuilder Text(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            _out.Write(b);
            _out.WriteByte(0);
            return this;
        }

        public TocBuilder Raw(params byte[] bytes) { _out.Write(bytes); return this; }

        public TocBuilder U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _out.Write(b);
            return this;
        }

        public byte[] Bytes => _out.ToArray();
    }

    /// <summary>A packed group's toc and packfile (contiguous data from offset 0, as the stock files are).</summary>
    private static (byte[] Toc, byte[] Packfile) Packed(string group, uint flags, params (string Name, byte[] Data)[] files)
    {
        var toc = new TocBuilder().Text(group).Text($@"pc_media\{group}\").U32(flags).U32(1).Text($@"pc_media\{group}\{group}.packfile").U32((uint)files.Length);
        var pack = new MemoryStream();
        foreach (var (name, data) in files)
        {
            toc.Text(name).U32((uint)data.Length).U32((uint)pack.Length);
            pack.Write(data);
        }
        return (toc.Bytes, pack.ToArray());
    }

    /// <summary>A loose group's toc (each path is the folder plus the name, as the stock files are).</summary>
    private static byte[] Loose(string group, uint flags, params (string Name, long Size)[] files)
    {
        var toc = new TocBuilder().Text(group).Text($@"pc_media\{group}\").U32(flags).U32((uint)files.Length);
        foreach (var (name, size) in files) toc.Text(name).U32((uint)size).Text($@"pc_media\{group}\{name}");
        return toc.Bytes;
    }

    /// <summary>Writes a game folder with pc_media\&lt;group&gt;.toc_group and returns the toc path.</summary>
    private static string WriteToc(TempFolder temp, string group, byte[] toc) => temp.Write(Path.Combine("pc_media", group + ".toc_group"), toc);

    // ---- the reader --------------------------------------------------------------------------------------------

    [Fact]
    public void Packed_group_reads_exactly_and_its_entries_extract_byte_exact()
    {
        using var temp = new TempFolder();
        var a = TestData.Bytes(1001, 1);
        var b = TestData.Bytes(17, 2);
        var (toc, pack) = Packed("Levels", 2, ("l01.rfl", a), ("empty_v3d.peg", []), ("odd.tbl", b));
        string tocPath = WriteToc(temp, "Levels", toc);
        temp.Write(Path.Combine("pc_media", "Levels", "Levels.packfile"), pack);

        var group = Rf2TocGroup.Read(toc, "Levels.toc_group");
        Assert.True(group.IsPacked);
        Assert.Equal("Levels", group.GroupName);
        Assert.Equal(@"pc_media\Levels\", group.Folder);
        Assert.Equal(1u, group.Second);
        Assert.Equal(@"pc_media\Levels\Levels.packfile", group.PackfilePath);
        Assert.Equal(3, group.Entries.Count);
        Assert.Equal(1001L, group.Entries[1].Offset); // no alignment: offsets follow each other
        Assert.Equal(1001L, group.Entries[2].Offset);
        Assert.Equal(toc.Length, group.ByteLength);

        var package = Rf2Packfiles.Open(tocPath);
        Assert.True(package.IsReadOnly);
        Assert.NotNull(package.Rf2);
        Assert.Equal(3, package.Count);
        Assert.Equal(a, TestData.ReadEntry(package.Items[0]));
        Assert.Empty(TestData.ReadEntry(package.Items[1]));
        Assert.Equal(b, TestData.ReadEntry(package.Items[2]));
        Assert.All(package.Items, i => Assert.IsType<ArchiveSource>(i.Source));
        Assert.Contains(VppValidator.Validate(package), p => p.Code == "RF2004" && p.EntryName == "empty_v3d.peg");
        Assert.DoesNotContain(VppValidator.Validate(package), p => p.Code.StartsWith("VPP", StringComparison.Ordinal)); // RF1 rules do not apply
    }

    [Fact]
    public void Packed_group_finds_its_packfile_by_the_stored_path_below_the_game_folder()
    {
        using var temp = new TempFolder();
        var (toc, pack) = Packed("Data", 6, ("a.tbl", TestData.Bytes(40, 3)));
        // the group file in another folder of the game: <game>\copies\Data.toc_group, the packfile only at the stored path
        string tocPath = temp.Write(Path.Combine("copies", "Data.toc_group"), toc);
        temp.Write(Path.Combine("pc_media", "Data", "Data.packfile"), pack);
        Assert.Equal(Path.GetFullPath(temp.Path), Path.GetFullPath(Rf2TocGroup.GameFolderOf(tocPath)!));
        var package = Rf2Packfiles.Open(tocPath);
        Assert.Contains("bit 2 set", package.Rf2!.FlagsText, StringComparison.Ordinal);
        Assert.Equal(40, package.Items[0].Size);

        // and directly beside the group file when that is where it was copied
        using var other = new TempFolder();
        string beside = other.Write("Data.toc_group", toc);
        other.Write("Data.packfile", pack);
        Assert.Single(Rf2Packfiles.Open(beside).Items);
    }

    [Fact]
    public void Packed_group_without_its_packfile_is_refused_saying_where_cairn_looked()
    {
        using var temp = new TempFolder();
        var (toc, _) = Packed("Gone", 2, ("a.tbl", TestData.Bytes(10, 4)));
        string tocPath = WriteToc(temp, "Gone", toc);
        var ex = Assert.Throws<AssetFormatException>(() => Rf2Packfiles.Open(tocPath));
        Assert.Contains("was not found", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Gone.packfile", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Packed_entry_past_the_packfiles_end_is_refused()
    {
        using var temp = new TempFolder();
        var (toc, pack) = Packed("Short", 2, ("a.tbl", TestData.Bytes(100, 5)), ("b.tbl", TestData.Bytes(100, 6)));
        string tocPath = WriteToc(temp, "Short", toc);
        temp.Write(Path.Combine("pc_media", "Short", "Short.packfile"), pack[..150]);
        var ex = Assert.Throws<AssetFormatException>(() => Rf2Packfiles.Open(tocPath));
        Assert.Contains("b.tbl", ex.Message, StringComparison.Ordinal);
        Assert.Contains("150", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loose_group_reads_files_by_their_stored_paths_and_keeps_missing_ones_listed()
    {
        using var temp = new TempFolder();
        var wav = TestData.Bytes(300, 7);
        var toc = Loose("Sounds", 4, ("ding.wav", wav.Length), ("vssver.scc", 3152), ("zero_v3d.peg", 0));
        string tocPath = WriteToc(temp, "Sounds", toc);
        temp.Write(Path.Combine("pc_media", "Sounds", "ding.wav"), wav);
        temp.Write(Path.Combine("pc_media", "Sounds", "zero_v3d.peg"), []);

        var group = Rf2TocGroup.Read(toc, "Sounds.toc_group");
        Assert.False(group.IsPacked);
        Assert.Equal(3u, group.Second); // a loose group's second value is its count
        Assert.Null(group.PackfilePath);
        Assert.Equal(@"pc_media\Sounds\ding.wav", group.Entries[0].StoredPath);

        var package = Rf2Packfiles.Open(tocPath);
        Assert.Equal(wav, TestData.ReadEntry(package.Items[0]));
        var missing = Assert.IsType<MissingFileSource>(package.Items[1].Source);
        Assert.Equal(3152, package.Items[1].Size);
        Assert.Contains("not on disk", Assert.Throws<FileNotFoundException>(() => missing.Open()).Message, StringComparison.Ordinal);
        Assert.Equal(["vssver.scc"], package.Rf2!.Missing);
        Assert.Contains("1 listed file is not on disk", package.Rf2.Banner, StringComparison.Ordinal);
        var problems = VppValidator.Validate(package);
        Assert.Contains(problems, p => p.Code == "RF2001" && p.Severity == VppSeverity.Warning && p.EntryName == "vssver.scc");
        Assert.Contains(problems, p => p.Code == "RF2004" && p.Severity == VppSeverity.Info && p.EntryName == "zero_v3d.peg");
        Assert.DoesNotContain(problems, p => p.Severity == VppSeverity.Error);
    }

    [Fact]
    public void Loose_group_lists_case_duplicates_once_each_and_reports_them()
    {
        using var temp = new TempFolder();
        var mesh = TestData.Bytes(64, 8);
        var toc = Loose("Bots", 0, ("military_Grunt.rfc", mesh.Length), ("military_grunt.rfc", mesh.Length));
        string tocPath = WriteToc(temp, "Bots", toc);
        temp.Write(Path.Combine("pc_media", "Bots", "military_grunt.rfc"), mesh);
        var package = Rf2Packfiles.Open(tocPath);
        Assert.Equal(2, package.Count); // both listed
        Assert.Equal(["military_Grunt.rfc / military_grunt.rfc"], package.Rf2!.CaseDuplicates);
        Assert.Contains(VppValidator.Validate(package), p => p.Code == "RF2002");
        Assert.Equal(TestData.ReadEntry(package.Items[0]), TestData.ReadEntry(package.Items[1])); // one file on Windows
    }

    [Fact]
    public void Loose_file_whose_size_differs_uses_the_file_on_disk()
    {
        using var temp = new TempFolder();
        var toc = Loose("Menu", 0, ("a.peg", 999));
        string tocPath = WriteToc(temp, "Menu", toc);
        temp.Write(Path.Combine("pc_media", "Menu", "a.peg"), TestData.Bytes(10, 9));
        var package = Rf2Packfiles.Open(tocPath);
        Assert.Equal(10, package.Items[0].Size);
        Assert.Contains(VppValidator.Validate(package), p => p.Code == "RF2003" && p.EntryName == "a.peg");
    }

    [Fact]
    public void Facts_name_the_mode_flags_counts_and_types()
    {
        using var temp = new TempFolder();
        var (toc, pack) = Packed("All", 2, ("a.rfl", TestData.Bytes(5, 1)), ("b.peg", TestData.Bytes(6, 2)), ("c.peg", TestData.Bytes(7, 3)));
        string tocPath = WriteToc(temp, "All", toc);
        temp.Write(Path.Combine("pc_media", "All", "All.packfile"), pack);
        var package = Rf2Packfiles.Open(tocPath);
        var facts = package.Rf2!.Facts(package.Items).ToDictionary(f => f.Label, f => f.Value);
        Assert.StartsWith("Packed", facts["Mode"], StringComparison.Ordinal);
        Assert.StartsWith("2 (packed", facts["Flags"], StringComparison.Ordinal);
        Assert.Equal("3", facts["Files"]);
        Assert.StartsWith("2 ", facts["Type .peg"], StringComparison.Ordinal);
        Assert.Contains("1 (meaning unknown)", facts["Second value"], StringComparison.Ordinal);
    }

    // ---- refusals: the format has no signature, so the parse is strict -----------------------------------------

    [Fact]
    public void Truncated_toc_is_refused()
    {
        var (toc, _) = Packed("T", 2, ("a.tbl", TestData.Bytes(5, 1)), ("b.tbl", TestData.Bytes(5, 2)));
        for (int cut = 1; cut < toc.Length; cut += 3)
            Assert.Throws<AssetFormatException>(() => Rf2TocGroup.Read(toc[..cut], "t.toc_group"));
        Assert.Throws<AssetFormatException>(() => Rf2TocGroup.Read([], "t.toc_group"));
    }

    [Fact]
    public void Bytes_after_the_last_entry_are_refused_with_the_offset()
    {
        var (toc, _) = Packed("T", 2, ("a.tbl", TestData.Bytes(5, 1)));
        var ex = Assert.Throws<AssetFormatException>(() => Rf2TocGroup.Read([.. toc, 0x41], "t.toc_group"));
        Assert.Contains($"offset {toc.Length:N0}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_printable_names_are_refused()
    {
        var toc = new TocBuilder().Text("G").Text(@"pc_media\G\").U32(0).U32(1).Raw(0x61, 0x01, 0x62, 0).U32(1).Text(@"pc_media\G\ab").Bytes;
        Assert.Contains("0x01", Assert.Throws<AssetFormatException>(() => Rf2TocGroup.Read(toc, "g.toc_group")).Message, StringComparison.Ordinal);
        var high = new TocBuilder().Text("G").Text(@"pc_media\G\").U32(0).U32(1).Raw(0x61, 0xE9, 0).U32(1).Text(@"pc_media\G\a").Bytes;
        Assert.Throws<AssetFormatException>(() => Rf2TocGroup.Read(high, "g.toc_group"));
    }

    [Fact]
    public void Paths_that_leave_the_game_folder_are_refused()
    {
        foreach (string path in new[] { @"..\..\Windows\win.ini", "pc_media:x.wav", @"\x.wav", @"pc_media\..\..\x.wav" })
        {
            var toc = new TocBuilder().Text("G").Text(@"pc_media\G\").U32(0).U32(1).Text("x.wav").U32(1).Text(path).Bytes;
            Assert.Throws<AssetFormatException>(() => Rf2TocGroup.Read(toc, "g.toc_group"));
        }
    }

    [Fact]
    public void A_count_larger_than_the_file_is_refused_before_allocating()
    {
        var toc = new TocBuilder().Text("G").Text(@"pc_media\G\").U32(2).U32(1).Text(@"pc_media\G\G.packfile").U32(0x7FFFFFFF).Bytes;
        Assert.Contains("more than its remaining", Assert.Throws<AssetFormatException>(() => Rf2TocGroup.Read(toc, "g.toc_group")).Message, StringComparison.Ordinal);
        var loose = new TocBuilder().Text("G").Text(@"pc_media\G\").U32(0).U32(uint.MaxValue).Bytes;
        Assert.Throws<AssetFormatException>(() => Rf2TocGroup.Read(loose, "g.toc_group"));
    }

    [Fact]
    public void Random_bytes_are_refused_and_never_throw_anything_else()
    {
        var random = new Random(1234);
        for (int i = 0; i < 300; i++)
        {
            var bytes = new byte[random.Next(0, 200)];
            random.NextBytes(bytes);
            try { Rf2TocGroup.Read(bytes, "r.toc_group"); }
            catch (AssetFormatException) { }
        }
        // a valid toc with single bytes changed: read or refused, nothing else
        var (toc, _) = Packed("T", 2, ("a.tbl", TestData.Bytes(5, 1)), ("b.tbl", TestData.Bytes(5, 2)));
        for (int at = 0; at < toc.Length; at++)
        {
            var copy = (byte[])toc.Clone();
            copy[at] ^= 0xA5;
            try { Rf2TocGroup.Read(copy, "t.toc_group"); }
            catch (AssetFormatException) { }
        }
    }

    // ---- the real install (strictly read-only; skipped when absent) --------------------------------------------

    private static IReadOnlyList<string> RealTocs()
    {
        string? dir = LocalPaths.Rf2GameDirectory;
        string? media = dir is null ? null : Path.Combine(dir, "pc_media");
        return media is not null && Directory.Exists(media) ? [.. Directory.EnumerateFiles(media, "*.toc_group").Order(StringComparer.OrdinalIgnoreCase)] : [];
    }

    [Fact]
    public void Every_real_toc_group_opens()
    {
        var tocs = RealTocs();
        if (tocs.Count == 0) return;
        Assert.Equal(14, tocs.Count);
        int total = 0;
        foreach (string toc in tocs)
        {
            var package = Rf2Packfiles.Open(toc);
            var info = package.Rf2!;
            Assert.Equal(Path.GetFileNameWithoutExtension(toc), info.Toc.GroupName);
            Assert.Equal(new FileInfo(toc).Length, info.Toc.ByteLength);
            Assert.DoesNotContain(VppValidator.Validate(package), p => p.Severity == VppSeverity.Error);
            total += package.Count;
            if (info.Toc.GroupName == "All_Levels")
            {
                Assert.True(info.Toc.IsPacked);
                Assert.Equal(1215, package.Count);
                Assert.Equal(105, package.Items.Count(i => i.Extension == ".rfl"));
                var last = info.Toc.Entries[^1];
                Assert.Equal(661_753_212L, last.Offset + last.Size);
                Assert.Equal(661_753_212L, new FileInfo(info.PackfilePath!).Length);
            }
            if (info.Toc.GroupName == "Common_sounds") Assert.Equal(["vssver.scc"], info.Missing);
            if (info.Toc.GroupName == "Botmatch_core") Assert.Single(info.CaseDuplicates);
            if (!info.Toc.IsPacked) Assert.Empty(info.SizeDiffers);
        }
        Assert.Equal(8682, total);
    }

    [Fact]
    public void Real_packed_entries_read_byte_exact_from_their_offsets()
    {
        var tocs = RealTocs();
        string? levels = tocs.FirstOrDefault(t => Path.GetFileNameWithoutExtension(t) == "All_Levels");
        if (levels is null) return;
        var package = Rf2Packfiles.Open(levels);
        string packfile = package.Rf2!.PackfilePath!;
        // a bounded sample: the first, the last and every 97th entry, compared with a direct read of the packfile
        using var raw = new FileStream(packfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        for (int i = 0; i < package.Count; i++)
        {
            if (i != 0 && i != package.Count - 1 && i % 97 != 0) continue;
            var entry = package.Rf2.Toc.Entries[i];
            if (entry.Size > 8 << 20) continue;
            var expected = new byte[entry.Size];
            raw.Position = entry.Offset;
            raw.ReadExactly(expected);
            Assert.Equal(expected, TestData.ReadEntry(package.Items[i]));
        }
    }
}
