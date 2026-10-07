using Cairn.Formats.Tbl;

namespace Cairn.Rfa.Tests;

public class TblSnippetTests
{
    [Theory]
    [InlineData("my_clip", "my_clip.mvf")]
    [InlineData("my_clip.rfa", "my_clip.mvf")]
    [InlineData(@"C:\work\out\My_Clip.RFA", "My_Clip.mvf")]
    [InlineData("my_clip.mvf", "my_clip.mvf")]
    public void ClipsAreSpeltTheTableWay(string input, string expected) =>
        Assert.Equal(expected, TblSnippet.TableClipName(input));

    [Fact]
    public void SingleLinesUseTheStockColumns()
    {
        // Key padded to 24, quoted name to 24, quoted clip to 41 (entity.tbl's widths), leading tab.
        Assert.Equal("\t+State:                 \"stand\"                 \"new_stand.mvf\"",
            TblSnippet.Line(ClipUsageKind.State, "stand", @"D:\out\new_stand.rfa"));
        Assert.Equal("\t+Action:                \"reload\"                \"new_reload.mvf\"                         \"Ultor Reload\"",
            TblSnippet.Line(ClipUsageKind.Action, "reload", "new_reload", "Ultor Reload"));
        // An action always has a sound column; a state never does, even when given one.
        Assert.Equal("\t+Action:                \"fire\"                  \"new_fire.mvf\"                           \"\"",
            TblSnippet.Line(ClipUsageKind.Action, "fire", "new_fire.rfa"));
        Assert.Equal(TblSnippet.Line(ClipUsageKind.State, "walk", "w"), TblSnippet.Line(ClipUsageKind.State, "walk", "w", "ignored"));

        // weapons.tbl style: no indent, columns sized to the content plus three spaces.
        Assert.Equal("+Action:                \"fire\"   \"fp_new_fire.mvf\"   \"Pistol Fire\"",
            TblSnippet.Line(ClipUsageKind.Action, "fire", "fp_new_fire", "Pistol Fire", TblSnippetStyle.Weapon));
    }

    [Fact]
    public void ABlockAlignsItsColumnsAndGroupsWeaponBlocksLast()
    {
        string block = TblSnippet.Block(
        [
            new TblSnippetEntry(ClipUsageKind.State, "stand", "a_stand"),
            new TblSnippetEntry(ClipUsageKind.State, "stand", "a_shotgun_stand", WeaponBlock: "Shotgun"),
            new TblSnippetEntry(ClipUsageKind.Action, "a_really_long_action_name", "a_clip_with_a_name_long_enough_to_overflow.rfa", "Boom"),
            new TblSnippetEntry(ClipUsageKind.Action, "fire_stand", "a_shotgun_fire", WeaponBlock: "shotgun"),
        ]);
        string[] lines = block.Split("\r\n");
        Assert.Equal(6, lines.Length);
        Assert.Equal("", lines[^1]);
        Assert.Equal("\t+State:                 \"stand\"                      \"a_stand.mvf\"", lines[0]);
        Assert.Equal("\t+Action:                \"a_really_long_action_name\"  \"a_clip_with_a_name_long_enough_to_overflow.mvf\"  \"Boom\"", lines[1]);
        Assert.Equal("\t+Weapon Specific: \"Shotgun\"", lines[2]);
        Assert.Equal("\t+State:                 \"stand\"                      \"a_shotgun_stand.mvf\"", lines[3]);
        Assert.Equal("\t+Action:                \"fire_stand\"                 \"a_shotgun_fire.mvf\"                              \"\"", lines[4]);
        Assert.Equal(string.Empty, TblSnippet.Block([]));
    }

    [Fact]
    public void SnippetsReadBackToTheSameValues()
    {
        TblSnippetEntry[] entries =
        [
            new(ClipUsageKind.State, "stand", "x_stand.rfa"),
            new(ClipUsageKind.State, "attack crouch walk", "x crouch walk"),
            new(ClipUsageKind.Action, "fire_stand", "x_fire", "Gun (Fire) 1"),
            new(ClipUsageKind.Action, "reload", "x_reload"),
            new(ClipUsageKind.State, "stand", "x_smw_stand", WeaponBlock: "Sniper Rifle"),
            new(ClipUsageKind.Action, "fire_stand", "x_smw_fire", "Sniper Fire", "Sniper Rifle"),
        ];
        foreach (var style in new[] { TblSnippetStyle.Entity, TblSnippetStyle.Weapon })
        {
            string text = "#Entity Classes\r\n$Name: \"x\"\r\n$V3D Filename: \"x.vcm\"\r\n" + TblSnippet.Block(entries, style) + "#End\r\n";
            var cls = Assert.Single(TableReaders.ReadEntities(text));
            Assert.Equal(["stand", "attack crouch walk"], cls.States.Select(s => s.Name));
            Assert.Equal(["x_stand.mvf", "x crouch walk.mvf"], cls.States.Select(s => s.Clip.Original));
            Assert.Equal(["fire_stand", "reload"], cls.Actions.Select(a => a.Name));
            Assert.Equal(["Gun (Fire) 1", null], cls.Actions.Select(a => a.Sound));
            var block = Assert.Single(cls.WeaponAnimations);
            Assert.Equal("Sniper Rifle", block.WeaponName);
            Assert.Equal("x_smw_stand.rfa", Assert.Single(block.States).Clip.DiskName);
            Assert.Equal("Sniper Fire", Assert.Single(block.Actions).Sound);
        }
    }

