using System.Diagnostics;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;
using Xunit.Abstractions;

namespace Cairn.Tbl.Tests;

public sealed class ModelTests(ITestOutputHelper output)
{
    private const string Weapons = """
        // header comment
        #Primary Weapons

        $Name:                  "Remote Charge"
        $Display Name:			XSTR(296, "Remote Charge")
        $V3D Filename:          "rmt_explosive.v3d"
        $Flags:                 ("remote_charge" "alt_fire")
        +State:                 "idle"         "fp_rmt_chrg_idle.mvf"
        $Damage Radius:			5.0
        	+Crater Radius:      5.0
        $Scorch Size:				<10.0, 10.0, 10.0>

        $Name:                  "Detonator"
        $V3D Filename:          ""

        #End
        """;

    [Fact]
    public void ParsesSectionsEntriesFieldsAndValues()
    {
        var doc = TblDocument.Parse(Weapons);
        var section = Assert.Single(doc.Sections);
        Assert.Equal("Primary Weapons", section.Name);
        Assert.NotNull(section.EndSpan);
        Assert.Equal("$Name:", section.EntryMarker);
        Assert.Equal(["Remote Charge", "Detonator"], section.Entries.Select(e => e.Name));
        var first = section.Entries[0];
        Assert.Equal("Remote Charge", first.NameSpan.GetText(Weapons));
        var display = first.Fields[1];
        Assert.Equal(TblValueKind.Call, display.Values[0].Kind);
        Assert.Equal("XSTR", display.Values[0].Text);
        var flags = first.Fields.Single(f => f.Is("$Flags:"));
        Assert.Equal(["remote_charge", "alt_fire"], flags.Values[0].Items.Select(i => i.Text));
        // Without a schema "+" fields belong to the $ field before them.
        Assert.Equal("+State:", Assert.Single(flags.Children).Marker);
        var scorch = first.Fields.Single(f => f.Is("$Scorch Size:"));
        Assert.Equal(TblValueKind.Vector, scorch.Values[0].Kind);
        Assert.Equal([10.0, 10.0, 10.0], scorch.Values[0].Items.Select(i => i.Number!.Value));
        Assert.True(first.Span.End < section.Entries[1].Span.Start);
        Assert.Empty(doc.Issues);
        Assert.Same(scorch, doc.FieldAt(scorch.MarkerSpan.Start + 2));
        Assert.Same(first, doc.EntryAt(scorch.MarkerSpan.Start));
    }

    [Fact]
    public void ReportsStructuralIssues()
    {
        var doc = TblDocument.Parse("#Items\r\n$Name: \"a\r\n$Flags: (\"x\" \r\n$V: >\r\n#End\r\n#End\r\n#Other\r\n$Name: \"b\"");
        var kinds = doc.Issues.Select(i => i.Kind).ToList();
        Assert.Contains(TblSyntaxIssueKind.UnterminatedString, kinds);
        Assert.Contains(TblSyntaxIssueKind.UnclosedBracket, kinds);
        Assert.Contains(TblSyntaxIssueKind.StrayCloser, kinds);
        Assert.Contains(TblSyntaxIssueKind.StrayEnd, kinds);
        Assert.Contains(TblSyntaxIssueKind.MissingEnd, kinds);
    }

    [Fact]
    public void NumberedHeadersAndTextBlocksAreEntries()
    {
        var strings = TblDocument.Parse("#Strings\r\n#0\r\nEn: \"a\"\r\nGr: \"b\"\r\n#1\r\nEn: \"c\"\r\n#End\r\n");
        var s = Assert.Single(strings.Sections);
        Assert.Equal(["0", "1"], s.Entries.Select(e => e.Name));
        Assert.Equal(2, s.Entries[0].Fields.Length);
        Assert.Empty(strings.Issues);

        var endgame = TblDocument.Parse("$Name: \"g\"\r\nEn:\r\nYour failure (to protect\r\n#end\r\nGr:\r\ntext\r\n#end\r\n$Name: \"h\"\r\n");
        var root = Assert.Single(endgame.Sections);
        Assert.Equal(["g", "h"], root.Entries.Select(e => e.Name));
        Assert.NotNull(root.Entries[0].Fields[1].EndSpan);
        Assert.DoesNotContain(endgame.Issues, i => i.Kind == TblSyntaxIssueKind.StrayEnd);
        // The unbalanced bracket inside the text block is not reported by the linter.
        Assert.DoesNotContain(TblLinter.Lint(endgame), d => d.Severity != TblSeverity.Information);
    }

