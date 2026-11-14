using System.Buffers.Binary;
using System.Text;
using Cairn.Assets;
using Cairn.Formats;
using Cairn.Formats.XForge;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;
using Cairn.Vpp.XForge;
using Cairn.Workspace;

namespace Cairn.Vpp.Tests;

/// <summary>X-Forge CFL3 archives (.cfl): synthetic archives, the reader's refusals, and Red Faction for N-Gage's archive when present.</summary>
public sealed class CflArchiveTests
{
    private static readonly byte[] Text = Encoding.Latin1.GetBytes("SOUND \"snd_alert.wav\"\r\nEND\r\n// café\r\n");
    // compresses to about a third: long enough that cutting its stream loses data
    private static readonly byte[] Big = Encoding.ASCII.GetBytes(string.Join(" ", Enumerable.Range(0, 900).Select(i => $"w{i * 7919 % 1009}")));

    private static CflArchive Read(byte[] bytes) => CflArchive.Read(new MemoryStream(bytes), "test.cfl");

    private static byte[] Entry(byte[] archive, string name)
    {
        var cfl = Read(archive);
        return cfl.ReadEntry(new MemoryStream(archive), cfl.Find(name)!);
    }

    private static AssetFormatException Refused(byte[] bytes) => Assert.Throws<AssetFormatException>(() => Read(bytes));

