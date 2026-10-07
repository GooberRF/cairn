using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;

namespace Cairn.Tbl.Tests;

/// <summary>Rows and matrix sections checked cell by cell, and Alpine option lines checked as Alpine reads them.</summary>
public sealed class LayoutTests
{
    private const string RowsJson = """
        { "file": "rows.tbl", "sections": [
          { "name": "#Sounds Start", "layout": "rows", "end": "#Sounds End", "maxEntries": 3, "columns": [
            { "type": "file", "file": "sound" }, { "type": "float" }, { "type": "float", "min": 0 }, { "type": "float" } ] },
          { "name": "#Grid", "layout": "matrix", "size": 2, "cell": { "type": "int", "min": 0, "max": 4 }, "header": { "type": "ref", "ref": "material" } } ] }
        """;

    private static string Codes(string text, TblLintContext? context = null) =>
        string.Join(" ", TblLinter.Lint(TblDocument.Parse(text, TblSchemaSet.Parse(RowsJson, "rows")), null, context).Select(d => d.Code));

    [Theory]
    [InlineData("\"a.wav\" 1.0 0.5 1.0\r\n\"b.wav\" 2 1 1\r\n", "")]
    [InlineData("\"a.wav\" 1.0 0.5\r\n", "TBL119")]
    [InlineData("\"a.wav\" 1.0 0.5 1.0 7\r\n", "TBL119")]
    [InlineData("\"a.wav\" \"x\" 0.5 1.0\r\n", "TBL105")]
    [InlineData("\"a.wav\" 1.0 -1 1.0\r\n", "TBL106")]
    [InlineData("\"a.wav\" 1 1 1\r\n\"a.wav\" 1 1 1\r\n\"a.wav\" 1 1 1\r\n\"a.wav\" 1 1 1\r\n", "TBL112")]
    public void RowsAreCheckedColumnByColumn(string rows, string expected) =>
        Assert.Equal(expected, Codes("#Sounds Start\r\n" + rows + "#Sounds End\r\n"));

    [Fact]
    public void RowFilesAreLookedUp()
    {
        var context = new TblLintContext { FileExists = n => n.Equals("a.wav", StringComparison.OrdinalIgnoreCase) };
        Assert.Equal("TBL201", Codes("#Sounds Start\r\n\"a.wav\" 1 1 1\r\n\"b.wav\" 1 1 1\r\n#Sounds End\r\n", context));
    }

    [Theory]
    [InlineData("\"rock\" \"lava\"\r\n\"rock\" 1 2\r\n\"lava\" 0 4\r\n", "")]
    [InlineData("\"rock\" \"lava\"\r\n\"rock\" 1 2\r\n\"lava\" 0 5\r\n", "TBL106")]
    [InlineData("\"rock\" \"lava\"\r\n\"rock\" 1\r\n\"lava\" 0 4\r\n", "TBL105 TBL105 TBL119")]
    [InlineData("\"rock\" \"lava\"\r\n\"rock\" 1 2\r\n\"lava\" 0 4 9\r\n", "TBL113")]
    public void MatrixIsReadValueAfterValue(string grid, string expected) =>
        Assert.Equal(expected, Codes("#Sounds Start\r\n#Sounds End\r\n#Grid\r\n" + grid));

    [Theory]
    [InlineData("$Disable Multiplayer Button: true\r\n", "")]
    [InlineData("$Disable Multiplayer Button: 1\r\n", "")]
    [InlineData("$Disable Multiplayer Button: yes\r\n", "TBL120")]
    [InlineData("$Summoner Trailer Button Action: 2\r\n", "")]
    [InlineData("$Summoner Trailer Button Action: 7\r\n", "TBL107")]
    [InlineData("$Summoner Trailer Button Action: two\r\n", "TBL120")]
    [InlineData("$Summoner Trailer Button Action: 3 // play the level\r\n", "TBL113")]
    public void AlpineOptionsAreReadAsAlpineReadsThem(string line, string expected)
    {
        var schema = TblSchemaSet.Default.Find("af_ui.tbl")!;
        var d = TblLinter.Lint(TblDocument.Parse("#Start\r\n" + line + "#End\r\n", schema), null, TblLintContext.Default);
        Assert.Equal(expected, string.Join(" ", d.Select(x => x.Code)));
    }

    [Theory]
    [InlineData("$Lightmap Clamp Floor: 202020\r\n", "")]
    [InlineData("$Lightmap Clamp Floor: {32, 32, 32}\r\n", "")]
    [InlineData("$Lightmap Clamp Floor: <32,32,32>\r\n", "")]
    [InlineData("$Lightmap Clamp Floor: {32, 32, 32, 255}\r\n", "TBL120")]
    [InlineData("$Lightmap Clamp Floor: 2020\r\n", "TBL120")]
    [InlineData("$Mesh Replacement: {\"a.v3m\", \"b.v3m\"}\r\n", "")]
    [InlineData("$Mesh Replacement: {\"a.v3m\", \"missing.v3m\"}\r\n", "TBL201")]
    [InlineData("$Mesh Replacement: \"a.v3m\"\r\n", "TBL120")]
    public void AlpineColoursAndMeshPairs(string line, string expected)
    {
        var schema = TblSchemaSet.Default.Find("l1s1_info.tbl")!;
        var context = new TblLintContext { FileExists = n => !n.StartsWith("missing", StringComparison.OrdinalIgnoreCase) };
        var d = TblLinter.Lint(TblDocument.Parse("#Start\r\n" + line + "#End\r\n", schema), null, context);
        Assert.Equal(expected, string.Join(" ", d.Select(x => x.Code)));
    }
}
