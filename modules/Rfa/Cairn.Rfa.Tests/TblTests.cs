using Cairn.Formats.Tbl;

namespace Cairn.Rfa.Tests;

public class TblTests
{
    // ── Tokenizer ────────────────────────────────────────────────────────────

    [Fact]
    public void TokenizerSeesKeysStringsSectionsAndSkipsComments()
    {
        const string text = """
            // header comment with "quotes" and $Fake: key
            #Entity Classes
            	$Name:   "miner1"   // trailing comment
            	$Body Temperature(F):	90.0
            	$Corona (Glare) 1:      "Thruster"   "headlamp"
            	+State:  "stand"  "ult2_stand.mvf"
            //	+Action: "commented" "out.mvf" ""
            	$Flags: ("walk" "fly")
            	+Color: {128,128,96}
            	+Base Dir: <0.5, 2.5, 1.0>
            En: "Envirosuit Parker"
            #End
            """;
        var tokens = TblTokenizer.Tokenize(text);
        string joined = string.Join(" ", tokens.Select(t => t.ToString()));
        Assert.Equal(
            "#Entity Classes $Name: \"miner1\" $Body Temperature(F): 90.0 $Corona (Glare) 1: \"Thruster\" \"headlamp\" "
            + "+State: \"stand\" \"ult2_stand.mvf\" $Flags: ( \"walk\" \"fly\" ) +Color: { 128 , 128 , 96 } "
            + "+Base Dir: < 0.5 , 2.5 , 1.0 > En: \"Envirosuit Parker\" #End",
            joined);
        var name = tokens.First(t => t.Kind == TblTokenKind.Key);
        Assert.Equal('$', name.Prefix);
        Assert.Equal("Name", name.Text);
        Assert.Equal(3, name.Line);
        Assert.True(tokens.Single(t => t.Text == "En").IsKey('\0', "en"));
    }

    [Fact]
    public void TokenizerSurvivesOddInput()
    {
        var tokens = TblTokenizer.Tokenize("$Name: \"unterminated\n+State: \"a\" \"b.mvf\"\n+5 -3 $NoColon\n#");
        Assert.Contains(tokens, t => t.Kind == TblTokenKind.String && t.Text == "unterminated");
        Assert.Contains(tokens, t => t.IsKey('+', "State"));
        Assert.Contains(tokens, t => t.Kind == TblTokenKind.Word && t.Text == "+5");
        Assert.Contains(tokens, t => t.Kind == TblTokenKind.Word && t.Text == "$NoColon");
        Assert.Empty(TblTokenizer.Tokenize(""));
        Assert.Empty(TblTokenizer.Tokenize("// only a comment"));
    }

    [Fact]
    public void FieldsCollectTheirValuesAndSection()
    {
        var fields = TblParser.ParseFields("#Weapons\n$Name: \"Gun\"\n$Damage: 12.5\n+Action: \"fire\" \"g_fire.mvf\" \"Gun Fire\"\n");
        Assert.Equal(3, fields.Count);
        Assert.All(fields, f => Assert.Equal("Weapons", f.Section));
        Assert.Equal("Gun", fields[0].String(0));
        Assert.Equal(12.5f, fields[1].Number());
        Assert.Equal("Gun Fire", fields[2].String(2));
        Assert.Null(fields[2].String(3));
    }

    // ── File names ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ult2_stand.mvf", "ult2_stand.rfa", "ult2_stand")]
    [InlineData("Miner.VCM", "Miner.v3c", "Miner")]
    [InlineData("fp_glock.v3d", "fp_glock.v3m", "fp_glock")]
    [InlineData("clip.rfa", "clip.rfa", "clip")]
    [InlineData("skin.tga", "skin.tga", "skin")]
    public void TableFileNamesNormaliseToDiskNames(string table, string disk, string baseName)
    {
        var name = new TblFileName(table);
        Assert.Equal(table, name.Original);
        Assert.Equal(disk, name.DiskName);
        Assert.Equal(baseName, name.BaseName);
    }

    [Fact]
    public void AV3dNameCanBeEitherMeshKind()
    {
        Assert.Equal(["fp_glock.v3m", "fp_glock.v3c"], new TblFileName("fp_glock.v3d").Candidates);
        Assert.True(new TblFileName("").IsEmpty);
    }

    // ── Readers on inline text ───────────────────────────────────────────────

