using System.Collections.Concurrent;
using Cairn.Rfa.Assets;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.SampleGen;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Open packfiles as library sources (L14): a packfile's clips and meshes probed from headers only, grouped into
/// skeleton families per game, Red Faction II characters with their skeletons, the ranking of characters for a clip and
/// of clips for a character, and the two games kept apart. Synthetic files only.
/// </summary>
public class PackfileLibraryTests
{
    /// <summary>An in-memory packfile that counts whole reads and head reads per name.</summary>
    private sealed class FakePackfile(string label, Dictionary<string, byte[]> files, bool rf2 = false)
    {
        public ConcurrentDictionary<string, int> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> HeadReads { get; } = new(StringComparer.OrdinalIgnoreCase);

        public PackfileContents Contents => new("key-" + label, label, @"X:\" + label, [.. files.Keys],
            (name, count) =>
            {
                HeadReads.AddOrUpdate(name, 1, (_, n) => n + 1);
                return files.TryGetValue(name, out var b) ? b[..Math.Min(b.Length, count)] : null;
            },
            name =>
            {
                Reads.AddOrUpdate(name, 1, (_, n) => n + 1);
                return files.TryGetValue(name, out var b) ? b : null;
            })
        { IsRedFactionII = rf2 };
    }

    private static byte[] Figure() => V3dWriter.Write(V3dBuilder.Build(SampleFigure.Description(SampleSet.SubmeshName, SampleSet.TextureFile)));

    private static readonly int FigureBones = SampleFigure.StoredBones.Length;

    private static FakePackfile Mixed() => new("mixed.packfile", new(StringComparer.OrdinalIgnoreCase)
    {
        ["figure.v3c"] = Figure(),
        ["figure_b.v3c"] = Figure(),
        ["idle.rfa"] = RfaWriter.Write(SampleClips.Idle()),
        ["guard.rfc"] = SyntheticRf2Mesh.SampleCharacter(),
        ["zz_other.rfc"] = SyntheticRf2Mesh.SampleCharacter(),
        ["crate.rfm"] = SyntheticRf2Mesh.SampleStatic(),
        ["guard_swing.rfa"] = SyntheticRf2Clip.SampleSwing(),
        ["broken.rfa"] = [1, 2, 3, 4, 5, 6, 7, 8],
        ["readme.txt"] = "hello"u8.ToArray(),
    }, rf2: true);

    [Fact]
    public void Build_lists_clips_and_meshes_reading_clip_headers_only()
    {
        var pack = Mixed();
        var library = PackfileLibrary.Build(pack.Contents);

        Assert.Equal(["broken.rfa", "guard_swing.rfa", "idle.rfa"], library.Clips.Select(c => c.Name));
        Assert.Equal(["crate.rfm", "figure.v3c", "figure_b.v3c", "guard.rfc", "zz_other.rfc"], library.Meshes.Select(m => m.Name));
        Assert.Equal(1, library.Errors);
        Assert.NotNull(library.FindClip("broken")?.Error);
        // clips: the header only, never the whole file; a Red Faction II static mesh and other files: not read at all
        foreach (string clip in new[] { "idle.rfa", "guard_swing.rfa", "broken.rfa" })
        {
            Assert.Equal(0, pack.Reads.GetValueOrDefault(clip));
            Assert.Equal(1, pack.HeadReads.GetValueOrDefault(clip));
        }
        Assert.Equal(0, pack.Reads.GetValueOrDefault("crate.rfm") + pack.HeadReads.GetValueOrDefault("crate.rfm"));
        Assert.Equal(0, pack.Reads.GetValueOrDefault("readme.txt") + pack.HeadReads.GetValueOrDefault("readme.txt"));
        // a Red Faction II character: its skeleton from the start of the file
        Assert.Equal(0, pack.Reads.GetValueOrDefault("guard.rfc"));
        Assert.Equal(1, pack.HeadReads.GetValueOrDefault("guard.rfc"));
        Assert.Equal("5 meshes · 3 clips", library.Summary);
    }

