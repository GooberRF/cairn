using System.Buffers.Binary;
using Cairn.Formats;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;
using Cairn.Vpp.Writing;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

public sealed class PackageTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryGamePackfile_OpensWithHeaderCount()
    {
        var files = TestData.GamePackfiles();
        if (files.Count == 0) return;
        var failures = new List<string>();
        var broken = new List<string>();
        int entries = 0;
        foreach (var file in files)
        {
            try
            {
                var package = VppPackage.Open(file.FullName);
                uint headerCount;
                using (var stream = file.OpenRead())
                {
                    var header = new byte[16];
                    stream.ReadExactly(header);
                    headerCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                }
                if (package.Count != headerCount) failures.Add($"{file.FullName}: {package.Count} entries, header says {headerCount}");
                if (package.IsModified) failures.Add($"{file.FullName}: freshly opened package reports modified");
                entries += package.Count;
                // Truncated or garbage-directory packfiles open (the directory is complete) but report missing data.
                if (VppValidator.Validate(package).Any(p => p.Code == "VPP011")) broken.Add($"data missing: {file.FullName}");
            }
            catch (AssetFormatException ex)
            {
                broken.Add($"refused ({file.Length:N0} bytes): {file.FullName}: {ex.Message}");
            }
            catch (Exception ex)
            {
                failures.Add($"{file.FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        output.WriteLine($"{files.Count} packfiles, {entries:N0} entries, {failures.Count} failures, {broken.Count} broken in the game data");
        foreach (var b in broken) output.WriteLine(b);
        foreach (var f in failures.Take(50)) output.WriteLine(f);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
        // The scanned folder has a 0-byte stub and 3 corrupt packfiles (two truncated copies, one garbage directory).
        Assert.True(broken.Count <= 4, string.Join(Environment.NewLine, broken));
    }

    [Fact]
    public void ResaveUnmodified_SampleOfGamePackfiles_IsByteIdenticalOrExplained()
    {
        var files = TestData.GamePackfiles().Where(f => f.Length is >= 4096 and < 600L << 20).OrderBy(f => f.Length).ToList();
        if (files.Count == 0) return;
        var sample = new List<FileInfo>();
        const int Picks = 32;
        for (int i = 0; i < Picks; i++) sample.Add(files[(int)((long)i * (files.Count - 1) / (Picks - 1))]);
        var big = files.LastOrDefault(f => f.Length > 100L << 20 && f.Length < 400L << 20);
        if (big is not null) sample.Add(big);
        // Stock Volition packfiles reuse the previous directory block as slack.
        foreach (string stock in new[] { "audio.vpp", "maps1.vpp", "levels1.vpp" })
        {
            if (TestData.Stock(stock) is { } path) sample.Add(new FileInfo(path));
        }
        sample = [.. sample.DistinctBy(f => f.FullName)];

        using var temp = new TempFolder();
        var unexplained = new List<string>();
        int identical = 0;
        foreach (var file in sample)
        {
            var package = VppPackage.Open(file.FullName);
            if (VppValidator.Validate(package).Any(p => p.Code == "VPP011")) { output.WriteLine($"{file.Name}: truncated, skipped"); continue; }
            string target = temp.File("resave.vpp");
            var saved = VppSaver.Save(package, target, null, CancellationToken.None, VppSaveOptions.Default);
            Assert.Equal(package.Count, saved.Count);
            var diff = PackfileComparer.Compare(file.FullName, target, package);
            if (diff.Identical) identical++;
            else
            {
                // Slack and padding are copied from the original, so every well-formed packfile must come back identical.
                output.WriteLine($"{file.Name} ({file.Length:N0} bytes): {diff}");
                unexplained.Add($"{file.Name}: {diff}");
            }
            File.Delete(target);
        }
        output.WriteLine($"{sample.Count} packfiles re-saved, {identical} byte-identical, largest {sample.Max(f => f.Length):N0} bytes");
        Assert.True(sample.Count >= 30 || files.Count < 30);
        Assert.Empty(unexplained);
    }

    [Fact]
    public async Task Edits_ThenSave_ReopenedEntriesMatchSources()
    {
        using var temp = new TempFolder();
        string archive = CopyStock(temp, "edit.vpp") ?? BuildArchive(temp, "edit.vpp");
        var package = VppPackage.Open(archive);
        Assert.True(package.Count >= 3);
        var originals = package.Items.ToDictionary(i => i.Name, TestData.ReadEntry, StringComparer.OrdinalIgnoreCase);

        string fileA = temp.Write("in/added_one.tbl", TestData.Bytes(5000, 11));
        string fileB = temp.Write("in/sub/added_two.txt", TestData.Bytes(2048, 12));
        var add = VppEdit.AddFolder(package, temp.File("in"), VppClashPolicy.Replace);
        Assert.Equal(2, add.Report.Added.Length);
        package = add.Package;
        package = VppEdit.AddBytes(package, "memory.txt", TestData.Bytes(1, 13), VppClashPolicy.Replace).Package;

        string replaced = package.Items[0].Name;
        var replacement = TestData.Bytes(777, 14);
        package = VppEdit.Replace(package, replaced, new MemorySource(replacement));
        Assert.Equal(VppItemState.Replaced, package.Items[0].State);
        string removed = package.Items[1].Name;
        package = VppEdit.Remove(package, [removed.ToUpperInvariant()]);
        Assert.False(package.Contains(removed));
        string renamedFrom = package.Items[1].Name;
        string renamedTo = "renamed_" + renamedFrom;
        if (renamedTo.Length > VppNames.MaxNameBytes) renamedTo = "renamed" + VppNames.ExtensionOf(renamedFrom);
        package = VppEdit.Rename(package, renamedFrom, renamedTo);
        Assert.Equal(VppItemState.Renamed, package.Items[1].State);
        Assert.Equal(renamedFrom, package.Items[1].OriginalName);
        package = VppEdit.Sort(package, VppSortKey.Name);
        Assert.Same(package, VppEdit.Sort(package, VppSortKey.Name));

        var expected = package.Items.ToDictionary(i => i.Name, TestData.ReadEntry, StringComparer.OrdinalIgnoreCase);
        string target = temp.File("edited.vpp");
        var saved = await VppSaver.SaveAsync(package, target);
        var reopened = VppPackage.Open(target);
        Assert.Equal(package.Items.Select(i => i.Name), reopened.Items.Select(i => i.Name));
        Assert.Equal(package.Items.Select(i => i.Name), saved.Items.Select(i => i.Name));
        foreach (var item in reopened.Items)
        {
            Assert.Equal(VppItemState.Original, item.State);
            Assert.Equal(expected[item.Name], TestData.ReadEntry(item));
        }
        Assert.Equal(replacement, TestData.ReadEntry(reopened.Find(replaced)!));
        Assert.Equal(File.ReadAllBytes(fileA), TestData.ReadEntry(reopened.Find("added_one.tbl")!));
        Assert.Equal(File.ReadAllBytes(fileB), TestData.ReadEntry(reopened.Find("added_two.txt")!));
        Assert.Equal(originals[renamedFrom], TestData.ReadEntry(reopened.Find(renamedTo)!));
        Assert.Equal(new FileInfo(target).Length, reopened.ArchiveBytes);
        Assert.False(saved.IsModified);
    }

    [Fact]
    public async Task SaveOverTheSourceArchive_WhileAnEntryStreamIsOpen()
    {
        using var temp = new TempFolder();
        string archive = CopyStock(temp, "self.vpp") ?? BuildArchive(temp, "self.vpp");
        var package = VppPackage.Open(archive);
        var keep = package.Items.Skip(1).ToDictionary(i => i.Name, TestData.ReadEntry, StringComparer.OrdinalIgnoreCase);
        package = VppEdit.Remove(package, [package.Items[0].Name]);
        package = VppEdit.AddBytes(package, "zz_new.tbl", TestData.Bytes(3000, 5), VppClashPolicy.Replace).Package;
        keep["zz_new.tbl"] = TestData.Bytes(3000, 5);

        // A preview still reading from the old packfile must not stop the save.
        using var reader = package.Items[0].Source.Open();
        var saved = await VppSaver.SaveAsync(package, archive, options: new VppSaveOptions(KeepBackup: true));
        Assert.Equal(keep.Count, saved.Count);
        foreach (var item in VppPackage.Open(archive).Items) Assert.Equal(keep[item.Name], TestData.ReadEntry(item));
        Assert.True(File.Exists(archive + ".bak"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.old"));
    }

    [Fact]
    public async Task Cancellation_LeavesTargetUntouched_AndNoTempFile()
    {
        using var temp = new TempFolder();
        string target = temp.Write("target.vpp", TestData.Bytes(4096, 1));
        var before = File.ReadAllBytes(target);
        var package = VppPackage.Empty;
        for (int i = 0; i < 40; i++) package = VppEdit.AddBytes(package, $"e{i}.tbl", TestData.Bytes(1 << 20, i), VppClashPolicy.Replace).Package;

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Phase == VppSavePhase.Writing && p.EntriesDone >= 3) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VppSaver.SaveAsync(package, target, progress, cts.Token));
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task ChangedFileSource_IsReported_NotWritten()
    {
        using var temp = new TempFolder();
        string file = temp.Write("a.tbl", TestData.Bytes(100, 1));
        var package = VppEdit.AddFiles(VppPackage.Empty, [file], VppClashPolicy.Replace).Package;
        File.WriteAllBytes(file, TestData.Bytes(150, 2));
        var problems = VppSaver.CheckBeforeSave(package);
        Assert.Contains(problems, p => p.Code == "VPP010" && p.EntryName == "a.tbl");
        var ex = await Assert.ThrowsAsync<VppSaveBlockedException>(() => VppSaver.SaveAsync(package, temp.File("out.vpp")));
        Assert.Contains(ex.Problems, p => p.Code == "VPP010");
        Assert.False(File.Exists(temp.File("out.vpp")));

        File.Delete(file);
        Assert.Contains(VppSaver.CheckBeforeSave(package), p => p.Code == "VPP010" && p.Message.Contains("no longer exists"));
    }

    [Fact]
    public void Validation_EveryRuleFires()
    {
        var fired = new HashSet<string>();
        void Collect(VppPackage p) { foreach (var problem in VppValidator.Validate(p)) fired.Add(problem.Code); }

        Collect(VppPackage.Empty); // VPP013
        Collect(Raw(("", 1)));                       // VPP001
        Collect(Raw((new string('a', 60) + ".tbl", 1))); // VPP002
        Collect(Raw(("snow\u2603.tbl", 1)));         // VPP003
        Collect(Raw(("dir/a.tbl", 1)));              // VPP004
        Collect(Raw(("A.tbl", 1), ("a.TBL", 1)));    // VPP005
        Collect(Raw(("what?.tbl", 1)));              // VPP014
        Collect(Raw(("noext", 1)));                  // VPP015
        Collect(Raw(("model.blend", 1)));            // VPP009
        Collect(Raw(("empty.tbl", 0)));              // VPP017
        Collect(Raw(("caf\u00E9.tbl", 1)));          // VPP020
        Collect(Raw(("walk.old.rfa", 1)));           // VPP021
        Collect(Raw(("music.OGG", 1)));              // VPP023
        Collect(Raw((new string('n', 28) + ".tga", 1))); // VPP027
        Collect(new VppPackage(null, [new VppItem("huge.tbl", new FakeSource(VppValidator.MaxEntryBytes + 1), VppItemState.Added)])); // VPP006
        Collect(new VppPackage(null, [.. Enumerable.Range(0, 2).Select(i => new VppItem($"b{i}.tbl", new FakeSource(int.MaxValue - 1), VppItemState.Added))])); // VPP008
        Collect(new VppPackage(null, [.. Enumerable.Range(0, 3).Select(i => new VppItem($"h{i}.tbl", new FakeSource(int.MaxValue - 4096), VppItemState.Added))])); // VPP007
        var nothing = new FakeSource(1);
        VppPackage Many(int n) => new(null, [.. Enumerable.Range(0, n).Select(i => new VppItem($"{i}.tbl", nothing, VppItemState.Added))]);
        Collect(Many(VppValidator.MaxEntries + 1));        // VPP016
        var truncated = new VppPackage("x:\\t.vpp", [new VppItem("t.tbl", new ArchiveSource("x:\\t.vpp", 4096, 5000), VppItemState.Original)], new VppArchiveStamp(6144, DateTime.UnixEpoch))
        {
            HeaderArchiveSize = 8192,
        };
        Collect(truncated); // VPP011, VPP026
        foreach (var p in VppValidator.ValidateTargetPath("x:\\" + new string('d', 120) + "\\" + new string('p', 32) + ".vpp")) fired.Add(p.Code); // VPP024, VPP025

        using var temp = new TempFolder();
        string file = temp.Write("c.tbl", [1, 2, 3]);
        var withFile = VppEdit.AddFiles(VppPackage.Empty, [file], VppClashPolicy.Replace).Package;
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        foreach (var p in VppValidator.CheckSources(withFile)) fired.Add(p.Code); // VPP010
        string archive = BuildArchive(temp, "changed.vpp");
        var opened = VppPackage.Open(archive);
        File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddMinutes(5));
        foreach (var p in VppValidator.CheckSources(opened)) fired.Add(p.Code); // VPP012

        Assert.Equal(VppValidator.Rules.Keys.Order(), fired.Order());
    }

    [Fact]
    public void AlpineTypesAndLargePackfiles_AreNotProblems()
    {
        // Alpine Faction is the baseline: its added types and stock-only limits produce no problem.
        Assert.Empty(VppValidator.Validate(Raw(("rock.dds", 1), ("music.ogg", 1), ("photo.png", 1), ("water.atx", 1))));
        var nothing = new FakeSource(1);
        var big = new VppPackage(null, [.. Enumerable.Range(0, 14_000).Select(i => new VppItem($"{i}.tbl", nothing, VppItemState.Added))]);
        Assert.Empty(VppValidator.Validate(big));
        Assert.DoesNotContain(VppValidator.Rules.Keys, k => k is "VPP018" or "VPP019" or "VPP022");
        var dds = Facts.VppFacts.Describe("rock.dds", () => new MemoryStream(new byte[4]), 4);
        Assert.Contains(dds.Rows, r => r.Label == "Game" && r.Value.StartsWith("Loaded by the game", StringComparison.Ordinal));
        Assert.Contains(Facts.VppFacts.Describe("notes.txt", () => new MemoryStream(new byte[4]), 4).Rows, r => r.Label == "Game" && r.Value == "Not loaded by the game");
    }

    [Fact]
    public void LongAssetNames_WarnOnlyAbove31ForShortSlotTypes()
    {
        string Name(int length, string ext) => new string('x', length - ext.Length) + ext;
        bool Fires(string name) => VppValidator.Validate(Raw((name, 1))).Any(p => p.Code == "VPP027" && p.EntryName == name);
        Assert.False(Fires(Name(31, ".tga")));
        Assert.True(Fires(Name(32, ".tga")));
        Assert.True(Fires(Name(59, ".wav")));
        Assert.True(Fires(Name(40, ".vf")));
        Assert.False(Fires(Name(59, ".v3m")));
        Assert.False(Fires(Name(45, ".rfl")));
        Assert.False(Fires("mtl_contrl_panel03_drty-mip2.tga")); // external mip of a 27-character bitmap (stock maps1.vpp)
        Assert.True(Fires(new string('m', 28) + "-mip1.tga"));
    }

    [Fact]
    public void SharedReader_DecodesNamesAsLatin1_AndLookupFoldsAsciiOnly()
    {
        using var temp = new TempFolder();
        var package = TestData.Small(("Grüße.tga", 10), ("Über.tga", 5), ("über.tga", 6));
        Assert.DoesNotContain(VppValidator.Validate(package), p => p.Code == "VPP005");
        string path = VppSaver.Save(package, temp.File("latin.vpp"), null, CancellationToken.None, VppSaveOptions.Default).Path!;
        var archive = Cairn.Formats.Vpp.VppArchive.Open(path);
        Assert.Equal(["Grüße.tga", "Über.tga", "über.tga"], archive.Entries.Select(e => e.Name));
        Assert.Equal(["Grüße.tga", "Über.tga", "über.tga"], VppPackage.Open(path).Items.Select(i => i.Name));
        Assert.True(VppNames.Comparer.Equals("ABC.tga", "abc.TGA"));
        Assert.False(VppNames.Comparer.Equals("Über.tga", "über.tga"));
    }

    [Fact]
    public void Writer_KeepsOriginalSlackAndPadding_OnlyWhileTheEntryListIsUnchanged()
    {
        using var temp = new TempFolder();
        string archive = BuildArchive(temp, "slack.vpp");
        // Put junk in the directory slack and in an entry's padding, as old packers did.
        var bytes = File.ReadAllBytes(archive);
        int count = VppPackage.Open(archive).Count;
        for (int i = 2048 + count * 64; i < 4096; i++) bytes[i] = 0xAB;
        var first = (ArchiveSource)VppPackage.Open(archive).Items[0].Source;
        for (long i = first.Offset + first.Length; i < VppPackage.AlignUp(first.Offset + first.Length); i++) bytes[i] = 0xCD;
        File.WriteAllBytes(archive, bytes);

        var package = VppPackage.Open(archive);
        string same = temp.File("same.vpp");
        VppSaver.Save(package, same, null, CancellationToken.None, VppSaveOptions.Default);
        Assert.Equal(bytes, File.ReadAllBytes(same));

        var renamed = VppEdit.Rename(package, package.Items[1].Name, "other.txt");
        string changed = temp.File("changed.vpp");
        VppSaver.Save(renamed, changed, null, CancellationToken.None, VppSaveOptions.Default);
        var written = File.ReadAllBytes(changed);
        Assert.All(written[(2048 + count * 64)..4096], b => Assert.Equal(0, b));
        Assert.Equal(0xCD, written[first.Offset + first.Length]); // padding of an untouched entry is still its own
    }

    [Fact]
    public async Task NamesAtTheLimit_SaveAndReopen()
    {
        using var temp = new TempFolder();
        string longest = new string('n', VppNames.MaxNameBytes - 4) + ".tbl";
        Assert.Equal(59, longest.Length);
        Assert.True(VppNames.IsValid(longest));
        Assert.False(VppNames.IsValid("x" + longest));
        string latin = "caf\u00E9_" + new string('e', VppNames.MaxNameBytes - 9) + ".tbl";
        Assert.Equal(59, latin.Length);
        var package = TestData.Small((longest, 10), (latin, 20));
        var saved = await VppSaver.SaveAsync(package, temp.File("limit.vpp"));
        Assert.Equal([longest, latin], saved.Items.Select(i => i.Name));

        var tooLong = TestData.Small(("x" + longest, 1));
        Assert.Contains(VppValidator.Validate(tooLong), p => p.Code == "VPP002");
        await Assert.ThrowsAsync<VppSaveBlockedException>(() => VppSaver.SaveAsync(tooLong, temp.File("bad.vpp")));
    }

    [Fact]
    public void ClashPolicies_ReplaceKeepBothSkip()
    {
        var package = TestData.Small(("a.tbl", 5), ("b.tbl", 5));
        var keep = VppEdit.AddBytes(package, "A.TBL", [9], VppClashPolicy.KeepBoth);
        Assert.Equal("A (2).TBL", keep.Report.Renamed.Single().Used);
        var keep3 = VppEdit.AddBytes(keep.Package, "a.tbl", [9], VppClashPolicy.KeepBoth);
        Assert.Equal("a (3).tbl", keep3.Report.Added.Single());

        string limit = new string('q', VppNames.MaxNameBytes - 4) + ".tbl";
        var full = TestData.Small((limit, 1));
        var renamed = VppEdit.AddBytes(full, limit, [1], VppClashPolicy.KeepBoth);
        string used = renamed.Report.Added.Single();
        Assert.Equal(VppNames.MaxNameBytes, used.Length);
        Assert.EndsWith(" (2).tbl", used);

        var skip = VppEdit.AddBytes(package, "B.tbl", [1], VppClashPolicy.Skip);
        Assert.Same(package, skip.Package);
        Assert.Equal("B.tbl", Assert.Single(skip.Report.Skipped));

        var replace = VppEdit.AddBytes(package, "b.TBL", [7, 7], VppClashPolicy.Replace);
        Assert.Equal(2, replace.Package.Count);
        Assert.Equal([7, 7], replace.Package.Find("b.tbl")!.Source.ReadAll());
        Assert.Equal("b.tbl", replace.Package.Items[1].Name);
    }

    [Fact]
    public void Edits_ReturnSameInstanceWhenNothingChanges()
    {
        var package = TestData.Small(("a.tbl", 5), ("b.tbl", 6));
        Assert.Same(package, VppEdit.Remove(package, ["missing.tbl"]));
        Assert.Same(package, VppEdit.Rename(package, "a.tbl", "a.tbl"));
        Assert.Same(package, VppEdit.Replace(package, "missing.tbl", new MemorySource([1])));
        Assert.Same(package, VppEdit.Replace(package, "a.tbl", package.Items[0].Source));
        Assert.Same(package, VppEdit.Sort(package, VppSortKey.OriginalOrder));
        Assert.NotSame(package, VppEdit.Rename(package, "a.tbl", "A.tbl"));
        Assert.Throws<ArgumentException>(() => VppEdit.Rename(package, "a.tbl", "B.TBL"));
        Assert.Throws<ArgumentException>(() => VppEdit.Rename(package, "a.tbl", "x/y.tbl".Replace("x/", "x\u0001")));
        var bySize = VppEdit.Sort(package, VppSortKey.Size, descending: true);
        Assert.Equal(["b.tbl", "a.tbl"], bySize.Items.Select(i => i.Name));
    }

    [Fact]
    public void DirectoryReaderFuzz_OnlyThrowsAssetFormatException()
    {
        using var temp = new TempFolder();
        string archive = BuildArchive(temp, "fuzz.vpp");
        byte[] good = File.ReadAllBytes(archive);
        var random = new Random(1234);
        string path = temp.File("mutant.vpp");
        int rejected = 0;
        for (int round = 0; round < 400; round++)
        {
            byte[] bytes;
            switch (round % 4)
            {
                case 0: bytes = good[..random.Next(0, good.Length)]; break;
                case 1:
                    bytes = (byte[])good.Clone();
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)random.Next(0, int.MaxValue) | (round % 8 == 1 ? 0x80000000u : 0));
                    break;
                case 2:
                    bytes = (byte[])good.Clone();
                    for (int k = 0; k < 8; k++) bytes[random.Next(0, Math.Min(bytes.Length, 4096))] = (byte)random.Next(256);
                    break;
                default:
                    bytes = (byte[])good.Clone();
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2048 + 60 + 64 * random.Next(0, 3)), random.Next(int.MinValue, int.MaxValue));
                    break;
            }
            File.WriteAllBytes(path, bytes);
            try
            {
                var package = VppPackage.Open(path);
                _ = VppValidator.Validate(package);
            }
            catch (AssetFormatException)
            {
                rejected++;
            }
        }
        output.WriteLine($"{rejected} of 400 mutants rejected");
        Assert.True(rejected > 0);
    }

    // ---- helpers ----

    internal static string? CopyStock(TempFolder temp, string name)
    {
        var candidate = TestData.RootPackfiles()
            .Where(f => f.Length is > 20_000 and < 3_000_000)
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(f => { try { return VppPackage.Open(f.FullName).Count >= 4; } catch (AssetFormatException) { return false; } });
        if (candidate is null) return null;
        string target = temp.File(name);
        File.Copy(candidate.FullName, target);
        return target;
    }

    internal static string BuildArchive(TempFolder temp, string name)
    {
        var package = TestData.Small(("one.tbl", 3000), ("two.txt", 2048), ("three.tga", 1), ("four.wav", 5000));
        return VppSaver.Save(package, temp.File(name), null, CancellationToken.None, VppSaveOptions.Default).Path!;
    }

    private static VppPackage Raw(params (string Name, int Length)[] entries) =>
        new(null, [.. entries.Select((e, i) => new VppItem(e.Name, new MemorySource(TestData.Bytes(e.Length, i)), VppItemState.Added))]);

    private sealed class SyncProgress(Action<VppSaveProgress> action) : IProgress<VppSaveProgress>
    {
        public void Report(VppSaveProgress value) => action(value);
    }
}

/// <summary>A source of a given size that is never read (for limit checks).</summary>
internal sealed record FakeSource(long Length) : VppSource
{
    public override long Size => Length;

    public override string Describe() => "fake";

    public override Stream Open() => throw new NotSupportedException();
}
