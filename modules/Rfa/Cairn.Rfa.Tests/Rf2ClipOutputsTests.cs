using System.Text;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Where a converted Red Faction II clip goes (<see cref="Rf2ClipOutputs"/>): it keeps its .rfa name, so it must never
/// take the place of a Red Faction II clip (the source or another, a file or a packfile entry), even when replacing; a
/// Red Faction clip in the way is replaced as asked. Synthetic files in a temporary folder.
/// </summary>
public sealed class Rf2ClipOutputsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairn-rf2out-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private static readonly byte[] Rf2 = SyntheticRf2Clip.SampleSwing();
    private static readonly byte[] Rf1 = RfaWriter.Write(RfaReader.Read(Rf2, "x.rfa") with { Version = 8 });

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string FolderPath(string name, bool replace, params string[] sources) =>
        Rf2ClipOutputs.FolderPath(_root, name, replace, File.Exists, p => Rf2ClipOutputs.MustKeepFile(p, Rf2ClipOutputs.PathSet(sources)));

    [Fact]
    public void IsRf2ClipFile_ReadsTheHead()
    {
        Assert.True(Rf2ClipOutputs.IsRf2ClipFile(Write("a.rfa", Rf2)));
        Assert.False(Rf2ClipOutputs.IsRf2ClipFile(Write("b.rfa", Rf1)));
        Assert.False(Rf2ClipOutputs.IsRf2ClipFile(Write("c.rfa", [0x56, 0x4D])));
        Assert.False(Rf2ClipOutputs.IsRf2ClipFile(Path.Combine(_root, "missing.rfa")));
        Assert.False(Rf2ClipOutputs.IsRf2ClipFile(""));
    }

    [Fact]
    public void FolderPath_NeverReplacesARedFactionIIClip()
    {
        Write("walk.rfa", Rf2);
        Assert.Equal(Path.Combine(_root, "walk (2).rfa"), FolderPath("walk.rfa", replace: true));
        Assert.Equal(Path.Combine(_root, "walk (2).rfa"), FolderPath("walk.rfa", replace: false));
        // the free names skip taken ones, and a Red Faction II clip under a free name too
        Write("walk (2).rfa", Rf1);
        Write("walk (3).rfa", Rf2);
        Assert.Equal(Path.Combine(_root, "walk (4).rfa"), FolderPath("walk.rfa", replace: true));
    }

    [Fact]
    public void FolderPath_ReplacesARedFactionClipOnlyWhenAsked()
    {
        Write("run.rfa", Rf1);
        Assert.Equal(Path.Combine(_root, "run.rfa"), FolderPath("run.rfa", replace: true));
        Assert.Equal(Path.Combine(_root, "run (2).rfa"), FolderPath("run.rfa", replace: false));
        Assert.Equal(Path.Combine(_root, "new.rfa"), FolderPath("new.rfa", replace: false));
    }

    [Fact]
    public void FolderPath_NeverReplacesAFileBeingConverted()
    {
        // the source by its path, whatever it holds (here a work copy's twin that no longer reads as a Red Faction II clip)
        string source = Write("talk.rfa", Rf1);
        Assert.Equal(Path.Combine(_root, "talk (2).rfa"), FolderPath("talk.rfa", replace: true, source));
        Assert.Equal(Path.Combine(_root, "talk (2).rfa"), FolderPath("talk.rfa", replace: true, Path.Combine(_root, ".", "TALK.rfa")));
    }

    /// <summary>A packfile group file: loose (files below the game folder) or packed into one .packfile.</summary>
    private static byte[] Toc(string group, bool packed, params string[] names)
    {
        using var ms = new MemoryStream();
        void Text(string s) { ms.Write(Encoding.ASCII.GetBytes(s)); ms.WriteByte(0); }
        void U32(int v) => ms.Write(BitConverter.GetBytes(v));
        Text(group);
        Text($@"pc_media\{group}\");
        U32(packed ? 2 : 0);
        if (packed)
        {
            U32(1);
            Text($@"pc_media\{group}\{group}.packfile");
            U32(names.Length);
            for (int i = 0; i < names.Length; i++) { Text(names[i]); U32(Rf2.Length); U32(i * Rf2.Length); }
        }
        else
        {
            U32(names.Length);
            foreach (string name in names) { Text(name); U32(Rf2.Length); Text($@"pc_media\{group}\{name}"); }
        }
        return ms.ToArray();
    }

    [Fact]
    public void LooseFiles_ResolvesALooseGroupsEntriesToTheirFiles()
    {
        string toc = Write(Path.Combine("game", "pc_media", "anims.toc_group"), Toc("anims", packed: false, "walk.rfa", "run.rfa", "guard.rfc"));
        var files = Rf2ClipOutputs.LooseFiles(toc, ["WALK.rfa", "run.rfa", "absent.rfa"]);
        Assert.Equal([Path.Combine(_root, "game", "pc_media", "anims", "walk.rfa"), Path.Combine(_root, "game", "pc_media", "anims", "run.rfa")], files);

        // a packed group's entries are not files; another kind of file, a missing or damaged group: none
        string packed = Write(Path.Combine("game", "pc_media", "packed.toc_group"), Toc("packed", packed: true, "walk.rfa"));
        Assert.Empty(Rf2ClipOutputs.LooseFiles(packed, ["walk.rfa"]));
        Assert.Empty(Rf2ClipOutputs.LooseFiles(Write("mod.vpp", [1, 2, 3]), ["walk.rfa"]));
        Assert.Empty(Rf2ClipOutputs.LooseFiles(Path.Combine(_root, "missing.toc_group"), ["walk.rfa"]));
        Assert.Empty(Rf2ClipOutputs.LooseFiles(Write("bad.toc_group", [1, 2, 3]), ["walk.rfa"]));
        Assert.Empty(Rf2ClipOutputs.LooseFiles(null, ["walk.rfa"]));
    }

    [Fact]
    public void PackfileNames_KeepsTheSourceEntryAndRedFactionIIClips()
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["walk.rfa"] = Rf2, ["walk (2).rfa"] = Rf1, ["run.rfa"] = Rf2, ["old.rfa"] = Rf1, ["guard.v3c"] = [1],
        };
        byte[] tex = [7];
        var named = Rf2ClipOutputs.PackfileNames(
            [("walk.rfa", Rf1), ("run.rfa", Rf1), ("old.rfa", Rf1), ("guard.v3c", [2]), ("guard_body.tga", tex)],
            ["walk.rfa"], entries.Keys, name => entries.TryGetValue(name, out var b) ? b : null);
        // walk.rfa is converted (a source), run.rfa is a Red Faction II clip: both kept, the outputs take free names;
        // old.rfa is a Red Faction clip and the mesh is not a clip: they keep their names (the add replaces or keeps both)
        Assert.Equal(["walk (3).rfa", "run (2).rfa", "old.rfa", "guard.v3c", "guard_body.tga"], named.Select(f => f.Name));
        Assert.Same(tex, named[4].Bytes);
    }

    [Fact]
    public void PackfileNames_SourceEntryIsKeptEvenWhenItCannotBeRead()
    {
        var named = Rf2ClipOutputs.PackfileNames([("walk.rfa", Rf1)], ["walk.rfa"], ["walk.rfa"], _ => throw new IOException("gone"));
        Assert.Equal("walk (2).rfa", Assert.Single(named).Name);
        // an unreadable existing entry that is not a source is left to the add
        named = Rf2ClipOutputs.PackfileNames([("run.rfa", Rf1)], [], ["run.rfa"], _ => throw new IOException("gone"));
        Assert.Equal("run.rfa", Assert.Single(named).Name);
    }
}
