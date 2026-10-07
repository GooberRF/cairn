using System.Text;
using Cairn.Assets;
using Cairn.Formats.Tbl;

namespace Cairn.Rfa.Tests;

public class ClipUsageIndexTests
{
    private const string Entities = """
        #Entity Classes
        $Name:                  "guard"
        $V3D Filename:          "guard.vcm"
        +State:                 "stand"                 "g_stand.mvf"
        +State:                 "walk"                  "g_walk.mvf"
        +Action:                "fire_stand"            "g_fire.mvf"              "Gun Fire"
        +Weapon Specific: "Shotgun"
        +State:                 "stand"                 "g_shotgun_stand.mvf"
        +Action:                "fire_stand"            "G_FIRE.mvf"              ""
        $Name:                  "anim_female"
        $V3D Filename:          "nurse.vcm"
        +State:                 "stand"                 "f_stand.mvf"
        $Name:                  "nurse"
        $V3D Filename:          "nurse.vcm"
        +State:                 "stand"                 "n_stand.mvf"
        $Name:                  "crate"
        $V3D Filename:          "crate.v3d"
        #End
        """;

    private const string Weapons = """
        #Primary Weapons
        $Name:                  "Pistol"
        $3rd Person V3D:        "w_pistol.v3d"
        $1st Person Mesh:       "fp_pistol.v3d"
        +State:                 "idle"         "fp_pistol_idle.mvf"
        +Action:                "fire"         "fp_pistol_fire.mvf"     "Pistol Fire"
        $Name:                  "Shotgun"
        $1st Person Mesh:       "fp_shotgun.vcm"
        +State:                 "idle"         "fp_shotgun_idle.mvf"
        #End
        """;

    private const string Multi = """
        #Characters
        $Name:			"mp_nurse"
        $EntityType:	"nurse"
        $EntityAnimType:"anim_female"
        +Custom Fpgun:	"Pistol"		"pistol_normal"
        +Custom Fpgun:	"Shotgun"		"shotgun_arm"
        $Name:			"mp_guard"
        $EntityType:	"guard"
        $EntityAnimType:"guard"
        +Custom Fpgun:	"Shotgun"		"shotgun_arm2"
        #End
        """;

    private const string Fpguns = """
        $Name:	"pistol_normal"
        $Model: "fp_pistol.v3c"
        $Name:	"shotgun_arm"
        $Model: "fp_shotgun_arm.v3c"
        $Name:	"shotgun_arm2"
        $Model: "fp_shotgun_arm.v3c"
        """;

    private static ClipUsageIndex Synthetic() => ClipUsageIndex.FromTexts(Entities, Weapons, Multi, Fpguns);

