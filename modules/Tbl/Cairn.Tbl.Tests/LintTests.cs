using Cairn.Assets;
using Cairn.Tbl.Index;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;
using Xunit.Abstractions;

namespace Cairn.Tbl.Tests;

public sealed class LintTests(ITestOutputHelper output)
{
    /// <summary>A small hand-written schema exercising every rule.</summary>
    internal const string TestSchemaJson = """
        { "file": "test_items.tbl", "title": "Test", "unknownKey": 1,
          "sections": [ { "name": "#Items", "required": true, "entry": "$Name:", "defines": "item", "fields": [
            { "name": "$Name:", "type": "string", "required": true },
            { "name": "$Count:", "type": "int", "required": true, "min": 0, "max": 10 },
            { "name": "$Speed:", "type": "float" },
            { "name": "$Kind:", "type": "enum", "values": ["small", "large"] },
            { "name": "$Flags:", "type": "flags", "values": ["red", "blue"] },
            { "name": "$Mesh:", "type": "file", "file": "mesh" },
            { "name": "$Ammo:", "type": "ref", "ref": "ammo" },
            { "name": "$Sound:", "type": "string", "repeat": true, "parts": [ { "type": "enum", "values": ["a", "b"] }, { "type": "file", "file": "sound" } ] },
            { "name": "$Glow:", "type": "bool", "children": [ { "name": "+Radius:", "type": "float", "required": true } ] },
            { "name": "$Pos:", "type": "vec3" },
            { "name": "$New:", "type": "int", "alpineSince": "1.1" }
          ] },
          { "name": "#Extra", "fields": [ { "name": "$X:", "type": "int" } ] } ] }
        """;

    internal static TblTableSchema TestSchema => TblSchemaSet.Parse(TestSchemaJson, "test");

    private static ImmutableArray<TblDiagnostic> Lint(string text, TblLintContext? context = null) =>
        TblLinter.Lint(TblDocument.Parse(text, TestSchema), null, context);

