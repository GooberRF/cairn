using Cairn.Tbl.Assist;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;

namespace Cairn.Tbl.Tests;

/// <summary>Fields and children the engine reads only under a condition (<c>when</c>, <c>if</c>, <c>childrenWhen</c>).</summary>
public sealed class ConditionTests
{
    private const string SchemaJson = """
        { "file": "cond.tbl", "sections": [ { "name": "#W", "required": true, "entry": "$Name:", "fields": [
          { "name": "$Name:", "type": "string", "required": true },
          { "name": "$Flags:", "type": "flags", "values": ["player_wep", "x"] },
          { "name": "$Cycle:", "type": "int", "when": "required if $Flags contains player_wep", "otherwise": "absent" },
          { "name": "$Aim:", "type": "float", "when": "required if $Flags contains player_wep" },
          { "name": "$Bitmap:", "type": "string", "childrenWhen": "length>4", "children": [
            { "name": "$Radius:", "type": "float", "required": true },
            { "name": "+Length:", "type": "float" },
            { "name": "+Tail:", "type": "float", "when": "required after +Length" } ] },
          { "name": "$Glow:", "type": "bool", "if": "true", "children": [ { "name": "+R:", "type": "float", "required": true } ] },
          { "name": "$Use:", "type": "enum", "values": ["a", "none"], "childrenWhen": "known", "childrenUnless": ["none"], "children": [ { "name": "+radius:", "type": "float" } ] }
        ] },
        { "name": "#S", "entrySearch": true, "entry": "$Name:", "fields": [
          { "name": "$Name:", "type": "string", "required": true },
          { "name": "$Use:", "type": "enum", "values": ["a", "none"], "childrenWhen": "known", "childrenUnless": ["none"], "children": [ { "name": "+radius:", "type": "float" } ] }
        ] } ] }
        """;

    private static TblTableSchema Schema => TblSchemaSet.Parse(SchemaJson, "cond");

    private static List<string> Codes(string body) =>
        [.. TblLinter.Lint(TblDocument.Parse("#W\r\n$Name: \"a\"\r\n" + body + "#End\r\n", Schema)).Select(d => d.Code)];

    [Fact]
    public void SchemaReadsTheConditions()
    {
        var w = Schema.Sections[0];
        Assert.Equal(new TblFieldCondition("$Flags:", "player_wep"), w.Fields[2].RequiredIf);
        Assert.True(w.Fields[2].AbsentOtherwise);
        Assert.False(w.Fields[3].AbsentOtherwise);
        Assert.Equal("+Length:", w.Fields[4].Children[2].RequiredAfter);
        Assert.Equal("none", Assert.Single(w.Fields[6].ChildrenUnless));
    }

    [Theory]
    [InlineData("$Flags: (\"player_wep\")\r\n$Cycle: 1\r\n$Aim: 0.5\r\n", "")]
    [InlineData("$Flags: (\"x\")\r\n$Aim: 0.5\r\n", "")]
    [InlineData("$Flags: (\"player_wep\")\r\n", "TBL108 TBL108")]
    [InlineData("$Flags: (\"x\")\r\n$Cycle: 1\r\n", "TBL117")]
    [InlineData("$Cycle: 1\r\n", "TBL117")]
    [InlineData("$Bitmap: \"\"\r\n", "")]
    [InlineData("$Bitmap: \"\"\r\n$Radius: 1\r\n", "TBL117")]
    [InlineData("$Bitmap: \"a.tga\"\r\n", "TBL108")]
    [InlineData("$Bitmap: \"a.tga\"\r\n$Radius: 1\r\n+Length: 2\r\n", "TBL108")]
    [InlineData("$Bitmap: \"a.tga\"\r\n$Radius: 1\r\n+Length: 2\r\n+Tail: 1\r\n", "")]
    [InlineData("$Glow: false\r\n", "")]
    [InlineData("$Glow: false\r\n+R: 1\r\n", "TBL117")]
    [InlineData("$Glow: true\r\n", "TBL108")]
    [InlineData("$Use: \"none\"\r\n+radius: 1\r\n", "TBL117")]
    [InlineData("$Use: \"a\"\r\n+radius: 1\r\n", "")]
    public void ConditionsAreEnforced(string body, string expected)
    {
        Assert.Equal(expected, string.Join(" ", Codes(body)));
    }

    [Fact]
    public void WeaponsSchemaGatesCyclePositionOnPlayerWep()
    {
        var schema = TblSchemaSet.Default.Find("weapons.tbl")!;
        var cycle = schema.Sections.SelectMany(s => s.Fields).First(f => f.Name == "$Cycle Position:");
        Assert.Equal(new TblFieldCondition("$Flags:", "player_wep"), cycle.RequiredIf);
        Assert.True(cycle.AbsentOtherwise);
        var d = TblLinter.Lint(TblDocument.Parse("#Primary Weapons\r\n$Name: \"x\"\r\n$Flags: (\"autoaim\")\r\n$Cycle Position: 3\r\n#End\r\n", schema));
        Assert.Contains(d, x => x.Code == "TBL117");
        d = TblLinter.Lint(TblDocument.Parse("#Primary Weapons\r\n$Name: \"x\"\r\n$Flags: (\"player_wep\")\r\n#End\r\n", schema));
        Assert.Contains(d, x => x.Code == "TBL108" && x.Message.StartsWith("$Cycle Position:", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedGateInASearchedSectionIsSkippedNotFatal()
    {
        var d = TblLinter.Lint(TblDocument.Parse("#W\r\n$Name: \"w\"\r\n#End\r\n#S\r\n$Name: \"a\"\r\n$Use: \"none\"\r\n+radius: 1\r\n#End\r\n", Schema));
        var skipped = Assert.Single(d);
        Assert.Equal("TBL118", skipped.Code);
        Assert.Equal(TblSeverity.Warning, skipped.Severity);
    }

    [Fact]
    public void QuickFixRemovesTheUnreadField()
    {
        string text = "#W\r\n$Name: \"a\"\r\n$Flags: (\"x\")\r\n$Cycle: 1\r\n#End\r\n";
        var d = Assert.Single(TblLinter.Lint(TblDocument.Parse(text, Schema)));
        Assert.Equal("#W\r\n$Name: \"a\"\r\n$Flags: (\"x\")\r\n#End\r\n", Text.TextEdit.ApplyAll(text, d.QuickFixes[0].Edits));
    }

    [Fact]
    public void CompletionOffersConditionalFieldsOnlyWhenTheyAreRead()
    {
        string Labels(string flags)
        {
            string text = "#W\r\n$Name: \"a\"\r\n$Flags: (" + flags + ")\r\n$";
            var doc = TblDocument.Parse(text + "\r\n#End\r\n", Schema);
            return string.Join(" ", TblAssist.Complete(doc, text.Length).Items.Select(i => i.Label));
        }
        Assert.Contains("$Cycle:", Labels("\"player_wep\""), StringComparison.Ordinal);
        Assert.DoesNotContain("$Cycle:", Labels("\"x\""), StringComparison.Ordinal);
        Assert.Contains("$Aim:", Labels("\"x\""), StringComparison.Ordinal);

        string glow = "#W\r\n$Name: \"a\"\r\n$Glow: false\r\n+";
        var doc2 = TblDocument.Parse(glow + "\r\n#End\r\n", Schema);
        Assert.DoesNotContain(TblAssist.Complete(doc2, glow.Length).Items, i => i.Label == "+R:");
    }
}