    private const string EntityText = """
        #Entity Classes
        $Name:                  "miner1"
        $V3D Filename:          "miner.vcm"
        $Mass:                  100
        +State:                 "stand"                 "ult2_stand.mvf"
        +State:                 "walk"                  "ult2_walk.mvf"
        	+Footstep Trigger:	3	17
        +Action:                "reload"                "ult2_reload.mvf"              "Ultor Reload"
        +Action:                "fire_stand"            "ult2_firing_stand.mvf"        ""
        //	+Action:                "death_leg_left"        "ult2_Death_leg_Left.mvf"             "Human Death Left 1"
        +Weapon Specific: "Shotgun"
        +Spine Adjustment: -17.0
        +State:                 "stand"			         "park_shotgun_stand.mvf"
        +Action:                "fire_stand"            "park_shotgun_firepump.mvf"        ""
        +Weapon Specific: "Grenade"
        +State:                 "stand"			         "ult2_stand.mvf"
        $Use:                   "none"
        $Collision Sphere:      "head" 2.0 2.0
        $Name:                  "rat"
        $V3D Filename:          "rat.vcm"
        +State:                 "stand"                 "rat_stand.mvf"
        #End
        """;

    [Fact]
    public void EntityReaderCollectsStatesActionsAndWeaponBlocks()
    {
        var classes = TableReaders.ReadEntities(EntityText);
        Assert.Equal(["miner1", "rat"], classes.Select(c => c.Name));
        var miner = classes[0];
        Assert.Equal("miner.v3c", miner.Mesh.DiskName);
        Assert.Equal(["stand", "walk"], miner.States.Select(s => s.Name));
        Assert.Equal("ult2_stand.rfa", miner.States[0].Clip.DiskName);
        Assert.Equal(["reload", "fire_stand"], miner.Actions.Select(a => a.Name));
        Assert.Equal("Ultor Reload", miner.Actions[0].Sound);
        Assert.Null(miner.Actions[1].Sound);
        Assert.Equal(2, miner.WeaponAnimations.Length);
        var shotgun = miner.WeaponAnimations[0];
        Assert.Equal("Shotgun", shotgun.WeaponName);
        Assert.Equal(-17f, shotgun.SpineAdjustment);
        Assert.Equal("park_shotgun_stand.mvf", shotgun.States.Single().Clip.Original);
        Assert.Equal("park_shotgun_firepump.mvf", shotgun.Actions.Single().Clip.Original);
        Assert.Null(miner.WeaponAnimations[1].SpineAdjustment);
        Assert.Single(classes[1].States);
    }

    [Fact]
    public void MultiCharacterReaderReadsTypesAndFpguns()
    {
        const string text = """
            #Characters
            $Name:			"enviro_parker"
            $ScreenName:
            En: "Envirosuit Parker"
            Gr: "(gr) Envirosuit Parker"
            $ImageName:		"char_parker_env.tga"
            $EntityType:	"miner1"
            $EntityAnimType:"miner1"
            $SkinName:		"parker"
            +Custom Fpgun:	"Riot Stick"			"riot_stick_normal"
            $Name:			"nurse"
            $EntityType:	"nurse1"
            $EntityAnimType:"multi_female"
            #End
            """;
        var characters = TableReaders.ReadMultiCharacters(text);
        Assert.Equal(2, characters.Count);
        Assert.Equal("miner1", characters[0].EntityType);
        Assert.Equal("miner1", characters[0].EntityAnimType);
        Assert.Equal("parker", characters[0].SkinName);
        Assert.Equal(new CustomFpgun("Riot Stick", "riot_stick_normal"), characters[0].CustomFpguns.Single());
        Assert.Equal("multi_female", characters[1].EntityAnimType);
        Assert.Empty(characters[1].CustomFpguns);
    }

    [Fact]
    public void WeaponAndFpgunReadersReadMeshesAndClips()
    {
        const string weapons = """
            #Primary Weapons
            $Name:                  "12mm handgun"
            $V3D Filename:          ""
            $3rd Person V3D:	      "weapon_ultorgun.v3d"
            $1st Person Mesh:        "fp_glock.v3d"
            +State:                 "idle"             "fp_glock_idle.mvf"
            +Action:                "fire"             "fp_glock_fire.mvf"          ""
            $Shells Ejected:
            	+V3D:						"fp_glock_shell.v3d"
            $Name:                  "Rocket"
            $V3D Filename:          "rocket.v3d"
            #End
            """;
        var list = TableReaders.ReadWeapons(weapons);
        Assert.Equal(2, list.Count);
        Assert.Equal("fp_glock.v3d", list[0].FirstPersonMesh?.Original);
        Assert.Equal("weapon_ultorgun.v3d", list[0].ThirdPersonMesh?.Original);
        Assert.Null(list[0].ProjectileMesh);
        Assert.Equal("fp_glock_idle.rfa", list[0].States.Single().Clip.DiskName);
        Assert.Equal("fire", list[0].Actions.Single().Name);
        Assert.Equal("rocket.v3d", list[1].ProjectileMesh?.Original);

        var fpguns = TableReaders.ReadFpguns("$Name:\t\"handgun_normal\"\n$Model: \"fp_glock.v3c\"\n$Hand Texture: \"hand1st.tga\"\n");
        Assert.Equal("fp_glock.v3c", fpguns.Single().Model.DiskName);
        Assert.Equal("hand1st.tga", fpguns.Single().HandTexture);
    }