    [Fact]
    public void Items_are_located_in_the_packfile_and_read_from_it()
    {
        var pack = Mixed();
        var library = PackfileLibrary.Build(pack.Contents);
        var idle = library.FindClip("idle.rfa")!;
        Assert.Equal("mixed.packfile", idle.Location.DisplayLocation);
        Assert.Null(idle.Location.FilePath);
        Assert.Null(idle.Location.ArchivePath);
        Assert.Equal(RfaWriter.Write(SampleClips.Idle()), idle.Location.ReadAllBytes());
        Assert.True(library.Contains(idle));
        Assert.False(library.Contains(idle with { }));
        Assert.Equal(8, idle.Facts!.Version);
        Assert.Equal(9, library.FindClip("guard_swing")!.Facts!.Version);
    }

    [Fact]
    public void Items_keep_their_packfile_key_and_game_after_a_newer_snapshot_is_read()
    {
        var pack = Mixed();
        var first = PackfileLibrary.Build(pack.Contents);
        var second = PackfileLibrary.Build(pack.Contents);
        var oldRfc = first.FindMesh("guard.rfc")!;
        var oldFigure = first.FindMesh("figure.v3c")!;

        // the newer snapshot does not hold the old items (by reference), yet they keep what they were read as
        Assert.False(second.Contains(oldRfc));
        Assert.True(PackfileLibrary.IsRedFactionIIMesh(oldRfc));
        Assert.False(PackfileLibrary.IsRedFactionIIMesh(oldFigure));
        Assert.Equal("key-mixed.packfile", PackfileLibrary.KeyOf(oldRfc));
        Assert.Equal("key-mixed.packfile", PackfileLibrary.KeyOf(first.FindClip("idle.rfa")));
        // an equal item from elsewhere (records compare by value) is not a packfile's
        Assert.Null(PackfileLibrary.KeyOf(oldFigure with { }));
        Assert.False(PackfileLibrary.IsRedFactionIIMesh(oldRfc with { }));
    }

    [Fact]
    public void Families_form_per_game_from_bone_lists()
    {
        var library = PackfileLibrary.Build(Mixed().Contents);
        Assert.Equal(2, library.Families.Length);
        var rf1 = library.Families.Single(f => f.Members.Contains("figure.v3c"));
        Assert.Equal(["figure.v3c", "figure_b.v3c"], rf1.Members.ToArray());
        Assert.Equal(FigureBones, rf1.BoneCount);
        var rf2 = library.Families.Single(f => f.Members.Contains("guard.rfc"));
        Assert.Equal(["guard.rfc", "zz_other.rfc"], rf2.Members.ToArray());
        Assert.Equal(["crate.rfm"], library.StaticMeshes.Select(m => m.Name));

        Assert.True(library.IsRedFactionII(library.FindMesh("guard.rfc")!));
        Assert.True(library.IsRedFactionII(library.FindMesh("crate.rfm")!));
        Assert.False(library.IsRedFactionII(library.FindMesh("figure.v3c")!));
        Assert.Equal(2, library.Rf2Bones("guard.rfc")!.Value.Length);
        Assert.Null(library.Rf2Bones("figure.v3c"));
        Assert.Equal(2, library.FindMesh("guard.rfc")!.BoneCount);
    }

    [Fact]
    public void The_games_are_kept_apart()
    {
        var library = PackfileLibrary.Build(Mixed().Contents);
        // a Red Faction II clip: Red Faction II characters only, even of another bone count; a Red Faction one the reverse
        Assert.Equal(["guard.rfc", "zz_other.rfc"], library.CharactersFor(2, redFactionII: true).Select(m => m.Name));
        Assert.Equal(["figure.v3c", "figure_b.v3c"], library.CharactersFor(2, redFactionII: false).Select(m => m.Name));
        Assert.Equal(["guard_swing.rfa"], library.ClipsFor(2, redFactionII: true).Select(c => c.Name));
        Assert.Empty(library.ClipsFor(2, redFactionII: false));
        Assert.Equal(["idle.rfa"], library.ClipsFor(FigureBones, redFactionII: false).Select(c => c.Name));
        Assert.Empty(library.ClipsFor(FigureBones, redFactionII: true));

        Assert.Null(AssetGames.Mismatch("idle.rfa", false, "figure.v3c", false));
        Assert.Null(AssetGames.Mismatch("guard_swing.rfa", true, "guard.rfc", true));
        string? why = AssetGames.Mismatch("guard_swing.rfa", true, "figure.v3c", false);
        Assert.NotNull(why);
        Assert.Contains("Red Faction II clip", why, StringComparison.Ordinal);
        Assert.Contains("mangled", why, StringComparison.Ordinal);
        Assert.Contains("a Red Faction II mesh", AssetGames.Mismatch("idle.rfa", false, "guard.rfc", true), StringComparison.Ordinal);
    }

