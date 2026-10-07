using System.Text;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Writing;

namespace Cairn.Vpp.Tests;

/// <summary>Saving and recovery must never lose or mix the user's data (independent review findings 2-4, 6-8).</summary>
public sealed class SaveSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cairn-vpp-safety-" + Guid.NewGuid().ToString("N")[..8]);

    public SaveSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static byte[] Bytes(int n, byte seed)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(seed + i * 7);
        return b;
    }

    private static string MakeVpp(string path, params (string Name, byte[] Data)[] entries)
    {
        var pkg = VppPackage.Empty;
        foreach (var (n, d) in entries) pkg = VppEdit.AddBytes(pkg, n, d, VppClashPolicy.KeepBoth).Package;
        using (var fs = File.Create(path)) VppWriter.Write(pkg, fs);
        return path;
    }

    private sealed class SyncProgress(Action<VppSaveProgress> on) : IProgress<VppSaveProgress>
    {
        public void Report(VppSaveProgress value) => on(value);
    }

    private string[] Files() => [.. Directory.GetFiles(_dir).Select(Path.GetFileName).Order()!];

    // ---- finding 3: an added file rewritten while the save runs ---------------------------------------------

    [Theory]
    [InlineData(8192)] // longer
    [InlineData(4096)] // same size, new content
    public void AddedFileRewrittenDuringSave_AbortsAndNamesTheFile(int newSize)
    {
        var src = P("tex.tga");
        File.WriteAllBytes(src, Enumerable.Repeat((byte)'A', 4096).ToArray());
        var pkg = VppEdit.AddFiles(VppPackage.Empty, [src], VppClashPolicy.KeepBoth).Package;
        var target = P("out.vpp");
        bool done = false;
        var progress = new SyncProgress(p =>
        {
            if (p.Phase != VppSavePhase.Writing || done) return;
            done = true;
            File.WriteAllBytes(src, Enumerable.Repeat((byte)'B', newSize).ToArray());
            File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(1));
        });
        var ex = Assert.ThrowsAny<VppSaveException>(() => VppSaver.Save(pkg, target, progress, default, VppSaveOptions.Default));
        Assert.Contains("tex.tga", ex.Message);
        Assert.False(File.Exists(target), "nothing written at the target");
        Assert.Equal(["tex.tga"], Files());
    }

    // ---- finding 6: Keep backup and a failed save -------------------------------------------------------------

    [Fact]
    public void KeepBackup_FailedSaveKeepsTheExistingBak()
    {
        var path = MakeVpp(P("k.vpp"), ("a.tbl", Bytes(100, 1)));
        var before = File.ReadAllBytes(path);
        File.WriteAllBytes(path + ".bak", Encoding.ASCII.GetBytes("the user's previous backup"));
        var pkg = VppEdit.AddBytes(VppPackage.Open(path), "b.tbl", Bytes(50, 2), VppClashPolicy.KeepBoth).Package;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // the game or another tool holds it
            Assert.ThrowsAny<VppSaveException>(() => VppSaver.Save(pkg, path, null, default, new VppSaveOptions(KeepBackup: true)));
        Assert.Equal("the user's previous backup", File.ReadAllText(path + ".bak"));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(["k.vpp", "k.vpp.bak"], Files());
    }

    [Fact]
    public void KeepBackup_SuccessfulSaveMovesThePreviousFileToBak()
    {
        var path = MakeVpp(P("k.vpp"), ("a.tbl", Bytes(100, 1)));
        var before = File.ReadAllBytes(path);
        File.WriteAllBytes(path + ".bak", [1, 2, 3]);
        var pkg = VppEdit.AddBytes(VppPackage.Open(path), "b.tbl", Bytes(50, 2), VppClashPolicy.KeepBoth).Package;
        var saved = VppSaver.Save(pkg, path, null, default, new VppSaveOptions(KeepBackup: true));
        Assert.Equal(2, saved.Count);
        Assert.Equal(before, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(["k.vpp", "k.vpp.bak"], Files());
    }

    // ---- finding 14: the saved file cannot be read back at once -------------------------------------------------

    [Fact]
    public void Reopen_TransientLockForTwoSeconds_IsRetriedAndTheSaveSucceeds()
    {
        var path = MakeVpp(P("r.vpp"), ("a.tbl", Bytes(100, 1)));
        var pkg = VppEdit.AddBytes(VppPackage.Open(path), "b.tbl", Bytes(50, 2), VppClashPolicy.KeepBoth).Package;
        var first = DateTime.MinValue;
        int calls = 0;
        var options = new VppSaveOptions
        {
            ReopenForTest = p =>
            {
                calls++;
                if (first == DateTime.MinValue) first = DateTime.UtcNow;
                if (DateTime.UtcNow - first < TimeSpan.FromSeconds(2)) throw new IOException("locked by a scanner (simulated)");
                return VppPackage.Open(p);
            },
        };
        var saved = VppSaver.Save(pkg, path, null, default, options);
        Assert.True(calls > 1);
        Assert.Equal(path, saved.Path);
        Assert.All(saved.Items, i => Assert.Equal(path, Assert.IsType<ArchiveSource>(i.Source).ArchivePath));
        Assert.Equal(Bytes(50, 2), saved.Items[1].Source.ReadAll());
    }

    [Fact]
    public void Reopen_PersistentFailure_ReportsSavedButUnreadable_AndTheTargetHoldsTheNewData()
    {
        var path = MakeVpp(P("r.vpp"), ("a.tbl", Bytes(100, 1)));
        var pkg = VppEdit.AddBytes(VppPackage.Open(path), "b.tbl", Bytes(50, 2), VppClashPolicy.KeepBoth).Package;
        var options = new VppSaveOptions { ReopenForTest = _ => throw new IOException("locked by a scanner (simulated)") };
        var started = DateTime.UtcNow;
        var ex = Assert.Throws<VppSavedButUnreadableException>(() => VppSaver.Save(pkg, path, null, default, options));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(2), "retried for a few seconds");
        Assert.Equal(path, ex.TargetPath);
        var onDisk = VppPackage.Open(path);
        Assert.Equal(2, onDisk.Count);
        Assert.Equal(Bytes(50, 2), onDisk.Items[1].Source.ReadAll());
        Assert.Equal(["r.vpp"], Files());
    }

    // ---- finding 7: read-only targets and litter --------------------------------------------------------------

    [Fact]
    public void ReadOnlyTarget_IsRefusedWithoutLitter()
    {
        var path = MakeVpp(P("ro.vpp"), ("a.tbl", Bytes(100, 1)));
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var pkg = VppEdit.AddBytes(VppPackage.Open(path), "b.tbl", Bytes(50, 2), VppClashPolicy.KeepBoth).Package;
        var ex = Assert.ThrowsAny<VppSaveException>(() => VppSaver.Save(pkg, path, null, default, VppSaveOptions.Default));
        Assert.Contains("read-only", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(["ro.vpp"], Files());
    }

    [Fact]
    public void StaleTempFilesOfCairnAreSweptOnTheNextSave_OthersKept()
    {
        var path = MakeVpp(P("s.vpp"), ("a.tbl", Bytes(100, 1)));
        string stale = P($"~s.vpp.{Guid.NewGuid():N}.tmp"), staleOld = P($"~s.vpp.{Guid.NewGuid():N}.old");
        string fresh = P($"~s.vpp.{Guid.NewGuid():N}.tmp"), user = P("~s.vpp.notes.tmp"), other = P($"~t.vpp.{Guid.NewGuid():N}.tmp");
        foreach (var f in new[] { stale, staleOld, fresh, user, other }) File.WriteAllBytes(f, [1]);
        foreach (var f in new[] { stale, staleOld, user, other }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-1));
        VppSaver.Save(VppEdit.Remove(VppPackage.Open(path), ["a.tbl"]), path, null, default, VppSaveOptions.Default);
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleOld));
        Assert.True(File.Exists(fresh), "a temp file of a save that may still be running is kept");
        Assert.True(File.Exists(user), "a file that does not follow Cairn's own pattern is kept");
        Assert.True(File.Exists(other), "temp files of other targets are kept");
    }

    // ---- findings 2 and 4: recovery ------------------------------------------------------------------------------

    [Fact]
    public void Recovery_LargeInMemoryReplacementIsSpilledAndRestored()
    {
        var path = MakeVpp(P("r.vpp"), ("big.tga", Bytes(1000, 1)), ("keep.tbl", Bytes(100, 2)));
        var big = Bytes(2 << 20, 3);
        var pkg = VppEdit.Replace(VppPackage.Open(path), "big.tga", new MemorySource(big));
        string spill = P("spill");
        var json = VppRecoveryManifest.Capture(pkg, spillFolder: spill).ToJson();
        Assert.True(json.Length < 100_000, "the large data is not embedded in the manifest");
        var result = VppRecoveryManifest.FromJson(json).Restore();
        Assert.True(result.IsComplete, string.Join("; ", result.Problems));
        Assert.Equal(path, result.Package.Path);
        Assert.Equal(big, result.Package.Find("big.tga")!.Source.ReadAll());
        Assert.Equal(VppItemState.Replaced, result.Package.Find("big.tga")!.State);
    }

    [Fact]
    public void Recovery_UnrestorableEntryIsReportedAndTheRestIsANewDocument()
    {
        var path = MakeVpp(P("r.vpp"), ("big.tga", Bytes(1000, 1)), ("keep.tbl", Bytes(100, 2)));
        var pkg = VppEdit.Replace(VppPackage.Open(path), "big.tga", new MemorySource(Bytes(2 << 20, 3)));
        var result = VppRecoveryManifest.FromJson(VppRecoveryManifest.Capture(pkg).ToJson()).Restore(); // no spill folder
        Assert.False(result.IsComplete);
        Assert.Equal(["big.tga"], result.Problems.Select(p => p.EntryName));
        Assert.Null(result.Package.Path); // never saved over the original without the user's choice
        Assert.Equal(["keep.tbl"], result.Package.Items.Select(i => i.Name));
    }

    [Fact]
    public void Recovery_MissingAddedFileIsReported()
    {
        var path = MakeVpp(P("r.vpp"), ("a.tbl", Bytes(100, 1)));
        var added = P("added.tga");
        File.WriteAllBytes(added, Bytes(500, 4));
        var pkg = VppEdit.AddFiles(VppPackage.Open(path), [added], VppClashPolicy.KeepBoth).Package;
        var json = VppRecoveryManifest.Capture(pkg).ToJson();
        File.Delete(added);
        var result = VppRecoveryManifest.FromJson(json).Restore();
        Assert.Equal(["added.tga"], result.Problems.Select(p => p.EntryName));
        Assert.Null(result.Package.Path);
        Assert.Equal(["a.tbl"], result.Package.Items.Select(i => i.Name));
    }

    [Fact]
    public void Recovery_OriginalChangedOnDisk_ReportsEveryEntryReadFromIt()
    {
        var path = MakeVpp(P("r.vpp"), ("a.tbl", Bytes(1000, 1)), ("b.tbl", Bytes(1000, 2)));
        var pkg = VppEdit.AddBytes(VppPackage.Open(path), "new.tbl", Bytes(10, 5), VppClashPolicy.KeepBoth).Package;
        var json = VppRecoveryManifest.Capture(pkg).ToJson();
        MakeVpp(path, ("a.tbl", Bytes(1000, 77)), ("b.tbl", Bytes(1000, 78))); // another tool re-saved it: same names and sizes
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        var result = VppRecoveryManifest.FromJson(json).Restore();
        Assert.True(result.OriginalChanged);
        Assert.False(result.IsComplete);
        Assert.Equal(["a.tbl", "b.tbl"], result.Problems.Select(p => p.EntryName));
        Assert.Null(result.Package.Path);
        Assert.Equal(["new.tbl"], result.Package.Items.Select(i => i.Name));
    }
}