    // ── Stock tables ─────────────────────────────────────────────────────────

    private static string? StockTable(string name)
    {
        if (TestPaths.Tables is null) return null;
        string path = Path.Combine(TestPaths.Tables, name);
        return File.Exists(path) ? TblTokenizer.ReadText(path) : null;
    }

    [Fact]
    public void StockEntityTableReadsPlausibly()
    {
        if (StockTable("entity.tbl") is not { } text) return;
        var classes = TableReaders.ReadEntities(text);
        Assert.Equal(63, classes.Count);
        var miner = classes.Single(c => c.Name == "miner1");
        Assert.Equal("miner.vcm", miner.Mesh.Original);
        Assert.Equal("park_jeep_driver.mvf", miner.States.Single(s => s.Name == "jeep_drive").Clip.Original);
        Assert.DoesNotContain(miner.Actions, a => a.Name == "death_leg_left"); // commented out in the stock file
        Assert.Contains(miner.WeaponAnimations, w => w.WeaponName == "Shotgun" && w.SpineAdjustment == -17f);
        int states = classes.Sum(c => c.States.Length + c.WeaponAnimations.Sum(w => w.States.Length));
        int actions = classes.Sum(c => c.Actions.Length + c.WeaponAnimations.Sum(w => w.Actions.Length));
        Assert.Equal(1139, states);  // every uncommented +State: line
        Assert.Equal(1168, actions); // every uncommented +Action: line
        Assert.Equal(125, classes.Sum(c => c.WeaponAnimations.Length));
        // Every clip a class names resolves to a stock .rfa when the corpus is present.
        if (TestPaths.Corpus is not null)
        {
            var onDisk = new HashSet<string>(Directory.EnumerateFiles(TestPaths.Corpus, "*.rfa").Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(miner.States[0].Clip.DiskName, onDisk);
        }
    }

    [Fact]
    public void StockMultiCharacterTableReadsPlausibly()
    {
        if (StockTable("pc_multi.tbl") is not { } text) return;
        var characters = TableReaders.ReadMultiCharacters(text);
        Assert.Equal(20, characters.Count);
        var parker = characters.Single(c => c.Name == "enviro_parker");
        Assert.Equal("miner1", parker.EntityType);
        Assert.Equal("miner1", parker.EntityAnimType);
        Assert.Contains(parker.CustomFpguns, f => f.WeaponName == "12mm handgun" && f.FpgunName == "handgun_normal");
        Assert.Equal(320, characters.Sum(c => c.CustomFpguns.Length));
    }

    [Fact]
    public void StockWeaponAndFpgunTablesReadPlausibly()
    {
        if (StockTable("weapons.tbl") is not { } weaponsText || StockTable("fpgun.tbl") is not { } fpgunText) return;
        var weapons = TableReaders.ReadWeapons(weaponsText);
        Assert.Equal(44, weapons.Count);
        var glock = weapons.Single(w => w.Name == "12mm handgun");
        Assert.Equal("fp_glock.v3d", glock.FirstPersonMesh?.Original);
        Assert.Contains(glock.States, s => s.Name == "idle" && s.Clip.Original == "fp_glock_idle.mvf");
        Assert.Contains(glock.Actions, a => a.Name == "custom_start");
        Assert.Equal(19, weapons.Count(w => w.FirstPersonMesh is not null));
        Assert.Equal(43, weapons.Sum(w => w.States.Length));
        Assert.Equal(137, weapons.Sum(w => w.Actions.Length));

        var fpguns = TableReaders.ReadFpguns(fpgunText);
        Assert.Equal(256, fpguns.Count);
        Assert.Equal("fp_glock.v3c", fpguns.Single(f => f.Name == "handgun_normal").Model.Original);
    }
}
