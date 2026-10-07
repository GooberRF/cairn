using Cairn.Workspace;

namespace Cairn.Rfa.Tests;

public class WorkspaceTests
{
    private sealed record ViewportDefaults(bool ShowSkeleton, double Fov);

    [Fact]
    public void SettingsRoundTripIncludingTheFreeFormStore()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        var settings = new AppSettings
        {
            Theme = AppTheme.Dark,
            GameDirectory = @"C:\Games\RF",
            SearchFolders = [@"D:\mods"],
            RecentFiles = [@"D:\a.rfa"],
            Layout = { ["library"] = 240 },
            Panels = { ["timeline"] = false },
        };
        settings.Set("viewport", new ViewportDefaults(true, 55));
        settings.Set("previewMesh:park_jeep_driver.rfa", "miner.v3c");
        Assert.True(SettingsStore.Save(settings, path));

        var loaded = SettingsStore.Load(path);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(@"C:\Games\RF", loaded.GameDirectory);
        Assert.Equal([@"D:\mods"], loaded.SearchFolders);
        Assert.Equal(240, loaded.Layout["library"]);
        Assert.False(loaded.Panels["timeline"]);
        Assert.Equal(new ViewportDefaults(true, 55), loaded.Get<ViewportDefaults>("viewport"));
        Assert.Equal("miner.v3c", loaded.Get<string>("previewMesh:park_jeep_driver.rfa"));
        Assert.Equal(7, loaded.Get("missing", 7));
        Assert.Equal(3, loaded.Get("viewport", 3)); // wrong type: the fallback, not an exception

        var clone = loaded.Clone();
        clone.Set("viewport", (ViewportDefaults?)null);
        clone.SearchFolders.Add("x");
        Assert.NotNull(loaded.Get<ViewportDefaults>("viewport"));
        Assert.Single(loaded.SearchFolders);
    }

    [Fact]
    public void DamagedSettingsYieldDefaults()
    {
        using var temp = new TempFolder();
        Assert.Equal(AppTheme.System, SettingsStore.Load(temp.Write("bad.json", "{ not json")).Theme);
        var nulls = SettingsStore.Load(temp.Write("nulls.json", "{ \"searchFolders\": null, \"values\": null }"));
        Assert.Empty(nulls.SearchFolders);
        Assert.Empty(nulls.Values);
        Assert.Empty(SettingsStore.Load(temp.File("missing.json")).RecentFiles);
    }

    [Fact]
    public void AtomicWritesReplaceFilesAndLeaveNoTemporaries()
    {
        using var temp = new TempFolder();
        string path = temp.File("clip.rfa");
        AtomicFile.WriteAllBytes(path, [1, 2, 3]);
        AtomicFile.WriteAllBytes(path, [4, 5]);
        Assert.Equal([4, 5], AtomicFile.ReadAllBytes(path));
        AtomicFile.WriteAllText(temp.File("a.txt"), "héllo");
        Assert.Equal("héllo", File.ReadAllText(temp.File("a.txt")));
        Assert.Equal(2, Directory.GetFiles(temp.Path).Length);
        Assert.Throws<FileTooLargeException>(() => AtomicFile.ReadAllBytes(path, maxBytes: 1));
    }

    [Fact]
    public void RecentFilesAreNewestFirstWithoutDuplicates()
    {
        var backing = new List<string>();
        var recent = new RecentFilesList(backing, capacity: 3);
        recent.Add(@"C:\a.rfa");
        recent.Add(@"C:\b.rfa");
        recent.Add(@"c:\A.RFA");
        recent.Add(@"C:\c.rfa");
        recent.Add(@"C:\d.rfa");
        Assert.Equal([@"C:\d.rfa", @"C:\c.rfa", @"c:\A.RFA"], recent.Items);
        Assert.Same(backing, recent.Items);
        Assert.True(recent.Remove(@"C:\C.rfa"));
    }

    [Fact]
    public void RecentArchiveEntriesSitBesidePlainPaths()
    {
        // a list saved by an older version (plain paths only) loads unchanged; entries are archive path + '|' + name
        var backing = new List<string> { @"C:\maps\a.rfa" };
        var recent = new RecentFilesList(backing);
        string entry = RecentFilesList.EntryReference(@"C:\maps\pack.vpp", "pack_info.tbl");
        Assert.Equal(@"C:\maps\pack.vpp|pack_info.tbl", entry);
        recent.Add(entry);
        recent.Add(@"c:\MAPS\pack.vpp|pack_info.tbl"); // the same entry, other spelling of the archive path
        Assert.Equal([@"c:\MAPS\pack.vpp|pack_info.tbl", @"C:\maps\a.rfa"], recent.Items);
        Assert.True(RecentFilesList.TryParseEntry(recent.Items[0], out string archive, out string name));
        Assert.Equal((@"c:\MAPS\pack.vpp", "pack_info.tbl"), (archive, name));
        Assert.False(RecentFilesList.TryParseEntry(@"C:\maps\a.rfa", out _, out _));
        Assert.False(RecentFilesList.TryParseEntry(@"C:\maps\pack.vpp|", out _, out _));
        Assert.True(RecentFilesList.TryParseEntry(@"C:\p.vpp|odd|name.tbl", out _, out string odd));
        Assert.Equal("odd|name.tbl", odd);
        recent.RemoveMissing(); // neither archive exists
        Assert.Empty(recent.Items);
    }

    [Fact]
    public void RecoveryStoresBinaryDocuments()
    {
        using var temp = new TempFolder();
        var store = new RecoveryStore(temp.Path);
        byte[] data = [0, 1, 2, 255, 0x56, 0x4D, 0x56, 0x46];
        Assert.True(store.Save("tab:1", @"D:\clips\stand.rfa", "stand.rfa", "rfa", data));
        Assert.True(store.Save("tab:2", null, "untitled.v3c", "v3c", []));
        File.WriteAllText(Path.Combine(temp.Path, "junk.json"), "{ broken");

        var list = store.List();
        Assert.Equal(2, list.Count);
        var stand = list.Single(s => s.Id == "tab:1");
        Assert.Equal(data, stand.Data);
        Assert.Equal("rfa", stand.DocumentKind);
        Assert.Equal(@"D:\clips\stand.rfa", stand.OriginalPath);

        store.Discard("tab:1");
        Assert.Single(store.List());
        store.Clear();
        Assert.Empty(store.List());
    }
}
