using System.Text;
using Cairn.Formats;
using Cairn.Formats.Alpine;
using Cairn.Vpp.Alpine;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;
using Cairn.Workspace;

namespace Cairn.Vpp.Tests;

/// <summary>Alpine Faction cache packs (.cache): synthetic packs, the reader's refusals, and the game's map thumbnails when present.</summary>
public sealed class CachePackTests
{
    private static readonly byte[] Jpeg = [.. SyntheticCachePack.JpegHead(), 1, 2, 3, 4];
    private static readonly byte[] Text = Encoding.ASCII.GetBytes("just some notes");

    private static CachePack Read(byte[] bytes) => CachePack.Read(new MemoryStream(bytes), "test.cache");

    [Fact]
    public void Reads_the_index_with_offsets_from_the_start_of_the_file()
    {
        byte[] bytes = SyntheticCachePack.Build(0x1122334455667788, ("dm01.rfl", 7, Jpeg), ("notes.txt", 0xFFFFFFFFFFFFFFFF, Text), ("empty", 0, []));
        var pack = Read(bytes);
        Assert.Equal(0x1122334455667788UL, pack.WriteId);
        Assert.Equal(["dm01.rfl", "notes.txt", "empty"], pack.Entries.Select(e => e.Name));
        Assert.Equal([7UL, 0xFFFFFFFFFFFFFFFF, 0UL], pack.Entries.Select(e => e.Tag));
        long indexEnd = CachePack.HeaderBytes + (18 + 8) + (18 + 9) + (18 + 5);
        Assert.Equal(indexEnd, pack.DataStart);
        Assert.Equal(indexEnd, pack.Entries[0].Offset);
        Assert.Equal(indexEnd + Jpeg.Length, pack.Entries[1].Offset);
        Assert.Equal(bytes.Length, pack.Entries[2].Offset);
        Assert.Equal(Jpeg.Length + Text.Length, pack.DataBytes);
        Assert.Equal(Jpeg, bytes.AsSpan((int)pack.Entries[0].Offset, (int)pack.Entries[0].Size).ToArray());
        Assert.Equal([CachePack.Crc32(Jpeg), CachePack.Crc32(Text), 0u], pack.Entries.Select(e => e.Crc));
        Assert.All(pack.Entries, e => Assert.True(CachePack.DataMatches(new MemoryStream(bytes), e)));
    }

    [Fact]
    public void The_crc_is_zlibs()
    {
        Assert.Equal(0xCBF43926u, CachePack.Crc32("123456789"u8));
        Assert.Equal(0xCBF43926u, CachePack.Crc32("6789"u8, CachePack.Crc32("12345"u8)));
        Assert.Equal(0u, CachePack.Crc32([]));
    }

    [Fact]
    public void Data_that_does_not_match_its_crc_is_corrupt_but_the_pack_is_still_read()
    {
        byte[] bytes = SyntheticCachePack.Build(1, ("a", 0, Jpeg), ("b", 0, Text));
        bytes[^1] ^= 0xFF;
        var pack = Read(bytes);
        Assert.True(CachePack.DataMatches(new MemoryStream(bytes), pack.Entries[0]));
        Assert.False(CachePack.DataMatches(new MemoryStream(bytes), pack.Entries[1]));
    }

    [Fact]
    public void An_empty_pack_is_valid()
    {
        var pack = Read(SyntheticCachePack.Build(1));
        Assert.Empty(pack.Entries);
        Assert.Equal(CachePack.HeaderBytes, pack.DataStart);
    }

    [Fact]
    public void Names_are_kept_byte_for_byte()
    {
        var pack = Read(SyntheticCachePack.Build(1, ("café level.rfl", 0, Jpeg)));
        Assert.Equal("café level.rfl", pack.Entries[0].Name);
    }