    [Fact]
    public void Reads_stored_and_zlib_entries_with_the_short_streams_the_game_writes()
    {
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflEntry("C01_msg.txt", Text, CflArchive.Stored), new SyntheticCflEntry("big.tga", Big), new SyntheticCflEntry("empty.txt", [], CflArchive.Stored));
        var cfl = Read(bytes);
        Assert.Equal(0, cfl.Start);
        Assert.Equal(bytes.Length, cfl.Length);
        Assert.Equal(CflArchive.Zlib, cfl.DirectoryCompression);
        Assert.Equal(["C01_msg.txt", "big.tga", "empty.txt"], cfl.Entries.Select(e => e.Name));
        Assert.Equal([CflArchive.Stored, CflArchive.Zlib, CflArchive.Stored], cfl.Entries.Select(e => e.Compression));
        Assert.Equal(CflArchive.HeaderBytes, cfl.Entries[0].Offset);
        Assert.Equal((uint)Text.Length, cfl.Entries[0].PackedSize);
        Assert.True(cfl.Entries[1].PackedSize < Big.Length);
        var stream = new MemoryStream(bytes);
        Assert.Equal(Text, cfl.ReadEntry(stream, cfl.Entries[0]));
        Assert.Equal(Big, cfl.ReadEntry(stream, cfl.Entries[1]));
        Assert.Empty(cfl.ReadEntry(stream, cfl.Entries[2]));
        Assert.All(cfl.Entries, e => Assert.Null(cfl.Check(stream, e)));
        using var entry = cfl.OpenEntry(stream, cfl.Entries[1]);
        Assert.Equal(Big.Length, entry.Length);
        Assert.False(entry.CanWrite);
    }

    [Fact]
    public void A_stored_entry_starts_with_its_size_and_a_size_that_differs_from_the_directory_is_corrupt()
    {
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflEntry("C01_minemsg1.txt", Text, CflArchive.Stored), new SyntheticCflEntry("next.txt", Text, CflArchive.Stored));
        var cfl = Read(bytes);
        var first = cfl.Entries[0];
        // u32 size, then the raw bytes; the next entry starts right after them
        Assert.Equal((uint)Text.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)first.Offset)));
        Assert.Equal(Text, bytes.AsSpan((int)first.Offset + 4, Text.Length).ToArray());
        Assert.Equal(first.Offset + 4 + Text.Length, cfl.Entries[1].Offset);
        Assert.Equal(Text, cfl.ReadEntry(new MemoryStream(bytes), first));

        // the prefix says one byte less than the directory: corrupt (the archive still reads)
        byte[] bad = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan((int)first.Offset), (uint)Text.Length - 1);
        cfl = Read(bad);
        Assert.Equal($"it is stored with {Text.Length - 1:N0} bytes, but {Text.Length:N0} are listed", cfl.Check(new MemoryStream(bad), cfl.Entries[0]));
        Assert.Throws<AssetFormatException>(() => cfl.ReadEntry(new MemoryStream(bad), cfl.Entries[0]));
        Assert.Null(cfl.Check(new MemoryStream(bad), cfl.Entries[1]));
    }

    [Fact]
    public void The_stream_has_no_adler32_trailer_and_its_end_does_not_matter()
    {
        // the game's block ends with the deflate data; a complete or garbled trailer reads the same
        byte[] shortBlock = SyntheticCfl.ZlibBlock(Big);
        Assert.Equal(SyntheticCfl.ZlibBlock(Big, CflZlibEnd.Adler).Length - 4, shortBlock.Length);
        foreach (var end in new[] { CflZlibEnd.NoTrailer, CflZlibEnd.Adler, CflZlibEnd.GarbledAdler })
            Assert.Equal(Big, Entry(SyntheticCfl.Build(new SyntheticCflOptions { End = end }, new SyntheticCflEntry("big.tga", Big)), "big.tga"));
        Assert.Equal(0x11E60398u, SyntheticCfl.Adler32("Wikipedia"u8));
    }

    [Fact]
    public void A_stored_directory_reads_the_same()
    {
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflOptions { DirectoryCompression = CflArchive.Stored }, new SyntheticCflEntry("a.txt", Text), new SyntheticCflEntry("b.txt", Text, CflArchive.Stored));
        var cfl = Read(bytes);
        Assert.Equal(CflArchive.Stored, cfl.DirectoryCompression);
        Assert.Equal(cfl.DirectoryUnpackedSize, cfl.DirectoryPackedSize);
        Assert.Equal(["a.txt", "b.txt"], cfl.Entries.Select(e => e.Name));
        Assert.Equal(Text, Entry(bytes, "a.txt"));
    }

    [Fact]
    public void An_appended_archive_is_found_from_its_footer()
    {
        byte[] prefix = [.. Encoding.ASCII.GetBytes("an executable or another archive"), 0, 1, 2, 3];
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflOptions { Prefix = prefix }, new SyntheticCflEntry("a.txt", Text), new SyntheticCflEntry("big.tga", Big));
        var cfl = Read(bytes);
        Assert.Equal(prefix.Length, cfl.Start);
        Assert.Equal(bytes.Length - prefix.Length, cfl.Length);
        Assert.Equal(prefix.Length + CflArchive.HeaderBytes, cfl.Entries[0].Offset);
        Assert.Equal(Text, Entry(bytes, "a.txt"));
        Assert.Equal(Big, Entry(bytes, "big.tga"));
        Assert.True(CflArchive.Probe(new MemoryStream(bytes)));
    }

    [Fact]
    public void An_empty_archive_is_valid()
    {
        var cfl = Read(SyntheticCfl.Build());
        Assert.Empty(cfl.Entries);
        Assert.Equal(0u, cfl.DirectoryUnpackedSize);
    }

    [Fact]
    public void The_probe_is_cheap_and_strict()
    {
        Assert.True(CflArchive.Probe(new MemoryStream(SyntheticCfl.Build(new SyntheticCflEntry("a.txt", Text)))));
        Assert.False(CflArchive.Probe(new MemoryStream(Encoding.ASCII.GetBytes("CFL3 but nothing else, not even a footer"))));
        Assert.False(CflArchive.Probe(new MemoryStream([])));
        Assert.True(CflArchive.HasSignature("CFL3"u8));
        Assert.False(CflArchive.HasSignature("3CFL"u8));
        Assert.True(CflArchive.HasExtension(@"x\redfaction.CFL"));
    }

    // ---- refusals ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("another program's file")]
    [InlineData("")]
    [InlineData("CF")]
    public void Another_file_is_not_a_cfl_archive(string content)
    {
        Assert.Contains("is not an X-Forge CFL archive", Refused(Encoding.ASCII.GetBytes(content)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_version_is_refused_by_name()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("CFL2 an older archive without the footer");
        Assert.Contains("version '2'", Refused(bytes).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_file_is_damaged()
    {
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflEntry("a.txt", Text), new SyntheticCflEntry("big.tga", Big));
        Assert.Contains("does not end with the 3CFL footer", Refused(bytes[..^3]).Message, StringComparison.Ordinal);
        Assert.Contains("shorter than", Refused(bytes[..10]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_footer_that_does_not_match_the_file_is_damaged()
    {
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflEntry("a.txt", Text));
        byte[] longer = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(longer.AsSpan(longer.Length - 8), (uint)bytes.Length + 1);
        Assert.Contains("footer gives an archive size", Refused(longer).Message, StringComparison.Ordinal);
        byte[] shorter = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(shorter.AsSpan(shorter.Length - 8), (uint)bytes.Length - 1);
        Assert.Contains("no CFL3 header there", Refused(shorter).Message, StringComparison.Ordinal);
        byte[] header = (byte[])bytes.Clone();
        header[0] = (byte)'X';
        Assert.Contains("does not start with the CFL3 header", Refused(header).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Offsets_and_sizes_outside_the_archive_are_damaged()
    {
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflOptions { DirectoryCompression = CflArchive.Stored }, new SyntheticCflEntry("a.txt", Text, CflArchive.Stored));
        var cfl = Read(bytes);
        int directory = (int)cfl.DirectoryOffset, record = directory + 8;

        byte[] Patch(int at, uint value)
        {
            byte[] copy = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(at), value);
            return copy;
        }
        Assert.Contains("directory offset", Refused(Patch(4, (uint)bytes.Length)).Message, StringComparison.Ordinal);
        Assert.Contains("directory offset", Refused(Patch(4, 3)).Message, StringComparison.Ordinal);
        Assert.Contains("runs past the end of the archive", Refused(Patch(directory + 4, 1_000_000)).Message, StringComparison.Ordinal);
        Assert.Contains("more than the", Refused(Patch(8, uint.MaxValue)).Message, StringComparison.Ordinal);
        Assert.Contains("compression 7, which is not supported", Refused(Patch(directory, 7)).Message, StringComparison.Ordinal);
        Assert.Contains("is stored with", Refused(Patch(8, cfl.DirectoryUnpackedSize + 1)).Message, StringComparison.Ordinal);
        Assert.Contains("outside the archive", Refused(Patch(record + 4, (uint)bytes.Length)).Message, StringComparison.Ordinal);
        Assert.Contains("outside the archive", Refused(Patch(record + 4, 0)).Message, StringComparison.Ordinal);
        Assert.Contains("runs past the end of the archive", Refused(Patch(CflArchive.HeaderBytes, 1_000_000)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_directory_records_are_damaged()
    {
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflOptions { DirectoryCompression = CflArchive.Stored }, new SyntheticCflEntry("a.txt", Text, CflArchive.Stored));
        var cfl = Read(bytes);
        int record = (int)cfl.DirectoryOffset + 8;
        byte[] longName = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(longName.AsSpan(record + 12), 200);
        Assert.Contains("runs past the end of its directory", Refused(longName).Message, StringComparison.Ordinal);
        byte[] cut = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(8), cfl.DirectoryUnpackedSize - 3); // the last record is cut short
        BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan((int)cfl.DirectoryOffset + 4), cfl.DirectoryUnpackedSize - 3);
        Assert.Contains("runs past the end of its directory", Refused(cut).Message, StringComparison.Ordinal);

        Assert.Contains("empty name", Refused(SyntheticCfl.Build(new SyntheticCflEntry("", Text))).Message, StringComparison.Ordinal);
        Assert.Contains(@"control characters (a\x01b.txt)", Refused(SyntheticCfl.Build(new SyntheticCflEntry("a\u0001b.txt", Text))).Message, StringComparison.Ordinal);
        Assert.Contains("ends inside record 2", Refused(SyntheticCflWithTrailingDirectoryBytes(5)).Message, StringComparison.Ordinal);
    }

    /// <summary>A stored-directory archive whose directory has <paramref name="extra"/> bytes after its one record.</summary>
    private static byte[] SyntheticCflWithTrailingDirectoryBytes(int extra)
    {
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflOptions { DirectoryCompression = CflArchive.Stored }, new SyntheticCflEntry("a.txt", Text, CflArchive.Stored));
        var cfl = Read(bytes);
        int directory = (int)cfl.DirectoryOffset, size = (int)cfl.DirectoryUnpackedSize;
        using var ms = new MemoryStream();
        ms.Write(bytes, 0, directory);
        var b = new byte[4];
        void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        U32(CflArchive.Stored);
        U32((uint)(size + extra));
        ms.Write(bytes, directory + 8, size);
        ms.Write(new byte[extra]);
        U32((uint)(ms.Length + 8));
        U32(CflArchive.FooterSignature);
        byte[] result = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)(size + extra));
        return result;
    }

    [Fact]
    public void An_unknown_compression_is_listed_but_cannot_be_read()
    {
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflEntry("cipher.bin", Text, 5), new SyntheticCflEntry("a.txt", Text));
        var cfl = Read(bytes);
        var entry = cfl.Find("cipher.bin")!;
        Assert.False(entry.IsSupported);
        Assert.Equal("compression 5 (not supported)", entry.CompressionText);
        Assert.Equal("compression 5 is not supported", cfl.Check(new MemoryStream(bytes), entry));
        var ex = Assert.Throws<AssetFormatException>(() => cfl.ReadEntry(new MemoryStream(bytes), entry));
        Assert.Contains("compression 5 is not supported", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Text, Entry(bytes, "a.txt"));
    }

    [Fact]
    public void An_entry_that_does_not_unpack_to_its_size_is_corrupt_but_the_archive_reads()
    {
        byte[] bytes = SyntheticCfl.Build(
            new SyntheticCflEntry("long.tga", Big) { ListedSize = (uint)Big.Length + 10 },
            new SyntheticCflEntry("short.tga", Big) { ListedSize = (uint)Big.Length - 10 },
            new SyntheticCflEntry("stored.txt", Text, CflArchive.Stored) { ListedSize = 3 },
            new SyntheticCflEntry("good.txt", Text));
        var cfl = Read(bytes);
        var stream = new MemoryStream(bytes);
        // the listed size is also in the zlib block, so a block that disagrees with its directory record is corrupt
        Assert.Contains("says it unpacks to", cfl.Check(stream, cfl.Entries[0]), StringComparison.Ordinal);
        Assert.Contains("is stored with", cfl.Check(stream, cfl.Entries[2]), StringComparison.Ordinal);
        Assert.Null(cfl.Check(stream, cfl.Entries[3]));
        var ex = Assert.Throws<AssetFormatException>(() => cfl.ReadEntry(stream, cfl.Entries[1]));
        Assert.Contains("'short.tga' in 'test.cfl' is corrupt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stream_that_unpacks_to_the_wrong_size_is_corrupt()
    {
        // the block and its record agree, but the stream holds less (cut short) or more (another stream) than they say
        byte[] cut = SyntheticCfl.Build(new SyntheticCflOptions { CutBytes = 40 }, new SyntheticCflEntry("big.tga", Big));
        var cfl = Read(cut);
        string? problem = cfl.Check(new MemoryStream(cut), cfl.Entries[0]);
        Assert.NotNull(problem);
        Assert.StartsWith("it unpacks to ", problem, StringComparison.Ordinal);
        Assert.EndsWith($"not the {Big.Length:N0} listed", problem, StringComparison.Ordinal);
        Assert.Throws<AssetFormatException>(() => cfl.ReadEntry(new MemoryStream(cut), cfl.Entries[0]));

        byte[] block = SyntheticCfl.ZlibBlock(Big);
        BinaryPrimitives.WriteUInt32LittleEndian(block, 100); // says 100 bytes; the stream holds all of Big
        byte[] bytes = WithBlock(SyntheticCfl.Build(new SyntheticCflEntry("big.tga", Big) { ListedSize = 100 }), block);
        cfl = Read(bytes);
        Assert.Contains("unpacks to more than the", cfl.Check(new MemoryStream(bytes), cfl.Entries[0]), StringComparison.Ordinal);

        byte[] notZlib = SyntheticCfl.ZlibBlock(Big);
        notZlib[4] = 0x12;
        bytes = WithBlock(SyntheticCfl.Build(new SyntheticCflEntry("big.tga", Big)), notZlib);
        cfl = Read(bytes);
        Assert.Contains("does not start with a zlib header", cfl.Check(new MemoryStream(bytes), cfl.Entries[0]), StringComparison.Ordinal);
    }

    /// <summary>The archive with its first entry's data block replaced by <paramref name="block"/> (same length).</summary>
    private static byte[] WithBlock(byte[] archive, byte[] block)
    {
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(CflArchive.HeaderBytes)), (uint)block.Length);
        byte[] copy = (byte[])archive.Clone();
        block.CopyTo(copy, CflArchive.HeaderBytes + 4);
        return copy;
    }

    [Fact]
    public void A_size_deflate_cannot_reach_is_refused_before_allocating()
    {
        byte[] block = SyntheticCfl.ZlibBlock(Text);
        BinaryPrimitives.WriteUInt32LittleEndian(block, int.MaxValue);
        byte[] bytes = WithBlock(SyntheticCfl.Build(new SyntheticCflEntry("a.txt", Text) { ListedSize = int.MaxValue }), block);
        var cfl = Read(bytes);
        Assert.Contains("more than", cfl.Check(new MemoryStream(bytes), cfl.Entries[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_listed_twice_are_kept_and_the_first_is_found()
    {
        byte[] other = [.. Text, 1];
        byte[] bytes = SyntheticCfl.Build(new SyntheticCflEntry("D02.txt", Text), new SyntheticCflEntry("tex.pcx", Big), new SyntheticCflEntry("d02.TXT", other), new SyntheticCflEntry("tex.pcx", Big));
        var cfl = Read(bytes);
        Assert.Equal(4, cfl.Entries.Count);
        Assert.Same(cfl.Entries[0], cfl.Find("d02.txt"));
        Assert.Equal(Text, Entry(bytes, "D02.TXT"));
    }

    // ---- as a package ------------------------------------------------------------------------------------------

    [Fact]
    public void Opens_read_only_with_problems_and_facts()
    {
        using var temp = new TempFolder();
        string path = temp.Write("redfaction.cfl", SyntheticCfl.Build(
            new SyntheticCflEntry("C01_msg.txt", Text, CflArchive.Stored),
            new SyntheticCflEntry("lvl0_crate_01.tga", Big),
            new SyntheticCflEntry("cipher.bin", Text, 9),
            new SyntheticCflEntry("bad.wav", Big) { ListedSize = 7 },
            new SyntheticCflEntry("same.pcx", Big),
            new SyntheticCflEntry("SAME.pcx", Big),
            new SyntheticCflEntry("diff.pcx", Big),
            new SyntheticCflEntry("diff.pcx", Text),
            new SyntheticCflEntry("empty.txt", [], CflArchive.Stored)));
        var stamp = VppArchiveStamp.Of(path);
        var package = CflArchives.Open(path);
        Assert.True(package.IsReadOnly);
        Assert.NotNull(package.Cfl);
        Assert.False(package.IsModified);
        Assert.Equal(9, package.Count);
        Assert.Equal(["C01_msg.txt", "lvl0_crate_01.tga", "cipher.bin", "bad.wav", "same.pcx", "SAME.pcx", "diff.pcx", "diff.pcx", "empty.txt"], package.Items.Select(i => i.Name));
        Assert.All(package.Items, i => Assert.IsType<CflEntrySource>(i.Source));
        Assert.Equal(Text, package.Items[0].Source.ReadAll());
        Assert.Equal(Big, package.Find("LVL0_CRATE_01.TGA")!.Source.ReadAll());
        Assert.Equal(Big, package.Find("diff.pcx")!.Source.ReadAll()); // the first listed

        var info = package.Cfl!;
        Assert.Equal(2, info.StoredCount);
        Assert.Equal(6, info.ZlibCount);
        Assert.Equal(1, info.UnsupportedCount);
        Assert.Equal(1, info.CorruptCount);
        Assert.Equal(new[] { 2, 3 }, info.Problems.Keys.Order());
        Assert.Equal(2, info.ListedTwice.Count);
        Assert.True(info.ListedTwice.Single(t => t.Name == "same.pcx").SameData);
        Assert.False(info.ListedTwice.Single(t => t.Name == "diff.pcx").SameData);
        Assert.Contains("same.pcx / SAME.pcx (2 times, same data)", info.ListedTwice.Select(t => t.Text));
        var facts = info.Facts(package.Items).ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("X-Forge CFL3 (Red Faction for N-Gage)", facts["Format"]);
        Assert.Equal(9.ToString("N0"), facts["Entries"]);
        Assert.Equal("2", facts["Stored"]);
        Assert.Equal("6", facts["zlib"]);
        Assert.StartsWith("1 ", facts["Unsupported"], StringComparison.Ordinal);
        Assert.StartsWith("1 ", facts["Corrupt"], StringComparison.Ordinal);
        Assert.Contains("Packed size", facts.Keys);
        Assert.Contains("Unpacked size", facts.Keys);
        Assert.Contains("Type .pcx", facts.Keys);
        Assert.DoesNotContain("Starts at", facts.Keys);
        Assert.Contains("read only", info.Banner, StringComparison.Ordinal);

        // the unreadable entries are listed with the reason; reading them fails as an I/O error
        var cipher = (CflEntrySource)package.Find("cipher.bin")!.Source;
        Assert.Equal("compression 9 is not supported", cipher.Problem);
        Assert.Contains("compression 9 is not supported", Assert.Throws<IOException>(() => cipher.Open()).Message, StringComparison.Ordinal);
        Assert.NotNull(((CflEntrySource)package.Find("bad.wav")!.Source).Problem);
        Assert.Throws<IOException>(() => package.Find("bad.wav")!.Source.ReadAll());

        var problems = VppValidator.Validate(package);
        Assert.Contains(problems, p => p.Code == "CFL001" && p.EntryName == "cipher.bin" && p.Severity == VppSeverity.Warning);
        Assert.Contains(problems, p => p.Code == "CFL002" && p.EntryName == "bad.wav" && p.Severity == VppSeverity.Warning);
        Assert.Contains(problems, p => p.Code == "CFL003" && p.EntryName == "same.pcx" && p.Severity == VppSeverity.Info);
        Assert.Contains(problems, p => p.Code == "CFL003" && p.EntryName == "diff.pcx" && p.Severity == VppSeverity.Warning);
        Assert.Contains(problems, p => p.Code == "CFL004" && p.EntryName == "empty.txt" && p.Severity == VppSeverity.Info);
        Assert.All(problems, p => Assert.StartsWith("CFL", p.Code, StringComparison.Ordinal));
        Assert.All(problems, p => Assert.Contains(p.Code, CflArchives.Rules.Keys));
        Assert.Equal(stamp, VppArchiveStamp.Of(path));
    }

    [Fact]
    public void A_non_cfl_with_the_extension_is_refused_with_a_clear_message()
    {
        using var temp = new TempFolder();
        string path = temp.Write("notes.cfl", Encoding.ASCII.GetBytes("Comic book flowchart, not X-Forge"));
        var ex = Assert.Throws<AssetFormatException>(() => CflArchives.Open(path));
        Assert.Equal("'notes.cfl' is not an X-Forge CFL archive (it does not end with the 3CFL footer).", ex.Message);
    }

    [Fact]
    public void A_package_entry_is_an_asset_source_for_texture_lookups()
    {
        // what a work copy's resolver does (AssetSiblings.Layer over the packfile view): a texture an .xff names is
        // found by name in the same archive, through the texture probing rules
        using var temp = new TempFolder();
        byte[] tga = TestTga();
        string path = temp.Write("redfaction.cfl", SyntheticCfl.Build(new SyntheticCflEntry("level_01a.xff", Text), new SyntheticCflEntry("txtr_Jeep01_P01.tga", tga)));
        var package = CflArchives.Open(path);
        var resolver = new AssetResolver(new AssetResolverOptions()).WithOverlay("redfaction.cfl", n => package.Contains(Path.GetFileName(n)),
            n => package.Find(Path.GetFileName(n)) is { } item ? new MemoryStream(item.Source.ReadAll(), writable: false) : null);
        var found = resolver.Resolve("TXTR_JEEP01_P01.TGA");
        Assert.NotNull(found);
        Assert.Equal("redfaction.cfl", found!.Origin);
        Assert.Equal(tga, found.ReadAllBytes());
        Assert.Equal(4, resolver.ProbeImage("txtr_jeep01_p01.tga")?.Width);
        Assert.Null(resolver.Resolve("not_in_the_archive.tga"));
    }

    /// <summary>A 4x2 32-bit uncompressed TGA.</summary>
    private static byte[] TestTga()
    {
        var b = new byte[18 + 4 * 2 * 4];
        b[2] = 2;
        b[12] = 4;
        b[14] = 2;
        b[16] = 32;
        b[17] = 8;
        for (int i = 18; i < b.Length; i++) b[i] = (byte)(i * 5);
        return b;
    }

    // ---- what the open unpacks ---------------------------------------------------------------------------------

    /// <summary>Opens the archive at <paramref name="path"/>, with what this thread allocated and how long it took.</summary>
    private static (VppPackage Package, long Allocated, long Milliseconds) OpenMeasured(string path)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var package = CflArchives.Open(path);
        sw.Stop();
        return (package, GC.GetAllocatedBytesForCurrentThread() - before, sw.ElapsedMilliseconds);
    }

    [Fact]
    public void Records_that_share_a_data_block_unpack_it_once_and_only_a_piece_at_a_time()
    {
        // 24 records name one 16 MiB block: unpacked once per record, they would pass the 64 MiB the open may unpack
        const int Size = 16 << 20;
        byte[] zeros = new byte[Size];
        var records = new List<SyntheticCflEntry> { new("shared00.tga", zeros) };
        records.AddRange(Enumerable.Range(1, 23).Select(i => new SyntheticCflEntry($"shared{i:D2}.tga", zeros) { SharesBlockOf = 0 }));
        records.Add(new SyntheticCflEntry("same.tga", zeros) { SharesBlockOf = 0 });
        records.Add(new SyntheticCflEntry("SAME.tga", zeros) { SharesBlockOf = 0 });
        using var temp = new TempFolder();
        string path = temp.Write("shared.cfl", SyntheticCfl.Build([.. records]));
        Assert.True(CflArchives.CheckBudgetFor(new FileInfo(path).Length) < 24L * Size);

        var (package, allocated, _) = OpenMeasured(path);
        var info = package.Cfl!;
        Assert.Equal(26, package.Count);
        Assert.Empty(info.Problems);
        Assert.Empty(info.NotChecked);
        Assert.All(package.Items, i => Assert.False(((CflEntrySource)i.Source).NotChecked));
        Assert.True(Assert.Single(info.ListedTwice).SameData);
        Assert.DoesNotContain(VppValidator.Validate(package), p => p.Code == "CFL005");
        // the check holds the packed block, never an unpacked entry
        Assert.True(allocated < Size, $"the open allocated {allocated:N0} bytes");
        Assert.Equal(zeros, package.Items[25].Source.ReadAll());
    }

    [Fact]
    public void A_deflate_bomb_is_not_unpacked_when_the_archive_opens()
    {
        // 32 records naming one 128 MiB stream of zeros: a file of about 130 KB that lists 4 GiB
        const int Size = 128 << 20, Records = 32;
        byte[] zeros = new byte[Size];
        var records = Enumerable.Range(0, Records).Select(i => new SyntheticCflEntry($"bomb{i:D2}.tga", zeros) { SharesBlockOf = i == 0 ? null : 0 }).ToArray();
        using var temp = new TempFolder();
        string path = temp.Write("bomb.cfl", SyntheticCfl.Build(records));
        long length = new FileInfo(path).Length;
        Assert.Equal(CflArchives.MinCheckBudget, CflArchives.CheckBudgetFor(length));

        var (package, allocated, ms) = OpenMeasured(path);
        var info = package.Cfl!;
        Assert.True(ms < 3000, $"a {length:N0}-byte archive listing {(long)Size * Records:N0} bytes took {ms:N0} ms to open");
        Assert.True(allocated < 16 << 20, $"the open allocated {allocated:N0} bytes");
        Assert.Equal(Records, package.Count);
        Assert.Equal(Enumerable.Range(0, Records), info.NotChecked);
        Assert.Equal(CflArchives.MinCheckBudget, info.CheckBudget);
        Assert.Empty(info.Problems);
        Assert.All(package.Items, i => Assert.True(((CflEntrySource)i.Source).NotChecked));
        Assert.Contains("32 files were not checked (see Problems)", info.Banner, StringComparison.Ordinal);
        Assert.StartsWith("32 ", info.Facts(package.Items).Single(f => f.Label == "Not checked").Value, StringComparison.Ordinal);
        var problem = Assert.Single(VppValidator.Validate(package));
        Assert.Equal(("CFL005", VppSeverity.Warning, "bomb00.tga"), (problem.Code, problem.Severity, problem.EntryName));
        Assert.StartsWith("32 entries were not checked when the archive opened (bomb00.tga and others)", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_past_the_check_budget_are_listed_as_not_checked_and_still_read()
    {
        // the 65 MiB entry passes the 64 MiB a small archive's open may unpack: the checks stop there
        byte[] big = new byte[65 << 20];
        big[^1] = 7;
        using var temp = new TempFolder();
        string path = temp.Write("big.cfl", SyntheticCfl.Build(
            new SyntheticCflEntry("first.txt", Text),
            new SyntheticCflEntry("big.tga", big),
            new SyntheticCflEntry("after.txt", Text, CflArchive.Stored),
            new SyntheticCflEntry("first.txt", Text)));
        var package = CflArchives.Open(path);
        var info = package.Cfl!;
        Assert.Equal([1, 2, 3], info.NotChecked);
        Assert.False(((CflEntrySource)package.Items[0].Source).NotChecked);
        Assert.True(((CflEntrySource)package.Items[1].Source).NotChecked);
        Assert.Empty(info.Problems);
        var twice = Assert.Single(info.ListedTwice);
        Assert.True(twice.NotChecked);
        Assert.False(twice.SameData);
        Assert.Equal("first.txt (2 times, not checked)", twice.Text);
        var problems = VppValidator.Validate(package);
        Assert.Contains(problems, p => p.Code == "CFL005" && p.EntryName == "big.tga" && p.Message.StartsWith("3 entries were not checked", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Code == "CFL003" && p.Severity == VppSeverity.Warning);
        // an entry not checked is unpacked (and checked) when it is read
        Assert.Equal(big, package.Items[1].Source.ReadAll());
        Assert.Equal(Text, package.Items[2].Source.ReadAll());
    }

    [Fact]
    public void Copies_of_a_name_listed_twice_are_compared_without_holding_them()
    {
        // 4 copies with different data and 3 with the same: compared by a hash taken while each is checked
        const int Size = 8 << 20;
        var entries = new List<SyntheticCflEntry>();
        for (int i = 0; i < 4; i++)
        {
            var data = new byte[Size];
            Array.Fill(data, (byte)(i + 1));
            entries.Add(new SyntheticCflEntry("dup.tga", data));
        }
        var same = new byte[Size];
        Array.Fill(same, (byte)9);
        entries.AddRange(Enumerable.Range(0, 3).Select(_ => new SyntheticCflEntry("same.tga", same)));
        using var temp = new TempFolder();
        string path = temp.Write("dup.cfl", SyntheticCfl.Build([.. entries]));
        entries.Clear();

        var (package, allocated, _) = OpenMeasured(path);
        var info = package.Cfl!;
        Assert.Empty(info.NotChecked);
        Assert.Equal("dup.tga (4 times, different data)", info.ListedTwice.Single(t => t.Name == "dup.tga").Text);
        Assert.Equal("same.tga (3 times, same data)", info.ListedTwice.Single(t => t.Name == "same.tga").Text);
        Assert.True(allocated < Size, $"the open allocated {allocated:N0} bytes for 7 copies of {Size:N0} bytes");
    }

    // ---- the real archive --------------------------------------------------------------------------------------

    private static string? GameArchive() =>
        LocalPaths.NgageDirectory is { } dir && File.Exists(Path.Combine(dir, "redfaction.cfl")) ? Path.Combine(dir, "redfaction.cfl") : null;

    [Fact]
    public void The_n_gage_archive_reads_every_entry_to_its_size()
    {
        if (GameArchive() is not { } path) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(CflArchive.Probe(stream));
        var cfl = CflArchive.Read(stream, "redfaction.cfl");
        Assert.Equal(0, cfl.Start);
        Assert.Equal(stream.Length, cfl.Length);
        Assert.Equal(1205, cfl.Entries.Count);
        Assert.All(cfl.Entries, e => Assert.True(e.IsSupported));
        foreach (var e in cfl.Entries) Assert.Equal((int)e.UnpackedSize, cfl.ReadEntry(stream, e).Length);
        var byType = cfl.Entries.GroupBy(e => Path.GetExtension(e.Name).ToLowerInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}");
        Assert.Equal([".pcx 342", ".pr3 7", ".tga 242", ".txt 209", ".wav 132", ".xff 264", ".xm 9"], byType);
        // the 80 stored entries are all text, and the text starts at the entry's data (after its length field)
        Assert.Equal(80, cfl.Entries.Count(e => e.Compression == CflArchive.Stored));
        var stored = cfl.Entries.First(e => e.Compression == CflArchive.Stored);
        Assert.DoesNotContain(cfl.ReadEntry(stream, stored), b => b < 9);
    }

    [Fact]
    public void The_n_gage_archive_opens_as_a_package_without_problems_but_its_names_listed_twice()
    {
        if (GameArchive() is not { } path) return;
        var package = CflArchives.Open(path);
        Assert.Equal(1205, package.Count);
        Assert.Empty(package.Cfl!.Problems);
        Assert.Empty(package.Cfl.NotChecked);
        // no two records share a data block; of the 9 names listed twice, 3 hold different data
        Assert.Equal(1205, package.Cfl.Archive.Entries.Select(e => e.Offset).Distinct().Count());
        Assert.Equal(9, package.Cfl.ListedTwice.Count);
        Assert.Equal(["txtr_mtl_beigedrt02.pcx", "txtr_mtl_beigedrt10.pcx", "txtr_mtl_chromepipe02_rust.pcx"],
            package.Cfl.ListedTwice.Where(t => !t.SameData).Select(t => t.Name.ToLowerInvariant()).Order(StringComparer.Ordinal));
        var problems = VppValidator.Validate(package);
        Assert.All(problems, p => Assert.Equal("CFL003", p.Code));
        Assert.Equal(9, problems.Count);
    }
}
