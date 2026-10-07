using Cairn.Assets;
using Cairn.Tbl.Assist;
using Cairn.Tbl.Compare;
using Cairn.Tbl.Index;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;
using Xunit.Abstractions;

namespace Cairn.Tbl.Tests;

public sealed class IndexAssistCompareTests(ITestOutputHelper output)
{
    private static TblSchemaSet TestSchemas => TblSchemaSet.FromJson(
    [
        ("t", LintTests.TestSchemaJson),
        ("ammo", """{ "file": "ammo.tbl", "sections": [ { "name": "#Ammo", "entry": "$Name:", "defines": "ammo", "fields": [ { "name": "$Name:", "type": "string", "required": true } ] } ] }"""),
    ]);

    // Review findings 14 and 17: tables that are no longer visible are forgotten, only the copy the game loads counts,
    // and an open table shadows the indexed copy of the same name.
    [Fact]
    public void RefreshForgetsHiddenTablesAndKeepsOneCopyPerName()
    {
        string root = Path.Combine(Path.GetTempPath(), "cairn-tbl-index-" + Guid.NewGuid().ToString("N"));
        string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        try
        {
            File.WriteAllText(Path.Combine(a, "ammo.tbl"), "#Ammo\r\n$Name: \"Foo\"\r\n#End\r\n");
            File.WriteAllText(Path.Combine(b, "ammo.tbl"), "#Ammo\r\n$Name: \"Bar\"\r\n#End\r\n");
            var index = new TblIndex(TestSchemas);
            index.Refresh(new AssetResolver(new AssetResolverOptions { SearchFolders = [a, b] }));
            Assert.Single(index.Sources);
            Assert.NotEmpty(index.Define("ammo", "Foo"));
            Assert.Empty(index.Define("ammo", "Bar")); // b's copy is hidden by a's, as in the game
            index.Refresh(new AssetResolver(new AssetResolverOptions { SearchFolders = [b] }));
            Assert.Empty(index.Define("ammo", "Foo"));
            Assert.NotEmpty(index.Define("ammo", "Bar"));
            Assert.Single(index.Sources);
            // An open ammo.tbl without Bar: Bar is no longer defined by the file on disk.
            index.Update(new TblSource("unsaved:1", "ammo.tbl", "unsaved", TblSourceKind.OpenDocument), "#Ammo\r\n$Name: \"Baz\"\r\n#End\r\n");
            Assert.Empty(index.Define("ammo", "Bar"));
            Assert.NotEmpty(index.Define("ammo", "Baz"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IndexDefinesUsesAndUpdatesIncrementally()
    {
        var index = new TblIndex(TestSchemas);
        index.Update(TblSource.ForPath("ammo.tbl"), "#Ammo\r\n$Name: \"shell\"\r\n$Name: \"gas\"\r\n#End\r\n");
        index.Update(TblSource.ForPath("test_items.tbl"), "#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Mesh: \"m.v3d\"\r\n$Ammo: \"shell\"\r\n#End\r\n");
        var def = Assert.Single(index.Define("ammo", "SHELL"));
        Assert.Equal(2, def.Line);
        Assert.Equal("ammo.tbl", def.Source.FileName);
        var use = Assert.Single(index.Usages("ammo", "shell"));
        Assert.Equal("test_items.tbl", use.Source.FileName);
        Assert.Equal(["gas", "shell"], index.Names("ammo"));
        Assert.Single(index.FileUsages("m.v3d"));
        Assert.Single(index.Define("item", "a"));

        int version = index.Version;
        index.Update(TblSource.ForPath("test_items.tbl"), "#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Ammo: \"gas\"\r\n#End\r\n");
        Assert.True(index.Version > version);
        Assert.Empty(index.Usages("ammo", "shell"));
        Assert.Single(index.Usages("ammo", "gas"));
        index.Remove("ammo.tbl");
        Assert.False(index.HasKind("ammo"));
    }

    [Fact]
    public void IndexBuiltFromGameDataFindsAWeaponsAmmo()
    {
        if (TestData.GameDirectory is not { } game) return;
        var resolver = new AssetResolver(new AssetResolverOptions { GameDirectory = game });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var index = TblIndex.Build(resolver);
        output.WriteLine($"index of {index.Sources.Count} tables in {sw.ElapsedMilliseconds} ms; {index.FileNames("texture").Count} texture names");
        var weapons = index.Sources.FirstOrDefault(s => s.FileName.Equals("weapons.tbl", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(weapons);
        Assert.NotEmpty(index.Define("weapon", "12mm handgun"));
        var ammo = index.Define("ammo", "12mm");
        Assert.NotEmpty(ammo);
        Assert.Equal("ammo.tbl", ammo[0].Source.FileName, ignoreCase: true);
        var uses = index.Usages("ammo", "12mm");
        Assert.Contains(uses, u => u.Source.FileName.Equals("weapons.tbl", StringComparison.OrdinalIgnoreCase) && u.Field.Equals("$Ammo Type:", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"\"12mm rounds\": defined {ammo[0].Source.DisplayLocation} line {ammo[0].Line}; {uses.Length} uses");
        Assert.NotEmpty(index.Names("weapon"));
    }

    [Fact]
    public void CompletionOffersTheNextFieldsInEngineOrder()
    {
        string text = "#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n\r\n$Flags: ()\r\n#End\r\n";
        var doc = TblDocument.Parse(text, LintTests.TestSchema);
        int caret = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 2;
        var c = TblAssist.Complete(doc, caret);
        // After $Count: and before $Flags: the game reads $Speed: then $Kind:.
        Assert.Equal(["$Speed:", "$Kind:"], c.Items.Where(i => i.Kind == TblCompletionKind.Field).Select(i => i.Label).Take(2));
        Assert.DoesNotContain(c.Items, i => i.Label == "$Mesh:");

        string partial = "#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Kind: \"\"\r\n#End\r\n";
        var d2 = TblDocument.Parse(partial, LintTests.TestSchema);
        var values = TblAssist.Complete(d2, partial.IndexOf("\"\"", StringComparison.Ordinal) + 1);
        Assert.Equal(["small", "large"], values.Items.Select(i => i.InsertText));

        var index = new TblIndex(TestSchemas);
        index.Update(TblSource.ForPath("ammo.tbl"), "#Ammo\r\n$Name: \"shell\"\r\n#End\r\n");
        string withAmmo = "#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Ammo: \r\n#End\r\n";
        var refs = TblAssist.Complete(TblDocument.Parse(withAmmo, LintTests.TestSchema), withAmmo.IndexOf("$Ammo: ", StringComparison.Ordinal) + 7, index);
        Assert.Equal("\"shell\"", Assert.Single(refs.Items).InsertText);

        var sections = TblAssist.Complete(TblDocument.Parse("#", LintTests.TestSchema), 1);
        Assert.Contains(sections.Items, i => i.Label == "#Items");
    }

    [Fact]
    public void HoverAndNavigation()
    {
        var index = new TblIndex(TestSchemas);
        index.Update(TblSource.ForPath("ammo.tbl"), "#Ammo\r\n$Name: \"shell\"\r\n#End\r\n");
        string text = "#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Mesh: \"m.v3d\"\r\n$Ammo: \"shell\"\r\n#End\r\n";
        index.Update(TblSource.ForPath("test_items.tbl"), text);
        var doc = TblDocument.Parse(text, LintTests.TestSchema);
        var hover = TblAssist.Hover(doc, text.IndexOf("$Count", StringComparison.Ordinal) + 2, index)!;
        Assert.Equal("$Count:", hover.Title);
        Assert.Contains(hover.Lines, l => l.Contains("int", StringComparison.Ordinal) && l.Contains("0 to 10", StringComparison.Ordinal) && l.Contains("required", StringComparison.Ordinal));

        int ammoAt = text.IndexOf("shell", StringComparison.Ordinal) + 1;
        Assert.Contains(TblAssist.Hover(doc, ammoAt, index)!.Lines, l => l.Contains("ammo.tbl", StringComparison.Ordinal));
        var target = TblAssist.FindDefinition(doc, ammoAt, index)!;
        Assert.Equal("shell", target.Definition!.Name);
        Assert.Single(TblAssist.FindUsages(doc, ammoAt, index));

        var fileHover = TblAssist.Hover(doc, text.IndexOf("m.v3d", StringComparison.Ordinal) + 1, index, _ => null)!;
        Assert.Contains(fileHover.Lines, l => l.Contains("m.v3m or m.v3c", StringComparison.Ordinal));
        Assert.Contains(fileHover.Lines, l => l.StartsWith("Not found", StringComparison.Ordinal));
        Assert.Equal(TblSymbolKind.EntryName, TblAssist.SymbolAt(doc, text.IndexOf("\"a\"", StringComparison.Ordinal) + 1)!.Kind);
    }

    [Fact]
    public void CompareFindsNoDifferenceAgainstItselfAndTheRightOnesInAnEditedCopy()
    {
        var stockSample = TestData.Stock().FirstOrDefault(s => s.FileName.Equals("weapons.tbl", StringComparison.OrdinalIgnoreCase));
        string stockText = stockSample is null
            ? "#Primary Weapons\r\n$Name: \"Remote Charge\"\r\n$Damage: 500\r\n$Name: \"Rifle\"\r\n$Damage: 10\r\n$Name: \"Gone\"\r\n$Damage: 1\r\n#End\r\n"
            : TblTextFiles.Decode(stockSample.Bytes).Text;
        var schema = TblSchemaSet.Default.Find("weapons.tbl");
        var stock = TblDocument.Parse(stockText, schema);
        Assert.True(TblCompare.Compare(stock, TblDocument.Parse(stockText, schema)).IsIdentical);
        // Spacing and comments are not changes.
        Assert.True(TblCompare.Compare(TblDocument.Parse(System.Text.RegularExpressions.Regex.Replace(stockText, @"(?m)^\$Damage:", "// note\r\n$$Damage:   "), schema), stock).IsIdentical);

        // Edit: change the first $Damage:, remove the last entry, add one.
        int damage = System.Text.RegularExpressions.Regex.Match(stockText, @"(?m)^\$Damage:").Index;
        int lineEnd = stockText.IndexOf('\r', damage);
        string edited = stockText[..damage] + "$Damage: 12345" + stockText[lineEnd..];
        var lastEntry = stock.Sections.SelectMany(s => s.Entries).Last();
        int removeStart = lastEntry.Span.Start;
        edited = edited[..(removeStart + edited.Length - stockText.Length)] + edited[(lastEntry.Span.End + edited.Length - stockText.Length)..];
        int end = edited.IndexOf("#End", StringComparison.Ordinal);
        edited = edited[..end] + "$Name: \"Brand New\"\r\n" + edited[end..];

        var result = TblCompare.Compare(TblDocument.Parse(edited, schema), stock);
        foreach (var c in result.Changes) output.WriteLine($"{c.Kind} {c.Section}/{c.Name}: {string.Join("; ", c.Fields.Select(f => $"{f.Kind} {f.Path} {f.StockValue} -> {f.ModdedValue}"))}");
        var changed = Assert.Single(result.Changes, c => c.Kind == TblChangeKind.Changed);
        var field = Assert.Single(changed.Fields);
        Assert.Equal("12345", field.ModdedValue);
        Assert.Equal("$Damage: 12345", field.ModdedSpan!.Value.GetText(edited));
        Assert.Equal(lastEntry.Name, Assert.Single(result.Changes, c => c.Kind == TblChangeKind.Removed).Name);
        var added = Assert.Single(result.Changes, c => c.Kind == TblChangeKind.Added);
        Assert.Equal("Brand New", added.Name);
        Assert.StartsWith("$Name: \"Brand New\"", added.ModdedSpan!.Value.GetText(edited), StringComparison.Ordinal);
    }

    [Fact]
    public void FindStockReadsTheGamesOwnTable()
    {
        if (TestData.GameDirectory is not { } game) return;
        var stock = TblCompare.FindStock(game, "ammo.tbl");
        Assert.NotNull(stock);
        Assert.Contains("#Ammo", stock.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(TblCompare.FindStock(game, "no_such_table.tbl"));
    }
}