    [Fact]
    public void UsagesAreFoundByAnySpellingOfTheClip()
    {
        var index = Synthetic();
        var fire = index.UsagesOf("g_fire");
        Assert.Equal(2, fire.Count);
        Assert.Equal(fire, index.UsagesOf("G_Fire.rfa"));
        Assert.Equal(fire, index.UsagesOf(@"C:\some\folder\g_fire.MVF"));
        Assert.Equal(fire, index.UsagesOf("g_fire.mvf"));

        var first = fire[0];
        Assert.Equal("g_fire", first.ClipBaseName);
        Assert.Equal("entity.tbl", first.Table);
        Assert.Equal("guard", first.ClassName);
        Assert.Equal(ClipUsageKind.Action, first.Kind);
        Assert.Equal("fire_stand", first.SlotName);
        Assert.Null(first.WeaponBlock);
        Assert.Equal("Gun Fire", first.Sound);
        Assert.Equal(6, first.Line);
        Assert.Equal("g_fire.rfa", first.DiskName);
        Assert.Equal("Shotgun", fire[1].WeaponBlock);
        Assert.Null(fire[1].Sound);

        Assert.True(index.IsUsed("g_walk.rfa"));
        Assert.False(index.IsUsed("unknown"));
        Assert.False(index.IsUsed(""));
        Assert.Empty(index.UsagesOf("unknown.rfa"));
        Assert.Contains("fp_pistol_idle", index.ClipBaseNames, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(9, index.ClipBaseNames.Count);

        var weaponLine = Assert.Single(index.UsagesOf("fp_pistol_fire"));
        Assert.Equal("weapons.tbl", weaponLine.Table);
        Assert.Equal("Pistol", weaponLine.ClassName);
        Assert.Equal("Pistol Fire", weaponLine.Sound);
    }

    [Fact]
    public void ClassUsagesComeInTableOrder()
    {
        var index = Synthetic();
        var guard = index.UsagesOfClass("GUARD");
        Assert.Equal(["g_stand", "g_walk", "g_fire", "g_shotgun_stand", "G_FIRE"], guard.Select(u => u.ClipBaseName));
        Assert.Equal(guard.Select(u => u.Line).Order(), guard.Select(u => u.Line));
        Assert.Equal(2, index.UsagesOfClass("pistol", "weapons.tbl").Count);
        Assert.Empty(index.UsagesOfClass("pistol"));
    }

    [Fact]
    public void MeshesGetTheirClipListsThroughEveryRoute()
    {
        var index = Synthetic();

        // Entity class mesh (.vcm), any spelling.
        var guard = index.ClipListsForMesh("guard.v3c");
        Assert.Equal(index.ClipListsForMesh("GUARD.VCM"), guard);
        Assert.Equal(index.ClipListsForMesh(@"x\guard"), guard);
        Assert.Equal(2, guard.Count);
        Assert.Equal("entity class mesh", guard[0].Relation);
        Assert.Equal("guard.v3c", guard[0].MeshName);
        Assert.Equal("guard.vcm", guard[0].Mesh.Original);
        Assert.Equal(5, guard[0].Clips.Length);
        Assert.Equal("pc_multi.tbl", guard[1].Table);
        Assert.Equal("mp_guard", guard[1].Character);

        // A multiplayer character: EntityType's mesh, EntityAnimType's clips.
        var nurse = index.ClipListsForMesh("nurse.v3c");
        Assert.Equal(["anim_female", "nurse", "anim_female"], nurse.Select(l => l.ClassName));
        var mp = nurse[2];
        Assert.Equal("pc_multi.tbl", mp.Table);
        Assert.Equal("mp_nurse", mp.Character);
        Assert.Equal("multiplayer character mp_nurse (EntityType nurse uses EntityAnimType anim_female's clips)", mp.Relation);
        Assert.Equal("f_stand", Assert.Single(mp.Clips).ClipBaseName);

        // A weapon's first-person mesh, written .v3d (a character) in weapons.tbl.
        var pistol = Assert.Single(index.ClipListsForMesh("fp_pistol.v3c"));
        Assert.Equal("weapon first-person mesh", pistol.Relation);
        Assert.Equal("fp_pistol.v3c", pistol.MeshName);
        Assert.Equal(pistol, Assert.Single(index.ClipListsForMesh("fp_pistol.v3d")));

        // fpgun models reached through pc_multi's +Custom Fpgun: one list per (model, weapon); the
        // pistol_normal model is the weapon's own first-person mesh and adds nothing.
        var arm = Assert.Single(index.ClipListsForMesh("fp_shotgun_arm.v3c"));
        Assert.Equal("fpgun.tbl", arm.Table);
        Assert.Equal("Shotgun", arm.ClassName);
        Assert.Equal("fpgun model for weapon Shotgun (fpgun.tbl shotgun_arm, shotgun_arm2)", arm.Relation);
        Assert.Equal("fp_shotgun_idle", Assert.Single(arm.Clips).ClipBaseName);

        // A class with no clips gives its mesh no list.
        Assert.Empty(index.ClipListsForMesh("crate.v3m"));
        Assert.Empty(index.ClipListsForMesh(""));
    }

    [Fact]
    public void MeshesForAClipFollowEveryRoute()
    {
        var index = Synthetic();
        Assert.Equal(["guard.v3c"], index.MeshesForClip("g_stand.rfa"));
        Assert.Equal(["nurse.v3c"], index.MeshesForClip("f_stand"));
        Assert.Equal(["fp_shotgun.v3c", "fp_shotgun_arm.v3c"], index.MeshesForClip("fp_shotgun_idle.mvf"));
        Assert.Empty(index.MeshesForClip("nothing"));
    }

    [Fact]
    public void TheEmptyIndexAnswersNothing()
    {
        var empty = ClipUsageIndex.Empty;
        Assert.Empty(empty.Usages);
        Assert.Empty(empty.ClipBaseNames);
        Assert.False(empty.IsUsed("x"));
        Assert.Empty(empty.ClipListsForMesh("miner.v3c"));
        Assert.Empty(empty.MeshesForClip("x"));
        Assert.Empty(empty.TableSources);
        Assert.Same(empty, ClipUsageIndex.Empty);
        Assert.Empty(ClipUsageIndex.Build([], [], []).Usages);
    }

    [Fact]
    public async Task TablesLoadThroughTheResolverAndALooseTableBeatsTheArchivedOne()
    {
        using var temp = new TempFolder();
        string search = temp.SubDirectory("search");
        string archived = Entities.Replace("g_walk.mvf", "archived_walk.mvf");
        File.WriteAllText(Path.Combine(search, "entity.tbl"), Entities);
        File.WriteAllBytes(Path.Combine(search, "tables.vpp"), VppWriter.Build(
        [
            ("entity.tbl", Encoding.Latin1.GetBytes(archived)),
            ("weapons.tbl", Encoding.Latin1.GetBytes(Weapons)),
            ("fpgun.tbl", [0xFF, 0x00, 0x22]), // garbage reads as no entries, not as an error
        ]));
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [search] });