    [Theory]
    [InlineData("bad\"name")]
    [InlineData("two\nlines")]
    public void ValuesThatCannotBeQuotedAreRefused(string value)
    {
        Assert.Throws<ArgumentException>(() => TblSnippet.Line(ClipUsageKind.State, value, "clip"));
        Assert.Throws<ArgumentException>(() => TblSnippet.Line(ClipUsageKind.State, "stand", value));
        Assert.Throws<ArgumentException>(() => TblSnippet.Line(ClipUsageKind.Action, "fire", "clip", value));
        Assert.Throws<ArgumentException>(() => TblSnippet.Line(ClipUsageKind.State, " ", "clip"));
        Assert.Throws<ArgumentException>(() => TblSnippet.Line(ClipUsageKind.State, "stand", ""));
    }

    [Fact]
    public void RetargetKeepsTheSourceSlotsWithTheNewClips()
    {
        var index = ClipUsageIndex.FromTexts("""
            $Name:                  "guard"
            $V3D Filename:          "guard.vcm"
            +State:                 "stand"                 "g_stand.mvf"
            +State:                 "walk"                  "g_walk.mvf"
            +Action:                "fire_stand"            "g_fire.mvf"              "Gun Fire"
            +Weapon Specific: "Shotgun"
            +State:                 "stand"                 "g_shotgun_stand.mvf"
            """, null, null, null);
        var renames = new Dictionary<string, string>
        {
            ["G_STAND.rfa"] = @"out\h_stand.rfa",
            ["g_fire"] = "h_fire",
            ["g_shotgun_stand.mvf"] = "h_shotgun_stand",
        };
        var entries = TblSnippet.Retarget(index.UsagesOfClass("guard"), renames);
        Assert.Equal(
        [
            new TblSnippetEntry(ClipUsageKind.State, "stand", @"out\h_stand.rfa"),
            new TblSnippetEntry(ClipUsageKind.Action, "fire_stand", "h_fire", "Gun Fire"),
            new TblSnippetEntry(ClipUsageKind.State, "stand", "h_shotgun_stand", null, "Shotgun"),
        ], entries);

        string block = TblSnippet.RetargetBlock(index.UsagesOfClass("guard"), renames);
        Assert.Equal(
            "\t+State:                 \"stand\"                 \"h_stand.mvf\"\r\n"
            + "\t+Action:                \"fire_stand\"            \"h_fire.mvf\"                             \"Gun Fire\"\r\n"
            + "\t+Weapon Specific: \"Shotgun\"\r\n"
            + "\t+State:                 \"stand\"                 \"h_shotgun_stand.mvf\"\r\n",
            block);

        var all = TblSnippet.Retarget(index.UsagesOfClass("guard"), renames, keepUnmapped: true);
        Assert.Equal(4, all.Count);
        Assert.Equal("g_walk.mvf", all[1].ClipName);
    }

    [Fact]
    public void SnippetsMatchStockLinesExactly()
    {
        if (TestPaths.Tables is null) return;
        string path = Path.Combine(TestPaths.Tables, "entity.tbl");
        if (!File.Exists(path)) return;
        var lines = TblTokenizer.ReadText(path).Split("\r\n");

        // Lines the stock file writes with exactly the default widths are reproduced byte for byte.
        string state = TblSnippet.Line(ClipUsageKind.State, "stand", "ult2_stand.rfa");
        string action = TblSnippet.Line(ClipUsageKind.Action, "fire_crouch", "park_riotstick_crouch_swing.rfa");
        Assert.Contains(state, lines);
        Assert.Contains(action, lines);

        // And most stock state lines use exactly these columns.
        var stockStates = lines.Where(l => l.StartsWith("\t+State:", StringComparison.Ordinal)).ToList();
        int reproduced = 0;
        foreach (var cls in TableReaders.ReadEntities(TblTokenizer.ReadText(path)))
        {
            foreach (var s in cls.States.Concat(cls.WeaponAnimations.SelectMany(w => w.States)))
            {
                if (s.Line - 1 < lines.Length && lines[s.Line - 1] == TblSnippet.Line(ClipUsageKind.State, s.Name, s.Clip.Original)) reproduced++;
            }
        }
        Assert.True(reproduced > 500, $"{reproduced} of {stockStates.Count} stock state lines reproduced");
    }
}