    [Fact]
    public void ParsesEveryTableQuickly()
    {
        var samples = TestData.Stock().Concat(TestData.Game()).ToList();
        if (samples.Count == 0) return;
        var worst = (Ms: 0.0, Name: "");
        long total = 0;
        foreach (var s in samples)
        {
            string text = TblTextFiles.Decode(s.Bytes).Text;
            var schema = TblSchemaSet.Default.Find(s.FileName);
            var sw = Stopwatch.StartNew();
            var doc = TblDocument.Parse(text, schema);
            _ = TblClassifier.Classify(doc);
            sw.Stop();
            total += sw.ElapsedTicks;
            if (sw.Elapsed.TotalMilliseconds > worst.Ms) worst = (sw.Elapsed.TotalMilliseconds, s.Origin + $" ({text.Length / 1024} KB)");
            Assert.True(doc.Sections.Length > 0 || text.Trim().Length == 0 || doc.Tokens.All(t => t.IsTrivia), s.Origin);
        }
        output.WriteLine($"{samples.Count} tables parsed in {total * 1000.0 / Stopwatch.Frequency:F0} ms; slowest {worst.Ms:F1} ms: {worst.Name}");

        // The biggest stock table (entity.tbl, about 370 KB), warmed up.
        var big = samples.OrderByDescending(s => s.Bytes.Length).First();
        string bigText = TblTextFiles.Decode(big.Bytes).Text;
        var bigSchema = TblSchemaSet.Default.Find(big.FileName);
        TblDocument.Parse(bigText, bigSchema);
        double ms = double.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var timer = Stopwatch.StartNew();
            TblDocument.Parse(bigText, bigSchema);
            ms = Math.Min(ms, timer.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"{big.Origin}: {bigText.Length / 1024} KB in {ms:F1} ms");
        Assert.True(ms < 100, $"{ms:F1} ms");
    }

    [Fact]
    public void MutationFuzzNeverThrowsOrHangs()
    {
        var seeds = TestData.Stock().Select(s => TblTextFiles.Decode(s.Bytes).Text).Where(t => t.Length < 60_000).ToList();
        if (seeds.Count == 0) seeds = [Weapons];
        var rng = new Random(1234);
        int scale = int.TryParse(Environment.GetEnvironmentVariable("CAIRN_FUZZ_SCALE"), out int f) && f > 0 ? f : 1;
        string[] snippets = ["\"", "(", ")", "<", ">", "{", "}", "#End", "#", "$", "+", ":", "/*", "*/", "//", "\r\n", "\n", "\r", "XSTR(", ",", "$Name:", "\0", "€", "0x", "-"];
        var sw = Stopwatch.StartNew();
        int iterations = 300 * scale;
        for (int i = 0; i < iterations; i++)
        {
            string text = seeds[rng.Next(seeds.Count)];
            int edits = rng.Next(1, 12);
            for (int e = 0; e < edits; e++)
            {
                int at = rng.Next(text.Length + 1);
                text = rng.Next(5) switch
                {
                    0 => text[..at],
                    1 => text.Remove(at, Math.Min(rng.Next(1, 200), text.Length - at)),
                    _ => text.Insert(at, snippets[rng.Next(snippets.Length)]),
                };
            }
            var one = Stopwatch.StartNew();
            var doc = TblDocument.Parse(text, TblSchemaSet.Default.Tables.Length > 0 ? TblSchemaSet.Default.Tables[rng.Next(TblSchemaSet.Default.Tables.Length)] : null);
            _ = TblClassifier.Classify(doc);
            _ = TblLinter.Lint(doc);
            int caret = rng.Next(text.Length + 1);
            _ = Assist.TblAssist.Complete(doc, caret);
            _ = Assist.TblAssist.Hover(doc, caret);
            _ = Compare.TblCompare.Compare(doc, TblDocument.Parse(seeds[0]));
            Assert.True(one.ElapsedMilliseconds < 5000, $"iteration {i} took {one.ElapsedMilliseconds} ms");
        }
        output.WriteLine($"{iterations} mutations in {sw.ElapsedMilliseconds} ms");
    }
}