    [Theory]
    [InlineData("another program's cache")]
    [InlineData("AFC")]
    public void Another_programs_file_is_not_a_cache_pack(string content)
    {
        var ex = Assert.Throws<AssetFormatException>(() => Read(Encoding.ASCII.GetBytes(content)));
        Assert.Contains("not an Alpine Faction cache pack", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_version_is_refused_by_number()
    {
        var ex = Assert.Throws<AssetFormatException>(() => Read(SyntheticCachePack.Build(CachePack.Signature, 2, 1, ("a", 0, Jpeg))));
        Assert.Contains("version 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_header_is_damaged()
    {
        byte[] bytes = SyntheticCachePack.Build(1)[..12];
        var ex = Assert.Throws<AssetFormatException>(() => Read(bytes));
        Assert.Contains("is damaged", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_count_the_file_cannot_hold_is_refused_before_reading()
    {
        byte[] bytes = SyntheticCachePack.Build(1, ("a", 0, Jpeg));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 16);
        var ex = Assert.Throws<AssetFormatException>(() => Read(bytes));
        Assert.Contains("more than its", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_name_is_damaged()
    {
        var ex = Assert.Throws<AssetFormatException>(() => Read(SyntheticCachePack.Build(1, ("", 0, Jpeg))));
        Assert.Contains("empty name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_stored_twice_is_damaged_but_names_differing_in_case_are_not()
    {
        var ex = Assert.Throws<AssetFormatException>(() => Read(SyntheticCachePack.Build(1, ("a.rfl", 0, Jpeg), ("a.rfl", 0, Jpeg))));
        Assert.Contains("stored twice", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, Read(SyntheticCachePack.Build(1, ("a.rfl", 0, Jpeg), ("A.rfl", 0, Jpeg))).Entries.Count);
    }

    [Fact]
    public void Data_must_end_exactly_at_the_end_of_the_file()
    {
        byte[] bytes = SyntheticCachePack.Build(1, ("a", 0, Jpeg));
        Assert.Contains("follow the index", Assert.Throws<AssetFormatException>(() => Read([.. bytes, 0])).Message, StringComparison.Ordinal);
        Assert.Contains("follow the index", Assert.Throws<AssetFormatException>(() => Read(bytes[..^1])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_cut_short_is_damaged()
    {
        byte[] bytes = SyntheticCachePack.Build(1, ("a_long_level_name.rfl", 0, []));
        // room for the count (one entry fits 50 bytes), but the entry's tag, size and CRC are cut off
        var ex = Assert.Throws<AssetFormatException>(() => Read(bytes[..50]));
        Assert.Contains("index ends inside entry 1", ex.Message, StringComparison.Ordinal);
    }

    // ---- as a package ------------------------------------------------------------------------------------------

    [Fact]
    public void Opens_read_only_with_the_datas_type_added_where_the_name_does_not_say_it()
    {
        using var temp = new TempFolder();
        string path = temp.Write("map_thumbs.cache", SyntheticCachePack.Build(9,
            ("dm01.rfl", 0, Jpeg), ("shot.jpeg", 0, Jpeg), ("notes.txt", 0, Text), ("noext", 0, Jpeg), ("empty.rfl", 0, [])));
        var package = CachePacks.Open(path);
        Assert.True(package.IsReadOnly);
        Assert.NotNull(package.Cache);
        Assert.Equal(["dm01.rfl.jpg", "shot.jpeg", "notes.txt", "noext.jpg", "empty.rfl"], package.Items.Select(i => i.Name));
        Assert.Equal(2, package.Cache!.Typed);
        Assert.False(package.IsModified);
        Assert.All(package.Items, i => Assert.Equal(VppItemState.Original, i.State));
        Assert.Equal("dm01.rfl", package.Cache.EntryOf(package.Items[0])!.Name);
        Assert.Equal(Jpeg, package.Items[0].Source.ReadAll());
        Assert.Equal(Text, package.Items[2].Source.ReadAll());
        Assert.Contains("read only", package.Cache.Banner, StringComparison.Ordinal);
        Assert.Contains("dm01.rfl.jpg", package.Cache.Banner, StringComparison.Ordinal);
    }

    [Fact]
    public void Follows_none_of_the_packfile_rules()
    {
        using var temp = new TempFolder();
        string longName = new string('x', 80) + ".rfl";
        string path = temp.Write("odd.cache", SyntheticCachePack.Build(1, (longName, 0, Jpeg), ("Twin.rfl", 0, Text), ("twin.rfl", 0, Text), ("empty", 0, [])));
        var problems = VppValidator.Validate(CachePacks.Open(path));
        Assert.All(problems, p => Assert.Equal(VppSeverity.Info, p.Severity));
        Assert.Contains(problems, p => p.Code == "ACP001" && p.Message.Contains("Twin.rfl / twin.rfl", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Code == "ACP002" && p.EntryName == "empty");
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void A_corrupt_entry_is_listed_with_a_warning()
    {
        using var temp = new TempFolder();
        byte[] bytes = SyntheticCachePack.Build(1, ("dm01.rfl", 0, Jpeg), ("dm02.rfl", 0, Jpeg));
        bytes[^1] ^= 0xFF;
        var package = CachePacks.Open(temp.Write("map_thumbs.cache", bytes));
        Assert.Equal(["dm01.rfl.jpg", "dm02.rfl.jpg"], package.Items.Select(i => i.Name));
        Assert.Equal([1], package.Cache!.Corrupt);
        var problem = Assert.Single(VppValidator.Validate(package));
        Assert.Equal(("ACP003", VppSeverity.Warning, "dm02.rfl.jpg"), (problem.Code, problem.Severity, problem.EntryName));
    }

    [Fact]
    public void Another_programs_cache_file_is_refused_with_the_reason()
    {
        using var temp = new TempFolder();
        string path = temp.Write("thumbnails.cache", Encoding.ASCII.GetBytes("not ours at all"));
        var ex = Assert.Throws<AssetFormatException>(() => CachePacks.Open(path));
        Assert.Contains("'thumbnails.cache' is not an Alpine Faction cache pack", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_games_map_thumbnails_are_listed_as_jpegs_and_left_untouched()
    {
        string? dir = LocalPaths.GameDirectory;
        string? path = dir is null ? null : Path.Combine(dir, "alpine_cache", "map_thumbs.cache");
        if (path is null || !File.Exists(path)) return; // no game folder with a map thumbnail cache on this machine
        var before = VppArchiveStamp.Of(path);
        var package = CachePacks.Open(path);
        Assert.NotEmpty(package.Items);
        foreach (var item in package.Items)
        {
            var stored = package.Cache!.EntryOf(item)!;
            Assert.Equal(stored.Name + ".jpg", item.Name);
            using var stream = item.Source.Open();
            Assert.Equal(0xFF, stream.ReadByte());
            Assert.Equal(0xD8, stream.ReadByte());
        }
        Assert.DoesNotContain(VppValidator.Validate(package), p => p.Severity != VppSeverity.Info);
        Assert.Equal(before, VppArchiveStamp.Of(path));
    }
}