    [Fact]
    public void EmbeddedSchemasLoadWithoutErrors()
    {
        var set = TblSchemaSet.Default;
        foreach (var e in set.LoadErrors) output.WriteLine($"{e.Source}: {e.Message}");
        Assert.Empty(set.LoadErrors);
        output.WriteLine($"{set.Tables.Length} schemas: {string.Join(", ", set.Tables.Select(t => t.File ?? t.Pattern))}");
        foreach (var t in set.Tables)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.File ?? t.Pattern));
            string probe = t.File ?? t.Pattern!.Replace("*", "x");
            Assert.True(ReferenceEquals(t, set.Find(probe)), $"{t.Source}: {probe} finds {set.Find(probe)?.Source}");
            foreach (var s in t.Sections)
                foreach (var f in s.Fields.Concat(s.Fields.SelectMany(x => x.Children)))
                    Assert.True(f.Name.Trim().Length > 0, $"{t.Source}: a field without a name");
        }
    }

    [Fact]
    public void SchemaLoaderFindsByNameAndPatternAndReportsBadFiles()
    {
        var set = TblSchemaSet.FromJson([("a", TestSchemaJson), ("b", """{ "pattern": "*_info.tbl", "fields": [ { "name": "$A:" } ] }"""), ("c", "{ not json"), ("d", "[]")]);
        Assert.Equal(2, set.Tables.Length);
        Assert.Equal(2, set.LoadErrors.Length);
        Assert.Equal("Test", set.Find(Path.Combine("x", "TEST_ITEMS.tbl"))!.Title);
        var pattern = set.Find("level1_info.tbl")!;
        Assert.True(pattern.Sections[0].IsRoot);
        Assert.Null(set.Find("other.tbl"));
        var items = set.Tables[0].FindSection("items")!;
        Assert.Equal((2, items.Fields[2]), items.FindField("$speed:"));
        Assert.Equal(-1, items.FindField("$Spe ed:").Index);
        Assert.Equal(TblValueType.Enum, items.Fields[7].Items[0].Type);
    }

    [Fact]
    public void CleanTableHasNoDiagnostics()
    {
        var d = Lint("#Items\r\n$Name: \"a\"\r\n$Count: 3\r\n$Speed: -.5\r\n$Kind: \"LARGE\"\r\n$Flags: (\"red\" \"blue\")\r\n$Sound: \"a\" \"x.wav\"\r\n$Sound: \"b\" \"y.wav\"\r\n$Glow: yes\r\n\t+Radius: 2\r\n$Pos: <1 2 3>\r\n#End\r\n");
        Assert.Empty(d);
    }

    [Theory]
    [InlineData("#Items\r\n$Name: \"a\r\n$Count: 1\r\n#End", "TBL001")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1 /* open", "TBL002")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Flags: (\"red\"\r\n#End", "TBL003")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1)\r\n#End", "TBL004")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n", "TBL005")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n#End\r\n#End", "TBL006")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Speed 2\r\n#End", "TBL007")]
    [InlineData("#Items\r\nstray\r\n$Name: \"a\"\r\n$Count: 1\r\n#End", "TBL008")]
    [InlineData("#Items\n$Name: \"a\" // c\n$Count: 1\n#End", "TBL009")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n#End\r\n#Itemz\r\n#End", "TBL101")]
    [InlineData("#Extra\r\n#End", "TBL102")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Sped: 1.0\r\n#End", "TBL103")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Kind: \"small\"\r\n$Speed: 1.0\r\n#End", "TBL104")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: \"one\"\r\n#End", "TBL105")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1.5\r\n#End", "TBL105")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Speed: 1e3\r\n#End", "TBL105")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Flags: (\"red\", \"blue\")\r\n#End", "TBL105")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1 2\r\n#End", "TBL105")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 11\r\n#End", "TBL106")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Kind: \"smal\"\r\n#End", "TBL107")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Sound: \"c\" \"x.wav\"\r\n#End", "TBL107")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Speed: 1.0\r\n#End", "TBL108")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Glow: true\r\n#End", "TBL108")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Name: \"A\"\r\n$Count: 1\r\n#End", "TBL109")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Speed: 1.0\r\n$Speed: 1.0\r\n#End", "TBL110")]
    [InlineData("#Items\r\n$Name: \"a\"\r\n$Count:\r\n#End", "TBL111")]
    public void RuleFires(string text, string code)
    {
        var d = Lint(text);
        Assert.True(d.Any(x => x.Code == code), $"expected {code}, got: {string.Join(" | ", d)}");
        foreach (var x in d)
        {
            Assert.True(x.Span.End <= text.Length);
            Assert.False(string.IsNullOrWhiteSpace(x.Message));
            Assert.DoesNotContain("DESIGN", x.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AlpineOnlyFieldsAndTablesAreNotReported()
    {
        // Alpine Faction is the baseline: a field or table it added is not a problem; the version shows in hover and completion.
        string text = "#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$New: 1\r\n#End\r\n";
        var doc = TblDocument.Parse(text, TestSchema);
        Assert.Empty(TblLinter.Lint(doc, null, null));
        var hover = Cairn.Tbl.Assist.TblAssist.Hover(doc, text.IndexOf("$New", StringComparison.Ordinal) + 2)!;
        Assert.Contains(hover.Lines, l => l.Contains("Alpine Faction 1.1+", StringComparison.Ordinal));
        Assert.DoesNotContain(TblRules.All, r => r.Code is "TBL301" or "TBL302");

        var af = TblSchemaSet.Default.Find("af_ui.tbl")!;
        Assert.NotNull(af.AlpineSince);
        string afText = "#Start\r\n$Summoner Trailer Button Action: 2\r\n#End\r\n";
        var afDoc = TblDocument.Parse(afText, af);
        Assert.Empty(TblLinter.Lint(afDoc, null, TblLintContext.Default));
        var afHover = Cairn.Tbl.Assist.TblAssist.Hover(afDoc, afText.IndexOf("$Summoner", StringComparison.Ordinal) + 2)!;
        Assert.Contains(afHover.Lines, l => l.Contains("Alpine Faction 1.", StringComparison.Ordinal));
    }

    [Fact]
    public void QuickFixesRepair()
    {
        string text = "#Items\r\n$Name: \"a\"\r\n$Sped: 1.0\r\n";
        var d = Lint(text);
        string fixedText = text;
        foreach (string code in new[] { "TBL103", "TBL005", "TBL108" })
        {
            var diag = TblLinter.Lint(TblDocument.Parse(fixedText, TestSchema)).First(x => x.Code == code);
            fixedText = TextEdit.ApplyAll(fixedText, diag.QuickFixes[0].Edits);
        }
        Assert.Equal("#Items\r\n$Name: \"a\"\r\n$Count: 0\r\n$Speed: 1.0\r\n\r\n#End\r\n", fixedText);
        Assert.Empty(Lint(fixedText));
        Assert.Contains(d, x => x.Code == "TBL103" && x.Message.Contains("Did you mean $Speed:?", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceRulesUseTheContext()
    {
        var index = new TblIndex(TblSchemaSet.FromJson([("t", TestSchemaJson), ("ammo", """{ "file": "ammo.tbl", "sections": [ { "name": "#Ammo", "entry": "$Name:", "defines": "ammo", "fields": [ { "name": "$Name:", "type": "string" } ] } ] }""")]));
        index.Update(TblSource.ForPath("ammo.tbl"), "#Ammo\r\n$Name: \"shell\"\r\n#End\r\n");
        var context = new TblLintContext { Index = index, FileExists = n => n.Equals("ok.v3m", StringComparison.OrdinalIgnoreCase) };
        var d = Lint("#Items\r\n$Name: \"a\"\r\n$Count: 1\r\n$Mesh: \"ok.v3d\"\r\n$Ammo: \"shel\"\r\n$Name: \"b\"\r\n$Count: 1\r\n$Mesh: \"missing.v3d\"\r\n$Ammo: \"SHELL\"\r\n#End", context);
        Assert.Single(d, x => x.Code == "TBL201");
        var undefined = Assert.Single(d, x => x.Code == "TBL202");
        Assert.Contains("Did you mean \"shell\"?", undefined.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StockTablesLintClean()
    {
        var stock = TestData.Stock().Concat(TestData.Game().Where(g => g.IsStock)).ToList();
        if (stock.Count == 0) return;
        var report = new List<string>();
        int problems = 0;
        foreach (var s in stock)
        {
            var schema = TblSchemaSet.Default.Find(s.FileName);
            if (schema is null) continue;
            var doc = TblDocument.Parse(TblTextFiles.Decode(s.Bytes).Text, schema);
            var d = TblLinter.Lint(doc).Where(x => x.Severity != TblSeverity.Information).ToList();
            problems += d.Count;
            foreach (var g in d.GroupBy(x => x.Code))
                report.Add($"{s.Origin}: {g.Key} x{g.Count()} e.g. line {doc.Lines.LineOf(g.First().Span.Start)}: {g.First().Message}");
        }
        foreach (var line in report) output.WriteLine(line);
        Assert.True(problems == 0, $"{problems} errors/warnings in stock tables:\n" + string.Join("\n", report.Take(40)));
    }

    // With the game folder as the file and name context (as the editor lints), every stock table is clean: a
    // reference the stock game itself leaves unresolved is information, not a warning.
    [Fact]
    public void StockTablesWithGameDataHaveNoReferenceWarnings()
    {
        if (TestData.GameDirectory is not { } game) return;
        var resolver = new AssetResolver(new AssetResolverOptions { GameDirectory = game });
        var index = TblIndex.Build(resolver, null, CancellationToken.None);
        var stock = TestData.Stock().Concat(TestData.Game().Where(g => g.IsStock)).ToList();
        var report = new List<string>();
        int warnings = 0, tolerated = 0;
        var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stock)
        {
            var schema = TblSchemaSet.Default.Find(s.FileName);
            if (schema is null) continue;
            var doc = TblDocument.Parse(TblTextFiles.Decode(s.Bytes).Text, schema);
            var baseContext = TblLintContext.ForResolver(resolver, index);
            var unresolved = TblLinter.UnresolvedNames(doc, baseContext);
            foreach (string u in unresolved) all.Add($"{s.FileName}\t{u}");
            var context = baseContext with { StockMissing = unresolved };
            foreach (var d in TblLinter.Lint(doc, schema, context).Where(x => x.Code is "TBL201" or "TBL202"))
            {
                if (d.Severity == TblSeverity.Information) { tolerated++; output.WriteLine($"info {s.FileName}: {d.Message}"); continue; }
                warnings++;
                report.Add($"{s.Origin}: {d.Code} line {doc.Lines.LineOf(d.Span.Start)}: {d.Message}");
            }
        }
        output.WriteLine($"{warnings} reference warnings, {tolerated} tolerated (also missing in the stock game); {all.Count} distinct unresolved (table, reference)");
        if (Environment.GetEnvironmentVariable("CAIRN_TBL_REPORT") is { Length: > 0 } file) File.WriteAllLines(file, all);
        foreach (var line in report) output.WriteLine(line);
        Assert.True(warnings == 0, $"{warnings} reference warnings in stock tables:\n" + string.Join("\n", report.Take(60)));
        // What the game finds must resolve, not merely be tolerated: movies in data\movies, HUD bitmaps loaded as name_0.
        foreach (string found in new[] { "TBL201:intro.bik", "TBL201:reticle.tga", "TBL201:scope_ret.tga", "TBL201:bullet_icon.tga" })
            Assert.DoesNotContain(all, a => a.EndsWith("\t" + found, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StockMissingNamesStayWarningsInOtherTables()
    {
        const string text = "#Primary Weapons\r\n$Name: \"a\"\r\n$Display Name: \"a\"\r\n$V3D Filename: \"gone.tga\"\r\n"
            + "$Name: \"b\"\r\n$Display Name: \"b\"\r\n$V3D Filename: \"typo.tga\"\r\n#End\r\n";
        var schema = TblSchemaSet.Default.Find("weapons.tbl")!;
        var doc = TblDocument.Parse(text, schema);
        var baseContext = new TblLintContext { FileExists = _ => false };
        var stockOnly = TblDocument.Parse(text.Replace("typo.tga", "gone.tga"), schema);
        var context = new TblLintContext { FileExists = _ => false, StockMissing = TblLinter.UnresolvedNames(stockOnly, baseContext) };
        var refs = TblLinter.Lint(doc, schema, context).Where(d => d.Code == "TBL201").ToList();
        Assert.Contains(refs, d => d.Severity == TblSeverity.Information && d.Message.Contains("gone.tga") && d.Message.Contains("stock game"));
        Assert.Contains(refs, d => d.Severity == TblSeverity.Warning && d.Message.Contains("typo.tga"));
    }

    [Fact]
    public void ModdedTablesGivePlausibleDiagnostics()
    {
        var modded = TestData.Game().Where(g => !g.IsStock).ToList();
        if (modded.Count == 0) return;
        var counts = new Dictionary<string, int>();
        int tables = 0;
        var examples = new Dictionary<string, string>();
        // CAIRN_TBL_REPORT=<file> writes every diagnostic, one per line, for comparing runs.
        var all = new List<string>();
        foreach (var s in modded)
        {
            var schema = TblSchemaSet.Default.Find(s.FileName);
            if (schema is null) continue;
            tables++;
            var doc = TblDocument.Parse(TblTextFiles.Decode(s.Bytes).Text, schema);
            foreach (var d in TblLinter.Lint(doc))
            {
                counts[d.Code] = counts.GetValueOrDefault(d.Code) + 1;
                examples.TryAdd(d.Code, $"{s.Origin} line {doc.Lines.LineOf(d.Span.Start)}: {d.Message}");
                all.Add($"{d.Code}\t{s.Origin}\t{doc.Lines.LineOf(d.Span.Start)}\t{new string(d.Span.GetText(doc.Text).ReplaceLineEndings(" ").Take(60).ToArray())}\t{d.Message}");
            }
        }
        output.WriteLine($"{tables} modded tables with a schema");
        foreach (var (code, n) in counts.OrderBy(p => p.Key)) output.WriteLine($"{code} x{n}; e.g. {examples[code]}");
        if (Environment.GetEnvironmentVariable("CAIRN_TBL_REPORT") is { Length: > 0 } report)
            File.WriteAllLines(report, all.Order(StringComparer.Ordinal));
    }
}