    [Fact]
    public void Characters_rank_for_a_clip_by_name_then_fit()
    {
        var library = PackfileLibrary.Build(Mixed().Contents);
        var clip = RfaReader.Read(SyntheticRf2Clip.SampleSwing(), "guard_swing.rfa");
        var ranked = library.RankRf2Meshes(clip, "guard_swing.rfa");
        Assert.Equal(["guard.rfc", "zz_other.rfc"], ranked.Select(r => r.Mesh.Name));
        Assert.Same(library.FindMesh("guard.rfc"), ranked[0].Mesh);
        Assert.Equal(ranked[0].JointOffset, ranked[1].JointOffset, 6);
        // a clip of another bone count: none
        var other = RfaReader.Read(SyntheticRf2Clip.Build(0, 160, 0, 0,
            [new SyntheticRf2Clip.Bone(10f, [(0, 0, 0, 0, 16384)], [(0, System.Numerics.Vector3.Zero)])]), "one.rfa");
        Assert.Empty(library.RankRf2Meshes(other, "one.rfa"));
    }

    [Fact]
    public void Clips_rank_for_a_character_by_name_then_fit()
    {
        var bones = LegacyMeshSupport.Rf2Skeleton(SyntheticRf2Mesh.SampleCharacter())!.Value;
        var swing = RfaReader.Read(SyntheticRf2Clip.SampleSwing(), "x.rfa");
        var one = RfaReader.Read(SyntheticRf2Clip.Build(0, 160, 0, 0,
            [new SyntheticRf2Clip.Bone(10f, [(0, 0, 0, 0, 16384)], [(0, System.Numerics.Vector3.Zero)])]), "one.rfa");
        var ranked = Rf2AnimationConverter.RankClips("guard.rfc", bones, [("medm_walk.rfa", swing), ("guard_swing.rfa", swing), ("guard_one.rfa", one)]);
        // the name shared with the mesh first; the one-bone clip does not play on a two-bone character
        Assert.Equal(["guard_swing.rfa", "medm_walk.rfa"], ranked.Select(r => r.Name));
        Assert.All(ranked, r => Assert.True(r.JointOffset >= 0));
    }

    [Fact]
    public void An_entry_that_cannot_be_read_is_an_error_not_an_exception()
    {
        var contents = new PackfileContents("k", "gone.vpp", null, ["lost.rfa", "lost.v3c", "lost.rfc"], (_, _) => null, _ => null);
        var library = PackfileLibrary.Build(contents);
        Assert.Equal(3, library.Errors);
        Assert.All(library.Clips, c => Assert.False(c.IsReadable));
        Assert.All(library.Meshes, m => Assert.False(m.IsReadable));
        Assert.Empty(library.Families);
    }

    [Fact]
    public void Build_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PackfileLibrary.Build(Mixed().Contents, cts.Token));
    }

    [Fact]
    public void A_red_faction_packfile_static_mesh_is_not_red_faction_ii()
    {
        var pack = new FakePackfile("ps2.vpp", new(StringComparer.OrdinalIgnoreCase) { ["crate.rfm"] = [0, 0, 0, 0] });
        var library = PackfileLibrary.Build(pack.Contents);
        Assert.False(library.IsRedFactionII(library.FindMesh("crate.rfm")!));
        Assert.Equal("1 mesh · 0 clips", library.Summary);
    }
}