        var index = ClipUsageIndex.Load(resolver);
        Assert.True(index.IsUsed("g_walk"));
        Assert.False(index.IsUsed("archived_walk"));
        Assert.True(index.IsUsed("fp_pistol_idle"));
        Assert.Equal(AssetSourceKind.SearchFolder, index.TableSources["entity.tbl"].Kind);
        Assert.Equal(AssetSourceKind.SearchFolderArchive, index.TableSources["weapons.tbl"].Kind);
        Assert.Equal("tables.vpp", index.TableSources["WEAPONS.TBL"].DisplayLocation);
        Assert.False(index.TableSources.ContainsKey("pc_multi.tbl"));
        Assert.Empty(index.TableErrors);

        var again = await ClipUsageIndex.LoadAsync(resolver);
        Assert.Equal(index.Usages.AsEnumerable(), again.Usages.AsEnumerable());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipUsageIndex.LoadAsync(resolver, new CancellationToken(true)));
    }

    // ── Stock tables ─────────────────────────────────────────────────────────

    private static ClipUsageIndex? Stock()
    {
        if (TestPaths.Tables is not { } dir) return null;
        string? Read(string name) => File.Exists(Path.Combine(dir, name)) ? TblTokenizer.ReadText(Path.Combine(dir, name)) : null;
        if (Read("entity.tbl") is not { } entity) return null;
        return ClipUsageIndex.FromTexts(entity, Read("weapons.tbl"), Read("pc_multi.tbl"), Read("fpgun.tbl"));
    }

    [Fact]
    public void StockTablesGiveTheKnownUsages()
    {
        if (Stock() is not { } index) return;

        // Every uncommented +State:/+Action: line of entity.tbl (1139 + 1168) and weapons.tbl (43 + 137).
        Assert.Equal(1139 + 1168 + 43 + 137, index.Usages.Length);

        var jeep = index.UsagesOf("park_jeep_driver.rfa");
        Assert.Contains(jeep, u => u.ClassName == "miner1" && u.Kind == ClipUsageKind.State && u.SlotName == "jeep_drive"
            && u.Table == "entity.tbl" && u.WeaponBlock is null);
        Assert.Contains("miner.v3c", index.MeshesForClip("park_jeep_driver"));

        // Weapon first-person clips play on the weapon's $1st Person Mesh (fp_glock.v3d is a .v3c).
        Assert.Contains(index.UsagesOf("fp_glock_idle.mvf"),
            u => (u.Table, u.ClassName, u.Kind, u.SlotName) == ("weapons.tbl", "12mm handgun", ClipUsageKind.State, "idle"));
        Assert.Contains(index.UsagesOf("fp_glock_idle"), u => u.ClassName == "Undercover 12mm handgun");
        Assert.Contains("fp_glock.v3c", index.MeshesForClip("fp_glock_idle"));
        Assert.Contains(index.ClipListsForMesh("fp_glock.v3c"), l => l.Relation == "weapon first-person mesh" && l.ClassName == "12mm handgun");

        // pc_multi: the nurse character wears nurse1's mesh and plays multi_female's clips.
        var nurse = index.ClipListsForMesh("nurse1.v3c");
        var mp = Assert.Single(nurse, l => l.Character == "nurse");
        Assert.Equal("multi_female", mp.ClassName);
        Assert.Contains(mp.Clips, u => u.SlotName == "stand" && u.WeaponBlock is null && u.ClipBaseName == "mnr3f_12mm_stand");
        Assert.Contains(nurse, l => l.Table == "entity.tbl" && l.ClassName == "nurse1");

        // fpgun.tbl: fp_shotgun_armA.v3c (the arms many characters see) plays the Shotgun's clips.
        var arm = Assert.Single(index.ClipListsForMesh("fp_shotgun_arma.v3c"));
        Assert.Equal("fpgun.tbl", arm.Table);
        Assert.Equal("Shotgun", arm.ClassName);
        Assert.Contains(arm.Clips, u => u.SlotName == "fire" && u.ClipBaseName == "fp_shotgun_fire_slow");

        // A weapon-specific override: miner1 holding the Shotgun stands with park_shotgun_stand.
        Assert.Contains(index.UsagesOf("park_shotgun_stand"), u => u.ClassName == "miner1" && u.WeaponBlock == "Shotgun" && u.SlotName == "stand");
    }

    [Fact]
    public void TheRealInstallsTablesComeFromTablesVpp()
    {
        if (TestPaths.GameDirectory is not { } game || !File.Exists(Path.Combine(game, "tables.vpp"))) return;
        var index = ClipUsageIndex.Load(new AssetResolver(new AssetResolverOptions { GameDirectory = game }));
        Assert.All(ClipUsageIndex.TableNames, t => Assert.True(index.TableSources.ContainsKey(t), t));
        Assert.Equal("tables.vpp", index.TableSources["entity.tbl"].DisplayLocation, ignoreCase: true);
        Assert.Contains(index.UsagesOf("park_jeep_driver"), u => u.ClassName == "miner1" && u.SlotName == "jeep_drive");
    }
}
